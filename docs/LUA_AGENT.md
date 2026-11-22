# Lua Agent

Lua Agent 是模型与 Terraria 游戏 API 之间的程序执行接口。模型通过一次 `execute_lua` 提交 Lua 源码，在 Worker 中组织观察、查询和动作，并根据返回结果决定后续策略。

## 程序模型

模型可使用 Lua 的局部变量、函数、条件、循环和普通计算，将多个 API 调用组织成一个程序。游戏 API 通过宿主注入的 `api(name, args)` 函数访问。

Server AI 还提供：

- `read_skill`：按需读取已注册的 Terraria 玩法知识。
- `api("help", ...)`：查询当前连接开放的 API、参数和返回结构。
- 内置 Gameplay Modules：复用制作、建造等多步骤 Lua 流程。

模块与底层 API 的关系见 [Gameplay Modules 设计](AGENT_API_AND_GAMEPLAY_MODULES_DESIGN.md)，玩法知识见 [Gameplay MVP 第一阶段](TERRARIA_AGENT_GAMEPLAY_MVP_PHASE1.md)。

## API 发现

Server AI 在连接时根据 IPC 能力握手和服务端白名单生成可用 API。模型通过 Help 查询具体参数、返回字段和示例。不同服务器实例可能开放不同能力，因此帮助内容与当前连接保持一致。

## 任务协作

短操作可以在同一 Lua 程序中顺序完成。需要跨多个游戏更新持续推进的动作交给游戏侧任务运行时：

1. Lua 提交任务并取得 taskId。
2. 游戏线程在后续更新中推进任务。
3. Agent 可查询进度、等待终态或取消任务。
4. Agent 根据任务回执和新观察选择后续行动。

Program、Action 和游戏 Task 各自具有生命周期。Lua 程序结束后，独立游戏任务仍按任务自身的状态运行。

## 预算与执行

Lua Worker 负责程序计算和数据编码。宿主管理源码、指令、内存、API 调用、动作和执行时间预算。Worker 通过有界 JSON 消息与 Python 宿主通信，宿主再调用游戏 Bridge。

Worker 不持有 Terraria 对象，也不跨程序保留 Lua 全局状态。跨程序任务进度使用任务 API 或游戏状态表达。

## 代码入口

- `python/agent.py`：聊天轮次、模型请求和工具调用。
- `python/lua_agent.py`：Lua 工具定义与 API 契约。
- `python/lua_runtime.py`：Worker 生命周期、预算、动作监督和取消。
- `python/lua_worker.py`：Lua 执行环境与数据序列化。
- `python/atomic_operations.py`：API Schema、校验和分发。
- `python/program_tasks.py`：持续游戏任务的协调。
- [Lua Runtime](LUA_RUNTIME.md)：执行细节。
- [Lua 执行诊断](LUA_OBSERVABILITY.md)：Trace 和任务证据。
