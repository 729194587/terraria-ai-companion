> **历史阶段记录**：本文保留对应阶段的设计、实现过程与当时验证结果。当前导航架构见 [NAVIGATION.md](NAVIGATION.md)。本文中的代码基线、待办和测试状态属于记录时点。

> **历史阶段记录**：本文保留对应阶段的设计、实现过程与当时验证结果。当前导航架构和能力说明见 [NAVIGATION.md](NAVIGATION.md)；本文中的分支、提交、待办和测试状态均属于记录时点。

# Phase 4.3 翅膀与火箭靴 MVP 实现记录

日期：2026-11-06。基线 `a1f96ec`，分支 `codex/unified-autonomous-navigation`。设计依据：[v0.2 文档](PHASE4_3_WINGS_ROCKET_BOOTS_MVP_DESIGN_v0.2.md)。

本次完成代码、离线与定向工程验证；遵守文档阶段边界，**未启动游戏世界、未运行原生飞行场景、未进行图形 Host & Play 或真实世界验收**。IL 核对和构建不能替代这些实际验证。

## 最小接入与范围

没有新增任务生命周期或通用特殊运动框架。仍使用 MobilityProfile / Tracker、PhysicsSimulator、StateSpacePlanner、Surface Graph、LocalMotionController、NavigationTask、原生碰撞适配及 Task Runtime。

新增小型 `AirMotionModel`，明确建模两种原生行为：

- 恶魔翅膀（功能翼槽 1）和天使翅膀（功能翼槽 2），原生基础 WingStats 为 FlyTime=100、AccRunSpeedOverride=6.25、加速度倍率 1、无特殊悬停。按功能槽位和动态基础数据选择，不扫描装备/物品 ID；基础数据改变或未知型号拒绝支持。
- 基础火箭靴：rocketBoots=1，只在没有功能性翅膀时参与导航。其他衍生型号与其跑速效果不作为本 MVP 的支持承诺。

能力策略严格以功能性 wingsLogic 为先：有翼时保持翼模式，时间耗尽仍滑翔或安全下降；同时有火箭靴也不选择推进模式。未知功能性翅膀会明确拒绝，不回退火箭靴；没有二者时继续原来的普通/ExtraJump 导航。

MVP 只接入基本或 Swiftness 数值物理配置。slowFall、empressBrooch、chilled 及无法验证的飞行物理组合拒绝参与该模型。与未阻止的 Enabled ExtraJump 组合暂不参与空中模型，避免同一输入被其他能力优先消费；原有独立 Cloud ExtraJump 支持保留。未增加坐骑、钩爪、绳索、液体或 Mod 特殊运动支持，G1 与引擎指纹不变。

## 预测、资源与真实状态核验

`PhysicsState` 增加不可变 AirResources，保存翼时/上限、火箭资源/上限、实际 rocketDelay、canRocket、rocketRelease 和 rocketFrame。完整值参与搜索标签、缓存、续接与失败记录；未舍弃位置、速度、跳跃计数或输入边沿。

WingTime 的正常逐帧消耗不会增加 Revision；容量改变、能力/物理改变或与执行计划不符的资源状态仍使旧计划失效。资源不因被发现而假定可用，实际捕获仍位于原 PostUpdateRunSpeeds 链。rocketDelay2 继续作为原生快照诊断字段采集，因其属于声音/视觉节奏而不进入物理搜索状态或推进资格判断。

预测流程保留完整 20×42 碰撞箱、原生 Collision、危险/未知覆盖、落差和落脚语义。新的有限飞行候选在普通候选后生成：普通起跳后松键、短段持续翼输入或一次火箭请求，再验证完整制动/落脚尾段。每段空中动作最多预测 180 帧；普通/ExtraJump 原有上限保持。Graph 只扩大候选连接范围，仍由物理模拟验证实际可行性。

- 翅膀：跳跃计数归零且原生条件允许时，持跳请求上升并消费翼时；松键使用正常下降；翼时耗尽后持跳请求原生滑翔。无新增控制通道或直接速度写入。
- 火箭：资格取决于资源、真实延迟和原生 release/canRocket 状态；一次请求启动有持续时间的脉冲，不视作持续飞行。即使松键，模型仍预测剩余推进时间。
- 执行 After 阶段同时检查资源/延迟、jump/release、位置与速度；AirFeedback 保存阶段、确认结果及预计/观察资源。未确认推进或偏差超过既有公差时立即进入现有释放、恢复和真实安全检查点流程。推进动画或按键请求不构成成功证据。
- 小幅翼飞行偏差可以从实际状态重模拟剩余轨迹，尝试前四帧控制修正；只有整个剩余路径和至少两个停稳支撑帧均验证通过才接纳。此修正有 3ms 时间片，未完成证明就保留原计划，仍须逐帧核验，不能接受部分未验证尾段。大幅偏差直接走既有安全恢复。
- 最终任务到达仍由真实位置、目标区域、安全支撑、停稳和输入释放决定。没有新增空中 A*、独立执行器或跳过落脚确认的成功路径。

## 核实原生机制后的调整

以下基于两个受支持版本的真实 tModLoader DLL / Mono.Cecil 检查，不通过执行扩展钩子推测：

1. 天使翅膀使用默认垂直分支：基础上升加速 .1、下降时额外加速 .5、上升时额外加速 .1、速度上限倍率 1.5、上升判断阈值倍率 .5，主动翼运动跳过普通重力；普通滑翔使用重力和最大下降速度的 1/3。基础 WingStats 不足以推断任意其他翼型，因此限定已核实模型。
2. WingAirLogicTweaks 在采集点之前把空中 accRunSpeed 提高到 6.25。模型保留统一的基础规划 profile，在实际空中阶段应用该值，避免地面/空中正常参数切换造成逐帧 InvalidateMobility。其他未验证参数仍拒绝，不靠放宽误差容忍。
   `ObservedPhysics` 同时保存未经规范化的原生边界数值，`EffectivePhysics` 表达规划基准，避免诊断把原生 6.25 隐藏成 3。
3. 原生火箭请求消耗一个 rocketTime，设置 rocketDelay=10，并在同一更新推进与递减；请求帧的更新后延迟是 9。松键不终止这段已启动的脉冲。安全恢复因此必须等待真实剩余推进结束，而非假定释放即静止。
4. rocketDelay2 不是推进冷却，它影响声音/视觉；不能用它阻止推进，也不能让它的逐帧变化重建物理搜索。
5. 功能性翅膀和火箭靴同存时，引擎会把剩余 rocketTime 按 ×6 转入 wingTime，再清空 rocketTime。模型如实表示这项原生资源转换，**导航策略仍是翅膀，未请求组合推进**。若装备切换时已有残留 rocketDelay，则拒绝该未建模组合，不假装能立刻停止旧脉冲。
6. 不额外调用 HorizontalWingSpeeds、VerticalWingSpeeds、WingGlidingSpeeds、AnyExtraJumpUsable 或其他扩展钩子。资格判断仍由正常原生更新执行，确认代码通过资源和运动观察识别结果。
7. Graph 的静态站姿、精确目标站姿和前三帧普通起跳净空探测没有 Actor 资源。它们继续用无推进的地面物理验证接触/净空，不为假想节点伪造飞行资源；完整连接仍使用真实起点、资源和完整飞行模拟。新增翼/靴连续 Graph 回归覆盖这一实际调用链差异。

## 修改文件

| 文件 | 修改 |
| --- | --- |
| `Motion/AirMotionModel.cs` | 两个模型、策略选择、物理资源、原生步进与有限空中反馈修正。 |
| `Motion/PhysicsSimulator.cs` | 原有状态/预测扩展，飞行/滑翔/脉冲步进及稳定检查点。 |
| `Motion/StateSpacePlanner.cs`、`SurfaceGraph.cs` | 有限空中动作和连接候选，维持原有增量搜索及预算。 |
| `Motion/NavigationKnowledge.cs`、`NavigationFailureMemory.cs`、`NavigationTask.cs` | 空中输入时机加入缓存/失败身份；从真实状态重新模拟续接。 |
| `Motion/GameNavigation.cs`、`NativeMotionWorld.cs` | 能力准入、规范化物理、原生资源观察及预期/异常状态处理。 |
| `Motion/MobilityProfile.cs`、`MovementSnapshot.cs`、`NativeMovementReader.cs` | 复用原采集并补充火箭状态，资源容量与规划版本比较。 |
| `Motion/LocalMotionController.cs`、`NavigationDiagnostics.cs` | 逐帧核验、反馈证据和阶段变化日志。资源消耗不会刷逐帧阶段日志。 |
| `ServerIpc/MovementDiscoveryFixture.cs` | 既有字段对照 fixture 兼容新增火箭快照字段；本轮未运行世界 fixture。 |
| `tests/AirMotionTests.cs`、`verify_air_engine_contract.ps1` | 新增空中行为离线专项与双引擎只读 IL 契约。 |
| `tests/run_csharp_tests.ps1`、`ServerAiPackageTests.cs` | AirOnly 定向入口、NavigationOnly 接入及编译包安全检查。 |

生产文件路径均以 `CompanionBridge/` 为前缀。文档更新了 INDEX、NAVIGATION，并保存本设计与实现记录。

## 验证结果

没有运行仓库全量测试；没有启动世界或请求模型。

| 命令/范围 | 结果 |
| --- | --- |
| `run_csharp_tests.ps1 -AirOnly` | 57 项通过：选择优先级、耗尽不切火箭、飞行/滑翔/下降、脉冲与真实延迟、不可触发条件、资源与版本、双向高台、未触发/偏移/接触后的恢复、完整落脚尾段修正及修正预算退出、危险与 unknown。碰撞均为明确离线替身。 |
| `-MobilityOnly` | Phase 4.1 快照 121、既有 Mobility 70、Phase 4.2 ExtraJump 28、Air 54 项通过；之后新增连续 Graph 两项与原始物理采样一项，在最终 AirOnly 中通过，共 57 项。快照新增原始字段使值类型白名单检查比 4.2 多 5 项。 |
| `-NavigationOnly` | Phase 1–3 回归通过：局部改造 143、Surface Graph 1281、Trip 68、Recovery 111、Planner 187、Perception 44 及 landing/motion/Player hook；含 8192 节点资源失败、滚动缓存和搜索续接。最终 runner 也接入独立 Air 专项，后续 AirOnly 已验证最新修改。Surface Graph 部分检查次数取决于实际完成的规划轮数，不代表删减场景。 |
| 最终 `-SurfaceGraphOnly` | 静态站姿探测调整后定向复测 1288 项通过；没有重复运行未变化的整套导航测试。翼/靴连续 Graph 实际状态接续另在 AirOnly 通过。 |
| `-MiningSupportOnly` | 30 项通过，保留当前站姿支撑移除诊断。 |
| Python `test_server_ai_landing.py` | 5 项通过，含真实回执核验、取消及 unknown 不重放；执行器是测试替身。 |
| `verify_air_engine_contract.ps1` | 两引擎各 21 项通过：天使翼注册数据、垂直系数、滑翔、火箭资格、脉冲/资源与原生字段。只读 IL，不执行 Player.Update。 |
| `verify_movement_engine_contract.ps1` | 两引擎各 7 项通过，保留 Phase 4.1/4.2 getter、ExtraJump 和禁止探测钩子的机制核对。 |
| `run_server_ai_ipc_tests.ps1 -PackagePath ...` | 两引擎包均通过 IPC 45、atomic 48、read 68、native adapter 300、Task Runtime 92、chat 30 及每包 100 项 IL 检查；无游戏世界。 |
| `build-companion.ps1` | `2026.09.3.1`、`2026.08.3.0` 均 0 errors / 0 warnings。只生成 `.local/` 包，未部署。 |

最终构建：

- `.local/phase4-3-build-current/Mods/CompanionBridge.tmod`：`0200E964425DE0C28869B09AEB0368BC5055A3297071C2B4162A1841C35F01EB`。
- `.local/phase4-3-build-previous/Mods/CompanionBridge.tmod`：`EF1C95E11574E0D56343B9A728E50FF0798D5785C35A95544D7B0DF75D46DAD6`。

## 剩余限制与阶段判断

代码与离线/定向工程验收覆盖 v0.2 的 MVP。运行时接入了真实状态确认，但**本轮没有通过原生飞行实测证明模型精度或成功率**。不能把 IL 检查、碰撞替身或构建通过写成游戏验收。

目前按相邻安全落脚动作组织飞行，不保证任意空中目标、全局最优或没有中间落脚面的任意长航程。保留现有原生碰撞窗口、64px 安全落差语义与局部搜索预算；不能在空中任意换目标或保证耗尽后总能找到安全着陆点。小幅反馈修正也必须在本地时间片内完成全尾段证明，未能完成时仍可能进入恢复。

尚未验证：两个引擎中的真实翼时/推进精度、低翼时与耗尽时的原生切换、翼靴同存的实际资源转换、复杂平台/斜坡/低顶棚、自然跨沟、延迟或敌人扰动。其他翼型、特殊悬停、火箭靴衍生型号、ExtraJump 联合规划和第三方 Mod 不在支持承诺内。

遵守设计，本轮不安排集中 Host & Play 或真实世界测试。Phase 4 全部子阶段完成后，再在隔离 profile 中核对实际加载包、Actor 身份和双引擎版本，按“天使翼单独、翼时耗尽、翼靴同存、基础火箭靴单独、触发失败、偏移与取消”的矩阵补齐原生与图形证据；届时才能扩展完成声明。

## 翼槽修正后的原生复测

在 2026.09.3.1 的复制世界使用同一座 8 格高台，分别装备天使翅膀（翼槽 2）和恶魔翅膀（翼槽 1）。两轮均实际上升，翼时从 100 降至 92，安全落地、停稳并释放输入。预测坐标均为 (33718.777, 3334)，观测坐标均为 (33720.473, 3334)，水平误差约 1.70 像素，垂直误差为 0。可用原生导航夹具的 `-ExtraJump -Scenes angel,demon` 重测。
