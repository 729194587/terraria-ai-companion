> **历史阶段记录**：本文保留对应阶段的设计、实现过程与当时验证结果。当前导航架构见 [NAVIGATION.md](NAVIGATION.md)。本文中的代码基线、待办和测试状态属于记录时点。

> **历史阶段记录**：本文保留对应阶段的设计、实现过程与当时验证结果。当前导航架构和能力说明见 [NAVIGATION.md](NAVIGATION.md)；本文中的分支、提交、待办和测试状态均属于记录时点。

# Phase 4.1 MVP：原生运动能力发现与状态采样

日期：2026-11-06。设计依据：[Phase 4.1 v0.1](terraria_navigation_phase4_1_design_v0.1.md)。实现基线为 `35906041ad6dc17ac897c5eccdd3b672cb49463e`，分支 `codex/unified-autonomous-navigation`。

## 实际实现与复用

- `MovementSnapshot.cs`：只含数值、布尔、字符串和不可变记录组件；ExtraJump、Grapple 的输入与输出数组均复制。保留全部已加载 ExtraJump 的 FullName 和原生顺序，不按装备模板或类型白名单筛选。
- `NativeMovementReader.cs`：游戏线程直接读取注册表和当前 Player 状态。功能性翅膀使用 `wingsLogic`，复制当前槽位的 WingStats 基础字段；火箭靴记录资源；坐骑保留 Active、Type、ModMount.FullName；当前钩爪保留列表序号、运行时索引、有效性、active、type、identity、owner 和原生 projHook 标记。无效索引和不完整列表保留诊断信息。绳索、四种液体状态单独记录。
- 扩展现有 `MobilityProfile` / `MobilityTracker`，没有新增平行能力管理器。仍在 `ProbePlayer.PostUpdateRunSpeeds → ActorControl.BeforeMovement → ServerAiNavigation.BeforeMovement → NativeNavigation.Before` 中采样；SourceTick 使用该次 Main.GameUpdateCount。Ordinary 的预检也只读原生状态，不发布额外预测采样。
- `GameNavigation` 删除 `AnyExtraJumpUsable()` 准入调用：任何 Active 都拒绝普通模型；Available 且没有 blockExtraJumps 也拒绝；只有 Available、但被 blockExtraJumps 阻止时，不由这个标志单独拒绝。Available 并不证明 CanStart 会通过。原有功能性翅膀、火箭靴、坐骑、钩爪、绳索及其他不适用状态的保守门保持；补齐独立液体标志。
- 模式仍只有已支持的 `ground_walk`、`ground_jump`。特殊运动不会因为被发现而获得预测或输入执行能力。原有物理模拟器、StateSpacePlanner、Surface Graph、改造规划、原子执行器和 Task Runtime 不改动。
- 注册项顺序/身份/状态、block、功能槽位及基础参数、火箭靴启用、坐骑、钩爪身份、环境、有效物理参数、尺寸和 session/generation 的变化递增 Revision。A→B→A 不复用旧版本；Reset 不重置递增计数。当前 wingTime/rocketTime 及其资源上限只随帧采样，不进入当前普通模型的版本比较；未来使用特殊资源的模型必须另建预测状态。
- 支持范围内的 Revision 变化继续走现有 `InvalidateMobility`、释放输入和恢复重规划。新出现的未建模状态沿用已有失败/释放流程，不尝试特殊输入，也不自动复活终态或 unknown。Actor/world/generation/slot、取消、控制归属和 G1 引擎隔离均保持。
- `navigation_mobility` 仅在新任务或 Revision 改变时记录前后 Mobility；单独的资源消耗和 SourceTick 不刷日志。现有 get_navigation / 任务证据自然携带新增快照，无新增 Agent 决策接口。

## 与实际引擎核对后的调整

通过两个安装目录内的真实 tModLoader DLL / Mono.Cecil 核对，而非仅依据类型名推断：

1. `AnyExtraJumpUsable` 在两版均调用 `ExtraJump.CanStart` 和 `PlayerLoader.CanStartExtraJump`，故不适合作为只读采集/准入入口。
2. `ExtraJumpState.Enabled` 的公开 getter 为 `_enabled && !_disabled`；Available、Active 还受 Enabled 门控。快照保存公开 getter 返回的**有效原生状态**，不通过反射读取私有 backing 字段，不制造游戏对外并未提供的独立状态。
3. 两版 WingStats 的实际公开基础字段是 FlyTime、AccRunSpeedOverride、AccRunAccelerationMult、HasDownHoverStats、DownHoverSpeedOverride、DownHoverAccelerationMult；从动态 `ArmorIDs.Wing.Sets.Stats` 数组复制。后续 WingMovement 局部计算的垂直参数未采集，不调用 HorizontalWingSpeeds、VerticalWingSpeeds、WingGlidingSpeeds 等钩子补齐。
4. 数字 Type、槽位、Projectile index 仅表示本次运行时查找值；ModType 使用 FullName，钩爪 identity 也与当前 Actor / 任务作用域一起解释，不声称可跨世界持久化。

## 定向验证

没有运行仓库全量测试，没有部署到用户现用 profile，没有调用收费模型。原生检查使用复制世界、独立端口和现有 Player 更新流程。

| 验证 | 结果 |
| --- | --- |
| `run_csharp_tests.ps1 -MobilityOnly` | 新增快照 116 项、原有 Mobility 70 项通过。覆盖顺序/身份、状态组合、输入输出复制、类型白名单、离散版本、资源、ABA、未建模能力及前后诊断。 |
| `run_csharp_tests.ps1 -NavigationOnly` | Phase 1–3 定向回归通过：局部改造 143、Surface Graph 1283、Trip 68、Recovery 111、Planner 187、Perception 44，以及 landing/motion/Player hook 检查；包含 8192 资源停止及滚动缓存。此轮快照测试为 68 项，后续新增类型白名单后以 MobilityOnly 复测为 116 项。 |
| `run_csharp_tests.ps1 -MiningSupportOnly` | 30 项通过。 |
| Python `test_server_ai_landing.py` | 5 项通过。首次沙箱轮次卡在 localhost 测试，停止后在允许本机 socket / Worker 的权限下重跑成功。 |
| `verify_movement_engine_contract.ps1` | 两个引擎各 4 项通过：真实 getter 只读及 AnyExtraJumpUsable 钩子调用链。 |
| 双引擎 `build-companion.ps1` | `2026.09.3.1`、`2026.08.3.0` 均 0 errors / 0 warnings。 |
| 双引擎 `run_server_ai_ipc_tests.ps1 -PackagePath ...` | IPC、atomic/read/native adapter、Task Runtime、chat 通过；每包 96 项 IL 检查通过。新增检查限制采集器原生调用白名单、禁止 AnyExtraJumpUsable、确认唯一原生边界且无额外 Player.Update / 原生字段写入。 |
| 双引擎 `verify_navigation_recovery_native.ps1 -Mobility ...` | 每个引擎 13 个场景全部完成，预测误差 0；覆盖普通/Swiftness、跳跃、双向落脚区、增加/移除/快速 ABA/自然到期、数值效果及装备。实际到达、停稳、输入释放与恢复断言通过。 |
| 双引擎新增原生字段 fixture | 各 474 项通过，在同一个 PostUpdateRunSpeeds 边界对照当前原生注册项与字段；诊断注入在移动前恢复，不增加 Player.Update。覆盖功能/视觉翼槽区别、资源下降、ExtraJump Enable/Available/Disable、无效及有效 Projectile 索引、绳索与液体字段。 |
| 每帧原生快照核验 | 对上述全部 26 条已有原生 trace 运行新增 `check_movement_trace`：当前引擎 1780 帧、上一引擎 1783 帧通过；SourceTick 等于实际 inputPhysics.sampleTick，顺序和身份完整，未新增特殊模式。该断言已加入后续原生 Mobility 运行入口。 |

当前引擎的 `verify_server_ai_native.ps1 -Actions -Tasks -AutonomousNavigation -Placement -LandingRegions ...` 通过：34 个 mine_target 场景、23 个自主导航/Phase 3 场景、独立放置及 20 个双向落脚场景（18 次到达，2 次超高平台安全失败）。短绕行、连续挖掘、组合桥接、材料不足、目标变化和 unknown 均符合预期，未将预期失败或 unknown 算作成功到达。独立 `-AutonomousNavigation` 恢复回归也通过：offset / air 均恢复一次并重规划一次后到达，cancel_air 按请求取消，三者输入均已释放。

本地证据不提交：

- 当前引擎：`.local/navigation-recovery-native-aa35107e5dbb4a58b4e93b1310b4f387/`。
- 上一引擎：`.local/navigation-recovery-native-fec421f2ebac4e589d960e23ead0095e/`。
- 当前引擎 Phase 1–3 / mine_target / 放置：`.local/companion-g81-native-d9e9022eba3a458f95d8fdb571895f82/result.json`（57 条任务记录、20 条落脚场景、result=passed、exitCode=0）。
- 当前引擎恢复/取消：`.local/navigation-recovery-native-f3c5c9bc60a0481fadd920861c79cfdb/recovery-results.json`。
- 两目录均有 `movement-discovery.json`、13 条 `mobility-results.json` 记录、对应 trace、动作回执及包/world SHA256 manifest。首次字段 fixture tick 为 0（Actor 创建期原生更新）；正式导航逐帧采样由后续 trace 独立证明。
- 当前包 `.local/phase4-1-build-current/Mods/CompanionBridge.tmod`，SHA256 `4716D99F561D46E7CB126099F7505E040E0D685F02641F0F247ED83487D7404B`。
- 上一包 `.local/phase4-1-build-previous/Mods/CompanionBridge.tmod`，SHA256 `2E6087951C1185C3CABB0F07D991C686E3E87FA6652158776D4669DCE4788F7A`。

## 尚未验证与阶段边界

G1 未放开，未加载第三方 Mod，未增加测试 ExtraJump 注册项。因此 ModMount.FullName / 第三方 ExtraJump 和自定义钩爪的真实注册运行仍未验证；枚举路径本身没有原版类型过滤，离线覆盖同名不同 Mod、增删和重排。

原生 fixture 的部分字段由明确门控的诊断注入验证，不能当成真实飞行、激活坐骑、钩爪牵引或液体运动验收；ExtraJump Active=true 的兼容门组合由离线快照测试覆盖，没有执行特殊跳跃。Available 的真实 CanStart 可行性刻意不求值。未进行图形 Host & Play、特殊能力轨迹预测、特殊输入或完整 Phase 4 验收。

源码、离线、双引擎构建/包检查及隔离原生证据满足 Phase 4.1 MVP 标准。交付范围是可信的能力/状态采样、比较和保守准入；发现能力与导航支持保持分离。
