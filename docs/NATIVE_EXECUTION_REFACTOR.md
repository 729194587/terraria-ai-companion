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
  R3     增加原生门交互                 已确认待审查
  R4     审查并优化抓钩执行             待审查
  R5     增加右键物品使用               已确认待审查
  R6     清理放置重复预检查             待审查

每个阶段独立实施、测试和验收。阶段之间允许根据实际代码依赖调整顺序。

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
