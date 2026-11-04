# 独立 Server AI 原生合成（0.1.60）

服务器提供 `query_recipes` 和 `craft_recipe`，由 Agent 根据授权目标和新鲜观察自行决定产物、次数及采集/移动顺序。配方来自当前加载的 `Main.recipe`，没有固定工作台、火把或其他产物配方表。原子执行器不采集、走近、放制作站、换配方或失败重试。

本轮只进行定向离线与隔离原生验证。**未部署现用 profile，未进行图形 Host & Play 或真实 DeepSeek 测试。** 0.1.59地图与本轮合成留待后续集中实机验收。

## 当前接口

同时遵循服务器原有能力门禁；握手 `server_ai_crafting_v1` 才开放新 API，执行还需 `server_ai_actions_v1`。旧服务器和客户端玩家控制路径不宣告合成能力。模型仍仅调用 `execute_lua`。

`api('query_recipes', {...})` 是只读查询，不消耗动作次数。参数：

| 参数 | 含义 |
| --- | --- |
| `resultType` | 可选产物类型ID；用 `resolve_content(kind='item',query=...)` 的 `data.candidates[].typeId` 解析，不猜配方ID。 |
| `availableOnly` | 默认 true，只返回当前满足本执行端支持条件的配方；false 可查缺材料、缺制作站、环境不符、禁用或不支持原因。 |
| `limit` / `offset` | 默认12/0，每页最多24条；检查 `matchedCount/returnedCount/truncated`，翻页不是同一快照。 |

回执沿用绑定世界/Actor代次/槽位、`gameUpdate` 和服务器真实UTC时间，详情在 `data`：

- `items[]`：当前注册表 `recipeId`、绑定/定义作用域的 `recipeToken`、`output` 产物类型/键/名称/每次数量，`ingredients` 材料及原生配方组可替代类型，`stations` 所需Tile及本AI原生邻接状态，`conditions` 描述/支持性/满足状态。
- `supported/available/reason` 区分本执行端支持与当前可制作；不支持的条件 `met=null`，不会通过读取真人环境猜结果。
- `hasOutputSpace` 是当前保守入包空间检查，独立于材料/站点可用性；查询不保证之后一定成功。
- `unsupportedCount/registryRecipeCount/scope` 限定覆盖；默认列表排除了不支持配方，不能把返回为空解读为整个注册表没有对应产物。

`api('craft_recipe', {recipeId=..., recipeToken=..., crafts=...})` 是单一原子意图。`crafts` 为 **1..20次配方执行**，不是产物件数；实际总量是每次产量乘执行次数。无需选中材料快捷栏。配方标识从真实查询取得，token防止换世界/代次、换注册表对象或定义后继续用旧配方；执行前仍重新检查真实状态。

```lua
local content = api('resolve_content', {kind='item', query='Terraria/WorkBench'})
assert(content.data.status == 'ok' and #content.data.candidates == 1)
local recipes = api('query_recipes', {resultType=content.data.candidates[1].typeId})
-- 是否制作、选择哪个配方及次数由 Agent 根据任务和材料决定。
if #recipes.data.items == 0 then return recipes end
local r = recipes.data.items[1]
local made = api('craft_recipe', {recipeId=r.recipeId, recipeToken=r.recipeToken, crafts=1})
return {action=made, inventory=api('inspect_inventory', {mode='full'})}
```

配方数量不等于执行次数；原版1木材→2木平台、1木材+1凝胶→3火把的值也是运行时读取，不是执行器硬编码。

## 原生机制与隔离

研究了当前安装DLL中的 `Recipe.FindRecipes/Create/ConsumeForCraft`、`Main.CraftItem`、`Player.AdjTiles/GetItem`、`RecipeLoader` 和 `Condition`，并核对 tModLoader [配方补丁](https://github.com/tModLoader/tModLoader/blob/1.4.4/patches/tModLoader/Terraria/Recipe.cs.patch) 与 [RecipeLoader文档](https://docs.tmodloader.net/docs/stable/class_recipe_loader.html)。`Recipe.Create` 主要扣料，不负责把最终产物放入背包；原制作界面还负责克隆、随机前缀、OnCraft和鼠标槽产物。这两步都不能只凭函数返回宣称获得物品。

`ActorCrafting` 在原有精确引擎/无其他Mod隔离门禁后，使用私有 `DynamicMethodDefinition` 副本适配原方法：

1. `AdjTiles` 原生扫描本AI周围制作站/液体，保留站点等价和原扫描范围，排除末尾客户端配方UI刷新。
2. `CollectItemsToCraftWithFrom` 收集本AI材料，`CollectedEnoughItemsToCraftRecipeNew` 和原Tile/环境判定复用原生实现。支持条件直接执行原生Predicate的绑定副本，不重写水体/昼夜规则。另用不扣料的顺序分配检查排除重叠配方组重复计数。
3. `Main.CraftItem` 副本保留原生克隆、Prefix、`Recipe.Create`、`RecipeLoader.OnCraft` 与产物组装；本地玩家读取绑定AI，鼠标槽替换为私有临时产物。`Create` 内原 `ConsumeIngredient/ConsumeForCraft` 执行材料消耗和实际消耗记录。副本排除客户端成就/配方刷新、提示文字和声音尾段。
4. 使用本AI的原生 `Player.GetItem` 入包，随后核验实际产物类型/数量、无剩余产物以及所有物品类型的背包净变化，不能只靠原生扣料或“调用成功”完成。

不改共享 `Main.myPlayer/mouseItem`，不补写背包数量、直接生成世界掉落或伪造消费。配方临时计数缓存和原生消耗记录在作用域结束时恢复；不修改共享配方定义/条件/回调。所有Terraria操作在原绑定游戏线程/Reservation Gate内完成；动作沿用互斥、身份、接受/执行/终态、取消/断线/死亡/截止和释放证据，无网络线程引擎操作。

私有副本仍依赖既有 Terraria 1.4.4.9 / tML 2026.8.3.0 精确DLL指纹，另验证读取模式和UI边界。未知结构拒绝启用；没有扩展引擎或Mod支持范围。

## 支持范围与失败语义

首版支持普通原版配方、原生材料组、背包0..57材料、原生邻接制作站；产物经原生入包。本执行端认可原生 `NearWater/NearLava/NearHoney/NearShimmer/TimeDay/TimeNight` 六种条件Predicate。**本轮独立原生环境对照实际覆盖NearWater，其他五种未分别做场景矩阵。**

以下明确不支持：Mod配方/物品、任意特殊消耗或OnCraft回调（包括炼金节材类）、其他生物群系/特殊环境条件、打开箱子/存钱罐/虚空袋及Mod额外材料仓库、微光分解、研究复制、自动放置新制作站或复杂制作任务。注册表可报告这些条目及原因，不会为通过测试忽略其条件。当前服务器本身仍受既有Mod隔离门禁约束，不能由“读取加载注册表”推导为已支持任意Mod合成。

产物空间按**扣料前**背包0..49空位及普通可堆叠余量保守检查；不借助消耗后才释放的槽、不丢到地上、不移到虚空。原生本可能通过腾出材料槽完成的满包情形会拒绝；这是本版明确边界。原生入包仍是最终依据。

每次游戏线程Pump最多执行一批，批次之间重新查配方、材料、站点、环境和空间；不预扣整单。回执固定 `targetRecipe`，证据提供 `requestedCrafts/completedCrafts/producedCount/outputTypeId/outputCountPerCraft/batches`。每批保留实际原生消耗；`completedCrafts` 只在入包和净变化核验后增加，`inventoryVerified=true` 表示**整单**完成。

- 前提拒绝：`insufficient_materials/missing_crafting_station/environment_not_met/insufficient_inventory_space/recipe_changed` 等，零批时 `mutationAttempted=false`。
- 部分完成：可能先成功一批、下一批材料或空间不足；整单 `failed`，已产生和消耗的数量保留，不回滚。取消也可能已有产物。
- 原生异常、净变化不符、产物未完全入包或同步失败：可能已变更，保持unknown并quarantine，不重放/补发/回滚。异常时可能存在未确认产物，不能承诺恢复或持久化临时产物。
- `completed` 还需绑定结束证据、输入释放及效果核验。Python同时核对目标token/次数、完成批数、总产量和入包证明；程序正常返回不等于合成成功。

Lua原内存、指令、时间、API和动作预算不变，失败后仅感知及unknown断开保护保持。没有自动重试。

## 实测与证据

最终包0.1.60 SHA-256 `ee86b63aebd8ec38580faea1af2c80cdb748d04143e610e194bb48cc5bd645ed`。原始程序、回执、日志与哈希见[manifest](evidence/server-crafting/manifest.json)，结果见[summary](evidence/server-crafting/summary.json)。隔离夹具会注入诊断库存/起点并构造工作台/水体或原生生长树，不能当作自然世界或图形实机验收。

- 工作台配方：9木材明确不足、满背包明确拒绝且未扣料；原生采集拾取后的10木材实际消耗，得到1工作台。
- 木剑：同材料无工作台拒绝，附近原生工作台存在时消耗7木材并获得1木剑。
- 配方组：3北地木材和3凝胶实际用于火把配方，执行3次实际获得9火把；普通工作台的原版木材配方不被误当成接受所有木材。
- 环境：从加载注册表选取无特殊回调的NearWater配方；缺水拒绝，附近水体存在时实际合成。本引擎选到瓶装水配方，产物ID126由真实配方读取。
- 空间/部分完成：木平台请求2次，第一批原生生成2个平台并补满现有堆叠，第二批因无空间停止，保留1/2次完成与实际消费。
- 旧token零消费拒绝；未执行前取消零消费。额外原生诊断在完成1/20批后调用执行器取消，确认没有多执行一批；忙碌拒绝句柄的取消保持未开始失败回执，真人背包、鼠标物品、myPlayer、制作列表/焦点和材料缓存保持不变。
- 真实Lua/IPC组合：铜斧原生砍树13击，导航至掉落附近、原生拾取，解析产物名称并查询实时配方，再真实合成工作台、重读背包；木材0→16→6且新增1工作台，12次API/4动作，不调用LLM。原子层没有加入该任务流程。
- 独立复制世界复跑旧读取/导航/使用/挖掘/取消/退出，避免新夹具影响旧动作初态。

定向离线：54个不同Python用例（合成7、后端28、场景7、原子12）；C# IPC/原子/读取/适配/聊天45/40/57/63/30项，包64项IL；原工具19+30、放置119、新合成11项IL通过。构建零错误/警告。**未运行全量测试。**

早期私有方法名称/近水Predicate读取数检查失败、回执字段大小写失败及夹具错误的木材组/类型ID/产量假设均保留。相应修正不放宽材料、制作站、空间、身份或unknown核验；最终夹具从注册表读取条件配方和单次产量，避免以错误固定预期要求生产合成。

真实DeepSeek自主选择配方和组合策略、语义地图协作、图形Host & Play、真人/敌怪同场以及远程同步仍待验收。

## 定向复现

在仓库根目录、已有依赖和原生DLL环境执行。原生脚本仅操作新隔离profile并关闭自身测试进程：

```powershell
& .local/lua-venv/Scripts/python.exe -B -m unittest discover -s tests -p test_server_ai_crafting.py -v
pwsh -NoProfile -File tests/run_server_ai_tool_tests.ps1
pwsh -NoProfile -File scripts/build-companion.ps1 -OutputPath .local/craft-build
pwsh -NoProfile -File tests/run_server_ai_ipc_tests.ps1 -PackagePath .local/craft-build/Mods/CompanionBridge.tmod
pwsh -NoProfile -File tests/verify_server_ai_native.ps1 -Atomic -Reads -Actions -Crafting -PackagePath .local/craft-build/Mods/CompanionBridge.tmod -Port 28003 -IpcPort 28004
# 旧动作另用新复制世界，不接着复用已经合成/移动过的夹具
pwsh -NoProfile -File tests/verify_server_ai_native.ps1 -Atomic -Reads -Actions -PackagePath .local/craft-build/Mods/CompanionBridge.tmod -Port 28005 -IpcPort 28006
```
