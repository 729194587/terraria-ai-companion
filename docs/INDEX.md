# 文档索引

本文是正式工程文档入口。描述当前行为的专题文档应与源码保持一致；阶段报告和原始验证材料不作为接口规范。

## 项目与开发

| 文档 | 用途 |
| --- | --- |
| [架构总览](ARCHITECTURE.md) | 系统组成、执行路径、模块边界 |
| [开发指南](DEVELOPMENT.md) | 环境、构建、定向测试和游戏验证 |
| [路线图](DEVELOPMENT_ROADMAP.md) | 当前能力与尚待解决的方向 |
| [工程原则](ENGINEERING.md) | 长期有效的安全与设计约束 |
| [API 契约](API_CONTRACT.md) | 模型工具入口、Schema、能力投影和结果语义 |
| [G1 Gameplay E2E Baseline](G1_GAMEPLAY_INTEGRATION_MVP.md) | 用真实模型验证现有 Agent 是否能自主完成木材采集、工作台制作与放置；先测试后实现 |
| [Terraria World Interaction](TERRARIA_WORLD_INTERACTION_DESIGN.md) | Building、Housing、NPC Interaction 与 Economy 的架构基线，含 Numen 参考、Terraria 1.4.4.9 规则及 tModLoader 原生机制 |
| [G2 Building & Housing](G2_BUILDING_HOUSING_IMPLEMENTATION.md) | Blueprint、差异检查、原生施工与只读验房的接口和验证记录；含现有向导入住、占用与过期引用拒绝的专服实测 |

## Agent 与运行时

| 文档 | 用途 |
| --- | --- |
| [Lua Runtime](LUA_RUNTIME.md) | Lua 沙箱、调用、预算、取消和失败语义 |
| [Lua Agent](LUA_AGENT.md) | 模型如何生成并提交 Lua 程序 |
| [Lua 可观测性](LUA_OBSERVABILITY.md) | 本地执行 Trace 与诊断关联 |
| [External Brain MVP](TERRARIA_EXTERNAL_BRAIN_MVP.md) | MCP 接入、控制租约、启动与验收 |
| [游戏系统消息](GAME_MESSAGES.md) | 系统提示事件的捕获与 Agent 上下文处理 |
| [Server AI 能力契约](SERVER_AI_CAPABILITIES.md) | 模型可见能力与运行时能力门禁 |
| [Server AI Actions](SERVER_AI_ACTIONS.md) | 服务器侧移动和原生动作 |
| [任务运行时](M1_TASK_RUNTIME.md) | 根任务、子任务与 mine_target 生命周期 |

## 游戏观察与世界状态

| 文档 | 用途 |
| --- | --- |
| [客户端观察历史](archive/OBSERVATION.md) | 旧客户端观察接口记录，仅供追溯 |
| [语义场景](LOCAL_SEMANTIC_SCENE.md) | Server AI 的 L0/L1 场景快照、编码和候选语义 |
| [局部语义世界](L1_SEMANTIC_WORLD.md) | 树木、矿物和掉落物的按需语义查询 |
| [AI Map](AI_MAP_V1.md) | 持久化探索地图及其时效语义 |
| [模型状态投影](MODEL_STATE_PROJECTION.md) | 完整观察校验、模型投影与历史上下文压缩 |

## 动作与游戏系统

| 文档 | 用途 |
| --- | --- |
| [原子操作](ATOMIC_OPERATIONS.md) | 原子 API 的调用和结果语义 |
| [背包操作](INVENTORY_OPERATIONS.md) | 库存 Token、原生堆叠/拆分和 unknown |
| [合成](SERVER_AI_CRAFTING.md) | 配方查询与原生合成 |
| [物品放置](SERVER_AI_OBJECT_PLACEMENT.md) | 服务器侧物体放置 |
| [导航](NAVIGATION.md) | 物理预测、路径规划、执行与落脚确认 |
| [Phase 2 Surface Graph 实现](PHASE2_SURFACE_GRAPH_IMPLEMENTATION.md) | 地形图、多尺度搜索、物理验证、阶段证据与范围 |
| [Phase 3 局部改造规划](PHASE3_LOCAL_PLANNING_IMPLEMENTATION.md) | v1.1 简化版成本比较、独立虚拟地形、逐项执行与验证 |
| [Phase 4.1 运动能力发现](PHASE4_1_MOVEMENT_DISCOVERY_IMPLEMENTATION.md) | 原生注册项与状态快照、规划版本、双引擎定向验证 |
| [Phase 4.2 ExtraJump MVP](PHASE4_2_EXTRA_JUMP_NAVIGATION_IMPLEMENTATION.md) | Cloud 预测、资源/输入边沿、原生确认、安全恢复与手测步骤 |
| [Phase 4.3 翅膀与火箭靴 MVP](PHASE4_3_WINGS_ROCKET_BOOTS_IMPLEMENTATION.md) | 天使翅膀、基础推进脉冲、资源核验、空中反馈与离线证据 |
| [世界角色持久化](SERVER_AI_WORLD_CHARACTER_PERSISTENCE.md) | Server AI Player 的世界绑定与存档 |
| [多人原子确认](MULTIPLAYER_ATOMIC_CONFIRMATION.md) | 多人原生交互的服务器权威确认与 unknown 语义 |

## 历史资料

- [历史阶段资料](archive/README.md)：仍有助于理解架构演进的早期实现与阶段报告。仅供追溯，不作为当前接口规范。

## 文档维护规则

1. 一项机制只设一个权威说明；其他文档链接过去，不复制整段契约。
2. 专题文档说明当前实现、调用方式、重要约束和未覆盖边界。
3. 临时日志、Trace、回执和测试压缩包默认保存在本地临时目录，不提交到仓库；长期回归行为应由测试代码表达。
4. 历史报告必须标明历史性质；过时结论不能继续使用“当前”“最新”等措辞。
5. 修改接口时对照源码、Schema、能力握手和对应定向测试更新文档。
