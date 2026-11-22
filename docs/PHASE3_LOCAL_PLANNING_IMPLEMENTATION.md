# Phase 3 v1.1：局部改造规划实现与验证

代码基线：`568a50d26790d0cc4198dea8c88a4040805ec909`，实现前 HEAD 与 [v1.1 简化版设计](terraria_navigation_phase3_unified_planning_v1.1.md) 一致。本阶段实现有限规则候选、独立虚拟地形及成本比较，没有增加通用联合 A* 或第二套执行运行时。

## 实现映射与复用

| 文件/模块 | 本次修改与复用 |
| --- | --- |
| `Motion/LocalTerrainPlanning.cs` | 新增只读源上的 `LocalTerrainView` 覆盖层、有序动作、分项成本、有限候选评估器。每个分支独立维护改动和材料计数。 |
| `NativeNavigationEdits` | 实现局部候选的原生准入与报价接口，继续检查目标、原生工具、距离、危险/家具/掉落物块、材料和支撑。只把所选第一项动作绑定到现有挖掘/放置校验字段。 |
| `LocalEditGeometry` | 从原开路代码抽取通道格子范围与单行下降结构判定；旧 `PlanAcross` / `CanDropAfter` 和新评估器共用，保留 `mine_target` 的原开路执行流程。 |
| `NativeNavigationWorld` | 替换旧的单步 `Cost/Waypoint` 比较，先评估有限改造，再在有必要时取得真实移动路线及成本，与候选比较。世界、真实完整状态、物理参数或物品状态变化时丢弃旧评估。 |
| `SurfaceGraph` / `SurfaceNavigator` | 继续提供真实地形路线，补充候选路线成本估价及“图候选是否到达目标”诊断；不读取虚拟改动。 |
| `AutonomousNavigationTask` | 接入可延续的规划等待、拒绝和取消清理。每次只启动一项原有内部子任务；真实改造、停稳或局部移动完成后重新观察并比较。 |
| `ServerAiNativeActions.ChildExecutor` | 成本探测和实际移动共享原有根行程 `NavigationKnowledge`，复用图缓存、搜索学习及执行失败记忆。 |
| `ActorMining.EstimateUpdates` | 使用原 `ChooseTool/Start` 相同的镐力伤害委托及选中槽位估算周期，不改变挖掘执行器的原生输入、伤害、身份和效果核验流程。 |

执行链仍为一次 `navigate_to(x,y)` → 根 Task Runtime → 串行移动/挖掘/放置子任务。Agent 没有新增选工具、造路或开路决策接口。`mine_target` 继续使用其既有目标保护、到达/射程核验和最终原生挖掘流程。

## 有限候选与虚拟地形

- 水平方向采用朝目标的最多 4 个单格通道段；可以包含连续挖掘、连续支撑放置，以及先挖后放。垂直方向保留最多 4 次有安全下一行支撑的下降规则。
- 每个候选最多 18 项真实地形改动。该限制只约束本次局部预览，不是整趟任务累计能力上限；实际执行后从真实状态生成新候选。
- 按当前可用的热栏支撑材料分别评估分支，并额外评估不放置分支；每个分支使用独立覆盖层和单栈材料余额。不合并同位置但不同改造历史的状态。
- `LocalTerrainView` 只保存计划移除/放置的格子，后续动作读取之前改动后的结构。`#` 与 `=` 保持不同碰撞语义；未知和危险格子不能改写为空气或安全支撑。
- 准入只支持可可靠建模的普通实体挖除与普通实体/平台支撑。拒绝未知/Mod 碰撞、掉落方块、危险材料、复杂家具及不受支持的原生放置类型，保留原邻域保护规则。
- 已发现材料不足、无法放置或安全规则不通过的尾段，不保留依赖它才能继续的未完成前缀。有限深度内仍是普通可挖障碍的长通道，可以作为有界部分候选；它不能压过已经完整验证的移动路线。

虚拟地形没有实现 `IMotionWorld`，也不写 `Main.tile`。所有虚拟方案都显式标记 `virtualNativeVerified=false`。它们的身体结构检查和工作位置只是估价前提，不能当作通过 Terraria 碰撞验证的路线。真实改造后，由共享图缓存重读实际 Tile，再由原 `StateSpacePlanner` / `PhysicsSimulator` / `NativeMotionWorld` 验证实际移动。

## 成本与选择

统一以预计 Game Updates 加显式附加惩罚比较：

`总成本 = 工作/通行移动 + 挖掘 + 放置 + 材料惩罚 + 改造惩罚 + 剩余路线估价`。

初始实现中，局部单格移动估为 `20 + 16 / MaxRun`；挖掘按同一工具对真实目标的原生镐力伤害计算预计击数，再按 `useTime` 估算周期；放置按 `max(useTime,useAnimation)+1`。每单位材料增加 8、每次改造增加 4，只有收益超过 8 才用改造取代可比较的移动路线。这些是可解释的初始权重，不是最优性或精确时序保证。

短程地面移动继续复用 `SafeGroundWalk`。其他移动方案通过现有 Surface Graph 和局部完整物理状态搜索取得：已到达目标的真实模拟路线按执行帧数计价；多段图候选使用图上的移动估价和当前安全前缀，明确保留 `movementVerifiedComplete=false`。仅走到障碍前的前缀不能被当成已证明的廉价绕行。

`PlanningEvidence` 包含移动成本、完整验证标志、候选数量、所选动作及分项成本、图诊断和最近 8 次决策。该历史记录规划意图；实际挖掘/放置数量仍仅由原生子动作的 verified effect 记账。

## 执行、时效与预算

局部评估每次最多 64 个工作单元、3ms 检查预算，移动成本搜索以另一个 3ms 检查片接续；没有把 Terraria Tile/Collision 调用移出游戏线程。时间片耗尽返回等待并保留游标，不被当作无路线；尚无结论时不启动改造。单个原生调用不可抢占，预算不是硬实时保证。

如果移动比较的总局部搜索额度仍无结论，保守地保留原生移动执行入口，诊断为 `movement_evaluation_incomplete`，不借预算耗尽判定绕行不存在。Phase 2 的 `surface_node_budget`、未知地形及不支持 profile 仍保持独立拒绝/unknown 语义；不绕过其安全停止去挖掘。

真实动作前继续使用 `plannedToolSlot` / `plannedPlacementSlot` 和原生准入，额外绑定所规划物品的引用、类型、前缀、能力、周期、材料和数量状态。首动作不再成立时不执行旧计划。改造后必须等待真实停稳、读取真实地形并重新规划；未确认的效果、外部目标替换和 unknown 均不能启动依赖动作。评估期间也复核运动 profile；取消时释放待评估搜索，原有 Actor/world/generation/slot、输入归属与原子效果核验保持不变。

本轮原生长距离挖掘暴露并修复了一个实际接续问题：图路径可能跳过当前已经占据的重叠 Surface，但失败连接原来只从逻辑起点排除，导致反复搜索同一条连接。排除现在也作用于当前真实身体已占据的 Surface，仍只属于这次真实起点搜索，不增加第二套失败记忆。新增全部动作被拒绝的重叠 Surface 回归确认搜索会有限结束；未放宽行程时间或节点预算。

## 定向验证

未运行仓库全量测试。源码/离线与原生证据分别如下：

- `tests/run_csharp_tests.ps1 -LocalEditsOnly`：143 项通过，涵盖连续改造、组合成本、短绕行/大绕行/小收益、材料不足、不可放置、工具缺失、危险材料和地形、覆盖层隔离、源地形不变、实体/平台支撑及预算续接。
- `-NavigationOnly`：包含上述测试和 Phase 2 Surface Graph、滚动缓存、8192 节点资源停止、重叠 Surface 连接排除，以及 Phase 1 planner/trip/recovery/landing/mobility/motion/Player hook 定向回归，通过。
- `tests/run_server_ai_ipc_tests.ps1`：新增规划等待、取消清理、unknown/资源拒绝不启动后续动作；Task Runtime 92 项及 IPC、原子动作、读取、chat/native adapter 和 91 项包/IL 检查通过。
- `-MiningSupportOnly`：30 项通过；Python `test_server_ai_landing.py`：5 项通过，涵盖目标改变、取消、真实到达与 unknown 禁止重放。
- `scripts/build-companion.ps1 -OutputPath .local/phase3-local-edits-build`：0 errors / 0 warnings，不部署到现用 profile。

隔离原生验证使用 tModLoader `2026.09.3.1`，通过已有脚本复制世界并启动独立端口，不调用模型：

| 本地证据（不提交 `.local/` 临时产物） | 已执行结果 |
| --- | --- |
| `companion-g81-native-8a3cb8bd200a4579a0f592a3910540f5/result.json`；`verify_server_ai_native.ps1 -Actions -Tasks -Placement -LandingRegions` | 34 个 mine_target + 23 个自主导航/故障场景、独立放置及 20 场双向落脚矩阵符合预期。包含 18 个成功移动/改造旧场景、2 个新增成功选择/组合场景、3 个新安全负例。 |
| 同 profile 的 `mine-target-results.json` | 短绕行方案原生模拟完整，计价 67 updates，实际无改造；组合方案依次执行 3 次真实挖掘、移动、1 次平台放置及移动后到达。缺材料时 0 挖掘/0 放置；外部替换后 `target_changed`；结果未知后原子 `unknown` / Lua `action_unknown`，只有 1 个未完成挖掘子动作，无后续放置/移动，输入释放且不允许自动重试。 |
| 同 profile 长距离场景 | 27 次实际挖掘、10 次实际平台放置后完成；所选完整假想序列未被一次性提交。每趟仍是一项外部动作。 |
| `navigation-recovery-native-a2da882d56674128bb7559590ff35d28/recovery-results.json` | offset / air 完成，均有实际恢复与重规划；cancel_air 取消。真实安全检查点、失败记录、输入释放、取消和身份断言通过。 |
| 最终包 `companion-g81-native-591d792fbfe242deb3a971d6d87f92cf/result.json` | 23 个自主导航/Phase 3 场景及独立放置复测通过；成本选择、先挖后放、长距离改造、材料不足、目标变化和 unknown 均保持预期结果。 |
| 最终包 `natural-native-d9883fb7d6364772b75d7f715825eedb/result.json` | 自然地形 4 次真实目标到达；2 次无所需高度的观察候选而跳过，未算作成功。 |

前两批任务/恢复原生轮次包 SHA256 为 `A0FB9380CA5BABF55F81E12C37F58D73E8FD1E9DBDA0B8864AFCD65D14F79C93`。补充评估期间运动 profile 复核的最终包 SHA256 为 `6871F39523D18DFD884CC759740C866FB9855CC2200D7DB7F12945C7E6783839`，对应上表最终包专项复测；最终包也通过上述包/IL 定向检查。20 场双向落脚矩阵中 18 次到达、2 次超高平台安全失败，测试均符合预期。

## 阶段判断与限制

实现达到 v1.1 简化版的局部规划分工：有限连续改造参与成本比较，虚拟变化按顺序独立评估，真实效果通过既有执行器确认，每次只执行下一项并从真实状态继续。没有用虚拟路线替代原生碰撞，没有修改真实 Tile 来模拟方案。

第一版仍保守：从安全平坦、整 Tile 脚部高度的真实站姿评估改造；不规划向上挖掘、斜坡/半砖重塑、液体或动态地形模拟。每条候选只使用一种热栏材料栈，暂不合并背包/多个材料栈；放置预览保守要求当前原生 Simple 交互距离，超范围工作在实际走近后重新考虑。完整挖掘伤害累积、复杂邻域更新和未来移动只做有限结构/时间估计，不能保证全局最优或任意长的改造组合都能发现。

尚未做图形 Host & Play、复杂自然世界性能基准和完整四阶段验收；没有实现 Phase 4。上述代价和模板长度后续应按实际场景调整，当前证据不推广为全世界可达保证。
