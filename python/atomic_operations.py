"""LLM atomic operations. One Bridge session, game-thread actions and release receipts."""
import asyncio
import copy
import math
import re
from time import perf_counter
from control import BridgeError


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
INVENTORY_TOKEN = {"type": "string", "minLength": 64, "maxLength": 64, "pattern": "^[0-9a-f]{64}$"}
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
TOOLS = [tool("query_map", "查询AI独立探索记忆，绝非实时地形。Tile坐标；overview自动适配世界<=128x48，方格比例，为战略地图；region默认角色中心、120x68 Tile、cellSize=2，支持1/2/4。返回data.rows、originX/Y、cellSize、exploredRatio、legend；?未知，空格已知空地，#实心，=平台，:混合/部分已知，~水/L岩浆/H蜂蜜/S微光表示存在。混合格需cellSize=1放大，不能据此判通路。记忆可能过时；地下视野采用遮挡/近身/可见原版火把近似。",
    {"mode":{"type":"string","enum":["overview","region"]},"centerX":{"type":"integer","minimum":0,"maximum":100000},"centerY":{"type":"integer","minimum":0,"maximum":100000},"width":{"type":"integer","minimum":1,"maximum":128},"height":{"type":"integer","minimum":1,"maximum":96},"cellSize":{"type":"integer","enum":[1,2,4]}},["mode"]),tool("query_recipes", "查询当前加载原生配方及AI背包/附近制作站/已支持环境条件。默认只返回可制作配方；resultType来自resolve_content；availableOnly=false可查缺失/不支持原因。检查分页、supported及hasOutputSpace。",
    {"resultType":{"type":"integer","minimum":1},"availableOnly":{"type":"boolean"},"limit":{"type":"integer","minimum":1,"maximum":24},"offset":{"type":"integer","minimum":0,"maximum":100000}}),
    tool("craft_recipe", "按新鲜query_recipes的recipeId/recipeToken原生合成，crafts是配方执行次数1..20，不是产物件数；每次重查材料/制作站/空间。可能部分完成，不回滚或失败后自动重试。",
    {"recipeId":{"type":"integer","minimum":0,"maximum":100000},"recipeToken":INVENTORY_TOKEN,"crafts":{"type":"integer","minimum":1,"maximum":20}},["recipeId","recipeToken","crafts"]),tool("observe_scene", "服务器AI局部45×29二维侧视语义图：几何/对象/危险来源、可直接用于导航的landingRegions.items[].target和相互独立的placementCandidates（单格place_tile）/objectPlacementCandidates（工作台place_object，含完整footprint/support）候选。geometry/map共用originX/originY，Lua用string.sub解析行。单次游戏线程采样；候选不保证可达/放置成功，树组件不是整树计数；检查coverage与各集合truncated。可选layers为非空、无重复的白名单数组，未传返回全部七层；data.includedLayers列出实际层，未请求字段省略。objects附带objectCounts，放置候选附带对应Material；身份、范围、角色、coverage和sample始终保留。objectPlacementCandidates.diagnostics区分已检查排除、unknownCount及notCheckedCount；matchedCount仍为通过筛选且截断前的数目。" + SCENE_TEXT_CONTRACT,
    {**{key:{"type":"integer","minimum":1,"maximum":64 if key=="maxHazards" else 24 if key=="maxRegions" else 32}
     for key in ("maxObjects","maxRegions","maxPlacements","maxHazards")},
     "layers":{"type":"array","items":{"type":"string","enum":SCENE_LAYERS},"minItems":1,"maxItems":7,"uniqueItems":True}}),tool("get_player_state", "读取真实玩家位置/速度/生命和地面支撑及运动模型支持状态。"),
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
    tool("get_world_state", "读取客户端昼夜、天气、事件、生物群系和深度。"),
    tool("move_input", "仅服务器AI：持续注入原生A/D、W/S及Space；horizontal/vertical各自互斥，none为松开。maxUpdates是1..600次游戏更新，不是距离；jumpHoldUpdates为最初0..maxUpdates次按住跳跃。无需落地、支撑或路径预测，可空中转向/走下边缘；Down按原生平台下穿语义，Down+Jump可能先跳起再在下降时下穿。完成只表示输入时长结束，撞墙/下落/受伤是正常结果。取消只释放输入，保留惯性；不自动装备/飞行/恢复。",
         {"horizontal":{"type":"string","enum":["none","left","right"]},
          "vertical":{"type":"string","enum":["none","up","down"]},
          "jumpHoldUpdates":{"type":"integer","minimum":0,"maximum":600},
          "maxUpdates":{"type":"integer","minimum":1,"maximum":600}},
         ["horizontal","vertical","jumpHoldUpdates","maxUpdates"]),
    tool("navigate_to_x", "复用连续导航至玩家左上角水平世界像素targetX；30秒上限，普通角色运动限制仍适用；arrived由游戏物理状态校验。失败时evidence.navigation.blockingTiles若存在，提供x/y/contentKey及basis，表示被拒绝的根节点前向步行试算中原生裁剪确认的完整实心接触，不证明全部路径被封死；缺失表示未可靠定位。",
         {"targetX": {"type": "number", "minimum": 0, "maximum": 1000000}}, ["targetX"]),
    tool("navigate_to_landing_region", "二维落脚导航：全部为世界像素，Y向下；minPlayerLeftX/maxPlayerLeftX是玩家碰撞框左边缘的闭区间，surfaceY是脚底目标水平表面高度（非玩家顶部）。原有普通角色限制；落点支持完整方块顶面和水平单向平台，不支持斜平台/半砖；30秒上限。仅实际进入X区间、脚底高度误差<=0.5px、安全着地且停稳后完成，不能只经过目标X。失败时evidence.navigation.blockingTiles若存在，提供x/y/contentKey及basis，仅表示被拒绝的根节点前向步行试算的已确认完整实心接触；缺失表示未可靠定位。",
         {key: {"type": "number", "minimum": 0, "maximum": 1000000}
          for key in ("minPlayerLeftX", "maxPlayerLeftX", "surfaceY")},
         ["minPlayerLeftX", "maxPlayerLeftX", "surfaceY"]),
    tool("drop_through_platform", "按原生Down直接穿过当前水平单向平台，不绕到边缘。须已停稳，自动检查同X下方64像素内的第一个安全落点；穿过当前层即松Down，实际落在下一层并停稳才完成，3秒上限。"),
    tool("select_hotbar_slot", "按游戏显示的快捷键选择：1..9为前九格，0为最右第十格。slot不是背包数组索引。核对实际选中按键。", {"slot": {"type": "integer", "minimum": 0, "maximum": 9}}, ["slot"]),
    tool("use_item", "用当前物品完成一次原生使用周期；target可选世界像素{x,y}。completed只证明使用周期，具体效果需感知核验。无target保留原生鼠标，不限制画面缩放；有target支持当前缩放，仍限正常重力及80像素。",
         {"target": {"type": "object", "properties": {"x": {"type": "number", "minimum": 0, "maximum": 1000000}, "y": {"type": "number", "minimum": 0, "maximum": 1000000}}, "required": ["x", "y"], "additionalProperties": False}}),
    tool("mine_target", "服务器复合任务：优先直接传 observe_scene 的矿物对象 target（{target=ore.target}），无需从地图计算或重建坐标；也可显式{x,y}绝对Tile位置，二者互斥。游戏侧核对观察World/Actor/Generation、30秒/600更新时效及目标未改变，选快捷栏纯镐，按原生挖掘交互范围寻找安全站位，导航后重验并挖指定一格。仅已观察普通原版矿物/局部导航窗口，无自动重试；unknown停止。回执evidence.task含taskId/taskName/phase/status/currentChild/progress/lastResult/reason及子动作效果/释放证据。完成不证明掉落入包。", {"target": MINE_TARGET, **TILE}),
    tool("mine_tile", "以选中镐原生挖掘80像素内Tile，支持当前缩放，仍限正常重力；移除才成功，10秒上限。Server AI 另允许纯镐清理 SmallPiles（Tile 185）环境碎堆；其他非实心装饰并不因此获准。放置前用 inspect_tile 核对占地，显式清理成功后重新观察再 place_object；不会自动清理。调用前inspect_inventory确认所选为镐，必要时先select_hotbar_slot。不在该原子操作内自动选工具/移动。", TILE, ["x", "y"]),
    tool("attack_entity", "以当前物品攻击scan_entities给出的NPC entityRef；80像素内，生命下降才成功，失活但无伤害证据失败，其他伤害来源可能存在。10秒上限。",
         {"entityRef": {"type": "string", "minLength": 1, "maxLength": 150}}, ["entityRef"]),
    tool("mine_wall", "选中纯锤原生挖背景墙；80像素内，前景必须为空；实际墙消失才成功。多人需同版本服务器确认；10秒上限，可取消。", TILE, ['x', 'y']),
    tool("place_wall", "选中原版消耗型墙物品，在80像素内无墙格原生放置；正确墙类型和消耗一件共同核验，多人需服务器确认；自动补邻墙或自动涂漆不支持，10秒上限。", TILE, ['x', 'y']),
    tool("hammer_tile", "选中纯锤对80像素内原版普通方块/平台原生锤击；目标无背景墙，实际形状变化才成功，多人需服务器确认。不支持家具/破坏型锤目标。10秒上限。", TILE, ['x', 'y']),
    tool("interact_tile", "80像素内原版门、未锁宝箱、开关、拉杆基础交互；多格任一占用格可指定；宝箱核验打开/关闭，开关及拉杆核验Frame切换，不证明下游电路效果。多人需同版本服务器确认。", TILE, ["x", "y"]),
    tool("place_object", "仅服务器AI：选中快捷栏原版Work Bench默认样式，x/y为TileObjectData目标锚点（Tile坐标，左上角）；完整2×1干燥占地为空或原生可清除植物/装饰物，下方双格实心支撑、排除玩家、锚点80px内。非实心不等于可覆盖；沿用原生占地判断及清除，不额外挖掘。真实原生放置事件、全占地帧及消耗和输入释放共同核验；候选不保证原生成功，unknown不得重试。10秒上限。", TILE, ["x", "y"]),
    tool("place_tile", "用选中消耗品在80像素内空格放单格方块，支持当前画面缩放，仍限正常重力；Tile类型与物品消耗共同核验；不支持多格家具或ModItem。10秒上限。", TILE, ["x", "y"]),
    tool("stop_action", "取消当前Agent动作并等待游戏线程输入释放证据，不抢占手动控制。")]
SCHEMAS = {t['function']['name']: t['function']['parameters'] for t in TOOLS}

# Model-only server projection. Execution validation and the shared TOOLS stay unchanged.
SERVER_DESCRIPTIONS = {
    'transfer_inventory_item': '服务器AI：仅0..49普通库存槽，新鲜两槽token；原生拆分/堆叠，CanStack检查Mod兼容性，容量不足不执行；核验数量及后续游戏更新。unknown可能已变更，不回滚、不自动重试。',
    'swap_inventory_slots': '服务器AI：新鲜两槽token，交换0..49完整物品实例；不合并，后续游戏更新核验；unknown不回滚、不自动重试。',
    'hold_inventory_item': '服务器AI：完整交换sourceSlot与hotbarKey(游戏键1..9,0)，目标物品回源槽，再选中；同槽只选择，不自动使用；核验两槽与后续更新。unknown不回滚、不自动重试。',
    'get_player_state': '读取绑定服务器AI的实际位置/速度/生命及motion；无参数。返回{valid,player,motion,binding,...}，player.position是碰撞框左上角世界像素，velocity为像素/游戏更新，Y向下；motion.grounded与supported独立，supported只是导航profile适用性，不保证支撑/路径安全。',
    'inspect_inventory': '只读AI真实背包，必须显式mode；局部查询不代表完整背包。返回{valid,available,data,...}，data.inventoryScope/inspectedSlots标明实际检查范围，item匹配slots不等于搜索范围。hotbarKey是游戏键1..9,0；新版本server_ai_inventory_v1提供绑定World/Actor/Generation/槽位、物品实例与基础字段的inventoryToken；操作前用slots/selected/hotbar读取两槽新鲜token。full/item不保证提供token，全部Mod自定义状态未知；token不调用保存钩子，不能识别仅自定义状态变化。',
    'observe_terrain': '读取AI周围45×29碰撞格及同次状态；无参数。返回{valid,available,data,...}，data.terrain含originX/originY/rows，data.state含centerWorldPixels，另含bounds/legend/coverage。原点/格为Tile（16像素），不是摄像机或材质图；取具体Tile用已开放的精查接口。supported只是profile适用性。',
    'use_item': '当前选中快捷栏物品完成一次原生使用周期；参数仅{}，先观察真实持用物品，必要时显式选栏。仅木剑、铜镐、铜斧、苹果，无target瞄准、实体锁定或其他物品适配；3秒上限。completed和cycleVerified只证明使用周期，effectStatus=unconfirmed，不证明命中或击杀。',
    'mine_target': '仅server_ai_tasks_v1；直接使用矿物对象的target字段，不重构坐标；明确玩家指定位置才用x/y。30秒或600游戏更新内的观察可提交；任务只随真实更新推进，截止不因子任务冻结。当前仅选快捷栏纯镐、一次导航和一次挖掘，不自动恢复、不挖遮挡或整片矿脉；按evidence.task.lastResult/children验证。',
    'mine_tile': '当前真实镐挖普通方块或SmallPiles（Tile185），或铜斧砍原生斧类树干。x/y为新鲜观察或玩家指定的实际Tile格；先检查工具，80像素保守距离叠加原生射程/工具力/保护规则。10秒上限，destroyedByActor且targetAbsent和绑定结束证据才完成；树木连带变化/掉落不证明入包。其他装饰不因此支持；不自动选工具、走近、挖邻格或重试。',
    'place_tile': '在空Tile格x/y，以当前选中原版泥土块、石块、木材或普通火把进行一次原生放置。检查物品、占地和原生邻接；80像素保守检查加原生条件，10秒上限。仅本AI放置目标、消耗1件、计时结束及释放共同确认completed。不支持家具/平台/沙/ModItem/替换格/墙；不自动选材或走近，取消可能已有方块/消耗。',
    'scan_entities': '以AI中心扫描世界像素半径radius（1..1600）内真实活跃NPC/掉落/其他玩家，排除AI自身；category与可选typeIds/npcFilter按Schema，先筛选再按距离排序截断。返回data.items及范围/计数/截断；entityRef绑定世界/AI代次/NPC实例，仅是观察标识，不保证视线、可达或可攻击。',
}
SERVER_DESCRIPTION_SUFFIXES = {
    'query_recipes': '返回data.items中的output.count、supported/available、材料/站点/条件、hasOutputSpace和分页。仅普通原版配方/配方组、AI背包0..57、原生邻接站点、水/熔岩/蜂蜜/微光/昼夜六种条件；特殊回调/其他条件/Mod内容/箱子或虚空仓库不支持。resultType需从当前已知注册表取得，空页不证明全局无配方。',
    'craft_recipe': '仅普通原版配方/配方组、AI背包0..57与已支持制作站/六类环境条件；特殊回调、其他条件、Mod内容、箱子/虚空仓库不支持。前置：新鲜配方token及supported/available、材料/站点/条件/保守空间检查；单次产量见output.count，每批重验。按completedCrafts/producedCount、batches、inventoryVerified核验；可能部分完成，不回滚。制作站是否邻接要重新查询，不能由放置动画推定。',
    'move_input': '前置为绑定身份/控制权、无互斥动作、正常重力；坐骑/失能状态拒绝，空中/非零起速允许。30秒墙钟看门，不要求导航profile/安全支撑。evidence包含updatesExecuted、startPosition/endPosition/endVelocity（向量X/Y）、healthDelta、grounded/airborne、inputReleased、settled、noProgress；completed只证明输入时长结束，inputReleased不证明停稳或效果确定，观察/同步不确定时仍返回unknown。',
    'navigate_to_x': '先读取当前位置再计算相对targetX；起步/路径仍需普通导航profile，飞行/坐骑/反重力等不支持。结束evidence含x/y/vx/vy、arrived/grounded/safeSupport/settled；不能把经过X、零预测误差或局部搜索失败当成已落地/全局不可达。',
    'navigate_to_landing_region': '目标须来自新鲜观察，可将已观察候选target直接传入；不同高度仍受原局部预算、普通profile及64px下降限制。半砖/斜坡/斜平台不是目标落脚面；斜坡可作路径。导航不执行Down平台下穿。成功须targetRegion一致、arrived/grounded/safeSupport/settled=true及known释放；未确认落地保持unknown。',
    'scan_tiles': '返回data.items及matchedCount/returnedCount/complete/truncated；按Tile格计数，一棵树有多格，不能当树的棵数。附近查询先取得观察bounds；世界像素转Tile用math.floor(px/16)，不猜内容ID。',
    'inspect_tile': '返回data内的实际格和preflight；x/y为Tile坐标。只读前提不是原生执行许可；移除支撑比较不预测实际落点。',
    'resolve_content': '返回data中的匹配候选/ID及歧义；内容注册表中存在不等于当前观察范围或世界内存在。',
    'observe_scene': 'data.map/geometry为45×29行字符串，共用Tile原点originX/Y；tileX=originX+列索引-1，tileY=originY+行索引-1。按任务选layers，未请求字段直接省略，不表示空；旧响应缺includedLayers不证明支持筛选。对应placementMaterial/objectPlacementMaterial需核对真实材料。导航候选candidate.target、单格和工作台候选用途独立；单格placementCandidates不能传给place_object，工作台用objectPlacementCandidates.items。工作台diagnostics中rejectedCount仅计已知占地/支撑的首个互斥拒绝，unknownCount/notCheckedCount不算明确排除；coveredScopeComplete仅指完整占地/支撑在观察范围内的锚点全部已知，缺diagnostics不表示计数为0。集合各自检查coverage/complete/truncated；移动后重观察。',
    'place_object': '须检查真实快捷栏材料及完整占地；场景工作台候选来自objectPlacementCandidates，不能用单格候选替代。completed须placedByActor/targetPresent、targetChanged=false、真实消耗1件及known释放；取消可能已有副作用，放成后重查制作站邻接。',
    'select_hotbar_slot': '只改变AI当前快捷栏选择；输入slot是游戏显示键，不是inventory.slot内部索引。completed需下一游戏线程状态及绑定选择结束证据，不等于使用物品。',
    'query_map': '返回data.rows/originX/Y/cellSize/exploredRatio等；持久地图是AI探索记忆，不是脚本变量存储，不替代当前观察或保证可达。',
    'wait': '返回{valid,status,elapsedMilliseconds,clock}；占API次数但不占变更动作次数。',
    'stop_action': '只取消当前拥有的AI动作；已释放输入仍可status=unknown，读取status与结束证据分别判断，不把释放当成功。',
}


def server_tools(supported):
    """Reuse shared schemas; narrow server use_item and attach backend-specific semantics."""
    definitions = [copy.deepcopy(t) for t in TOOLS if t['function']['name'] in supported]
    for definition in definitions:
        f = definition['function']
        name = f['name']
        if name == 'use_item':
            f['parameters'] = {'type': 'object', 'properties': {}, 'required': [], 'additionalProperties': False}
        f['description'] = SERVER_DESCRIPTIONS.get(name, f['description'])
        if name in SERVER_DESCRIPTION_SUFFIXES:
            f['description'] += SERVER_DESCRIPTION_SUFFIXES[name]
        if name in ('navigate_to_x', 'navigate_to_landing_region'):
            f['description'] += '已确认可恢复失败仍返回failed及实际位置/速度/支撑/释放证据；只有宿主continuationAllowed=true且continuationScope=observe_scene_then_move_input时，才可重新observe_scene后尝试一次有界move_input，不能按reason文字自行放行或重试导航。'
    return definitions

READS = {'get_player_state', 'observe_terrain', 'inspect_tile', 'scan_tiles', 'scan_entities', 'inspect_inventory', 'get_world_state'}
READS |= {'query_map', 'query_recipes', 'observe_scene', 'get_player_status', 'resolve_content', 'observe_exploration'}
UTILITIES = {'wait'}
ACTIONS = set(SCHEMAS) - READS - UTILITIES
BUDGETED_ACTIONS = ACTIONS - {'stop_action'}


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
    if name == 'mine_target' and not (set(arguments) == {'target'} or set(arguments) == {'x', 'y'}):
        raise ValueError('mine_target requires either target or absolute x/y')
    if name == 'query_map':
        if arguments.get('cellSize', 2) not in (1, 2, 4):
            raise ValueError('query_map.cellSize must be 1, 2 or 4')
        if arguments['mode'] == 'overview' and set(arguments) != {'mode'}:
            raise ValueError('query_map overview accepts only mode')
    if name == 'scan_tiles':
        width, height = arguments['maxX'] - arguments['minX'] + 1, arguments['maxY'] - arguments['minY'] + 1
        if width <= 0 or height <= 0 or width * height > 4096:
            raise ValueError(f'scan_tiles.bounds: inclusive area {width} x {height} = {width * height}; require ordered bounds and area <=4096 tiles. Use observe_terrain bounds (45 x 29) or split into bounded scans')
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
    return {'valid': True, 'status': action['status'], 'reason': action['reason'], 'reasonDetail': action.get('reasonDetail'),
            'operation': action.get('operation'), 'actionId': action['actionId'],
            'session': action.get('session'), 'actionGeneration': action.get('generation'), 'targetRegion': action.get('targetRegion'), 'targetTile': action.get('targetTile'), 'targetRecipe': action.get('targetRecipe'), 'targetInventory': action.get('targetInventory'), 'targetTask': action.get('targetTask'),
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


INVENTORY_OPERATIONS = frozenset(('transfer_inventory_item', 'swap_inventory_slots', 'hold_inventory_item'))


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
        ('actionId', 'session', 'generation', 'worldSessionId', 'operation', 'targetRegion', 'targetTile', 'targetRecipe', 'requestedInputs', 'targetInventory', 'targetTask', 'actorId', 'actorSlot'))


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
    native_verified = {
        'move_input': (native_evidence.get('requestedInputs') == action.get('requestedInputs')
                       and isinstance(action.get('requestedInputs'), dict)
                       and type(native_evidence.get('updatesExecuted')) is int
                       and native_evidence['updatesExecuted'] == action['requestedInputs'].get('maxUpdates')
                       and native_evidence.get('updatesRequested') == native_evidence['updatesExecuted']
                       and native_evidence.get('inputReleased') is True),
        'navigate_to_x': native_evidence.get('arrived') is True,
        'navigate_to_landing_region': verified_server_landing(action, native_evidence),
        'use_item': native_evidence.get('cycleVerified') is True,
        'craft_recipe': verified_server_craft(action, native_evidence),
        'mine_target': verified_server_mine_task(action, native_evidence),
        **{op: verified_server_inventory(action, native_evidence) for op in INVENTORY_OPERATIONS},
        'place_tile': (isinstance(action.get('targetTile'), dict)
                       and set(action['targetTile']) == {'x', 'y'}
                       and all(type(action['targetTile'][key]) is int and type(native_evidence.get(key)) is int for key in ('x', 'y'))
                       and native_evidence.get('x') == action['targetTile'].get('x')
                       and native_evidence.get('y') == action['targetTile'].get('y')
                       and native_evidence.get('placedByActor') is True
                       and native_evidence.get('targetPresent') is True
                       and native_evidence.get('targetChanged') is False
                       and type(native_evidence.get('consumedCount')) is int and native_evidence['consumedCount'] == 1),
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
                  and action.get('operation') in ('move_input', 'navigate_to_x', 'navigate_to_landing_region', 'use_item', 'mine_tile', 'place_tile', 'place_object', 'craft_recipe', 'mine_target', *INVENTORY_OPERATIONS)
                  and end.get('phase') == 'ServerAI_game_thread_inputs_released'
                  and end.get('operation') == action.get('operation')
                  and end.get('inputsReleased') is True
                  and type(end.get('inputsAcquired')) is bool
                  and isinstance(action.get('actorId'), str) and bool(action['actorId'])
                  and end.get('actorId') == action['actorId']
                  and end.get('actorGeneration') == action.get('generation')
                  and end.get('actorSlot') == action.get('actorSlot')
                  and (action.get('status') != 'completed'
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
    if name == 'mine_target' and 'server_ai_tasks_v1' not in bridge.hello.get('capabilities', []):
        raise PreDispatchRejected('Requires server_ai_tasks_v1; no task submitted')
    if name in {'query_recipes','craft_recipe'} and 'server_ai_crafting_v1' not in bridge.hello.get('capabilities', []):
        raise PreDispatchRejected('Requires server_ai_crafting_v1; no crafting RPC started')
    if name == 'place_object' and 'server_ai_place_object_workbench_v1' not in bridge.hello.get('capabilities', []):
        raise PreDispatchRejected('Requires server_ai_place_object_workbench_v1; no object placement RPC started')
    if name == 'observe_scene' and 'server_ai_scene_v1' not in bridge.hello.get('capabilities', []):
        raise PreDispatchRejected('Requires server_ai_scene_v1; no scene query started')
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
    required_capability = {'move_input': 'server_ai_direct_input_v1', 'select_hotbar_slot': 'hotbar_keys_v1', 'drop_through_platform': 'platform_drop_v1'}.get(name)
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
        if name == 'move_input':
            placement_invalid = True
            if action.get('requestedInputs') != arguments:
                raise BridgeError('Direct inputs missing or different in start receipt')
            placement_invalid = False
            placement_identity = dict(action)
        if name in INVENTORY_OPERATIONS and 'server_ai_atomic_v1' in bridge.hello.get('capabilities', []):
            placement_invalid = True
            if action.get('targetInventory') != arguments:
                raise BridgeError('Inventory target missing or different in start receipt')
            placement_invalid = False
            placement_identity = dict(action)
        if name == 'mine_target':
            placement_invalid = True
            if action.get('targetTask') != arguments:
                raise BridgeError('Mine task target missing or changed in start receipt')
            placement_invalid = False
            placement_identity = dict(action)
        if name == 'craft_recipe':
            placement_invalid = True
            if action.get('targetRecipe') != arguments:
                raise BridgeError('Craft target missing or different in start receipt')
            placement_invalid = False
            placement_identity = dict(action)
        if (name == 'place_tile' and 'server_ai_place_tile_v1' in bridge.hello.get('capabilities', [])
                or name == 'place_object' and 'server_ai_place_object_workbench_v1' in bridge.hello.get('capabilities', [])):
            placement_invalid = True
            if (action.get('targetTile') != arguments
                    or any(type(value) is not int for value in action['targetTile'].values())):
                raise BridgeError('Placement target missing or different in start receipt')
            placement_invalid = False
            placement_identity = dict(action)
        action_id = action['actionId']
        deadline = asyncio.get_running_loop().time() + action['durationMs'] / 1000 + 1
        while action['status'] == 'running':
            if asyncio.get_running_loop().time() >= deadline:
                stopped = await stop(bridge, action_id, action)
                return {**stopped, 'status': 'unknown' if stopped.get('status') == 'unknown'
                        else 'timeout' if stopped['valid'] else 'unknown', 'completed': False,
                        'cleanupReason': stopped['reason'], 'reason': 'supervisor_deadline'}
            await asyncio.sleep(0.1)
            reply = await command(bridge, 'get_movement')
            current = reply['movement']['action']
            if landing_identity is not None and current is not None and not same_action(landing_identity, current):
                landing_invalid = True
                raise BridgeError('Landing action target/session/generation/world changed')
            if current is None or not same_action(action, current):
                track(uncertain=True)
                return {'valid': False, 'status': 'unknown', 'reason': 'action_ownership_changed'}
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
        except (BridgeError, OSError, KeyError, ValueError) as error:
            track(uncertain=True, cleanupError=str(error))
        raise
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
        return {'valid': False, 'status': 'failed' if getattr(error, 'status', None) else 'unknown', 'completed': False, 'reason': str(error)}
