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
