# 系统架构

Terraria AI Companion 将 AI 的目标决策与 Terraria 内的实际执行连接起来。Python Agent 负责理解目标、选择策略和组织操作；CompanionBridge 在游戏线程中读取世界状态、控制 Player，并调用 Terraria 原生机制。

系统有两条执行路径：客户端本地玩家控制，以及 Dedicated Server 中的独立 AI Player。两者共享部分协议和游戏能力，但运行环境与 Player 生命周期不同。

## 系统组成

| 组件 | 职责 | 主要入口 |
| --- | --- | --- |
| Python Agent | 聊天轮次、模型调用、上下文、工具调度与回复 | `python/agent.py`、`python/llm.py` |
| Lua Agent | 将模型目标组织为可执行的 Lua 程序 | `python/lua_agent.py` |
| Lua Runtime | Worker 生命周期、预算、API 调用、取消与结果收集 | `python/lua_runtime.py`、`python/lua_worker.py` |
| API 宿主 | Schema 校验、能力投影、参数分发和结果契约 | `python/atomic_operations.py`、`python/server_ai_contract.py` |
| 任务协调 | 提交、查询、等待和取消持续游戏任务 | `python/program_tasks.py` |
| Server AI Bridge | Python 与服务器 Mod 之间的请求和事件通信 | `python/server_ai.py` |
| CompanionBridge | 游戏线程调度、观察、输入、原生动作和同步 | `CompanionBridge/` |
| Server Player Runtime | 独立 Player 的创建、身份绑定、生命周期与引擎集成 | `CompanionBridge/ServerPlayerRuntime/` |

## 客户端控制路径

外部控制程序通过 TCP 向 CompanionBridge 发送控制请求。Mod 接收请求后，将输入状态交给游戏更新线程处理。Player 的移动、碰撞和物品行为仍由 Terraria 自身更新流程完成。

主要入口：

- Python：`python/control.py`
- C#：`CompanionBridge/TcpControlServer.cs`、`CompanionBridge/ControlState.cs`
- Player 集成：`CompanionBridge/Common/Players/CompanionPlayer.cs`、`CompanionBridge/Common/Systems/CompanionSystem.cs`

## 独立服务器 AI Player 路径

服务器模式通过本地聊天入口接收玩家指令。Python Agent 调用模型，并通过 `execute_lua` 提交 Lua 程序。Lua Worker 使用宿主注入的 `api(name, args)` 访问当前连接开放的查询和动作。Python 完成参数校验与分发，C# IPC 将请求送到游戏线程，由绑定的独立 Player 执行。

```text
玩家聊天
  → Python Agent / LLM
  → execute_lua
  → Lua Runtime / Worker
  → API 校验与分发
  → ServerAiBridge / IPC
  → 游戏线程
  → 绑定的 Server AI Player
```

服务器 AI 的 API 集合由运行时能力握手、服务端白名单和模型契约共同形成。具体接口说明见 [API 契约](API_CONTRACT.md) 与 [Server AI 能力](SERVER_AI_CAPABILITIES.md)。

## 行为执行模型

系统将高层决策与游戏动作分层：

1. **Agent** 选择目标、制定策略，并根据观察和执行结果调整行动。
2. **Lua 程序** 将多步骤意图组织为查询与动作序列。
3. **原子 API** 提供单项游戏操作及其参数、结果和证据契约。
4. **游戏侧任务运行时** 推进需要跨多个游戏更新持续执行的动作。
5. **Terraria 原生机制** 完成移动、碰撞、物品使用、Tile 交互和实体行为。

普通 Lua 程序与持续游戏任务具有各自的生命周期。任务运行时负责持续动作的推进，Agent 在合适的时机查询结果并决定后续策略。

## 线程与身份模型

网络线程处理协议和纯数据，Terraria Player、Tile、Collision 及原生游戏 API 在游戏线程访问。Server AI 的操作绑定具体 World、Actor、Generation 和 Player Slot，身份随角色生命周期核验。

动作结果包含受理状态、执行终态及操作相关证据。不同操作根据实际效果定义完成条件，例如物品数量变化、目标 Tile 变化、实体命中或角色落脚。

## 代码导航

- Agent 与模型：`python/agent.py`、`python/llm.py`
- Lua 执行：`python/lua_agent.py`、`python/lua_runtime.py`、`python/lua_worker.py`
- API 契约与分发：`python/atomic_operations.py`、`python/server_ai_contract.py`、`python/server_ai.py`
- 任务协调：`python/program_tasks.py`
- Server IPC：`CompanionBridge/ServerIpc/`
- 独立 Player：`CompanionBridge/ServerPlayerRuntime/`
- 客户端控制：`CompanionBridge/TcpControlServer.cs`、`CompanionBridge/ControlState.cs`
- 运动与导航：`CompanionBridge/Motion/`
- 测试：`tests/`
