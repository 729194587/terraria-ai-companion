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

v4 存档兼容读取 v1–v3。v3 起使用 Brotli 压缩地形格。每个已观测的 32×32 Tile 块含内容版本、最后观测会话及游戏更新号。重复观察相同内容只刷新观测记录。旧存档加载时重建分类计数，观测年龄未知。块内任何格被看见都可刷新块时间，具体落点与支撑仍需现场复核。

## 地图揭示

`AiMapRuntime` 在角色存活且身份有效时每 15 个游戏更新自动采样。采样与 `look_around()` 共用以角色为中心的 119×67 格视窗和逐格光照结果。调用 `look_around()` 也会记录这次看到的地形；后台采样无需调用模型或生成 PNG。光源范围见[实时地形视野](LOCAL_SEMANTIC_SCENE.md#实时地形视野与角色光照)。

亮格按地图原有的七种地形类别写入。光线消失或角色离开后保留最后所见，重新照亮时才用新观察刷新。角色自己确认完成的挖掘和放置也会更新受影响的格子。原有存档的已知内容继续保留。

v4 在同一角色档案内按 32×32 格稀疏块持久化脚印，每块 128 字节位图。角色存活时按实际采样的脚底中心记格；瞬移只记两端。旧存档初次载入时访问位图为空。近期足迹另保留最近 4096 次换格，重载后从当前位置重新积累。照亮岔路不会生成脚印；`observe_scene` 的 `passages.routes[].visitState` 描述候选落点附近是否有近期脚印，`unknownNearbyTiles` 描述附近还有多少未知地形。

## 查询

地图能力通过 `server_ai_map_v1` 开放，支持两种模式：

- `overview`：将整张世界地图聚合为统一分辨率，提供宏观概览。
- `region`：读取指定或角色附近的区域，可设置宽、高和 cellSize。使用 `cellSize=1` 可查看单 Tile 状态。

服务端公布 `server_ai_map_chunks_v1` 时，`region` 可选传 `includeChunks=true`，取得世界尺寸、`mapRevision`、`observationSession` 和相交的已观测块摘要。只有同一观测会话的有效更新号可用于计算年龄。

服务端公布 `server_ai_exploration_v2` 时，`region` 可传 `includeVisits=true`。返回的 `visitedCount` 是本世界角色实际访问过的不同脚底格总数。`visitBits` 为 Base64 位图，按返回的 Tile 宽高逐行排列，每字节从低位起读，独立于地形聚合参数 `cellSize`。

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
