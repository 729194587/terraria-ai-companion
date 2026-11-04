# M4.1 搜索与空间事实正确性基线

日期：2026-11-03。基线为 PR #25（Open / Draft）、分支 `codex/g85-server-ai-perception`、HEAD `213bc90993d43d261fec606c036263a450e05dca`。开始时 GitHub head_sha 与本地一致。原有 Python/Lua/原生脚本修改和未跟踪设计、实验文件保留，不纳入本次源码提交。

**验收结论：M4.1 在下述离线和隔离原生范围内通过。** 只完成搜索正确性基线，没有实现 M4.2 的偏差恢复、Trip、装备支持或地图扩展。没有运行全量测试、图形 Host & Play 或真实模型，没有部署现用 profile，不合并 PR。

## 已证实的问题与修复

**实验结果：Cell/速度标签错误合并了后继不等价的状态。** 矩形碰撞夹具中，Basic 角色从 `(1600,1558)`、零速度出发；障碍左边 `1632`、宽 `128`、高 `32`；目标是玩家左边 X `[1645,1645.5]`、脚底 Y `1568`。原 `213bc909` 规划器在 150 次扩展后耗尽前沿，仅返回部分路线；保留精确状态的搜索找到五边路线，最后状态 `(1645,1526,0,0,true,0,true)`，逐帧重放及每条边的 sweep、checkpoint、完整制动尾均通过。

`PlannerCorrectnessTests.Reference` 独立枚举相同动作和安全规则，以有界 Dijkstra 对比原 Cell/速度支配规则与保留精确状态的规则；它不调用生产规划器，不用扩大世界或直接改位置来制造成功。另用原 HEAD 源码副本与最终规划器作直接对照，原始输出在 ZIP 的 `audit/counterexample.txt`。

两个真实生成的 checkpoint（hold=8/10）分别为 X `1644.88/1653.28`、Vx `2.64/2.88`，成本 `53/56`；它们落在同 Cell、同速度桶，原规则会剪去后者，但制动后位置相差超过 8px。相同起点、墙左边 `1632`、高 `200`、6 帧行走时，Vx `0.8` 有安全动作，Vx `3` 被阻挡；更快也不意味着后继能力包含更慢状态。

最小修复：Cell 仅作为查找分区；Label 保存完整 `PhysicsState`，只有成本不高于候选的**完全相同物理状态**才支配候选。去掉速度桶与位置折叠造成的支配，跳过已被更低成本替换的队列条目。`came` 不再在支配检查前覆盖更低成本记录。保留原帧成本、跳跃额外成本、动作枚举、搜索窗口和生产预算。

**事实：checkpoint 规则原本已经集中于 `SupportedCheckpoint`。** 本次在入队、部分路线和水平完成判据复用它：Grounded、低 Vy、ReleaseJump、JumpFramesLeft=0、SafeLanding 必须同时成立。允许有水平残速的交接仍需完整安全制动尾；区域最终到达继续要求 SafeFlatLanding 和停稳。未发现需要放宽 `MotionSupport`、`SafeSweep` 或感知候选规则的证据。原平台边缘、半砖、斜坡、窄井、不同高度、未知/危险和净空回归保留。

**实验结果：水平启发式不能宣称可采纳。** 已接受的 12px 到达带内，剩余动作成本可为零，但原 `distance/MaxRun` 为正；真实模拟一步从 Vx=2.99 加速也会超过 MaxRun=3，尚不涉及原生 StepUp/Down 位移修正。区域的 0/1 几何非成员指标有下界证明：几何外至少需要一条正成本边，几何内取零；跨边差值最多 1，因此一致。本次将水平完成谓词从 H 数值中解耦，明确水平 H 仅是既有 best-first 排序导向，不保证最短路。试过将其改为保守 0/1，但现有连续移动测试退化，故未采用此性能/行为变更；没有以放宽测试处理退化。

**已确认的契约缺陷：扩展上限原先可能按 `no_route` 获得失败后降级资格。** 新增内部诊断 `SearchExit`：`goal_reached`、`time_budget`、`expansion_budget`、`local_frontier_exhausted`，并保留 `ReachedGoal`、`TimeBudgetReached`、候选拒绝计数和精确状态支配次数。可行完整 incumbent 与退出原因独立；预算退出可以携带完整或部分路线。局部前沿耗尽也只表示当前动作/跨度内没有更多候选，不是全局不可达。

零路线的扩展预算退出映射为 `navigation_search_expansion_budget`，保留具体原因，不提供墙体证据或 `no_route` 恢复资格；原耗时原因不变。未知/危险等原因由真实预测拒绝计数保留，不把一次局部拒绝推断为整个世界不可达；能力不支持仍沿用既有 profile 拒绝。新字段为诊断快照的增量字段，没有新增原子 API。Python 回归验证即便伪造旧可恢复标记，新的预算原因也不能放行 Direct Input。

## 验证

| 层级 | 实际执行与结果 |
|---|---|
| 源码 | `PhysicsSimulator`、`NativeMotionWorld`、`MotionSupport` 无修改；manifest 保存哈希。没有新增 IL 挂钩或引擎支持 |
| 搜索定向 | `run_csharp_tests.ps1 -PlannerOnly` 最终 187 项检查通过；反例、同 Cell、速度支配、启发式、零预算、部分/完整预算路线、未知/危险、checkpoint 与逐帧重放 |
| 导航回归 | `-NavigationOnly` 通过（当时新增检查为183项；最后补充启发式/默认预算断言后 `-PlannerOnly` 为187项）；原落地确认、连续运动、取消、身份和玩家钩子检查保留 |
| 空间/高度 | `-SceneOnly` 2018 项通过；`-VerticalOnly` 的障碍、平台升降、不同高度、同X窄井与拒绝矩阵通过 |
| IPC/M3 | `run_server_ai_ipc_tests.ps1` 通过，含289项原生适配替身、64项任务运行时、30项聊天及其他协议检查 |
| Python | `python -B -m unittest test_navigation_recovery -v`，8项通过，包含新增扩展预算子场景及取消/unknown/释放/身份/共享预算检查 |
| 构建 | HEAD 0.1.75 基线包和最终0.1.76包均0错误、0警告；最终包 SHA-256 `f315858d245d275fabf684b1d513f887838a0db3e951088ca2e4c5093a1cda83` |
| 原生 | 固定自然世界、offset=0、双向 explore/direct，各4场景×6段；旧包24/24段，新包24/24段，全部真实到达、停稳、支撑及known输入释放通过；记录预测误差均为0，但到达另行核验 |

第一次普通沙箱 IPC 运行被 localhost 套接字权限拒绝；Python Worker 测试也在该环境卡住，停止本轮对应进程后在允许 localhost 的环境重跑通过。这是环境限制，没有修改产品门禁来通过测试。Native 使用新隔离 profile，正常退出并核验 teardown。

## 固定自然世界基线

源世界来自已有 `natural-navigation/raw-runs.zip` 中的 `world/source.wld`，seed `20261028`，SHA-256 `9f4665e8c6989bc5a523349e1bd4a303895814152170ff6cf9ebc7e862a54482`。旧包从原 HEAD 单独导出构建；两组固定使用原 HEAD 的 explore helper，不使用工作区中正在修改的 helper。两个进程的 world/actor/generation/slot 标识与生命周期在各自原始报告中；没有把跨进程标识当作相同 Actor。

| 路线 | 旧包/新包完成段 | 旧包/新包实际累计位移(px) | 两组各自规划次数 |
|---|---|---|---|
| 右向 explore | 6/6 | 871.117 / 871.117 | 18 / 18 |
| 右向 direct | 6/6 | 871.117 / 871.117 | 18 / 18 |
| 左向 explore | 6/6 | 758.133 / 758.133 | 21 / 21 |
| 左向 direct | 6/6 | 758.133 / 758.133 | 21 / 21 |

两组各78次内部局部规划；这不是新增的失败后重规划机制。回执仅保存每次动作**最后一次规划**的开销：两组这24个样本均最多4次扩展，最大耗时旧包8.0379ms、新包16.973ms。没有全部内部规划的逐次耗时，不能把这些数字作为最坏游戏线程tick或严格性能对照。新包最后规划样本中，10次 `state_space` 的退出均为 `goal_reached`；14次既有快速地面行走没有进入搜索，`SearchExit=null`。24次动作均 completed，无失败/预算终态；预算停止主要由离线确定性夹具覆盖。

两组使用相同的现有未提交 Python runtime；ZIP保留这些输入字节用于辨别验证环境，不把它们作为本轮源码变更提交。两组无Tile改写、无LLM、无战斗验收；一个seed的出生点附近不能代表复杂自然洞穴成功率。

## 复现与剩余风险

```powershell
pwsh -NoProfile -File tests/run_csharp_tests.ps1 -PlannerOnly
pwsh -NoProfile -File tests/run_csharp_tests.ps1 -NavigationOnly
# 需要允许 localhost / Worker
$env:PYTHONPATH="$pwd/tests;$pwd/python"
& .local/lua-venv/Scripts/python.exe -B -m unittest test_navigation_recovery -v
# 将历史 archive 的 world/source.wld 解压到新 .local 目录，
# 本 evidence archive 的 audit/baseline-explore.lua 用作固定 helper。
# 先构建到隔离输出，再执行；每次脚本创建新profile，不部署现用游戏。
& ./tests/verify_natural_navigation.ps1 -WorldPath <固定source.wld> `
    -HelperSource <固定baseline-explore.lua> -PackagePath <隔离CompanionBridge.tmod> `
    -Offsets @(0) -Port 27981 -IpcPort 27982
```

- 窄落点反例使用诊断扩展上限256、5000ms以隔离剪枝因素；实际首次实验使用2000ms也找到。最终规划约100ms、256次扩展后保存了完整incumbent；**默认32次扩展仍不能证明该目标完成**，正确报告 `expansion_budget`。没有乘大生产预算来掩盖局限。
- 精确状态保留会增加前沿和内存、可能更早触发既有时间预算；新包最后规划样本较慢。没有证明复杂地图上整体性能或成功率提高。
- Cell不再错误合并状态，不意味着有限动作枚举、90帧预测、128/192px窗口和默认预算下搜索完备。水平启发式仍非可采纳，不承诺最优。
- 斜坡/半砖/平台的空间规则已有定向夹具，但本轮新原生对照仅固定自然地形样本；未新增任意斜坡碰撞、未知原生Tile、窄落点或装备/Buff的原生适用性证明。
- M1～M3身份、租约、任务终态、取消、unknown与输入释放不变量保留；没有失败后自动重试、复活旧动作或执行恢复。M4.2须另行评审。

[summary.json](summary.json) 保存逐场位移、规划次数和最终规划样本；[manifest.json](manifest.json) 保存源码、引擎、世界、包和44份ZIP条目的SHA-256；[raw-runs.zip](raw-runs.zip) 无损保存新旧报告、原始回执、逐帧轨迹、生命周期与运行输入。未将历史结果改写为本次证据。
