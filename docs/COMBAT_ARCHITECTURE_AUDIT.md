# COMBAT_ARCHITECTURE_AUDIT.md

**审查基线：** `codex/unified-autonomous-navigation`\
**HEAD：** `926101e817282930c510121f2532f23e16418fe5`\
**审查范围：** C# 任务运行时、原生动作执行器、输入控制、NPC 感知、Python
TaskCoordinator、现有测试。\
**性质：** 静态代码与测试审查；未在本次审查中启动 Terraria 实机。

## 结论

当前代码可以作为 Combat MVP 的基础，但不能直接新增一个独立 `ActorCombat`
执行器就开始战斗。

真正的阻塞点有三个：

1.  当前动作入口和任务运行时只允许一个活动身体动作/根任务。
2.  移动与物品使用有明确的互斥检查，而且不同执行器会清除彼此使用的
    Player 控制字段。
3.  当前任务运行时没有原地暂停/恢复；项目已有的危险恢复契约是"安全终止后，由
    Agent 显式重新提交"。

建议在现有架构上增加战斗优先级仲裁和移动/近战输入协调，不重建通用任务运行时，也不在
MVP 引入任意动作并行。

## 1. 现有任务运行时：已有可复用基础，但没有 Suspend/Resume

### 已有能力

-   `TaskSlot` 是游戏更新驱动的根任务包装器。
-   `AbstractCompanionTask`
    支持父任务调用一个活动子任务，子任务结束后记录结果，再启动下一个子任务。
-   `AiAtomicActions.Tick()` 由游戏线程的 `AiReadOnlyServer.Tick()`
    推进；`ServerAiSystem` 在 `PostUpdatePlayers` 中 Pump。长任务不依赖
    LLM 每帧轮询。
-   任务身份绑定 World、Actor、Generation 和
    Slot；完成与输入释放分别核验。

主要入口：

-   `CompanionBridge/ServerIpc/TaskRuntime.cs`
-   `CompanionBridge/ServerIpc/AiAtomicActions.cs`
-   `CompanionBridge/ServerIpc/ServerAiNativeActions.cs`
-   `CompanionBridge/ServerIpc/ServerAiSystem.cs`
-   `CompanionBridge/ServerIpc/AiReadOnlyServer.cs`

### 当前限制

-   `TaskRuntime.cs` 明确是 single-root / single-active-child。
-   `AbstractCompanionTask` 只保留一个活动 child。
-   `TaskSlot.Stop()` 会终止任务并记录终态；没有 `Suspend()` /
    `Resume()`。
-   `AiAtomicActions` 只有一个 `active`
    Entry；新动作在已有动作运行时返回 `action_busy`。
-   Python `TaskCoordinator.blocked()`
    会阻止活动或结果不确定的任务期间提交新的身体变更。

因此，当前系统具备"复合任务串行编排"，不具备"多个根任务并行"或"任务原地暂停恢复"。

## 2. 项目已有明确的危险中断与恢复契约

`NativeNavigationWorld.CheckHazards()` 当前检查：

-   生命值相较任务初始值下降；
-   接触熔岩；
-   160px 内存在活动、非 friendly 且有接触伤害的 NPC。

`AutonomousNavigationTask.Tick()` 收到危险原因后会停止当前任务并取消活动
child。当前这是终止，不是暂停。

Python `program_tasks.py` 的 `resume_task` 则采用显式重新提交：

-   仅接受已知的 `cancelled` 结果；
-   原因必须以 `task_interrupt_` 开头；
-   必须确认 `endEvidence.inputsReleased == true`；
-   不允许恢复 cluster 挖掘；
-   重新提交时产生新的 Task ID，游戏侧重新验证。

现有测试明确锁定了这一行为：

-   `tests/TaskRuntimeOfflineTests.cs`：危险解除后，终止任务不会自动恢复。
-   `tests/test_m3_program_tasks.py`：已验证危险中断可以显式恢复，但使用新
    Task ID；输入释放不确定时恢复和新任务均被拒绝。
-   `python/server_ai_contract.py`：明确写明"不自动恢复"。

**审查建议：Combat MVP 保留这套契约。** 不要为了战斗把现有任务改成原地
Suspend/Resume。战斗抢占时以明确原因安全终止原任务；战斗结束后由 Agent
决定是否调用 `resume_task`。这比在本阶段为每种任务和原生 child
增加可暂停语义更小、更安全，也符合现有测试。

注意：当前 `resume_task` 仅支持非 cluster 的
`mine_target`。独立导航或其他原子动作由 Agent
重新观察后决定是否重发；unknown 结果不能恢复或重放。

## 3. 移动与物品使用确实互斥，且不只是 Start 检查

### 启动层互斥

-   `ActorControl.Start()` 拒绝 `ActorItemUse.Running` 和
    `ActorMining.Running`。
-   `ActorItemUse.Idle()` 拒绝 `ActorControl.Running` 和
    `ActorMining.Running`。
-   `ActorMining.Start()` 拒绝
    `ActorControl.Running`、`ActorItemUse.Running`
    以及未结束的原生物品动画。
-   `ServerAiNavigation.Start()` 和 `ServerAiDirectInput.Start()` 也检查
    Actor 是否 busy。
-   `ServerAiNativeActions.StartCore()`
    在所有原生操作入口统一检查活动控制器和物品动画。

### 输入字段也会互相覆盖

-   `ActorControl.Inputs()` 会清除
    `controlUseItem`、跳跃、上下移动和使用 Tile 等字段。
-   `ServerAiNavigation.Apply()` 设置移动字段，同时清除 `controlUseItem`
    和 `controlUseTile`。
-   `ServerAiDirectInput.BeforeMovement()` 同样清除物品使用字段。
-   绳索控制器的 Release 也会清除全部相关控制字段。

所以，绕过 Start
互斥并不能得到可靠的移动+攻击。输入写入和释放本身也需要协调。

### 物品使用目前不是持续战斗攻击

`ActorItemUse.Supported()` 排除：

-   `shoot` 武器；
-   需要弹药的武器；
-   `channel` 武器。

`ActorItemUse.Update()` 只在首个 Tick 发出一次使用按下，并自行调用
`Player.ItemCheck()`，等待一个原生使用周期结束。它是单次物品使用动作，不是持续近战攻击控制器。

同时，`ActorItemUse.Update()` 在 `ActorMining.Running` 时会直接转交给
`ActorMining.Update()` 并返回。当前 AI 的 ItemCheck
执行路径需要明确唯一所有者，不能让挖掘与战斗控制器重复调用原生
`ItemCheck()`。

**审查建议：**
统一的是输入意图与控制权，不是把所有动作都改成可以任意并行。MVP
只要求移动与普通近战攻击可以组合；挖掘、放置等动作仍按其原生约束串行执行。

## 4. 当前 NPC 感知不能直接作为战斗目标判定

### 现有观察的限制

`EntitySnapshot.cs`：

-   半径 480px；
-   最多返回 12 个实体；
-   `hostile_creature` 是对部分原版 NPC 的推断；
-   明确注明当前攻击意图未验证；
-   Mod NPC 通常归为 `unknown`。

`ObservationSnapshot.cs`：

-   来源是 client-known 实体；
-   仅覆盖视口；
-   几何相交不等于真实可见；
-   `InstanceKey` 由 observation ID 和实体槽位组成，不是跨观察周期稳定的
    NPC 身份。

这些数据适合 Agent 观察和战略决策，不适合直接作为游戏侧 Reflex
的权威目标来源。

### 当前危险检测也不等于 Combat Reflex

`NativeNavigationWorld.CheckHazards()` 只判断 NPC 是否活动、非
friendly、有伤害并处于一定距离；没有确认 NPC 是否正在攻击
AI。它会把"附近存在潜在敌人"作为导航/挖掘任务的中断理由。

这项检查应与 CombatReflex 的威胁判定分离。CombatReflex
应在服务端使用权威 NPC
状态，至少结合实际攻击目标关系或最近的实际受伤证据；不能仅凭附近有 NPC
就无条件开战。

## 5. Agent 异步参与：已有基础，战斗 API 尚未接入

已有 `program_tasks.py` 支持异步提交、查询、等待、取消和显式恢复，但
`submit_task` 目前只接受 `mine_target`。

已有测试
`test_read_only_program_still_uses_host_facts_while_task_is_active`
证明：任务运行期间 Agent 仍可以执行只读观察。聊天通过独立的 Server AI
消息路径发送，不应被 CombatTask 占用。

战斗需要接入同一套异步任务体验，并确保：

-   Agent 可以读取战斗状态；
-   Agent 可以继续观察和聊天；
-   战斗期间其他身体变更仍受仲裁器约束；
-   Agent 不需要逐 Tick 发出攻击命令。

## 6. 测试现状

-   `TaskRuntimeOfflineTests.cs` 覆盖单根任务、单
    child、危险终止、输入释放和显式取消等行为。
-   `test_m3_program_tasks.py` 覆盖异步任务、显式恢复、unknown
    阻断和任务期间只读观察。
-   `ServerAiActionFixture.cs` 明确说明现有 NPC fixture 只测试 NPC
    存在性，不测试 AI/Combat。

当前没有 CombatTask、CombatReflex
或移动+攻击组合的专门测试，也没有真实战斗验收。

## 7. 推荐的架构修订

1.  保留一个身体控制权所有者和一个活动根任务的约束。
2.  增加 Combat 优先级仲裁。Agent 主动战斗和本地 Reflex 进入同一
    CombatTask/Controller，不建立两套攻击逻辑。
3.  Reflex 触发时，先安全终止低优先级动作/任务，使用
    `task_interrupt_combat` 作为可识别原因；确认 Actor
    身份和输入释放后，才把控制权交给 CombatTask。
4.  若输入释放或 Actor 绑定无法确认，不允许另一个控制器并发写入；保留
    unknown。
5.  MVP 不增加原地 Suspend/Resume。普通任务终止后，Agent
    在战斗结束时决定是否显式重新提交；恢复必须重新验证。
6.  引入统一输入意图协调。MVP
    只组合移动与普通近战攻击，不扩展为任意动作并行。
7.  CombatReflex 使用服务端权威 NPC 状态；Agent
    的观察快照用于理解和评论，不作为唯一战斗控制依据。
8.  为 CombatTask、Reflex
    抢占、输入合并、原生攻击和真实游戏战斗补齐定向测试。

## 关键代码与测试入口

-   `CompanionBridge/ServerIpc/AiAtomicActions.cs`
-   `CompanionBridge/ServerIpc/TaskRuntime.cs`
-   `CompanionBridge/ServerIpc/AutonomousNavigationTask.cs`
-   `CompanionBridge/ServerIpc/NativeNavigationWorld.cs`
-   `CompanionBridge/ServerIpc/ServerAiNativeActions.cs`
-   `CompanionBridge/ServerIpc/ServerAiSystem.cs`
-   `CompanionBridge/ServerPlayerRuntime/ActorControl.cs`
-   `CompanionBridge/ServerPlayerRuntime/ActorItemUse.cs`
-   `CompanionBridge/ServerPlayerRuntime/ActorMining.cs`
-   `CompanionBridge/ServerPlayerRuntime/NativeSurvival.cs`
-   `CompanionBridge/EntitySnapshot.cs`
-   `CompanionBridge/ObservationSnapshot.cs`
-   `python/program_tasks.py`
-   `python/server_ai.py`
-   `python/server_ai_contract.py`
-   `tests/TaskRuntimeOfflineTests.cs`
-   `tests/test_m3_program_tasks.py`
-   `CompanionBridge/ServerIpc/ServerAiActionFixture.cs`
