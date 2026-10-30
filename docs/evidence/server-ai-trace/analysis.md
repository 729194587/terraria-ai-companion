# Server AI 追踪示例

## 示例 A：可复现失败任务

运行 `tests/test_failure_trace.py` 的 test_failure_decision_to_terminal_and_readonly_followup，设置 TRACE_FAILURE_EXAMPLE 导出。HTTP模型和游戏执行器均为替身；Agent、LLMClient、两个loopback协议、Lua Worker、原子分发与失败屏障为正式实现。没有付费模型调用或真实游戏失败重演。

- task_id：`e793de8601104f25ac67266938fd2642`
- 导航 lua_run_id：`b6ab6805852c4e9e963f11cf81e93789`
- 导航 actionId：`5b6de6b8df9b4e7f9f031aa4307d1e46`
- 查看同名任务目录的 events.jsonl 或直接解压 task-evidence.zip。agent-trace.jsonl 是同时开启的完整Trace副本。

人工分析顺序：
1. llm_request.body 是实际交给 HTTP 客户端的完整请求（包括本轮 runtimeInventory/runtimeLua 和历史反馈）；llm_response/llm_return 通过 model_request_id 对应。凭据及已注册秘密脱敏，headers不记录。
2. llm_return.assistant.tool_calls 中的 execute_lua 参数对应 source-<sha256>.lua；Lua事件带 tool_call_id、lua_run_id、source_sha256、lua_call_id 和行号。第一程序依次读取get_player_state、请求navigate_to_x。
3. server_ai_ipc_request/reply 用 connection_id + request.id/request_id 对应；start中的actionId对应后续action轮询及Lua最终回执。connection_id是本地随机关联ID，不是新协议字段。
4. 替身返回navigation_search_timeout、候选拒绝详情和失败/释放证据。这些数据是测试注入，不是实际原生planner输出。下一llm_request确实含该失败；第二程序仅get_player_state，没有第二次start导航；第三模型响应尝试普通文字收尾。
5. llm_return展示模型原文，reply_prepared/send_chat_start展示宿主整理后的拟发布文本；Server AI say请求/回执、send_chat_result及真实loopback接收端断言证明实际发送。该例宿主沿用既有逻辑，将模型文字改为含navigation_search_timeout的未完成说明，不能把模型原文当成玩家看到的最终文本。

任务manifest的finished、eventsDropped、writeErrors及source redacted字段用于判断证据覆盖；本例保留原始生成结果，不手改ID或回执。本例eventsDropped=2来自两次LuaRuntime尝试归档同一26177字节的既有探索辅助源码，超过原16KiB源码上限；该辅助源码未归档，也没有在本例调用explore。两个模型实际提交的Lua程序和三次完整模型请求均在包内并已断言逐项对应，不能把这两次计数说成丢失模型请求。运行工作区保留用户此前未提交的探索改动；辅助源码摘要b69f2b857cce23f2896b25e354fc38c98eeb43167ad6e86aa21dadccb810ea0e只能用于标识，不能从哈希恢复原文。

## 示例 B：实际规划器超时详情

trace-navigation-timeout.jsonl来自NavigationOnly中的真实StateSpacePlanner→NavigationTask→NavigationDiagnostics，碰撞世界是替身，故意延迟一个Move触发原有25ms deadline。行首保留原日志的`Navigation `前缀。搜索为空时稳定reason为navigation_search_timeout，而searchFailureReason包含实际walking blocked候选计数，planStart/targetRegion/planNumber/expansions/耗时均在同一事件中。不是示例A中同一次动作，不能按A的ID拼接。

## 示例 C：真实原生成功动作的跨层关联

native-navigation-events.jsonl逐行保留隔离复制世界server.log中的atomic-native-start与atomic-navigation原字节；native-result.json保留真实Lua动作结果。共同actionId与world/generation/slot/session串起每次规划、已核验检查点、最终到达和取消清理。共10个导航事件、8个原生开始事件；关联集合已经脚本核验。该场景使用确定性诊断Lua，没有LLM；不等于示例A真实模型自主导航验收。

原始完整server.log仍保留在本地隔离profile。导航日志不逐帧记录所有输入/碰撞，也不保存整个世界或全部Mod副作用；这里只验证正式原生日志关联和原有动作回归，未强制构造原生搜索超时。
