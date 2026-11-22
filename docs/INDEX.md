# 文档索引

## 项目与开发

| 文档 | 内容 |
| --- | --- |
| [README](../README.md) | 项目目标、主要组件和技术栈 |
| [系统架构](ARCHITECTURE.md) | 客户端与服务器执行路径、模块关系 |
| [开发指南](DEVELOPMENT.md) | 环境配置、构建和测试 |
| [工程设计](ENGINEERING.md) | Agent、游戏运行时、原生执行与身份模型 |
| [开发路线图](DEVELOPMENT_ROADMAP.md) | 当前系统能力和后续工作重点 |
| [tModLoader 引擎配置](TML_ENGINE_MIGRATION.md) | 引擎版本、构建配置与角色迁移 |

## Agent 与运行时

| 文档 | 内容 |
| --- | --- |
| [Lua Agent](LUA_AGENT.md) | 模型如何生成并提交 Lua 程序 |
| [Lua Runtime](LUA_RUNTIME.md) | Worker、预算、API 调用和任务协作 |
| [Lua 执行诊断](LUA_OBSERVABILITY.md) | Trace、Runtime 事件和任务证据 |
| [游戏侧任务运行时](M1_TASK_RUNTIME.md) | 持续游戏任务的提交、推进与终止 |
| [Agent 私人记忆](AGENT_MEMORY.md) | 世界与角色范围内的长期笔记 |
| [Gameplay Skills](TERRARIA_AGENT_GAMEPLAY_MVP_PHASE1.md) | 玩法知识的注册与按需加载 |
| [External Brain](TERRARIA_EXTERNAL_BRAIN_MVP.md) | 通过 MCP 接入外部 Agent |

## API 与数据契约

| 文档 | 内容 |
| --- | --- |
| [API 契约](API_CONTRACT.md) | 工具入口、Schema、能力发现和结果语义 |
| [原子操作](ATOMIC_OPERATIONS.md) | 单项游戏操作的参数与执行结果 |
| [Server AI 能力](SERVER_AI_CAPABILITIES.md) | 服务器运行时开放的 API 集合 |
| [Server AI Actions](SERVER_AI_ACTIONS.md) | 移动、挖掘和持续动作 |
| [背包操作](INVENTORY_OPERATIONS.md) | 库存观察、Token、转移与装备 |
| [多人原子确认](MULTIPLAYER_ATOMIC_CONFIRMATION.md) | 客户端操作的服务器权威确认 |
| [Lua 返回值编码](LUA_TABLE_ENCODING.md) | Lua Table 到 JSON 的编码规则 |

## 感知与世界状态

| 文档 | 内容 |
| --- | --- |
| [Server AI 语义场景](LOCAL_SEMANTIC_SCENE.md) | 局部与区域场景观察 |
| [局部语义世界](L1_SEMANTIC_WORLD.md) | Tile、实体和内容的组合查询 |
| [Server AI 地图记忆](AI_MAP_V1.md) | 持久化探索地图 |
| [模型状态投影](MODEL_STATE_PROJECTION.md) | 模型请求中的观察投影与历史压缩 |
| [游戏系统消息](GAME_MESSAGES.md) | Terraria 系统提示事件 |

## 导航与动作

| 文档 | 内容 |
| --- | --- |
| [导航系统](NAVIGATION.md) | 运动模型、地表图、规划与执行 |
| [通用地形挖掘](GENERAL_TERRAIN_EXCAVATION.md) | 资源采集与通路清障 |
| [合成](SERVER_AI_CRAFTING.md) | 配方查询与原生合成 |
| [物品放置](SERVER_AI_OBJECT_PLACEMENT.md) | 多格物体放置与蓝图施工 |
| [原生放置预检](NATIVE_PLACEMENT_SURVEY_IMPLEMENTATION.md) | Tile、Wall 和多格物体的放置规则 |
| [Building V2 设计](BUILDING_V2_DESIGN.md) | 依赖感知施工调度 |
| [Building V2 实现记录](BUILDING_V2_IMPLEMENTATION.md) | 施工调度实现与对照验证 |
| [世界交互设计](TERRARIA_WORLD_INTERACTION_DESIGN.md) | 住房、NPC 服务和经济系统 |

## 战斗

| 文档 | 内容 |
| --- | --- |
| [战斗系统](COMBAT_SYSTEM_DESIGN.md) | 战斗任务、身体控制与目标感知 |
| [战斗配装](COMBAT_WEAPON_LOADOUT_DESIGN.md) | 武器配置、自卫切换与召唤物 |
| [Boss Encounters](R2_E_BOSS_ENCOUNTERS.md) | Boss 战斗任务与成员追踪 |

## 历史与研究

- [历史阶段资料](archive/README.md)：早期架构、阶段实施和修复记录。
- Phase 2–4 导航阶段记录：根目录中的 `PHASE*` 与 `terraria_navigation_phase*` 文件均为历史资料，当前架构见 [导航系统](NAVIGATION.md)。
- [原生机制审计报告](NATIVE_MECHANISM_PARITY_AUDIT_REPORT.md)：目标引擎 IL 对照、已确认差异及隔离专服证据。
- [战斗架构历史审计](COMBAT_ARCHITECTURE_AUDIT.md)：Combat MVP 早期架构快照。
- [Numen 参考研究](architecture/NUMEN_ADAPTATION_ROADMAP.md)：外部项目的架构参考与适配思路。
- [架构审查计划](architecture/ARCHITECTURE_AUDIT_PLAN.md)：Agent 与游戏运行时的后续审查问题。
- [R3-A1 Agent 自主能力审计](architecture/R3_A1_AGENT_AUTONOMY_AUDIT.md)：跨轮目标、世界认知、探索导航的证据与 R3 实施优先级。
- [R3-B0 Terraria 游戏知识架构研究](architecture/R3_B0_GAME_KNOWLEDGE.md)：Numen 与官方 Wiki 研究、知识检索和实时状态结合方案，以及分阶段验收。
- [R3-B1 游戏知识最小闭环实施规格](architecture/R3_B1_GAME_KNOWLEDGE_SPEC.md)：以熔岩镐为例，定义备料、核仓、探索、制作及逐步验收条件。
- [原生执行架构](NATIVE_EXECUTION_REFACTOR.md)：原生执行、Actor 适配与效果核验。

专题文档记录所属模块的设计与实现。阶段实施记录保留当时的代码基线和验证结果，当前接口以对应模块说明和源码为准。
