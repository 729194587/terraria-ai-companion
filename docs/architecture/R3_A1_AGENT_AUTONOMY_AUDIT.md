# R3-A1 · Agent 自主能力审计

2026-11-22，审计对象为 [GitHub main 提交 338dccb8](https://github.com/729194587/terraria-ai-companion/commit/338dccb885aa1d62a8bbd6477dad06f6c7d4e5a3)。重点是内置 Python Agent 驱动的独立服务器 AI Player，同时核查了客户端控制和 External Brain 的边界。本文是 R3 选型依据，升级方案均为建议。

## 决策摘要

**建议先打通一个可验收的跨轮目标。** 让同伴在玩家开启自主模式后，能自行选一个值得调查的地点，走到最后一个已验证的安全落点，复查收获，再决定继续、改道或停止。玩家途中交付任务时，应能接手并独立完成。这个切片可以同时检验目标管理、世界知识、导航和控制权；仅增加运动能力，很难定位失败发生在哪一层。

| 决策问题 | 当前结论 | 依据等级 |
| --- | --- | --- |
| 玩家是否需要逐步指定操作？ | 一次委托可以包含选目标、导航、挖掘、复查，也可提交跨轮存活的游戏任务。真实模型在开放地形上的成功率仍待测。 | 源码、定向夹具、有限历史专服 |
| 无新聊天时能否自己行动？ | C# 会因威胁发起自卫，也会在满足条件时补召唤、插火把。Python 可因已提交任务结束而开结果轮；常规空闲和游戏公告没有通向新高层目标的入口。 | 生产调用链、事件及任务测试 |
| 是否了解已经探索过的世界？ | 已保存按世界和角色隔离的粗地形，能查询当下对象，也有私人笔记。缺少把“这里曾有矿、此路受阻、该去哪里复查”持续连接到目标选择的机制。 | 存储格式、读写调用点；战略影响为推断 |
| 长距离移动是否可行？ | 指定目的地可分段前进、局部绕行、改造和重规划。历史专服记录过约 952 像素的行程。未知洞穴选路、回家和跨任务改道没有同等级证据。 | 规划源码、阶段专服记录 |

下文的**事实**指在该提交可定位的行为；**历史验证**给出当时的执行环境；**推断**描述这些机制组合后的影响；**待验证**给出可以推翻推断的实验。基线核对时本地为 `f8241da`，与远端的差异仅涉及 README、LICENSE 和 SECURITY。未跟踪的 `terrabot/` 只作为外部样本阅读。

## 1. 一次行动由谁发起，何时结束

| 边界 | 生产调用链与行为 |
| --- | --- |
| 开轮 | `agent.run → Agent.receive → _start_turn → _run`。服务器模式分开连接聊天与执行后端，接收 `local_ai_command` 且 `isSelf=true` 的玩家消息。主循环 0.2 秒的超时用于连接检查。游戏公告去重后进入最近 32 条缓冲，随下一轮提供。客户端控制另有其他玩家聊天及本地终端入口。 [入口](../../python/agent.py#L406) · [运行主循环](../../python/agent.py#L1237) |
| 选工具与计划 | `_run` 给模型投影背包、预算、按能力可用的任务回执及笔记索引。服务器通过能力握手构造 API 集合，模型可读玩法攻略、生成含条件分支的 Lua，并在本轮读取结果后继续推理。模型侧游戏入口为 `execute_lua`，另有 `read_skill` 和可用时的 `memory`。默认每轮最多 8 次模型请求、7 次模型工具调用；Lua 共用 64 次 API、8 次动作尝试与 60 秒执行预算。 [模型轮](../../python/agent.py#L662) · [能力投影](../../python/server_ai.py#L30) · [服务端契约](../../python/server_ai_contract.py#L285) · [Lua 预算](../../python/lua_limits.py#L7) |
| 游戏执行 | Worker 调用 `api`，宿主分发至已绑定的 Server AI IPC。持续任务由 `TaskCoordinator.submit` 受理，C# 的 `TaskSlot.Tick` 校验身份、游戏更新和期限，再推进根任务及子任务。Python 监视器持有独立 taskId。普通新聊天会取代旧 Run；已受理的独立任务可以继续，显式停止走取消与释放。 [程序与任务](../../python/program_tasks.py#L151) · [Lua 分发](../../python/lua_runtime.py#L418) · [游戏任务](../../CompanionBridge/ServerIpc/TaskRuntime.cs#L55) |
| 结果回传 | 活跃轮读取 `runtimeTasks`；空闲时 `on_ready → _wake_task_results` 可用最近聊天身份开一次结果通知。宿主按序号投影、消费结果；通知轮执行 Lua 时关闭新动作。协调器声明范围为当前连接，未决或 unknown 会限制再次受理。 [唤醒](../../python/agent.py#L406) · [动作开关](../../python/agent.py#L788) · [迟到结果与禁止新动作测试](../../tests/test_m3_program_tasks.py#L221) |
| 生存反应 | `PostUpdatePlayers → Pump → Threat → Reflex` 可在无模型请求时发起战斗，并检查身体冲突与退避时间。维护逻辑还会检查召唤配置、身体空闲和安全施放窗口；插火把要求有材料、足够暗且有放置点。独立角色有原生死亡与复活路径。 [游戏更新](../../CompanionBridge/ServerIpc/ServerAiSystem.cs#L95) · [自卫受理](../../CompanionBridge/ServerIpc/AiAtomicActions.cs#L172) · [火把维护](../../CompanionBridge/ServerIpc/ServerTorchMaintenance.cs#L36) · [原生复活](../../CompanionBridge/ServerPlayerRuntime/NativeSurvival.cs#L146) |

**事实。** `AgentRun` 记录 generation、chat_id、预算、阶段和结果。宿主区分程序是否返回、具体任务是否完成、玩家目标是否达成。`task_outcome` 对指定挖掘能核对证据；一般调用 `goal_outcome` 时没有可执行的自然语言目标契约，常规成功路径仍返回 `unknown`。精确位置抵达有专门判据。任务中断也有局部例外，已安全释放的特定危险中断可显式 `resume_task`，重新受理并取得新 ID。External Brain 接管期间会暂停内脑，外部调用方可以另行编排。[结果分层](../../python/agent_run.py#L29) · [Run 终态](../../python/agent.py#L1107) · [恢复准入](../../python/program_tasks.py#L292) · [外部驾驶](../../python/external_brain.py#L72)

**推断。** 依赖玩家的根因在两个交接点。首先，空闲、资源缺口和新发现缺少一个能审查权限并发起目标的调用者。其次，子任务结束后，没有独立于聊天轮次的目标记录来判断剩余工作。模型可以在一次委托内自行解决这些问题；一旦本轮结束，地图、笔记和回执都无法自行决定何时再读它们。仅把结果通知的动作开关打开，会让旧回执承担新的行动授权，而且服务端对话发布仍绑定当前 chatId 和回复代次。[回复身份校验](../../python/agent.py#L1123)

## 2. 世界知识如何取得、保存和使用

| 知识来源 | 可以据此回答 | 影响决策的边界 |
| --- | --- | --- |
| 当前场景 `observe_scene` | 局部 45×29 Tile 中有哪些对象、危险和候选落点；区域模式可扩大到至多 8192 Tile。一次查询绑定角色、游戏更新、范围和覆盖。 | 区域采样有 10 ms 预算，可能提前截断；区域数据来自当前服务器 Tile，缺少可直接执行的动作目标。局部落点仍要经过运动验证。历史地图的可见范围与此读数不同。 [查询分流](../../CompanionBridge/ServerIpc/ServerAiQueries.cs#L67) · [场景采样与覆盖](../../CompanionBridge/ServerIpc/ServerSceneReader.cs#L24) · [区域限制](../../CompanionBridge/SceneLayers.cs#L40) |
| 语义查询 | `sense_world` 可组合类型解析、扫描和检查，找树干片段、矿与掉落；客户端的 `observe_exploration` 可压缩视口中的待调查对象。 | 多次读取可能来自不同帧，需保留来源、截断和缺测。客户端的视口投影不能推作专服能力；服务器暴露集取决于握手。语义矿物候选已有时效和变化检查，30 秒或 600 更新后须重新取得。 [语义组合](../../python/lua/semantic.lua) · [视口投影](../../python/exploration.py#L153) · [矿物目标令牌](../../CompanionBridge/ServerIpc/SceneMineTargets.cs#L45) |
| 持久地图 `query_map` | `Pump → AiMapRuntime.Tick → Remember` 在常规路径每隔至少 15 个游戏更新，按 120×68 窗口、遮挡和近身/日光/火把近似采样。角色保存链按世界、角色和尺寸校验并读写地图。可查全局概览、区域细图及已知比例。 | 每格一字节保存未知、空/非阻挡、实体、平台和四类液体。已知值是最后一次记录，查询不去实时世界补洞。单格没有材料种类、对象身份、观测时间、访问原因或连通关系；旧矿点只能作为复查线索。 [采样](../../CompanionBridge/ServerPlayerRuntime/AiMapRuntime.cs#L26) · [编码与查询](../../CompanionBridge/ServerPlayerRuntime/AiMap.cs#L17) · [保存与恢复](../../CompanionBridge/ServerPlayerRuntime/WorldCharacterPersistence.cs#L41) |
| 私人笔记与历史 | SQLite 按 worldUid/characterId 隔离，最多 32 条命名笔记，可记坐标、经验和玩家偏好。索引按轮注入，正文按需 recall。历史压缩保留摘录和回执索引，标明有损及遗漏。 | 模型决定写什么、何时修正。笔记没有空间索引或自动失效机制；聊天摘要也没有逐项反映世界变化。两者都不保证已探索的支路会在下次选路时被检索。 [笔记](../../python/agent_memory.py#L28) · [上下文压缩](../../python/agent_context.py#L29) |

**推断。** 项目可以发现眼前的机会。缺口在于把一次发现转为可跨任务查询、复查和淘汰的机会。例如在洞口看到铜矿，当前可以立即接近它，也可以主动记一条笔记。若玩家此时要求回家，地图仍可保留洞口地形，但没有生产链自动保留“铜矿在此、何时看见、上次到哪一步、为什么适合以后再来”并在缺铜时重新选中它。区域扫描的覆盖也不能直接算作角色亲自开图的收益，否则站着扫描就会虚增探索成绩。

## 3. 从给定目的地到自主探索

**已验证的规划边界。** `navigate_to` 根任务保存目的地，推进移动或地形改造子任务；确认改造或到达工作检查点后可以重新读世界继续。Surface Graph 从半径 24 Tile 开始，逐步扩至 128，节点上限 40,000；物理规划用实际角色状态搜索，逐段核对原生运动、支撑和落脚。这个半径约束一次图采集，整趟行程可以超过一个窗口。装备、运动参数及地形变化会影响缓存，执行偏差可进入有界恢复；未知地形和搜索预算耗尽有不同出口。[根任务](../../CompanionBridge/ServerIpc/AutonomousNavigationTask.cs#L70) · [地表图](../../CompanionBridge/Motion/SurfaceGraph.cs#L40) · [执行与恢复](../../CompanionBridge/Motion/NavigationTask.cs#L152) · [规划退出分类](../../CompanionBridge/ServerIpc/NativeNavigationWorld.cs#L179)

**地图已经参与导航，但用途很窄。** `ServerAiNavigation.HistoricalPreference` 检查身份，读取候选脚下三列的已记忆支撑；`StateSpacePlanner` 在安全绕行检查点得分相同时用它打破平局。根行程还可在子移动之间共享失败记录、搜索状态和表面图，按角色、世界、代次、槽位及运动参数失效。这里能复用本次行程的几何知识；没有看到从持久地图选一个未知边界、保留替代分支并规划返程的调用链。[历史偏好](../../CompanionBridge/ServerIpc/ServerAiNavigation.cs#L48) · [实际排序位置](../../CompanionBridge/Motion/StateSpacePlanner.cs#L292) · [行程知识的寿命](../../CompanionBridge/Motion/NavigationKnowledge.cs#L7)

**现有探索是一个可工作的短循环。** 在授权探索的程序中，`explore()` 读角色与地形、扫威胁、找局部候选、调用 `navigate_to_x`、核对结束回执，再读位置与生命。它用水平访问桶和新增 X 范围减少来回走，最多八段。主分支契约有场景落点模式的条件分支，但它检查 Worker 是否接受 `scene_exploration` 参数；当前 `worker(..., helper_source=None, module_names=())` 不接受，内置 helper 走水平实现。模型仍可自己写 Lua 使用二维 `landingRegions`。一次程序的访问桶、地标和换向次数没有自动写回为下次旅程的探索策略。[契约条件](../../python/server_ai_contract.py#L55) · [Worker](../../python/lua_worker.py#L149) · [候选与循环](../../python/lua/explore.lua#L77)

**历史验证。** 一个自然世界实验把“候选没选中”和“身体走不过去”分开测了。同一生成世界、8 个起点、左右共 16 条探索路线，固定 Lua 的旧版和新版都只有 9 条走满六段，总完成段数从 60 增到 61。曾被 helper 拒绝的一处下降通道，在同一停点直接给原生导航器目标却能走通。这个对照支持 R3 单独记录候选被过滤、导航未受理和执行失败；单个种子、固定方向、无战斗的历史数据不足以估计当前模型的成功率。[自然世界对照](../archive/NATURAL_WORLD_NAVIGATION.md)

**推断。** 未知地形首先需要挑一个能增加知识、风险可接受的观察位置。给局部寻路器一个远点，只能回答当前运动模型能否向那个点推进。若同一入口反复失败，高层要区分地形未知、确认无进展、缺材料、装备不支持、预算耗尽和执行结果未知。已有导航降级路径只对宿主确认的某些失败，放行重新观察后的一次有界输入；战略改道应发生在已核对效果、允许新决策的目标边界。不能把局部搜索耗尽写入地图为永久封路，也不能用历史地形批准眼前的落脚。[降级判据](../../python/navigation_recovery.py#L12) · [运行时续行](../../python/lua_runtime.py#L547)

## 4. 用“找铜并带回来”检查闭环

| 环节 | 现在可以做到 | 跨轮仍需谁负责 |
| --- | --- | --- |
| 观察 | 读取当前背包、危险、矿簇、站位与持久地图。 | 空闲时按变化或到期触发观察，区分历史线索和当前事实。 |
| 发现目标 | 已获委托的 Lua 能从本次观察中选矿；原生自卫能选威胁。 | 从库存缺口和探索收益生成候选，和玩家委托、危险、返程需要比较。 |
| 计划 | 同一轮可拆成找矿、接近、挖和拾取；根任务内部还可拆子动作。 | 记录跨轮的数量目标、返程点、允许的开挖范围和剩余预算。 |
| 执行 | 既有 Lua、持续任务、原生身体仲裁和进度租期能完成有界工作。 | 将每次受理的 taskId 与所属目标和阶段关联，使换轮、抢占和重连有据可查。 |
| 验证 | 指定格移除、掉落入包、实际抵达各有不同证据；可用背包复读检查数量。 | 用满足玩家语义的组合条件收尾。例如“带回 3 块铜矿”要检查入包数量和返程位置，仅核对矿格消失会误判。 |
| 重规划 | 导航可局部改道；明确的危险中断可显式恢复；模型在单轮内可换目标或站位。 | 汇总已完成部分，根据新观察选择继续、换地点、搁置或结束。结果不明时先对账；玩家停止后清除自动续行意图。 |

同一个缺口影响两种玩法。在没有玩家任务时，需要生成目标；在已经接到任务时，需要保存玩家目标并决定下一步。这两条入口可共享目标生命周期、身体执行和验证器。分开维护两套执行循环会让“谁在使用角色、旧任务还剩什么”更难回答。

## 5. 证据强度与现有验证

| 证据 | 支持的结论 | 适用范围 |
| --- | --- | --- |
| [被动公告测试](../../tests/test_game_messages.py#L22)、[M3 任务测试](../../tests/test_m3_program_tasks.py#L87) | 公告不触发模型；迟到终态可以唤醒一次；结果通知被尝试用于新提交时，游戏侧启动数仍为一。新聊天、取消、失联有独立用例。 | 模型替身；M3 使用真实 C# TCP/注册表和替代执行器。 |
| [探索夹具](../../tests/test_autonomous_exploration.py#L36)、[动态 Lua 夹具](../../tests/test_autonomous_lua.py#L113)、[导航恢复夹具](../../tests/test_navigation_recovery.py) | 一份程序能对不同观察选择不同目标，遇目标变化、生命下降或释放不确定时停止。挖掘后背包未增的场景有单独断言。 | 真实 Worker 与回环传输，游戏效果由夹具提供，程序已给定。 |
| [探索 MVP 专服记录](../archive/AUTONOMOUS_EXPLORATION_MVP.md)、[Phase 2](../PHASE2_SURFACE_GRAPH_IMPLEMENTATION.md#L70)、[Phase 3](../PHASE3_LOCAL_PLANNING_IMPLEMENTATION.md#L67) | 早期探索记录有一次程序、18 次 API、3 次原生导航及停稳，模型与聊天授权为替身。Phase 2 记录长程及偏差恢复；Phase 3 最终包记录 23 个导航/局部改造场景及独立放置复测通过。 | 历史构建、复制世界和固定夹具。覆盖特定路线和失败处理。 |
| [Boss 验证](../R2_E_BOSS_ENCOUNTERS.md#L52)、[建筑记录](../BUILDING_V2_IMPLEMENTATION.md)、[Phase 1 铜矿评测](../../tests/benchmark/scenarios.json)、[评测器测试](../../tests/test_scenario_benchmark.py#L61) | 原生战斗、施工及采矿都有独立核验手段。铜矿评测给定三种布局和 solution/noop，用原生移除证据、位置变化及场地保护条件评分。 | 固定 Lua 基线能隔离执行器问题；评测器的单测通过不能给出真实模型的探索或生存成功率。 |

**本次复核。** 在仓库根目录执行下列定向测试。七个模块合计 67 项，65 项通过，1 项断言失败，1 项报错。笔记模块另有 3 项全部通过。共 70 个不同用例，68 项通过。下列第三条测试命令单独复现了上述两个结果。

```powershell
$env:PYTHONPATH='python;tests'
& .local/lua-venv/Scripts/python.exe -B -u -m unittest test_game_messages test_m3_program_tasks test_autonomous_exploration test_autonomous_lua test_ai_map test_navigation_recovery test_scenario_benchmark
& .local/lua-venv/Scripts/python.exe -B -u -m unittest -v test_agent_memory
& .local/lua-venv/Scripts/python.exe -B -u -m unittest test_autonomous_lua.AutonomousLuaTests.test_known_failure_never_starts_following_action test_autonomous_exploration.ExplorationTests.test_action_timeout_cancels_owned_action_and_verifies_release
```

- **导航已知失败。** “只发起一个动作”和“动作未成功”的断言已通过，随后测试读取 `value.reason` 时遇到 `value=None`。测试期待 Lua 返回 `navigation_stopped`。[用例](../../tests/test_autonomous_lua.py#L147)
- **导航超时。** 监督器发出超时停止请求，动作回执为 `cancelled`，输入释放已确认，程序没有返回值。用例期待程序 `completed` 并返回 `action_timeout`。[用例](../../tests/test_autonomous_exploration.py#L147)

两者都涉及同一宿主边界：动作未完成且没有续行许可时，运行时可直接终止程序。R3 的恢复判据必须读取宿主的动作、结束及释放证据，允许 Lua 报告缺失。应先对齐这两处测试的预期，再将其纳入跨轮恢复验收。[运行时终止分支](../../python/lua_runtime.py#L579)

**待验证。** 本次未启动真实模型或游戏服，未运行全量测试。阶段专服结论引自受版本控制的记录，未重放其原始轨迹。已审查记录不足以量化无新聊天的长期探索、跨昼夜存活、受玩家打断后恢复，以及采集—制作—返程的成功率。玩家明确委托时，真实模型可能已经能用现有接口完成部分复合目标。应先用现有版本跑同场基线，再把失败归因给选目标、知觉、路线、游戏执行或验收。

## 6. Numen 与 TerraBot 的可借鉴机制

| 样本 | 读到的具体机制 | 对 Terraria AI Companion 的取舍 |
| --- | --- | --- |
| Numen，源码快照 `ce047ffb` | `CompanionLoop` 接入事件收件箱与独立的持久目标；`GoalSteward` 监听 Run 结束，身体还有有限任务时推迟评估，新 Run 或停止会作废旧评估。其设计记录解释了多个唤醒入口曾造成重复开轮。 [CompanionLoop](https://github.com/Dwinovo/minecraft-numen/blob/ce047ffb08541798ae95757877233e1a4039ff8a/numen-api/common/src/main/java/com/dwinovo/numen/api/brain/CompanionLoop.java) · [GoalSteward](https://github.com/Dwinovo/minecraft-numen/blob/ce047ffb08541798ae95757877233e1a4039ff8a/agent/src/main/java/com/dwinovo/numen/api/agent/goal/GoalSteward.java) · [设计记录](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/docs/agent-loop.md) | 收敛到一个接纳事件和续行的入口，长期目标单独保存。能由背包差值、落点或世界变化判断的目标，直接使用游戏证据；只有语义不明确时才需模型评估。保留 Terraria 的角色代次、身体占用和回复身份。 |
| TerraBot，本地 `terrabot/TerraPlayerBot` 快照，未提供独立 Git 版本 | `BotBrain` 在 Explore 分支调用 `BotAutonomy.Tick`；它发现搭桥缺木材时设置需求、数量并进入采集，也会因附近掉落调整目标。`BotRoamMemory` 记录到达点、有效期与地形签名，支持保存/恢复；`BotRouteFailureMemory` 以有向边、签名和过期时间过滤重复失败。定位见 `BotBrain.cs:852`、`BotAutonomy.cs:332`、`BotRoamMemory.cs` 和 `BotRouteFailureMemory.cs`。前述自治、漫游两个样本文件的 SHA-256 前缀为 `6798DDCA7F3B28B0`、`3939C4900FAE9C12`。 | 把资源缺口变成能检验的需求，给“到过/试过”记录失效条件。仅移植少数可测规则；将取材、站位和动作交回已有任务。整套固定优先级直接搬入 C# 会把玩家语义目标和另一套身体控制耦合。此处仅比较所读样本，未运行 TerraBot。 |

## 7. R3 的实现顺序与验收门槛

| 优先级与依赖 | 最小改动 | 用什么决定能否进入下一步 |
| --- | --- | --- |
| **P0 · 基线与目标判据**，可立即开始 | 扩展现有 benchmark 的执行入口，保留固定 Lua、noop 和原生评测器，加入内置 Agent + 真实模型对照。先定义“指定矿移除”和“取得指定数量并返回”两个不同谓词。记录初始物品、角色、世界、模型版本、预算、回执和最终位置。 | 同一布局、同一初始状态分开报告三种执行结果。途中仅挖掉矿、入包不足、只发出移动命令时，评测能指出哪项未满足。 |
| **P0 · 跨轮目标与唤醒**，依赖上述判据 | 在 Python 增加目标记录，含来源、授权范围、优先级、成功条件、剩余预算、关联 taskId、已确认进度与状态。玩家委托、空闲检查、任务结果、危险解除共用一个调度入口；普通公告保留为上下文。自主模式开关与聊天回复分开。 | 首先对一个已委托目标实现无新聊天的第二步。重复结果只推进一次；普通聊天可回应；明确停止撤销待续行；外脑接管时内脑不驱动。重启先重新绑定身份、核对世界事实和未决任务，再决定新步骤。 |
| **P1 · 探索机会与分段导航**，依赖目标和观测来源约定 | 复用 AiMap，按世界/角色记录少量地点、资源线索、已调查方向、最后实证和失败原因。先从已知与未知交界找候选，按可能新增覆盖、往返代价、危险和当前需求选点。在已知一侧选可复查的落点，交给现有导航；每段结束重新观察、更新或淘汰候选。 | 双岔路一侧阻塞时能换支路；旧矿被移除后能撤销线索；返回已知区域仍能找到基地。记录新增已知 Tile、重复访问、进展停滞、返程成功率，分开统计不可达、未知和预算耗尽。 |
| **P1 · 生存仲裁与恢复**，与机会索引可并行，依赖 P0 目标状态 | 原生自卫继续处理逐帧威胁；高层只处理会改变计划的事实，如掉血超过阈值、缺少照明、复活后装备或位置改变。危险先保命，玩家明确任务优先于自由探索。危险解除时重新检查任务、身份、材料和目标，再决定是否恢复或放弃。 | 探索中插入威胁和玩家委托，核对每次身体所有者、取消或释放回执、玩家任务终态。明确停止后自主动作数为零；未确认的旧动作不被重放。 |
| **P2 · 发展任务与地理扩展**，依赖 P1 的复查、返程及中断验收 | 用“发现材料 → 采集入包 → 满足配方 → 制作并核对库存”验证发展。记录阻碍任务的具体地形和失败类别，再决定补洞穴拓扑、运动模型或装备策略。 | 多个固定种子分开报告任务完成、死亡、停滞、玩家干预、游戏更新数和模型成本。固定 Lua 成功而模型失败时，先查决策与上下文；两者在同一落点均失败时再调查导航或执行。 |

**建议的首个 R3 验收场景。** 在隔离专服给同伴一个已知返程点、基础工具和有限自主时段。开启模式后不再逐步发令，观察它是否自行选边界、取得新增覆盖并回到安全点。在同一初态的第二轮，途中插入“带回 3 块铜矿”，随后改变一处已观察矿点。通过条件分别核对探索增量、陈旧线索失效、背包净增、实际返程、身体释放和停止后的静默。先在固定场地用脚本跑通判据，再用真实模型重复，逐次报告分子、分母和失败原因。

**架构取舍。** 直接工具调用适合单项读取，但增加入口不会赋予跨轮目标；有界 Lua 适合在一次授权内组合观察、条件与动作。R3 可保留现有 Lua 和原生 Task 的职责，把跨轮选择、授权与验证放在 Python。低频、有意义的变化才唤醒规划，逐帧风险继续由游戏线程处理。这项分工允许用同一组原生证据评价自主探索与玩家委托。
