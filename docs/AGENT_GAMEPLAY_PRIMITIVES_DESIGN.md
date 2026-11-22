# Agent Gameplay Primitives：掉落物收集与通用放置

**状态：** P0–P3 容器与库存能力已实现；本文定义 P4  
**目标：** 让 Agent 能主动收集世界掉落物，并通过统一入口放置 Terraria 中可放置的物品  
**执行环境：** Dedicated Server 上绑定的 Server AI Player

## 1. 当前基础

项目已经具备 P4 所需的大部分底层能力：

- `scan_entities` 可以观察世界中的活动掉落物。
- `NativeSurvival` 已接入原生 `Player.GrabItems`。AI Player 的游戏更新会执行 Terraria 拾取逻辑，并同步背包和掉落实例变化。此前挖矿测试中，Agent 已自动拾取铁矿石 ×2。
- 现有导航任务支持异步提交、查询、等待和取消。
- `ActorItemUse` 管理原生 `Player.ItemCheck` 使用周期。
- `ActorPlacement` 已通过原生 `Player.PlaceThing_Tiles` 放置普通 Tile，并观察 `WorldGen.PlaceTile` 等结果。
- `ActorObjectPlacement` 已读取 `TileObjectData`，处理多格占地、锚点和支撑；当前对外的 `place_object` 仍限于工作台。

P4 主要工作是把这些游戏侧能力整理成 Agent 能直接表达的操作意图。

## 2. P4-A：主动收集掉落物 `collect_drops`

### 目标

提供一个异步收集任务。Agent 可以要求收集附近掉落物，不必提前知道掉落物种类，也可以根据观察结果指定目标或筛选类型。

建议接口：

```lua
submit_task({
    operation = "collect_drops",
    arguments = {
        radiusPixels = 320,
        -- typeIds 可选；省略时收集范围内所有可拾取掉落物
    }
})
```

参数以现有 Task Schema 为准；默认范围应适合一次短程收集，并允许 Agent 按需扩大范围。

### 执行流程

1. **观察目标**：扫描任务范围内的活动掉落物，读取物品类型、数量和位置。
2. **选择目标**：优先处理附近且容易抵达的掉落物；没有类型筛选时，物品种类不影响收集资格。
3. **计算拾取站位**：根据 Terraria 原生拾取距离和玩家碰撞范围，寻找能够触发拾取的可达位置。导航目标是可拾取区域，不是掉落物所在 Tile。
4. **执行导航**：复用现有导航任务移动到目标区域。
5. **原生拾取**：角色进入范围后，由游戏更新中的 `Player.GrabItems` 完成吸附、拾取、堆叠和背包处理。
6. **刷新观察**：确认背包、掉落实例和角色状态的变化，再处理剩余目标。

### 目标更新与结果

掉落物会移动、合并、消失，也可能被其他玩家拾取。每次接近目标前应刷新其状态；目标变化时按最新位置继续收集。

任务结果应包含：

- 已收集物品及数量，按物品类型汇总。
- 仍留在世界中的目标及其当前位置。
- 未完成原因，例如导航超时、目标不可达、背包空间不足或原生拾取条件不满足。
- 任务整体状态及已确认的部分效果。

默认在本轮观察到的目标处理完毕、没有可继续处理的目标或任务结束条件触发时完成。任务不持续追逐执行期间无限生成的新掉落物。取消时沿用现有任务输入释放和结果语义。

### 验收

- 不指定物品类型即可收集附近不同种类的掉落物。
- 可以按观察结果指定目标，或通过类型筛选收集。
- 角色能从掉落物附近的可达站位触发原生拾取，不要求站在物品坐标上。
- 验证物品移动、吸附、合并、消失、背包已满、部分成功和导航超时。
- Dedicated Server 上至少两个客户端在线，确认掉落物和背包同步正确。

## 3. P4-B：通用物品放置 `place_item`

### 目标

提供统一的 `place_item` 入口，让 Agent 使用当前选中的快捷栏物品，在指定世界 Tile 坐标执行原生放置。

```lua
submit_task({
    operation = "place_item",
    arguments = {
        x = 2100,
        y = 180
    }
})
```

Agent 只表达放置位置。游戏侧根据物品的原生放置属性和 Terraria/tModLoader 调用路径，选择正确的放置行为。

### 放置类型

| 类型 | 游戏侧处理 |
| --- | --- |
| 普通方块、平台 | 扩展现有 `ActorPlacement`，使用原生 Tile 放置路径 |
| 多格家具、工作站 | 使用 `TileObjectData` 解析占地、锚点、方向和支撑，并核验 `TileObject.Place` 结果 |
| 背景墙 | 接入原生墙壁放置分支 |
| 火把及常见装饰物 | 使用对应的原生物品使用与放置分支 |
| 箱子 | 完成 Tile 放置及 Chest 存储记录注册 |
| 绳索等已有专用放置行为 | 复用现有放置能力，并纳入统一入口 |
| Mod 物品 | 按其 tModLoader 原生放置属性和 Hook 执行 |

识别依据包括物品的 `createTile`、`createWall`、`placeStyle`、`TileObjectData` 以及原生 `Player.ItemCheck` 使用路径。对于使用自定义 Hook 的 Mod 物品，沿其原生行为接入。

### 执行流程

1. 读取当前选中的快捷栏物品及目标坐标。
2. 识别物品对应的放置类型，计算目标锚点、占地和必要的支撑信息。
3. 检查 Terraria 原生放置条件，包括交互范围、占地、支撑和玩家碰撞。
4. 复用 `ActorItemUse` 的原生物品使用周期，进入对应放置分支。
5. 通过原生放置方法及事件完成世界修改。
6. 核验放置结果、物品消耗和附属世界数据，并同步给客户端。

多格物体的回执应包含实际锚点和占地范围。放置失败时返回原生拒绝原因；世界状态已经变化但结果无法确认时，沿用现有 `unknown` 语义。

### 箱子验收重点

箱子放置完成后，服务端应存在与 Tile 锚点对应的有效 Chest 记录。随后使用现有容器能力验证：

- 能够打开并观察箱子。
- 能够存入和取出物品。
- 箱子内容及放置状态能正确同步到其他客户端。

### 验收

优先覆盖以下样例：

- 普通方块、平台、背景墙和火把。
- 工作台、熔炉等多格家具。
- 原版箱子及后续开箱、存取闭环。
- 至少一种使用标准原生放置机制的 Mod 物品。

每种样例验证放置位置、占地/锚点、物品消耗、原生 Hook 行为和客户端同步，并回归现有 `place_tile`、`place_object`、`place_rope`。

## 4. 实施顺序

P4-A 和 P4-B 相互独立，建议分开实现和验收：

1. **P4-A `collect_drops`**：先复用现有实体观察、导航和 `Player.GrabItems`，补齐主动收集任务。
2. **P4-B `place_item`**：再扩展现有物品使用和放置链路，形成统一放置入口。

两项能力都沿用现有 Server AI 能力握手、参数 Schema、异步任务生命周期和结果契约。

## 5. 参考项目

- **Numen**：`ItemReach` 和 `Destination` 对拾取可达区域的建模；`BuildPlacer` 和 `UseApi` 对原生物品使用与放置流程的组织。
- **TerraBot**：`BotPickupPolicy`、`BotPickupRegression` 对拾取归属、兼容性和测试的处理；`BotWorldActions` 对箱子创建结果的观察。

参考项目用于启发任务建模、原生行为复用和测试设计。具体拾取距离、碰撞、放置规则及服务端调用方式，以当前 Terraria/tModLoader 版本为准。

## 6. 相关实现与文档

- `CompanionBridge/ServerPlayerRuntime/NativeSurvival.cs`
- `CompanionBridge/ServerPlayerRuntime/ActorItemUse.cs`
- `CompanionBridge/ServerPlayerRuntime/ActorPlacement.cs`
- `CompanionBridge/ServerPlayerRuntime/ActorObjectPlacement.cs`
- [背包操作](INVENTORY_OPERATIONS.md)
- [Server AI 动作接口](SERVER_AI_ACTIONS.md)
- [Server AI 能力契约](SERVER_AI_CAPABILITIES.md)
