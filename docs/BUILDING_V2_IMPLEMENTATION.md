# Building V2 Implementation Spec

**类型：** Implementation Spec  
**设计依据：** \`docs/BUILDING_V2_DESIGN.md\`  
**代码基线：** \`45e6ce69a650a2029b0d2bd78edc046618b4c1c3\`（开始实现前确认最新 HEAD）  
**目标版本：** Terraria 1.4.4.9 / tModLoader 2026.09.3.1 Stable

## 1. Objective

按照 Building V2 架构设计，将当前逐目标队列施工升级为依赖感知、站点驱动的施工调度器。

保持现有 \`build_diff\` / \`build_place\` 的模型使用方式。主要变化发生在游戏侧：施工器先 Survey 和规划，再按站点组织目标，复用导航、库存和原生放置能力完成施工。

## 2. Current Implementation

当前 \`BuildPlaceTask\`：
- Prepare 阶段按 tiles → walls → objects，再按 Y 坐标建立 Queue。
- 每个目标重新调用 BuildDiff Survey。
- 缺支撑时放入 deferred，队列结束后有限重试。
- 目标超出范围时只调用 \`navigate_to_x\` 做水平接近。
- 每个目标分别准备快捷栏材料，再调用 \`place_item_native\`。
- 最后逐项重新观察已完成目标。

已有可复用设施：
- \`BuildingBlueprint\`、\`BuildDiff\`、\`NativePlacementSurvey\`。
- \`ActorInventory\` 的 Token 化物品持用。
- \`NativeActionTask\` / ChildExecutor。
- \`ServerAiNavigation\`、\`NativeNavigationWorld\`、落脚区域查询。
- 原生 \`Player.ItemCheck()\` 和放置结果证据。
- 当前任务取消、超时、输入释放和身份绑定机制。

## 3. Design Decisions

### 3.1 不增加模型工具

继续使用：
- \`build_diff(blueprint)\`
- \`build_place(blueprint)\`

V2 内部增加 Survey、Dependency、Worksite、Scheduler、Ledger 和 Recovery 逻辑。避免让模型手动调用内部施工阶段。

### 3.2 新增内部施工模型

根据现有代码风格实现必要的数据结构，至少表达：

- \`BuildTargetState\`：稳定 ID、Blueprint Target、Survey 结果、状态、依赖和材料。
- \`BuildDependency\`：先决目标与被依赖目标、依赖来源。
- \`BuildWorksite\`：安全落脚区域、覆盖目标、移动成本和候选证据。
- \`BuildBatch\`：站点内可连续完成的一组目标及材料类型。
- \`BuildMetrics\`：阶段耗时、导航、站点、材料切换和放置统计。

这些是内部实现概念，不要求逐个创建同名文件。

### 3.3 Survey 与依赖图

任务启动时对 Blueprint 做一次初始 Survey，构建目标状态与依赖关系。

- 已匹配目标标记完成。
- NativePlacementSurvey 返回的缺支撑/Anchor 信息用于构建依赖。
- 只有有明确原生依据或 Blueprint 施工顺序依据时才建立依赖。
- 依赖目标已经匹配则直接解除。
- Blueprint 内可满足的依赖目标未完成则 deferred。
- Blueprint 外缺失的支撑返回明确 outstanding。
- 检测循环依赖，避免无限 deferred。

施工过程中只在以下情况触发局部或完整 Survey：
- 原生动作结果包含不确定副作用。
- 目标占地或支撑发生变化。
- 当前站点可施工目标全部处理完。
- 导航失败或施工位置失效。
- 外部世界变化被检测到。
- 准备从 partial 恢复。

### 3.4 Worksite Selection

基于现有导航返回的安全落脚区域选择站点。

每个站点评估：
- 路径是否完整且可达。
- 玩家是否稳定落脚。
- 可覆盖的 ready 目标数量。
- 放置距离是否满足 \`ActorRope.InReach\` / 当前放置范围规则。
- 材料类型切换成本。
- 到达站点的预计移动成本。
- 当前结构是否可能遮挡目标。

首版使用有界贪心：
1. 优先当前玩家位置可覆盖的站点。
2. 否则选择移动成本较低且能覆盖较多 ready 目标的站点。
3. 站点内先执行会解除更多依赖的目标。
4. 再按材料分组和目标空间邻近度排序。
5. 当前站点没有可施工目标后才切换站点。

导航搜索预算耗尽、未知地形和真正不可达必须分别表达；不得把 timeout 当成 unreachable。

复用现有导航任务，不在 Building 中复制寻路和运动模型。若当前导航只提供水平目标而无法返回可复用的落脚候选，应在现有接口上增加最小的只读站点候选查询，或明确将该目标标记为当前导航能力不支持。

### 3.5 Dependency-aware Ordering

每轮从所有依赖已满足且 Survey 可尝试的目标中生成候选。

排序优先级：
1. 能解除最多后续依赖的目标。
2. 当前站点可达且材料已持有的目标。
3. 同一材料批次内空间相近的目标。
4. 对象和装饰层目标在其支撑结构完成后施工。

不得仅按 layer 和 Y 坐标作为唯一排序规则。保留 Blueprint 输入顺序作为稳定 tie-breaker，保证同一世界状态下计划确定。

### 3.6 Material Ledger

修正并统一材料统计语义：

- \`required\`：Blueprint 总需求。
- \`carriedUsable\`：实际可由当前原生持用操作选中的槽位。
- \`carriedUnavailable\`：背包其他槽位中存在但不能直接持用的数量。
- \`storedNearby\`：可选的已观察容器材料，仅作为潜在来源。
- \`reserved\`、\`consumed\`、\`missing\`。

同材料批次连续施工。材料切换仅在当前批次没有可继续目标时发生。

容器材料只读显示来源，不自动转移。需要容器取物时由 Agent 决策调用现有容器 API。

### 3.7 Execution and Recovery

- 每个目标一次任务内最多发起一次原生放置尝试。
- 每次成功后记录原生证据并更新依赖。
- 对可继续的失败目标保留具体 reason 和坐标。
- unknown 后先重新 Survey，不自动重放。
- 取消、超时保留已完成成果。
- 同一 Blueprint 重交时重新观察世界并跳过 matched 目标。
- 施工结束对所有 placed/matched 目标进行最终状态复核。
- 只有全部目标最终匹配才返回 completed。

### 3.8 Result and Metrics

保留现有 \`entries\`、\`counts\`、\`completedByLayer\` 字段兼容性，增加：

\`\`\`json
{
  "scheduler": {
    "phase": "work",
    "worksitesPlanned": 3,
    "worksitesVisited": 2,
    "navigationCalls": 1,
    "materialSwitches": 2,
    "surveyPasses": 2,
    "nativePlacementAttempts": 18,
    "nativePlacementSuccesses": 17,
    "deferredResolved": 3
  },
  "timing": {
    "surveyMs": 0,
    "planningMs": 0,
    "travelMs": 0,
    "workMs": 0,
    "reconcileMs": 0,
    "totalMs": 0
  }
}
\`\`\`

示例数值仅用于说明结构。实际字段名可与现有任务结果约定协调。避免把每格重复日志放入最终模型回执。

## 4. Implementation Steps

1. 核对最新分支与当前 V1 代码，确认 NativePlacementSurvey 后的实际调用链。
2. 实现内部 BuildTargetState、依赖图和材料账本。
3. 将 BuildPlaceTask 的固定 Queue 改为 Scheduler 驱动；先保证行为正确，再增加站点优化。
4. 接入现有导航的安全落脚区域候选与移动任务，建立 Worksite 选择。
5. 实现站点内目标排序和材料分组，减少重复导航与持用切换。
6. 加入局部 Survey、外部变化处理和可恢复结果。
7. 增加施工统计和精简回执。
8. 更新现有文档和测试。

建议先保持改动集中在 BuildPlaceTask 及新增内部调度模块；只有导航接口确实缺少站点查询能力时才修改导航公共接口。

## 5. Testing

### A. Scheduler Unit / Contract Tests

- 基础结构先于依赖家具。
- 多格家具作为单个目标，依赖与占地正确。
- 依赖解除后 deferred 目标进入候选。
- 循环依赖明确失败，不无限重试。
- 同站点覆盖多个目标时只执行一次站点移动。
- 同材料目标优先连续施工。
- 站点移动成本和覆盖数量影响选择。
- timeout / unknown 不会被当作不可达。
- 外部变化只触发必要的重新调查。
- unknown 动作不重复执行。
- 重交同一 Blueprint 跳过已匹配目标。
- 取消和超时保留已完成结果。

### B. Dedicated Server Native Tests

复用现有隔离专服 Fixture，覆盖：
- 8×8 住宅与多种家具。
- 不同高度的 Tile、Wall、Object。
- 需要多个施工站点的长条/宽区域 Blueprint。
- 多种材料交错的 Blueprint。
- 缺料、缺支撑、可替换植物和不可替换障碍。
- 施工中外部放置/移除 Tile。
- 中途取消、重启后同 Blueprint 续建。
- 最终原生 Housing 检查通过。

所有实际放置继续运行 Native Placement Survey 与 Player.ItemCheck 核验。

### C. V1 vs V2 Performance Comparison

使用相同 Blueprint、世界 Fixture、起始玩家位置、背包材料和导航状态比较：
- 站点访问次数。
- 导航调用次数。
- 材料切换次数。
- 原生放置尝试数。
- Survey 次数。
- 任务总 Tick / elapsed time。
- 完成率和 unknown 数量。

性能优化不能通过跳过原生核验或减少必要的最终检查实现。

### D. Regression

- Native Placement Survey parity tests。
- BuildDiff / BuildPlace 现有测试。
- Housing / NPC Home 测试。
- Inventory hold token 测试。
- Navigation / landing region 测试。
- Python tool/result contract tests。
- 目标版本构建和 \`git diff --check\`。

本任务不要求真实模型 Host & Play；该测试留给 V2 完成后的单独验收。

## 6. Deliverables

1. 完整 Building V2 调度实现。
2. 更新的 BuildPlaceTask、内部调度/依赖/站点/材料结构。
3. Dedicated Server 和 Python 定向测试。
4. V1/V2 对比统计。
5. \`docs/BUILDING_V2_DESIGN.md\` 与本 Implementation Spec。
6. Git Commit，列出实际测试命令、结果和未执行项。

## 7. Acceptance Criteria

- 施工计划显式考虑依赖，而不是固定按 Tile/Wall/Object 和 Y 排序。
- 能选择站点并在一个站点完成多个可达目标。
- 导航调用和材料切换有可验证的统计。
- 外部变化、缺料、取消和 unknown 有正确恢复语义。
- 原生放置行为与 V1 保持一致。
- 现有建筑、放置、住宅和导航回归通过。
- 提供同一 Fixture 下 V1/V2 的可比较结果。
