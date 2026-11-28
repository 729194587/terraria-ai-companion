# Server AI 合成

Server AI 提供只读配方查询 `query_recipes` 和原生合成 `craft_recipe`。Agent 决定目标产物、配方、次数及前置采集/移动顺序；执行器只执行指定配方，不自动采集、移动、放置制作站、切换材料或重试。

## 能力与查询

需要服务端能力握手 `server_ai_crafting_v1`。旧服务器或客户端控制后端不应假设开放该接口。

`query_recipes` 从当前加载的 `Main.recipe` 查询，不使用硬编码配方表：

| 参数 | 含义 |
| --- | --- |
| `resultType` | 可选产物 Item 类型 ID；先用 `resolve_content` 解析 |
| `ingredientType` | 可选材料 Item 类型 ID，要求 `server_ai_recipe_uses_v1`。匹配直接用料和配方组接受的替代材料，可与 `resultType` 一起使用 |
| `availableOnly` | 指定材料时默认 false，列出所有匹配的已启用配方。其他查询默认 true，只列当前支持且满足材料、制作站和环境条件的配方 |
| `limit` / `offset` | 分页，默认 12/0，每页最多 24 |

每条配方包含运行时 `recipeId`、绑定定义的 `recipeToken`、产物数量、材料/原生配方组、制作站和环境条件，以及 `supported`、`available`、`reason`。不支持条件以未知/不支持表达，不会通过读取真人玩家状态猜测。空页不证明整个注册表没有该配方。

例如，捡到凝胶后可以查它能做什么。即使背包里没有木材，结果里也能看到普通火把及其用料。

```lua
local material = api("resolve_content", {kind="item", query="Terraria/Gel"})
return api("query_recipes", {ingredientType=material.data.candidates[1].typeId, limit=8})
```

读取 `data.items` 获取当前页。若 `data.truncated` 为 true，下一页传 `offset=data.offset+data.returnedCount`。同一产物的不同配方分别列出。`ingredients[].acceptedGroups` 说明每种材料可由哪些物品替代。

## 递归材料规划

Server AI 握手包含 `server_ai_material_plan_v1` 时，调用 `terraria.material_plan.plan(typeId, count)` 或 `api("plan_materials", {typeId=id, count=n})`。两种写法都只发一次只读请求。数量表示背包内最终希望持有的总数。委托要求额外新制 N 件时，以当前背包槽 0–57 的目标物总量加 N 做规划。

- `steps` 是展开后的合成树，按先材料后产物排列。每步有物品键、选中配方 ID、该产物的候选配方 ID、批次数、单批产量、实际选用的输入材料和数量、制作站及条件。
- `inventoryUsed` 汇总本次分配的背包物品（槽位 0–57）。`deficits` 汇总还需从合成链外取得的物品。没有启用配方记作 `no_recipe`；仅靠当前循环不能取得的起始材料记作 `cycle`。目标数量已经够时，步骤和缺口都为空。
- 每个候选配方使用独立的库存副本，选中路线的库存抵扣和整批多产的余量供后续步骤使用。优先选新增缺口件数较少的路线，再按步骤数和注册顺序决定。材料组可混用已有成员，剩余缺口按配方代表物展开。这是可复现的一条路线，不按物品稀有度定价，也不保证整棵树的全局最优。
- 计算使用已加载配方的标称用量，不执行 Mod 回调，不将站点和条件视为当前已满足。需实际制作时仍由合成接口检查。超过深度或搜索预算返回 `plan_search_limit`，数量溢出返回 `plan_quantity_overflow`，均不会返回部分汇总。

### 真实专服样例

在 Terraria 1.4.4.9、tModLoader 2026.09.3.1 上运行 `tests/verify_server_ai_native.ps1 -MaterialPlan`。每个目标均通过一次 Lua 查询返回，动作数为 0。第一组背包有 30 北地木和 3 凝胶；第二组有天顶剑、血腥屠刀和铜短剑各 1 把。

| 加载环境 | 目标总量 | 合成步骤 | 缺口种数 | 实际路线 |
| --- | --- | ---: | ---: | --- |
| 原版，第一组 | 天顶剑 ×1 | 14 | 22 | 最终配方有 10 把剑；永夜刃选用魔光剑。缺口包括 120 叶绿矿、60 狱石、20 黑曜石、1 喵刃。 |
| 原版，第二组 | 天顶剑 ×2 | 10 | 20 | 抵扣已有天顶剑和铜短剑，永夜刃改用已有血腥屠刀。 |
| 灾厄 2.2.4，第一组 | 天顶剑 ×1 | 24 | 29 | 最终配方使用宇宙砧，额外消耗 5 个 `CalamityMod/AuricBar`。 |
| 灾厄 2.2.4，第二组 | 天顶剑 ×2 | 20 | 27 | 抵扣三把剑，仅再合成一把天顶剑。 |
| 灾厄 2.2.4，第二组 | `CalamityMod/DesertMedallion` ×2 | 1 | 3 | 缺 80 沙块、8 蚁狮上颚和 4 个 `CalamityMod/StormlionMandible`。 |

同一测试还验证了 3 凝胶加 3 北地木可制作所需的 8 把火把（3 批，产出 9 把），以及已有成品足量时返回零步骤。加 `-KeepProfile` 可保留完整的 `material-plan.json`。

## 执行

```lua
local content = api('resolve_content', {kind='item', query='Terraria/WorkBench'})
assert(content.data.status == 'ok' and #content.data.candidates == 1)

local recipes = api('query_recipes', {
    resultType = content.data.candidates[1].typeId
})
local recipe = recipes.data.items[1]
return api('craft_recipe', {
    recipeId = recipe.recipeId,
    recipeToken = recipe.recipeToken,
    crafts = 1
})
```

`crafts` 表示配方执行次数（1..20），不是产物件数。执行前和每一批之间重新检查配方 Token、材料、制作站、环境条件和背包空间。无需将材料放入选中快捷栏。

## 原生执行与支持范围

执行器复用 Terraria 原生配方、材料消耗、`OnCraft` 和入包机制，并在绑定 AI Player 的游戏线程中运行。它不直接修改背包数量、不生成世界掉落来伪造产物，也不修改共享 `Main.myPlayer` 或鼠标物品。

当前支持范围限于已适配的普通原版配方/配方组、AI 普通背包材料、已支持的原生邻接制作站，以及已明确支持的环境条件。Mod 配方/物品、任意特殊消耗回调、未知环境条件、箱子/银行/虚空袋材料、微光分解、研究复制和任意 Mod 制作站不在支持范围。

输出空间检查是保守预检；它不保证之后必然制作成功。配方/库存/环境在查询后仍可能变化，执行器必须重新核验。

## 结果语义

- 前置条件不满足：明确拒绝，尚未执行合成。
- 多批执行可能部分成功；后续批次失败时保留已完成产物和材料消耗，不回滚。
- 原生异常、产物未能完整入包、库存净变化不符或结束证据不确定：返回 unknown 并隔离，不补发、不重放。
- `completedCrafts`、`producedCount`、`batches` 和 `inventoryVerified` 用于核对实际产物。只有整单批次和入包结果都验证后才可报告整单完成。
- 取消可能发生在已完成一批之后；取消不意味着此前副作用被撤销。

## 实现入口

`CompanionBridge/ServerIpc/ActorCrafting.cs`、`ServerAiNativeActions.cs`、`python/atomic_operations.py`、`python/server_ai.py`。

## 用 Lua 按产物件数制作

`terraria.crafting.make(resultType, desiredCount, {maxBatches=8})` 中，`resultType` 为物品类型 ID，`desiredCount` 为本次期望新增的件数。模块逐页查询候选，检查可制作性及产物空间，以配方单次产量换算执行次数。每批最多执行 20 次，重新查询下一批的配方令牌。批数上限允许 1 至 32，仍受当前 Lua 预算约束。

查看报告的 `producedCount`、`remaining`、`batches` 和原始 `receipt`。单次产量可能使实际件数超过目标。无可用候选时，`recipes` 保留最后一页观察，可查材料、工作站和空间。隔离专服用每次产出三件的配方请求七件，实得九件；空间不足场景保留了实际部分产量，随后停止。

模块在角色当前位置制作。各页都没有可用配方，而有候选只缺工作站时，返回 `missing_crafting_station`、该候选的 `stations` 和还要制作的 `remaining`。站点是否生效由原生配方判定。返程由 Agent 安排。在野外缺站就回基地制作区，已经在基地边缘时也可前往已知的制作区。抵达后按剩余数量再调用 `make`，仍缺站时说明当前位置需要哪种站。
