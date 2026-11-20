# Building V2：智能施工调度架构设计

**状态：** 已在 Dedicated Server 施工链路实现。实测场景、性能口径和已知限制见 [Implementation Spec 的验证记录](BUILDING_V2_IMPLEMENTATION.md#8-实现与验证记录)。
**基线：** \`codex/unified-autonomous-navigation\` @ \`45e6ce69a650a2029b0d2bd78edc046618b4c1c3\`  
**目标：** Terraria 1.4.4.9 / tModLoader 2026.09.3.1 Stable  
**主要参考：** Numen 1.21.1 Building 系统

## 1. 目标与范围

当前 Building V1 已经具备二维 Blueprint、BuildDiff、BuildPlaceTask、NativePlacementSurvey、原生 ItemCheck 放置及结果核验。V2 的重点不是增加新的放置种类，而是把当前逐目标施工任务提升为能够理解依赖、组织工作区域、选择施工位置、管理材料并恢复施工的调度系统。

目标体验：

模型提交 Blueprint 后，施工器能够：
- 识别哪些目标已完成、可施工、需要准备或依赖其他目标。
- 建立施工依赖并选择合理的目标顺序。
- 根据 Terraria 的真实放置范围和导航能力选择工作站点。
- 在一个站点连续完成当前可施工的目标，减少移动和材料切换。
- 在施工现场变化、材料不足或任务中断时局部重新调查并继续。
- 返回精简、可执行的结果和性能证据。

V2 不负责替模型设计建筑外观。Blueprint 仍由模型生成；V2 负责把设计转化为高效、可靠的施工计划。

## 2. Numen 源码研究与可借鉴设计

研究基线：Numen \`1.21.1\`。

| Numen 模块 | 核心职责 | Terraria V2 对应 |
|---|---|---|
| \`BuildSurvey\` | 逐目标现场调查、目标分类 | Building Survey |
| \`BuildCellRules\` | Survey 和施工共用的格子规则 | NativePlacementSurvey + Building Dependency |
| \`BuildOrder\` | 层次、顺序和施工优先级 | Build Dependency / Ordering |
| \`BuildCompanionTask\` | Consent、Walk、Work 阶段及施工循环 | Build Scheduler |
| \`BuildPlacer\` | 放置预演和执行 | 现有 Native Placement Executor |
| \`BuildLedger\` | 需求、缺料、消耗账本 | BuildingMaterials V2 |
| \`BuildInventory\` | 材料准备与库存变更跟踪 | ActorInventory 集成 |
| \`BuildOutstanding\` | 失败分类和后续处理建议 | Build Result / Recovery |

Numen 的关键工程经验：

1. Survey 与执行共享规则，避免预检和施工结论分裂。
2. 施工分为准备、移动、工作阶段，避免把导航和放置混成一个逐格循环。
3. 按层和施工依赖排序；对暂时缺少支撑的目标延迟，而不是立即失败。
4. 一个施工站点内尽量完成所有当前可达目标，再移动到下一个站点。
5. 根据施工层、当前位置和可点击/可达条件决定工作顺序，避免近处结构遮挡远处目标。
6. 施工过程中持续检查世界和材料变化。
7. 部分完成是正常结果；同一设计可重新 Survey 并继续，世界状态作为进度依据。
8. 未完成项使用结构化原因表达，而不是只有一个整体失败状态。

不直接移植 Minecraft 的 BlockState、BlockPlaceContext、方块支撑算法或 \`SET_DIRECTLY\` 写入路径。Terraria 的最终放置继续使用现有 \`Player.ItemCheck()\`。

## 3. 当前 Terraria V1 基线

### 已有能力

- \`BuildingBlueprint\`：解析 tiles、walls、objects 三层目标，解析多格物体的 TileObjectData 占地。
- \`BuildDiff\`：只读逐项调查、材料汇总、可达性和支持候选。
- \`NativePlacementSurvey\`：统一单项原生放置调查，区分原生可替换、需要准备、可尝试和未知。
- \`BuildPlaceTask\`：有界异步任务，逐目标执行、材料持用、有限水平接近、缺支撑延迟、取消与部分结果。
- \`ActorPlacement / ActorObjectPlacement\`：单项放置 admission 和原生效果核验。
- \`ActorInventory\`：带 Token 的物品持用和背包操作。
- 现有导航：落脚区域、路径搜索、移动执行和输入释放。
- 任务运行时：ChildExecutor、取消、超时、Actor/World 身份绑定。

### V1 的主要限制

当前 \`BuildPlaceTask\` 的目标队列按 Tile、Wall、Object 分类，再按 Y 坐标排序。它逐目标重新 Survey，必要时启动单独的 \`navigate_to_x\` 和 \`hold_inventory_item\` 子任务，再启动一次 \`place_item_native\`。

因此它虽然减少了模型与 MCP 的往返，但施工器仍缺少：
- 显式依赖图和可解释的依赖解除。
- 多目标工作站点选择。
- 以实际放置可达性为依据的批量目标分组。
- 对站点内目标的顺序优化。
- 统一的材料准备计划。
- 外部变化后的局部重规划。
- 足够的阶段耗时和移动统计。

V1 还存在一项已知问题：材料摘要中的 available 与实际可持用 usable 范围不同。V2 必须在账本中明确区分，不让“可用材料”含义含混。

## 4. V2 目标架构

\`\`\`text
Blueprint
   |
   v
Building Survey ---- NativePlacementSurvey
   |                         |
   v                         v
Build Dependency Graph <- Native placement facts
   |
   v
Material Ledger
   |
   v
Build Scheduler
   |---- Worksite Planner
   |---- Target Ordering
   |---- Material Batch Planner
   |---- Navigation Adapter
   |
   v
Native Placement Executor
   |
   v
World Verification
   |
   v
Outstanding / Recovery Report
\`\`\`

职责边界：

### 4.1 Building Survey

负责对 Blueprint 和当前世界生成一份施工快照。每个目标至少包含：
- 稳定目标 ID（由 Blueprint 层、锚点坐标、物品类型和样式组成）。
- Layer、目标点击坐标、占地范围、材料类型。
- NativePlacementSurvey 的状态、阻挡和 Alternate 信息。
- 是否已匹配、是否可以尝试、是否需要移动。
- 所需依赖和依赖来源。
- 目标对应的世界/Actor 代次及调查时间。

Survey 负责观察和分类，不移动角色、不改变库存、不执行放置。

### 4.2 Build Dependency Graph

将目标之间的关系表示为有向依赖图。边的含义为“先完成 A，B 才具备施工条件”。

依赖来源：
- 多格物体的原生 Anchor / 支撑要求。
- Blueprint 中提供的前景、背景或支撑目标。
- 目标占地冲突和施工顺序约束。
- 由原生 Survey 返回的缺失 Anchor 信息。

优先使用原生 TileObjectData / NativePlacementSurvey 的结构化信息，不自行重写 Terraria 支撑算法。

依赖处理：
- 依赖已在世界中满足：目标可进入候选队列。
- 依赖属于 Blueprint 且尚未完成：目标进入 deferred。
- 依赖不在 Blueprint 且世界中不存在：报告 missing_support，不自动生成新设计。
- 依赖循环：报告 dependency_cycle，不无限重试。
- 目标已匹配：视为依赖已满足。

不要把所有普通 Tile 都强行建立支撑边。只建立有原生依据或明确施工顺序依据的依赖。

### 4.3 Build Ordering

施工排序采用分层优先 + 依赖可满足 + 当前站点收益的组合，而不是单一 Y 排序。

建议默认优先级：
1. 满足其他目标依赖的基础结构。
2. 当前工作站点可完成且不会被后续结构遮挡的目标。
3. 同材料、同层、空间相近的目标。
4. 需要在最后完成的家具和依附物体。

对同一多格对象，只保留一个逻辑目标；占地格只参与冲突和依赖分析，不重复消耗材料。

### 4.4 Worksite Planner

工作站点是一个可以站立、且能够从该位置对一组目标执行原生放置的玩家位置/落脚区域，不等于每一个目标坐标。

候选站点来源：
- 当前玩家位置。
- 当前导航系统返回的安全落脚区域。
- Blueprint 周边已观察到的安全平台/地面区域。
- 前一站点附近仍可利用的安全区域。

每个候选站点需要评估：
- 是否可达，且导航搜索结果是完整的；预算耗尽不能当成不可达。
- 玩家碰撞盒和安全落脚条件。
- 对目标的原生放置距离/点击范围。
- 从该站点能完成多少个当前 ready 目标。
- 材料批次切换数量。
- 与当前玩家位置和下一站点的移动成本。
- 是否会被正在施工的结构遮挡或改变可达性。

站点选择目标是减少总移动和切换，同时保持依赖正确。首版采用有界贪心调度即可，不需要全局最优求解。

推荐评分：
- 当前站点可完成目标数量越多越好。
- 依赖可满足、材料已持有的目标优先。
- 移动距离、材料切换和预计遮挡风险越低越好。
- 不可达、未知或导航预算不完整的候选不得被当作已证明不可达。

### 4.5 Build Scheduler

任务阶段建议：

1. **Survey**：建立初始施工快照和依赖图。
2. **Prepare**：选择当前站点和材料批次。
3. **Travel**：移动到站点；复用现有导航任务。
4. **Work**：完成该站点范围内的 ready 目标。
5. **Reconcile**：核验已完成目标，更新依赖和局部现场状态。
6. **Replan**：必要时选择下一个站点或重新 Survey。
7. **Finish**：生成最终完成/部分完成/未知结果。

每个站点工作期间：
- 先处理基础结构和会解锁依赖的目标。
- 在可达范围内按材料分组，减少快捷栏切换。
- 对家具按原生放置点、Alternate 和占地核验。
- 每次原生放置后更新目标状态；不要对已匹配目标再次执行。
- 发生部分副作用或 unknown 时停止该目标的重试，重新观察后再决定任务是否能继续。

### 4.6 Navigation Integration

不新增建筑专用运动控制器。

复用现有导航的安全落脚区域和移动任务，但为施工器增加一个高层适配器：
- 输入一组施工目标，输出能够覆盖其中最多 ready 目标的可达站点。
- 路径搜索必须保留 complete / timeout / unknown / unreachable 的区别。
- 站点移动完成后重新确认落脚状态和目标可达性。
- 施工过程中如果地形改变、路径失效或玩家被击退，返回 Survey/Replan。
- 不允许用仅水平移动作为所有施工目标的唯一接近策略；是否支持垂直接近取决于现有导航已验证能力，未支持时明确返回对应原因。

### 4.7 Material Ledger

账本分为：
- **Required**：Blueprint 总需求。
- **Carried usable**：当前可直接被原生放置选中的背包槽材料。
- **Carried unavailable**：存在背包但当前不在可直接持用范围的材料。
- **Stored nearby**：已观察容器中的材料，只表示可取回，不算已持有。
- **Reserved**：为当前施工批次预留的材料。
- **Consumed**：由原生放置证据确认的实际消耗。
- **Missing**：当前没有可用来源的缺料。

材料准备策略：
- 当前持有材料足够时连续施工。
- 同一材料批次尽量在一个站点完成。
- 缺料时不自动搜刮或清空箱子；返回精确缺口，由 Agent 决定采集、合成或从容器取出。
- 若箱子已在 Blueprint 周边的观察上下文中出现，可在只读调查中列出附近材料来源和容器引用，但不隐式打开/转移。
- 物品变化、容器 Token 过期或外部玩家修改后重新核验账本。

### 4.8 Outstanding / Recovery

最终和中途回执都应提供：
- 目标总数、已匹配、已放置、延迟、阻塞、未知。
- 每个未完成目标的 ID、层、坐标、物品类型、占地和原因。
- 是否可以直接继续。
- 需要移动、挖掘、材料、支撑、重新观察或修改 Blueprint 的建议。
- 已确认的副作用和原生证据。
- 当前阶段、站点和进度。

重复提交同一 Blueprint 时重新 Survey；已匹配目标跳过。不能只凭旧任务内存状态认定世界仍保持原样。

### 4.9 Observability

施工任务应在最终回执中提供精简统计：
- survey 次数及耗时。
- 站点数量、站点切换次数。
- 导航调用次数和累计耗时。
- 原生放置尝试/成功/未知次数。
- 材料切换次数。
- 依赖延迟和解除数量。
- 局部重调查次数。
- 总任务耗时和各阶段耗时。

详细逐目标记录保留在调试日志或可查询任务结果中，不将大量重复 JSON 暴露给模型。

## 5. 状态与接口决策

V2 保持现有 \`build_diff\` 和 \`build_place\` 的主要输入兼容。

\`build_diff\` 继续是只读操作，但增加：
- dependency / plannedSupport 信息。
- 材料来源分类。
- 当前站点覆盖估计（如果导航证据足够）。
- 对每个目标的 nextAction。

\`build_place\` 继续接收 Blueprint，任务内部使用 V2 Scheduler。首版不新增额外的模型工具，避免模型必须理解内部调度步骤。

任务状态：
- \`survey\`
- \`prepare\`
- \`travel\`
- \`work\`
- \`reconcile\`
- \`replan\`
- \`completed\`

整体结果：
- \`completed\`：所有 Blueprint 目标最终重新观察均匹配。
- \`partial\`：存在明确未完成目标，但已完成部分均有证据。
- \`unknown\`：关键原生动作副作用或 Actor/World 状态无法确认。
- \`failed\`：输入无效、依赖不可满足或明确不能继续。

## 6. 不在 V2 范围内

- 自动生成建筑蓝图或决定建筑风格。
- 自动挖掘南瓜、石块、箱子或玩家建筑。
- 自动清空附近箱子或擅自转移储存物品。
- 直接写 Tile 或绕过 \`Player.ItemCheck()\`。
- 新增 NPC、经济、农业、钓鱼等玩法。
- 全局最优路径规划；首版使用有界、可解释的站点贪心选择。

## 7. 验收方向

V2 的成功标准是施工调度行为确实发生改变，而不只是增加统计字段：

- 同一 Blueprint 的基础结构、依赖目标和家具按依赖关系施工。
- 能选择一个站点连续完成多个目标，而不是每个目标都单独导航。
- 同材料目标尽量连续施工。
- 施工现场改变后能够局部重调查，不盲目重复已执行动作。
- 材料不足、依赖未满足和不可达目标能准确区分。
- 继续通过 Native Placement Survey、原生 ItemCheck、Housing 和取消/续建回归。
- 通过同一组 Dedicated Server Fixture 与 V1 对比移动、切换和施工调度统计。

真实模型 Host & Play 作为实现后的下一阶段验收，不作为 V2 代码合并的替代证据。
