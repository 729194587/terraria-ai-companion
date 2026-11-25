# Server AI 场景观察

`observe_scene` 为独立 Server AI Player 提供结构化的局部与区域世界观察。采样在 Terraria 游戏线程完成，结果包含坐标原点、采样更新、Actor 身份、覆盖范围和各数据层的完整性。

## L0：局部场景

L0 默认范围为角色附近 45×29 Tile，schemaVersion 为 2。可按需选择数据层：

| 层 | 内容 |
| --- | --- |
| `map` | 面向模型阅读的字符地图与语义标记 |
| `geometry` | Tile 碰撞形状、液体和地形知识 |
| `objects` | 矿物、树木组件、家具、掉落物等对象 |
| `hazards` | 液体、接触危险和动态实体 |
| `landingRegions` | 导航落脚区域候选 |
| `placementCandidates` | 单格 Tile 放置候选 |
| `objectPlacementCandidates` | 多格物体的锚点、占地和支撑候选 |

通过 `layers` 请求所需层。响应中的 `includedLayers` 表示实际返回内容；集合各自包含计数、完整性和截断信息。

## L1：区域场景

具备 `server_ai_scene_region_v1` 能力时，可请求更大范围：

```lua
local region = api('observe_scene', {
    scale = 'region',
    width = 120,
    height = 68,
    layers = {'geometry', 'objects', 'hazards'}
})
```

宽度范围为 45..128 Tile，高度为 29..96 Tile，最多 8192 Tile。L1 使用 schemaVersion 3，默认返回 geometry、objects 和 hazards，也可请求 map。有 `server_ai_region_resources_v1` 时可单独请求 resources，读取同一游戏更新内的 Tile 与掉落物。资源层的 bounds、scannedTiles、unknownCells、truncated 和场景 sample.elapsedMilliseconds 共同说明本次覆盖；数量截断、聚类截断或未采样均不能证明对象消失。。

L1 面向区域理解与资源定位，不生成落脚、放置或可执行目标令牌。需要执行动作时，Agent 使用当前局部观察和相应动作 API。

## 坐标与编码

`map` 与 `geometry` 共用 `originX/originY)。行列索引从 1 开始，Tile 坐标为：

```text
tileX = originX + column - 1
tileY = originY + row - 1
```

L1 网格使用 encodingVersion=2 字典编码，响应包含 palette 和 indexDigits。行数据可使用 RLE 或定长索引编码，需按对应字段解码。

知识状态区分已读取、未知、未加载和未采样。对象聚类、危险列表和逐格网格分别维护覆盖及截断信息。

## 对象与候选

对象记录描述本次观察识别到的类别、内容键、范围和必要的 Tile 详情。树木等多 Tile 结构可能由多个组件表示。

L0 中符合任务条件的矿物对象可以附带 `target`，供 `mine_target` 使用。落脚候选用于二维导航；单格放置候选和多格物体候选对应不同操作。

候选是采样时的预检结果。实际执行时会重新读取 Tile、材料、Actor 身份、原生交互范围及 Terraria 放置和碰撞条件。

## 动态实体与危险

`hazards.entities` 可包含 NPC 和弹幕的包围盒、原生速度、采样 Tick 及短时威胁估计。当前通用威胁估计采用有限时间窗内的相对运动包围盒；Boss 战斗有独立的弹幕与威胁预测器。

## 角色中心光照

`lighting` 提供角色中心 Tile 的亮度估计。服务器使用原生 TileLightScanner 和局部 LightMap 计算，结果包含 RGB 最大分量及采样更新号。该结果不包含手持光源、弹幕动态光源和客户端天空特效。

## 与地图记忆的关系

`observe_scene` 读取当前局部世界；`query_map` 读取持久化探索记忆。前者适合即时动作决策，后者用于战略定位和回顾已探索区域。两者的用途与数据时效不同。

## 实现入口

- 场景采样：`CompanionBridge/ServerIpc/ServerSceneReader.cs`
- 场景数据与编码：`CompanionBridge/ServerIpc/` 下 Scene、Geometry 相关实现
- Schema 与描述：`python/atomic_operations.py`
- 能力投影：`python/server_ai.py`、`python/server_ai_contract.py`
