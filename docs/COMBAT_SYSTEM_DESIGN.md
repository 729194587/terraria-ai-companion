# COMBAT_SYSTEM_DESIGN.md

**状态：** 代码审查修订版，可作为 Combat MVP 实施基线\
**审查基线：** `codex/unified-autonomous-navigation` @
`926101e817282930c510121f2532f23e16418fe5`\
**目标：** 实现单目标普通近战自主战斗，并验证 Combat
优先级抢占与移动/攻击输入协调。

## 1. 目标与原则

为 Terraria AI Companion 增加游戏侧自主战斗能力。

Agent 可以主动提交战斗任务；当角色遭遇明确的近距离威胁时，游戏侧
CombatReflex 可以自动触发战斗。CombatTask 在游戏 Tick 中持续执行，不要求
Agent 逐帧发送操作。

Agent 在战斗期间仍可观察游戏、读取战斗状态和发送聊天消息。战斗执行不等待
LLM 响应。

核心原则：

-   Agent 决定战略意图；游戏侧负责实时战斗执行。
-   同一时刻只有一个主要身体任务拥有控制权。
-   CombatReflex 和 Agent 主动战斗共用同一套
    CombatTask/CombatController。
-   移动和攻击可以组合，但不因此开放任意动作并行。
-   继续使用 Terraria 原生物品使用、碰撞和伤害机制。

## 2. 当前架构基线

当前项目已经具备：

-   游戏更新驱动的 `TaskSlot`；
-   单根任务、单活动子任务的任务组合；
-   异步 `submit_task` / `query_task` / `await_task` / `cancel_task`
    基础；
-   World、Actor、Generation、Slot 身份绑定；
-   原生移动、挖掘和物品使用路径；
-   任务期间继续执行只读观察的能力。

当前尚不具备：

-   通用任务优先级仲裁；
-   原地 Suspend/Resume；
-   移动与持续攻击的输入合并；
-   CombatTask、CombatReflex 和战斗状态查询。

本设计要求在现有任务运行时和原生执行路径上演进，不重建一套平行任务系统。

## 3. 任务与仲裁

### 3.1 单一身体控制权

保持一个活动根身体任务。任务内部可以串行调用导航、挖掘等子任务；CombatTask
内部则协调移动与攻击意图。

不允许多个独立执行器同时直接控制同一个 Player。

优先级保持简单：

1.  显式停止/取消；
2.  Combat（Agent 主动战斗或 CombatReflex）；
3.  普通 Agent 身体任务；
4.  空闲。

Agent 主动战斗和 CombatReflex 必须进入同一个
CombatController。若目标相同且已有 CombatTask，不创建第二个战斗执行器。

### 3.2 CombatTask 生命周期

CombatTask 是异步、游戏 Tick 驱动的根任务。

任务生命周期沿用现有异步任务终态语义：

``` text
ACCEPTED
   ↓
RUNNING
   ├── COMPLETED
   ├── FAILED
   ├── CANCELLED
   └── UNKNOWN
```

MVP 不增加 `SUSPENDED` 状态。

战斗内部阶段独立管理：

``` text
ACQUIRE_TARGET
      ↓
APPROACH
      ↓
ATTACK
      ↓
VERIFY_RESULT
      ↓
REASSESS
      ├── 继续战斗
      └── 结束战斗
```

CombatTask 至少记录：

-   Task ID 和创建来源（Agent / Reflex）；
-   目标 NPC 身份；
-   当前阶段；
-   当前武器；
-   最近一次攻击结果；
-   当前威胁摘要；
-   终止原因和输入释放证据。

任务完成必须依据实际游戏状态，不能仅根据攻击次数或持续时间推断。

### 3.3 CombatReflex 抢占

CombatReflex 在游戏侧运行，不依赖 Agent 及时响应。

触发依据必须是可靠威胁，而不是"附近有一个可攻击 NPC"。MVP
至少结合以下信号：

-   敌对 NPC 已明确将 AI 作为攻击目标，且进入实际威胁/交战范围；
-   AI 最近受到可归因于敌对 NPC 或敌对攻击的实际伤害。

仅有距离接近、`friendly == false` 或观察快照中的 `hostile_creature`
推断，不足以单独触发战斗。

触发流程：

1.  确认目标有效、具有敌对威胁且可交战。
2.  若已有同目标 CombatTask，复用该任务。
3.  若普通身体任务正在运行，请求通过统一仲裁路径安全中断。
4.  以 `task_interrupt_combat` 记录危险中断原因。
5.  确认 Actor 身份仍有效、原任务输入已释放。
6.  启动 CombatTask 并取得身体控制权。

如果原动作清理失败、输入仍被占用或 Actor
身份无法确认，不允许第二个控制器并发写入。保留 unknown 和诊断信息。

CombatReflex 只负责检测威胁和申请控制权，不实现另一套攻击逻辑。

### 3.4 被抢占任务的恢复

**MVP 不做原地暂停和自动恢复。**

当前项目已有经过测试的危险中断契约：原任务以明确的 `task_interrupt_*`
原因终止；Agent 在危险解除后决定是否调用 `resume_task`。恢复会产生新的
Task ID，并由游戏侧重新验证目标、工具和环境。

Combat 使用
`task_interrupt_combat`，使已知且安全释放的普通任务可以进入现有恢复流程。

限制：

-   当前 `resume_task` 只支持非 cluster 的 `mine_target`。
-   独立导航和其他原子动作由 Agent 重新观察后决定是否重新提交。
-   如果原任务结果为 unknown，或输入释放未确认，不得恢复或重放。
-   恢复时不能沿用旧按键、旧位置或旧的原生动作句柄。

未来若确实需要原地
Suspend/Resume，应另行设计每种任务和原生子动作的暂停检查点，不属于本次
MVP。

## 4. 统一移动与攻击输入协调

### 4.1 输入意图

建立统一的 Actor 输入协调机制（可沿用或扩展
`ActorControl`，具体类结构由实现根据现有代码决定）。

移动控制器产生移动意图：

``` text
MovementIntent:
    direction
    jump
    verticalMovement
    facing
```

攻击控制器产生攻击意图：

``` text
AttackIntent:
    useItem
    weaponSlot
    attackState
```

输入协调器将两者合并为同一游戏更新周期的 Actor 输入，并在正确的 Terraria
原生执行阶段应用。

### 4.2 输入所有权

-   同一个身体任务是输入意图的所有者。
-   移动逻辑不得清除不属于移动通道的攻击输入。
-   攻击逻辑不得覆盖移动方向、跳跃等输入。
-   停止任务时只释放当前任务拥有的输入；任务终止后必须确认所有 AI
    控制输入已释放。
-   不同 Hook
    需要在不同原生阶段应用输入时，仍由同一个协调机制决定最终意图。
-   每个 Player 更新周期只能执行一次受控的原生 ItemCheck，避免重复调用。

MVP 只要求支持"移动 +
普通近战攻击"组合。挖掘、放置和其他动作仍按各自原生约束执行，不要求任意并行。

### 4.3 导航复用

复用现有 NavigationTask/导航规划能力，不重写导航算法。

但战斗不能简单启动一个独立 `navigate_to` 动作，再同时启动
`use_item`。当前两个执行器互斥，而且导航输入会清除物品使用字段。

战斗需要取得导航产生的移动意图，并由统一输入协调器与攻击意图合并。具体实现可调整现有
MovementDriver/导航执行边界，但不得形成第二个身体控制所有者。

### 4.4 原生近战攻击

当前 `ActorItemUse` 是单次物品使用动作，不是持续战斗控制器。MVP
需要战斗专用的近战攻击周期管理：

-   使用当前选中的有效近战武器；
-   维护正确的攻击按下、持续和释放时机；
-   遵循 `itemAnimation`、`itemTime`、`reuseDelay` 和 Terraria
    原生使用规则；
-   通过原生 `ItemCheck`、命中判定和伤害流程执行；
-   不直接修改 NPC 生命值，不自行伪造命中结果。

战斗期间不能让 ActorMining、ActorItemUse 和 CombatAttack
重复拥有或重复调用 AI 的 ItemCheck 路径。

## 5. Combat Perception 与目标身份

战斗控制以服务端权威 NPC 状态为准，不以 Agent 的 client-known
观察快照作为唯一执行依据。

MVP 至少读取：

-   NPC 是否 active、存活；
-   是否为有效敌对目标；
-   NPC 当前位置、速度和碰撞箱；
-   是否正在攻击/追踪 AI；
-   当前目标是否仍然有效。

目标引用必须防止 NPC 槽位复用。至少结合 NPC
slot、类型和本次生成身份/代次，在每次关键攻击前重新验证。

当前 `EntitySnapshot` 的半径、数量限制、Mod NPC
分类和攻击意图不确定性只适合观察，不足以承担 CombatReflex 的权威判定。

## 6. Combat MVP 范围

第一版只实现单目标普通近战战斗。

流程：

1.  获取并验证一个有效敌对 NPC。
2.  验证目标身份、生命状态和交战条件。
3.  验证当前选中的武器确实属于受支持的普通近战武器。
4.  使用现有导航规划能力接近目标。
5.  根据目标位置和近战范围调整朝向与站位。
6.  由 CombatController 协调移动和原生近战攻击。
7.  观察目标 NPC 的实际生命变化、死亡或失效状态。
8.  目标被击败或战斗条件不再成立时结束任务。
9.  释放输入并发布终态结果。

第一版不做复杂装备切换。没有有效近战武器时，明确失败，不尝试用任意物品攻击。

## 7. Agent 参与方式

Agent 主动战斗接入现有异步任务体验，扩展 `submit_task` 支持
`combat_attack`，并复用任务查询、等待和取消机制。

CombatReflex 自动启动的战斗也必须向 Agent 暴露可查询的状态和结果。

战斗期间：

-   允许只读观察和状态查询；
-   允许 Agent 发送聊天消息；
-   允许 Agent 根据进度发表评论；
-   不要求 Agent 逐帧发送攻击指令；
-   其他身体变更必须经过仲裁器，不得绕过 CombatTask 抢占输入。

## 8. 非本阶段范围

-   弓箭、枪械、魔法等远程武器；
-   弹道预测和动态瞄准；
-   复杂武器选择与自动换装；
-   多目标战术；
-   高级闪避和弹幕预测；
-   Boss 战斗；
-   任意动作并行（例如通用的移动+挖矿）；
-   通用任务原地 Suspend/Resume、持久化和进程重启恢复。

这些功能不应阻止近战 Combat MVP 落地。

## 9. 验收标准

1.  Agent 可以异步提交 `combat_attack`，获得稳定 Task
    ID，并查询任务进度和结果。
2.  CombatReflex 能在明确威胁出现时自动请求战斗控制权。
3.  正在运行的低优先级动作/任务能够被安全中断；CombatTask 只在 Actor
    身份有效且输入释放得到确认后启动。
4.  Agent 主动战斗和 Reflex 自卫使用同一套
    CombatController，不会生成两个互相争夺输入的战斗执行器。
5.  战斗期间可以在同一游戏更新周期协调移动与普通近战攻击，输入不会互相覆盖。
6.  近战攻击通过 Terraria 原生使用和命中机制实际造成伤害。
7.  每个 Player 更新周期不会重复执行受控 ItemCheck。
8.  Agent 在战斗期间仍能执行只读观察和聊天；其他身体变更受到仲裁。
9.  战斗结束、失败、取消和异常后，输入释放状态有明确证据。
10. 被中断的 `mine_target` 不自动恢复；Agent 显式调用 `resume_task`
    时使用新 Task ID，并重新验证目标。unknown 不得恢复。
11. NPC 槽位复用、目标死亡、目标失效和 Actor Generation
    改变不会导致攻击旧目标或写入旧 Actor。
12. 完成离线定向测试后，必须在真实 Terraria Dedicated Server +
    客户端环境验证普通敌怪战斗、战斗反射、Agent
    并行观察/聊天和任务恢复流程。

## 10. 主要实现入口

优先检查和扩展：

-   `CompanionBridge/ServerIpc/AiAtomicActions.cs`
-   `CompanionBridge/ServerIpc/TaskRuntime.cs`
-   `CompanionBridge/ServerIpc/ServerAiNativeActions.cs`
-   `CompanionBridge/ServerIpc/ServerAiSystem.cs`
-   `CompanionBridge/ServerIpc/NativeNavigationWorld.cs`
-   `CompanionBridge/ServerIpc/AutonomousNavigationTask.cs`
-   `CompanionBridge/ServerPlayerRuntime/ActorControl.cs`
-   `CompanionBridge/ServerPlayerRuntime/ActorItemUse.cs`
-   `CompanionBridge/ServerPlayerRuntime/ActorMining.cs`
-   `CompanionBridge/ServerPlayerRuntime/NativeSurvival.cs`
-   `CompanionBridge/EntitySnapshot.cs`
-   `python/program_tasks.py`
-   `python/server_ai.py`
-   `python/server_ai_contract.py`
-   相关 Schema、能力握手和测试。

## 11. 参考原则

### Numen

借鉴异步身体任务、任务优先级、Reflex
抢占、父子任务组织和战斗任务统一入口。

不照搬 Minecraft 的战斗动作和游戏规则。

### TerraBot

借鉴 Terraria 原生移动、近战攻击周期、武器范围、战斗站位和原生物理反馈。

MVP 只采用普通近战所需的子集，不在本阶段移植远程弹道和 Boss 战术。

**最终目标：** 让角色能够在游戏侧自主完成基础近战战斗，同时保持 Agent
对战斗过程的观察和参与；战斗是现有身体任务系统的高优先级能力，而不是一套独立、互相争夺
Player 输入的新系统。
