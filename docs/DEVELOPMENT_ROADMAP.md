# 当前路线图

本文概述项目当前能力和仍需推进的方向，不记录每次提交、阶段测试数量或历史开发过程。实现细节以 [文档索引](INDEX.md) 中的专题为准。

## 项目目标

让 AI 成为 Terraria 世界中的真实玩家：能理解目标、感知世界、移动、收集资源、使用物品、建造、应对危险，并在长期任务中根据结果调整计划。

## 当前系统基础

- Python Agent 负责聊天、模型交互、上下文、取消和执行编排。
- Lua Runtime 允许模型提交有界程序，组合当前执行端开放的 API。
- CompanionBridge 提供客户端控制与独立服务器 AI Player 两条运行路径。
- Server AI 已具备原生 Player 生命周期、世界/角色持久化、结构化场景观察、历史地图、导航与直接输入、挖掘任务、物品/库存操作、放置和合成等基础能力。
- 原子动作和游戏侧任务保留身份绑定、取消、输入释放、原生效果核验及 unknown 语义。

上述列表表示代码中存在的能力，不代表每一项都已通过图形多人环境或真实模型长期任务验收。用户已有实测反馈表明 Agent 曾能执行工作台放置；后续开发应以实际运行证据为依据，不从“缺少专用实现”推断“缺少能力”。

## 已完成：Native Placement Survey & Building Parity

统一放置预检查已在当前分支实现，并通过 Dedicated Server 原生放置对照测试。详见 [Native Placement Survey](NATIVE_PLACEMENT_SURVEY_IMPLEMENTATION.md)。

## 已完成：Building V2 — 智能施工调度

Building V2 已在当前开发分支实现并通过 Dedicated Server 对照测试。真实模型建房也已完成一次约 6 分钟的验收；该次使用现有材料，未覆盖自动采集资源的完整闭环。详见 [Building V2 Design](BUILDING_V2_DESIGN.md) 与 [Building V2 Implementation](BUILDING_V2_IMPLEMENTATION.md)。

## 当前阶段：Agent API Self-Documentation & Gameplay Modules

完善模型发现和正确使用 Terraria API 的能力，并建立可复用的高层 Lua Gameplay Modules。重点是 API Help 的契约一致性、正确示例，以及制作和建造流程模块。架构见 [Agent API & Gameplay Modules Design](AGENT_API_AND_GAMEPLAY_MODULES_DESIGN.md)，实现规范见 [Agent API & Gameplay Modules Implementation](AGENT_API_AND_GAMEPLAY_MODULES_IMPLEMENTATION.md)。



## 历史基线：G1 Gameplay E2E Baseline

先使用当前 Agent、真实模型、实际 MCP 工具集合和真实游戏环境，验证 Agent 能否在一次指令下收集木材、制作并放置工作台。具体步骤见 [G1 E2E 基线文档](G1_GAMEPLAY_INTEGRATION_MVP.md)。

本阶段先测试、后实现。只有真实测试发现明确阻塞点，才针对该根因提出最小实现任务；若流程已成功，则整理回归证据，不新增重复功能。

## 当前阶段：G3-B 普通商人交易

G2 已在隔离专服完成建房、验房与向导入住。G3-B 已通过公开 Lua 接口向普通商人买入并卖回一支火把，买价 44 铜、卖价 11 铜。专服已核对物品、实收钱币、银行状态和过期引用、槽位令牌的拒绝结果。真实模型 Host & Play 另行验收。详见 [G2 验证记录](G2_BUILDING_HOUSING_IMPLEMENTATION.md) 和 [世界交互架构 §9](TERRARIA_WORLD_INTERACTION_DESIGN.md)。

## 后续架构方向：World Interaction

普通商人之后的首个 NPC 服务已接入原版护士。隔离专服通过 Lua 完成诊金报价、付款、回血和减益清除，并核对受保护减益与银行状态。真实模型验收与其他世界交互功能集中进行。[护士治疗实测](TERRARIA_WORLD_INTERACTION_DESIGN.md#护士治疗2026-11-20)

项目后续重点从单个角色的生存/战斗操作扩展到世界建设和利用：

- **Building & Housing**：基于真实模型建筑基线，逐步补齐通用 Blueprint、差异检查、施工和原生住宅核验。
- **NPC Interaction & Economy**：研究 Dedicated Server 可执行的 NPC 服务、商店查询、购买/出售和结果核验；明确客户端专属钩子的边界。
- **Gameplay Skills**：按照 Numen 风格组织独立、按需加载的 Terraria 知识 Skills，依据目标游戏版本整理官方 Wiki 规则。

架构基线见 [Terraria World Interaction 设计](TERRARIA_WORLD_INTERACTION_DESIGN.md)。先进行真实模型基线与目标版本原生机制验证，再根据实际阻塞点拆分实现任务。

## 后续方向

### 自主探索与长期任务

让 Agent 能基于地图新鲜度、场景观察、资源需求和风险持续选择探索目标；支持中断、失败恢复和跨任务经验利用。

### 移动与环境适应

扩展复杂地形、移动装备、液体、平台、绳索及失败恢复能力。导航预测和运动 profile 只表达已验证的适用范围，不应把预测器限制当作游戏规则。

### 战斗与生存

完善敌对实体理解、战斗动作、装备选择、生命与资源管理，以及死亡后的恢复和策略调整。

### 多人协作与真实验收

持续验证独立 Server AI Player 与真人玩家共存时的身份隔离、世界同步、断连恢复和长期稳定性。模型端到端任务、图形 Host & Play 和远端客户端验收应单独记录。

## 优先级判断

优先解决真实运行中已证实会阻断玩家目标的问题，而不是单纯增加 API 数量。每项工作都应明确用户可观察的游戏目标、实际失败证据、涉及的执行边界和最小充分验证。
