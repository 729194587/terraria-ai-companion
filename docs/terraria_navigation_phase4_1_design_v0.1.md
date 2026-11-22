# Terraria 自主导航系统 Phase 4.1 设计文档 v0.1

**阶段名称：** 动态运动能力发现与状态采集\
**文档状态：** 设计草案，待审查\
**前置阶段：** Phase 1--3\
**架构依据：**《Phase 4 总体架构设计 v0.1》\
**代码基线：** `codex/unified-autonomous-navigation`，提交
`35906041ad6dc17ac897c5eccdd3b672cb49463e`

------------------------------------------------------------------------

## 1. 目标与边界

Phase 4.1 建立导航系统的运动能力感知基础，使系统能够从
Terraria/tModLoader 当前运行时读取角色实际具备的运动能力及其状态。

本阶段负责：

-   读取游戏已经计算出的有效物理参数；
-   动态枚举原生注册体系中的能力；
-   采集当前玩家的能力状态、资源状态和环境状态；
-   将这些信息整合进现有 `MobilityProfile`；
-   检测会影响规划的能力变化，并复用现有导航失效与恢复机制。

本阶段**不实现特殊运动的预测和导航**。普通跳跃、ExtraJump、飞行、坐骑、钩爪等运动模型仍由后续子阶段完成。

本阶段完成后，导航系统能够准确描述"角色当前有什么、状态如何"，但不代表已经能够使用所有这些能力规划路线。

## 2. 研究基线与主要发现

### 2.1 当前项目实现

当前 `MobilityProfile` 已记录：

-   有效水平运动参数；
-   重力和最大下落速度；
-   普通跳跃参数；
-   角色宽高；
-   `AvailableModes`；
-   `UnsupportedEffects`；
-   `Revision`、`SourceTick`、Session 和 Generation。

当前 `AvailableModes` 只有 `ground_walk` 和
`ground_jump`。`MobilityTracker`
主要比较物理参数、角色尺寸、变体和不支持原因，没有逐项记录动态运动能力。

`GameNavigation.OrdinaryRejection()`
目前通过若干原生状态字段拒绝尚未支持的运动状态，包括额外跳跃、翅膀、火箭靴、坐骑、钩爪、绳索和液体等。

其中有一个需要在本阶段修正的问题：

`Player.AnyExtraJumpUsable()` 不只是读取状态。它会遍历额外跳跃，并调用
`ExtraJump.CanStart()` 和
`PlayerLoader.CanStartExtraJump()`。导航每次采样时再次调用它，会额外执行
Mod 扩展钩子。

能力采集应当是只读观察，不应为了发现能力而重复执行游戏的扩展逻辑。

### 2.2 tModLoader 没有统一的运动能力注册表

tModLoader 将不同能力分散在不同原生子系统中：

  -----------------------------------------------------------------------------------------------------------------------
  能力类别                原生来源                                           4.1 的采集范围
  ----------------------- -------------------------------------------------- --------------------------------------------
  额外跳跃                `ExtraJumpLoader.OrderedJumps`、`ExtraJumpState`   枚举全部注册项并读取玩家状态

  翅膀                    `EquipLoader`、`WingStats`、Player 翅膀字段        读取当前功能性翅膀槽位、基础参数和飞行资源

  火箭靴                  Player 原生字段                                    读取当前启用状态和剩余资源

  坐骑                    `MountLoader`、`Mount.mounts`、`MountData`         读取当前坐骑状态和类型

  钩爪                    `ProjectileLoader`、`Main.projHook`、Player        读取当前活动钩爪的身份
                          活动钩爪列表

  绳索                    Player 的 `pulley` 状态                            读取当前附着状态

  液体                    Player 环境字段                                    读取当前液体环境
  -----------------------------------------------------------------------------------------------------------------------

因此，本阶段采用一个统一的玩家运动状态快照，但底层通过少量、直接的原生数据读取来完成。暂时不建立通用插件注册框架，也不通过反射扫描任意
Mod 代码。

### 2.3 额外跳跃注册机制

当前支持的 tModLoader 版本提供：

-   `ExtraJumpLoader.OrderedJumps`：当前注册的全部额外跳跃，顺序也是原生处理顺序；
-   `Player.GetJumpState(jump)`：读取玩家对应跳跃状态；
-   `ExtraJumpState.Enabled`：当前是否启用；
-   `ExtraJumpState.Available`：资源是否尚未消耗；
-   `ExtraJumpState.Active`：是否正在执行；
-   `ExtraJump.FullName`：内容身份，可用于区分不同 Mod 的同名内容。

`Available` 不等于"当前一定能启动"。原生处理还会调用 `CanStart` 和
`PlayerLoader.CanStartExtraJump`。4.1
只记录状态，不自行调用这些钩子，也不推断其返回结果。

必须保留 `OrderedJumps` 的顺序，不能自行按名称或类型重新排序。

### 2.4 翅膀、坐骑和钩爪

**翅膀：**

-   `WingStatsInitializer` 根据 `EquipLoader.nextEquip[EquipType.Wings]`
    创建动态长度的翅膀参数数组；
-   `Player.wingsLogic` 是功能性翅膀槽位，不能与仅用于视觉表现的
    `Player.wings` 混淆；
-   `wingTime` 和 `wingTimeMax` 可描述当前飞行资源；
-   `ItemLoader.HorizontalWingSpeeds`、`VerticalWingSpeeds` 和
    `WingGlidingSpeeds` 会调用扩展钩子。

水平翅膀参数已在当前 `PostUpdateRunSpeeds`
采集点之前参与速度计算。部分垂直飞行参数则在后续 `WingMovement`
中通过局部变量计算，不能由采集器再次调用相关钩子获取。

**坐骑：**

-   `MountLoader.MountCount` 和 `MountLoader.GetMount(type)`
    提供动态注册信息；
-   `Mount.mounts` 是包含注册坐骑数据的数组；
-   当前玩家状态通过 `Player.mount.Active` 和 `Player.mount.Type` 获取。

`MountLoader` 的运动和效果方法可能执行 `ModMount` 自定义代码。4.1
只读取当前状态和必要的注册身份，不主动调用这些方法。

**钩爪：**

-   `ProjectileLoader` 会扩展 `Main.projHook` 数组；
-   使用默认钩爪 AI 的 ModProjectile 会被标记为钩爪；
-   Player 的活动钩爪列表可以反映当前正在使用的钩爪。

自定义 Projectile 可以实现任意
AI，因此仅凭注册类型不能推断完整牵引行为。4.1
只采集当前活动钩爪的基本身份，不读取或调用其自定义运动钩子。

### 2.5 不能自动发现任意 Mod 自定义行为

`ModPlayer`、`ModItem`、`ModMount` 和 `ModProjectile`
可以通过扩展钩子执行自定义逻辑，甚至直接改变玩家速度或位置。

tModLoader 没有一个能够枚举所有任意自定义运动行为的统一 API。

因此，4.1 的自动发现范围是：

-   tModLoader 提供标准注册体系的能力；
-   Player 当前公开的实际运动状态；
-   游戏已经计算出的有效物理参数。

不能承诺自动理解任意 ModPlayer
代码。超出标准机制的行为需要未来的显式适配或专门模型。

### 2.6 当前运行环境对 Mod 支持的限制

当前项目 `EngineHooks.Load()` 有 G1 隔离检查，只允许当前探针 Mod 和
tModLoader 加载。

这意味着当前服务器进程不能加载第三方 Mod 内容。即使 4.1 使用动态注册
API，当前运行环境也无法验证第三方 Mod 注册的 ExtraJump、ModMount 或
ModProjectile。

**4.1 不修改 G1 隔离规则。**
本阶段只保证采集器能够枚举当前进程实际加载的原生注册项。第三方 Mod
的真实加载与验证，需要在后续单独审查 G1 隔离架构后决定。

## 3. 设计方案

### 3.1 扩展现有 MobilityProfile

继续使用现有 `MobilityProfile` 和
`MobilityTracker`，不创建平行的运动能力管理系统。

在 `MobilityProfile`
中增加一个只包含值类型、字符串和数组副本的运动状态快照，例如
`MovementSnapshot`。

建议包含以下信息：

**A. ExtraJump**

每个 `ExtraJumpLoader.OrderedJumps` 项生成一条记录：

-   `FullName`；
-   原生顺序索引；
-   `Enabled`；
-   `Available`；
-   `Active`。

同时记录 `Player.blockExtraJumps`。

**B. Wings**

记录当前功能性翅膀状态：

-   `wingsLogic`；
-   `wingTime`；
-   `wingTimeMax`；
-   当前槽位的 `WingStats` 基础字段。

不把视觉翅膀槽位当成运动能力来源。

**C. Rocket Boots**

记录：

-   `rocketBoots`；
-   `rocketTime`；
-   `rocketTimeMax`。

**D. Mount**

记录：

-   是否激活；
-   当前 Mount 类型；
-   如果是 ModMount，记录其内容身份。

4.1 不复制完整 `MountData` 对象，也不调用 Mount 的运动钩子。

**E. Grapple**

记录当前活动钩爪的基本身份：

-   活动数量；
-   对应 Projectile 的运行时类型；
-   Projectile identity。

只保存值，不保存活动 Projectile
对象引用。暂不记录完整锚点几何和牵引轨迹。

**F. Environment**

将绳索和液体作为环境运动状态单独记录：

-   `pulley`；
-   `pulleyDir`；
-   `wet`；
-   `honeyWet`；
-   `lavaWet`；
-   `shimmerWet`。

这些状态不是装备能力，不应混入 ExtraJump 或 Wing 的能力列表。

### 3.2 身份和数据安全

-   对 `ExtraJump` 等 ModType 内容，使用 `FullName` 作为稳定内容身份。
-   数字 Type、槽位和 Projectile index
    仅作为当前运行时的查找值，不作为跨会话的持久身份。
-   不保存 `Player`、`Item`、`ModType`、`Projectile`
    等可变游戏对象引用。
-   采集结果必须是当前游戏帧的值快照，供导航任务和诊断读取。
-   不扫描物品 ID 或 Buff ID 来推断移动能力。
-   不扫描整个背包来推断所有潜在坐骑或钩爪。可用物品的搜索与主动使用属于后续对应运动阶段。

### 3.3 采集时机

沿用现有 `ProbePlayer.PostUpdateRunSpeeds()` →
`ActorControl.BeforeMovement()` 的采集路径。

该位置的优势：

-   已完成当前帧的基础装备和效果计算；
-   `ExtraJumpLoader.UpdateHorizontalSpeeds` 已参与水平速度计算；
-   `ItemLoader.HorizontalWingSpeeds` 已参与水平速度计算；
-   随后即将执行水平移动；
-   当前项目已有的 `grapCount`
    检查也在此时读取，避免在后续清理后丢失活动状态。

不新增独立更新循环，不重复调用 `Player.Update`。

采集器只读取状态，不调用
`CanStart`、`UpdateEffects`、`VerticalWingSpeeds`、`GrapplePullSpeed`
等扩展钩子。

### 3.4 能力发现与导航支持分离

本阶段采集到的能力不自动变成可用导航模式。

继续保持：

-   `AvailableModes` 只包含当前已经验证的 `ground_walk` 和
    `ground_jump`；
-   尚无可靠运动模型的特殊能力仍然属于未支持状态；
-   `PhysicsSimulator`、`StateSpacePlanner` 和 `SurfaceGraph`
    不因能力被发现而自动扩展。

采集快照用于状态识别、诊断和后续阶段接入，不代表 Phase 4.1
已支持飞行、二段跳或钩爪导航。

### 3.5 ExtraJump 的保守兼容门

移除导航采集路径对 `Player.AnyExtraJumpUsable()`
的额外调用，改为读取快照中的原始状态。

在特殊跳跃模型尚未实现时，保守判定：

-   任意 ExtraJump 正在 `Active`：当前普通地面模型不适用；
-   任意 ExtraJump `Available` 且
    `blockExtraJumps == false`：视为可能影响导航，暂不使用普通地面模型；
-   `blockExtraJumps == true` 且没有正在执行的
    ExtraJump：不因可用标志单独阻止普通地面模型。

`Available` 不代表原生 `CanStart` 一定通过。4.1
不调用自定义钩子来确认这一点，因此可能出现保守拒绝，但不能因此错误地把未建模能力当成已支持能力。

### 3.6 Revision 与动态资源分离

必须区分每帧状态采样与规划版本变化。

**`SourceTick`：** 每次采集更新，表示快照对应的游戏帧。

**`Revision`：** 只在影响当前规划假设的离散状态发生变化时递增，例如：

-   注册能力列表或原生优先顺序变化；
-   ExtraJump 的 Enabled、Available、Active 状态变化；
-   功能性翅膀槽位或火箭靴启用状态变化；
-   Mount 激活状态或类型变化；
-   活动钩爪身份变化；
-   绳索附着或液体环境模式变化；
-   现有有效物理参数或角色尺寸变化。

连续资源值不应单独导致每帧 Revision 变化，例如：

-   `wingTime` 逐帧减少；
-   `rocketTime` 逐帧减少；
-   活动钩爪的位置和速度逐帧变化。

这些值仍然放在每帧快照中。未来某个运动模型真正使用它们时，再将其纳入该模型的搜索状态和资源预测。

继续复用
`NavigationTask.InvalidateMobility()`：当规划版本发生实质变化时，按现有安全流程释放输入、恢复或重新规划。

## 4. 实施范围

本阶段预计主要涉及：

-   `MobilityProfile`；
-   `MobilityTracker`；
-   `GameNavigation` 的状态采集和普通运动兼容门；
-   当前 `PostUpdateRunSpeeds` 采集调用链；
-   运动状态诊断；
-   对应离线测试和原生运行时采集测试。

不要求修改：

-   `PhysicsSimulator` 的运动方程；
-   `PhysicsState` 的特殊运动预测状态；
-   `StateSpacePlanner`；
-   `SurfaceGraph`；
-   Task Runtime 的动作接口；
-   AI 对外工具接口。

如果实现过程中发现某个字段必须由后续阶段才能可靠读取，应记录为明确的未采集项，不为凑齐统一结构而调用不安全的扩展钩子。

## 5. 验证与验收标准

### 5.1 离线测试

至少覆盖：

1.  ExtraJump 快照能够保留注册项的完整顺序和稳定身份。
2.  `Enabled`、`Available`、`Active` 各种组合能够正确表达。
3.  快照数据不会引用或暴露可变游戏对象。
4.  注册项、离散状态变化能够递增 `Revision`。
5.  `SourceTick` 和连续资源值变化不会导致每帧 `Revision` 递增。
6.  A → B → A 的状态变化仍然产生单调递增的版本。
7.  能力被发现但未建模时，不会自动加入 `AvailableModes`。
8.  原有 MobilityProfile、普通地面运动和 Phase 1--3 定向回归不退化。

### 5.2 原生运行时检查

通过现有隔离服务器和玩家更新流程检查：

1.  ExtraJump 快照与
    `ExtraJumpLoader.OrderedJumps`、`Player.GetJumpState()`
    读取结果一致。
2.  翅膀、火箭靴、坐骑、活动钩爪、绳索和液体状态与当前 Player 字段一致。
3.  采集器不调用 `AnyExtraJumpUsable()`、`CanStart()` 或其他 Mod
    扩展钩子。
4.  采集发生在现有 `PostUpdateRunSpeeds` 边界，不额外执行
    Player.Update。
5.  状态变化诊断能够说明变化前后的能力状态，不产生逐帧资源变化日志洪泛。

如果需要验证动态注册路径，可以考虑在当前探针 Mod 内提供测试用
ExtraJump；不要求为了本阶段解除 G1 隔离或加载第三方 Mod。

### 5.3 构建和回归

-   验证当前 EngineProfiles 支持的两个 tModLoader 版本。
-   运行 MobilityProfile 和导航相关离线测试。
-   运行现有 Phase 1、Phase 2、Phase 3 定向回归。
-   通过 IPC、包检查和构建。
-   本阶段不要求最终图形 Host & Play
    导航验收；完整真实验收仍在整个导航系统实现后进行。

## 6. 明确不属于 Phase 4.1 的内容

-   二段跳轨迹预测和跳跃动作规划；
-   飞行、翅膀、火箭靴的运动模型；
-   坐骑、钩爪和绳索的运动模型；
-   任何新运动方式的原生输入执行；
-   扫描所有背包物品并决定使用哪件装备；
-   任意 ModPlayer 自定义运动代码的自动语义识别；
-   第三方 Mod 加载架构变更；
-   AI 直接决定跳跃、飞行或钩爪时机。

## 7. 参考实现与源码

### 当前项目

-   [MobilityProfile.cs](https://github.com/729194587/terraria-ai-companion/blob/35906041ad6dc17ac897c5eccdd3b672cb49463e/CompanionBridge/Motion/MobilityProfile.cs)
-   [GameNavigation.cs](https://github.com/729194587/terraria-ai-companion/blob/35906041ad6dc17ac897c5eccdd3b672cb49463e/CompanionBridge/Motion/GameNavigation.cs)
-   [ProbeSession.cs](https://github.com/729194587/terraria-ai-companion/blob/35906041ad6dc17ac897c5eccdd3b672cb49463e/CompanionBridge/ServerPlayerRuntime/ProbeSession.cs)
-   [EngineHooks.cs](https://github.com/729194587/terraria-ai-companion/blob/35906041ad6dc17ac897c5eccdd3b672cb49463e/CompanionBridge/ServerPlayerRuntime/EngineHooks.cs)
-   [EngineProfiles.json](https://github.com/729194587/terraria-ai-companion/blob/35906041ad6dc17ac897c5eccdd3b672cb49463e/CompanionBridge/EngineProfiles.json)
-   [MobilityProfileTests.cs](https://github.com/729194587/terraria-ai-companion/blob/35906041ad6dc17ac897c5eccdd3b672cb49463e/tests/MobilityProfileTests.cs)

### tModLoader 当前目标引擎源码

目标引擎版本对应源码提交 `4f072c7a2aaad0c35ef7455f5195c526f2a9066a`：

-   [ExtraJumpLoader.cs](https://github.com/tModLoader/tModLoader/blob/4f072c7a2aaad0c35ef7455f5195c526f2a9066a/patches/tModLoader/Terraria/ModLoader/ExtraJumpLoader.cs)
-   [ExtraJumpState.cs](https://github.com/tModLoader/tModLoader/blob/4f072c7a2aaad0c35ef7455f5195c526f2a9066a/patches/tModLoader/Terraria/DataStructures/ExtraJumpState.cs)
-   [ModType.cs](https://github.com/tModLoader/tModLoader/blob/4f072c7a2aaad0c35ef7455f5195c526f2a9066a/patches/tModLoader/Terraria/ModLoader/ModType.cs)
-   [PlayerLoader.cs](https://github.com/tModLoader/tModLoader/blob/4f072c7a2aaad0c35ef7455f5195c526f2a9066a/patches/tModLoader/Terraria/ModLoader/PlayerLoader.cs)
-   [WingStats.cs.patch](https://github.com/tModLoader/tModLoader/blob/4f072c7a2aaad0c35ef7455f5195c526f2a9066a/patches/tModLoader/Terraria/DataStructures/WingStats.cs.patch)
-   [MountLoader.cs](https://github.com/tModLoader/tModLoader/blob/4f072c7a2aaad0c35ef7455f5195c526f2a9066a/patches/tModLoader/Terraria/ModLoader/MountLoader.cs)
-   [ModProjectile.cs](https://github.com/tModLoader/tModLoader/blob/4f072c7a2aaad0c35ef7455f5195c526f2a9066a/patches/tModLoader/Terraria/ModLoader/ModProjectile.cs)

项目同时支持的上一引擎配置为 `2026.08.3.0`，对应 API 也核对了
ExtraJump、WingStats、MountLoader、ModType.FullName 和
PostUpdateRunSpeeds 等本阶段依赖。

### 架构参考

-   [Numen
    BodySnapshot](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/numen/pathing/src/main/java/com/dwinovo/numen/pathing/plan/BodySnapshot.java)
-   [Numen
    Mobility](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/numen/pathing/src/main/java/com/dwinovo/numen/pathing/plan/Mobility.java)
-   [Numen
    Kinematics](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/numen/pathing/src/main/java/com/dwinovo/numen/pathing/world/Kinematics.java)

Numen
值得借鉴的是不可变状态快照与预测逻辑分离，而不是照搬其固定能力字段。

TerraBot 的 `BotAirNavigation`、`BotFollowDevices` 和 `BotFollowGrapple`
可用于核对 Terraria 原生状态字段和执行行为；其 `BotMobilityProfiles`
的固定装备模板及按具体 ExtraJump 类型判断的方式不作为本阶段设计依据。

------------------------------------------------------------------------

**阶段完成定义：**
导航系统能够在现有原生更新流程中，生成可靠、只读、可比较的角色运动能力快照；识别能力状态变化；但不会因为发现了能力就提前宣称支持该能力的自主导航。
