# Lua Runtime

Lua Runtime 在 Python 宿主中运行模型提交的程序，并通过宿主 API 访问 Terraria。Worker 负责受限计算；宿主负责预算、API 参数与能力校验、游戏动作监督、取消收尾和结果语义。

## 执行模型

1. Agent 将模型返回的完整 Lua 源码交给 Runtime。
2. Runtime 为程序创建隔离 Worker，并通过有界 JSON 消息传递请求与结果。
3. Worker 只执行 Lua 计算；`api(name, args)` 请求由宿主验证并分发。
4. 游戏状态和原生动作仍由 Bridge/游戏线程处理。Worker 不直接访问 Terraria 对象。
5. Runtime 收集程序返回值、内部 API 结果、动作终态和独立保留的观察证据。

Worker 重启后，Lua 全局变量和局部程序状态不跨程序保留。需要跨调用的任务状态必须使用宿主明确提供的任务 API，而不是依赖 Worker 内存。

## 沙箱与数据限制

Worker 使用受限环境，提供基础 Lua 类型/迭代/错误处理、受控 `pcall`、`api`、JSON 与日志 helper，以及有限的 `math`、`table` 和字符串函数。没有 `os`、`io`、`require`、`xpcall` 或 `print`。

当前 Worker 对递归结构施加 20,000 节点和 24 层深度限制。源码、请求帧、返回值和 IPC 帧均有独立字节上限，具体常量以 `python/lua_worker.py` 为准。Worker 是受限执行环境，不应描述为 OS 级敌对代码沙箱。

## 预算

默认可信限制定义在 `python/lua_runtime.py:Limits`：

| 项目 | 默认值 |
| --- | ---: |
| 单轮累计 Lua 执行时间 | 60 秒 |
| 内部 API 调用 | 64 次 |
| Lua 指令 | 1,000,000 |
| Lua 内存 | 16,000,000 字节 |
| 动作预算 | 8 次 |
| Worker 清理等待 | 3 秒 |

限制可由宿主配置，但必须通过 Limits 校验。Lua 执行时间包含程序执行期间的 API 等待，不包含 LLM 推理和模型请求耗时。多份程序共享当前轮次剩余额度；不能通过重新提交程序重置预算。

## API 调用与失败语义

- API 名称、参数 Schema、范围和当前后端能力由宿主验证；Lua 不能自行扩大白名单。
- 本地参数/范围拒绝可返回受控拒绝信息；受理后的游戏动作失败由宿主结合动作终态和结束证据判断是否允许继续。
- `pcall` 只能捕获普通 Lua 错误和宿主明确允许继续的本地拒绝。它不能吞掉真实动作失败、unknown、取消、通信/协议错误或资源终止。
- `continuationAllowed=true` 必须由宿主基于该操作的无副作用及释放证据授予；Lua 脚本不能自行声明。
- 已完成动作不会因后续 Lua 错误回滚。程序失败不能覆盖已确认的动作事实，也不能将未完成目标包装成成功。
- `unknown` 表示副作用或结果无法确定。保持连接/动作隔离，不自动重放，不通过更换 API 入口绕过失败屏障。

## 取消与收尾

取消、超时和 Worker 异常由宿主监督。Runtime 尝试在独立清理期限内停止 Worker，并等待游戏动作进入可信终态、完成输入释放和必要效果核验。若收尾结果未知，不复用可能仍持有动作的 Bridge。

Program、Action 和游戏 Task 是不同生命周期。普通 Program 返回不自动取消已受理的独立游戏 Task；任务提交、查询、等待和取消见 [游戏侧任务运行时](M1_TASK_RUNTIME.md)。等待超时不等于任务执行超时。

## 结果与可观测性

- Program 返回值是脚本数据，不是游戏事实。
- Action 回执包含操作状态和操作专属证据；输入释放本身不证明目标完成。
- 宿主单独保留最后成功的只读观察，和脚本返回值分离；超限省略时明确标记，不把旧观察伪装成新鲜状态。
- 源码按内容哈希保存到本地诊断目录；Trace 与日志可能包含世界状态、脚本和玩家数据，不应默认提交到 Git。
- 诊断字段和事件关联方式见 [Lua 可观测性](LUA_OBSERVABILITY.md)。

## 实现入口

- `python/lua_runtime.py`：预算、Worker 生命周期、API 派发、动作监督和收尾。
- `python/lua_worker.py`：Lua 沙箱、序列化和数据限制。
- `python/lua_agent.py`：模型工具契约。
- `python/program_tasks.py`：独立游戏任务的结果交接。
- `python/atomic_operations.py`：API Schema、后端校验和动作结果。

## 内置 Gameplay Module 的执行边界

宿主从 `python/lua_modules.py` 的固定注册表按本次握手筛选依赖。每个新 Worker 从 `python/lua/modules/` 加载选定的 Lua 源文件，检查返回表及导出函数，在本次程序的 `terraria` 表中发布。制作需要 `query_recipes` 和 `craft_recipe`，施工需要 `build_diff`、`build_place`、`submit_task` 和 `await_task`。模块的游戏调用使用现有宿主 callback，共享程序的调用、动作、时间和序列化预算。

正常返回值在 `value`。若原生动作触发失败屏障，宿主可在 `moduleResult` 中保留内置模块的部分产量或施工报告；工具终态和原始动作证据仍在外层回执。已确认输入释放的施工失败允许模块对原蓝图做一次只读复查。结果未知时立即停止，后续脚本语句及包裹调用的 `pcall` 均不能恢复执行。
