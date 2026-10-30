# Lua可观测性与分层预算（0.1.40）

基线：干净工作区 `2325fa62096b81df35f0c241ca455dd2889aadd6`。本批改善执行与调试基础，
不增加砍树、拾取、合成或家具放置能力，没有部署或启动真实游戏。

## 借鉴Numen的范围

固定参考 `e32d981eff9da39356704417308dcafa949425ec`：

- [ScriptRun](https://github.com/Dwinovo/minecraft-numen/blob/e32d981eff9da39356704417308dcafa949425ec/script/src/main/java/com/dwinovo/numen/script/ScriptRun.java)的调用记录带源码行号：我们在Worker协议增加line，把调用/结果/错误关联起来。
- [LuaEngine](https://github.com/Dwinovo/minecraft-numen/blob/e32d981eff9da39356704417308dcafa949425ec/script/src/main/java/com/dwinovo/numen/script/lua/LuaEngine.java)与[shell设计](https://github.com/Dwinovo/minecraft-numen/blob/e32d981eff9da39356704417308dcafa949425ec/docs/shell.md)区分模型提交程序和内部API驱动：我们拆分预算，保留现有回执写法，不引入pcall或自动重试。
- [TaskApi](https://github.com/Dwinovo/minecraft-numen/blob/e32d981eff9da39356704417308dcafa949425ec/numen-api/common/src/main/java/com/dwinovo/numen/api/task/TaskApi.java)体现任务身份、运行状态和剩余预算：我们记录对应事实，没有迁入后台任务/世界时间定时器。
- [寻路日志设计](https://github.com/Dwinovo/minecraft-numen/blob/e32d981eff9da39356704417308dcafa949425ec/docs/pathing.md)的一行一个事件、记录实际事实：沿用JSONL与既有导航日志，补API边界和阶段标记。

不合并其Java/Minecraft执行器。Terraria的动作身份、租约、取消及endEvidence继续决定执行结果。

## 三层预算

| 默认整轮上限 | 计数 |
| --- | --- |
| 7次模型工具 | 直接工具和execute_lua包装；内部API不再扣这一额度；仍每次模型响应一个工具并预留反馈 |
| 64次Lua内部API | 多份程序共享；查询、等待、动作、参数失败和blocked尝试均计数；宿主最多可配256 |
| 8次变更动作尝试 | Lua、直接原子动作、旧move共享；宿主可配0..32；停止工具不占变更额度 |
| 60秒Lua墙钟 | 多段程序共享，含Worker启动和API等待；清理另有默认3秒预算；直接工具仍用原有期限 |

资源耗尽后停止本轮行动，不切换直接工具绕过；停止和必要清理保留原有路径。
即使Lua恰好用完API额度并正常返回，后续直接原子变更和旧move也被拒绝；
累计Lua墙钟达到上限时采用同一屏障。不会把这份正常完成的程序回执改成失败。
直接只读观察及wait仍可使用，继续受模型工具次数及各自期限限制，不恢复Lua额度；
API用尽后不能再提交Lua程序（包括只读程序）。stop_action/stop_movement不受变更屏障限制，
但不豁免真实停止结果的身份和结束证据校验，必要收尾保持独立清理预算。
直接挖掘恢复的固定查询allowance保留，其动作也计预算。失败停止/未知会话封锁没有放宽。
runtimeLua返回剩余模型工具、内部API、动作和秒数；remainingApiCallsIfExecutedNow还考虑能否提交包装。
成本tool_calls仍统计包装与内部工具结果总数，它与模型工具预算消耗不同。

## 源码行号与阶段日志

```lua
log('读取工具')
local selected = api('inspect_inventory', {mode='selected'})
log('按真实回执判断下一步')
return selected
```

log只接受字符串，每程序最多32条、每条512 UTF-8字节，不调用游戏、不扣API/动作次数，
仍受指令/内存/墙钟限制；超限为log_limit或bad_argument。它是程序自述，不是目标完成证据。
没有新增文件、终端、debug、require或pcall权限。

Worker的私有行钩子捕获API位置（包括尾调用）；宿主验证行号范围。
关联链为 `task_id → lua_run_id/source_sha256 → lua_call_id/lua_line → 游戏actionId/session/actionGeneration/worldSessionId`。
参数校验失败也有开始和错误事件；unknown动作先记录实际回执再结束；取消/Worker退出保留cleanup。
失败屏障或allow_actions=false合成的blocked回执未派发动作，不占动作额度，
不替换最后真实动作/释放状态，也不因缺少终态证据封锁Bridge；真实派发（含stop_action）
若返回未知或缺少释放证据，仍封锁整个Bridge，不能通过新建Runtime绕过。
failure字段包含kind/message/call/line/retryAllowed/actionState；retryAllowed=false不授予重试权限。
Lua自身错误带errorLine，成功API之后的脚本错误不会被归咎于上一个API。

## 常开摘要与完整Trace

CLI继续默认写 `.local/diagnostics/agent-runtime-*.jsonl`，可用--log-file指定。
新增API事件、阶段、任务结束、预算/成本，以及扫描数量/未知/截断/范围/少量候选、内容解析歧义、
探索时效/覆盖/脚下信息、人物与所选工具摘要。摘要注明省略数量，不收原始地形网格、完整背包或模型对话。
完整Trace仍由TERRARIA_AGENT_TRACE=1显式开启，可能包含完整模型上下文与观察，只写本地。
两类日志保留轮转和已注册凭据脱敏，新增丢失/写入失败计数及结束时health；超大记录标记保留Lua身份。
摘要遇异常字段记录summaryUnavailable，日志故障不改变动作结果。
模型回执中的trace只保留简短步骤元数据；详细感知与动作证据进入任务包。跨轮省略脚本返回值及阶段文字，
分别标记valueExpired/logsExpired，避免历史观察或自述被当成当前状态。

背包原生异常继续为unknown，额外保留permission/transfer_or_swap/recipe_refresh阶段、异常类型、
最多512字符消息与2048字符堆栈。前后库存和可能已变更的含义不变；这不是所有原子操作的统一异常改造。

## 任务证据包与导出

默认 `.local/evidence/<task_id>/`；一轮Agent任务一个包，多份程序放在一起；独立LuaRuntime自己建包。
manifest.json记录身份、生命周期/目标结果区别、次数、源码索引及丢失/错误计数；
source-<sha256>.lua是脱敏源码副本；events.jsonl保存API、感知摘要、动作回执和收尾。
它与全局64份ProgramStore独立，避免旧源码先被淘汰。

事件每包2MiB、单条64KiB；超大事件留身份标记，容量耗尽计数。源码最多64份，每份脱敏后也限16KiB。
默认保留最近32个已完成受管任务；运行中/崩溃未完成、符号链接或含用户新增文件的目录不自动删除，
所以不是整个目录的硬磁盘配额。无法创建/写入会报告available/health，不改变游戏结果。

必要清理结束、manifest.finished=true后可导出，不覆盖已有ZIP：

```powershell
.local/lua-venv/Scripts/python.exe python/evidence.py .local/evidence/<task_id> .local/task-evidence.zip
```

只收受管文件，忽略.env、其他文件和符号链接；不执行源码、不上传或重放动作。
崩溃未完成目录可人工检查。证据包不是确定性游戏回放，没有保存完整世界、同步状态或所有Mod副作用。

## 验证边界

测试使用真实Lua Worker、模型/Bridge替身及实际C#代码的合成世界协议测试；覆盖行号、预算共享、
日志限额、脱敏、保留/导出、磁盘不可写、原生异常及原有取消/身份/释放证据。
真实tModLoader构建只验证API兼容性；未部署、未做真实模型/真实游戏或多人验收。

2026-10-24边界修正验证：完整Python回归405项通过（含API精确耗尽后直接use_item/旧move、
只读与停止保留、未派发blocked不封锁、失败屏障下停止与真实未知封锁），
C#离线/协议回归通过，真实tModLoader构建0错误、0警告。
本地日志为 `.local/lua-boundary-full-python.log`、`.local/lua-boundary-csharp.log`、
`.local/lua-boundary-build.log`；构建产物在 `.local/lua-boundary-build`，均不提交仓库。


## Server AI 跨层追踪补充（2026-10-30）

检查结果：既有任务包已包含Lua源码、API顺序/行号/预算、原子终态与清理，完整Trace已包含模型HTTP body和API可见响应；但任务包只有观察摘要，ServerAiBridge缺少IPC请求/回执事件，原生导航缺少原子actionId关联与逐检查点事件。另确认navigation_search_timeout在上一轮只保留稳定码，遗漏Route.FailureReason中的候选拒绝详情。

本轮复用现有机制：

| 信息 | 获取与关联 |
| --- | --- |
| 模型实际提交上下文 | 开启完整Trace后，llm_request.body镜像到当前任务包；含messages、tools、runtimeInventory/runtimeLua和经过投影/省略的历史反馈，以实际HTTP提交版本为准，不能拿完整游戏状态冒充模型看到的观察 |
| API可见模型输出 | llm_response及llm_return按model_request_id关联；tool_call_id继续关联Lua程序及源哈希 |
| Lua/API | 既有task_id→tool_call_id→lua_run_id/source_sha256→lua_call_id/lua_line不变；源码、API开始/结果及失败屏障沿用原事件 |
| Server AI IPC | 新增server_ai_ipc_request/reply/error；本地随机connection_id加request.id/request_id解决多连接请求ID重复；请求、accept/running/terminal回执及断连/超时可按Lua上下文对应。记录在请求线程，保留to_thread传播的任务上下文，不在无任务上下文的接收线程猜归属 |
| 原生导航 | atomic-native-start.actionId与atomic-navigation.detail.actionId相同；detail另含world/generation/slot/session/gameUpdate。navigation_plan记录planStart、targetX/targetRegion、planNumber、Expansions/耗时/ReachedGoal/TimeBudgetReached及searchFailureReason；navigation_checkpoint记录已核验的预测/实际落点、速度、动作序号；navigation_state/cancel保留终止或清理原因，不逐帧打印 |
| 超时候选诊断 | NavigationSnapshot.searchFailureReason保留Route.FailureReason；ServerAiNavigation超时终态reason仍为navigation_search_timeout，reasonDetail带真实搜索拒绝详情；非空部分路线照常执行，不把deadline当不可达 |
| 失败后的真实行为 | 下一llm_request检查反馈确实进入模型请求，下一llm_return与Lua/IPС开始事件检查模型实际提交与宿主实际派发。reply_prepared/send_chat_start、say IPC和send_chat_result区分模型原文、宿主改写、发布尝试与发布确认；准备发送不证明玩家已看到 |

完整模型/IPC payload仍需显式`TERRARIA_AGENT_TRACE=1`；未开启时，不向常开摘要或任务包新增原始对话/背包/网格，不承诺从默认日志恢复完整模型输入。新增emit_full只镜像已获完整Trace开关授权的数据到当前已有任务包，不另建日志体系。完整Trace仍为5MiB滚动文件，任务包仍为2MiB/单事件64KiB/64份源码，各自独立截断；超限标记补保留model_request_id/connection_id/request_id，需查看manifest的eventsDropped/writeErrors和Trace健康。若模型body超64KiB，任务包可能只剩标记，应结合未被轮转的完整Trace；不能把缺失记录解释为未调用。

沿用注册凭据与敏感键脱敏，不记HTTP headers/.env，不记录或推断模型内部隐藏推理。源码redacted标记、哈希语义不变。镜像写失败被隔离；原生NavigationDiagnostics序列化/写入失败也仅增加内部WriteErrors，不改变导航输入、预算、路线、终态和后续执行。新增日志位于原定规划计时段之外；并非性能压力或磁盘故障下实时性保证。原生日志由tModLoader既有日志设施管理，没有扩大游戏世界/实体采样。

验证：NavigationOnly定向组、IPC定向组通过；Python新追踪4项、test_trace.py 11项、test_lua_observability.py 14项通过；新包构建0错误0警告，隔离原生既有读取/选栏/导航/物品/挖掘/取消及进程清理通过，脚本核验10个导航事件与8个原生开始事件的actionId关联。首轮测试夹具曾把导入的TestCase也带入发现（20项中新增用例错误，其余通过），已改模块导入并仅运行当前4项；另修正断连测试误取前置state请求、以及把模型原文误当宿主实际发布文本的断言。没有运行全量测试，没有调用真实模型或部署现用profile。

[可人工分析的示例与分层结论](evidence/server-ai-trace/analysis.md)分别保留：A，HTTP/执行器替身的完整失败任务与ZIP；B，真实规划器在碰撞替身上产生的超时详情；C，真实原生成功动作日志和回执。三组不能拼接成同一次实机任务。仍无法观察完整世界/所有Mod副作用、丢失的轮转或超限payload、客户端实际渲染/玩家是否阅读、模型内部推理；没有确定性世界回放，也未构造原生强制搜索超时或真实LLM自主失败场景。
