# Terraria AI Companion

让 AI 通过 Terraria 原生 Player 参与游戏：理解玩家目标、观察世界、规划行动，并组合游戏原生能力完成探索、生存与交互。

当前项目同时包含两条执行路径：

- **客户端控制**：通过 CompanionBridge 控制安装 Mod 的本地玩家，保留 Terraria 原生输入、物理和动作机制。
- **独立服务器 AI Player**：服务器创建并维护独立 Player；Python Agent 通过聊天与模型交互，使用 Lua 编排和服务器原生 API 执行任务。

## 当前技术栈

- **游戏侧**：C# / tModLoader Mod（CompanionBridge）。
- **Agent 侧**：Python，负责聊天轮次、模型调用、Lua Worker、预算、取消和结果整理。
- **程序编排**：Lua；模型通过 `execute_lua` 组合当前后端实际开放的 API。
- **通信**：客户端控制 TCP 与服务器 AI IPC；游戏对象和 Terraria API 的访问受游戏线程及 Actor 绑定约束。

当前 Mod 版本见 [CompanionBridge/build.txt](CompanionBridge/build.txt)。版本号不等于所有功能均已完成实机验收；实现状态、能力门禁和验证范围应分别核对。

## 从这里开始

- [文档索引](docs/INDEX.md)：按主题查找权威文档。
- [系统架构](docs/ARCHITECTURE.md)：模块职责、两条执行路径和关键边界。
- [开发指南](docs/DEVELOPMENT.md)：环境、构建和定向验证。
- [当前路线图](docs/ROADMAP.md)：已具备能力与尚待推进的方向。
- [Agent 工作约定](AGENTS.md)：Codex/开发代理的仓库规则。

## 开发与验证

常用命令见 [开发指南](docs/DEVELOPMENT.md)。默认按改动选择定向测试；不要运行全量测试。游戏端构建、隔离原生验证、图形 Host & Play 和真实模型任务是不同验证等级，报告结果时必须区分。

## 项目边界

- 以当前源码和运行时能力握手为实现依据，不以旧阶段报告推断当前能力。
- 接受请求、调用原生函数或返回 IPC 回执，不等于游戏效果已经完成。
- 保留 Actor/World/Generation 身份、取消、输入释放和 unknown 保护。
- AI 负责目标与策略；游戏侧负责在真实 Terraria 规则下执行并核验动作。
- 历史开发记录不作为当前使用说明。专题技术文档与原始证据分开维护。
