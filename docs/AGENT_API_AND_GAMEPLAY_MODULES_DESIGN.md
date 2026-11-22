# Terraria Agent：API Self-Documentation & Gameplay Modules 设计

**状态：** Architecture Design  
**代码基线：** \`codex/unified-autonomous-navigation\` @ \`a3cae72d0c645e31e275ae920812bec3b2fdac1b\`  
**参考项目：** Minecraft Numen 1.21.1  
**范围：** Server AI / Lua Agent 的 API 自描述与可复用 Gameplay Modules

## 1. 目标

改善模型使用 Terraria API 和组织常见多步骤玩法的效率。

本阶段解决两个问题：

1. 模型能否可靠发现 API 的真实参数、返回结构、限制和正确调用方式。
2. 模型能否复用经过验证的多步骤游戏流程，而不必每次从原子 API 开始重新组织。

目标分工：

- **API Self-Documentation**：解释 API 能做什么、如何调用、返回什么以及失败意味着什么。
- **Gameplay Modules**：把常见的多步骤操作封装成可复用 Lua 函数。
- **Skills**：继续提供 Terraria 游戏知识、机制和策略。
- **Atomic APIs**：继续提供受能力白名单和原生执行契约约束的基础操作。

不重新设计 Lua Runtime、MCP 协议或游戏侧原子执行器。

## 2. Numen 源码研究结论

研究基线：Numen \`1.21.1\`。

### 2.1 API 自描述

Numen 的 \`ApiFunction\` 在登记时保存函数摘要、参数、返回类型、示例、注意事项和相关 API。

\`ApiDocs\` 从登记信息生成：
- 系统提示中的 API 索引。
- 按分组查询的函数签名。
- 单个函数的完整帮助。
- 参数错误时附带的调用用法。
- LuaLS 存根与机器可读元数据。

\`ApiTester\` 会检查 API 是否有摘要、参数说明、可解析示例和有效的 SeeAlso；还会检查模块、Skill 和提示词中写出的调用是否存在、参数是否正确。

参考：
- [ApiFunction.java](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/numen-api/common/src/main/java/com/dwinovo/numen/api/sdk/ApiFunction.java)
- [ApiDocs.java](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/numen-api/common/src/main/java/com/dwinovo/numen/api/sdk/ApiDocs.java)
- [ApiTester.java](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/numen-api/common/src/main/java/com/dwinovo/numen/api/sdk/ApiTester.java)

### 2.2 Gameplay Modules

Numen 的 Lua Module 是返回函数表的可复用程序模块，内置模块和用户模块通过同一命名空间调用。

值得借鉴的实例：

- \`numen.inv.make\`：查配方、选择可用配方、确定制作网格、寻找或放置工作台、执行批量合成、关闭临时窗口。
- \`numen.work.mine\`：挖掘目标、收集掉落物、检查剩余目标并继续。
- \`numen.work.collect\`：按距离收集掉落物，跳过不可达目标并保留失败原因。

Numen 还支持模块列表、查看源码、保存、删除、重置和运行统计。模块保存前会检查 Lua 是否可解析、是否返回函数表、是否覆盖原生 API。

参考：
- [inv.lua](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/numen/common/src/main/resources/numen/modules/numen/inv.lua)
- [work.lua](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/numen/common/src/main/resources/numen/modules/numen/work.lua)
- [ModuleApi.java](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/numen-api/common/src/main/java/com/dwinovo/numen/api/script/ModuleApi.java)
- [Modules.java](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/numen-api/common/src/main/java/com/dwinovo/numen/api/script/Modules.java)

### 2.3 Knowledge 与执行流程分离

Numen 的 Skill 负责游戏知识，Module 负责执行流程，API Help 负责接口契约。三者不互相替代。

我们应保留这一分工。

## 3. Terraria 当前实现与问题

### 3.1 已有 API 自描述基础

当前 Server AI 的 API 信息分布如下：

| 信息 | 当前来源 |
|---|---|
| 原子 API 名称、参数 Schema、基础描述 | \`python/atomic_operations.py\` |
| 当前后端能力过滤及 Server AI 描述 | \`python/server_ai.py\`、\`python/server_ai_contract.py\` |
| 返回值 JSON Schema | \`python/api_result_contracts.py\` |
| API Help 详情与示例 | \`python/server_ai_contract.py\` |
| 模型使用规则和 Runtime 约束 | \`python/lua_agent.py\`、\`python/server_ai_contract.py\` |

当前已经存在 \`api("help")\`：
- 无 name 时返回当前后端可调用的 API 分类。
- 指定 name 时返回当前 API 描述、参数 Schema、返回 Schema、状态语义、限制和示例。
- Help 在 Lua Runtime 内由宿主本地处理，不调用游戏 IPC，不改变游戏状态。
- 已知但未开放的 API 返回 \`not_exposed\`，未知名称返回 \`unknown_api\`。

因此本阶段不是新建 Help API，而是完善它的文档来源、示例质量和测试覆盖。

### 3.2 API 文档的主要问题

当前 API 契约信息由多个文件共同维护，存在重复和漂移风险：

- 参数 Schema 与返回 Schema 分别维护。
- \`help_result_contract\` 和 \`help_example\` 需要人工维护。
- 通用示例可能只展示必填字段，无法表达 Token、实体引用或新鲜观察等前置条件。
- 部分接口的返回说明只给摘要，缺少对关键字段、部分成功和 unknown 的可执行解释。
- Skill、Prompt 和示例中的 API 调用没有统一的自动检查入口。

建房日志中的 \`inspect_inventory\` 就是典型例子：模型先漏掉必填 mode，随后又把 \`data.inventory\` 错写成 \`data.slots\`。

### 3.3 已有 Lua 辅助函数与缺口

当前 \`python/lua_worker.py\` 在每个 Worker 中注入：
- \`explore\`
- \`sense_world\`

源码位于 \`python/lua/\`。它们是受限 Lua 组合函数，内部仍通过宿主 \`api()\` 调用游戏接口。

但当前没有通用 Gameplay Module 注册表：
- 没有模块元数据索引。
- 没有按模块依赖的 API 能力过滤。
- 没有统一命名空间和模块帮助。
- 没有模块级自动测试与接口文档检查。
- 每次程序创建新 Worker，普通 Lua 函数不能跨程序保留。

\`lua_agent.ProgramStore\` 是诊断源码归档，不是可执行模块库。

## 4. 目标架构

\`\`\`text
                    Agent System Context
                   /          |          \\
          API Manifest    Skill Index   Module Index
               |              |             |
          api("help")      read_skill    terraria.*
               |              |             |
       API Contract Docs   Game Knowledge  Gameplay Modules
               |              |             |
               +--------------+-------------+
                              |
                       Lua Worker / Runtime
                              |
                       api(name, arguments)
                              |
                  Capability + Schema Validation
                              |
                    Terraria Native Game APIs
\`\`\`

## 5. API Self-Documentation 设计

### 5.1 保留现有契约来源

不复制或重新定义已有参数和返回 Schema：

- 请求参数继续以当前后端投影的 API Schema 为准。
- 返回结构继续以 \`api_result_contracts.py\` 为准。
- 能力范围继续以 Bridge 握手和 \`server_definitions\` 投影为准。

新增一层轻量 API 文档元数据，用于补充：
- 可运行的 Lua 示例。
- 返回字段重点和状态语义。
- 特殊前置条件。
- 预算/范围/部分完成说明。
- 相关 API 和推荐下一步。

建议放在独立的 \`python/api_documentation.py\`，不要再把这些内容散落到多个 Help 分支中。

### 5.2 Help 结构

保留现有调用方式：

\`\`\`lua
return api("help")
return api("help", {name="inspect_inventory"})
\`\`\`

分类索引返回当前连接可调用的 API、类别和能力边界。

单个 API 详情至少包括：
- \`name\`、\`category\`、\`description\`
- \`parameters\`
- \`returns\`、\`returnsSchema\`
- \`statusSemantics\`
- \`limits\`
- \`examples\`
- \`related\`

保留现有 \`example\` 字段兼容性，若有多个示例则新增 \`examples\`，或由第一条示例填充旧字段。

Help 必须从当前 Server AI 能力投影生成；不能显示当前后端未开放的操作。它是本地契约查询，不是游戏观察。

### 5.3 示例质量

示例分为两类：

1. **可直接运行的只读示例**：参数完整，返回字段真实。
2. **依赖新鲜观察的动作示例**：明确展示先观察、再提取 Token/引用、最后执行的完整链路。

不要为需要新鲜 Token 或实体引用的动作生成虚构占位值后伪装成可运行示例。若无法提供可靠示例，明确给出前置步骤或不提供示例。

优先完善高频接口：
- \`inspect_inventory\`
- \`resolve_content\`
- \`query_recipes\`
- \`craft_recipe\`
- \`inspect_container\` / 存取接口
- \`build_diff\` / \`build_place\`
- \`inspect_housing\`
- \`scan_entities\` 与 NPC 交互接口

### 5.4 错误反馈

参数错误时继续保留现有 Schema 校验与 recovery 语义，并明确提示模型：
- 查询对应 API Help。
- 按真实字段名和参数类型修正。
- 若操作要求新鲜 Token、报价或实体引用，先执行相应观察。
- 不通过重复试错或切换未开放入口绕过能力限制。

### 5.5 文档一致性测试

新增 API 文档契约测试：
- 每个暴露 API 都能获得匹配的参数 Schema 和返回 Schema。
- Help 的参数 Schema 必须等于当前后端实际投影。
- 未开放 API 不会出现在当前索引。
- 高优先级 API 必须有至少一个经过检查的示例或明确的前置说明。
- 示例中引用的 API 和返回字段必须存在。
- Help 查询不产生 IPC、游戏动作或观察副作用。

## 6. Gameplay Modules 设计

### 6.1 运行模型

建立内置 Lua Gameplay Module 注册表。模块源文件放在 \`python/lua/modules/\`，由 Worker 加载并注入统一的 \`terraria\` 命名空间。

调用形式：

\`\`\`lua
terraria.crafting.make(...)
terraria.building.construct(...)
\`\`\`

模块采用 Numen 风格：
- 每个模块返回一个 Lua table。
- 函数通过 table 暴露。
- 模块和函数都有简短说明。
- 模块内部只使用受限 Lua 计算和宿主 \`api()\`。
- 不直接访问 Terraria 对象、Python 对象、文件系统或网络。
- 所有游戏动作仍由当前原子 API、任务运行时和游戏线程执行。

### 6.2 模块注册和能力过滤

每个模块声明：
- 名称。
- 简短描述。
- 依赖的 API 集合。
- Lua 源文件。
- 导出的函数及说明。

只有当前 Bridge 能力集合完整满足依赖时，模块才注入 Worker，也才出现在 System Context 的 \`available_modules\` 索引。

这样模型不会看到无法调用的模块。

### 6.3 首批内置模块

首批只实现两个经常出现、且当前 API 足以支持的流程。

#### A. \`terraria.crafting.make(resultType, desiredCount, options)\`

职责：
1. 调用 \`query_recipes\` 查询指定产物。
2. 从结果中选择 \`supported=true\` 且 \`available=true\` 的配方。
3. 若无可用配方，返回可用性、材料缺口、制作站/环境条件和不支持原因。
4. 根据 \`output.count\` 计算需要执行的配方次数。
5. 使用新鲜 \`recipeToken\` 调用 \`craft_recipe\`，按单批上限分批制作。
6. 每批核对实际产量、\`completedCrafts\` 和 \`inventoryVerified\`。
7. 部分完成时返回已制作数量和剩余需求，不自动采集材料、不自动放置工作站、不盲目重试。

输入使用已解析的 Item Type ID；模块不猜测名称或内容 ID。

#### B. \`terraria.building.construct(blueprint, options)\`

职责：
1. 调用 \`build_diff\` 做施工前调查。
2. 若观察无效或身份/世界状态未知，返回调查结果，不开始施工。
3. 保留材料缺口、阻塞、依赖和未知项，交给模型决策。
4. 通过 \`submit_task(operation="build_place")\` 提交 Building V2 施工。
5. 使用有界 \`await_task\` 等待；若仍在运行，返回 taskId 和当前状态供模型继续查询。
6. 任务进入可信终态后重新调用 \`build_diff\` 核验剩余差异。
7. 返回施工前调查、任务结果和施工后差异。

模块不自动采集、合成、挖除障碍或分配 NPC 住所。它负责把一次建造执行和结果核验组织起来。

### 6.4 后续模块候选

首批完成后，根据真实模型 Trace 再选择：
- \`terraria.mining.mine_and_collect\`
- \`terraria.inventory.transfer_from_container\`
- \`terraria.housing.verify_and_assign\`
- \`terraria.npc.buy\`

其中挖掘与掉落物收集需要先明确掉落物归属和拾取结果证据；不能把附近所有掉落物消失都当成当前挖掘所得。

### 6.5 模块与 Skill 的边界

| 机制 | 内容 |
|---|---|
| API Help | 参数、返回字段、限制和调用示例 |
| Gameplay Module | 可执行的多步骤操作流程 |
| Skill | Terraria 规则、机制、配方知识和策略 |
| Atomic API | 单项原子观察或游戏动作 |

现有 \`read_skill\` 与 Skill 文件布局继续使用，不将可执行代码塞进 Skill 正文。

### 6.6 模块持久化范围

本阶段先建立版本控制的内置模块，不实现模型自行保存、覆盖或删除模块。

Numen 的用户模块体系值得后续借鉴，但当前 Lua Worker 每次程序都会重建，尚无与 Agent/World/Actor 绑定的模块存储、版本和运行记录。先验证内置模块的调用、预算、回执和兼容性，再单独设计用户模块持久化。

## 7. 与当前项目的集成边界

- 不改变 \`api(name,args)\` 原子调用协议。
- 不改变现有 Capability Manifest 和后端白名单语义。
- 不改变 API 参数/结果 Schema 的权威来源。
- 不新增绕过宿主的执行通道。
- 不改变 \`read_skill\` 的加载方式。
- Gameplay Module 不跨程序保存 Lua 全局变量；需要跨程序状态时使用现有 Task/观察接口。
- MCP 端口和连接初始化属于外部连接层，不由本设计处理。

## 8. 验收标准

1. API Help 对当前后端暴露的 API 提供一致的参数和返回契约。
2. 高频 API 示例准确反映真实字段与调用前置条件。
3. API Help 索引和详情严格遵循当前能力白名单。
4. Worker 能按能力注入内置 Gameplay Modules。
5. \`terraria.crafting.make\` 能完成配方选择、批量合成和部分结果报告。
6. \`terraria.building.construct\` 能完成 diff、提交 Building V2 任务、有限等待和施工后 diff。
7. 模块不能将 API failed/unknown/timeout 包装为成功，也不能自动重放动作。
8. 现有 Lua Runtime、Skill、API Contract、Building V2 和 Crafting 回归通过。
