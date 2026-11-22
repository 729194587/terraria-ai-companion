> **历史阶段记录**：本文保留对应阶段的设计、实现过程与当时验证结果。当前导航架构见 [NAVIGATION.md](NAVIGATION.md)。本文中的代码基线、待办和测试状态属于记录时点。

> **历史阶段记录**：本文保留对应阶段的设计、实现过程与当时验证结果。当前导航架构和能力说明见 [NAVIGATION.md](NAVIGATION.md)；本文中的分支、提交、待办和测试状态均属于记录时点。

# Phase 4.5：原版单钩爪攀升导航（MVP）设计 v0.1

**状态：** 实施前设计，原生发射路径待验证

**代码基线：** `codex/unified-autonomous-navigation`，`583fa959468abc282163f1bdd9bce67fc25245e9`

**适用引擎：** 项目当前支持的 tModLoader `2026.09.3.1`、`2026.08.3.0`

**前置：** Phase 4.1–4.3；Phase 4.4 坐骑暂缓

## 1. 目标与范围

在现有自主导航过程中，当普通行走、跳跃等已支持动作无法安全连接上方高台时，允许导航**自动选择并执行一次原版普通单钩爪攀升**，最终站稳在经验证的高台落脚区，再返回现有导航继续规划。

本阶段只实现局部高台攀升：选择钩爪/锚点 → 验证空间 → 原生发射 → 确认附着与牵引 → 松钩/短跳 → 验证安全落脚。首个验证对象优先采用原版基础 Grappling Hook；其他原版单钩爪只有在相同机制和参数经过核验后才纳入。

不扩展到多钩爪、连续换钩、横向摆荡、特殊瞬移钩爪、任意 ModProjectile 或与翅膀/火箭靴的联合规划。不开设独立 AI 操作接口。

## 2. 已有基础和原生研究

- `MovementSnapshot.GrappleSnapshot`、`NativeMovementReader` 已经采集活动 Projectile 的索引、type、identity、owner 和钩爪类型标记；**尚未解决未发射时可用钩爪物品的识别**。
- `GameNavigation.OrdinaryRejection` 当前以 `grapCount != 0` 拒绝钩爪状态，Phase 4.5 需要仅对已受控的钩爪动作放行，普通导航的保守门不能整体删除。
- `ServerAiNavigation` 与 `ActorControl` 已有专属 Actor、游戏线程、唯一原生 `Player.Update`、控制释放、真实落脚确认；`NavigationTask` / Surface Graph / Task Runtime 已有任务生命周期与恢复。
- TerraBot `BotFollowGrapple.cs` 提供“发射—牵引—松钩跳跃—落脚”参考，但其 `Fire` 会设置 `Main.mouseX/Y`，并通过 `BotCompatibility.RunWithBotAuthority` 临时设置 `Main.myPlayer`。**不直接移植这种全局状态切换。**
- tModLoader 1.4.4 的 `Player.cs.patch` 显示 `QuickGrapple` 附近存在 `ProjectileID.Sets.SingleGrappleHook`、`ProjectileLoader.CanUseGrapple/UseGrapple` 和鼠标世界坐标依赖；牵引中会使用 `ProjectileLoader.GrappleTargetPoint/GrapplePullSpeed`。钩爪附着/牵引不应以自研逐帧物理替代。

**实施门槛：** Codex 必须先对照两个固定引擎版本的实际 `Player.QuickGrapple`、`QuickGrapple_GetItemToUse`、Projectile 生成/同步、`Player.Update` 钩爪处理及解绑流程，验证是否存在不切换 `Main.myPlayer` / 鼠标全局状态的、具有正确 Actor 归属的专用服务器调用方式。源码补丁和公共 API 文档不足以独立证明服务器可发射；需要隔离原生测试证据。若无安全可行路径，应记录阻塞原因，不通过全局身份伪装强行完成。

参考：
- 本仓库：`CompanionBridge/Motion/{MovementSnapshot,NativeMovementReader,GameNavigation,NavigationTask,SurfaceGraph}.cs`；`CompanionBridge/ServerIpc/ServerAiNavigation.cs`。
- 用户提供的 TerraBot：`TerraPlayerBot/BotFollowGrapple.cs`、`BotCompatibility.cs`。
- tML 1.4.4 补丁：<https://github.com/tModLoader/tModLoader/blob/1.4.4/patches/tModLoader/Terraria/Player.cs.patch>。

## 3. 局部动作设计

### 3.1 候选与安全预检

仅在可控、活着、正常重力、未被禁用动作、没有冲突运动模式的 Actor 上尝试；已有未归属当前动作的钩爪状态时不接管。只考虑实际装备或携带的、已经验证支持的原版单钩爪。原版物品选择顺序、发射前使用限制、射程和 Projectile 类型以固定版本原生实现为准，不能仅凭 `grapCount` 推断钩爪可用。

从当前已观察地形寻找可附着锚点和**独立的安全落脚区**；约束射程与瞄准线、身体牵引通道、台沿越过空间、完整 20×42 玩家碰撞箱、落脚支撑和局部危险/未知地形。锚点可附着并不代表高台可站立。首版遇不确定的机关、复杂斜坡、平台边界或特殊钩爪行为，宁可拒绝候选。

沿用现有 Surface Graph / 局部规划器选择短的、具有明确落脚区的攀升候选；不把仅凭几何成立的钩爪边当作已经物理验证的普通路径，也不在 A* 中反复运行真实 `Player.Update`。

### 3.2 有界状态流程

| 阶段 | 核心动作与通过条件 |
| --- | --- |
| `Prepare` | 验证 Actor、可用钩爪、锚点、射程、通道、落脚区与局部资源 |
| `Fire` | 通过已核实的服务端原生路径发射一次，关联本次动作的 Projectile |
| `Latch` | 在期限内观察同一 Actor 的 Projectile 类型、index、identity、owner、有效附着状态；未附着或身份不一致即失败 |
| `Pull` | 让原生机制牵引，持续检查真实位置/速度、锚点及支撑变化；不自行写入位置/牵引速度 |
| `ReleaseAndClimb` | 满足安全边缘条件时释放本次钩爪，按需短跳并水平校正，禁止无限悬挂或无限重试 |
| `Settle` | 钩爪已解除、输入释放、Player 真正落在目标区域、支撑安全且位置/速度稳定，才完成动作 |

每个阶段采用有限帧预算和整体超时；发射失败、附着失败、阻挡、超时、装备变化、Actor/World/Generation 变更、死亡和取消，必须释放受控输入及本动作钩爪，并交给既有恢复与重新规划机制。**解除钩爪不等于已经安全落地**；仍在空中或无法确认落脚时不得报告 `completed`，应使用现有 `unknown` / 安全恢复语义。

### 3.3 与现有导航的衔接

- 钩爪作为局部移动候选，由现有 `navigate_to_*` 路径在普通路线不可安全通过时自动选用；成功后回到真实状态并继续常规规划。无需新增面向 LLM 的工具。
- 只对本次受控攀升过程豁免原有 `grapCount` 不支持门；其余活动钩爪、异常状态仍保持拒绝。
- 不绕过现有 Actor 所有权、主线程、任务取消、Task Runtime、网络同步、Terrain/Revision 变化和失败记忆。Projectile index 会重用，必须结合 owner、identity、type、当前作用域验证。
- 异常时优先遵循当前安全恢复；不能以“松开控制键”代替钩爪清理，也不能将半空中的任务结束标记为安全完成。

## 4. 验收标准

1. **原生门槛：** 两个固定引擎版本均有明确的发射/同步/解绑调用链核验；隔离原生测试证明发射 Projectile 属于 Server AI Actor，且不需要修改 `Main.myPlayer`、`Main.mouseX/Y` 或手动额外 `Player.Update`。
2. **能力边界：** 无钩爪、未支持/多钩爪、无射程内锚点、射线被挡、无安全高台、地形未知等情况安全拒绝，继续沿用既有导航/局部改造失败语义。
3. **有限攀升：** 在确定性夹具中能够完成一次发射、确认附着、原生牵引、释放与高台落脚；最终到达必须由真实 Player 状态确认，不由计划预测或 Projectile 创建结果代替。
4. **失败恢复：** 钩爪未生成、射偏、超时、锚点/地形变化、半空取消、Actor 失去所有权和 Projectile 槽位重用，均不会误报成功、遗留输入或损坏其他玩家的 Projectile。
5. **回归：** 不装备钩爪时 Phase 1–4.3 既有普通/额外跳跃/飞行导航不回退；两个引擎构建、原生钩子契约、导航/Task Runtime/IPC 定向检查通过。

按照总体 Phase 4 安排，本轮只要求代码、离线与隔离原生夹具验证，不要求集中图形 Host & Play 验收。报告应区分“构建通过”“发射路径已在隔离原生场景验证”与“真实世界玩法已验证”。

## 5. 实现约束

Codex 根据现有代码选择最小的状态表示和接入位置，不预先规定新类或特定寻路算法。应先核实原生服务端发射路径，再实现有限攀升；若研究结果与本设计不同，以两个固定引擎的真实运行行为为准，并在实现记录中解释调整。
