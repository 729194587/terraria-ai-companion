# 系统架构

## 目标

Terraria AI Companion 让 AI 以 Terraria 原生 Player 的身份参与游戏。Agent 负责理解目标、观察、规划和策略调整；游戏侧负责按 Terraria 规则执行输入与动作，并验证实际效果。

系统保留两条执行路径。它们共享部分 Agent、Lua 和原子能力设计，但 Player 身份、运行环境、控制权和同步边界不同。

## 组成

| 层 | 主要职责 | 主要入口 |
| --- | --- | --- |
| Python Agent | 聊天轮次、模型调用、取消、预算、上下文和任务编排 | `python/agent.py`、`python/llm.py` |
| Lua Runtime | 在受限 Worker 中运行模型生成的程序，调度 API 并收集结果 | `python/lua_agent.py`、`python/lua_runtime.py`、`python/lua_worker.py` |
| 原子操作宿主 | 参数校验、能力过滤、动作派发、结果和证据归一化 | `python/atomic_operations.py`、`python/server_ai.py` |
| C# Mod | IPC、游戏线程调度、Player/Tile 访问、原生动作执行 | `CompanionBridge/` |
| Server Player Runtime | 独立服务器 Player 的创建、绑定、生命周期、输入和原生执行 | `CompanionBridge/ServerPlayerRuntime/`、`CompanionBridge/ServerIpc/` |
| 客户端控制 | 对安装 Mod 的本地玩家注入受限输入并维护控制租约 | `CompanionBridge/TcpControlServer.cs`、`CompanionBridge/ControlState.cs`、`CompanionBridge/Common/` |

## 执行路径

### 客户端本地玩家

外部控制客户端通过 TCP 向 Mod 发送控制请求。网络线程只处理协议和请求队列；游戏更新线程验证控制状态并应用输入。移动仍由 Terraria 原生 Player 更新、碰撞和物理逻辑完成。

该路径的控制对象是安装并启用 Mod 的客户端本地玩家，不应将其能力或同步行为推断为服务器独立 AI Player 的行为。

### 独立服务器 AI Player

服务器聊天进入 Python Agent，模型通过 `execute_lua` 提交 Lua 程序。Lua Runtime 根据当前后端能力白名单调用查询或动作 API；Python 负责参数和调用边界，C# IPC 将请求交给服务器游戏线程，最终由绑定的独立 Player 执行原生游戏逻辑。

典型路径：

`聊天 → Python Agent/LLM → execute_lua → Lua Runtime → Server AI API → IPC → 游戏线程 → 绑定的 Server AI Player`

Server AI 的具体可用 API 以运行时能力握手为准。客户端工具列表不代表服务器端全部开放。

## 任务与动作

- **模型/Agent**：决定做什么、选择目标、组合已有能力，并根据观察与回执调整策略。
- **Lua 程序**：把一个高层意图组织为有界的查询和动作序列。
- **原子操作**：表达单一游戏意图，负责原生执行和操作专属效果核验。
- **游戏侧任务**：对需要持续推进的工作维护根任务/子任务生命周期；子任务复用绑定 Actor 的原生执行上下文。
- **Terraria**：最终决定碰撞、移动、物品、Tile 和实体的真实效果。

不能把每个原子操作都扩展成隐式策略引擎，也不能把长任务的生命周期交给模型逐 Tick 轮询。

## 必须保持的边界

1. Terraria Player、Tile、Collision 和原生游戏 API 只能在正确的游戏线程与生命周期中访问；网络 I/O 和模型推理不能阻塞游戏更新。
2. Server AI 的读写始终绑定目标 Actor、World、Generation 和 Slot；不能回退到真人玩家或 `Main.LocalPlayer` 来补偿失败。
3. 请求受理、原生函数调用和输入已释放都不单独证明游戏动作完成；完成状态必须由操作专属效果证据支持。
4. 取消、超时、断连、身份变化和结果未知必须保留各自语义；不确定副作用不能盲目重放或自动重试。
5. 控制租约和输入释放是控制权安全的一部分；旧会话不能影响新会话。
6. 历史地图、场景采样和多次查询有各自的时间语义；过期或未知数据不能伪装成当前事实。
7. IL/引擎挂钩依赖已核验的 Terraria/tModLoader 版本与指纹；未知引擎环境应拒绝启用，而不是静默绕过检查。

## 代码导航

- Agent 与聊天：`python/agent.py`、`python/llm.py`
- Lua 编排：`python/lua_agent.py`、`python/lua_runtime.py`、`python/lua_worker.py`、`python/program_tasks.py`
- 原子 API：`python/atomic_operations.py`、`CompanionBridge/ServerIpc/`
- Server Player 与引擎挂钩：`CompanionBridge/ServerPlayerRuntime/`
- 客户端控制：`CompanionBridge/TcpControlServer.cs`、`CompanionBridge/ControlState.cs`
- 场景与观察：`CompanionBridge/ServerIpc/ServerSceneReader.cs` 及 `CompanionBridge/Common/Systems/`
- 运动与导航：`CompanionBridge/Motion/`
- 测试：`tests/`

本文是模块关系总览，不替代各专题对参数、协议和失败语义的权威定义。
