# Server AI 合成

Server AI 提供只读配方查询 `query_recipes` 和原生合成 `craft_recipe`。Agent 决定目标产物、配方、次数及前置采集/移动顺序；执行器只执行指定配方，不自动采集、移动、放置制作站、切换材料或重试。

## 能力与查询

需要服务端能力握手 `server_ai_crafting_v1`。旧服务器或客户端控制后端不应假设开放该接口。

`query_recipes` 从当前加载的 `Main.recipe` 查询，不使用硬编码配方表：

| 参数 | 含义 |
| --- | --- |
| `resultType` | 可选产物 Item 类型 ID；先用 `resolve_content` 解析 |
| `availableOnly` | 默认 true，仅返回当前执行端支持且满足条件的配方；false 可查看不可用/不支持原因 |
| `limit` / `offset` | 分页，默认 12/0，每页最多 24 |

每条配方包含运行时 `recipeId`、绑定定义的 `recipeToken`、产物数量、材料/原生配方组、制作站和环境条件，以及 `supported`、`available`、`reason`。不支持条件以未知/不支持表达，不会通过读取真人玩家状态猜测。空页不证明整个注册表没有该配方。

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
