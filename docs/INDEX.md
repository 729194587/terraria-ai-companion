# 文档索引

本文是正式工程文档入口。描述当前行为的专题文档应与源码保持一致；阶段报告和原始验证材料不作为接口规范。

## 项目与开发

| 文档 | 用途 |
| --- | --- |
| [架构总览](ARCHITECTURE.md) | 系统组成、执行路径、模块边界 |
| [开发指南](DEVELOPMENT.md) | 环境、构建、定向测试和游戏验证 |
| [路线图](ROADMAP.md) | 当前能力与尚待解决的方向 |
| [工程原则](ENGINEERING.md) | 长期有效的安全与设计约束 |

## Agent 与运行时

| 文档 | 用途 |
| --- | --- |
| [Lua Runtime](LUA_RUNTIME.md) | Lua 沙箱、调用、预算、取消和失败语义 |
| [Lua Agent](LUA_AGENT.md) | 模型如何生成并提交 Lua 程序 |
| [Lua 可观测性](LUA_OBSERVABILITY.md) | 执行记录、Trace 与证据 |
| [Server AI 能力契约](SERVER_AI_CAPABILITIES.md) | 模型可见能力与运行时能力门禁 |
| [Server AI Actions](SERVER_AI_ACTIONS.md) | 服务器侧移动和原生动作 |
| [任务运行时](M1_TASK_RUNTIME.md) | 根任务、子任务与 mine_target 生命周期 |

## 游戏观察与世界状态

| 文档 | 用途 |
| --- | --- |
| [观察接口](OBSERVATION.md) | 客户端观察数据与兼容边界 |
| [语义场景](LOCAL_SEMANTIC_SCENE.md) | Server AI 场景快照与语义层 |
| [场景数据层](SERVER_AI_SCENE_LAYERS.md) | observe_scene 的可选数据层 |
| [局部语义世界](L1_SEMANTIC_WORLD.md) | 树木、矿物和掉落物的按需语义查询 |
| [AI Map](AI_MAP_V1.md) | 持久化探索地图及其时效语义 |

## 动作与游戏系统

| 文档 | 用途 |
| --- | --- |
| [原子操作](ATOMIC_OPERATIONS.md) | 原子 API 的调用和结果语义 |
| [背包操作](INVENTORY_OPERATIONS.md) | 库存 Token、原生堆叠/拆分和 unknown |
| [合成](SERVER_AI_CRAFTING.md) | 配方查询与原生合成 |
| [物品放置](SERVER_AI_OBJECT_PLACEMENT.md) | 服务器侧物体放置 |
| [导航](TERRABLIND_NAVIGATION.md) | 物理预测、路径规划与执行 |
| [落脚确认](NAVIGATION_LANDING_CONFIRMATION.md) | 导航落点和真实支撑确认 |
| [世界角色持久化](SERVER_AI_WORLD_CHARACTER_PERSISTENCE.md) | Server AI Player 的世界绑定与存档 |

## 验证证据与历史资料

- [evidence/](evidence/)：原始日志、回执、manifest、测试包和精选分析。证据用于追溯具体场景，不自动构成当前接口规范。
- [archive/](archive/)：历史阶段报告和已被现行文档取代的材料。归档内容仅供追溯，不能作为当前实现依据。

## 文档维护规则

1. 一项机制只设一个权威说明；其他文档链接过去，不复制整段契约。
2. 专题文档说明当前实现、调用方式、重要约束和未覆盖边界。
3. 测试数量、逐轮工作记录和原始日志放入 evidence，不放在 README 或架构正文。
4. 历史报告必须标明历史性质；过时结论不能继续使用“当前”“最新”等措辞。
5. 修改接口时对照源码、Schema、能力握手和对应定向测试更新文档。
