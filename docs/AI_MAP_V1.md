# Server AI 地图记忆

`query_map` 读取独立 Server AI Player 在世界中逐步积累的探索地图。地图用于区域定位和战略规划，保存已知地形的近似状态。

## 数据模型

地图按 World UUID 和 AI Character ID 绑定，并随世界角色档案保存。每个 Tile 使用一个字节记录：

| 值 | 含义 |
| ---: | --- |
| 0 | 未知 |
| 1 | 已知非阻挡 |
| 2 | 实心 |
| 3 | 平台 |
| 4 | 水 |
| 5 | 岩浆 |
| 6 | 蜂蜜 |
| 7 | 微光 |

地图不保存墙、家具、Tile Frame 或逐格采样时间，因此它是探索记忆，不是完整世界副本。

v2 存档兼容读取 v1。每个已观测的 32×32 Tile 块含内容版本、最后观测会话及游戏更新号。重复观察相同内容只刷新观测记录。旧存档加载时重建分类计数，观测年龄未知。块内任何格被看见都可刷新块时间，具体落点与支撑仍需现场复核。

## 地图揭示

`AiMapRuntime` 在 AI Player 存活且处于有效运行状态时更新地图，默认每 15 个游戏更新采样一次。采样区域以角色中心为基准，宽 120 Tile、高 68 Tile。

当前可见性估计使用格线视线和简化照明规则：

- 以中间实心 Tile 作为遮挡。
- 角色附近 4 Tile 范围内可直接揭示。
- 白天符合地表开口条件的区域可按日光揭示。
- 已识别的原版火把在无遮挡的 14 Tile 范围内提供近似照明。

采样只记录实际检查的格子，不沿移动或传送路径插值。世界中的其他角色或 Mod 修改会在后续采样时更新。

## 查询

地图能力通过 `server_ai_map_v1` 开放，支持两种模式：

- `overview`：将整张世界地图聚合为统一分辨率，提供宏观概览。
- `region`：读取指定或角色附近的区域，可设置宽、高和 cellSize。使用 `cellSize=1` 可查看单 Tile 状态。

服务端公布 `server_ai_map_chunks_v1` 时，`region` 可选传 `includeChunks=true`，取得世界尺寸、`mapRevision`、`observationSession` 和相交的已观测块摘要。只有同一观测会话的有效更新号可用于计算年龄。

返回数据包含世界身份、区域原点、聚合尺寸、探索比例和字符网格。字符含义：

| 字符 | 含义 |
| --- | --- |
| `?` | 未知 |
| 空格 | 已知非阻挡 |
| `#` | 实心 |
| `=` | 平台 |
| `~` | 水 |
| `L` | 岩浆 |
| `H` | 蜂蜜 |
| `S` | 微光 |
| `:` | 混合或部分已知 |

聚合单元中的液体符号表示该单元内存在对应液体，不表示整个单元均被液体覆盖。

## 与实时观察的关系

地图查询仅读取已保存的地图数据，不访问 Terraria 当前 Tile 来补全未知区域。需要精确行动信息时，使用 [Server AI 场景观察](LOCAL_SEMANTIC_SCENE.md) 读取当前世界。

## 实现入口

- `CompanionBridge/ServerPlayerRuntime/AiMap.cs`
- `CompanionBridge/ServerPlayerRuntime/AiMapRuntime.cs`
- `CompanionBridge/ServerPlayerRuntime/WorldCharacterPersistence.cs`
- `CompanionBridge/ServerIpc/ServerAiQueries.cs`
