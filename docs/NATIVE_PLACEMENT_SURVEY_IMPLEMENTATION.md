# 原生放置预检

Native Placement Survey 为建筑差异调查和实际放置提供共享的原生规则判断。它将目标 Tile/Object 的占地、支撑、液体、碰撞和可替换内容整理为结构化结果，供 BuildDiff 与施工调度使用。

## 调查对象

当前放置目标分为三类：

- **单格 Tile**：检查目标格、支撑、原生放置范围和可替换内容。
- **Wall**：按墙体规则检查目标格与材料。
- **多格 Object**：根据 TileObjectData 的宽高、Origin、Alternate、占地和 Anchor 规则调查完整结构。

## 原生规则

Survey 使用目标对象的注册定义及 Terraria 原生放置规则判断候选，包括：

- Tile 类型及 `tileCut`、`BreakableWhenPlacing` 等可清除占位。
- TileObjectData 的尺寸、Origin、Alternate 和 Anchor。
- 对象的 `LiquidPlace` 条件。
- 占地与 AI Player、其他活跃 Player 的碰撞。
- Tile/Wall 类型、世界边界和原生放置范围。

单格 Tile、多格 Object 和 Wall 使用各自的规则路径。Survey 的结果由目标类型、原生定义和当前世界状态共同决定。

## 与执行器的关系

BuildDiff 使用 Survey 解释 Blueprint 当前差异和可施工状态。BuildPlaceTask 根据 Survey 结果安排依赖和工作位置。

实际放置通过绑定 Actor 的原生物品使用路径执行。执行后读取世界占地、Frame、支撑及物品消耗，更新施工目标状态。

## 实现入口

- Blueprint 目标解析：`CompanionBridge/ServerIpc/BuildingBlueprint.cs`
- 差异调查：`CompanionBridge/ServerIpc/BuildDiff.cs`
- 施工调度：`CompanionBridge/ServerIpc/BuildPlaceTask.cs`
- 工作站点：`CompanionBridge/ServerIpc/BuildWorksiteSearch.cs`
- 原生放置适配：`CompanionBridge/ServerPlayerRuntime/ActorObjectPlacement.cs` 及相关 Actor Placement 实现
- Python Schema：`python/atomic_operations.py`

建筑施工流程见 [Building V2](BUILDING_V2_DESIGN.md)，单项物体放置见 [Server AI 多格物体放置](SERVER_AI_OBJECT_PLACEMENT.md)。目标版本的原生 IL 对照和历史验证数据见 [原生机制一致性审计报告](NATIVE_MECHANISM_PARITY_AUDIT_REPORT.md)。
