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

具备 `server_ai_directional_landings_v1` 时，单独请求 `landingRegions` 可传 `landingDirection`。left/right 将当前窗口朝前扩展 16 格至 61×29，up/down 朝前扩展 24 格至 45×53。实际范围见 bounds。

具备 `server_ai_passages_v1` 时，本地读取 `landingRegions` 可同时传 `includePassages=true`。标准尺寸角色的 `data.passages` 描述与脚下相连的已采样空间。采样在角色能容身的两列三行干燥空间中寻找连通分支，并与落脚候选相交。角色外侧出现相连的有顶通道时返回 `possible_cave_entrance`；室内和岩石悬挑也可能有这种形状。通道的走跳可达性由 Navigation 执行时确认。

| 字段 | 用途 |
| --- | --- |
| `exits[].target`、`kind`、`direction` | 各支路中观测到的落脚点、地形线索和相对方向。 |
| `descentStep` | 从当前高度沿已知开口可下降到的较近落点。进入洞道时可先到这里，再观察下一段。 |
| `openShaft` | 通到观察窗底部、尚未见底的开口位置。这个位置只供识别井口。 |
| `junction`、`returnTarget` | 具有多个分支的局部选择点，以及角色本次有支撑的脚下位置。折返前应重查地形。 |
| `routes[]` | 可供比较的行进方向、落点与地形线索。即使一整片平地只有一个连通区域，也会分别报告两侧的候选落点（若存在）。 |

`routes[].visitState` 来自当前世界和角色实际采样到的脚下格。每次移动到另一个格子就记录一次，保留最近 4096 次变化。目标周围一格内出现过足迹时返回 `walked_nearby`，否则为 `no_recent_visit`。世界重新加载会清空足迹，瞬移不会补画中途经过的路线。`unknownNearbyTiles` 单独统计落点周围 9×9 格中仍未在 AiMap 揭示的格数，范围裁至世界边界。地图不可用时这两项分别为 `unknown` 和 `null`。

以上点的 `x/y` 都是世界像素，Y 向下。连通检查保守处理未知格、危险和狭窄通道；`originResolved=false` 表示未能从角色身位建立这次观察。局部采样不证明全世界或视窗外的地形。

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
