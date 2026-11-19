# G1：Gameplay Integration MVP

**状态：** 实现设计基线，待 Codex 实现  
**目标后端：** Dedicated Server 上绑定的 Server AI Player  
**真实验收环境：** Host & Play + 真实模型  
**代码基线：** \`codex/unified-autonomous-navigation\`，\`30075a4a211293216bfa2aa77028983cea2752ea\`

## 1. Objective

让 Agent 在玩家只给出一次目标后，使用现有游戏能力独立完成第一个多系统玩法闭环：

> 从新角色开始，收集足够木材，制作一个原版工作台，并将它放置在合适的位置。

成功必须来自真实游戏状态和原生动作证据，而不是模型自述、Lua 返回值或单项 API 的调用成功。

G1 只验证这个垂直切片，不实现通用长期规划系统，也不扩展到地下探索、Boss 准备或完整开局进度。

### 完成条件

- 玩家只提交一次目标；中途不再提供坐标、材料数量或下一步指令。
- Agent 自行观察附近世界，选择可操作的树木并获取木材。
- 通过实际背包状态确认木材已经进入 AI 背包。
- 从当前游戏配方注册表查询工作台配方，制作至少一个原版 Work Bench。
- 将工作台放入可用快捷栏，选择合适的本地放置位置。
- 使用原生物品放置路径完成工作台放置。
- 原生回执确认完整 2×1 占地、一次放置事件、恰好消耗一件工作台材料、目标仍存在以及输入已释放。
- Agent 根据上述证据报告完成；部分成功、失败或 unknown 必须如实报告。

## 2. Current Implementation

以下为当前源码可确认的事实。

### 已有机制

- \`ServerSceneReader\` / \`SceneGeometry\` 能在局部语义场景中识别 \`tree_component\`，并提供 \`details.observedTargetTile\`。树木目前没有矿物那样的观察目标令牌。
- \`ActorMining.TreeTarget\` 和 \`ChooseTool\` 已支持使用斧头处理树干；\`mine_target\` 会自动选择适用工具并处理接近、目标重验和原生破坏核验。
- \`NativeMineTargetWorld.FindOpening\` 的局部开路只接受普通镐类目标。因此，斧头无法处理的树木障碍应通过选择另一棵可接近的树解决，不能假设任务会用斧头开路。
- \`NativeSurvival\` 已接入原生 \`Player.GrabItems\`；附近掉落物可由正常游戏更新自动拾取。另有 \`collect_drops\` 异步任务处理仍留在附近的掉落物。
- \`ActorCrafting\` 已提供运行时配方查询和原生合成，执行时重新检查配方、材料、制作站及库存空间。
- \`place_item\` 已通过 \`ActorPlacement.PrepareUnified\`、\`ActorItemUse\` 和绑定 Actor 的原生 \`Player.ItemCheck()\` 执行。标准多格物体由 \`TileObjectData\` 解析；工作台的默认 2×1 形状及支撑候选已有专门观察支持。
- \`inspect_inventory\`、槽位 Token、\`transfer_inventory_item\` / \`swap_inventory_slots\` 和 \`select_hotbar_slot\` 可用于整理快捷栏。
- Agent 已有 \`read_skill\`、\`execute_lua\`、有界 Lua Runtime 和 \`submit_task/query_task/await_task/cancel_task\`。
- 游戏任务独立于 Lua Program 生命周期。任务完成通知会唤醒 Agent，但 \`task_notification\` 轮次通过 \`allow_actions=False\` 保持只读。
- \`agent_run.goal_outcome()\` 尚无通用自然语言目标契约；当前单项动作完成不能证明多步骤目标完成。

### 可复用测试

- \`tests/native_crafting.py\` 已有 \`harvest-pickup-craft\` 场景：通过真实 Lua/IPC 和原生玩家执行挖掘、移动、拾取等待、运行时配方查询及工作台合成；它不覆盖模型自主选树，也没有完成工作台放置。
- \`tests/native_server_ai_agent.py\` 已有 Agent → 模型 → Lua → 原生 Server AI 的集成测试入口，并支持显式启用真实模型；现有场景主要验证移动和探索。
- \`tests/native_server_ai_placement.py\` 验证单格 \`place_tile\`，不是工作台的统一 \`place_item\` 放置闭环。
- 相关自动化测试包括 \`test_gameplay_skills\`、\`test_m3_program_tasks\`、\`test_server_ai_agent\`、\`test_server_ai_crafting\`、\`test_server_ai_placement\`、\`test_mine_target\` 和场景层测试。

上述机制存在不等于本阶段的组合流程已经通过真实模型验收。

## 3. Design

### 3.1 以 Agent / Skill 组合现有能力

新增一篇内置 Skill：

\`skills/wood_and_workbench/SKILL.md\`

Skill 负责解释工作台制作流程、如何发现树木、如何组合当前 API、如何核验每一步及何时停止。不要把固定物品 ID、固定坐标或一份假设所有世界都成立的 Lua 流程写成硬编码策略。

工作流原则：

1. 读取当前 Capability Manifest / API Help，确认本次握手开放的观察、任务、库存、合成和放置能力。
2. 读取玩家状态和背包，确认初始工具、空余空间及当前材料。
3. 使用 L0 \`observe_scene\` 观察树木、地形和危险。树木对象从 \`details.observedTargetTile\` 取得实际 Tile 坐标；先用新鲜 \`inspect_tile\` 核对目标仍是可破坏的树干，并确认背包存在适用斧头。
4. 对树干使用 \`mine_target({x,y})\`。树木没有矿物目标 Token，不得伪造 \`target\`。服务端会在任务开始时重新读取并验证实际目标。优先选择当前可达或能在现有地形上找到安全站位的树；若不可达，换另一棵树，不要求导航器或斧头执行局部开路。
5. 破坏后先重新检查背包。正常 \`GrabItems\` 已拾取时不再重复收集；只有仍观察到附近木材掉落物且库存不足时，才考虑 \`collect_drops\`。掉落物消失本身不能证明入包。
6. 用 \`resolve_content\` 解析原版 Work Bench，再用 \`query_recipes\` 查询当前实际配方。按返回的 \`supported\`、\`available\`、材料、制作站和输出空间决定是否可制作；使用新鲜 \`recipeId/recipeToken\` 调用 \`craft_recipe\`。不得硬编码配方 ID 或仅凭 Wiki 推断当前可制作。
7. 合成后重新读取背包和新鲜槽位 Token。必要时把工作台移到空闲快捷栏，并通过 \`hotbarKey\` 选择快捷键。每次库存变更后重新观察受影响槽位。
8. 持有工作台时调用 L0 \`observe_scene\` 的 \`objectPlacementCandidates\`，选择完整占地、支撑已知且安全、干燥、没有玩家碰撞并处于原生交互范围内的候选。候选只是预检，不是放置许可。
9. 通过 \`submit_task({operation="place_item",arguments={x,y}})\` 放置；使用同一 Task ID 查询或等待终态。完成后重新观察工作台及背包。
10. 只在所有必要效果均有可信证据时报告完成。预算不足、无可达树木、背包空间不足、配方不可用或放置候选不存在时，保留已确认进度并说明停止原因。

循环必须有界，并为后续核验保留 API / action / model-tool 预算。材料需求来自实际配方；树木数量按背包净增动态决定，不预设“一棵树一定够”。

### 3.2 Agent 任务衔接

G1 优先复用现有 Agent 多工具决策循环和 Program Task API。

- 模型应在当前用户请求对应的 Agent Run 中提交并等待必要的游戏任务终态，再决定下一步；可用多次有界 \`await_task\` / \`query_task\`，每次都核对 Task ID、状态及绑定。
- 不要把 Lua Program 正常结束、等待超时或任务受理当作任务完成。
- 不依赖 \`host_task_result\` 通知轮次执行后续世界修改；该轮次继续保持只读。
- 若任务无法在当前 Run 的预算内可信收尾，报告部分进度和阻塞原因。只有真实测试证明这是 G1 的必要阻塞时，才另行提出有明确授权范围、总预算和取消语义的最小续行设计；不要直接移除 \`allow_actions=False\` 或扩大通用 Agent 权限。
- 不增加通用 Goal Session、跨重启任务恢复或长期规划器。

### 3.3 G1 目标完成证据

增加一个范围严格限定的工作台闭环完成判定，复用现有动作回执验证。

**制作证据：**

- \`craft_recipe\` 回执通过现有 \`verified_server_craft\` 检查。
- 原生配方产物确认为 vanilla Work Bench，且 \`completedCrafts >= 1\`、\`producedCount >= 1\)、\`inventoryVerified=true\`。
- 结束证据确认输入/动作生命周期已可信结束。

**放置证据：**

- \`place_item\` 任务及其终态通过现有 \`atomic.terminal\` 原生验证。
- 任务结果证明 \`kind=object\`、物品为 vanilla Work Bench、样式为默认样式、实际占地为 2×1。
- \`nativePlacementEvents=1\`、\`placedByActor=true\`、\`targetPresent=true\`、\`targetChanged=false\`、\`uncertainNativeEffect=false\)、\`consumedCount=1\)。
- \`effectVerified=true\`、\`inputsReleased=true\)，并且 Actor / World / Generation / Slot 与制作动作一致。

可以复用当前固定引擎下已验证的 vanilla Work Bench 内容标识；Skill 侧仍应通过 \`resolve_content\` 获取运行时内容，不硬编码物品 ID 或配方 ID。

**结果语义：**

- 只有同一 Agent Run、同一绑定下的制作和放置证据都通过，才把 G1 工作台闭环标记为 \`completed\`。
- 已确认制作但放置尚未完成时报告部分进度；相关动作出现 \`unknown\` 时不得升级为成功。
- 模型文本、Lua return 值和 Skill 自述不能作为完成证据。
- 该判定只说明“工作台已制作并放置”，不扩展成通用自然语言目标判定。

### 3.4 必须保持的现有行为

- Server AI Capability Manifest、IPC 白名单和后端参数校验仍是能力权威来源。
- 所有世界修改继续绑定当前 Actor / World / Generation / Slot，并在游戏线程执行。
- 原生效果、库存变化、输入释放和 \`unknown\` 语义不得弱化。
- 不因 G1 测试失败而放宽导航预算、危险检查、目标时效或原生放置条件。
- 不重放 \`unknown\` 动作；明确失败后只能依据现有契约重新观察并选择新动作。
- Dedicated Server 和 Host & Play 的验证结论分别记录。

## 4. Implementation

建议按以下顺序实施：

1. 对照当前源码确认本文件描述的接口与回执字段；如有差异，以代码和当前能力握手为准，并修订本文。
2. 新增 \`wood_and_workbench\` Skill，提供简洁的流程说明、停止条件和一段符合当前 API Schema 的 Lua 示例。
3. 补齐工作台流程所需的 Agent 指引，使模型能在一个用户请求内持续观察、执行、等待任务并根据结果决定下一步。
4. 在 \`agent_run.py\` 或现有结果归一化层增加 G1 专用证据判定，并让 Runtime Outcome / 最终事实回复使用该判定。保持其他 Goal Outcome 的既有语义。
5. 扩展现有原生工作流测试：覆盖树木识别与斧头采集、掉落入包、动态配方合成、库存整理、工作台 \`place_item\` 放置和最终复查。
6. 扩展 Agent 集成测试：同一用户指令驱动模型完成多步流程，记录模型工具调用、Lua 程序、任务终态、动作证据和最终回复。
7. 只有发现现有接口不能支持闭环时才修改游戏侧执行代码；任何新增接口必须同步 Schema、能力握手、Help、回执契约和定向测试。

预期主要修改范围：

- \`skills/wood_and_workbench/SKILL.md\`
- \`python/agent.py\`、\`python/server_ai_contract.py\`（仅在确有提示/流程缺口时）
- \`python/agent_run.py\` 及必要的动作结果验证代码
- \`tests/test_gameplay_skills.py\`、\`tests/test_m3_program_tasks.py\`、\`tests/test_server_ai_agent.py\` 或新增的 G1 定向测试
- \`tests/native_crafting.py\`、\`tests/native_server_ai_agent.py\` 或新增的原生工作流测试
- 对应专题文档、\`docs/INDEX.md\` 和 \`docs/DEVELOPMENT_ROADMAP.md\`

不预设必须修改 C# 执行器；优先证明现有能力能否完成流程。

## 5. Testing

按层级执行，不运行无关的全量测试。

### A. 静态与契约检查

- Skill 注册、元数据、大小限制和索引读取正常。
- 所有 Lua 示例通过当前 API Help / Schema；参数使用新鲜观察结果和库存 Token。
- 目标完成判定对伪造 Lua return、缺失字段、错 Actor/Generation、部分制作、放置未发生及 unknown 均拒绝成功。
- \`git diff --check\` 通过。

### B. Python 定向测试

至少覆盖：

- \`test_gameplay_skills\)
- \`test_m3_program_tasks\)
- \`test_server_ai_agent\)
- \`test_server_ai_crafting\`
- \`test_server_ai_placement\`
- \`test_mine_target\)
- 新增的 G1 Goal Outcome / Agent 工作流测试

验证一个 Agent Run 内多个 \`execute_lua\` / Task 结果的顺序、预算共享、任务等待超时和终态处理。特别验证 Agent 不会因为任务通知只读而误报完成，也不会依赖该通知继续修改世界。

### C. Dedicated Server 原生闭环

复用现有复制世界与隔离专服测试设施。初始角色不得预置木材或工作台；测试环境可以准备确定的树木和放置区域，但不得直接发放目标材料或伪造原生效果。

至少验证：

1. 从场景识别树干，选择适用斧头并通过 \`mine_target\` 完成原生破坏。
2. 正常 \`GrabItems\` 或 \`collect_drops\` 后，AI 背包木材净增与实际掉落一致。
3. 查询当前注册表的 Work Bench 配方，原生合成并核对产物入包。
4. 通过新鲜 Token 整理快捷栏，选择工作台。
5. 使用 \`place_item\` 通过原生 \`ItemCheck\` 放置完整工作台，核对 2×1 Frame、支撑、一次原生放置事件、消耗一件和输入释放。
6. 再次读取场景与背包，确认工作台仍存在且材料数量正确。
7. 覆盖树木不可达、斧头缺失、掉落未入包、背包满、材料不足、配方不支持、快捷栏无空位、放置位置被占用、任务超时/取消及 unknown。所有失败都不得报告目标完成或盲目重试。

### D. Agent 端到端测试

- 使用现有 Agent 集成测试设施，先以确定性模型验证完整调用链。
- 增加可选真实模型模式；真实模型测试只发送一次用户指令，模型自行读取 Skill、观察、执行和核验。
- 记录模型请求/工具调用、Lua 源码摘要、任务 ID、各动作回执、初始/最终背包、最终场景和最终回复。
- 测试世界使用新角色档案及干净保存目录；开始时无木材、无工作台，附近存在至少一棵可安全接近的自然树木。
- 真实模型未运行时，明确报告“未执行”，不得用确定性模型或离线回执替代。

### E. Host & Play 手动验收

在图形 Host & Play 世界中，使用新 AI 角色和真实模型，只发送一次：

> 收集足够的木材，制作一个工作台，并放置在附近合适的位置。

不再人工给出坐标、选栏或下一步操作。核对真实客户端同步、AI 背包和工作台世界状态，以及 Agent 的最终回复。记录游戏版本、Mod 包 SHA、模型、世界副本和 Trace 位置。

### 通过标准

- A、B、C 全部通过。
- D 的确定性模型闭环通过；真实模型结果单独报告。
- E 成功完成才可宣布 G1 的真实模型验收通过。
- 若 E 失败，按 Trace 区分 Agent 决策、API 能力、导航/场景、原生执行或预算问题，形成下一项具体任务；不把一次失败概括为所有同类能力不可用。

## 6. Deliverables

1. G1 工作台 Skill。
2. 必要的 Agent / 结果判定代码及定向测试。
3. Dedicated Server 原生闭环测试及可复现命令。
4. Agent 集成测试结果；真实模型是否执行及结果明确标注。
5. 更新后的接口/玩法文档和路线图。
6. Git Commit，提交说明包含实现范围、测试命令、通过结果及未完成项。

本阶段只交付“新角色收集木材并制作、放置工作台”的可验证闭环。后续熔炉、矿物装备、地下探索和 Boss 准备属于后续阶段，不自动并入 G1。
