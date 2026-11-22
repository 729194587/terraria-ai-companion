> **历史阶段记录**：本文保留对应阶段的设计、实现过程与当时验证结果。当前导航架构和能力说明见 [NAVIGATION.md](NAVIGATION.md)；本文中的分支、提交、待办和测试状态均属于记录时点。

# Phase 4.5 单钩爪攀升实现与验证

基线为 `codex/unified-autonomous-navigation` 上的 `583fa95`。设计见 [Phase 4.5 MVP](PHASE4_5_GRAPPLE_NAVIGATION_MVP_DESIGN_v0.1.md)。

## 原生机制核查

对照 tModLoader `2026.09.3.1` 和 `2026.08.3.0` 实际 DLL。两版相关 IL 一致。

| 原生环节 | 核查结果与接法 |
| --- | --- |
| 物品选择与发射 | `QuickGrapple_GetItemToUse` 优先检查钩爪装备栏，再检查库存。完整 `QuickGrapple` 含客户端瞄准和交互逻辑。服务端复用原生选择、物品使用检查、`CanUseGrapple`、`UseGrapple` 和物品来源，用已核验的原生 `Projectile.NewProjectile` 发射段显式传入 Actor slot 与几何瞄准向量。发射前检查空 Projectile 槽，防止原生满池替换旧 Projectile。 |
| 附着与同步 | `AI_007_GrapplingHooks` 的三个 owner 比较读取 `Main.myPlayer`，其中包括原生附着门。按两个固定引擎的既有指纹契约安装 IL 钩，只为本次、仍绑定的 Projectile 替换比较操作数。其他 Projectile 执行原比较。服务端显式发出发射和附着的 `SyncProjectile`，并在确认解绑后发出携带 identity 与 owner 的 `KillProjectile`。 |
| 原生牵引与解绑 | 附着时原生 Player.Update 跳过通常的 run-speeds 输入挂钩。只为受控钩爪使用 `GrappleMovement` 输入边界。附着后的上升来自原生牵引，基础钩实测每帧约 11 像素。上升中的原生跳跃按键边沿调用 `RemoveAllGrapplingHooks` 并保留上升惯性。要求该钩爪独占 Actor 的钩爪状态才走原生解绑分支。 |
| 身份与槽位复用 | 发射时绑定 world、Actor 对象、generation 和 slot。Projectile 另绑定对象、index、identity、owner、type 和每次 `SetDefaults` 重建的实例标记。失去绑定时只在同一世界退役仍精确匹配的旧 Projectile，避免读写失效 Actor，也避免其旧附着进入替换 Actor 的牵引路径。槽位已经复用时不发旧 identity 的删除包。 |

原生隔离验证分别在两版引擎上观测到 Actor `254` 的 Projectile 正确附着并带动 Player 上升，服务器 `Main.myPlayer` 保持 `255`，鼠标全局值保持不变。正式路径不修改这些全局值，也不额外调用 Player.Update。

## 局部导航接入

- 自主导航的连续落脚子任务在普通搜索明确无路线，或只交付没有上升的安全前缀时，检查一次垂直攀升候选。完整普通路线优先。搜索切片、资源耗尽和未知地形不作为触发信号。同一根行程最多发起一次钩爪尝试。
- MVP 只支持原版基础 Grappling Hook、基础或迅捷地面运动、正重力、干燥环境和无其他钩爪/空中能力。锚点位于 Actor 正上方，采用 280 像素的保守射程，低于基础钩的原生 300 像素范围。目标须是紧邻垂直通道的独立实体高台，升高范围 144 至 192 像素。预检与执行共同检查通道、整个人体碰撞箱、支撑和地形签名。
- 实际脱钩时保留原生上升速度。松钩前用观测到的牵引速度预检交接，松钩后用真实状态重新模拟水平移动与制动，复用 `PhysicsSimulator`、`NativeMotionWorld` 和 `LocalMotionController`。连续两帧无钩、安全、静止且在真实目标区域才结束局部动作，再由原导航和根任务分别核验到达。
- 发射、等附着、牵引、交接和落脚都受帧预算约束。可确认清理的失败回到现有中性输入和真实安全检查点恢复。绑定或清理无法确认时保留 `unknown`。

## 验证

隔离场景使用复制世界，无客户端、无模型。钩爪、地形和故障注入只出现在显式启用的测试夹具。未运行全量测试。

| 检查 | 结果 |
| --- | --- |
| 双引擎包构建 | 两版均 0 错误、0 警告 |
| C# `-NavigationOnly` | 通过。钩爪预检 12 项，Surface Graph、Phase 1 行程和恢复、Phase 4.1–4.3、局部改造和真实落脚门的离线回归通过 |
| 双包 `run_server_ai_ipc_tests.ps1` | 均通过。每版 45 项 IPC、92 项 Task Runtime、103 项包级 IL 检查，及脚本包含的原子动作与协议回归 |
| 双引擎 `verify_navigation_recovery_native.ps1 -Grapple` | 两版都通过原生发射证明和 10 个导航场景。高台实际到达，未装备和 Ivy Whip 未发射，锚点移除、射偏和超时清理后从真实检查点重规划，半空取消释放输入，两种 Projectile 槽位复用与 Actor generation 失效均返回 `unknown`。两种替换 Projectile 均在清理当帧保留 |
| 当前引擎 `-ExtraJump -Scenes cloud_root` | 根导航到达，观测到 1 次原生 Cloud 跳跃。另通过 474 项运行时运动发现检查 |
| 当前引擎 `-AutonomousNavigation -Scenes offset` | 原生偏移后完成 1 次恢复、1 次重规划，真实到达并确认释放输入 |

## 验证边界

当前只接入目的地对应的局部高台。夹具和实现没有覆盖在更远路径中任选中间钩爪落脚点。未验证图形 Host & Play、远端客户端收到的同步与视觉表现、真实换世界或热插拔其他玩家、第三方 Mod 改写原版钩爪行为。未做多钩、连续换钩、摆荡、平台边缘或与其他空中能力组合的实机验证。
