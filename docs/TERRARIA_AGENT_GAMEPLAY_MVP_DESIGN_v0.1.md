> **历史设计/审计记录**：本文描述特定代码基线下的方案与审查结论。当前实现以对应专题文档和源码为准；本文中的待办、缺口和限制属于当时状态。

# Terraria Agent Gameplay MVP 设计 v0.1

**状态：** MVP 设计基线，待实现\
**目标后端：** 独立 Server AI Player\
**代码基线：**
`codex/unified-autonomous-navigation`，`b4d168653c0574aa16541687ec6e14bdca671072`\
**设计参考：** Numen `1.21.1`、TerraBot `main`、官方 Terraria Wiki

------------------------------------------------------------------------

## 1. 目标与定位

本阶段的目标不是继续增加底层游戏 API，而是让 Agent
能够使用现有能力，独立完成一批有实际意义的 Terraria 游戏任务。

玩家给出目标后，Agent 应当能够：

1.  理解目标属于聊天、信息查询还是游戏行动。
2.  读取当前角色状态，按需观察附近世界。
3.  选择适用的游戏知识 Skill。
4.  将目标组织成有界 Lua 程序，组合已有原子 API。
5.  根据真实执行结果继续、调整或停止。
6.  只报告已经由游戏状态或宿主回执确认的结果。

**核心原则：先让 Agent
熟练复用经过整理的玩法，再考虑让它从经验中自动学习。**

本 MVP 采用：

-   稳定的 Agent Harness。
-   简洁、明确的 System Prompt。
-   人工整理并版本化的 Terraria Skills。
-   每轮请求提供精简、最新的 Runtime Context。
-   LLM
    负责目标理解和策略选择；Lua/游戏侧负责有界执行、持续动作和效果核验。

本阶段不实现自动生成 Skill、自动修改 System Prompt、自动修改 Harness
或通用自进化机制。

## 2. 参考项目的设计取舍

### 2.1 Numen：借鉴 Agent 教学与上下文组织

参考 Numen `1.21.1` 分支，提交
`24504701c5f67f23106b845b42d59a43b4ad2c93`。

重点借鉴：

-   **稳定系统提示前缀：** 人设、行动原则、技能目录、API
    索引和说话规则放在稳定区域。
-   **Skills 按需加载：** 系统提示只列出技能名称和用途；模型通过 Skill
    工具读取相关 Markdown 正文。
-   **动态 Runtime State：**
    当前背包、装备、效果、身体状态和任务不写入长期聊天历史，而是在每轮请求时重新提供。
-   **行动原则：** Act, don't narrate；Verify, don't assume；Failed
    results teach；One call is a one-line program；Plan only what's
    big。
-   **职责分离：** API 索引告诉模型有哪些能力，Skill
    讲游戏规则和工作流程，工具帮助信息解释单个 API 的具体用法。

适配到本项目时：

-   保留现有 `execute_lua` 作为游戏行动入口。
-   增加一个只读 `read_skill` 工具，按需加载 Skill。
-   不把完整 Terraria Wiki 或全部 Skill 正文塞入 System Prompt。
-   简单请求直接执行，不为每个小动作强制读取 Skill 或建立计划。
-   Runtime Context 只提供当前事实，不把历史快照伪装成当前状态。

Numen 的 `todo`、Memory、自动模块保存和更复杂的长期目标机制不属于本
MVP。

参考源码： -
[NumenPrompts.java](https://github.com/Dwinovo/minecraft-numen/blob/24504701c5f67f23106b845b42d59a43b4ad2c93/ai/src/main/java/com/dwinovo/numen/api/agent/prompt/NumenPrompts.java) -
[SystemPromptComposer.java](https://github.com/Dwinovo/minecraft-numen/blob/24504701c5f67f23106b845b42d59a43b4ad2c93/numen-api/common/src/main/java/com/dwinovo/numen/api/agent/request/SystemPromptComposer.java) -
[RuntimeState.java](https://github.com/Dwinovo/minecraft-numen/blob/24504701c5f67f23106b845b42d59a43b4ad2c93/numen-api/common/src/main/java/com/dwinovo/numen/api/agent/request/RuntimeState.java) -
[SkillRegistry.java](https://github.com/Dwinovo/minecraft-numen/blob/24504701c5f67f23106b845b42d59a43b4ad2c93/numen-api/common/src/main/java/com/dwinovo/numen/api/agent/skill/SkillRegistry.java)

### 2.2 TerraBot：借鉴 Terraria 玩法组织方式

参考 `Sprxxx/terrabot` 的 `main` 分支，提交
`dcd836efc3570445db9293442e9e980a714f65d8`。

TerraBot 不是 LLM Prompt 架构，而是程序化的行为系统。它的价值在于展示了
Terraria 的实际任务应该怎样拆分和衔接。

重点借鉴：

-   `BotBrain` 负责选择当前工作并协调技能。
-   `ProgressionPlanner` 用
    Need/依赖关系描述目标，通过游戏配方递归求解材料、制作站和最终物品。
-   `ISkill` 将探索、移动、采集、制作、装备、战斗等行为组织成可启动、逐
    Tick 执行、可停止的技能。
-   `Work` 与 `Override`
    分离：战斗、撤退、拾取等紧急行为可以暂停当前工作，结束后恢复原工作。
-   失败次数、无进展监视和退避机制避免 Agent 永久卡在同一任务。
-   自动进度链展示了从初始工具、工作台、熔炉、铁砧、铁/铅镐，到房屋、装备和早期
    Boss 准备的实际玩法顺序。

本项目不复制 TerraBot 的硬编码物品 ID、固定世界路线或 Tick
控制实现。应将它的玩法知识和依赖关系整理为可供 LLM 读取的
Skills，并通过当前项目的动态内容解析、配方查询和原生动作接口执行。

参考源码： -
[README.md](https://github.com/Sprxxx/terrabot/blob/dcd836efc3570445db9293442e9e980a714f65d8/README.md) -
[BotBrain.cs](https://github.com/Sprxxx/terrabot/blob/dcd836efc3570445db9293442e9e980a714f65d8/Bot/Brain/BotBrain.cs) -
[ProgressionPlanner.cs](https://github.com/Sprxxx/terrabot/blob/dcd836efc3570445db9293442e9e980a714f65d8/Bot/Brain/ProgressionPlanner.cs) -
[ISkill.cs](https://github.com/Sprxxx/terrabot/blob/dcd836efc3570445db9293442e9e980a714f65d8/Bot/Skills/ISkill.cs)

### 2.3 官方 Terraria Wiki：作为游戏知识来源

第一批 Skill 以原版 Terraria 为基线，优先整理以下主题：

-   游戏进度循环：探索新区域、面对新危险、取得新资源、升级装备、挑战
    Boss、解锁新区域。
-   开局生存：木材、基础庇护所、工作台、火把和早期武器。
-   地下探索：矿物、生命水晶、地下房间、宝箱和移动配饰。
-   采矿与制作：镐力要求、世界矿物变体、熔炉、铁/铅砧及配方依赖。
-   装备成长：防御、伤害、职业加成、通用配饰和移动能力。
-   Boss 准备：装备、药水、竞技场、地形和攻击阶段。

主要资料： - [Guide: Game
progression](https://terraria.wiki.gg/wiki/Guide%3AGame_progression) -
[Guide: Getting
started](https://terraria.wiki.gg/wiki/Guide%3AGetting_started) -
[Crafting stations](https://terraria.wiki.gg/wiki/Crafting_station) -
[Ores](https://terraria.wiki.gg/wiki/Ore) -
[Pickaxes](https://terraria.wiki.gg/wiki/Pickaxes) - [Life
Crystal](https://terraria.wiki.gg/wiki/Life_Crystal) - [Guide: Class
setups](https://terraria.wiki.gg/wiki/Guide%3AClass_setups) -
[Arena](https://terraria.wiki.gg/wiki/Arena) - [Guide: Eye of Cthulhu
strategies](https://terraria.wiki.gg/wiki/Guide%3AEye_of_Cthulhu_strategies)

Wiki 是知识来源，不是运行时事实来源。Skill
必须注明适用游戏版本；遇到世界种子、难度、当前进度、Mod
内容或版本差异时，优先通过当前游戏观察、内容解析和配方查询确认。不要把
Wiki 中的物品 ID、固定坐标或推荐装备当作所有世界都成立的事实。

## 3. 当前系统基线与已知差距

### 3.1 已有基础

当前 Server AI 已具备：

-   单一 `execute_lua` 模型工具入口。
-   根据 IPC 握手和后端白名单生成 Capability Manifest 与 API Contract。
-   Lua 程序组合多个查询和原子动作。
-   `runtimeInventory`、`runtimeLua`、`runtimeTasks`、中断运行记录等动态上下文。
-   `get_player_state`、`inspect_inventory`、`observe_scene`、`scan_entities`、`scan_tiles`、`query_map`
    等观察能力。
-   `navigate_to`、`navigate_to_x`、`navigate_to_landing_region`、`mine_tile`、`mine_target`、`place_tile`、`place_object`、`query_recipes`、`craft_recipe`
    等有限动作。
-   Phase 4 的钩爪、绳索、液体环境能力，以及 Phase 4.8
    的装备观察、`equip_item`、`unequip_item`。
-   Lua 预算、动作终态、效果证据、取消、输入释放和 `unknown` 保护。

具体可用 API 必须以当前连接的 Capability Manifest
为准，不能仅凭本文或客户端 API 清单推断。

### 3.2 当前 Prompt 的主要差距

当前 Server AI 的 `ROLE_PRINCIPLES` 主要要求模型：

-   根据目标和观察自主生成 Lua 程序。
-   不要求玩家逐步指定。
-   不套用固定采集流程。
-   在权限不足、预算不足或无进展时停止。

这些原则强调自主性和安全性，但没有充分提供：

-   明确的行动优先规则。
-   Terraria 常见任务的推荐工作流程。
-   何时加载哪一个 Skill。
-   简单任务和多阶段任务的不同处理方式。
-   可以直接模仿的 Terraria Lua 任务示例。

本阶段应补足这些内容，但不把完整游戏策略硬编码进底层原子 API。

### 3.3 Runtime Context 的差距

当前每轮请求已提供背包摘要、Lua
剩余预算、任务状态和部分中断记录；最近游戏事件也有独立投影。

但还没有形成统一、精简的玩家实时状态块。玩家位置、速度、生命、支撑状态、装备和附近威胁仍可能需要模型主动调用不同查询接口。

建议增加统一 Runtime State
投影，复用现有读取能力，不为此重新设计游戏观察协议。

### 3.4 当前能力契约需要先同步的一处问题

当前 `server_ai.operations_for_capabilities()` 已根据
`server_ai_equipment_v1` 投影 `equip_item` 和 `unequip_item`，但
`server_ai_contract.py` 的 `CATEGORIES` 尚未将这两个 API
放入装备/背包类别，且边界描述仍写着"装备可观察，不能换装"。

因此，Phase 4.8 的新能力可能无法在模型看到的 Manifest
中得到正确说明。实现 Skills 前应同步 Manifest
分类、边界描述和对应契约测试。

同样，`attack_entity` 虽然出现在共享类别清单中，但不代表当前 Server AI
握手开放了该能力；当前模型必须以实际白名单为准。

## 4. MVP 架构

### 4.1 静态 System Prompt

System Prompt 只保留跨任务稳定的规则，建议分为：

1.  **角色与控制对象**：Agent 是独立 Server AI
    Player，不是控制真人玩家。
2.  **行动原则**：明确的游戏行动请求优先执行，不要只描述准备怎么做。
3.  **意图分类**：
    -   普通聊天、知识问答：直接回复，不调用游戏动作。
    -   单步明确操作：直接使用对应 API。
    -   多步骤具体目标：加载相关 Skill，使用一个有界 Lua 程序组织工作。
    -   目标含糊或关键条件缺失：只询问必要信息。
4.  **观察与验证**：观察不等于行动许可；动作结果以宿主回执和真实游戏状态为准。
5.  **Skill 使用规则**：系统提示列出可用 Skill；复杂任务在行动前读取相关
    Skill。
6.  **能力边界**：当前后端未开放或未验证的能力不得尝试、假装完成。
7.  **Runtime Context 说明**：告诉模型动态状态块的字段、时效和未知语义。
8.  **说话规则**：简短报告结果、部分进度和停止原因，不逐步播报内部思考。

建议核心行动原则：

> 当玩家提出明确的游戏操作目标时，优先采取行动，而不是只描述计划。简单目标直接调用对应
> API；多步骤目标使用一个有界 Lua
> 程序组合观察、决策、动作和核验。只有普通聊天、目标不明确、缺少必要信息或能力不可用时，才纯文本回复或询问。不要让
> LLM 逐 Tick 控制角色。

不要在 System Prompt 中重复完整 API 参数、全部 Wiki 内容或所有 Skill
正文。详细规则由动态 API Contract 和按需 Skill 提供。

### 4.2 Skills 系统

采用 Numen 式的 Markdown Skill 目录和按需加载方式。

建议模型工具：

-   `execute_lua`：唯一游戏行动入口。
-   `read_skill`：只读读取 Skill
    正文或其引用资料，不访问或修改游戏世界。

当前 `agent.py` 的 Server AI 分发分支明确拒绝除 `execute_lua` 以外的模型工具。因此实现时需要为 `read_skill` 增加一个受限的宿主侧只读分发分支：它只读取已注册 Skill 文件，不调用 Server AI Bridge，不执行游戏操作，也不消耗 Lua API/变更动作预算；它仍计入模型工具调用次数。保留一次模型决策只处理一个工具调用的现有限制。\n\nSystem Prompt 提供简短的 `available_skills`
索引，包含技能名和一句用途描述。模型遇到对应任务时读取
Skill；简单单步操作不必加载。

Skill 采用以下结构：

``` text
skills/
  survival_basics/
    SKILL.md
  exploration/
    SKILL.md
  mining_and_ores/
    SKILL.md
  crafting_and_progression/
    SKILL.md
  equipment_and_mobility/
    SKILL.md
  building_and_shelter/
    SKILL.md
  threat_response/
    SKILL.md
```

每份 `SKILL.md` 至少包含：

-   YAML 元数据：`name`、`description`、适用版本和范围。
-   适用场景与目标。
-   前置条件和需要观察的状态。
-   推荐工作流程。
-   对应的现有 API 及调用顺序。
-   失败、危险和停止条件。
-   结果核验方法。
-   一至两个完整、可执行或接近可执行的 Lua 示例。
-   Wiki/代码参考来源及版本说明。

Skill 内容应教模型"如何玩"，而不是重复 API Schema。

### 4.3 第一批 Skills

  ------------------------------------------------------------------------------------------------------------------------------
  Skill                               第一版职责
  ----------------------------------- ------------------------------------------------------------------------------------------
  `survival_basics`                   检查生命、装备和基本危险；判断是否适合继续当前任务

  `exploration`                       使用实时场景和探索记忆寻找新区域、资源和安全落脚点；有界探索，不把历史地图当实时地形

  `mining_and_ores`                   识别当前世界的矿物变体、核对工具能力、选择观察到的矿物目标、挖掘并核验

  `crafting_and_progression`          从目标物品查询原生配方，递归理解材料/制作站依赖，优先使用当前已有资源和可用配方

  `equipment_and_mobility`            检查功能装备槽，选择适合当前目标的已有装备；使用已支持的穿戴、卸下、导航、钩爪和绳索能力

  `building_and_shelter`              使用当前已支持的放置能力建造基础支撑、平台和工作台；核验真实放置结果

  `threat_response`                   识别危险、评估继续任务的风险、选择安全撤离或停止；不假设当前已具备完整战斗能力
  ------------------------------------------------------------------------------------------------------------------------------

第一批 Skill 以原版 Terraria 为主。Mod
内容只通过当前已加载内容、运行时能力和动态配方扩展；不为灾厄等 Mod
预先写入未经验证的专属流程。

### 4.4 Runtime Context

每次模型请求附加一个精简的、独立于历史消息的 `runtimeState` 块。

建议字段：

``` yaml
runtimeState:
  player:
    position: ...
    velocity: ...
    life: ...
    maxLife: ...
    mana: ...
    maxMana: ...
    grounded: ...
    wet: ...
    depth: ...
    biome: ...
  equipment:
    selectedItem: ...
    functionalEquipmentSlots: ...
    buffs: ...
  inventory:
    summary: ...
    emptySlots: ...
  nearby:
    hostileEntities: ...
    drops: ...
    freshness: ...
  tasks:
    active: ...
    recentResults: ...
  budgets:
    remainingToolCalls: ...
    remainingApiCalls: ...
    remainingActionAttempts: ...
    remainingSeconds: ...
```

这是目标投影结构，不要求所有字段一次实现。只提供当前后端能够可靠读取的字段：

-   缺失字段省略或明确标记 `unknown`，不能写成空列表或 `false`。
-   带时间戳、游戏 Tick 或观察范围的状态必须保留其时效。
-   `runtimeInventory`、`runtimeLua`、`runtimeTasks`
    的现有信息应复用，不重复制造第二份真相。
-   玩家基础状态优先通过现有轻量查询或可靠快照提供。
-   附近实体和完整场景不必每轮强制全量扫描；没有新鲜数据时明确提示模型按任务需要调用
    `observe_scene` / `scan_entities`。
-   Runtime State 只追加到本轮请求副本，不写入规范聊天历史。

不要每轮无条件发送完整 45×29
场景图。动态状态用于让模型知道"我是谁、现在怎样、手上有什么"；具体地形仍按需观察。

## 5. 标准行动流程

### 5.1 单步任务

例如查询装备、选择快捷栏或使用明确物品：

1.  判断请求是否明确。
2.  检查相关状态或目标。
3.  直接调用对应 API。
4.  核验结果并简短回复。

不强制加载 Skill，不建立多阶段计划。

### 5.2 多步骤任务

例如寻找指定矿物、制作指定装备：

1.  读取 Runtime State。
2.  读取最相关的一个或少量 Skills。
3.  根据 Skill 确定本次任务的完成条件和停止条件。
4.  获取必要的新鲜观察。
5.  在一个有界 Lua 程序中组合查询、目标选择和原子操作。
6.  每次世界或库存变更后，按需要重新观察并核验。
7.  根据已确认的进度继续；无法安全继续时停止并说明原因。

不要让模型为每个游戏 Tick
或每个普通移动步骤重新请求。持续移动、挖掘和其他长动作仍交给现有游戏侧执行器/Task
Runtime。

### 5.3 失败处理

-   已知失败：读取具体原因，判断是否存在不同且安全的下一步。
-   `not_admitted`：按未受理语义处理，不把它当作已执行。
-   `unknown`：停止后续变更，不自动重试或重放；先重新建立事实。
-   无进展：停止重复消耗预算，报告已完成部分和阻塞原因。
-   成功：必须有操作专属效果证据；Lua
    `return`、日志和模型自述都不构成成功证明。

保留现有 Actor/World/Generation 绑定、取消、输入释放、动作预算和
`unknown` 屏障。

## 6. MVP 的游戏范围

本阶段以"能完成一批基础游戏任务"为验收目标，不以打通原版全部 Boss 作为
MVP 完成条件。

优先支持的任务类型：

-   观察当前角色、背包和装备。
-   对已观察到的目标进行局部探索和导航。
-   采集当前后端明确支持的矿物/方块目标。
-   查询当前可用配方，使用已有材料和制作站制作指定物品。
-   穿戴、替换和卸下已有装备。
-   使用已支持的方块、工作台和绳索操作处理局部环境。
-   在任务失败、遇到危险或缺少能力时正确停止并说明原因。

### 战斗边界

Terraria Wiki 的战斗知识可以进入后续
Skill，但不能用知识弥补执行能力缺口。

当前 Server AI 的 `use_item` 不能等同于完整战斗系统；`attack_entity`
未由当前握手能力开放，通用鼠标瞄准、持续射击、弹药管理和完整 Boss
战斗流程也不属于本 MVP。

因此，`threat_response` 第一版只教 Agent
识别威胁、避免盲目深入、在能力不足时撤离或停止。完整战斗 Skill
应等攻击目标、瞄准、命中验证和持续战斗执行能力具备后再扩展。

## 7. 性能与模型调用原则

目标是减少不必要的推理和模型往返，而不是牺牲安全性。

-   System Prompt 保持短且稳定；Skill 正文按需加载。
-   简单任务不加载 Skill。
-   多步骤任务优先一次 `execute_lua` 组织多个有界步骤。
-   不要求模型逐帧控制角色。
-   不重复查询已在本轮 Runtime State 中可靠提供的数据。
-   不为"看起来在工作"而执行无目的观察。
-   每个 Skill 的流程应明确完成条件，避免模型无限探索或无限采集。
-   保留现有模型调用、Lua API、动作次数和墙钟预算。

当前 LLM 请求使用 `tool_choice=auto`，并请求关闭 thinking。MVP
第一轮不全局强制工具调用，以免普通聊天被误判为游戏操作。先通过 Trace
区分"没有产生 tool call""生成了 Lua 但没有动作""API
被拒绝"和"动作完成但目标未完成"。如果明确行动请求仍频繁只返回文字，再评估窄范围的行动意图路由，而不是直接对所有消息强制工具调用。

## 8. 验收与评测

使用同一个模型、服务商参数、存档、世界和任务，对比改造前后行为。

### 核心指标

-   明确游戏任务首次产生工具调用的比例。
-   从收到指令到第一次真实游戏动作派发的耗时。
-   第一次动作前的模型请求次数和只读查询次数。
-   Skill 加载是否相关、是否重复加载。
-   Lua 程序是否能组合多个步骤，而不是每一步重新请求模型。
-   任务完成率和已核验效果比例。
-   API 参数错误、能力误用、重复动作和无进展次数。
-   `unknown` 是否被正确停止且没有重放。
-   平均模型耗时、Token 消耗和 API 调用数量。

### 第一批测试任务

1.  "查看我现在的生命、装备和背包情况。"------应优先读取实时状态，不做世界修改。
2.  "寻找附近的铁/铅矿。"------应按需读取采矿
    Skill，观察世界并报告真实目标。
3.  "挖掉附近观察到的一处指定矿物。"------使用观察目标和
    `mine_target`，核验实际结果。
4.  "用背包已有材料制作指定物品。"------先查询配方和缺失条件，再合成并核验。
5.  "把背包里更合适的镐子装备上。"------读取装备 Skill，使用新鲜 Token
    穿戴/替换并核验。
6.  "到附近一个可观察的高台/洞穴落脚点。"------调用现有导航，不由 LLM
    手工控制每一帧。
7.  "探索附近区域，寻找可采集资源。"------使用有界探索和实时场景观察，达到停止条件后报告。

涉及采集树木、持续战斗或其他当前后端不支持的完整流程时，应单独标注为能力缺口，不把它混入基础
Skill 成功率。

### 结果诊断

  -----------------------------------------------------------------------
  Trace 现象                          优先检查
  ----------------------------------- -----------------------------------
  明确行动请求没有 `tool_calls`       行动原则、Skill 索引、模型
                                      tool_choice 行为

  调用了 `read_skill`，之后没有执行   Skill
                                      内容是否清楚、加载后是否继续行动

  调用了                              Lua 生成逻辑、程序控制流和预算
  `execute_lua`，但没有游戏动作       

  API 被拒绝                          Capability
                                      Manifest、参数契约和前置条件

  动作完成但整体目标未完成            Skill 流程、目标分解和后续核验

  长时间重复相同查询/动作             Skill
                                      停止条件、无进展判断和反馈质量
  -----------------------------------------------------------------------

### MVP 验收标准

-   简单明确的游戏操作不需要多轮无意义推理即可执行。
-   多步骤任务能在一次 Lua 程序中组合多个合理步骤；需要 Skill
    时最多读取相关知识，不重复加载。
-   Agent 能根据 Runtime State
    选择合适的起始动作，而不是每次从零查询全部状态。
-   任务有明确停止条件，不能无限探索或重复失败动作。
-   所有成功声明都由真实回执或状态证据支持。
-   未开放的战斗、瞄准或 Mod 能力不会被 Skill 伪装成已支持。
-   现有导航、库存、装备、IPC、Task Runtime 和 Actor
    生命周期安全回归通过。

## 9. 明确不属于本 MVP 的内容

-   自动生成、修改和发布 Skills。
-   自动改写 System Prompt 或 Harness。
-   新增长期情景记忆、自动经验归纳和 Skill Evolution。
-   通用长期 Goal/Todo 管理器。
-   完整 Terraria 战斗系统、虚拟鼠标和 Boss 自动战斗。
-   自动通关至 Moon Lord。
-   自动理解任意 Mod 的玩法或特殊装备效果。
-   为 Prompt 问题继续扩展底层导航 API。

现有 AI Map 仍然是世界探索记忆，不应与未来的 Agent 个人经验 Memory
混为一谈。

## 10. 建议实施顺序

1.  **校准能力契约：** 修正装备 API 的 Manifest
    分类和边界描述，确认所有模型可见 API 与握手白名单一致。
2.  **建立 Skill Loader：** 添加只读 `read_skill`，实现 Skill
    目录、元数据解析、索引和按需正文加载。
3.  **重写 Server AI 行动原则：** 明确聊天/行动分类、Act don't
    narrate、单步与多步骤处理方式。
4.  **编写第一批原版 Skills：**
    先完成生存、探索、采矿、制作/进度、装备/运动、建造和威胁响应。
5.  **增加 Runtime State：**
    复用现有读取能力，提供精简的玩家/装备/任务/预算快照。
6.  **建立 A/B 评测：**
    使用固定任务集记录首次动作时间、模型请求数、完成率和安全结果。

先完成第 1--4 项并跑通基础任务，再决定 Runtime State
是否需要新增读取路径。不要在没有 Trace 证据前重构 Lua Runtime 或 Task
Runtime。

## 11. 后续成长方向

未来可以在不改变核心 Harness 的前提下逐步增加：

-   持久化 Memory：记录玩家偏好、重要地点和已验证的环境经验。
-   经验复盘：从任务 Trace 和真实回执中提取候选经验。
-   Skill Evolution：生成候选 Markdown
    Skill，经测试和人工/受控审核后纳入技能库。
-   Skill 版本与回滚：新经验不能静默覆盖已验证的旧策略。

这些属于后续阶段，不是本 MVP 的实现条件。

------------------------------------------------------------------------

## 参考资料与版本

### 项目源码

-   Numen `Dwinovo/minecraft-numen`, branch `1.21.1`, commit
    `24504701c5f67f23106b845b42d59a43b4ad2c93`.
-   TerraBot `Sprxxx/terrabot`, branch `main`, commit
    `dcd836efc3570445db9293442e9e980a714f65d8`.
-   本项目 `729194587/terraria-ai-companion`, branch
    `codex/unified-autonomous-navigation`, commit
    `b4d168653c0574aa16541687ec6e14bdca671072`.

### Terraria Wiki

-   [Guide: Game
    progression](https://terraria.wiki.gg/wiki/Guide%3AGame_progression)
-   [Guide: Getting
    started](https://terraria.wiki.gg/wiki/Guide%3AGetting_started)
-   [Crafting stations](https://terraria.wiki.gg/wiki/Crafting_station)
-   [Ores](https://terraria.wiki.gg/wiki/Ore)
-   [Pickaxes](https://terraria.wiki.gg/wiki/Pickaxes)
-   [Life Crystal](https://terraria.wiki.gg/wiki/Life_Crystal)
-   [Guide: Class
    setups](https://terraria.wiki.gg/wiki/Guide%3AClass_setups)
-   [Arena](https://terraria.wiki.gg/wiki/Arena)
-   [Guide: Eye of Cthulhu
    strategies](https://terraria.wiki.gg/wiki/Guide%3AEye_of_Cthulhu_strategies)

**知识维护原则：** Wiki 页面会随 Terraria 版本更新。Skill
必须注明适用版本和原版/Mod 范围；关键规则应在目标 tModLoader
版本中复核。运行时真实观察、配方查询、能力握手和原生回执始终优先于静态攻略。