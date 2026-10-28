# L1：局部语义世界感知

## 实现与使用

`python/lua/semantic.lua` 随 Lua Worker 装载为 `sense_world(options)`，模型契约仅在服务器后端同时支持五项既有感知 API 时公布。它不是新增原子动作，也不维护跨调用地图。

```lua
return sense_world({maxObjects=8, radius=640})
```

`maxObjects` 为 1..16（默认 8），分别限制 Tile 对象和掉落物数量；`radius` 为 1..1600 世界像素（默认 640），只影响掉落物。Tile 范围来自 `observe_terrain.data.bounds`。默认最多 31 次 API，16 对象最多 39 次；所有查询和候选 inspect 都计入原 Runtime 的调用/时间预算，复用取消、失败屏障、绑定身份检查与证据归档。实际执行的 helper 源码及 SHA-256 进入证据。

流程：一次地形范围读取 → 精确解析 Trees 和 19 种原版矿物注册表键 → 一次最多 256 格的类型扫描 → 聚合 → 对最多 maxObjects 个候选重新 inspect → 一次掉落物扫描。解析失败保留 `unresolvedContent`，不猜数字 ID，也不把注册表内容当附近存在。矿物范围从 Copper/Tin 至 Chlorophyte，不含宝石、化石、Mod 矿物。

## 按需筛选（2026-10-28）

优先按目标关闭无关类别，避免每次解析 20 个内容键。保留无参数总览兼容。

```lua
local trees = sense_world({trees=true, ores=false, drops=false, maxObjects=4})
local copper = sense_world({trees=false, oreNames={'Copper'}, drops=false})
local drops = sense_world({trees=false, ores=false, drops=true, radius=320})
```

`trees/ores/drops` 为布尔值，默认 true。`oreNames` 是非空、连续、唯一的短名称列表，最多19项，
仅支持上述原版矿物（如 Copper、Iron），不含 `Terraria/` 前缀；ores=false 时不可传。
`dropTypes` 是从当前观察/注册表取得的物品ID列表（1..32个唯一整数，0..65535），仅 drops=true 时可传；
转交原 scan_entities.typeIds，在排序/截断之前过滤，无需重新解析所有物品。省略表示所有掉落。
参数在读取前校验；不引入跨次注册表缓存。`requested` 明确记录查询类别与筛选；关闭类别的空数组表示未查询，不能解释成不存在。

只找树最多 `3+maxObjects` 次调用；只找 K 种矿物最多 `2+K+maxObjects` 次；只找掉落固定2次（范围/身份观察与实体扫描）。
类别混合按实际内容键数累加，scan_tiles与inspect只执行一次组合流程；候选不足时更少。
每次仍读取真实范围、身份和时间，sources 保留原有未知、截断与时效字段；radius仍只作用于掉落。

## 事实、推断与覆盖

- `identity` 保留 world、generation、slot 和完整 binding；任何后续读取身份变化立即终止，不能发布混合身份目标。无自动重连或重放。
- `sources` 保存来源索引、更新号、UTC 时间、范围、扫描 complete/truncated/unknownCount、计数或掉落半径/原点。不同来源不是同帧快照；候选 inspect 有自己的 source。
- `objects` 是推断结果，`observedTileCount` 是实际返回格数。对象上限省略计入 `omittedObjects`，扫描自身的省略仍看 sources.truncated。输出不复制地形 rows、完整 TileCell、背包空槽或重复 contentKey。
- 普通 `Terraria/Trees` 按同 X 连续 Y 聚合为 `tree_trunk`，bounds 是无缺口的竖列，可精确重建树干格。选择观察到的最底格作为候选。侧向枝根可能属于邻树，保留 `ambiguous_lateral_tiles`，不跨列强行合并。孤立枝根也可能形成短片段，**对象数不是树的棵数**。所有树结果 `complete=false`：这是树干片段，不承诺树冠/根系/整树完整性；不支持棕榈树、宝石树或复杂相邻树的完整归属恢复。
- 矿脉按相同真实 Tile 类型的四邻接连通分量聚合，斜角不连、不同矿种不合并。每个对象保留 typeId/contentKey 和精确 `[x,y]` 坐标数组。仅当扫描无截断、无未知、完整且该分量不触及边界时，`complete=true`；含义仅是该次请求内该同类型四邻接分量封闭，不是可达/安全/可挖证明。
- candidate 保留实际 Tile 坐标、独立 inspect 来源、当前是否仍同类型存在、保守距离检查。`changed_or_unknown` 不再宣称完整；`observed_present` 仍不是可操作承诺，检查与实际动作之间还会变化。已有 `mine_tile(x,y)` 最终检查工具、距离、保护、Actor 归属与效果。helper 本身不选工具、不移动、不挖掘。
- 掉落物保留本次槽位、类型、堆叠、世界像素中心、速度、距离；不承诺持续实例 ID。导航提示是中心点；`navigate_to_x` 要用当前 Player 宽度转换为左上角 X，且须核对 Y 与地形，不保证同一 X 可以拾取。移动后重新扫描，库存按同类物品计数比较；掉落消失不证明 AI 拾取，同期其他来源或竞争也会影响归属。
- 无视线/遮挡检测：结果是服务器已读 Tile/活跃实体，不表述为玩家可见或可触及。未知与截断不会被解释为空，树也不因局部没有更多格而被升级为完整对象。

## 定向自动化与隔离原生验证

未运行全量测试。命令从仓库根执行：

```powershell
$env:PYTHONPATH="$pwd/python;$pwd/tests"
& .local/lua-venv/Scripts/python.exe -B -m unittest test_semantic_world test_lua_coordinates test_server_ai_backend -q
pwsh -NoProfile -File scripts/build-companion.ps1 -OutputPath .local/semantic-build
pwsh -NoProfile -File tests/verify_server_ai_native.ps1 -Port 27951 -IpcPort 27952 -PackagePath .local/semantic-build/Mods/CompanionBridge.tmod -Atomic -Reads -Actions -Perception -Semantic
```

合成观察驱动真实 Lua 的 12 项测试覆盖连续树干/相邻归属歧义、同类四邻接/异类分离、边界/截断/未知、inspect 后变化、重新读取、Actor/world/generation/slot 拒绝、输出上限、掉落物导航/库存语义和参数预算。相关坐标与服务器后端回归首次 42 项通过，补充 3 项后新增模块 12 项通过，共 45 个不同用例，包含真实 Worker 和 loopback TCP；受限环境首轮 Worker 无输出，清理后在允许进程/管道的环境完成。不是图形或原生几何测试。

新增 `-Semantic` 只允许与既有 `-Actions -Perception` 复制世界 fixture 同时使用。额外诊断开关仅在原有隔离 marker、无客户端、游戏主线程和绑定 Actor 检查内生效。通过原生 `WorldGen.GrowTree` 生成树；矿格与铜斧仅为该复制世界 fixture 注入，不进入生产行为。Python 脚本通过原 Lua/ServerAiBridge 验证两个铜矿分量、掉落物和语义候选砍树，动作后重新聚合。正常完整帧驱动、取消/超时/输入释放及进程清理沿用已有验证。

首次隔离运行实际砍树成功，但脚本误从 endEvidence 读取 targetAbsent，断言失败；正确字段在 evidence，输入释放在 endEvidence。保留原失败，不计为验收通过。

最终隔离原生通过：64 次调用、2 次动作，矿脉分量/掉落/原生砍树后刷新及原有读取、导航、物品使用、挖掘、取消、进程清理均通过。最终隔离结果与包哈希见 [原生证据](evidence/L1_SEMANTIC_NATIVE.json)。图形 Host & Play、真实 LLM 自主找树/采矿/拾取验收尚未执行；未部署至现用 profile。原生请求代次错配拒绝不等于已验证真实死亡重生/世界重载时序，复杂树林的完整对象归属也未宣称实现。
