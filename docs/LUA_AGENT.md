# Lua Agent

Lua Agent 允许模型在一次 `execute_lua` 工具调用中提交完整程序，通过受限 Worker 顺序组合当前后端开放的游戏 API。模型负责选择目标和策略；宿主负责参数校验、能力过滤、预算、动作派发、失败屏障和结果核验。

## 调用模型

- Agent 对模型只注册 `execute_lua`；内部 Lua `api(name, args)` 调用由宿主分发。
- 模型可使用局部函数、条件、有限循环和普通 Lua 计算，根据观察结果动态选择后续动作。
- 多步骤任务优先组合现有查询和原子操作；不要假设存在未开放的客户端 API 或专用任务函数。
- `explore()` 等 helper 是可选程序辅助，不是强制探索策略，也不替代 Agent 自主决策。
- Server AI 的 API 集合由实际 IPC 能力握手、服务端白名单和契约描述共同决定；客户端 API 清单不代表服务器可用能力。

## 预算与隔离

Lua Worker 使用受限运行环境。调用次数、动作次数、源码大小和累计 Lua 执行时间均由宿主控制；多份程序共享当前轮次剩余额度。模型推理时间不计入 Lua 执行时间。

源码和执行结果可通过本地诊断链路关联。Trace 可能包含游戏观察、脚本和玩家数据，应按本地诊断资料管理，不提交临时日志或密钥。

## 结果语义

- Program 正常返回只说明 Lua 程序结束，不代表玩家目标完成。
- 内部 API 的结果必须按各自状态与效果证据解释；脚本返回值不能覆盖宿主失败。
- 已知失败后遵守本轮行动屏障；unknown 表示副作用或结果无法确定，不能包装成成功或自动重试。
- 动作完成、输入释放和目标达成是不同事实。需要持续执行的任务使用游戏侧任务 API，并按任务身份查询/等待/取消。
- 新聊天、取消和 Agent 关闭必须先完成既有安全收尾；旧轮次不得发布迟到回复或启动新动作。

## 代码入口

- `python/agent.py`：聊天轮次与模型工具注册。
- `python/lua_agent.py`：Lua 工具与模型契约。
- `python/lua_runtime.py`、`python/lua_worker.py`：Worker、预算和执行协议。
- `python/atomic_operations.py`：API Schema、分发与结果处理。
- `python/server_ai_contract.py`、`python/server_ai.py`：Server AI 能力投影和调用适配。
- [Lua Runtime](LUA_RUNTIME.md)：沙箱、取消、失败恢复和执行细节。
- [Lua 可观测性](LUA_OBSERVABILITY.md)：Trace 与诊断格式。
