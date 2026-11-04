# 导航规划诊断与真实游戏验收（0.1.28）

## 后续：服务器 AI 阻挡证据（2026-10-30）

在 `7d0fcf2` 基线上增加可选 `evidence.navigation.blockingTiles`，沿用原 native_navigation_failed
回执、reasonDetail、结束证据和输入释放；客户端导航不启用接触采集。没有新增操作或扩大预算。
示意（非实机采样）：

```json
{"reason":"native_navigation_failed","evidence":{"navigation":{"blockingTiles":[
  {"x":2,"y":0,"contentKey":"Terraria/Stone","basis":"rejected_root_forward_walk_contact"}
]}}}
```

证据只覆盖一个严格受限的情形：规划根节点向目标方向的步行候选被拒绝，原生 `StepUp/StepDown`
之后的 `Collision.TileCollision` 实际裁剪了水平位移，预测下一步确实停止；接触面与 Tile 边界一致，
接触面沿角色身体高度相交的格被读回为已知完整实心 `#`。仅在搜索没有返回任何可执行路线且未触发时间预算时附带。
这是**被拒绝的前向步行试算**的接触，不是角色已经撞上它的事件，不证明所有跳跃/其他路线不可行。
坐标和内容键在该规划游戏更新中读取，不在稍后的 Poll 扫描或替换内容。

复用现有 native Move 的位置/请求位移/裁剪位移；只补读同一接触列最多四个 Tile，
这些格在原有预测邻域内，不扫描周围实体、整片地形或目标方向走廊。
已核对当前引擎 TileCollision 的水平裁剪表达式为 tileLeft-(positionX+width) 或 tileRight-positionX；
垂直方向的 ±0.01 修正不用于推断水平阻挡。坐标对齐校验失败即不提供证据。
空气、单向平台、未知、液体、危险、虚化、半砖和斜坡均不冒充这里的完整实心阻挡。

无可靠证据时省略 blockingTiles，不能解释为没有阻挡。时间/扩展/支撑/生命变化/执行偏差等其他失败
不通过附近实心格猜测原因；其他失败及取消清除旧证据。原有落地核验仍可能把失败升级为 unknown，
诊断不会改变终态、continuationAllowed、预算、pcall 或重试规则。

本轮定向验证命令（仓库根目录）：

```powershell
pwsh -NoProfile -File tests/run_csharp_tests.ps1 -NavigationOnly
pwsh -NoProfile -File tests/run_csharp_tests.ps1 -SceneOnly
pwsh -NoProfile -File tests/run_scene_layer_tests.ps1
pwsh -NoProfile -File scripts/build-companion.ps1 -OutputPath .local/navigation-perception-build
pwsh -NoProfile -File tests/run_server_ai_ipc_tests.ps1 -PackagePath .local/navigation-perception-build/Mods/CompanionBridge.tmod
# 在 tests/ 运行：
../.local/lua-venv/Scripts/python.exe -B -m unittest test_navigation_perception test_scene_layers test_server_ai_scene test_landing_navigation test_server_ai_backend.BackendTests.test_workbench_explicit_cancel_preserves_unknown_release test_server_ai_backend.BackendTests.test_missing_selection_evidence_preserves_unknown test_server_ai_backend.BackendTests.test_server_unknown_evidence_poisoning_uses_existing_lua_runtime -v
../.local/lua-venv/Scripts/python.exe -B -m unittest test_server_ai_placement test_server_ai_crafting test_server_ai_backend.BackendTests.test_existing_lua_worker_runs_navigation_use_mining_in_sequence -v
```

结果：新增 44 项身体范围/接触证据/失效清理检查，既有导航和落地回归通过；
场景几何 1932 项、实际 reader 分层/兼容检查 595 项通过；25 项 Python（真实隔离 Lua Worker、
loopback 与模型替身）及另 13 项放置/合成/组合动作回归通过；IPC/包回归通过，含 71 项 IL 检查。
构建 0 错误、0 警告，包 SHA-256：`00D4AD9DEDB01FD543831D118604C7B0043585DCA929979BECFE634FFC4F65CA`。
不运行全量测试，不部署，不运行真实 DeepSeek、原生游戏执行或 Host & Play；
碰撞替身与源码/IL 检查不能代替真实引擎场景验收。垂直接触、半砖/坡面、平台阻挡及所有路线失败的
完整归因尚未覆盖；这些情形保持省略诊断，不放宽现有导航安全检查。

## 历史：0.1.28 验证

本轮基于 `5ad142f` 补齐观测链路，不修改规划算法、25ms/32节点预算或原有50ms保护。构建产物为 `.local/navigation-diagnostics-build/Mods/CompanionBridge.tmod`，版本 **0.1.28**。本轮未部署、未运行真实游戏。

## 诊断链路

`Route` 的结果由 `NavigationTask` 复制进既有 `NavigationSnapshot`。执行器逐帧发布、动作交接、失败和到达后仍保留最近一次规划数据；取消或新任务初始化清除旧数据。`GameNavigation.Snapshot` 沿用原有选择逻辑，因此现有 `get_navigation` 自动带出新增字段，无新增请求命令。

`GameNavigation` 在每次 Before/After 和失败/取消出口观察快照。每个规划只写一条 `navigation_plan`，终止状态变化写 `navigation_state`，取消写一次 `navigation_cancel`；普通执行帧和相同目标续租不重复打印。日志使用现有 Mod `Logger.Info`，前缀为 `Navigation `，后面是 JSON，属性命名与 `get_navigation` 一致。

规划日志在适配器原有50ms计时检查之后序列化；规划器的 `planMilliseconds` 本来就只测量规划调用内部，不包含日志序列化。日志写入仍占用游戏线程时间，本轮不声称零开销，也未改计时器或50ms判定。若触发50ms保护，失败日志保留最后一次规划字段。

| 字段（`navigation` 内） | 含义 |
|---|---|
| `session` / `generation` / `planNumber` | 关联一次控制任务及其中第几次规划。新任务从0开始，每次实际调用规划器递增 |
| `targetRegion` | 二维目标的 `minPlayerLeftX`、`maxPlayerLeftX`、`surfaceY`；水平目标为 null |
| `reachedGoal` | 最近一次搜索是否返回到达其搜索终点的预测路线。允许是预算结束时保留的可行完整候选，不保证最优 |
| `timeBudgetReached` | 搜索是否检查到时间预算并退出；不等价于 `planMilliseconds > 25`，也不等价于失败 |
| `planMilliseconds` | 最近一次实际规划的内部 Stopwatch 毫秒数，未四舍五入为整数 |
| `expansions` / `plannedActions` | 实际扩展节点数，以及本次返回路线的动作数；任务只执行第一段，再根据实测状态重规划 |
| `status` / `reason` | 执行任务状态与原因；`arrived` 才代表任务已按真实状态确认到达 |
| `completedActions` / `completedJumps` / `lastLanding` | 实际核验过的动作、跳跃和最近一次落点反馈 |
| `expectedPosition` / `observedPosition`、对应速度、`maxError` | 原有执行偏差诊断；用于核对预测与真实状态 |

日志最外层还包含 `event` 和 `goalKind`。

- `goalKind=landing_region` 时，`reachedGoal=true` 表示预测完整路线满足指定 X 区间、脚底高度、安全支撑及停稳条件。X 指碰撞框左边缘，Py 指顶部，脚底为 `Py+42`，高度容差0.5px。
- `goalKind=horizontal` 时，`reachedGoal=true` 只表示当前局部水平搜索终点，可能只是128px滚动子目标，不能证明整个 `targetX` 任务已完成。
- `reachedGoal=true,timeBudgetReached=true` 是合法组合：搜索触及时间预算，但已保留完整候选。
- `reachedGoal=false,plannedActions>0` 是可执行的安全部分路线；`plannedActions=0,status=failed` 时检查拒绝原因。
- 两个布尔字段为 **null**、`planNumber=0` 表示尚未规划，例如已经在目标上、起步支撑不安全或能力不匹配。null 不能当成搜索失败的 false。
- `timeBudgetReached=false,expansions=32,reachedGoal=false` 提示先调查节点上限/搜索回退，不要直接归因为时间预算；这组字段不单独证明目标不可达。
- 取消日志保留被取消任务的身份与最近规划，供关联；实际 idle 快照清除这些旧字段。

格式示意（非真实游戏采样，省略既有快照字段）：

```text
Navigation {"event":"navigation_plan","goalKind":"landing_region","navigation":{"session":1,"generation":8,"planNumber":1,"reachedGoal":true,"timeBudgetReached":true,"planMilliseconds":25.4,"expansions":20,"plannedActions":1,"targetRegion":{"minPlayerLeftX":1664,"maxPlayerLeftX":1744,"surfaceY":1552},"status":"running"}}
Navigation {"event":"navigation_state","goalKind":"landing_region","navigation":{"session":1,"generation":8,"planNumber":1,"status":"arrived"}}
```

## 部署与可立即执行的水平链路验收

1. 保存并完全退出测试客户端。确认该客户端实际的 `-tmlsavedirectory`，将本次产物复制到该目录的 `Mods/CompanionBridge.tmod`，保留旧包备份。重启并在 Mod 列表确认 **0.1.28**。不要把仓库中历史构建目录的包误当成本轮产物。
2. 在本机客户端使用普通20×42角色、支持的基础物理，无功能装备、坐骑、液体或未知地形影响；关闭自动暂停，松开方向和跳跃键。选择干燥、已加载、安全的平地及左右低障碍。
3. 在仓库根目录运行已有入口，按场地距离调整参数：

   ```powershell
   python python/navigate.py --direction right --tiles 20 --seconds 25 --output .local/navigation-diagnostics-right.json
   python python/navigate.py --direction left --tiles 20 --seconds 25 --output .local/navigation-diagnostics-left.json
   ```

4. 从**该客户端实际保存目录**的 `Logs/client.log` 查找 `Navigation `。它是客户端日志，不是服务端日志。例如，在把 `$saveDir` 设置为已核对的实际目录后：

   ```powershell
   Select-String -LiteralPath (Join-Path $saveDir 'Logs/client.log') -Pattern 'Navigation '
   ```

5. 用三元组 `session/generation/planNumber` 对齐日志与脚本采样。确认同一次规划的节点数、耗时、两个布尔值在执行中保持不变；重规划时 `planNumber` 加一；低频 `get_navigation` 只显示最近一次规划，逐次历史应从日志读取。水平日志应明确为 `goalKind=horizontal`。
6. 比较新进程首次导航与同进程多次导航，记录每次耗时、扩展数、是否返回部分路线、是否触及时间预算，以及最终任务状态。25ms是候选之间检查的软预算，单次候选、JIT和线程调度可能使实际耗时超过25ms。关注 `reason=local prediction exceeded 50ms budget`，不要把它与搜索内部时间退出混为一谈。
7. 分别测试无路、执行中按P、断开监督脚本、死亡/切换世界。确认失败/取消有可关联记录，任务不会自动恢复，新的任务没有继承旧规划布尔值。保留完整客户端日志、脚本JSON和对应部署版本。

## 二维落脚真实验收的入口前提

**历史范围（0.1.28）：当时 `navigate.py`、TCP `navigate` 及正常玩家入口只传 `targetX`，上述水平测试不能充当二维落脚验收。0.1.30 已新增独立的 `navigate_to_landing_region` 原子工具及真实更新入口，当前调用方法见 [二维落脚导航](LANDING_REGION_NAVIGATION.md)；旧 `navigate.py`/TCP `navigate` 仍保持水平导航语义。**

二维验收需要调用方（0.1.30 已由上述原子工具接入），在现有真实 Before/After 挂钩、控制 session/generation、续租和取消生命周期内，持续向 `GameNavigation.Before(..., region: new LandingRegion(minPlayerLeftX,maxPlayerLeftX,surfaceY))` 提供固定目标。仅从控制台调用一次 Plan/Before，或绕开真实输入/After 核验，都不足以验收执行闭环。

若尚无该调用方，二维游戏验收入口仍是后续前置项；本轮可以完成日志链路的水平冒烟验收，不应宣布二维游戏验证通过。

当该调用方具备时，按以下矩阵验收，并先确认日志出现 `goalKind=landing_region` 和正确的 `targetRegion`：

1. 从地面到已知普通实心平台，分别测试左右32/48px高差、较高平台、窄安全X区间。先从宽目标开始，记录冷启动及连续重复结果。
2. 完整候选：`reachedGoal=true` 后必须继续观察真实执行和最终 `navigation_state/status=arrived`，不能以规划记录代替实际到达。检查实际脚底 `Py+42`、X范围、着地、速度绝对值≤0.1、跳跃释放与计数归零；结合 `lastLanding` 和实时状态。
3. 站在目标X下方、平台旁经过、支撑不足、净空不足、危险/未知区域或不可达平台：不得出现最终 `arrived`。保留计划与失败日志，区分部分路线、节点上限、时间退出和实际落地偏差。
4. 执行中中断控制或产生实际偏差：观察原有偏差/落地核验拒绝，检查失败/取消后无旧任务恢复。

## 本轮证据

离线测试使用生产规划器、任务、执行器、日志去重与JSON格式器；合成碰撞世界通过刻意延迟一次 Move，分别覆盖部分路线超时、空路线超时、完整候选与超时同时为true，不是原生性能测量。覆盖规划结果在逐帧发布/落点/失败/取消/代次切换后的保留或清除，以及真实 `get_navigation` TCP 序列化。原生 `GameNavigation` 日志接线经 Mod 构建验证，尚未在真实游戏中运行。

本轮实际运行结果：`pwsh -NoProfile -File tests/run_csharp_tests.ps1 -MotionOnly` 与完整 C# 离线回归通过；`python -m unittest discover -s tests -p test_navigate.py` 7项通过；Mod构建0错误、0警告；`git diff --check` 通过。离线玩家挂钩测试仍替换 Terraria 适配器，不能据此声称原生日志已经在真实客户端中验证。
