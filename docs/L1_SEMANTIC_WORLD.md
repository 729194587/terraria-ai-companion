# 局部语义世界查询

`sense_world(options)` 是 Lua Worker 注入的只读组合 helper，不是独立原子 API，不维护跨程序地图或稳定对象 ID。它组合现有的地形观察、内容解析、Tile 扫描、单格检查和掉落物扫描。

只有当前后端同时开放以下 API 时，Server AI 模型契约才会公布该 helper：`observe_terrain`、`scan_entities`、`scan_tiles`、`resolve_content`、`inspect_tile`。客户端与服务器能力不能互相推断。

## 参数

| 参数 | 默认值 | 约束 |
| --- | --- | --- |
| `trees` / `ores` / `drops` | `true` | 分别启用树、矿物、掉落物查询 |
| `maxObjects` | 8 | 1..16；限制返回的树/矿物候选及掉落物数量 |
| `radius` | 640 | 1..1600 世界像素；只影响掉落物扫描 |
| `oreNames` | 全部支持矿物 | 非空唯一列表；仅支持当前 helper 列出的 19 种原版矿物名称 |
| `dropTypes` | 不过滤 | 非空唯一 Item 类型 ID 列表，最多 32 个，范围 0..65535；仅在 drops=true 时有效 |

支持的矿物名称：Copper、Tin、Iron、Lead、Silver、Tungsten、Gold、Platinum、Demonite、Crimtane、Meteorite、Hellstone、Cobalt、Palladium、Mythril、Orichalcum、Adamantite、Titanium、Chlorophyte。参数在执行查询前校验；未知选项、重复项、稀疏列表或与关闭类别冲突的过滤器会拒绝。

## 查询与返回

1. 读取当前 `observe_terrain` 范围和 Server AI 身份。
2. 通过 `resolve_content` 解析请求的 Trees/矿物内容键；解析不唯一或不完整时记入 `unresolvedContent`，不猜测数字 ID。
3. 在本次地形 bounds 内执行一次有界 `scan_tiles`，最多返回 256 格，并聚合候选。
4. 对每个返回的树/矿物候选执行 `inspect_tile`，检查候选格仍然存在且类型匹配。
5. 如启用 drops，执行一次有界 `scan_entities`，返回掉落物类型、堆叠、位置、速度和距离。

报告保留各来源的 `gameUpdate`、时间、范围、完整性、截断和未知计数。不同来源不是同一帧快照；身份或绑定发生变化时 helper 终止，不发布混合身份结果。

## 对象语义

- **树木**：按同一 X 列连续的 Trees Tile 形成树干片段。相邻列归属存在歧义；结果不是整棵树，不含完整树冠/根系保证，`complete` 不会被提升为 true。
- **矿物**：按同类型 Tile 的四邻接连通分量聚合；斜角不连通。只有扫描完整、无截断/未知且分量未触及观察边界时，分量才可能标记为 complete。它只表示本次请求范围内的连通性，不表示可达、安全或可挖。
- **候选**：候选格经独立 inspect 后标记为 `observed_present` 或 `changed_or_unknown`。即使仍存在，也不代表当前工具、距离、保护规则或原生动作一定允许挖掘。
- **掉落物**：位置和扫描槽位只代表本次观察；槽位可能复用，没有跨调用稳定实例 ID。掉落物消失不证明 AI 已拾取，必须重新扫描并结合背包数量变化核验。

## 限制

该 helper 不提供玩家视线/摄像机可见性、全局寻矿、路径可达性、自动选工具、移动、挖掘、拾取或 Mod 矿物识别。观察未覆盖、截断、未知或候选不完整时，不能推断目标不存在。需要执行动作时，使用相应原子 API 并以其最新回执和结束证据为准。

实现：`python/lua/semantic.lua`、`python/lua_worker.py`、`python/server_ai_contract.py`。
