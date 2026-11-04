# 架构审查计划（工作草案）

**文档版本：** V1.0\
**制定日期：** 2026-11-02\
**状态：** 讨论用审查草案；不定义当前实现契约或预先决定重构方向\
**主要参考：** Dwinovo/minecraft-numen（1.21.1 分支）

------------------------------------------------------------------------

> 本文用于组织架构审查问题与研究范围。它不是当前架构说明、已确认缺陷清单或实现计划；具体事实须以审查时的源码和可复现验证重新确认。\n\n## 1. 审查背景

Terraria AI Companion 的目标是让 AI 成为 Terraria
中真正能够自主行动的第二玩家，在 Host & Play
世界中完成探索、采集、建造、战斗和生存。

当前采用：

-   Python：LLM Agent、上下文管理、工具调用。
-   TCP IPC：Python 与游戏 Mod 之间的通信。
-   C# tModLoader：游戏感知、原生动作、角色控制和任务执行。
-   Lua：游戏行为编排与程序执行。

项目已经实现了一批基础能力，包括环境感知、移动、跳跃、采矿、物品使用、库存查询、建造相关操作以及原生任务运行时。

但近期多轮实机测试暴露出一个共同问题：

**底层能力虽然不断增加，Agent 却始终难以稳定地完成连续游戏任务。**

典型表现：

1.  DeepSeek 经常生成 Lua 程序后出现语法、参数、API
    契约或运行时错误，随后需要额外推理分析错误。
2.  一个简单的游戏目标可能触发多个前置条件检查，导致正常游戏行为被拒绝。
3.  导航失败、站位不满足、环境变化等局部问题容易导致整个任务终止。
4.  底层缺少足够的局部恢复能力，常规执行问题被不断上抛给 LLM。
5.  任务执行、Lua 编排、原子操作之间的职责仍不够清晰。
6.  实机测试较多依赖人工观察，缺少系统化、可重复的任务评测体系。

因此，当前需要的不是继续增加 API，而是重新审视整个 Agent 架构。

## 2. 本次审查的核心目标

回答一个根本问题：

**如何让 DeepSeek
只需要表达游戏意图，底层就能自主完成常规游戏行为，并在确实无法完成时返回真实、可解释的失败？**

理想状态：

-   DeepSeek 决定做什么、去哪里、优先完成什么。
-   程序负责组合行为和执行局部决策。
-   C# 负责真实游戏环境中的身体控制、物理交互和执行反馈。
-   常规障碍由底层自主尝试解决。
-   只有真正需要更高层决策时，才向 LLM 请求介入。
-   所有执行结果都有可靠的状态和证据。

本次审查不预设当前架构正确，也不预设必须推倒重来。

保留、重构、删除或替换某个模块，都应以实际代码、测试证据和设计目标为依据。

## 3. 当前已知问题与验收证据

### 3.1 M1 任务运行时验收

2026-11-02 完成第一轮 M1 实机验收。

成功案例：

-   `mine_target({x=2142,y=214})` 成功采矿。
-   原生子任务 `root/mine` 返回完整执行证据。
-   篡改目标坐标后正确拒绝，返回 `observed_target_modified`。
-   一次 `mine_target` 调用只占一个外部 Action，内部子任务不额外计数。

失败案例：

-   4 次任务均返回 `no_safe_mining_stance_in_local_window`。
-   任务停留在 `observe` 阶段，没有进入 `approach`。
-   玩家预先手工挖出 2×2 空间后，采矿任务才能成功。

主要问题：

1.  `FindApproach()` 只寻找已经存在的安全站位。
2.  当前站位检查可能禁止站在矿物上直接挖掘脚下的矿。
3.  没有站位时无法尝试挖掘阻挡物。
4.  失败诊断不足。
5.  普通任务失败会导致 Lua 程序终止。
6.  令牌过期测试没有得到有效验证。

验收报告：

`runs/20261102-m1/verdict.md`

注意：后续源码审查发现，原先对令牌过期测试的判断可能不准确。`wait(40000)`
超出工具参数上限，实际等待没有执行。因此不能将该测试作为令牌时效校验顺序错误的证据。

### 3.2 已发现的实现审查线索

以下是此前源码检查发现的重点，进入正式审查后仍须对照最新代码复核。

**采矿限制：**

-   `NativeMineTargetWorld.FindApproach()` 只枚举局部现成站位。
-   `SafePose()` 检查目标移除后是否影响当前支撑。
-   `CanMine()` 复用站位安全条件，可能错误禁止挖掘脚下的支撑矿物。
-   `GameNavigation.Ordinary()` 的角色 Profile
    检查可能过度限制正常玩家状态。
-   `ActorMining.QueryReach()` 额外使用 80px 距离门禁。
-   导航和采矿共享了部分不适合彼此的限制条件。

**身体控制：**

-   已经存在 `ServerAiDirectInput`。
-   支持连续移动、跳跃、空中改向和自然下落。
-   但导航失败后的降级条件可能过于严格。
-   某些状态变化会被错误解释为不支持的角色状态或 `unknown`。

**Lua 执行：**

-   普通动作失败可能终止整个 Lua Worker。
-   `pcall` 无法捕获已经由宿主终止的动作执行。
-   Lua 局部变量和程序执行上下文不能在 Worker 终止后自然恢复。
-   `actionResults` 和 `lastObservation` 已经提供了一部分结果保留能力。

以上是需要验证的审查线索，不应直接视为全部已经确认的架构缺陷。

## 4. 重点参考项目：Numen

仓库：

https://github.com/Dwinovo/minecraft-numen

主要分支：`1.21.1`

Numen 的价值不仅在于其当前架构，更在于它经历过多次实际重构。

已经初步梳理出以下演变：

  时间            重要演变
  --------------- -----------------------------------------
  5 月            Animus 原型，Mob + LLM Tool Calling
  5 月底          A\*、意图级采矿、Navigator
  6 月 13 日      从 Mob 转向 Fake ServerPlayer，重构身体
  6 月底          API 模块化、任务执行器和 ToolDispatcher
  7 月            外部大脑、身体控制接口、导航恢复
  7～8 月         建造、空间感知、长期目标和插件
  9 月            独立 AgentLoop，重构运行循环
  10 月 1～3 日   胖工具转向原子 API + Lua
  10 月 4～5 日   Lua 整段迁移至服务端执行
  10 月下旬       Trip / Driver 分离、寻路独立基准

重点提交：

-   `045eb4c5`：意图级采矿。
-   `2e838927`：Fake ServerPlayer 身体。
-   `f91c58a8`：旧寻路内核退役。
-   `8c1c5533`：NumenActuator。
-   `92808741`：Best-effort 长距离导航。
-   `b699437e`：长期目标。
-   `9e4a799d`：AgentLoop。
-   `12b13ed2`：模型只保留 Lua 工具。
-   `b98dcdcc`：API 原子化。
-   `27c777cd`：整段 Lua 程序独立执行。
-   `f7ae5ee1`：服务端程序执行入口。
-   `d2ee238a`：Trip / Driver 分离及寻路评测。

关键设计文档：

1.  https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/docs/architecture-mind-model.md
2.  https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/docs/agent-loop.md
3.  https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/docs/spatial-perception.md
4.  https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/docs/adr/control/0005-atomic-commands-composed-by-scripts.md
5.  https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/docs/adr/control/0006-lua.md
6.  https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/docs/adr/control/0008-lua-only-tool.md
7.  https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/docs/adr/control/0012-external-brain-fewer-calls.md
8.  https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/docs/adr/body/0001-server-side-player.md
9.  https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/docs/adr/pathing/README.md

### 研究原则

不能只看 Numen 当前的代码，然后复制它的最终架构。

需要理解：

-   为什么它做出某项设计？
-   旧方案遇到了什么问题？
-   新方案解决了什么？
-   引入了什么新的复杂性？
-   哪些测试证明新方案有效？
-   哪些方案后来被撤销？
-   Terraria 与 Minecraft 有哪些根本区别？

借鉴的是问题解决方法、架构边界和验证机制，而不是照搬实现。

## 5. 五大审查工作流

### A. Agent Loop 与上下文管理

审查范围：

-   Python Agent Loop。
-   System Prompt 与动态 Capability Manifest。
-   消息历史和上下文组装。
-   工具调用及 Dispatch。
-   工具返回值与错误处理。
-   Lua 程序执行结果注入。
-   中断、等待、恢复和最终回复。
-   多轮任务和长期目标。

核心问题：

1.  当前 Agent Loop 是否存在重复决策、无效轮次或上下文污染？
2.  DeepSeek 为什么频繁需要分析 Lua 程序错误？
3.  工具调用结果是否容易理解、是否提供足够的下一步决策信息？
4.  Lua 是否是当前最合适的默认游戏交互接口？
5.  直接 Tool Calling、Lua 单一入口、混合模式分别有什么优劣？
6.  是否应该借鉴 Numen 的 Run / Hold / pump 设计？
7.  如何减少 LLM 对常规游戏操作的介入？

### B. Lua 执行与原子 API

审查范围：

-   Lua Runtime。
-   execute_lua。
-   C# 原生 API。
-   参数 Schema。
-   返回值契约。
-   错误传播。
-   程序预算与取消。
-   模块加载和缓存。
-   Lua 与 C# 之间的执行反馈。

核心问题：

1.  当前原子 API 是否真正易于组合？
2.  API 是否存在语义不一致、参数过多、返回值不统一的问题？
3.  Lua 程序失败是否被过度放大成整个任务失败？
4.  哪些错误应由底层处理，哪些应交给 Lua？
5.  如何让 DeepSeek 不必承担大量程序调试工作？
6.  是否需要内置 Lua Skill 库？
7.  是否有必要比较直接 Tool Calling 和 Lua 的实际表现？

### C. 身体控制与 Task Runtime

审查范围：

-   TaskRuntime。
-   Task 生命周期。
-   Program.advance / Dispatch / Await / Done 对应机制。
-   Native Actions。
-   Direct Input。
-   Navigation。
-   Mining。
-   Building。
-   Combat。
-   Cancel、Lease、Generation、ActionId。
-   输入释放与执行证据。

核心问题：

1.  当前任务运行时是否真的支持持续执行，还是仅仅串联多个原子操作？
2.  为什么正常游戏行为会被预检和导航器拒绝？
3.  导航是否被错误地设置成所有移动行为的前置条件？
4.  Direct Input 应该处于什么架构位置？
5.  如何处理跳跃、下落、击退、碰撞和环境变化？
6.  任务是否可以自主更换策略？
7.  哪些失败属于 recoverable，哪些才应该成为 failed？
8.  如何防止局部失败演变为整个 Lua 程序失效？

必须区分：

-   游戏行为约束。
-   任务执行约束。
-   运行时安全约束。

身份绑定、控制权、取消、输入释放、期限和 `unknown`
隔离等运行时保障不能因为追求自由行动而随意删除。

### D. Perception 与 Navigation

审查范围：

-   observe_scene。
-   observe_terrain。
-   scan_tiles。
-   scan_entities。
-   inspect_tile。
-   resolve_content。
-   query_map。
-   语义地图。
-   Physics Simulator。
-   Navigation Planner。
-   Direct Input。
-   路径执行和重新规划。

核心问题：

1.  感知数据是否足以支持真实游戏决策？
2.  地图表示是否过度依赖局部观察？
3.  未知区域、障碍、平台、斜坡和液体如何表达？
4.  运动学模型是否与真实 Terraria 物理一致？
5.  导航规划是否过于保守？
6.  规划失败是否意味着角色真的无法移动？
7.  如何处理动态世界和外部干扰？
8.  是否需要借鉴 Numen 的 Trip / Driver 分离？

### E. Evaluation 与 Observability

审查范围：

-   Python 测试。
-   C# 离线测试。
-   GameTest。
-   Host & Play 实机测试。
-   Agent Trace。
-   任务结果。
-   失败分类。
-   性能与 Token 统计。

核心问题：

1.  当前测试是否能真实反映 Agent 玩游戏的能力？
2.  如何建立固定场景和可重复的游戏任务？
3.  如何区分模型问题、Prompt 问题、感知问题、执行问题？
4.  如何记录每次失败的具体原因和恢复过程？
5.  如何比较不同架构和 Prompt 的效果？
6.  如何避免仅凭少量成功案例就宣布功能通过？

建议统一记录：

-   任务完成率。
-   任务耗时。
-   LLM 调用次数。
-   Token 消耗。
-   工具调用次数。
-   局部恢复次数。
-   最终失败分类。
-   人工干预次数。
-   游戏状态证据。

## 6. 架构方案比较

审查完成后，需要对至少三种路线进行比较。

### 方案 A：直接 Tool Calling

DeepSeek 直接调用结构化 Terraria Tools。

优点：

-   不需要每次生成 Lua 程序。
-   工具参数可以通过 Schema 约束。
-   工具错误更容易结构化。
-   实现和调试相对直接。

风险：

-   多步骤操作可能产生大量 LLM 往返。
-   模型需要承担更多中间决策。
-   长时间动作需要可靠的异步执行接口。

### 方案 B：Lua 单一入口

DeepSeek 编写 Lua 程序，组合原子 API 完成游戏目标。

优点：

-   减少模型与游戏之间的交互轮次。
-   可以在程序内部持续观察和调整。
-   能够建立可复用 Skill。
-   更适合复杂行为编排。

风险：

-   模型需要编写正确程序。
-   Lua API 契约不完善时容易反复出错。
-   程序错误可能破坏整个任务流程。
-   必须认真设计程序预算、错误和恢复机制。

### 方案 C：混合架构

日常简单操作使用直接 Tool Calling，复杂的连续行为通过经过验证的 Lua
Skill 或程序完成。

优点：

-   简单任务不需要编写 Lua。
-   复杂行为可以减少 LLM 往返。
-   可以逐步迁移和验证。

风险：

-   两套交互接口可能增加复杂性。
-   需要明确工具与 Skill 的职责边界。

最终方案不预先指定，以实际实验结果和架构审查结论决定。

## 7. 审查方法与执行顺序

本次采用以下顺序：

**第一阶段：建立当前实现基线**

-   确认 Git 分支、HEAD 和工作区状态。
-   阅读 AGENTS.md 与项目架构文档。
-   绘制当前完整调用链。
-   确认各模块真实职责。
-   整理已有测试和实机证据。

**第二阶段：研究 Numen**

-   按照上述五个工作流研究对应源码。
-   阅读相关 ADR。
-   追踪关键提交的前后差异。
-   检查测试和评测实现。
-   记录与 Terraria 的差异。

**第三阶段：对照分析**

对每个模块分别记录：

-   当前实现。
-   Numen 实现。
-   设计目标。
-   现存问题。
-   根因。
-   可能的改进。
-   改进成本。
-   验证方式。

区分三种结论：

-   【事实】：源码或测试直接证实。
-   【推断】：根据实现推导的风险。
-   【建议】：未来拟采用的设计。

**第四阶段：架构决策**

形成：

-   整体架构图。
-   模块职责与边界。
-   任务执行模型。
-   错误与恢复模型。
-   LLM 与程序之间的决策分工。
-   运行时安全契约。
-   评测方案。

**第五阶段：垂直原型验证**

优先选择采矿作为代表任务。

从 DeepSeek 给出目标开始，验证：

1.  自主观察。
2.  自主移动。
3.  处理阻挡。
4.  适应下落、击退和地形变化。
5.  执行采矿。
6.  核验实际结果。
7.  无法继续时返回合理失败。

不要求一次性完成所有游戏能力。

## 8. 最终交付物

本次审查完成后，应形成：

1.  `ARCHITECTURE_AUDIT.md`：完整架构差距和根因报告。
2.  `AGENT_RUNTIME_DESIGN.md`：Agent Loop、上下文与交互模式设计。
3.  `TASK_EXECUTION_DESIGN.md`：任务运行时、身体控制与失败恢复设计。
4.  `PERCEPTION_NAVIGATION_DESIGN.md`：感知、地图与导航设计。
5.  `EVALUATION_PLAN.md`：固定场景、评测指标和验收标准。
6.  必要的 ADR：记录重要架构决策及其理由。

这些是审查结束后的目标交付物，不要求一开始就全部编写。

## 9. 本次审查的范围边界

当前阶段：

-   暂停新增游戏功能。
-   暂停大规模架构性代码修改。
-   不为了让现有测试通过而继续叠加前置限制。
-   不预设必须保留 M1 的实现方式。
-   不预设必须全面接入 DeepSeek Harness。
-   不预设 Lua 必须作为最终交互方式。
-   保留已经验证有效的基础设施和运行时保障。

允许根据审查结果提出局部重构、模块替换甚至较大范围的架构调整。

但任何重构都应该有明确的问题依据和验收目标。

## 10. 新对话接续说明

新对话开始时，请先阅读本文件，并继续 Terraria AI Companion
大型架构审查。

本项目的最终目标是：

**开发一个真正能够在 Terraria 中自主探索、采集、建造、战斗和生存的 AI
队友，而不是一个只能按照预设 API 流程执行操作的自动化工具。**

本次审查最重要的原则：

1.  以 Agent 实际完成游戏目标为最高优先级。
2.  不把局部执行问题轻易上抛给 LLM。
3.  不把人为设计的安全限制误认为游戏真实限制。
4.  不以增加 API 数量作为能力进步的标准。
5.  充分研究 Numen 的历史演变，不只复制其最终架构。
6.  每项重要结论尽量有源码、测试或实机证据。
7.  允许推翻不合理的旧设计，不因既有投入而强行保留。

**下一步从第一阶段开始：建立当前仓库实现基线，然后依次开展五个工作流的源码审查。**

不要直接开始重构代码。先弄清楚我们为什么会走到今天这一步。
