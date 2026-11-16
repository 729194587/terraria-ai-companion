"""LLM atomic operations. One Bridge session, game-thread actions and release receipts."""
import asyncio
import copy
import math
import re
from time import perf_counter
from control import BridgeError, NotAdmittedError


class PreDispatchRejected(BridgeError):
    """A local capability guard rejected the call before any game RPC.

    Keep the BridgeError contract for direct callers, while allowing the Lua
    host to distinguish this positive refusal from transport/receipt failures.
    Never use this type for errors inferred from a missing action or start task.
    """


def tool(name, description, properties=None, required=None):
    return {"type": "function", "function": {"name": name, "description": description,
        "parameters": {"type": "object", "properties": properties or {}, "required": required or [], "additionalProperties": False}}}


TILE = {"x": {"type": "integer", "minimum": 0, "maximum": 100000},
        "y": {"type": "integer", "minimum": 0, "maximum": 100000}}
MINE_TARGET = {"type": "object", "properties": {
    "kind": {"type": "string", "enum": ["tile"]}, **TILE,
    "typeId": {"type": "integer", "minimum": 0, "maximum": 65535},
    "worldSessionId": {"type": "string", "minLength": 1, "maxLength": 128},
    **{key: {"type": "integer", "minimum": 0, "maximum": 9007199254740991}
       for key in ('actorGeneration', 'observedGameUpdate', 'observedAtUnixMs')},
    "actorSlot": {"type": "integer", "minimum": 0, "maximum": 254},
    "targetToken": {"type": "string", "minLength": 64, "maxLength": 64, "pattern": "^[0-9a-f]{64}$"}},
    "required": ['kind','x','y','typeId','worldSessionId','actorGeneration','actorSlot','observedGameUpdate','observedAtUnixMs','targetToken'], "additionalProperties": False}
TYPE_IDS = {"type": "array", "items": {"type": "integer", "minimum": 0, "maximum": 65535},
            "minItems": 1, "maxItems": 32, "uniqueItems": True}
INVENTORY_SLOT = {"type": "integer", "minimum": 0, "maximum": 49}
EQUIPMENT_SLOT = {"type": "string", "minLength": 1, "maxLength": 256}
INVENTORY_TOKEN = {"type": "string", "minLength": 64, "maxLength": 64, "pattern": "^[0-9a-f]{64}$"}
CONTAINER_TRANSFER_ARGS = {
    "inventorySlot": INVENTORY_SLOT, "containerSlot": {"type": "integer", "minimum": 0, "maximum": 39},
    "count": {"type": "integer", "minimum": 1, "maximum": 9999},
    **dict.fromkeys(("inventoryToken", "containerToken", "slotToken"), INVENTORY_TOKEN),
}
INVENTORY_GUARDS = {"sourceSlot": INVENTORY_SLOT, "sourceToken": INVENTORY_TOKEN,
                    "destinationToken": INVENTORY_TOKEN}
SCENE_LAYERS = ["map", "geometry", "objects", "hazards", "landingRegions", "placementCandidates", "objectPlacementCandidates"]
SCENE_TEXT_CONTRACT = (
    "actor.bodyTileBounds为宿主按实际碰撞框计算的Tile范围，minX/minY/maxX/maxY均为包含端点；右/下边缘恰在Tile分界线时不包含边缘外的格。actor.mapTile是中心格，不是身体左上角；范围未裁剪至观察窗口，也不表示其中格为空或安全。"
    "observe_scene schemaVersion=2：固定图例/对象分组解释由本契约统一给出；三类候选的validation提升到集合级，matchedCount=0时省略validation。"
    "geometry图符：.=不阻挡（不等于可落脚），#=完整实心，h=半砖，1..4=实心斜坡，平平台为单字符=，a/b=斜平台，A=虚化Tile，?=未知碰撞/世界边界。"
    "map另有T=树干组件、O=矿脉、C=箱柜、D=门、W=工作台、t=火把光源物体、$=掉落、N=NPC、P=其他玩家、*=敌对弹幕、@=AI；~=水、L=岩浆、H=蜂蜜、S=微光、!=接触/NPC危险、B=巨石、M=运动模型排除。"
    "叠加优先级是未知>Tile危险>Actor/实体/对象>几何；图符不能恢复被覆盖的geometry，重叠危险须看结构化sources，边缘实体标记可能钳位；墙不阻挡。"
    "对象范围为原版树干、19种原版矿、箱柜/门/工作台、原版Torches各样式及相交活跃实体，未分类不等于不存在。"
    "light_source逐火把返回typeId/contentKey、observedBounds与details.anchorTile（真实Tile坐标），details保留frameX/frameY/actuated；分类不保证点亮或照明，不覆盖其他灯具/Mod光源。objects先返回非光源，再返回光源，组内按距离排序；objectCounts.observed/returned与集合截断共同说明省略，map标记不受上限影响。"
    "tree_component是相连观察树干，可能拆分一棵或合并相邻树，complete始终false，不是整树计数；ore_component按同类型四邻接，边界/未知使其不完整。"
    "箱柜/门/工作台按注册锚点分组，未验证完整家具占地或内容；动态实体按当前相交碰撞框观察，槽位/objects.id不是跨观察持久身份。"
    "所有候选仅为几何预筛选，不保证路径/可达或原生放置权限，行动后仍需真实回执和新鲜观察。"
)
MINE_TARGET_DESCRIPTION = "服务器复合挖掘（server_ai_tasks_v1）。传 observe_scene 矿物对象的 target，或传明确的绝对 Tile 坐标 x/y，两种形式互斥。观察目标须通过身份、30秒及600游戏更新时效和目标变化检查。与 mine_tile 共用主背包自动选工具、临时切换快捷栏及原生触及和工具判定。已在安全站位且够得着时直接挖掘，否则先尝试沿现有地形接近，抵达后重验目标和站位。最终核验指定格，可连带拆除该格所属家具。按 evidence.task.lastResult 和子动作释放证据核验，掉落入包需另行观察。unknown 停止。"
PLACE_OBJECT_DESCRIPTION = "服务器 AI 放置快捷栏选中的多格对象。x/y 是绝对 Tile 锚点，按选中物品的 createTile、placeStyle 和原生 TileObjectData 解析占地和 Anchor。由原生范围、碰撞和 Mod Hook 判定；依赖全局光标的特殊对象不支持。核验放置事件、整组帧、消耗和释放后才完成。取消可能已有副作用，unknown 停止。10秒上限。"

TOOLS = [tool("query_map", "查询AI独立探索记忆，绝非实时地形。Tile坐标；overview自动适配世界<=128x48，方格比例，为战略地图；region默认角色中心、120x68 Tile、cellSize=2，支持1/2/4。返回data.rows、originX/Y、cellSize、exploredRatio、legend；?未知，空格已知空地，#实心，=平台，:混合/部分已知，~水/L岩浆/H蜂蜜/S微光表示存在。混合格需cellSize=1放大，不能据此判通路。记忆可能过时；地下视野采用遮挡/近身/可见原版火把近似。",
    {"mode":{"type":"string","enum":["overview","region"]},"centerX":{"type":"integer","minimum":0,"maximum":100000},"centerY":{"type":"integer","minimum":0,"maximum":100000},"width":{"type":"integer","minimum":1,"maximum":128},"height":{"type":"integer","minimum":1,"maximum":96},"cellSize":{"type":"integer","enum":[1,2,4]}},["mode"]),tool("query_recipes", "查询当前加载原生配方及AI背包/附近制作站/已支持环境条件。默认只返回可制作配方；resultType来自resolve_content；availableOnly=false可查缺失/不支持原因。检查分页、supported及hasOutputSpace。",
    {"resultType":{"type":"integer","minimum":1},"availableOnly":{"type":"boolean"},"limit":{"type":"integer","minimum":1,"maximum":24},"offset":{"type":"integer","minimum":0,"maximum":100000}}),
    tool("craft_recipe", "按新鲜query_recipes的recipeId/recipeToken原生合成，crafts是配方执行次数1..20，不是产物件数；每次重查材料/制作站/空间。可能部分完成，不回滚或失败后自动重试。",
    {"recipeId":{"type":"integer","minimum":0,"maximum":100000},"recipeToken":INVENTORY_TOKEN,"crafts":{"type":"integer","minimum":1,"maximum":20}},["recipeId","recipeToken","crafts"]),tool("observe_scene", "服务器AI局部45×29二维侧视语义图：几何/对象/危险来源、可直接用于导航的landingRegions.items[].target和相互独立的placementCandidates（单格place_tile）/objectPlacementCandidates（工作台place_object，含完整footprint/support）候选。geometry/map共用originX/originY，Lua用string.sub解析行。单次游戏线程采样；候选不保证可达/放置成功，树组件不是整树计数；检查coverage与各集合truncated。可选layers为非空、无重复的白名单数组，未传返回全部七层；data.includedLayers列出实际层，未请求字段省略。objects附带objectCounts，放置候选附带对应Material；身份、范围、角色、coverage和sample始终保留。objectPlacementCandidates.diagnostics区分已检查排除、unknownCount及notCheckedCount；matchedCount仍为通过筛选且截断前的数目。" + SCENE_TEXT_CONTRACT,
    {**{key:{"type":"integer","minimum":1,"maximum":64 if key=="maxHazards" else 24 if key=="maxRegions" else 32}
     for key in ("maxObjects","maxRegions","maxPlacements","maxHazards")},
     "layers":{"type":"array","items":{"type":"string","enum":SCENE_LAYERS},"minItems":1,"maxItems":7,"uniqueItems":True},
     "scale":{"type":"string","enum":["local","region"]},
     "width":{"type":"integer","minimum":45,"maximum":128},
     "height":{"type":"integer","minimum":29,"maximum":96}}),tool("get_player_state", "读取真实玩家位置/速度/生命和地面支撑及运动模型支持状态。"),
    tool("get_player_status", "紧凑玩家状态：生命/魔力、运动和液体、所选完整物品、非空装备及活跃Buff；背包仅槽位摘要。不是完整背包，Mod自定义状态未知。"),
    tool("resolve_content", "按内容键或名称解析当前已加载的item/tile/wall ID。item支持本地化名称；tile/wall名称仅内容符号（如Terraria/Dirt），不是本地化地图名称。默认精确匹配，search为包含搜索；歧义必须选择候选，不猜ID。",
         {"kind": {"type": "string", "enum": ["item", "tile", "wall"]},
          "query": {"type": "string", "minLength": 1, "maxLength": 150},
          "mode": {"type": "string", "enum": ["exact", "search"]},
          "maxResults": {"type": "integer", "minimum": 1, "maximum": 32}}, ["kind", "query"]),
    tool("observe_exploration", "Lua可调用的有界视口探索观察：以玩家为中心裁剪Tile半径（默认8，最大8），最多16实体及32调查候选，保留未知/时效/覆盖说明。不是寻路或全世界；没有返回的区域/实体不能视为空。",
         {"radiusTiles": {"type": "integer", "minimum": 1, "maximum": 8},
          "maxEntities": {"type": "integer", "minimum": 1, "maximum": 16}}),
    tool("wait", "可取消的宿主真实时间等待，milliseconds为1..10000。不会推进游戏时间、持有按键或确认游戏效果；受Lua总时限和工具次数预算约束。",
         {"milliseconds": {"type": "integer", "minimum": 1, "maximum": 10000}}, ["milliseconds"]),
    tool("observe_terrain", "复用get_terrain读取45×29局部碰撞及同次玩家状态；Tile为16像素。"),
    tool("inspect_tile", "读取指定Tile类型、Frame、形状、液体、墙/线/颜色，附内容键及距离/工具等preflight局部前提（不保证可达或可执行）；服务器可附data.miningSupportImpact：只读比较当前姿态移除单格前后的支撑，检查available/reasons/current/afterRemoval/supportDeltaPixels；不预测下落落点、不保证返程或挖掘许可，缺字段表示未提供；越界或未加载显式未知。", TILE, ["x", "y"]),
    tool("scan_entities", "按世界像素半径扫描客户端已知NPC、掉落物、其他玩家；先按typeIds/npcFilter筛选，再按玩家中心距离排序截断；npcFilter须category=npc，typeIds须npc/item。NPC entityRef仅当前世界实例有效；截断不等于没有。",
         {"category": {"type": "string", "enum": ["all", "npc", "item", "player"]}, "radius": {"type": "number", "minimum": 1, "maximum": 1600},
          "typeIds": TYPE_IDS, "maxResults": {"type": "integer", "minimum": 1, "maximum": 128},
          "npcFilter": {"type": "string", "enum": ["all", "hostile_creature", "friendly_npc", "critter", "unknown"]}}, ["category", "radius"]),
    tool("scan_tiles", "按Tile格坐标闭区间[minX,maxX]×[minY,maxY]扫描指定tileTypes；1格16像素，Y向下。面积<=4096，先按玩家中心距离排序再截断，最多返回256；未加载不等于空。类型ID来自inspect_tile/观察。",
         {**{k: {"type": "integer", "minimum": 0, "maximum": 100000} for k in ('minX', 'minY', 'maxX', 'maxY')},
          "tileTypes": {"type": "array", "items": {"type": "integer", "minimum": 0, "maximum": 65535}, "minItems": 1, "maxItems": 32, "uniqueItems": True},
          "maxResults": {"type": "integer", "minimum": 1, "maximum": 256}}, ['minX', 'minY', 'maxX', 'maxY', 'tileTypes']),
    tool("open_container", "仅服务器AI。指定原版箱子任一格的Tile坐标。需在原生范围内且箱子未锁、无人占用；已有打开的箱子时先关闭。带机关的原版箱子可能触发机关。", TILE, ["x", "y"]),
    tool("inspect_container", "仅观察AI当前打开的原版箱子。返回锚点、类型、Chest索引及40个槽位，签发新的容器和槽位Token。未打开或上下文失效时返回不可用。"),
    tool("deposit_container_item", "仅服务器AI。从指定普通背包槽存入当前箱子的指定槽。传最新 inspect_inventory(mode=slots) 和 inspect_container 的三种Token。目标容量不足时，只搬入可容纳数量，回执failed且effectStatus=partial。", CONTAINER_TRANSFER_ARGS, list(CONTAINER_TRANSFER_ARGS)),
    tool("withdraw_container_item", "仅服务器AI。从当前箱子的指定槽取到指定普通背包槽。传最新 inspect_inventory(mode=slots) 和 inspect_container 的三种Token。目标容量不足时，只取出可容纳数量，回执failed且effectStatus=partial。", CONTAINER_TRANSFER_ARGS, list(CONTAINER_TRANSFER_ARGS)),
    tool("quick_stack_nearby", "服务器AI。将普通背包10..49中未收藏的非钱币快速堆叠到600像素内已有同类物品且无人占用的原版箱子。"),
    tool("quick_stack_container", "服务器AI。将普通背包10..49中未收藏的非钱币快速堆叠到当前箱子的已有同类物品。需要新鲜容器Token。", {"containerToken": INVENTORY_TOKEN}, ["containerToken"]),
    tool("loot_all", "服务器AI。按拾取规则从当前箱子批量取出，需要新鲜容器Token。", {"containerToken": INVENTORY_TOKEN}, ["containerToken"]),
    tool("deposit_all", "服务器AI。批量存入普通背包10..49中未收藏物品，需要新鲜容器Token。", {"containerToken": INVENTORY_TOKEN}, ["containerToken"]),
    tool("collect_drops", "通过 submit_task 收集本轮观察到的附近掉落物。默认320像素，最多16个。可用 typeIds 筛选，或用 scan_entities 返回的掉落物 entityRef 指定目标。", {
        "radiusPixels": {"type": "integer", "minimum": 16, "maximum": 960, "default": 320},
        "typeIds": {"type": "array", "minItems": 1, "maxItems": 16, "uniqueItems": True,
                    "items": {"type": "integer", "minimum": 1, "maximum": 65535}},
        "entityRef": {"type": "string", "minLength": 1, "maxLength": 128},
    }),
    tool("sort_inventory", "服务器AI。使用原生背包排序，包括钱币和弹药整理。排序后重新读取背包槽位。"),
    tool("sort_container", "服务器AI。使用原生算法整理当前箱子，需新鲜容器Token。排序后重新读取箱子槽位。", {"containerToken": INVENTORY_TOKEN}, ["containerToken"]),
    tool("restock", "服务器AI。从当前箱子补充背包已有的可堆叠非钱币物品，需要新鲜容器Token。", {"containerToken": INVENTORY_TOKEN}, ["containerToken"]),
    tool("close_container", "仅关闭AI当前打开的原版箱子。传最近一次inspect_container返回的containerToken，有效期30秒。", {"containerToken": INVENTORY_TOKEN}, ["containerToken"]),
    tool("inspect_inventory", "mode必填：selected仅当前选中槽；hotbar检查0..9；slots配slots检查指定内部索引（0..58，最多16项）；item按精确contentKey或本地名称搜索全部player.inventory槽（含快捷栏/钱币/弹药/58号槽），slots结果仅为匹配位置。full检查全部player.inventory、原版装备/社交栏、染料、misc装备/染料和全部Buff槽（仅返回活跃Buff）。均返回inventoryScope与inspectedSlots（实际检查的player.inventory内部索引，非匹配位置）；inventoryScope.arrays列出实际检查的各数组槽。full不独立读取银行/仓库、箱子、Main.mouseItem或Mod自定义槽位/状态；58号槽仅按player.inventory实际内容返回。局部模式不代表完整背包；hotbarKey为游戏按键。客户端inventoryToken仅供既有背包动作防旧快照校验。",
         {"mode": {"type": "string", "enum": ['selected', 'hotbar', 'item', 'full', 'slots']},
          "item": {"type": "string", "minLength": 1, "maxLength": 150},
          "slots": {"type": "array", "items": {"type": "integer", "minimum": 0, "maximum": 58},
                    "minItems": 1, "maxItems": 16, "uniqueItems": True}}, ["mode"]),
    tool("transfer_inventory_item", "单人：在0..49普通背包槽搬运指定count；空目标可拆分，兼容同类目标可堆叠，容量不足则整个请求不执行。sourceToken/destinationToken须取本轮inspect_inventory对应槽。经原生拆分/堆叠钩子，核对数量与下一更新状态；不碰鼠标、箱子、装备、钱币或弹药槽。失败/未知后停止。",
         {**INVENTORY_GUARDS, "destinationSlot": INVENTORY_SLOT,
          "count": {"type": "integer", "minimum": 1, "maximum": 9999}},
         ['sourceSlot', 'destinationSlot', 'sourceToken', 'destinationToken', 'count']),
    tool("swap_inventory_slots", "单人：交换两个0..49普通背包槽的完整物品，不合并、不丢弃；保留物品实例。两个token须来自当前槽位观察；下一游戏更新核验。鼠标持物、使用中、旧快照或多人会拒绝。",
         {**INVENTORY_GUARDS, "destinationSlot": INVENTORY_SLOT},
         ['sourceSlot', 'destinationSlot', 'sourceToken', 'destinationToken']),
    tool("hold_inventory_item", "单人：将sourceSlot完整物品与指定hotbarKey对应槽交换后选择该快捷键；目标原物品回源槽。即使同类也不合并，source已在目标槽则只选择。sourceToken/destinationToken来自两槽当前观察；核验槽位与下一更新持用状态。不自动使用物品。",
         {**INVENTORY_GUARDS, "hotbarKey": {"type": "integer", "minimum": 0, "maximum": 9}},
         ['sourceSlot', 'hotbarKey', 'sourceToken', 'destinationToken']),
    tool("equip_item", "仅服务器AI：从普通背包穿戴一件装备。用inspect_inventory(mode='slots')取得sourceToken，用full的functionalEquipmentSlots取得targetSlotId和targetToken（空槽也要）。替换时原装备回来源槽，后续游戏更新确认。unknown后先观察。",
         {"sourceSlot": INVENTORY_SLOT, "sourceToken": INVENTORY_TOKEN, "targetSlotId": EQUIPMENT_SLOT,
          "targetToken": INVENTORY_TOKEN}, ["sourceSlot", "sourceToken", "targetSlotId", "targetToken"]),
    tool("unequip_item", "仅服务器AI：从功能装备槽卸下一件装备。用inspect_inventory(mode='full')取得targetSlotId和targetToken。可指定0..49的空背包槽，省略时使用首个空槽。后续游戏更新确认，unknown后先观察。",
         {"targetSlotId": EQUIPMENT_SLOT, "targetToken": INVENTORY_TOKEN, "destinationSlot": INVENTORY_SLOT},
         ["targetSlotId", "targetToken"]),
    tool("get_world_state", "读取客户端昼夜、天气、事件、生物群系和深度。"),
    tool("move_input", "仅服务器AI：持续注入原生A/D、W/S及Space；horizontal/vertical各自互斥，none为松开。maxUpdates是1..600次游戏更新，不是距离；jumpHoldUpdates为最初0..maxUpdates次按住跳跃。无需落地、支撑或路径预测，可空中转向/走下边缘；Down按原生平台下穿语义，Down+Jump可能先跳起再在下降时下穿。完成只表示输入时长结束，撞墙/下落/受伤是正常结果。取消只释放输入，保留惯性；不自动装备/飞行/恢复。",
         {"horizontal":{"type":"string","enum":["none","left","right"]},
          "vertical":{"type":"string","enum":["none","up","down"]},
          "jumpHoldUpdates":{"type":"integer","minimum":0,"maximum":600},
          "maxUpdates":{"type":"integer","minimum":1,"maximum":600}},
         ["horizontal","vertical","jumpHoldUpdates","maxUpdates"]),
    tool("navigate_to_x", "复用连续导航至玩家左上角水平世界像素targetX；30秒上限，普通角色运动限制仍适用；arrived由游戏物理状态校验。失败时evidence.navigation.blockingTiles若存在，提供x/y/contentKey及basis，表示被拒绝的根节点前向步行试算中原生裁剪确认的完整实心接触，不证明全部路径被封死；缺失表示未可靠定位。",
         {"targetX": {"type": "number", "minimum": 0, "maximum": 1000000}}, ["targetX"]),
    tool("navigate_to_landing_region", "二维落脚导航：全部为世界像素，Y向下；minPlayerLeftX/maxPlayerLeftX是玩家碰撞框左边缘的闭区间，surfaceY是目标脚底高度（世界像素）。按原生碰撞确认方块、平台、半砖或斜坡支撑，适用普通角色运动；30秒上限。仅实际进入X区间、脚底高度误差<=0.5px、安全着地且停稳后完成，不能只经过目标X。失败时evidence.navigation.blockingTiles若存在，提供x/y/contentKey及basis，仅表示被拒绝的根节点前向步行试算的已确认完整实心接触；缺失表示未可靠定位。",
         {key: {"type": "number", "minimum": 0, "maximum": 1000000}
          for key in ("minPlayerLeftX", "maxPlayerLeftX", "surfaceY")},
         ["minPlayerLeftX", "maxPlayerLeftX", "surfaceY"]),
    tool("drop_through_platform", "按原生Down直接穿过当前水平单向平台，不绕到边缘。须已停稳，自动检查同X下方64像素内的第一个安全落点；穿过当前层即松Down，实际落在下一层并停稳才完成，3秒上限。"),
    tool("select_hotbar_slot", "按游戏显示的快捷键选择：1..9为前九格，0为最右第十格。slot不是背包数组索引。核对实际选中按键。", {"slot": {"type": "integer", "minimum": 0, "maximum": 9}}, ["slot"]),
    tool("use_item", "用当前物品完成一次原生使用周期；target可选世界像素{x,y}。completed只证明使用周期，具体效果需感知核验。无target保留原生鼠标，不限制画面缩放；有target支持当前缩放，仍限正常重力及80像素。",
         {"target": {"type": "object", "properties": {"x": {"type": "number", "minimum": 0, "maximum": 1000000}, "y": {"type": "number", "minimum": 0, "maximum": 1000000}}, "required": ["x", "y"], "additionalProperties": False}}),
    tool("navigate_to", "服务器自主导航。只传预期落脚面的世界像素 x、y（Y 向下）。游戏侧自行选择走、跳、安全下落、挖掘和搭路。安全着地停稳且脚底覆盖目的地才完成。unknown 后停止。",
         {key: {"type": "number", "minimum": 0, "maximum": 1000000} for key in ("x", "y")}, ["x", "y"]),
    tool("mine_target", MINE_TARGET_DESCRIPTION, {"target": MINE_TARGET, **TILE, "cluster": {"type": "boolean"}}),
    tool("combat_attack", "通过 submit_task 提交单目标战斗。使用 scan_entities 返回的 entityRef 和当前选中的受支持武器。", {"entityRef": {"type": "string", "minLength": 1, "maxLength": 150}}, ["entityRef"]),
    tool("query_combat", "读取绑定角色当前或最近一次战斗。返回 action（含可查询或取消的 actionId），空闲且无战斗记录时为 null。", {}),
    tool("mine_tile", "原生挖掘指定 Tile。服务器 AI 从整个主背包自动选择合适工具，必要时临时切换快捷栏，按 Terraria 原生范围和工具规则判定。客户端控制使用当前手持镐。动作在当前位置执行，以实际移除为准，10秒上限。",
         {"x": {**TILE["x"], "description": "目标格的绝对 Tile X 坐标"},
          "y": {**TILE["y"], "description": "目标格的绝对 Tile Y 坐标"}}, ["x", "y"]),
    tool("attack_entity", "以当前物品攻击scan_entities给出的NPC entityRef；80像素内，生命下降才成功，失活但无伤害证据失败，其他伤害来源可能存在。10秒上限。",
         {"entityRef": {"type": "string", "minLength": 1, "maxLength": 150}}, ["entityRef"]),
    tool("mine_wall", "选中纯锤原生挖背景墙；80像素内，前景必须为空；实际墙消失才成功。多人需同版本服务器确认；10秒上限，可取消。", TILE, ['x', 'y']),
    tool("place_wall", "选中原版消耗型墙物品，在80像素内无墙格原生放置；正确墙类型和消耗一件共同核验，多人需服务器确认；自动补邻墙或自动涂漆不支持，10秒上限。", TILE, ['x', 'y']),
    tool("hammer_tile", "选中纯锤对80像素内原版普通方块/平台原生锤击；目标无背景墙，实际形状变化才成功，多人需服务器确认。不支持家具/破坏型锤目标。10秒上限。", TILE, ['x', 'y']),
    tool("interact_tile", "80像素内原版门、未锁宝箱、开关、拉杆基础交互；多格任一占用格可指定；宝箱核验打开/关闭，开关及拉杆核验Frame切换，不证明下游电路效果。多人需同版本服务器确认。", TILE, ["x", "y"]),
    tool("place_item", "通过 submit_task 使用当前选中的快捷栏物品在 Tile 坐标 x/y 执行原生放置。", TILE, ["x", "y"]),
    tool("place_object", PLACE_OBJECT_DESCRIPTION, TILE, ["x", "y"]),
    tool("place_rope", "服务器 AI 放置一节原版绳索。x/y 为绝对 Tile 坐标，空格直接放置，已有原版绳索则向下延伸一节。自动使用自身快捷栏绳索，点击格与实际落点均须在范围内。回执核验实际落点、绳索类型和一件库存消耗。", TILE, ["x", "y"]),
    tool("move_on_rope", "服务器 AI 沿对齐的干燥原版连续绳柱原生上下移动。ropeX 为 Tile 列，targetFeetY 为脚底世界像素 Y，向下增大。起点须静止，绳索位于头顶探测格。hold 确认在绳上停稳，land 沿下端脱离并确认正下方实体地面落脚。单次最多384像素及maxUpdates帧。",
         {"ropeX":{"type":"integer","minimum":0,"maximum":100000},
          "targetFeetY":{"type":"number","minimum":0,"maximum":1000000},
          "finish":{"type":"string","enum":["hold","land"]},
          "maxUpdates":{"type":"integer","minimum":1,"maximum":360}},
         ["ropeX","targetFeetY","finish","maxUpdates"]),
    tool("place_tile", "用选中消耗品在80像素内空格放单格方块，支持当前画面缩放，仍限正常重力；Tile类型与物品消耗共同核验；不支持多格家具或ModItem。10秒上限。", TILE, ["x", "y"]),
    tool("stop_action", "取消当前Agent动作并等待游戏线程输入释放证据，不抢占手动控制。")]
SCHEMAS = {t['function']['name']: t['function']['parameters'] for t in TOOLS}

# Model-only server projection. Execution validation and the shared TOOLS stay unchanged.
SERVER_DESCRIPTIONS = {
    'mine_target': MINE_TARGET_DESCRIPTION,
    'navigate_to': '到达目的地用 navigate_to，只传落脚面世界像素 x/y，Y 向下。游戏侧自主选择走、跳、安全下落、挖掘或搭路。受普通角色运动、局部安全和任务预算限制。unknown 后停止。',
    'place_object': PLACE_OBJECT_DESCRIPTION,
    'transfer_inventory_item': '服务器AI：仅0..49普通库存槽，新鲜两槽token；原生拆分/堆叠，CanStack检查Mod兼容性，容量不足不执行；核验数量及后续游戏更新。unknown可能已变更，不回滚、不自动重试。',
    'swap_inventory_slots': '服务器AI：新鲜两槽token，交换0..49完整物品实例；不合并，后续游戏更新核验；unknown不回滚、不自动重试。',
    'hold_inventory_item': '服务器AI：完整交换sourceSlot与hotbarKey(游戏键1..9,0)，目标物品回源槽，再选中；同槽只选择，不自动使用；核验两槽与后续更新。unknown不回滚、不自动重试。',
    'get_player_state': '读取绑定服务器AI的实际位置/速度/生命及motion；无参数。返回{valid,player,motion,binding,...}，player.position是碰撞框左上角世界像素，velocity为像素/游戏更新，Y向下；motion.grounded与supported独立，supported只是导航profile适用性，不保证支撑/路径安全。',
    'open_container': '检查completed及endEvidence，再用inspect_container读取箱子。不会打开其他玩家的箱子界面。',
    'inspect_container': 'data.status为open时，data.container含箱子身份和40个槽位。只保留最新容器Token，有效期30秒。槽位Token覆盖实例及基础字段，自定义物品状态未知。data.status为unavailable时检查reason。',
    'close_container': '使用最新观察的data.container.containerToken。关闭或换箱后旧Token失效。检查completed及endEvidence。',
    'inspect_inventory': '只读AI真实背包，必须显式mode；局部查询不代表完整背包。返回{valid,available,data,...}，data.inventoryScope/inspectedSlots标明实际检查范围，item匹配slots不等于搜索范围。hotbarKey是游戏键1..9,0；新版本server_ai_inventory_v1提供绑定World/Actor/Generation/槽位、物品实例与基础字段的inventoryToken；操作前用slots/selected/hotbar读取两槽新鲜token。full附functionalEquipmentSlots及装备targetToken；full/item不提供普通背包槽token，全部Mod自定义状态未知；token不调用保存钩子，不能识别仅自定义状态变化。',
    'observe_terrain': '读取AI周围45×29碰撞格及同次状态；无参数。data.terrain含originX/originY/rows和独立液体层liquids。液体项含绝对Tile坐标x/y、type、原生数量amount（1–255），未知格的缺项不代表干燥。data.state含centerWorldPixels；data还含bounds、legend、scannedCount、unknownCount和complete。格为Tile（16像素）。supported只是profile适用性。',
    'use_item': '当前快捷栏物品执行一次原生使用周期，参数仅{}。支持可无坐标单次使用的原版物品及 ModItem，使用条件由 ItemCheck 和 Mod Hook 判定。需要瞄准、弹药、持续按住或放置坐标的物品需使用相应入口。3秒上限。cycleVerified 证明使用周期，consumptionObserved 表示持用物品堆叠减少；具体治疗、Buff、命中等效果需另行观察。',
    'mine_tile': '原生挖掘指定 Tile。自动在整个主背包（含快捷栏）选择合适工具，必要时临时切换快捷栏，无需预选镐。由 Terraria 原生挖掘范围、工具和破坏许可判定。在当前位置执行，需要接近时用 mine_target。可指定家具有效组成格，已识别的多格家具核验整组移除。10秒上限，destroyedByActor、targetAbsent 和绑定输入释放证据齐备才完成。掉落入包需另行观察，unknown 停止。',
    'place_tile': '在空的绝对 Tile 格 x/y 用选中的快捷栏材料放置普通单格 Tile 或原版绳索。按 createTile、placeStyle 和原生放置范围、邻接及 Mod Hook 判定。点击已有绳段向下延伸用 place_rope；多格对象用 place_object。核验本 AI 的放置事件、目标格、consumedCount=1 和输入释放。取消可能已有副作用，unknown 停止。10秒上限。',
    'scan_entities': '以AI中心扫描世界像素半径radius（1..1600）内真实活跃NPC/掉落/其他玩家，排除AI自身；category与可选typeIds/npcFilter按Schema，先筛选再按距离排序截断。返回data.items及范围/计数/截断；entityRef绑定世界/AI代次/NPC实例，仅是观察标识，不保证视线、可达或可攻击。',
}
LEGACY_SERVER_OPERATION_DESCRIPTIONS = {
    'use_item': '旧版服务器的物品使用范围受限，参数仅{}。completed 仅核验原生使用周期。',
    'place_tile': '旧版服务器仅支持有限的原版单格材料。按服务器预检和原生规则判定，核验放置和 consumedCount=1。',
    'place_object': '旧版服务器放置默认样式的原版工作台。使用已观察的工作台候选锚点，核验占地、消耗和释放。',
}
SERVER_DESCRIPTION_SUFFIXES = {
    'query_recipes': '返回data.items中的output.count、supported/available、材料/站点/条件、hasOutputSpace和分页。仅普通原版配方/配方组、AI背包0..57、原生邻接站点、水/熔岩/蜂蜜/微光/昼夜六种条件；特殊回调/其他条件/Mod内容/箱子或虚空仓库不支持。resultType需从当前已知注册表取得，空页不证明全局无配方。',
    'craft_recipe': '仅普通原版配方/配方组、AI背包0..57与已支持制作站/六类环境条件；特殊回调、其他条件、Mod内容、箱子/虚空仓库不支持。前置：新鲜配方token及supported/available、材料/站点/条件/保守空间检查；单次产量见output.count，每批重验。按completedCrafts/producedCount、batches、inventoryVerified核验；可能部分完成，不回滚。制作站是否邻接要重新查询，不能由放置动画推定。',
    'move_input': '前置为绑定身份/控制权、无互斥动作、正常重力；坐骑/失能状态拒绝，空中/非零起速允许。30秒墙钟看门，不要求导航profile/安全支撑。evidence包含updatesExecuted、startPosition/endPosition/endVelocity（向量X/Y）、healthDelta、grounded/airborne、inputReleased、settled、noProgress；completed只证明输入时长结束，inputReleased不证明停稳或效果确定，观察/同步不确定时仍返回unknown。',
    'navigate_to_x': '先读取当前位置再计算相对targetX；起步/路径仍需普通导航profile，飞行/坐骑/反重力等不支持。结束evidence含x/y/vx/vy、arrived/grounded/safeSupport/settled；不能把经过X、零预测误差或局部搜索失败当成已落地/全局不可达。',
    'navigate_to_landing_region': '目标须来自新鲜观察，可将已观察候选target直接传入；不同高度仍受原局部预算、普通profile及64px下降限制。支持部分脚底悬空的落点，须由原生碰撞和停稳状态核验。导航不执行Down平台下穿。成功须targetRegion一致、arrived/grounded/safeSupport/settled=true及known释放；未确认落地保持unknown。',
    'scan_tiles': '返回data.items及matchedCount/returnedCount/complete/truncated；按Tile格计数，一棵树有多格，不能当树的棵数。附近查询先取得观察bounds；世界像素转Tile用math.floor(px/16)，不猜内容ID。',
    'inspect_tile': '返回data内的实际格和preflight；x/y为Tile坐标。只读前提不是原生执行许可；移除支撑比较不预测实际落点。',
    'resolve_content': '返回data中的匹配候选/ID及歧义；内容注册表中存在不等于当前观察范围或世界内存在。',
    'observe_scene': 'data.map/geometry为45×29行字符串，共用Tile原点originX/Y；tileX=originX+列索引-1，tileY=originY+行索引-1。按任务选layers，未请求字段直接省略，不表示空；旧响应缺includedLayers不证明支持筛选。对应placementMaterial/objectPlacementMaterial需核对真实材料。导航候选candidate.target、单格和工作台候选用途独立；单格placementCandidates不能传给place_object，工作台用objectPlacementCandidates.items。工作台diagnostics中rejectedCount仅计已知占地/支撑的首个互斥拒绝，unknownCount/notCheckedCount不算明确排除；coveredScopeComplete仅指完整占地/支撑在观察范围内的锚点全部已知，缺diagnostics不表示计数为0。集合各自检查coverage/complete/truncated；移动后重观察。',
    'select_hotbar_slot': '只改变AI当前快捷栏选择；输入slot是游戏显示键，不是inventory.slot内部索引。completed需下一游戏线程状态及绑定选择结束证据，不等于使用物品。',
    'query_map': '返回data.rows/originX/Y/cellSize/exploredRatio等；持久地图是AI探索记忆，不是脚本变量存储，不替代当前观察或保证可达。',
    'wait': '返回{valid,status,elapsedMilliseconds,clock}；占API次数但不占变更动作次数。',
    'stop_action': '只取消当前拥有的AI动作；已释放输入仍可status=unknown，读取status与结束证据分别判断，不把释放当成功。',
}


SCENE_REGION_CONTRACT = ('支持server_ai_scene_region_v1时可用observe_scene({scale="region",width=120,height=68})获取L1；'
    'width=45..128、height=29..96，面积<=8192 Tile；以AI中心为中心，非摄像机/视觉可见性。'
    'schemaVersion=3，默认geometry/objects/hazards；仅可选这三层及map，无导航/放置target。'
    'geometry.grid及map.terrain为encodingVersion=2无损逐Tile字典行编码：palette下标从0开始；rows[].encoding为rle或hex_indices。'
    'rle的data交替[count,paletteIndex]；hex_indices的data是ASCII字符串，每格固定indexDigits个十六进制字符；'
    'Lua第x格（从1开始）取palette[tonumber(string.sub(row.data,(x-1)*row.indexDigits+1,x*row.indexDigits),16)+1]。'
    'coverage.knowledge用相同编码区分原始未知/Mod碰撞/未采样；'
    'hazards.tileMask同编码，按bitSources的0起始位保留每格全部已观察危险，即使tiles详情truncated也不抹掉危险；0不是未知格安全的证据。'
    'map的actor/markers独立，不覆盖terrain；collection.truncated与coverage.samplingTruncated独立。'
    'L1对象聚类最多256组；objects.groupingTruncated时matchedCount/omittedCount为null，matchedCountLowerBound仅下界，'
    'objectCounts.observedCountsComplete=false；objects.kindGrid同编码保留每格kind/contentKey（空kind仍须结合knowledge判断）。'
    '采样有10ms软预算，剩余格为not_sampled；检查sample耗时/分配/字节。'
    'L1用于区域知识，当前动作仍需L0/原生碰撞；query_map复用既有历史地图，查询时间不是逐格新鲜度，不证明当前可达。')


def server_tools(supported, *, scene_region=False, general_operations=True, mining_opening=False, excavation=False):
    """Reuse shared schemas; narrow server use_item and attach backend-specific semantics."""
    definitions = [copy.deepcopy(t) for t in TOOLS if t['function']['name'] in supported and t['function']['name'] not in ('combat_attack', 'collect_drops', 'place_item')]
    for definition in definitions:
        f = definition['function']
        name = f['name']
        if name == 'use_item':
            f['parameters'] = {'type': 'object', 'properties': {}, 'required': [], 'additionalProperties': False}
        f['description'] = SERVER_DESCRIPTIONS.get(name, f['description'])
        if not general_operations and name in LEGACY_SERVER_OPERATION_DESCRIPTIONS:
            f['description'] = LEGACY_SERVER_OPERATION_DESCRIPTIONS[name]
        if name == 'mine_target' and mining_opening:
            f['description'] += '服务器可在局部范围内串行挖开阻挡格，等待原生落脚并重新规划；开路有明确预算，可能已移除部分地形。查看 task.progress 的 clearedTiles、opening 和 search。'
        if name == 'mine_target':
            if excavation:
                f['description'] += '设置 cluster=true 采集种子所在的同种四连通矿簇，上限18格。逐格结果见 task.result.evidence.targets。'
            else:
                f['parameters']['properties'].pop('cluster', None)
        if name == 'observe_scene':
            f['description'] += ('data.lighting 在 available=true 时提供角色中心的原生 Tile 光照估计。'
                'hazards.entities.items可带observedGameUpdate、observationRef、velocity和threat。'
                'observationRef仅本次场景有效。threat是在12游戏更新内按恒速和弹幕extraUpdates推算的包围盒接触，'
                '未评估地形、AI、输入和伤害规则。先按推断接触时间排序，再按距离。'
                '无推断接触、字段缺失或列表截断都不能证明安全。')
            if scene_region:
                f['description'] += SCENE_REGION_CONTRACT
            else:
                for key in ('scale','width','height'):
                    f['parameters']['properties'].pop(key,None)
        if name in SERVER_DESCRIPTION_SUFFIXES:
            f['description'] += SERVER_DESCRIPTION_SUFFIXES[name]
        if name in ('navigate_to_x', 'navigate_to_landing_region'):
            f['description'] = (f['description'].replace('evidence.navigation.blockingTiles', 'evidence.navigation.BlockingTiles')
                                .replace('x/y/contentKey及basis', 'X/Y/ContentKey及Basis'))
            f['description'] += '已确认可恢复失败仍返回failed及实际位置/速度/支撑/释放证据；只有宿主continuationAllowed=true且continuationScope=observe_scene_then_move_input时，才可重新observe_scene后尝试一次有界move_input，不能按reason文字自行放行或重试导航。'
    return definitions

READS = {'inspect_container', 'get_player_state', 'observe_terrain', 'inspect_tile', 'scan_tiles', 'scan_entities', 'inspect_inventory', 'get_world_state'}
READS |= {'query_map', 'query_recipes', 'observe_scene', 'get_player_status', 'resolve_content', 'observe_exploration', 'query_combat'}
UTILITIES = {'wait'}
ACTIONS = set(SCHEMAS) - READS - UTILITIES
BUDGETED_ACTIONS = ACTIONS - {'stop_action'}
HOST_OPERATIONS = frozenset({'wait', 'observe_exploration', 'stop_action'})
NATIVE_OPERATIONS = frozenset(SCHEMAS) - HOST_OPERATIONS


def validate_backend(bridge, name, arguments):
    """Backend narrowing supplements, and never replaces, shared validation."""
    if 'server_ai_atomic_v1' in getattr(bridge, 'hello', {}).get('capabilities', ()):
        if name == 'use_item' and arguments:
            raise PreDispatchRejected('targeted_use_not_supported', status='targeted_use_not_supported')
    if name == 'mine_target' and 'cluster' in arguments and 'server_ai_excavation_v1' not in getattr(bridge, 'hello', {}).get('capabilities', ()):
        raise PreDispatchRejected('Requires server_ai_excavation_v1; no excavation task submitted')
    if name in ('combat_attack', 'query_combat') and 'server_ai_combat_v1' not in getattr(bridge, 'hello', {}).get('capabilities', ()):
        raise PreDispatchRejected('Requires server_ai_combat_v1')


def not_admitted_result(name, reason):
    return {'valid': False, 'status': 'failed', 'completed': False, 'operation': name,
            'reason': reason, 'dispatchState': 'not_admitted', 'actionId': None,
            'inputsAcquired': False, 'inputsReleased': None, 'effectVerified': False,
            'endEvidence': None, 'endEvidenceStatus': 'not_started'}


class ArgumentValidationError(ValueError):
    """Bounded schema facts; never include caller values in diagnostics."""
    def __init__(self, operation, issues, truncated=False):
        self.operation = operation
        self.diagnostics = issues
        self.diagnostics_truncated = truncated
        super().__init__('; '.join(issue['message'] for issue in issues)[:512])


def validate(name, arguments):
    """Validate shared schemas and losslessly normalize integer-valued numbers."""
    issues, truncated = [], False
    def typename(value):
        return {dict: 'object', list: 'array', str: 'string', bool: 'boolean',
                int: 'integer', float: 'number', type(None): 'null'}.get(type(value), 'unsupported type')
    def expected(schema):
        kind = schema['type']
        return 'array of ' + schema['items']['type'] + 's' if kind == 'array' else kind
    def issue(path, code, message, **facts):
        nonlocal truncated
        if len(issues) >= 8:
            truncated = True
            return
        issues.append(dict(path=path[:160], code=code, message=message[:240], **facts))
    def check(value, schema, path):
        kind = schema['type']
        if kind == 'object':
            if type(value) is not dict:
                issue(path, 'type_mismatch', f"field '{path}': expected object, got {typename(value)}", expected='object', actual=typename(value))
                return value
            properties = schema['properties']
            for key in schema.get('required', []):
                if key not in value:
                    issue(path+'.'+key, 'missing_required', f"missing required field '{key}'", expected=expected(properties[key]))
            for key, item in value.items():
                if key not in properties:
                    label=repr(key[:64]) if isinstance(key,str) else '<non-string key>'
                    issue(path, 'unknown_field', f"unknown field {label}")
            return {key: check(item, properties[key], path+'.'+key) for key,item in value.items() if key in properties}
        if kind == 'array':
            if type(value) is not list:
                issue(path, 'type_mismatch', f"field '{path}': expected {expected(schema)}, got {typename(value)}", expected=expected(schema), actual=typename(value))
                return value
            if not schema.get('minItems', 0) <= len(value) <= schema.get('maxItems', math.inf):
                issue(path, 'array_length', f"field '{path}': invalid array length", minimum=schema.get('minItems',0), maximum=schema.get('maxItems'))
                return value
            errors_before = len(issues)
            normalized = [check(entry, schema['items'], f'{path}[{i}]') for i,entry in enumerate(value,1)]
            if schema.get('uniqueItems') and not truncated and len(issues) == errors_before and len(set(normalized)) != len(normalized):
                issue(path, 'duplicate_items', f"field '{path}': duplicate array items")
            return normalized
        if kind in ('number', 'integer'):
            low, high = schema.get('minimum', -math.inf), schema.get('maximum', math.inf)
            numeric = type(value) is int or type(value) is float and math.isfinite(value)
            integral = kind != 'integer' or type(value) is int or type(value) is float and value.is_integer()
            if not numeric or not integral or not low <= value <= high:
                issue(path, 'invalid_number', f"{path}: expected finite {kind} in [{low}, {high}], got {typename(value)}; fractional values are not rounded", expected=kind, actual=typename(value))
                return value
            return int(value) if kind == 'integer' else value
        if kind == 'boolean' and type(value) is not bool:
            issue(path, 'type_mismatch', f"field '{path}': expected boolean, got {typename(value)}", expected='boolean', actual=typename(value))
        if kind == 'string':
            if type(value) is not str:
                issue(path, 'type_mismatch', f"field '{path}': expected string, got {typename(value)}", expected='string', actual=typename(value))
            elif not schema.get('minLength',0) <= len(value) <= schema.get('maxLength',math.inf):
                issue(path, 'string_length', f"field '{path}': string length outside allowed bounds", minimum=schema.get('minLength',0), maximum=schema.get('maxLength'))
            elif 'enum' in schema and value not in schema['enum']:
                issue(path, 'unsupported_value', f"field '{path}': unsupported value", allowed=schema['enum'])
            elif 'pattern' in schema and re.fullmatch(schema['pattern'],value) is None:
                issue(path, 'invalid_pattern', f"field '{path}': string does not match required pattern")
        return value
    if type(name) is not str or name not in SCHEMAS:
        raise ArgumentValidationError(None, [dict(path='operation',code='unsupported_operation',message='Unsupported atomic operation')])
    arguments = check(arguments, SCHEMAS[name], name)
    if issues:
        raise ArgumentValidationError(name, issues, truncated)
    if name == 'mine_target' and set(arguments) - {'cluster'} not in ({'target'}, {'x', 'y'}):
        raise ValueError('mine_target requires either target or absolute x/y')
    if name == 'observe_scene':
        region = arguments.get('scale') == 'region'
        if not region and any(k in arguments for k in ('width','height')):
            raise ValueError('observe_scene width/height require scale=region')
        if region:
            if arguments.get('width',120)*arguments.get('height',68)>8192:
                raise ValueError('observe_scene region area must be <=8192 tiles')
            if any(k in arguments for k in ('maxRegions','maxPlacements')) or any(
                    k not in ('map','geometry','objects','hazards') for k in arguments.get('layers',[])):
                raise ValueError('observe_scene region has no action candidates; use local observation')
    if name == 'query_map':
        if arguments.get('cellSize', 2) not in (1, 2, 4):
            raise ValueError('query_map.cellSize must be 1, 2 or 4')
        if arguments['mode'] == 'overview' and set(arguments) != {'mode'}:
            raise ValueError('query_map overview accepts only mode')
    if name == 'scan_tiles':
        width, height = arguments['maxX'] - arguments['minX'] + 1, arguments['maxY'] - arguments['minY'] + 1
        if width <= 0 or height <= 0 or width * height > 4096:
            raise ValueError(f'scan_tiles.bounds: inclusive area {width} x {height} = {width * height}; require ordered bounds and area <=4096 tiles. Use observe_terrain bounds (45 x 29) or split into bounded scans')
    if name == 'collect_drops' and 'entityRef' in arguments and 'typeIds' in arguments:
        raise ValueError('collect_drops entityRef and typeIds are mutually exclusive')
    if name == 'inspect_inventory' and (('item' in arguments) != (arguments.get('mode') == 'item')):
        raise ValueError('item is required only for mode=item')
    if name == 'inspect_inventory' and (('slots' in arguments) != (arguments.get('mode') == 'slots')):
        raise ValueError('slots is required only for mode=slots')
    if name in ('transfer_inventory_item', 'swap_inventory_slots') and arguments['sourceSlot'] == arguments['destinationSlot']:
        raise ValueError('Inventory source/destination must be different slots')
    if name == 'scan_entities':
        if 'typeIds' in arguments and arguments['category'] not in ('npc', 'item'):
            raise ValueError('typeIds requires category=npc or item')
        if 'npcFilter' in arguments and arguments['category'] != 'npc':
            raise ValueError('npcFilter requires category=npc')
    if name == 'move_input' and arguments['jumpHoldUpdates'] > arguments['maxUpdates']:
        raise ValueError('jumpHoldUpdates must not exceed maxUpdates')
    if name == 'navigate_to_landing_region' and arguments['minPlayerLeftX'] > arguments['maxPlayerLeftX']:
        raise ValueError('Landing X bounds must be ordered')

    return arguments


async def command(bridge, cmd, **args):
    return await asyncio.to_thread(bridge.command, cmd, **args)


def result(action, generation):
    end = action.get('endEvidence') or {}
    return {'valid': True, 'status': action['status'], 'reason': action['reason'], 'reasonDetail': action.get('reasonDetail'),
            'dispatchState': 'admitted', 'worldSessionId': action.get('worldSessionId'),
            'actorId': action.get('actorId'), 'actorSlot': action.get('actorSlot'),
            'inputsAcquired': end.get('inputsAcquired'), 'inputsReleased': end.get('inputsReleased'),
            'effectVerified': end.get('effectVerified'), 'stopRequest': action.get('stopRequest'),
            'operation': action.get('operation'), 'actionId': action['actionId'],
            'session': action.get('session'), 'actionGeneration': action.get('generation'), 'targetRegion': action.get('targetRegion'), 'targetTile': action.get('targetTile'), 'targetRecipe': action.get('targetRecipe'), 'targetInventory': action.get('targetInventory'), 'targetContainer': action.get('targetContainer'), 'targetTask': action.get('targetTask'),
            'task': action.get('task'), 'evidence': action.get('evidence'), 'endEvidence': action.get('endEvidence'),
            'endEvidenceStatus': action.get('endEvidenceStatus'), 'controlGeneration': generation,
            'completed': action['status'] == 'completed', 'cancelled': action['status'] == 'cancelled'}


def verified_server_object(action, evidence):
    target = action.get('targetTile') or {}
    footprint = evidence.get('footprint')
    return (isinstance(footprint, dict) and set(footprint) == {'x', 'y', 'width', 'height'}
            and all(type(v) is int for v in footprint.values())
            and evidence.get('objectPlacement') is True
            and evidence.get('fullFootprintVerified') is True
            and type(evidence.get('nativePlacementEvents')) is int and evidence['nativePlacementEvents'] == 1
            and evidence.get('uncertainNativeEffect') is False
            and type(evidence.get('typeId')) is int and evidence['typeId'] == 18
            and type(evidence.get('itemType')) is int and evidence['itemType'] == 36
            and type(evidence.get('style')) is int and evidence['style'] == 0
            and evidence.get('footprint') == dict(x=target.get('x'), y=target.get('y'), width=2, height=1))


CONTAINER_TRANSFERS = frozenset(('deposit_container_item', 'withdraw_container_item'))
CONTAINER_BATCHES = frozenset(('quick_stack_nearby', 'quick_stack_container', 'loot_all', 'deposit_all', 'restock'))
SORT_OPERATIONS = frozenset(('sort_inventory', 'sort_container'))
CONTAINER_OPERATIONS = frozenset(('open_container', 'close_container')) | CONTAINER_TRANSFERS | CONTAINER_BATCHES | SORT_OPERATIONS
INVENTORY_OPERATIONS = frozenset(('transfer_inventory_item', 'swap_inventory_slots', 'hold_inventory_item'))
EQUIPMENT_OPERATIONS = frozenset(('equip_item', 'unequip_item'))


def verified_server_equipment(action, evidence):
    goal = action.get('targetInventory')
    before, after = evidence.get('before'), evidence.get('after')
    if action.get('operation') not in EQUIPMENT_OPERATIONS or not all(isinstance(row, dict) for row in (goal, before, after)):
        return False
    slot = evidence.get('backpackSlot')
    mutation, confirmation = evidence.get('mutationGameUpdate'), evidence.get('confirmedAtGameUpdate')
    if (evidence.get('equipmentVerified') is not True or evidence.get('mutationAttempted') is not True
            or evidence.get('targetSlotId') != goal.get('targetSlotId')
            or type(slot) is not int or not 0 <= slot <= 49
            or type(mutation) is not int or type(confirmation) is not int or not 0 <= mutation < confirmation):
        return False
    bag, gear, new_bag, new_gear = (before.get('backpack'), before.get('equipment'),
                                    after.get('backpack'), after.get('equipment'))
    if not all(isinstance(row, dict) and isinstance(row.get('instanceId'), str)
               and isinstance(row.get('token'), str) and len(row['token']) == 64
               and all(type(row.get(key)) is int for key in ('typeId', 'stack', 'prefix'))
               and type(row.get('favorited')) is bool for row in (bag, gear, new_bag, new_gear)):
        return False
    def item(row):
        return tuple(row[key] for key in ('instanceId', 'typeId', 'stack', 'prefix', 'favorited'))
    if (gear['token'] != goal.get('targetToken') or item(bag) != item(new_gear)
            or item(gear) != item(new_bag) or bag['instanceId'] == gear['instanceId']):
        return False
    if action['operation'] == 'equip_item':
        return slot == goal.get('sourceSlot') and bag['token'] == goal.get('sourceToken') and bag['typeId'] > 0 and bag['stack'] == 1
    return (slot == goal.get('destinationSlot', slot) and bag['typeId'] == 0
            and gear['typeId'] > 0 and gear['stack'] > 0)


def verified_server_rope_motion(action, evidence):
    request = action.get('requestedInputs')
    if not isinstance(request, dict) or not all(evidence.get(k) == request.get(k)
            for k in ('ropeX', 'targetFeetY', 'finish', 'maxUpdates')):
        return False
    feet, target = evidence.get('feetY'), request.get('targetFeetY')
    updates, budget = evidence.get('updates'), request.get('maxUpdates')
    finish = request.get('finish')
    return (type(feet) in (int, float) and type(target) in (int, float)
            and type(updates) is int and type(budget) is int and 2 <= updates <= budget <= 360
            and evidence.get('settled') is True and evidence.get('attachmentObserved') is True
            and evidence.get('inputReleased') is True
            and (finish == 'hold' and evidence.get('attached') is True and abs(feet-target) <= 4
                 or finish == 'land' and evidence.get('attached') is False and abs(feet-target) <= .5
                 and evidence.get('grounded') is True and evidence.get('safeSupport') is True))


def verified_server_rope(action, evidence):
    target = action.get('targetTile')
    if not isinstance(target, dict) or set(target) != {'x', 'y'}:
        return False
    x, y = target['x'], target['y']
    end_y = evidence.get('y')
    initial, remaining = evidence.get('InitialStack'), evidence.get('remainingStack')
    return (type(x) is int and type(y) is int and type(end_y) is int and 0 <= end_y - y <= 16
            and all(type(evidence.get(k)) is int for k in ('requestedX', 'requestedY', 'x', 'itemType', 'typeId'))
            and evidence.get('requestedX') == evidence.get('x') == x and evidence.get('requestedY') == y
            and evidence.get('extended') is (end_y > y)
            and {965: 213, 2996: 353, 3077: 365, 3078: 366}.get(evidence.get('itemType'), -1) == evidence.get('typeId')
            and evidence.get('placedByActor') is True and evidence.get('targetPresent') is True
            and evidence.get('targetChanged') is False and evidence.get('uncertainNativeEffect') is False
            and type(evidence.get('nativePlacementEvents')) is int and evidence['nativePlacementEvents'] == 1
            and type(evidence.get('consumedCount')) is int and evidence['consumedCount'] == 1
            and type(initial) is int and type(remaining) is int and remaining >= 0 and initial - remaining == 1)


def verified_server_inventory(action, evidence):
    goal = action.get('targetInventory')
    if not isinstance(goal, dict) or action.get('operation') not in INVENTORY_OPERATIONS:
        return False
    try:
        validate(action['operation'], goal)
    except (ValueError, TypeError, KeyError):
        return False
    before, after = evidence.get('before'), evidence.get('after')
    if (not isinstance(before, dict) or not isinstance(after, dict)
            or evidence.get('inventoryVerified') is not True or evidence.get('mutationAttempted') is not True
            or type(evidence.get('mutationGameUpdate')) is not int
            or type(evidence.get('confirmedAtGameUpdate')) is not int
            or evidence['confirmedAtGameUpdate'] <= evidence['mutationGameUpdate']):
        return False
    destination = ((goal['hotbarKey'] - 1) % 10 if action['operation'] == 'hold_inventory_item' else goal['destinationSlot'])
    for key, slot, token in (('source', goal['sourceSlot'], goal['sourceToken']), ('destination', destination, goal['destinationToken'])):
        b, a = before.get(key), after.get(key)
        if (not isinstance(b, dict) or not isinstance(a, dict) or b.get('slot') != slot or a.get('slot') != slot
                or b.get('inventoryToken') != token or not isinstance(a.get('inventoryToken'), str) or len(a['inventoryToken']) != 64
                or any(type(v.get(field)) is not int for v in (a, b) for field in ('typeId', 'stack', 'prefix', 'maxStack'))
                or any(type(v.get('favorited')) is not bool for v in (a, b))):
            return False
    if evidence.get('sourceSlot') != goal['sourceSlot'] or evidence.get('destinationSlot') != destination:
        return False
    selected = destination if action['operation'] == 'hold_inventory_item' else before.get('selectedSlot')
    if type(selected) is not int or after.get('selectedSlot') != selected or evidence.get('selectedSlot') != selected:
        return False
    s, d, a, b = before['source'], before['destination'], after['source'], after['destination']
    fields = ('typeId', 'stack', 'prefix', 'maxStack', 'favorited')
    if action['operation'] != 'transfer_inventory_item':
        return all(a[k] == d[k] and b[k] == s[k] for k in fields)
    count = goal['count']
    def expected(item, stack, favorite):
        return dict(zip(fields, (0, 0, 0, 0, False))) if stack == 0 else {**{k: item[k] for k in fields}, 'stack': stack, 'favorited': favorite}
    return (0 < count <= s['stack'] <= s['maxStack'] and (d['typeId'] == 0 or s['typeId'] == d['typeId'] and s['prefix'] == d['prefix'])
            and a['stack'] == s['stack'] - count and b['stack'] == d['stack'] + count <= b['maxStack']
            and all(a[k] == expected(s, s['stack'] - count, s['favorited'] if d['typeId'] == 0 else False)[k] for k in fields)
            and all(b[k] == expected(s if d['typeId'] == 0 else d, d['stack'] + count,
                                    count == s['stack'] and s['favorited'] if d['typeId'] == 0 else d['favorited'] or s['favorited'])[k] for k in fields))


def verified_server_mine_task(action, evidence):
    task = evidence.get('task')
    if not isinstance(task, dict):
        return False
    arguments = action.get('targetTask')
    if isinstance(arguments, dict) and arguments.get('cluster') is True:
        return verified_excavation_task(action, task, arguments.get('target', arguments))
    last = task.get('lastResult') or {}
    if not isinstance(last, dict):
        return False
    proof = last.get('evidence') or {}
    goal = action.get('targetTask') or {}
    goal = goal.get('target', goal) if isinstance(goal, dict) else {}
    if not isinstance(proof, dict) or not isinstance(goal, dict):
        return False
    return (task.get('taskId') == action.get('actionId') and task.get('taskName') == 'mine_target'
            and task.get('status') == 'completed' and task.get('phase') == 'terminal'
            and task.get('worldSessionId') == action.get('worldSessionId')
            and task.get('actorGeneration') == action.get('generation') and task.get('actorSlot') == action.get('actorSlot')
            and last.get('status') == 'completed' and last.get('inputsReleased') is True and last.get('effectVerified') is True
            and proof.get('destroyedByActor') is True and proof.get('targetAbsent') is True and proof.get('targetChanged') is False
            and isinstance(action.get('targetTask'), dict)
            and all(type(goal.get(k)) is int and proof.get(k.upper()) == goal[k] for k in ('x', 'y'))
            and ('typeId' not in goal or proof.get('TileType') == goal['typeId']))


def verified_excavation_task(action, task, goal):
    if not (isinstance(goal, dict) and task.get('taskId') == action.get('actionId')
            and task.get('taskName') == 'mine_target' and task.get('status') == 'completed'
            and task.get('phase') == 'terminal' and task.get('currentChild') is None
            and task.get('worldSessionId') == action.get('worldSessionId')
            and task.get('actorGeneration') == action.get('generation') and task.get('actorSlot') == action.get('actorSlot')):
        return False
    result = task.get('result')
    if not (isinstance(result, dict) and result.get('status') == 'completed'
            and result.get('inputsReleased') is True and result.get('effectVerified') is True):
        return False
    batch = result.get('evidence')
    if not isinstance(batch, dict) or batch.get('outcome') != 'completed':
        return False
    seed = (goal.get('x'), goal.get('y'))
    ore = batch.get('typeId')
    if (not all(type(value) is int for value in seed) or seed != (batch.get('seedX'), batch.get('seedY'))
            or type(ore) is not int or ('typeId' in goal and ore != goal['typeId'])):
        return False
    targets = batch.get('targets')
    if not isinstance(targets, list) or not 1 <= len(targets) <= 18:
        return False
    cells = set()
    for target in targets:
        if not isinstance(target, dict) or target.get('status') != 'verified' or target.get('typeId') != ore:
            return False
        cell = (target.get('x'), target.get('y'))
        proof = target.get('proof')
        if (not all(type(value) is int for value in cell) or cell in cells or not isinstance(proof, dict)
                or (proof.get('X'), proof.get('Y'), proof.get('Type')) != (*cell, ore)
                or not all(type(proof.get(k)) is int and proof[k] > 0 for k in ('Update', 'Strikes'))):
            return False
        cells.add(cell)
    if seed not in cells:
        return False
    frontier = [seed]
    cells.remove(seed)
    while frontier:
        x, y = frontier.pop()
        for neighbor in ((x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1)):
            if neighbor in cells:
                cells.remove(neighbor)
                frontier.append(neighbor)
    return not cells


def verified_server_craft(action, evidence):
    goal = action.get('targetRecipe')
    return (isinstance(goal, dict) and set(goal) == {'recipeId', 'recipeToken', 'crafts'}
            and type(goal['recipeId']) is int and type(goal['crafts']) is int and 1 <= goal['crafts'] <= 20
            and type(evidence.get('recipeId')) is int and type(evidence.get('requestedCrafts')) is int
            and evidence.get('recipeId') == goal['recipeId'] and evidence.get('recipeToken') == goal['recipeToken']
            and type(evidence.get('completedCrafts')) is int and evidence['completedCrafts'] == goal['crafts']
            and evidence.get('requestedCrafts') == goal['crafts'] and evidence.get('inventoryVerified') is True
            and type(evidence.get('producedCount')) is int and evidence['producedCount'] > 0
            and type(evidence.get('outputTypeId')) is int and evidence['outputTypeId'] > 0
            and type(evidence.get('outputCountPerCraft')) is int and evidence['outputCountPerCraft'] > 0
            and evidence['producedCount'] == goal['crafts'] * evidence['outputCountPerCraft']
            and evidence.get('mutationAttempted') is True
            and isinstance(evidence.get('batches'), list) and len(evidence['batches']) == goal['crafts'])


def same_action(expected, current):
    return isinstance(current, dict) and all(current.get(key) == expected.get(key) for key in
        ('actionId', 'session', 'generation', 'worldSessionId', 'operation', 'targetRegion', 'targetTile', 'targetRecipe', 'requestedInputs', 'targetInventory', 'targetContainer', 'targetTask', 'actorId', 'actorSlot'))


def verify_landing_start(action, arguments, *, server_ai=False):
    region = action.get('targetRegion')
    if (not isinstance(region, dict) or set(region) != set(arguments)
            or any(type(region[key]) not in (int, float) or not math.isfinite(region[key])
                   or not math.isclose(region[key], arguments[key], rel_tol=1e-7, abs_tol=0.001) for key in arguments)
            or (not isinstance(action.get('session'), str) or not action['session']
                if server_ai else type(action.get('session')) is not int or action['session'] <= 0)
            or type(action.get('generation')) is not int or action['generation'] < 0
            or not isinstance(action.get('worldSessionId'), str) or not action['worldSessionId']):
        raise BridgeError('Landing target/identity missing or different in start receipt')


def verified_server_navigation(action, evidence):
    task = evidence.get('task')
    target = action.get('targetTask')
    if (not isinstance(task, dict) or not isinstance(target, dict) or set(target) != {'x', 'y'}
            or any(type(value) not in (float, int) for value in target.values())
            or task.get('taskId') != action.get('actionId') or task.get('taskName') != 'navigate_to'
            or task.get('status') != 'completed' or task.get('currentChild') is not None):
        return False
    result = task.get('result')
    arrival = result.get('evidence') if isinstance(result, dict) else None
    return (isinstance(arrival, dict)
            and result.get('effectVerified') is True and result.get('inputsReleased') is True
            and arrival.get('target') == target
            and all(arrival.get(key) is True for key in ('arrived', 'grounded', 'safeSupport', 'settled'))
            and type(arrival.get('x')) in (float, int) and type(arrival.get('feetY')) in (float, int)
            and arrival['x'] - .01 <= target['x'] <= arrival['x'] + 20.01
            and abs(arrival['feetY'] - target['y']) <= .51)


def verified_server_landing(action, evidence):
    if action.get('operation') != 'navigate_to_landing_region':
        return False
    region = action.get('targetRegion')
    if (not isinstance(region, dict) or set(region) != {'minPlayerLeftX', 'maxPlayerLeftX', 'surfaceY'}
            or evidence.get('targetRegion') != region
            or any(evidence.get(key) is not True for key in ('arrived', 'grounded', 'safeSupport', 'settled'))):
        return False
    values = [region[key] for key in ('minPlayerLeftX', 'maxPlayerLeftX', 'surfaceY')]
    values += [evidence.get('x'), evidence.get('y'), evidence.get('vx'), evidence.get('vy')]
    if any(type(v) not in (int, float) or not math.isfinite(v) for v in values):
        return False
    low, high, surface, x, y, vx, vy = values
    return low <= x <= high and abs(y + 42 - surface) <= 0.5 and abs(vx) <= 0.1 and abs(vy) <= 0.1


def verified_container_transfer(action, evidence, end):
    goal = action.get('targetContainer')
    before, after = evidence.get('before'), evidence.get('after')
    if not all(isinstance(row, dict) for row in (goal, before, after, end)):
        return False
    integers = ('requestedCount', 'movedCount', 'remainingCount', 'mutationGameUpdate', 'observedGameUpdate', 'chestIndex')
    if not all(type(evidence.get(key)) is int for key in integers):
        return False
    count, moved = evidence['requestedCount'], evidence['movedCount']
    if (evidence.get('mutationAttempted') is not True or end.get('inputsAcquired') is not False
            or evidence['chestIndex'] < 0 or not 0 < moved <= count or count != goal.get('count')
            or evidence['remainingCount'] != count - moved
            or not 0 <= evidence['mutationGameUpdate'] < evidence['observedGameUpdate'] == end.get('gameUpdate')
            or any(evidence.get(slot) != goal.get(slot) for slot in ('inventorySlot', 'containerSlot'))):
        return False
    for pair, side, delta in ((before, 'source', 0), (before, 'destination', 0),
                              (after, 'source', -moved), (after, 'destination', moved)):
        slot, original = pair.get(side), before.get(side)
        if not all(isinstance(row, dict) and type(row.get('stack')) is int for row in (slot, original)):
            return False
        if slot['stack'] < 0 or slot['stack'] != original['stack'] + delta:
            return False
    if moved == count:
        return (action.get('status') == 'completed' and evidence.get('effectStatus') == 'complete'
                and evidence.get('blockedSlot') is None)
    blocked = 'containerSlot' if action.get('operation') == 'deposit_container_item' else 'inventorySlot'
    return (action.get('status') == 'failed' and evidence.get('effectStatus') == 'partial'
            and evidence.get('blockedSlot') == goal.get(blocked))


def verified_container_batch(action, evidence, end):
    moves, blocked = evidence.get('moves'), evidence.get('blocked')
    if (not isinstance(end, dict) or end.get('inputsAcquired') is not False
            or evidence.get('mutationAttempted') is not True
            or not all(type(evidence.get(key)) is int for key in ('mutationGameUpdate', 'observedGameUpdate', 'movedCount'))
            or not 0 <= evidence['mutationGameUpdate'] < evidence['observedGameUpdate'] == end.get('gameUpdate')
            or not isinstance(moves, list) or len(moves) > 128 or not isinstance(blocked, list)):
        return False
    if not all(isinstance(move, dict) and all(type(move.get(key)) is int for key in
               ('inventorySlot', 'chestIndex', 'containerSlot', 'typeId', 'prefix', 'count'))
               and 0 <= move['inventorySlot'] < 58 and 0 <= move['containerSlot'] < 40
               and move['chestIndex'] >= 0 and move['typeId'] > 0 and move['count'] > 0 for move in moves):
        return False
    if sum(move['count'] for move in moves) != evidence['movedCount']:
        return False
    expected = 'partial' if moves and blocked else 'complete' if moves else 'none'
    return (evidence.get('effectStatus') == expected
            and action.get('status') == ('failed' if blocked else 'completed'))


def verified_server_combat(action, task, result, proof):
    if (task.get('taskName') != 'combat_attack' or task.get('taskId') != action.get('actionId')
            or task.get('status') != 'completed' or task != action.get('task')
            or result.get('inputsReleased') is not True or result.get('effectVerified') is not True):
        return False

    def defeated(hit):
        return (isinstance(hit, dict) and hit.get('Defeated') is True
                and type(hit.get('LifeAfter')) is int and type(hit.get('LifeBefore')) is int
                and hit['LifeAfter'] <= 0 < hit['LifeBefore'])

    requested = action.get('targetTask') or {}
    target = proof.get('target') or {}
    hit = proof.get('lastHit')
    if proof.get('source') != 'reflex' or result.get('reason') != 'combat_threats_cleared':
        return (target.get('EntityRef') == requested.get('entityRef')
                and defeated(hit) and proof.get('life') == hit['LifeAfter'])

    encounters = proof.get('encounters')
    if (not requested.get('threat') or requested['threat'] != proof.get('threat')
            or not isinstance(encounters, list) or not encounters):
        return False
    kills = set()
    previous_kill_tick = -1
    for encounter in encounters:
        if not isinstance(encounter, dict) or not isinstance(encounter.get('Target'), dict):
            return False
        reference = encounter['Target'].get('EntityRef')
        if not isinstance(reference, str) or not reference:
            return False
        reason = encounter.get('Reason')
        if reason == 'combat_target_defeated':
            strike = encounter.get('LastHit')
            if (not defeated(strike) or type(strike.get('DamageDone')) is not int or strike['DamageDone'] <= 0
                    or type(strike.get('GameUpdate')) is not int or strike['GameUpdate'] <= previous_kill_tick
                    or reference in kills):
                return False
            previous_kill_tick = strike['GameUpdate']
            kills.add(reference)
        elif reason not in ('combat_target_replaced', 'combat_target_invalidated',
                             'combat_target_out_of_range', 'combat_approach_failed', 'combat_shot_blocked',
                             'combat_firing_navigation_no_route'):
            return False
    return (encounters[0]['Target']['EntityRef'] == requested.get('entityRef')
            and encounters[-1]['Target'] == target and encounters[-1].get('LastHit') == hit)


def verified_sort(action, evidence, end):
    inventory = action.get('operation') == 'sort_inventory'
    slots = evidence.get('changedSlots')
    index = evidence.get('chestIndex')
    observed, mutated = evidence.get('observedGameUpdate'), evidence.get('mutationGameUpdate')
    return (isinstance(end, dict) and end.get('inputsAcquired') is False
            and evidence.get('sortVerified') is True and evidence.get('mutationAttempted') is True
            and type(mutated) is int and type(observed) is int and 0 <= mutated < observed == end.get('gameUpdate')
            and (index is None if inventory else type(index) is int and index >= 0)
            and isinstance(slots, list) and len(slots) <= (59 if inventory else 40)
            and all(isinstance(row, dict) and type(row.get('slot')) is int
                    and 0 <= row['slot'] < (59 if inventory else 40)
                    and type(row.get('stack')) is int and row['stack'] >= 0
                    and isinstance(row.get('instanceId'), str) for row in slots)
            and len({row['slot'] for row in slots}) == len(slots))


async def terminal(bridge, action, generation):
    deadline = asyncio.get_running_loop().time() + 1.0
    while action.get('endEvidenceStatus') == 'pending' and asyncio.get_running_loop().time() < deadline:
        await asyncio.sleep(0.05)
        reply = await command(bridge, 'get_movement')
        current = reply['movement']['action']
        if (current is None or current['actionId'] != action['actionId']
                or not same_action(action, current)):
            return {'valid': False, 'status': 'unknown', 'reason': 'action_changed_before_release_evidence'}
        action, generation = current, reply['movement']['generation']
    out = result(action, generation)
    end = action.get('endEvidence')
    server_ai_end = (isinstance(end, dict)
                     and 'server_ai_atomic_v1' in bridge.hello.get('capabilities', [])
                     and action.get('operation') == 'select_hotbar_slot'
                     and end.get('phase') == 'ServerAI_game_thread_no_input'
                     and end.get('inputsAcquired') is False
                     and isinstance(action.get('actorId'), str) and bool(action['actorId'])
                     and end.get('actorId') == action['actorId']
                     and end.get('actorGeneration') == action.get('generation')
                     and end.get('actorSlot') == action.get('actorSlot')
                     and (action.get('status') != 'completed'
                          or end.get('selectionVerified') is True
                          and action.get('evidence', {}).get('selectionApplied') is True
                          and end.get('selectedSlot') == action.get('evidence', {}).get('targetSlot')))
    native_evidence = action.get('evidence') or {}
    native_evidence = native_evidence if isinstance(native_evidence, dict) else {}
    combat = native_evidence.get('task')
    combat = combat if isinstance(combat, dict) else {}
    combat_result = combat.get('result') or {}
    combat_proof = combat_result.get('evidence') or {}
    container_verified = (native_evidence.get('containerVerified') is True
                          and native_evidence.get('mutationAttempted') is True
                          and all(type(native_evidence.get(key)) is int
                                  for key in ('mutationGameUpdate', 'observedGameUpdate', 'chestIndex'))
                          and 0 <= native_evidence['mutationGameUpdate'] < native_evidence['observedGameUpdate']
                          and isinstance(end, dict) and native_evidence['observedGameUpdate'] == end.get('gameUpdate'))
    if action.get('operation') in CONTAINER_TRANSFERS:
        effect = native_evidence.get('effectStatus')
        if action.get('status') == 'completed' or effect == 'partial':
            container_verified = verified_container_transfer(action, native_evidence, end)
    if action.get('operation') in CONTAINER_BATCHES:
        container_verified = verified_container_batch(action, native_evidence, end)
    pickup = combat_result.get('evidence')
    native_verified = {
        'place_item': (combat.get('taskName') == 'place_item' and combat.get('taskId') == action.get('actionId')
                       and combat == action.get('task') and combat.get('status') == 'completed'
                       and combat_result.get('effectVerified') is True and combat_result.get('inputsReleased') is True
                       and isinstance(pickup, dict) and pickup.get('requested') == action.get('targetTask')
                       and pickup.get('placedByActor') is True and pickup.get('targetPresent') is True
                       and pickup.get('targetChanged') is False and pickup.get('uncertainNativeEffect') is False
                       and pickup.get('nativePlacementEvents') == 1 and pickup.get('consumedCount') == 1
                       and (pickup.get('kind') != 'chest' or pickup.get('chestVerified') is True)),
        'collect_drops': (combat.get('taskName') == 'collect_drops' and combat.get('taskId') == action.get('actionId')
                          and combat == action.get('task') and combat.get('status') == 'completed'
                          and combat_result.get('effectVerified') is True and combat_result.get('inputsReleased') is True
                          and isinstance(pickup, dict) and pickup.get('remaining') == []
                          and pickup.get('unavailable') == [] and pickup.get('pickupUnconfirmed') is False
                          and ((pickup.get('truncated') is False and pickup.get('outcome') in ('collected', 'none'))
                               or (pickup.get('truncated') is True and pickup.get('outcome') == 'partial'
                                   and pickup.get('canContinue') is True))),
        **dict.fromkeys(SORT_OPERATIONS, verified_sort(action, native_evidence, end)),
        **dict.fromkeys(CONTAINER_BATCHES, container_verified),
        **dict.fromkeys(CONTAINER_TRANSFERS, container_verified),
        'move_input': (native_evidence.get('requestedInputs') == action.get('requestedInputs')
                       and isinstance(action.get('requestedInputs'), dict)
                       and type(native_evidence.get('updatesExecuted')) is int
                       and native_evidence['updatesExecuted'] == action['requestedInputs'].get('maxUpdates')
                       and native_evidence.get('updatesRequested') == native_evidence['updatesExecuted']
                       and native_evidence.get('inputReleased') is True),
        'navigate_to': verified_server_navigation(action, native_evidence),
        'navigate_to_x': native_evidence.get('arrived') is True,
        'navigate_to_landing_region': verified_server_landing(action, native_evidence),
        'use_item': native_evidence.get('cycleVerified') is True,
        'open_container': (container_verified and native_evidence.get('chestIndex', -2) >= 0
                           and native_evidence.get('typeId') in (21, 467)
                           and isinstance(action.get('targetContainer'), dict)
                           and all(type(native_evidence.get(key)) is int
                                   and action['targetContainer'].get(key) in (native_evidence[key], native_evidence[key] + 1)
                                   for key in ('x', 'y'))),
        'close_container': container_verified and native_evidence.get('chestIndex', -2) == -1,
        'craft_recipe': verified_server_craft(action, native_evidence),
        'mine_target': verified_server_mine_task(action, native_evidence),
        'combat_attack': verified_server_combat(action, combat, combat_result, combat_proof),
        **{op: verified_server_inventory(action, native_evidence) for op in INVENTORY_OPERATIONS},
        **{op: verified_server_equipment(action, native_evidence) for op in EQUIPMENT_OPERATIONS},
        'place_tile': (isinstance(action.get('targetTile'), dict)
                       and set(action['targetTile']) == {'x', 'y'}
                       and all(type(action['targetTile'][key]) is int and type(native_evidence.get(key)) is int for key in ('x', 'y'))
                       and native_evidence.get('x') == action['targetTile'].get('x')
                       and native_evidence.get('y') == action['targetTile'].get('y')
                       and native_evidence.get('placedByActor') is True
                       and native_evidence.get('targetPresent') is True
                       and native_evidence.get('targetChanged') is False
                       and type(native_evidence.get('consumedCount')) is int and native_evidence['consumedCount'] == 1),
        'place_rope': verified_server_rope(action, native_evidence),
        'move_on_rope': verified_server_rope_motion(action, native_evidence),
        'place_object': (isinstance(action.get('targetTile'), dict)
                       and set(action['targetTile']) == {'x', 'y'}
                       and all(type(action['targetTile'][key]) is int and type(native_evidence.get(key)) is int for key in ('x', 'y'))
                       and native_evidence.get('x') == action['targetTile'].get('x')
                       and native_evidence.get('y') == action['targetTile'].get('y')
                       and native_evidence.get('placedByActor') is True
                       and native_evidence.get('targetPresent') is True
                       and native_evidence.get('targetChanged') is False
                       and type(native_evidence.get('consumedCount')) is int and native_evidence['consumedCount'] == 1
                       and verified_server_object(action, native_evidence)),
        'mine_tile': (native_evidence.get('destroyedByActor') is True
                      and native_evidence.get('targetAbsent') is True
                      and native_evidence.get('targetChanged') is False),
    }.get(action.get('operation'), False)
    native_end = (isinstance(end, dict)
                  and 'server_ai_actions_v1' in bridge.hello.get('capabilities', [])
                  and action.get('operation') in ('move_input', 'move_on_rope', 'navigate_to', 'navigate_to_x', 'navigate_to_landing_region', 'use_item', 'mine_tile', 'place_tile', 'place_object', 'place_rope', 'craft_recipe', 'mine_target', 'combat_attack', 'collect_drops', 'place_item', *INVENTORY_OPERATIONS, *EQUIPMENT_OPERATIONS, *CONTAINER_OPERATIONS)
                  and end.get('phase') == 'ServerAI_game_thread_inputs_released'
                  and end.get('operation') == action.get('operation')
                  and end.get('inputsReleased') is True
                  and type(end.get('inputsAcquired')) is bool
                  and isinstance(action.get('actorId'), str) and bool(action['actorId'])
                  and end.get('actorId') == action['actorId']
                  and end.get('actorGeneration') == action.get('generation')
                  and end.get('actorSlot') == action.get('actorSlot')
                  and (action.get('status') != 'completed'
                       and not (action.get('operation') in (CONTAINER_TRANSFERS | CONTAINER_BATCHES) and native_evidence.get('effectStatus') == 'partial')
                       or end.get('effectVerified') is True and native_verified))
    server_ai_end = server_ai_end or native_end
    valid_phase = server_ai_end if 'server_ai_atomic_v1' in bridge.hello.get('capabilities', []) else (
        isinstance(end, dict) and end.get('phase') in ('PostUpdateRunSpeeds_before_bridge_input', 'UpdateDead_after_revoke'))
    if (action.get('endEvidenceStatus') != 'known' or not isinstance(end, dict)
            or type(end.get('gameUpdate')) is not int or end['gameUpdate'] < 0
            or not isinstance(end.get('worldSessionId'), str) or not end['worldSessionId']
            or end['worldSessionId'] != action.get('worldSessionId')
            or not valid_phase):
        out.update(valid=False, status='unknown', completed=False, reason='missing_game_thread_release_evidence', terminalActionStatus=action['status'])
        if action.get('operation') in (CONTAINER_TRANSFERS | CONTAINER_BATCHES | SORT_OPERATIONS):
            out.update(effectVerified=False, evidence=None)
    return out


async def stop(bridge, action_id=None, expected_action=None, *, lifecycle=None):
    reply = await command(bridge, 'get_movement')
    action = reply['movement']['action']
    if lifecycle is not None:
        lifecycle.update(action=copy.deepcopy(action), control_generation=reply['movement']['generation'])
    if action is None:
        if action_id is not None:
            return {'valid': False, 'status': 'unknown', 'completed': False, 'reason': 'action_missing', 'actionId': action_id}
        return {'valid': True, 'status': 'completed', 'reason': 'no_agent_action', 'noOp': True}
    if (action_id is not None and action['actionId'] != action_id
            or expected_action is not None and not same_action(expected_action, action)):
        return {'valid': False, 'status': 'cancelled', 'reason': 'ownership_changed'}
    if action['status'] == 'running':
        owned = action
        reply = await command(bridge, 'cancel_move', actionId=action['actionId'])
        action = reply['movement']['action']
        if not same_action(owned, action) or action['status'] == 'running':
            return {'valid': False, 'status': 'unknown', 'completed': False, 'reason': 'cancellation_unconfirmed'}
    return await terminal(bridge, action, reply['movement']['generation'])


async def dispatch(bridge, name, arguments, chat_id=None, *, lifecycle=None, owner=None):
    # A Lua run owns the whole atomic session, including its cancellation cleanup.
    if getattr(bridge, '_lua_poisoned', False):
        raise BridgeError('Lua session has unresolved action state; reconnect required')
    if getattr(bridge, '_lua_owner', None) is not owner:
        raise BridgeError('Atomic session is owned by a Lua run')
    bridge._atomic_inflight = getattr(bridge, '_atomic_inflight', 0) + 1
    try:
        return await _dispatch(bridge, name, arguments, chat_id, lifecycle=lifecycle)
    finally:
        bridge._atomic_inflight -= 1


async def _dispatch(bridge, name, arguments, chat_id, *, lifecycle):
    def track(**fields):
        if lifecycle is not None:
            lifecycle.update(copy.deepcopy(fields))
    arguments = validate(name, arguments)
    validate_backend(bridge, name, arguments)
    if name in ('collect_drops', 'place_item'):
        raise PreDispatchRejected(f'Use submit_task for {name}')
    if name == 'navigate_to' and 'server_ai_navigation_v1' not in bridge.hello.get('capabilities', []):
        raise PreDispatchRejected('Requires server_ai_navigation_v1; no navigation task submitted')
    if name == 'mine_target' and 'server_ai_tasks_v1' not in bridge.hello.get('capabilities', []):
        raise PreDispatchRejected('Requires server_ai_tasks_v1; no task submitted')
    if name in {'query_recipes','craft_recipe'} and 'server_ai_crafting_v1' not in bridge.hello.get('capabilities', []):
        raise PreDispatchRejected('Requires server_ai_crafting_v1; no crafting RPC started')
    if name == 'place_object' and 'server_ai_place_object_workbench_v1' not in bridge.hello.get('capabilities', []):
        raise PreDispatchRejected('Requires server_ai_place_object_workbench_v1; no object placement RPC started')
    if name == 'place_rope' and 'server_ai_place_rope_v1' not in bridge.hello.get('capabilities', []):
        raise PreDispatchRejected('Requires server_ai_place_rope_v1; no rope placement RPC started')
    if name == 'observe_scene' and 'server_ai_scene_v1' not in bridge.hello.get('capabilities', []):
        raise PreDispatchRejected('Requires server_ai_scene_v1; no scene query started')
    if name == 'observe_scene' and any(k in arguments for k in ('scale','width','height')) and 'server_ai_scene_region_v1' not in bridge.hello.get('capabilities', []):
        raise PreDispatchRejected('Requires server_ai_scene_region_v1; no regional scene query started')
    supported = getattr(bridge, 'supported_atomic_operations', None)
    if supported is not None and name not in supported:
        raise PreDispatchRejected(f'{name} is unsupported by this backend; no action started')
    if name == 'wait':
        started = perf_counter()
        # asyncio's event-loop clock can have a coarse resolution on Windows and
        # allow early timer callbacks. Keep waiting against a precise monotonic clock.
        duration = arguments['milliseconds'] / 1000
        while (remaining := duration - (perf_counter() - started)) > 0:
            await asyncio.sleep(remaining)
        return {'valid': True, 'status': 'completed', 'elapsedMilliseconds': (perf_counter() - started) * 1000,
                'clock': 'host_monotonic', 'effectStatus': 'not_applicable'}
    if name == 'observe_exploration':
        from exploration import read_bounded_exploration
        return await asyncio.to_thread(read_bounded_exploration, bridge, **arguments)
    if name in SORT_OPERATIONS and 'server_ai_sorting_v1' not in bridge.hello.get('capabilities', []):
        raise PreDispatchRejected('Requires server_ai_sorting_v1')
    if name in CONTAINER_BATCHES and 'server_ai_container_batch_v1' not in bridge.hello.get('capabilities', []):
        raise PreDispatchRejected('Requires server_ai_container_batch_v1')
    if name in CONTAINER_TRANSFERS and 'server_ai_container_transfer_v1' not in bridge.hello.get('capabilities', []):
        raise PreDispatchRejected('Requires server_ai_container_transfer_v1')
    if name in CONTAINER_OPERATIONS | {'inspect_container'} and 'server_ai_containers_v1' not in bridge.hello.get('capabilities', []):
        raise PreDispatchRejected('Requires server_ai_containers_v1')
    if name == 'inspect_container':
        reply = await command(bridge, 'query_atomic', operation=name, arguments=arguments)
        envelope = reply.get('data')
        if not isinstance(envelope, dict):
            raise BridgeError('Missing server AI container envelope')
        return {'valid': envelope.get('available') is True, **envelope}
    if name == 'query_map':
        if 'server_ai_map_v1' not in bridge.hello.get('capabilities', []):
            raise PreDispatchRejected('AI map unavailable', status='unsupported_operation')
    if name in {'query_map', 'query_recipes', 'observe_scene', 'observe_terrain', 'scan_tiles', 'scan_entities', 'inspect_tile', 'resolve_content'} and 'server_ai_perception_v1' in bridge.hello.get('capabilities', []):
        reply = await command(bridge, 'query_atomic', operation=name, arguments=arguments)
        envelope = reply.get('data')
        if not isinstance(envelope, dict):
            raise BridgeError('Missing server AI perception envelope')
        return {'valid': envelope.get('available') is True, **envelope}
    if name == 'get_player_state' and 'server_ai_reads_v1' in bridge.hello.get('capabilities', []):
        reply = await command(bridge, 'query_atomic', operation=name, arguments=arguments)
        envelope = reply.get('data')
        if not isinstance(envelope, dict):
            raise BridgeError('Missing server AI player query data')
        if envelope.get('available') is not True:
            return {'valid': False, **envelope}
        data = envelope.get('data')
        if not isinstance(data, dict) or not isinstance(data.get('player'), dict) or not isinstance(data.get('motion'), dict):
            raise BridgeError('Missing server AI player/motion projection')
        return {'valid': True, **{key: value for key, value in envelope.items() if key != 'data'}, **data}
    if name in ('get_player_state', 'observe_terrain'):
        reply = await asyncio.to_thread(bridge.get_terrain)
        if name == 'observe_terrain':
            return {'valid': True, **reply}
        return {'valid': True, 'player': reply['state'], 'motion': {k: reply['terrain'][k] for k in ('grounded', 'supported', 'playerWidth', 'playerHeight')}}
    if 'atomic_operations_v1' not in bridge.hello.get('capabilities', []):
        raise PreDispatchRejected('Requires atomic_operations_v1; loaded Mod does not support these operations')
    if name == 'navigate_to_landing_region' and 'landing_region_navigation_v1' not in bridge.hello.get('capabilities', []):
        raise PreDispatchRejected('Requires landing_region_navigation_v1; no two-dimensional action started')
    required_capability = {'move_input': 'server_ai_direct_input_v1', 'move_on_rope': 'server_ai_move_on_rope_v1',
                           'select_hotbar_slot': 'hotbar_keys_v1', 'drop_through_platform': 'platform_drop_v1'}.get(name)
    if (name in ('get_player_status', 'resolve_content')
            or name == 'scan_entities' and set(arguments) - {'category', 'radius'}):
        required_capability = 'atomic_foundations_v1'
    if name in ('scan_tiles', 'mine_wall', 'place_wall', 'hammer_tile', 'inspect_inventory', 'interact_tile'):
        required_capability = 'atomic_extensions_v1'
    if (name in ('transfer_inventory_item', 'swap_inventory_slots', 'hold_inventory_item')
            or name == 'inspect_inventory' and arguments.get('mode') == 'slots'):
        required_capability = ('server_ai_inventory_v1' if 'server_ai_atomic_v1' in bridge.hello.get('capabilities', []) and name in INVENTORY_OPERATIONS else 'inventory_operations_v1')
    if name == 'inspect_inventory' and 'server_ai_reads_v1' in bridge.hello.get('capabilities', []):
        required_capability = 'server_ai_reads_v1'
    if name in EQUIPMENT_OPERATIONS:
        required_capability = 'server_ai_equipment_v1'
    if required_capability and required_capability not in bridge.hello.get('capabilities', []):
        raise PreDispatchRejected(f'Requires {required_capability}; no action started')
    if name in ('mine_wall', 'place_wall', 'hammer_tile', 'interact_tile') and 'server_confirmed_interactions_v1' not in bridge.hello.get('capabilities', []):
        raise PreDispatchRejected('Requires server_confirmed_interactions_v1 (CompanionBridge 0.1.37+); no action started')
    if name in READS:
        reply = await command(bridge, 'query_atomic', operation=name, arguments=arguments)
        data = reply.get('data')
        if not isinstance(data, dict):
            raise BridgeError('Missing atomic query data')
        return {'valid': data.get('available') is True, **data}
    if name == 'stop_action':
        manager = getattr(bridge, '_program_tasks', None)
        if manager is not None and manager.blocked():
            stopped = await manager.stop_all()
            if stopped:
                return copy.deepcopy(stopped[-1]['result'])
        return await stop(bridge, lifecycle=lifecycle)
    start_task, action = None, None
    landing_invalid = False
    placement_invalid = False
    placement_identity = None
    try:
        initial = await command(bridge, 'get_movement')
        params = {'operation': name, 'arguments': arguments, 'expectedGeneration': initial['movement']['generation']}
        if chat_id is not None:
            params['chatId'] = chat_id
        track(start_attempted=True)
        start_task = asyncio.create_task(command(bridge, 'start_atomic', **params))
        reply = await asyncio.shield(start_task)
        action = reply['movement']['action']
        track(action=action)
        if action is None or reply['status'] != 'movement_started' or action.get('operation') != name:
            raise BridgeError('Missing atomic start receipt')
        landing_identity = None
        if name == 'navigate_to_landing_region':
            landing_invalid = True
            verify_landing_start(action, arguments, server_ai='server_ai_atomic_v1' in bridge.hello.get('capabilities', []))
            landing_invalid = False
            landing_identity = dict(action)
        if name in ('move_input', 'move_on_rope'):
            placement_invalid = True
            if action.get('requestedInputs') != arguments:
                raise BridgeError('Direct inputs missing or different in start receipt')
            placement_invalid = False
            placement_identity = dict(action)
        if name in INVENTORY_OPERATIONS | EQUIPMENT_OPERATIONS and 'server_ai_atomic_v1' in bridge.hello.get('capabilities', []):
            placement_invalid = True
            if action.get('targetInventory') != arguments:
                raise BridgeError('Inventory target missing or different in start receipt')
            placement_invalid = False
            placement_identity = dict(action)
        if name in CONTAINER_OPERATIONS:
            placement_invalid = True
            if action.get('targetContainer') != arguments:
                raise BridgeError('Container target missing or different in start receipt')
            placement_invalid = False
            placement_identity = dict(action)
        if name in ('mine_target', 'navigate_to'):
            placement_invalid = True
            if action.get('targetTask') != arguments:
                raise BridgeError('Task target missing or changed in start receipt')
            placement_invalid = False
            placement_identity = dict(action)
        if name == 'craft_recipe':
            placement_invalid = True
            if action.get('targetRecipe') != arguments:
                raise BridgeError('Craft target missing or different in start receipt')
            placement_invalid = False
            placement_identity = dict(action)
        if (name == 'place_tile' and 'server_ai_place_tile_v1' in bridge.hello.get('capabilities', [])
                or name == 'place_object' and 'server_ai_place_object_workbench_v1' in bridge.hello.get('capabilities', [])
                or name == 'place_rope'):
            placement_invalid = True
            if (action.get('targetTile') != arguments
                    or any(type(value) is not int for value in action['targetTile'].values())):
                raise BridgeError('Placement target missing or different in start receipt')
            placement_invalid = False
            placement_identity = dict(action)
        action_id = action['actionId']
        track(dispatchState='admitted')
        deadline = asyncio.get_running_loop().time() + action['durationMs'] / 1000 + 1
        while action['status'] == 'running':
            if asyncio.get_running_loop().time() >= deadline:
                stopped = await stop(bridge, action_id, action)
                if stopped.get('valid') is not True:
                    stopped = {**stopped, 'status': 'unknown', 'completed': False}
                return {**stopped, 'supervisorStopRequest': {'status': 'timeout', 'reason': 'supervisor_deadline'}}
            await asyncio.sleep(0.1)
            reply = await command(bridge, 'get_movement')
            current = reply['movement']['action']
            if landing_identity is not None and current is not None and not same_action(landing_identity, current):
                landing_invalid = True
                raise BridgeError('Landing action target/session/generation/world changed')
            if current is None or not same_action(action, current):
                track(uncertain=True)
                return {**result(action, None), 'valid': False, 'status': 'unknown', 'completed': False,
                        'dispatchState': 'unknown', 'reason': 'action_ownership_changed'}
            action = current
            track(action=action)
            if action['status'] == 'running':
                guards = {} if landing_identity is None else {'expectedGeneration': landing_identity['generation'],
                                                              'targetRegion': landing_identity['targetRegion']}
                try:
                    await command(bridge, 'renew_move', actionId=action_id, **guards)
                except BridgeError as error:
                    if error.status != 'action_changed':
                        raise
                    # The game may finish between our running snapshot and the
                    # renewal. Accept only the exact owned terminal action, then
                    # use the normal release-evidence verifier; never retry it.
                    reply = await command(bridge, 'get_movement')
                    current = reply['movement']['action']
                    if placement_identity is not None and not same_action(placement_identity, current):
                        placement_invalid = True
                    if landing_identity is not None and not same_action(landing_identity, current):
                        landing_invalid = True
                    if (not same_action(action, current)
                            or current.get('status') not in ('completed', 'failed', 'timeout', 'cancelled')):
                        raise
                    action = current
                    track(action=action)
        return await terminal(bridge, action, reply['movement']['generation'])
    except asyncio.CancelledError:
        try:
            if action is None and start_task is not None:
                reply = await start_task
                action = reply['movement']['action']
                track(action=action)
            if action is not None:
                track(cleanup=await stop(bridge, action['actionId'], action))
            elif start_task is not None:
                track(uncertain=True)
        except NotAdmittedError as error:
            track(uncertain=False, dispatchState='not_admitted', cleanup=not_admitted_result(name,str(error)))
        except (BridgeError, OSError, KeyError, ValueError) as error:
            track(uncertain=True, cleanupError=str(error))
        raise
    except NotAdmittedError as error:
        track(uncertain=False, dispatchState='not_admitted')
        return not_admitted_result(name,str(error))
    except (BridgeError, OSError, KeyError, ValueError) as error:
        if action is not None:
            try:
                stopped = await stop(bridge, action['actionId'], action)
                if landing_invalid or placement_invalid:
                    return {**stopped, 'valid': False, 'status': 'unknown', 'completed': False, 'reason': str(error)}
                return {**stopped, 'supervisorError': str(error)}
            except (BridgeError, OSError, KeyError, ValueError):
                pass
        track(uncertain=bool(start_task is not None))
        known = result(action,None) if isinstance(action,dict) and {'status','reason','actionId'} <= action.keys() else {'operation':name}
        return {**known, 'valid': False, 'status': 'unknown' if start_task is not None else 'failed' if getattr(error, 'status', None) else 'unknown',
                'completed': False, 'reason': str(error), 'dispatchState': 'unknown'}
