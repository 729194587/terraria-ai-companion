# Terraria AI Companion

Terraria AI Companion 是一个让 AI 以 Terraria 原生 Player 身份参与游戏的项目。Agent 理解玩家目标、观察世界并制定行动；CompanionBridge 将这些行动交给 Terraria 原生机制执行。

项目包含两种运行方式：

- **客户端控制**：通过 Mod 控制安装了 CompanionBridge 的本地玩家。
- **独立服务器 AI Player**：在 Dedicated Server 中创建独立角色，由 Python Agent 驱动，可通过 Lua 组合游戏 API 完成任务。

## 架构

- **CompanionBridge（C# / tModLoader）**：管理游戏内 Player、世界状态、原生输入、动作执行和 IPC。
- **Python Agent**：处理聊天、模型调用、上下文、工具调度和任务协调。
- **Lua Runtime**：执行模型生成的有界 Lua 程序，组合查询与动作 API。
- **游戏侧任务与原子操作**：承载持续动作，并将执行结果反馈给 Agent。

服务器 AI 的主要执行链路：

`玩家聊天 → Python Agent → execute_lua → Lua Runtime → Server AI API → IPC → Terraria 游戏线程 → 独立 AI Player`

## 技术栈

| 部分 | 技术 |
| --- | --- |
| 游戏 Mod | C#、tModLoader |
| Agent | Python |
| 行为编排 | Lua |
| 游戏通信 | TCP、服务器 IPC |
| 测试 | Python unittest、C# 离线测试、隔离 Dedicated Server 验证 |

## 许可证

本项目采用 [MIT License](LICENSE)。第三方组件保留各自的许可证和归属声明。

## 致谢

项目架构讨论与文档整理过程中使用了 ChatGPT 的 AI 辅助。

## 文档

- [文档索引](docs/INDEX.md)：按模块查找技术资料。
- [系统架构](docs/ARCHITECTURE.md)：组件职责与执行流程。
- [开发指南](docs/DEVELOPMENT.md)：环境、构建和验证。
- [工程设计](docs/ENGINEERING.md)：跨模块设计原则。
- [开发路线图](docs/DEVELOPMENT_ROADMAP.md)：当前项目方向。
- [Agent 工作约定](AGENTS.md)：代码代理的仓库协作方式。

当前 Mod 版本记录在 [CompanionBridge/build.txt](CompanionBridge/build.txt)。
