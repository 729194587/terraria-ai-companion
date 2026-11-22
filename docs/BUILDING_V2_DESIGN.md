# Building V2：施工调度架构

Building V2 将 Blueprint 转换为有序的游戏内施工任务。模型负责设计建筑并提交 Blueprint；游戏侧负责调查现场、安排施工顺序、选择工作位置、准备材料、执行原生放置并汇总结果。

## 系统输入与输出

输入是以世界 Tile 坐标为原点的 Blueprint，包含三个目标层：

- `tiles`：单格前景 Tile。
- `walls`：单格墙体。
- `objects`：由物品及其 TileObjectData 定义的多格物体。

施工器先通过 BuildDiff 调查 Blueprint 与当前世界的差异，再提交 `build_place` 持续任务。结果按目标条目记录 placed、matched、pending、failed 或 unknown 等状态，并提供最终差异与材料统计。

## 施工流程

```text
Blueprint
   ↓
Survey：检查目标、占地、支撑与材料
   ↓
Dependency：整理支撑关系和待施工目标
   ↓
Worksite Search：寻找可覆盖多个目标的安全站位
   ↓
Schedule：选择目标顺序与材料批次
   ↓
Navigate / Hold Item / Native Place
   ↓
Re-survey：核对世界变化并更新剩余工作
   ↺
```

### Survey

BuildDiff 对每个目标检查当前状态、材料、原生放置预检结果及可施工条件。已经匹配的目标直接计入完成状态；需要支撑的目标可关联 Blueprint 中能够提供支撑的其他目标。

NativePlacementSurvey 将 Terraria 原生放置条件用于预检，包含占地、支撑、液体、碰撞、范围和内容定义。Survey 与执行共用原生规则，避免施工计划与实际放置逻辑出现两套判断。

### Dependency 与顺序

施工目标以目标 ID 和空间占地表达。依赖来自放置预检提供的支撑关系及 Blueprint 内目标之间的结构关系。

调度器优先处理能够解除后续目标依赖的项目，并结合目标所在层、可施工状态和当前站位组织顺序。缺少支撑的目标进入 deferred，待支撑完成后重新调查。

### Worksite Search

工作站点是能够稳定落脚并在 Terraria 原生交互范围内覆盖若干待施工目标的区域。候选位置根据覆盖目标数量、材料种类和移动距离排序。

导航规划器负责验证候选站点之间的实际路线。站点搜索本身只产生候选，最终移动由现有导航系统执行。

### Material Ledger

施工过程跟踪目标所需材料、已消耗数量和当前持有物品。库存准备复用 ActorInventory 与现有物品持用任务，材料切换与放置动作分开管理。

### Scheduler 与 Recovery

施工器在当前站点完成可施工批次后，再选择下一站点。放置后重新读取目标占地和支撑状态；现场变化、材料不足、导航失败或部分完成时，根据最新 Survey 更新剩余目标。

取消或部分施工后可以再次提交同一 Blueprint。新任务以当前世界状态重新调查，已经匹配的目标不会重复施工。

## 与 Terraria 原生机制的关系

单格和多格物体均通过当前适配的原生放置路径执行。多格物体的占地、Origin、Alternate、支撑与液体条件来自 TileObjectData 和原生放置预检。

Building V2 不实现独立的 Tile 写入系统。放置结果通过世界差异、物品消耗及原生事件证据确认。

## 主要实现入口

- `CompanionBridge/ServerIpc/BuildingBlueprint.cs`：Blueprint 解析和目标几何。
- `CompanionBridge/ServerIpc/BuildDiff.cs`：现场调查与差异报告。
- `CompanionBridge/ServerIpc/BuildPlaceTask.cs`：施工任务与调度循环。
- `CompanionBridge/ServerIpc/BuildWorksiteSearch.cs`：工作站点候选。
- `CompanionBridge/ServerIpc/ServerAiNativeActions.cs`：施工任务创建和原生动作分发。
- `CompanionBridge/ServerPlayerRuntime/`：Actor Inventory 与原生物品放置。
- `python/lua/modules/building.lua`：Lua 侧蓝图施工组合模块。

## 相关文档

- [Server AI 多格物体放置](SERVER_AI_OBJECT_PLACEMENT.md)：单项放置与蓝图入口。
- [世界交互架构](TERRARIA_WORLD_INTERACTION_DESIGN.md)：住房、NPC 服务与经济交互。
- [施工实现与验证记录](BUILDING_V2_IMPLEMENTATION.md)：阶段实现、对照实验和专服证据。
