# Terraria Agent API Self-Documentation & Gameplay Modules — Implementation Spec

**设计依据：** \`docs/AGENT_API_AND_GAMEPLAY_MODULES_DESIGN.md\`  
**代码基线：** \`a3cae72d0c645e31e275ae920812bec3b2fdac1b\`（开始实现前确认最新 HEAD）  
**目标：** 完善现有 Server AI API Help，并建立首批内置 Gameplay Modules。

## 1. Objective

按照架构设计完成：
- API Help 文档元数据整理、示例完善和一致性测试。
- 内置 Lua Gameplay Module 注册、能力过滤、Worker 注入和模型索引。
- 首批 \`terraria.crafting.make\` 与 \`terraria.building.construct\`。

## 2. Current Implementation

重点现状文件：

- \`python/atomic_operations.py\`：原子 API Schema 和基础描述。
- \`python/server_ai.py\`：能力握手投影。
- \`python/server_ai_contract.py\`：Capability Manifest、API Help、返回语义、示例和 System Context。
- \`python/api_result_contracts.py\`：返回值 JSON Schema。
- \`python/gameplay_skills.py\`：已有 Skill 索引与按需加载。
- \`python/lua_worker.py\`：隔离 Worker，目前注入 explore/sense_world。
- \`python/lua_runtime.py\`：Worker 生命周期和 API 派发。
- \`python/lua_agent.py\`：模型侧 Lua 调用说明。
- \`python/lua/explore.lua\`、\`python/lua/semantic.lua\`：现有组合辅助函数。

当前 \`api("help")\` 已存在，需在其基础上整理，而不是再创建第二套 Help API。

## 3. Implementation Decisions

### 3.1 API 文档元数据

新增轻量文档模块（建议 \`python/api_documentation.py\`）：
- 只存示例、状态语义、限制补充、相关 API 等文档信息。
- 参数 Schema 从当前 \`server_definitions\` 读取。
- 返回 Schema 从 \`api_result_contracts.schema_for\` 读取。
- 基础描述继续从当前 Server AI API 投影读取。
- Help 只为当前后端实际开放的 API 生成详情。

整理 \`server_ai_contract.py\` 中分散的 \`help_example\`、\`help_result_contract\`，避免继续为每个 API 重复维护 Schema 或通用占位示例。

保留 \`api("help")\` 和现有返回字段兼容性；可增加 \`examples\`、\`related\`，原 \`example\` 保留为第一条示例。

### 3.2 API 示例

先覆盖：
- inspect_inventory
- resolve_content
- query_recipes / craft_recipe
- inspect_container / deposit_container_item / withdraw_container_item
- build_diff / submit_task(build_place)
- inspect_housing
- scan_entities 与 NPC 引用类接口

示例需使用真实字段名。涉及 Token、entityRef 或 quoteToken 时，示例应展示前置观察和引用提取，不能填造固定令牌。

### 3.3 Gameplay Module Registry

新增 \`python/lua_modules.py\` 和 \`python/lua/modules/\`。

Registry 为每个模块维护名称、摘要、依赖 API 和 Lua 源文件。模块只有在当前 Bridge 的 supported operations 满足所有依赖时才可注入。

Worker 初始化时加载模块 table，挂到 \`terraria\` 命名空间。保留现有 \`explore\` 与 \`sense_world\` 注入行为，不将它们强制迁移为新模块。

模块源代码与元数据在 Worker 启动时校验；模块返回值必须是 table。模块内部仍通过宿主 api callback 调用，所有现有参数、预算、能力、失败屏障和动作核验保持有效。

### 3.4 首批模块

#### \`terraria.crafting.make(resultType, desiredCount, options)\`

- 查询配方并选择 supported/available 配方。
- 根据 \`output.count\` 计算配方执行次数。
- 每批使用查询得到的新鲜 recipeId/recipeToken 调用 craft_recipe。
- 按实际产出累计；部分成功立即停止并返回已完成数量、剩余数量和原始回执。
- 不自动采集、不自动建造工作站、不切换未支持配方。

#### \`terraria.building.construct(blueprint, options)\`

- 先 build_diff。
- 调查无效/unknown 时返回，不提交施工。
- 用 submit_task 提交 build_place。
- 使用一次有界 await_task；等待到期时返回 taskId 和 running 状态，不重复提交。
- 任务可信终态后重新 build_diff。
- 返回 before/task/after 三部分结果，保留部分完成与 unknown。

### 3.5 Prompt Integration

在 Server AI System Context 中增加当前可用模块的简短索引：
- 模块名称。
- 一句话用途。
- 可调用函数。
- 使用前置或能力条件。

提示模型：模块是可复用流程；不替代 Skill 知识和 API 契约。相关模块可直接使用；需要参数细节时查 API Help。

## 4. Implementation Steps

1. 确认当前分支最新 HEAD，回读上述现有文件。
2. 新增 API 文档元数据并重构 \`api_help\` 的组装逻辑。
3. 完善高频 API 的示例、返回字段说明和状态语义。
4. 增加 API Help 契约测试，验证当前能力投影、Schema 一致性、示例和无副作用。
5. 新增 Gameplay Module Registry 与 Lua 模块目录。
6. 扩展 Worker 启动参数和 Bootstrap，将满足能力依赖的模块注入 \`terraria\`。
7. 在 Server AI System Context 中注入可用模块索引。
8. 实现 \`terraria.crafting.make\`。
9. 实现 \`terraria.building.construct\`。
10. 更新 \`API_CONTRACT.md\`、\`LUA_AGENT.md\`、\`LUA_RUNTIME.md\` 和 \`SERVER_AI_CRAFTING.md\`。

## 5. Testing

### A. API Help

- 无参数 Help 返回当前后端分类与可用 API。
- 每个可用 API 的参数 Schema 与 \`server_definitions\` 完全一致。
- 返回 Schema 与 \`api_result_contracts.schema_for\` 一致。
- 未开放 API 不会出现在索引；详情请求返回 not_exposed。
- 关键 API 的示例字段与实际结果 Schema 一致。
- Help 查询不调用 Bridge IPC、不改变观察状态、不执行动作。

### B. Module Registry / Worker

- 模块源文件可解析且返回 table。
- 模块名称、导出函数和依赖 API 校验。
- 能力不足时模块不注入且不出现在索引。
- 能力满足时模块在全新 Worker 中可调用。
- 新程序 Worker 重建后内置模块仍可用。
- 模块只能通过 api callback 访问游戏；预算和失败屏障照常生效。

### C. Gameplay Modules

Crafting:
- 单次配方产出数量大于 1。
- 需要多批才能满足 desiredCount。
- 多个候选配方，其中仅部分 supported/available。
- 无可用配方、缺料、缺工作站或空间不足。
- 部分制作后后续失败，保留实际产量且不重试。

Building:
- 完整 Blueprint 完成施工并返回 after diff。
- 材料不足时保留 BuildPlaceTask 部分结果。
- 初始 diff 无效/unknown 时不提交任务。
- 等待到期返回 taskId，不重复提交。
- 任务 unknown 时不伪造 after completed 或自动重放。
- 已完成 Blueprint 再次调用时跳过已匹配目标。

### D. Regression

- 现有 Lua Runtime / Worker tests。
- explore 与 sense_world 注入和行为测试。
- read_skill 与现有 Skills 测试。
- API result contract tests。
- Native Crafting、Inventory、Building V2 和 Housing 定向回归。
- 目标版本构建及 \`git diff --check\`。

真实模型 Host & Play 不作为代码实现测试的替代；实现完成后用建房和资源准备任务单独验收。

## 6. Deliverables

1. API Help 文档元数据和重构实现。
2. API Help 一致性与示例测试。
3. Gameplay Module Registry、Worker 注入和 System Context 索引。
4. \`terraria.crafting.make\`、\`terraria.building.construct\`。
5. 更新后的专题文档。
6. Git Commit，记录测试命令、结果和未覆盖事项。

## 7. Acceptance Criteria

- API Help 反映当前连接后端的真实能力、参数和返回 Schema。
- 高使用频率 API 的示例不再依赖模型猜字段。
- 模块可复用、按能力加载，且不拥有额外游戏权限。
- Crafting Module 能处理配方选择、批次和部分结果。
- Building Module 能完成调查、提交、等待和最终差异核验。
- 现有 Skill、Lua Runtime 和游戏原生动作契约不回归。
