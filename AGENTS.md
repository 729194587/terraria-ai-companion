# Agent 工作指南

## 项目概况

Terraria AI Companion 通过 Python Agent、Lua Runtime 和 C# CompanionBridge，让 AI 驱动 Terraria 原生 Player。项目有客户端本地玩家控制与独立服务器 AI Player 两条执行路径。

开始任务时先阅读 [README](README.md)、[文档索引](docs/INDEX.md) 和与当前任务相关的专题。

## 代码导航

| 模块 | 入口 |
| --- | --- |
| Agent 与聊天 | `python/agent.py`、`python/llm.py` |
| Lua Agent / Runtime | `python/lua_agent.py`、`python/lua_runtime.py`、`python/lua_worker.py` |
| API Schema 与分发 | `python/atomic_operations.py`、`python/server_ai_contract.py`、`python/server_ai.py` |
| 持续任务 | `python/program_tasks.py`、`CompanionBridge/ServerIpc/TaskRuntime.cs` |
| 客户端控制 | `python/control.py`、`CompanionBridge/TcpControlServer.cs`、`CompanionBridge/ControlState.cs` |
| 服务器 AI | `CompanionBridge/ServerIpc/`、`CompanionBridge/ServerPlayerRuntime/` |
| 观察与查询 | `CompanionBridge/Common/Systems/`、`CompanionBridge/ServerIpc/ServerSceneReader.cs` |
| 运动与导航 | `CompanionBridge/Motion/`、`CompanionBridge/ServerIpc/ServerAiNavigation.cs` |
| 测试 | `tests/` |

## 开发约定

- 开始前查看 `git status --short`，了解现有修改。
- 根据任务检索相关实现和测试，沿实际调用链理解行为。
- 修改 API 时同步检查 Schema、能力投影、Python 分发、C# IPC 和结果契约。
- 修改 Terraria 行为时关注游戏线程、Player 身份和原生调用时机。
- 验证范围根据改动选择，并在交付说明中列出实际执行的命令与结果。
- 文档说明当前实现时，以当前源码和运行时能力为依据；阶段报告用于追溯当时的实现与验证。

## 文档维护

README 介绍项目，ARCHITECTURE 说明模块关系，DEVELOPMENT 说明开发流程，ENGINEERING 记录跨模块设计。接口与模块行为写入对应专题文档。

优先更新已有文档。新增文档应有明确读者、独立主题和长期维护价值。
