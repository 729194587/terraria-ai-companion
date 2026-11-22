# 局部语义世界查询

`sense_world(options)` 是 Lua Worker 提供的只读组合 helper。它把地形观察、内容解析、Tile 扫描、单格检查和掉落物扫描组合成一份局部资源报告。

该 helper 不保存跨程序地图，也不生成跨调用稳定的对象 ID。它的可用性取决于当前连接是否开放所需的底层 API：`observe_terrain`、`scan_entities`、`scan_tiles`、`resolve_content` 和 `inspect_tile`。

## 参数

| 参数 | 默认值 | 说明 |
| --- | --- | --- |
| `trees` | true | 查询树木组件 |
| `ores` | true | 查询矿物 |
| `drops` | true | 查询掉落物 |
| `maxObjects` | 8 | 树木/矿物候选及掉落物返回上限，1..16 |
| `radius` | 640 | 掉落物扫描半径，1..1600 世界像素 |
| `oreNames` | 全部支持矿物 | 非空、无重复的矿物名称列表 |
| `dropTypes` | 不过滤 | 最多 32 个 Item 类型 ID |

当前支持的矿物名称为 Copper、Tin、Iron、Lead、Silver、Tungsten、Gold、Platinum、Demonite、Crimtane、Meteorite、Hellstone、Cobalt、Palladium、Mythril、Orichalcum、Adamantite、Titanium 和 Chlorophyte。

## 查询流程

1. 读取当前地形范围及 Server AI 身份。
2. 使用 `resolve_content` 解析树木和矿物内容键。
3. 在本次地形范围内扫描最多 256 个 Tile 候选。
4. 对候选调用 `inspect_tile`，核对目标格当前内容。
5. 如启用掉落物查询，扫描指定半径内的实体。

返回报告分别保留各来源的 gameUpdate、时间、范围、完整性、截断和未知计数。多次底层查询各自拥有采样时间，报告不会将它们合并成同一帧快照。

## 结果结构

- **树木**：按同一 X 列的连续树干 Tile 聚合为组件。组件不代表完整树冠或整棵树。
- **矿物**：按同类型 Tile 的四邻接连通分量聚合。complete 表示本次扫描范围内的连通性完整，不表示可达或可挖。
- **候选状态**：独立检查后标记为 `observed_present` 或 `changed_or_unknown`。
- **掉落物**：返回类型、堆叠、位置、速度和距离。实体槽位可能复用，拾取结果需结合背包变化重新观察。

## 使用方式

```lua
local world = sense_world({
    trees = false,
    ores = true,
    drops = true,
    oreNames = {'Iron', 'Lead'},
    radius = 800,
    maxObjects = 8
})

for _, ore in ipairs(world.objects) do
    if ore.kind == 'ore_component' and ore.contentKey == 'Terraria/Iron' then
        -- 根据组件的候选信息选择后续观察或采集目标
    end
end
return world
```

该 helper 负责发现与整理候选，不负责全局寻矿、路径规划、工具选择、移动、挖掘或拾取。相关行动由导航、任务和原子 API 完成。

实现：`python/lua/semantic.lua`、`python/lua_worker.py`、`python/server_ai_contract.py`。

## 物品信息

先通过 `resolve_content({kind="item", query=...})` 取得当前加载环境的 `candidates[].typeId`，再调用 `api("query_item_info", {typeId=id})`。成功时 `data` 包含本地化名称、内容键、Mod 内部名称、基础物品样本属性及普通描述行数组 `tooltip`。价值以铜币计，斧力已换算为游戏内百分比。未知的正整数 ID 返回 `available=false` 和 `reason="unknown_item_type"`。

实现从运行时本地化表读取描述。实现 `IHoldShiftTooltipItem` 的物品还会读取普通提示、风味文本和独立的多行 `extraTooltip`；没有扩展文本时省略该字段。查询不执行客户端悬停绘制或玩家状态相关的 UI 钩子。客户端支持已加载的内容 Mod。Server AI 仍受 G1 的 Mod 加载隔离条件限制。
