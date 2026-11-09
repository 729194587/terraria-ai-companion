---
name: mining_and_ores
description: 寻找与辨认矿脉、选择安全站位、挖指定矿格、核验矿石入包及报告未完成原因。
game_version: Terraria 1.4.4.9 / tModLoader 2026.09.3.1（目标引擎版本）
scope: 独立 Server AI Player 的原版矿物；Mod 内容仅按当前解析与握手能力处理。
---
# 挖矿与矿石

适用于找矿、挖指定矿格、采集指定矿种或数量。先从玩家请求区分只读找矿、改变地形和收集到背包。只读请求到观察和报告为止。玩家委托采集时，可自行选择符合条件的候选；具体指定了矿种、格子或保留地形要求时，按原目标执行。

## 识别目标

- 优先用当前契约开放的 `observe_scene({layers={'geometry','objects','hazards'}})` 读取局部场景，按 `kind='ore_component'` 和 `contentKey` 选矿。一个分组可包含多个同类相邻格；其 `target` 对应单个矿格。不要修改令牌、用区域图拼装令牌或把同一令牌用于下一格。移动、挖掘或目标过期后重观。
- 缺少场景能力时，可在辅助函数依赖齐备时用 `sense_world({trees=false,ores=true,oreNames={'Copper','Iron'},drops=false,maxObjects=4})`。也可用 `resolve_content` 精确解析 **tile** 内容名，以 `observe_terrain` 的局部 Tile 范围进行有界 `scan_tiles`，对候选用 `inspect_tile` 确认 `knowledge='read'`、`hasTile`、类型及前提。注册表命中、历史地图标记和场景图符都不足以确认当前目标格。坐标为绝对 Tile，1 格 16 像素。
- 铜/锡、铁/铅、银/钨、金/铂是常见生成替代组；具体世界可能出现其他来源或特殊种子。用户明确要铁时不要把铅计入。地下和洞穴通常更容易找到矿脉，低阶矿也可能出现在近地表。局部空结果只说明本次覆盖和筛选下无返回目标。有安全依据、授权和剩余预算时可以做一段局部探索后重扫。
- 核对 AI 的镐力。可按需读 `inspect_inventory({mode='full'})` 中主背包工具的 `use.pick`。普通低阶矿可用入门镐，地下魔矿/猩红矿通常需要 55%，狱石需要 65%。钴/钯、秘银/山铜、精金/钛金和叶绿矿依次需要 100%、110%、150%、200%。这些是原版筛选线索，实际破坏许可由当前引擎判定。工具不足时报告缺口，勿反复挥镐。
- Tile 与掉落 Item 的内容键独立，例如 `Terraria/Copper` 和 `Terraria/CopperOre`。查背包前确认准确 Item 内容键。

## 选择站位并执行

1. 从当前观察选择同类、较近、可在安全站位触及的暴露矿格。检查几何、液体、接触危险、敌怪和掉落风险，保留支撑和退路。跳过未知格、岩浆邻接、可能下落的脚下支撑或不能确认落点的候选。地狱的狱石被挖掉会生成岩浆，现场干燥也要评估挖掘后的流向和退路。`inspect_tile` 如提供 `miningSupportImpact`，检查可用性和移除后支撑；它不能预测实际下落终点。安全信息不足时先补观察。
2. 已处于安全可触及站位时用 `api('mine_tile',{x=x,y=y})`。需要接近且当前契约开放 `mine_target` 时，优先 `api('mine_target',{target=object.target})`，由游戏线程重验目标、站位和原生效果。只有明确 Tile 目标且经新鲜观察重验时才用坐标形式。两项会自动从 AI 主背包选合适工具，无需预选快捷栏。带开路能力的 `mine_target` 可能先挖阻挡格；若玩家限定只改目标格，先走已有安全路线，站稳后用 `mine_tile`。同样留意导航能力的地形改造契约。
3. 每次动作后检查宿主回执和新观察，再决定是否有条件挖下一格。用有限循环、明确矿种和数量上限，为回执与复查留预算。只读候选预检可以排除不适合的格；动作失败、超时、取消、未知或危险中断时按宿主终止规则收手，不重放动作、不换入口规避。

## 核验与停止

- 目标格由本次原生挖掘移除且输入释放，以宿主动作 `status`、已知 `endEvidence` 和效果字段为准。脚本正常返回、目标看起来空了或矿物掉落出现，各自不能确认原生挖掘完成。
- 若目标是收集矿石，行动前后用 `inspect_inventory({mode='item',item='Terraria/CopperOre'})` 等精确查询。只有 `valid`、`available`、`data.status='ok'` 且同一角色/世界、类型一致、后者采样更新时，才比较 `data.count`。零增量时报告入包未确认；掉落消失不足以证明拾取。若有竞争或其他来源，只报告观察到的同类库存净增。只读查询未确认或缺失时保留未知。
- 按玩家语言简短报告已核验的矿格/净增数量及停止原因。达到请求数量就停。部分格已挖、预算耗尽、路径不安全或附近未返回匹配矿格时报告实际进度和覆盖范围。

## Lua 示例

**采一格附近暴露的铜矿。** 将下面整段直接作为 `execute_lua.source`。适用于玩家已委托采集，且当前契约开放 `observe_scene`、`inspect_tile`、`inspect_inventory` 和 `mine_target` 的情况。若后端允许任务开路，委托也须允许该行为。为五次 API 调用和一次动作预留预算。

示例只考虑与身体同高、水平相隔至多四格、横向无遮挡的矿格，并用 80 像素距离作保守筛选。局部几何不完整、观察到危险、角色未停稳或移除矿格可能影响当前支撑时停止。快照检查只筛除明显风险，原生任务仍需重验目标、可挖性和安全站位。此例不处理远处矿脉或跨高度接近。

```lua
local function readable(r)
    return r.valid and r.available == true and type(r.data) == 'table'
end

local function same_actor(a, b)
    return a.worldSessionId == b.worldSessionId
        and a.actorGeneration == b.actorGeneration and a.actorSlot == b.actorSlot
end

local function clear_hazards(list)
    return type(list) == 'table' and list.truncated == false and list.matchedCount == 0
end

local scene = api('observe_scene', {layers={'geometry','objects','hazards'}})
if not readable(scene) then return {status='scene_unavailable'} end
local data = scene.data
local actor, geometry, hazards = data.actor, data.geometry, data.hazards
local objects = data.objects
if not data.coverage or data.coverage.geometryComplete ~= true
    or not geometry or not geometry.rows or not objects or not objects.items then
    return {status='scene_incomplete'}
end
if not hazards or not clear_hazards(hazards.tiles) or not clear_hazards(hazards.entities) then
    return {status='hazards_present_or_unknown'}
end
if not actor or actor.dead ~= false or actor.navigationProfileSupported ~= true
    or not actor.velocity or actor.velocity.x ~= 0 or actor.velocity.y ~= 0
    or not actor.bodyTileBounds or not actor.boundsWorldPixels then
    return {status='stance_unconfirmed'}
end

local body, box = actor.bodyTileBounds, actor.boundsWorldPixels
local function exposed_from_side(target)
    local x, y = target.x, target.y
    if y < body.minY or y > body.maxY then return false end
    local edge = x < body.minX and body.minX or body.maxX
    local gap = math.abs(x - edge)
    if (x >= body.minX and x <= body.maxX) or gap > 4 then return false end
    local row = geometry.rows[y - geometry.originY + 1]
    if not row then return false end
    local step = x < edge and -1 or 1
    local targetColumn = x - geometry.originX + 1
    if targetColumn < 1 or targetColumn > string.len(row)
        or string.sub(row, targetColumn, targetColumn) ~= '#' then return false end
    for tileX = edge, x - step, step do
        local column = tileX - geometry.originX + 1
        if column < 1 or string.sub(row, column, column) ~= '.' then return false end
    end
    return true
end
local target, bestDistance
for _, object in ipairs(objects.items) do
    local candidate = object.target
    if object.kind == 'ore_component' and object.contentKey == 'Terraria/Copper'
        and type(candidate) == 'table' and same_actor(candidate, scene)
        and exposed_from_side(candidate) then
        local dx = candidate.x * 16 + 8 - (box.x + box.width / 2)
        local dy = candidate.y * 16 + 8 - (box.y + box.height / 2)
        local distance = dx * dx + dy * dy
        if distance <= 80 * 80 and (not bestDistance or distance < bestDistance) then
            target, bestDistance = candidate, distance
        end
    end
end
if not target then
    return {status='no_safe_returned_target', bounds=data.bounds, objectsTruncated=objects.truncated}
end

local tile = api('inspect_tile', {x=target.x, y=target.y})
if not readable(tile) or not same_actor(tile, scene) then return {status='tile_unavailable'} end
local cell, impact = tile.data.cell, tile.data.miningSupportImpact
if not cell or cell.knowledge ~= 'read' or cell.hasTile ~= true or cell.tileType ~= target.typeId
    or not tile.data.preflight or tile.data.preflight.withinConservativeReach ~= true then
    return {status='target_changed_or_out_of_range'}
end
if not impact or impact.available ~= true or impact.affectsCurrentStance ~= false
    or not impact.pose or impact.pose.grounded ~= true or impact.pose.vx ~= 0 or impact.pose.vy ~= 0
    or impact.pose.px ~= box.x or impact.pose.py ~= box.y
    or not impact.afterRemoval or impact.afterRemoval.rejection ~= json.null then
    return {status='support_unconfirmed'}
end

local function read_count()
    return api('inspect_inventory', {mode='item', item='Terraria/CopperOre'})
end
local function has_count(r)
    return readable(r) and same_actor(r, scene) and r.data.status == 'ok'
        and type(r.data.count) == 'number' and type(r.data.typeId) == 'number'
        and type(r.gameUpdate) == 'number'
end
local before = read_count()
if not has_count(before) then return {status='inventory_baseline_unavailable'} end

local action = api('mine_target', {target=target})
local ended = action.endEvidence
if not action.valid or action.status ~= 'completed' or action.endEvidenceStatus ~= 'known'
    or type(ended) ~= 'table' or not same_actor(ended, scene)
    or ended.inputsReleased ~= true or ended.effectVerified ~= true then
    return {status='mining_unconfirmed', action=action}
end
local after = read_count()
if not has_count(after) or after.data.typeId ~= before.data.typeId
    or after.gameUpdate <= before.gameUpdate or after.gameUpdate < ended.gameUpdate then
    return {status='pickup_unconfirmed', action=action}
end
local net = after.data.count - before.data.count
return {status=net > 0 and 'inventory_increase_observed' or 'pickup_unconfirmed',
        inventoryNetChange=net, action=action}
```

检查宿主 `lastAction` 和动作回执再报告矿格是否移除。宿主可在失败或结果未知时直接终止程序，届时不会有脚本返回值。矿格已移除而库存未确认时，报告这两项各自的结果。

## 参考

- Numen `1.21.1` 的按需 Markdown 攻略和 Lua 原子动作组合。[Numen API](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/numen-api/README_EN.md)
- 本地 TerraBot 快照的 `BotWorldActions.TryTick` 将最近矿格、接近、选镐和原生挖掘输入分开。本项目通过当前 Server AI 契约完成这些步骤。[设计参考与版本](../../docs/TERRARIA_AGENT_GAMEPLAY_MVP_DESIGN_v0.1.md)
- 原版生成、镐力和狱石危险资料。[官方 Terraria Wiki · Ores](https://terraria.wiki.gg/wiki/Ores) · [Pickaxe power](https://terraria.wiki.gg/wiki/Pickaxe_power) · [Hellstone](https://terraria.wiki.gg/wiki/Hellstone)
