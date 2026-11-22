# Terraria AI Companion：原生执行路径架构审计与重构方案

**文档类型：** 长期架构设计与重构路线图\
**参考项目：** TerraBot\
**目标项目：** Terraria AI Companion\
**审计基准：** `codex/unified-autonomous-navigation` 分支，提交
`f2716914`（2026-11-16 审计快照）

------------------------------------------------------------------------

## 1. 重构目标

本次重构的目标不是复刻 TerraBot，也不是全面重写现有系统，而是优化
Terraria AI Companion 与 Terraria 原生游戏机制之间的执行关系。

核心原则：

1.  有合适的 Terraria 原生玩家行为入口时，优先复用原生入口。
2.  没有合适的玩家行为入口，但存在原生 API 时，优先调用原生 API。
3.  两者都不适用时，才编写专门适配。
4.  自主规划、任务管理、安全判断、效果核验和结果回执仍由 Companion
    自己负责。
5.  不为了消除 Hook 而破坏 Dedicated Server AI Player
    的身份隔离和安全边界。

重构重点是减少重复实现 Terraria
游戏规则的代码，而不是减少所有自定义代码。

## 2. 执行架构划分

将每种操作划分为四个职责：

-   **决策层：** 决定目标、工具、位置和操作策略。
-   **执行层：** 尽可能进入 Terraria 原生玩家行为路径。
-   **适配层：** 解决 Dedicated Server、AI Player
    和原生入口之间的兼容问题。
-   **核验层：** 观察实际世界变化、库存变化、目标状态和输入释放情况。

四个职责应保持清晰，避免执行适配与任务决策相互耦合。

## 3. TerraBot 对照审计结果

### 3.1 已经具备合理架构的模块

  模块               当前实现                                  结论
  ------------------ ----------------------------------------- ------
  Actor 生命周期     ProbeSession、NativeSurvival              保留
  移动执行           ActorControl、ServerAiDirectInput         保留
  导航规划           NativeNavigationWorld、SurfaceNavigator   保留
  掉落物拾取         NativeSurvival、ServerDrops               保留
  背包操作           ActorInventory                            保留
  箱子操作           ActorContainer                            保留
  合成               ActorCrafting                             保留
  多格物体放置核验   ActorObjectPlacement                      保留

这些模块已经有自己的合理设计，不应仅仅为了模仿 TerraBot 而重写。

### 3.2 需要优化的模块

#### A. 统一原生物品输入上下文

参考 TerraBot： - `RunBotItemCheckAsOwner` - `BotAutonomy.UseAt` -
`ItemCheckWrapped`

当前实现： - `ActorItemUse` - `ActorPlacement.CheckItem` -
`NativeCombatWorld.CheckItem`

问题：物品使用、放置和战斗分别维护部分虚拟输入环境。

目标：提取可复用的原生 `ItemCheck` 输入作用域，统一 Actor
身份、虚拟鼠标、屏幕坐标、Tile 目标和全局状态恢复。

不统一各个任务的生命周期和业务逻辑。

#### B. 挖掘与砍树

当前 `ActorMining`
自己管理原生挖掘方法调用、工具使用周期、范围检查和目标破坏观察。

参考 TerraBot 的
`BotAutonomy.UseAt`，优先考虑使用统一虚拟鼠标环境进入原生
`Player.ItemCheck()`。

保留： - 工具选择和镐力、斧力计算。 - 目标站位和导航。 - 批量挖掘。 -
原生破坏事件观察。 - 目标身份和实际效果核验。

目标是减少绕过完整原生物品使用路径的执行代码。

#### C. 战斗

当前 `NativeCombatWorld`
主要支持普通近战武器，弹射物、弹药和其他武器类别存在限制。

参考 TerraBot： - `BotCombatAgent` - `BotWeaponReach` -
`BotWeaponProfile` - `BotWeaponAmmoPolicy` - `BotCombatSafety`

目标：逐步扩展原生 `ItemCheck` 支持范围。

阶段方向： 1. 弓和枪。 2. 弹药及弹幕。 3. 魔法武器。 4. 召唤武器和哨兵。
5. Boss 和多段 NPC。

保留 NPC 实例绑定、攻击目标核验、伤害证据和危险中断机制。

#### D. 门交互

当前导航与动作实现缺少明确的通用原生开门能力。

参考 TerraBot 的门交互实现，调查并使用 Terraria 原生
`WorldGen.OpenDoor`。

目标： - 检测可交互门。 - 尝试原生开启。 - 观察实际门状态。 -
成功后重新规划导航。 - 失败时返回明确结果，不无限重试。

#### E. 抓钩

当前 `ActorGrapple` 已有专门的抓钩适配和攀爬逻辑。

调查 Terraria 原生 `Player.QuickGrapple()` 等入口，判断能否减少手动
Projectile 创建或其他重复执行逻辑。

保留抓钩实例追踪、附着确认和攀爬核验。

此项先审查，再决定是否重构。

#### F. 右键物品使用

当前 `ActorItemUse` 缺少通用右键物品操作能力。

参考 TerraBot 的 `ItemLoader.CanRightClick` 和 `ItemSlot.RightClick`。

调查 Dedicated Server
下的可用入口，支持宝藏袋、板条箱等原生右键物品行为。

注意原生 UI 全局状态依赖，不能未经验证直接将客户端 UI 入口用于服务器。

#### G. 放置预检查

P4-B 已经使用虚拟鼠标和原生 `Player.ItemCheck()`。

后续检查 `ActorPlacement` 和 `ActorObjectPlacement`
中重复实现的原生放置规则。

保留多格占地、支撑、Frame、Chest 记录和实际物品消耗核验。

不重复重构已经完成的原生放置执行路径。

## 4. 重构实施顺序

  阶段   任务                           初始状态
  ------ ------------------------------ --------------
  R0     原生物品输入上下文抽取         待审查/设计
  R1     挖掘与砍树改用原生 ItemCheck   已确认待重构
  R2     扩展原生战斗武器支持           已确认待重构
  R3     增加原生门交互                 普通木门已通过隔离专服验收
  R4     审查并优化抓钩执行             已审查，保留显式 Actor 发钩路径
  R5     增加右键物品使用               宝藏袋与板条箱已通过专服验收
  R6     清理放置重复预检查             已完成隔离专服定向验收

每个阶段独立实施、测试和验收。阶段之间允许根据实际代码依赖调整顺序。

### R2-d 原生仆从与哨兵（2026-11-18）

开发基线为 `codex/unified-autonomous-navigation@3fc4e23`。以下结果来自 tModLoader
`2026.09.3.1` 的隔离 Dedicated Server，使用复制的世界，无真人客户端或模型请求。

| 当前选中的武器 | 原生施放和存续 | 战斗效果证据 |
| --- | --- | --- |
| 史莱姆法杖（Slime Staff） | 原生消耗 10 魔力，产生 Baby Slime、对应 Buff，实测占 1 个仆从槽 | 仆从本体接触伤害进入原生 NPC 命中回调 |
| 寒霜九头蛇法杖（Staff of the Frost Hydra） | 原生消耗 20 魔力，在角色附近部署 Frost Hydra，实测哨兵容量为 1 | 哨兵原生 AI 产生 Frost Blast Friendly；核验父弹幕来源和所有者后记录冰弹伤害 |

`combat_attack` 与被动防御共用原生输入。每项任务可尝试一次部署，同武器已有可信本体时直接复用，即使当前魔力不足以施放。召唤槽、哨兵容量、Buff、原生替换与消失规则由引擎执行。当前策略维护一个同型本体；多槽配装、全自动补召、其他仆从与哨兵、鞭、模组武器、Boss 和多段 NPC 均在本阶段支持范围外。

- **施放与命中。** `NativeCombatWorld.cs` 经 `NativeItemInput`、`ItemCheckWrapped` 和 `ItemCheck_Shoot` 施放。`progress.summon` 分别记录输入尝试、进入原生发射、已观察到的本体身份、仆从 Buff 及子弹幕来源计数。部署情况只描述已采样的状态。击败仍要求同一 NPC 实例的原生致死命中与终态确认；`lastHit.Summon` 增加弹幕类型、所有者、弹幕身份、父身份、生成帧和目标 epoch。字段由 `CombatController.cs` 和 Python 结果 Schema 共同定义。
- **服务器权限。** `CombatBallistics.cs` 在核验本体或子弹幕的原生 `Projectile.Update` 内借用 owner 身份。寒霜九头蛇本体是无接触伤害的 sentry；引擎在本体 AI 中检查 `Main.myPlayer == owner`，再用 `GetProjectileSource_FromThis()` 发射冰弹。仆从接触伤害按命中帧的任务目标核验证据，冰弹的任务归属固定在生成帧。本体的原生聚焦目标和全局玩家索引在退出作用域时恢复。缺少父来源证明的同期冰弹计入未确认观察，不授予伤害归因。
- **生命周期。** 取消或 IPC 断开后释放输入，保留原生召唤物。任务外的原生攻击行为见下方 R2-D.1.1。后续任务可重新使用同一绑定下的可信本体。实测死亡后史莱姆与 Buff 消失，观察期内哨兵仍存在且失去授权。注入 generation 变化时两个本体仍存在，旧身份均无法继续行使权限。实际 IPC 断开与 Actor 代次变更分别验证。
- **定向验证。** `tests/native_combat.py` 通过 Lua/IPC 战斗入口或任务协调器验证原生扣费、实体、同步包调用、满容量复用、后续击杀、取消、双目标防御、NPC 槽复用，以及近战、弓枪和魔法回归。独立生命周期场景在运行 `tests/verify_server_ai_direct_input_native.ps1 -Combat` 前分别设置 `COMPANION_COMBAT_CASE` 为 `summon-death`、`summon-generation`、`summon-disconnect`。本次通过的构建包 SHA-256 为 `49C590BE6B01A5FF2F8653A796FDC534F0AD4CA66F6AA002DD1310AD5982BC23`。远端客户端的渲染、收包和 UI 显示仍待实测。

### R2-D.1.1 任务外自主攻击

基于 `4986016`。Baby Slime 与 Frost Hydra 的冰弹持有生成时的 Actor 绑定和原生弹幕身份。在绑定仍有效时，NPC 命中交由 Terraria 判定。活动任务仅在更新期间临时指定焦点，命中回执仍须匹配任务、NPC 实例和目标 epoch。任务外生成的冰弹没有任务归属；已发射的冰弹保留生成时的归属。取消、正常结束和 IPC 断开均不遣散召唤物。角色死亡或 generation 改变后停止授予旧身份原生权限。

**隔离 Dedicated Server 实验**（tModLoader `2026.09.3.1`）。在取消和正常完成后分别生成新的 1800 HP NPC，观察到 Baby Slime 的接触伤害和 Frost Hydra 新冰弹造成的实际掉血；再次查询原任务得到相同的终态回执。已有本体随后可复用。跨任务用例在诊断 fixture 中将旧任务的原生冰弹延迟至新任务开始，再恢复原生更新。冰弹击杀同一 NPC，新任务返回 `combat_target_invalidated`，`effectVerified=false`。同一构建通过了目标 epoch、NPC 槽位复用及既有近战、弓枪、魔法战斗回归。包 SHA-256 为 `99FB1858CE11C1F68A4E90A15AD655C8ADF2CB94427AF30034D2274F7300BA91`。本次未进行真人客户端或全量回归验证。

## 5. 不属于本次重构范围的内容

以下模块原则上保持现有架构：

-   Actor 身份与 Generation 管理。
-   Reservation 和主线程约束。
-   NativeSurvival 生命周期。
-   现有自主导航规划器。
-   原生移动物理执行。
-   掉落物原生拾取。
-   背包和箱子数据操作。
-   装备合法性检查。
-   当前合成执行架构。
-   多格家具和箱子效果核验。

如果后续发现明确问题，再单独讨论。

## 6. Hook 与原生机制使用原则

不以 Hook 数量作为重构成功标准。

允许保留： - Dedicated Server 兼容 Hook。 - Actor 身份隔离 Hook。 -
原生执行结果观察 Hook。 - 原生路径缺少服务器 AI 入口时必要的适配 Hook。

优先移除： - 重复实现原生游戏规则的代码。 -
可以通过原生玩家行为入口完成的独立执行循环。 - 不再需要的 IL 注入和
DynamicMethod 克隆。 - 与原生机制重复的状态维护。

### R2-E Boss 与多段 NPC（E0–E4 的隔离实测及逐 Boss 胜负见专题）

[专服自主 Boss 战斗设计](R2_E_BOSS_ENCOUNTERS.md)记录了单目标战斗审计、TerraBot 对照、Encounter 根任务与共享执行器的边界，以及隔离专服的分阶段验收条件。

### R3 普通木门与导航（2026-11-19）

专服握手声明 `server_ai_doors_v1` 时，Lua 可通过 `open_door({x,y})` 和 `close_door({x,y})` 指定普通木门占用的绝对 Tile。检查角色中心到目标格中心的 80 像素距离。游戏线程调用 WorldGen.OpenDoor 或非强制 CloseDoor，检查门轴三格及展开门扇，广播变更区域。已处于请求状态时返回 changed=false。

水平导航及目的地导航遇到行进方向上可及的关门时，先尝试向前开启，受阻后最多试一次反向。开门成功即丢弃旧路线，重新规划至原目标。两侧都堵住时返回 door_blocked。导航的到达回执仍核验角色站位和脚下支撑。测试用门后固定目的地代表队友所在位置，本轮未接入真人客户端。

隔离专服 2026.09.3.1 的 15 条动作回执记录了重复开门、左右开关、按展开门扇关门、越距拒绝、从开门距离外接近，以及 navigate_to_x 和 navigate_to 的通过与受阻终态。目的地任务实际在 X=33711.996 停稳，脚底覆盖指定 X=33728。两侧受阻时角色留在起点 X=33616。原始回执位于 .local/direct-input-native-63d02d2165014053a94b8bcf9da2e9ff/door-results.json。复现命令：

```powershell
pwsh -NoProfile -File scripts/build-companion.ps1 -OutputPath .local/r3-build
pwsh -NoProfile -File tests/verify_server_ai_direct_input_native.ps1 -TestScript native_door.py -PackagePath .local/r3-build/Mods/CompanionBridge.tmod
```

### R4 抓钩入口复核（2026-11-19）

在固定的 2026.09.3.1 引擎上复核 Player.QuickGrapple 的 IL：末端 NewProjectile 确实以当前 Player.whoAmI 为 owner，但入口读取共享交互键状态，前置分支读取 Main.myPlayer，发射方向取决于 Main.mouseX、mouseY 和 screenPosition。整个方法没有接受显式世界瞄点的入口。继续使用 ActorGrapple.Fire 的原生物品选择、准入、发射 Hook 及 NewProjectile，并显式传入绑定 Actor 和目标方向。维持实例标记与生命周期清理。[现有执行路径与历史验证](PHASE4_5_GRAPPLE_NAVIGATION_IMPLEMENTATION.md)

本轮复制世界定向复测使用 R3 构建。发钩 owner=254，专服本地槽为 255。ledge 记录发射、附着、脱钩、停稳到达；cancel 在空中牵引时取消并释放输入；same_reuse 在槽位、owner 和 identity 相同的替代弹幕出现后返回 unknown，保留替代实例。原始回执位于 .local/navigation-recovery-native-500166fadad54c6890ef7c11428b9354。本轮仅复核 2026.09.3.1 的普通单钩。

### R5 在专服打开背包物品（2026-11-19）

握手声明 `server_ai_open_item_v1` 时，用 inspect_inventory 的 slots 模式读取普通背包第 0–49 槽，再调用 `open_inventory_item({sourceSlot=slot,sourceToken=row.inventoryToken})`。每次打开一件原版 Boss 宝藏袋或钓鱼板条箱。装备右键和手持武器的副功能属于其他操作。

2026.09.3.1 的 ItemSlot.RightClick 选择本地玩家，TryOpenContainer 的尾部写入鼠标释放标记、堆叠计时器并刷新 UI 配方。专服只执行与绑定角色有关的部分：按物品分类调用 Player.OpenBossBag 或 Player.OpenFishingCrate，然后运行原生右键钩子及消耗判定。完成要求原槽减少一件，并观察到本次调用中 EntitySource_ItemOpen 指向同一角色和容器类型的实际掉落。drops 表示生成的物品，拾取需要另外核验。发生部分效果或无法核验时返回 unknown。

隔离专服通过五条动作回执。克苏鲁之眼宝藏袋从 2 减到 1，记录 5 组掉落；木板条箱从 1 减到 0，记录 4 组掉落。两次用旧令牌重试均被拒绝；普通石块被拒绝且槽位令牌保持不变。测试独立重读背包并验证回执 schema。证据目录为 .local/direct-input-native-ff8fea63b7d4473c99c74d7791a261bd。构建后运行 tests/verify_server_ai_direct_input_native.ps1，传入 -TestScript native_open_item.py 和 -PackagePath 指向待测包。

### R6 放置预检复用（2026-11-19）

place_tile、place_object 和统一 place_item 现在将已检查的目标、物件形状及支撑快照直接传给 ActorItemUse。此前在同一游戏线程请求内，受理和创建上下文各自扫描一次目标；多格物件还会再次解析备选形状。绳索沿用已经解析的目标。开始原生物品使用时仍检查 Actor 绑定、空闲状态和手持材料，之后在自然更新边界重验目标与材料。

原生 ItemCheck 负责触及、放置规则和消耗。精确目标、物件占用、玩家碰撞以及支撑变化仍是请求范围和结果归属的保护条件。验收继续检查多格 Frame、容器注册、目标实际状态、材料减少一件和输入释放。

tModLoader 2026.09.3.1 的三个复制世界通过 tests/verify_server_ai_placement_native.ps1 的默认、-Object、-Object -Failure 场景，分别完成 52、109、2 项检查。原始 placement-native-result.json 位于 .local/placement-native-validation- 加以下后缀的目录：6ca05b01c1a34950947c890892b4d13f、c85b6604f9124b148824e5cafd1e9744、9f46fd13040e41b09c3369e053bcaac8。

## 7. 每阶段验收标准

1.  确认目标 Terraria/tModLoader 版本中的真实原生执行入口。
2.  原生执行绑定正确的 AI Player，不操作真人玩家。
3.  操作运行在游戏主线程。
4.  异常、取消和身份变化时安全释放输入。
5.  实际效果由游戏世界或库存证据确认。
6.  未确认的效果不得伪装为成功，也不得自动重复执行。
7.  原有任务接口和回执语义保持兼容，除非本阶段明确需要修改。
8.  通过构建、定向测试和必要的 Dedicated Server 原生验证。

## 8. 文档维护与阶段交接

每完成一个阶段，更新对应条目：

-   当前状态。
-   相关提交 SHA。
-   修改的主要文件。
-   使用的原生入口。
-   删除或保留的适配代码。
-   测试结果。
-   尚未解决的问题。

下一阶段开始前，先核对当前开发分支 HEAD 和本文件记录。

本文件是长期架构依据，不是每次 Codex 任务的完整
Prompt。每次实现只提取当前阶段的目标、必要背景和验收标准。

## 9. 最终架构原则

Terraria AI Companion 不应重新实现 Terraria 已经具备的玩家行为。

自主规划和任务管理由 AI Companion 负责；具体游戏行为尽可能交给 Terraria
原生执行；必要的服务器适配负责连接两者；独立核验负责确认实际结果。

**目标不是让 AI 模拟一个 Terraria 玩家，而是让 AI
能够正确地指挥一个真正由 Terraria 引擎驱动的玩家。**
