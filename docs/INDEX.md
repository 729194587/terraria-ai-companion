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
| [原生放置预检](NATIVE_PLACEMENT_SURVEY_IMPLEMENTATION.md) | 原生放置规则与共享预检 |
| [Building V2 设计](BUILDING_V2_DESIGN.md) | 依赖感知施工调度 |
| [Building V2 实现](BUILDING_V2_IMPLEMENTATION.md) | 施工调度实现与验证 |
| [世界交互设计](TERRARIA_WORLD_INTERACTION_DESIGN.md) | 住房、NPC 服务和经济系统 |

## 战斗

| 文档 | 内容 |
| --- | --- |
| [Combat System Design](COMBAT_SYSTEM_DESIGN.md) | 战斗任务、身体控制与目标感知 |
| [Combat Weapon Loadout](COMBAT_WEAPON_LOADOUT_DESIGN.md) | 武器配置、自卫切换与召唤物 |
| [Boss Encounters](R2_E_BOSS_ENCOUNTERS.md) | Boss 战斗任务与成员追踪 |

## 历史与研究

- [历史阶段资料](archive/README.md)：早期架构、阶段实施和修复记录。
- [架构研究](architecture/)：Numen 参考与尚在研究的架构方向。

专题文档记录所属模块的设计与实现。阶段实施记录保留当时的代码基线和验证结果，当前接口以对应模块说明和源码为准。
