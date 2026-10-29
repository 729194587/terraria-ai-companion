# 原版工作台多格放置 MVP

## 支持范围与接口

CompanionBridge 0.1.62，独立 Server AI。握手同时提供 `server_ai_actions_v1` 与
`server_ai_place_object_workbench_v1` 才开放 `place_object({x,y})`。只允许当前快捷栏选中的
原版 `ItemID.WorkBench`（36），`TileID.WorkBenches`（18），默认样式 0；不自动选栏、走近、重试。
`place_tile` 仍只允许原有泥土、石块、木材单格方块。旧服务器和客户端不宣称支持新操作。

`x/y` 是 **Tile 坐标的 TileObjectData Origin 锚点**，不是角色中心或世界像素。
本版本实际定义 Width=2、Height=1、Origin=(0,0)，因此目标为左上格，占地 `(x,y)..(x+1,y)`，
下方支撑为 `(x,y+1)..(x+1,y+1)`。运行时读取注册定义并校验已验证形状、坐标帧尺寸、锚点和
CustomPlace；定义不匹配即拒绝，不推广为任意家具 API。

保守预检：完整占地及支撑在世界边界内，锚点中心距 AI 中心不超过 80px，占地空且无液体，
完整占地排除 AI 与其他活跃存活非幽灵玩家，支撑是无液体、未驱动、无斜坡/半砖的实心非平台。
原生支持的部分平台/斜面站点不在此 MVP 内。身份、世界、代次、对象、slot、动作互斥和快捷栏检查
复用现有执行链。所有 Player/Tile 操作只在游戏线程进行。

## 引擎机制与成功证据

研究本机固定版本 tModLoader DLL 的真实 IL 和加载后的 TileObjectData，未扩展引擎支持范围：

1. `ActorItemUse` 在既有自然 `ItemCheckWrapped` 边界执行一次原生 `ItemCheck`，只产生首帧使用输入。
2. 已有带 Player 身份的目标作用域调用 `Player.PlaceThing_Tiles`；保持原生范围、CanUseItem、
   TileLoader.CanPlace、占地/支撑/玩家碰撞及物品消费规则。共享 myPlayer/鼠标/Tile target 不变。
3. 实测工作台 CustomPlace=true，原生分支为 `TileObject.CanPlace` → 玩家碰撞检查 →
   `PlaceThing_Tiles_PlaceIt` → `TileObject.Place` → 原生计时、后处理和物品消费。
   监听 `On_TileObject.Place` 只记录本动作拥有的事件，不直接调用 PlaceObject/Place/手动写格或扣物品。
   原 `WorldGen.PlaceTile` 监听继续服务普通单格动作。
4. 完成要求：恰好一个本动作原生物体放置事件、完整两格类型/默认样式帧正确
   （frameX=0/18、frameY=0）、无驱动/液体、支撑物理事实保持、无观察到的外部移除/替换、
   本次 ItemCheck 消耗恰好 1 件、最终库存数量吻合，以及原生周期结束后的绑定输入释放。

`targetTile` 固定接受的锚点。回执含 `footprint`、`fullFootprintVerified`、`nativePlacementEvents`、
`placedByActor`、`consumedCount`、`targetChanged`、`uncertainNativeEffect` 和既有结束证据。
Python 再核验白名单、目标/占地、事件数、消费及身份/释放证据；动画或单格存在不能证明成功。

执行前阻挡/缺支撑/错误物品等为无动作拒绝。取消、超时可能留下已确认的放置/消耗，回执保留事实，
不回滚；它们不是 completed。放置后占地变化、消费未确认、部分物体写入等不确定结果为 unknown，
取消不得抹成普通 cancelled。Lua 保留 unknown 后禁止后续变更的保护，不自动重试。
原生异常沿用 NativeSurvival quarantine，释放已知拥有的输入并保留 reservation，不在旧进程恢复执行。

取消核验在游戏线程、清除计时器之前读取当前完整占地、支撑、事件数、材料消费和库存身份，
不等待下一次 ItemCheck。消费未确认、目标变化或异常留下的不确定性保持 unknown；重复 Retire/Stop、
玩家禁用/死亡、物品对象变化和超时均不能覆盖它。清理后重新读取实际输入释放证据；死亡但仍属于
本绑定的 Actor 也会释放输入。正常已放置且消费确认后的取消保留物体/消耗，不回滚、不算完成。
Python 的 place_object 监督截止也保留取消返回的 unknown，触发既有 Lua 变更屏障且不重试。
[取消回归原始证据](evidence/server-object-placement/cancellation/manifest.json) 独立记录本次包哈希与结果。

0.1.62 的明确预检拒绝补齐 `reasonDetail`：占地阻挡给出实际格坐标和 Tile 类型，玩家冲突给出
玩家与完整占地的像素碰撞框及 Tile 占地，距离拒绝给出实际锚点距离、80px 限制和锚点坐标。
`reason` 保持稳定；仍是 `mutationAttempted=false` / `effectStatus=not_started`，不是原生放置已执行。
仅宿主核验的 `continuationAllowed=true` 允许同一程序换候选，其他失败和 unknown 屏障不变。
`placementCandidates` 专供单格 place_tile，工作台必须使用现有 `objectPlacementCandidates`；
后者为空不能当作接口不存在或改用单格候选。原实机报告的更正见[逐候选分析](evidence/lua-recovery/analysis.md)。

支撑快照比较类型、驱动、形状、液体等物理事实。普通实心支撑的贴图帧会被原生邻接重算，
不能把正常重算当作外部替换；完整工作台自身的帧仍严格核验。原生移除事件同时监视两格占地和支撑。

## observe_scene

可选 `layers` 的省略、配套字段和计算依赖见 [场景按需数据层](SERVER_AI_SCENE_LAYERS.md)。
只需工作台候选时可请求 `{layers={'objectPlacementCandidates'}}`；未传 layers 仍返回原完整观察。

增加独立 `objectPlacementCandidates`，沿用 `maxPlacements` 上限和每集合的 matched/returned/
truncated/omitted 字段；保留原 `placementCandidates`。每项包含可直接传入新动作的 `target`、
完整 `footprint`、`support`、锚点 `distancePixels`、物品内容键及默认样式。
占地和支撑必须全部位于本次 45×29 观察范围、碰撞知识已知，且通过干燥/空格/支撑/玩家排除。
未知、范围边缘未覆盖、液体或过远位置不产生候选；没有候选不等于全局无可放位置。
`objectPlacementMaterial` 说明所选物品与注册定义是否支持；世界范围/时间/未知数量沿用原观察封装。
候选只提供几何预筛选，不保证原生权限、放置成功、路径或可达性。
已放工作台的语义对象仍按注册锚点聚合，两格不计为两个家具。

### 候选排除诊断（阶段二任务 1）

只在 `objectPlacementCandidates` 增加 `diagnostics`；原 `items/matchedCount/returnedCount/truncated/omittedCount`
保持原义。尤其 **matchedCount 仍是通过全部几何筛选、截断前的数量**，不是进入筛选的数量。
普通 `placementCandidates`、地图、观察封装、实际放置动作与白名单不变，没有自动选位或重试。

统计单位为本次观察网格内每个左上格对应的一个假设工作台锚点，不是 Tile 数或已放家具数。
默认样式锚点就是占地左上格。完整占地及必要支撑均使用当前注册定义。

| diagnostics 字段 | 口径 |
| --- | --- |
| `anchorCount` | 观察网格中所有左上格位置数；45×29 时为 1305 |
| `inBoundsAnchorCount` | 定义可用且完整占地、支撑行均落在本次观察网格内的位置数 |
| `checkedCount` | 上述范围内、完整占地和必要支撑全部已知的位置数；分类可在首个失败处结束 |
| `rejectedCount` | checkedCount 中明确未通过几何筛选的数量 |
| `unknownCount` | 完整范围在图内，但至少一格未加载、未读、碰撞未解释或导航未知；不计入 rejectedCount |
| `notCheckedCount` | 完整范围超出本次观察或注册定义不可用，未检查的位置数；不计入 rejectedCount |
| `coveredScopeComplete` | 图内可完整覆盖的位置非空、定义可用且 unknownCount=0；不表示观察边界外完整 |
| `rejectedByReason` | 以下五类互斥的首个失败计数，包含零值类别 |
| `notCheckedByReason` | `outside_observation` / `definition_unavailable` 互斥计数 |
| `firstExamples` | 每类至多一例，合计最多七例；固定逐行、从左到右的首例，不代表最近位置 |

计数满足：`anchorCount = inBoundsAnchorCount + notCheckedCount`；
`inBoundsAnchorCount = checkedCount + unknownCount`；`checkedCount = matchedCount + rejectedCount`。
`sum(rejectedByReason) = rejectedCount`；`sum(notCheckedByReason) = notCheckedCount`。
`omittedCount = matchedCount - returnedCount` 仅为通过筛选后被列表上限省略的数量。

先检查完整占地和支撑的知识：任一格未知就只计 unknown，即使别的已知格存在障碍。
全部已知后按以下顺序仅计首个失败：`footprint_occupied`（任一占地格有 Tile）、
`liquid_present`（占地或支撑有液体）、`missing_support`（缺少合格的平整普通实心支撑，
包括坡、半砖、平台或不支持的导航表面）、`out_of_reach`（锚点距角色中心超过 80px）、
`player_collision`（整个占地与角色或其他已观察玩家碰撞）。因此计数可相加，但不代表列出了每个位置的全部问题。

示例含 Tile 坐标 `target/footprint/support`，以及实际阻挡格和已知类型/液体/知识、距离与 80px 上限，
或相交玩家的世界像素碰撞框。unknown 示例保留实际知识状态。观察边缘示例仅给未完整覆盖的范围，
不猜范围外的 Tile；定义不可用时没有可验证的占地示例。共享格可影响多个不同锚点，同一锚点始终只计一次。
空列表且 `truncated=false` 只说明没有已返回位置；还需看 matchedCount、未知/未检查数量及覆盖口径，
不能把未完整观察、被截断或局部无候选说成全局不可放置。
旧后端缺少 diagnostics 表示没有提供诊断，不等于所有计数为零。

本任务验证（2026-10-29，基于 4f273c2）：

```powershell
pwsh -NoProfile -File tests/run_csharp_tests.ps1 -SceneOnly
pwsh -NoProfile -File tests/run_csharp_tests.ps1 -NavigationOnly
pwsh -NoProfile -File scripts/build-companion.ps1 -OutputPath .local/object-candidate-diagnostics-build
# 以下在 tests/ 中运行
../.local/lua-venv/Scripts/python.exe -B -m unittest test_server_ai_scene test_server_ai_backend.BackendTests.test_workbench_capability_and_full_receipt_required test_server_ai_backend.BackendTests.test_workbench_lua_uses_existing_dispatch_and_unknown_barrier test_server_ai_backend.BackendTests.test_workbench_explicit_cancel_preserves_unknown_release test_server_ai_backend.BackendTests.test_workbench_deadline_cancel_unknown_poisoned_lua_without_retry -v
```

结果：场景几何 1906 项检查通过（含单格放置/工作台原候选回归）；导航三组 harness、
26 条 PASS 记录通过；Python 12 项通过（实际隔离 Lua Worker/loopback 模拟后端）；
完整 Mod 编译 0 错误、0 警告。校准了一项场景旧测试，使其遵循 4f273c2 已有的动作失败立即终止
当前程序契约，再用独立只读程序验证场景查询；没有更改 pcall 或动作实现。
原始输出在 `.local/object-candidate-diagnostics-scene-final.log`、`object-candidate-diagnostics-navigation.log`、
`object-candidate-diagnostics-python.log`、`object-candidate-diagnostics-build.log`。
未部署、未调用真实 DeepSeek，未执行原生 Terraria 场景/Host & Play 或无关全量测试。
以下保留先前真实放置动作的历史复现入口，不代表本次执行过这些原生测试。

## 定向复现与证据

不运行全量测试。构建只生成包，不部署现用 profile：

```powershell
pwsh -NoProfile -File scripts/build-companion.ps1 -OutputPath .local/object-build
pwsh -NoProfile -File tests/run_csharp_tests.ps1 -SceneOnly
pwsh -NoProfile -File tests/run_server_ai_tool_tests.ps1
pwsh -NoProfile -File tests/run_server_ai_ipc_tests.ps1 -PackagePath .local/object-build/Mods/CompanionBridge.tmod
pwsh -NoProfile -File tests/verify_server_ai_placement_native.ps1 -Object -PackagePath .local/object-build/Mods/CompanionBridge.tmod -Port 27943
pwsh -NoProfile -File tests/verify_server_ai_placement_native.ps1 -Object -Failure -PackagePath .local/object-build/Mods/CompanionBridge.tmod -Port 27945
pwsh -NoProfile -File tests/verify_server_ai_placement_native.ps1 -Object -Failure -AfterPlacementException -PackagePath .local/object-build/Mods/CompanionBridge.tmod -Port 27947
```

隔离脚本先复制世界和包至新 GUID profile。测试夹具只为构造场景注入地形、背包、姿态和无人连接的
玩家替身；正式执行器始终通过原生 Player 物品流程。放置后的配方测试从**当前 Main.recipe 注册表**
选择普通、支持且只需工作台的配方，提供真实材料，再比较原生查询的站点邻接并实际合成；不写 adjTile。
本次选择到金枪鱼 → 生鱼片是该运行的观察结果，不是预写配方或 API 保证。

原始报告、失败记录、包/引擎哈希和验收结果见 [证据清单](evidence/server-object-placement/manifest.json)。

验证分层：离线测试检查协议、Schema/能力、Lua unknown 屏障、几何与 IL；隔离原生测试执行本机 DLL
真实放置/消耗/查询/合成及旧动作；**未进行真实图形 Host & Play、未调用真实模型、未部署现用 profile**。
真人在线竞争、远端客户端渲染/同步、战斗中放置、自然世界自主选点，以及任意 Mod/家具/非默认样式
均未在本轮验收；直接绕过事件的第三方原始内存同帧同值改写不能据此宣称可归因。
