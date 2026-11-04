# Lua 执行诊断

本项目有三类本地诊断数据，彼此独立，不作为游戏结果的权威来源：

| 类型 | 默认位置/开关 | 用途 |
| --- | --- | --- |
| Runtime 摘要 | `.local/diagnostics/agent-runtime-*.jsonl` | 有界运行事实、工具结果摘要、身份/覆盖/截断信息 |
| 完整 Trace | `.local/diagnostics/agent-trace-*.jsonl`；设置 `TERRARIA_AGENT_TRACE=1` | 调试完整模型请求/响应、工具上下文和跨层事件 |
| 任务证据包 | `.local/evidence/<task_id>/` | 将同一任务的 Lua 源码、API 事件、动作回执和清理结果关联起来 |

Runtime 摘要不记录完整模型对话、原始网格或完整背包。完整 Trace 可能包含聊天、模型上下文、观察和玩家数据；只在确有诊断需要时启用，并按敏感本地数据保管。诊断写入失败或记录被截断不得改变游戏动作结果。

## 关联关系

常用关联键：

`task_id → chat_event_id/tool_call_id → lua_run_id/source_sha256 → lua_call_id/lua_line → actionId/session/actionGeneration/worldSessionId`

Server AI IPC 事件还使用 `connection_id` 与 `request_id`，避免不同连接中的请求编号冲突。导航诊断使用 ActionId 关联规划、检查点和终止事件。具体事件字段以 `python/agent_trace.py`、`python/agent_diagnostics.py`、`python/evidence.py` 和相应执行器为准。

Trace 中的模型请求正文表示实际提交给模型的上下文，不代表游戏的完整状态，也不包含模型隐藏推理。模型响应、宿主分发、游戏侧原生执行、聊天发送尝试和最终发布确认是不同阶段，不能拼接成一个未经验证的“成功”。

## 任务证据包

任务包由宿主自动写入本地 `.local/evidence/`，不需要提交到 Git。当前实现对事件总量、单事件大小、源码数量和源码大小设有上限；默认只清理符合自身格式且已完成的旧任务目录。写入失败、容量截断和省略计数会保留诊断信息。

需要人工传递时，可在任务清理完成后用 `python/evidence.py` 导出有界 ZIP。导出只打包受管文件，不重放动作，也不是确定性游戏回放：它不保存完整世界、所有 Mod 副作用或可重建物理状态。

## 结果解释

- 脚本 `log()`、探索报告和模型文本是自述，不是游戏证据。
- API 回执必须结合 ActionId、身份、结束证据和对应世界状态解释。
- 输入释放不证明目标效果；动作结果 `unknown` 不得升级为成功。
- 记录缺失可能来自未启用 Trace、轮转、容量上限或写入失败，不能据此推断事件没有发生。
- 不同测试替身、隔离原生运行和图形实机的记录不能拼接成同一次游戏任务。

## 实现入口

- `python/agent_diagnostics.py`：默认 Runtime 摘要。
- `python/agent_trace.py`：显式开启的完整 Trace 与脱敏。
- `python/evidence.py`：本地任务包、保留和导出。
- `python/lua_runtime.py`、`python/server_ai.py`：Lua、IPC 和动作关联。
