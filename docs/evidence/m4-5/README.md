# M4.5 — 多尺度感知与既有 AI Map 的分工

2026-11-04（Asia/Shanghai）。审查基线为 PR #25 的 `codex/g85-server-ai-perception` 分支，提交 `d8e98e152eae6205819a49964f4c51b150b235ed`。本阶段按设计 D1、D7、M4.5 及分层验收实施。此次交付提交包含 M4.5 实现、定向测试及证据，供 Draft PR #25 审查。原有 Python/Lua、验证脚本及未跟踪设计和实验文件继续保留在本地工作区。

**阶段结论：** L0 兼容、L1 有界区域事实、L2 历史隔离在本报告的离线与隔离原生范围通过。最终包 15/15 个区域样本完整采样，真实 Worker 的单次只读调用通过，后续 1/1 个既有导航目标完成并确认释放。没有图形客户端/真实模型验收，也不承诺硬实时帧时限。

## 实现及协议

沿用 `ObservationReader.ReadCell`、`TerrainReader.ReadCell`、`SceneGeometry` 以及原游戏线程/绑定查询路径，未创建第二套地图、后台搜索或观察后的自动行动。

| 表示 | 范围与来源 | 契约 |
|---|---|---|
| L0 | 原 45×29、1305 Tile，当前 Player 周围 | 默认 `observe_scene({})` 仍为 schemaVersion=2、原七层、行字符串与候选；`observe_terrain`、原生执行/碰撞不变 |
| L1 | 当前服务器 Tile，AI 身体中心附近；默认 120×68 | 显式 `scale='region'`、schemaVersion=3；width=45..128、height=29..96，面积≤8192；无摄像机/视觉/照明含义 |
| L2 | 既有 `AiMap/query_map`，WorldUid+CharacterId 历史 | 存储格式、原子提交、哈希恢复与揭示近似不变；L1 不调用 Remember/刷新地图；历史查询时间不等于逐格新鲜度 |

新增握手能力 `server_ai_scene_region_v1`。旧连接的模型 API 定义隐藏 scale/width/height；宿主在 RPC 前拒绝新参数，不向旧服务器静默回退 L0。原服务端参数校验仍会拒绝未知参数。能力协商、Python schema、C# 校验与实际读器使用相同尺寸、面积与层限制。

```lua
local current = api('observe_scene', {
  scale='region', width=120, height=68,
  layers={'geometry','objects','hazards'}
})
-- 需要历史先验时，仍使用原 query_map，而非另一套持久地图。
local history = api('query_map', {
  mode='region', centerX=current.data.actor.mapTile.x,
  centerY=current.data.actor.mapTile.y, width=120, height=68, cellSize=2
})
return {bounds=current.data.bounds, coverage=current.data.coverage,
        objects=current.data.objects, historyKnowledge=history.data.knowledge}
```

L1 默认只返回 geometry/objects/hazards；可选 map。区域不提供 landingRegions、placementCandidates、objectPlacementCandidates，也拒绝它们的预算参数；矿物对象没有 `mine_target` 令牌/target。远处资源是当前观察事实，后续行动仍须经过既有导航及新鲜 L0/原生检查，不把区域空气连通解释为身体可达。

### 精度、未知、危险和显示

- `geometry.grid` 保留每格形状，不用高度图/多数票。半砖、斜坡、平台、完整块、孔洞、驱动不解释的格仍由现有分类器决定。
- `coverage.knowledge` 是独立逐格编码，保留原始 Tile knowledge、collision knowledge 与导航是否已知。out_of_world、unloaded、unknown_mod_hooks、not_sampled 不会压成空气。全局 unknown/uninterpreted/unknownNavigation 计数独立，未知原因有分项计数。
- `hazards.tileMask` 按 bitSources 的 0 起始位保留每格全部已观察危险，包括液体、接触伤害、特殊表面；危险详情列表截断不删除掩码。掩码 0 不是未知格安全的证据，必须同时看 knowledge/coverage。
- `objects.kindGrid` 保留每格已识别类别与内容键；空 kind 还须结合 knowledge，不能当作“该区域无资源”。对象列表可独立截断。
- map 的 terrain、actor、markers 分开。实体图符不会覆盖底层几何、危险或未知；NPC/弹幕仍是同次观察的当前包围盒/速度/作用域，不增加未来轨迹推断或永久槽位身份。

### 无损编码与 Worker 上限

上述 grid 的 `encodingVersion=2`，palette 下标从 0 开始。每行只有两种编码：

- `rle`：data 为交替 `[count,paletteIndex,...]`，每行最多四个 run。
- `hex_indices`：data 为 ASCII 字符串；每格占固定 `indexDigits` 个十六进制字符。Lua 第 x 格（x 从 1 开始）取 `palette[tonumber(string.sub(row.data,(x-1)*row.indexDigits+1,x*row.indexDigits),16)+1]`。y 也从 1 开始；世界 Tile 坐标由 bounds.minX/minY 加索引减 1 得到，Tile=16px。

这同时约束字节和节点：最坏交错地形若仍用数值索引数组，多个网格会超过原 Worker 的 20,000 节点限制，单看几十 KB 字节数不够。本轮没有放宽 Worker 的节点/内存/输出限制；独立解码反例覆盖 1～4 位索引边界，实际 Worker 也验证密集打包网格可接收、可用原 `string.sub/tonumber` 解码。测试的最大密集数据低于 16,000 节点（含字段名保守计数），并检查了字节上限。

### 有界成本与退出语义

区域 Tile 采样有 10ms **软预算**，每 128 格检查一次；停止后余格全部为 not_sampled，samplingTruncated=true、geometryComplete=false。对象聚类最多 256 组；触限时 groupingTruncated=true，matchedCount/omittedCount 为 null，仅给 matchedCountLowerBound，objectCounts.observedCountsComplete=false。逐格 kindGrid 仍保留已采到的资源事实。列表上限和采样/聚类上限分别表达。

计时明确分层：`sample.elapsedMilliseconds/allocatedBytes` 是读器及区域派生/编码；`projectionElapsedMilliseconds/projectionAllocatedBytes` 还包含游戏线程查询投影，不包含最终 IPC 序列化、网络和 Python/模型耗时。`payloadBytesBeforeMetrics` 是补成本指标前的数据对象序列化大小；下表 dataJsonBytes 是从实际回执重新规范化的 data JSON 字节数，包含最终指标及查询来源字段，不冒充完整 TCP 字节数。

开发期保留了一次未预热包的首次查询投影 58.0742ms；因此在**监听开放、世界采样之前**准备纯数据 codec/JSON 元数据。最终原生日志记录预热 124.76ms，这部分是加载成本，未藏入/冒充零耗时；不触碰 Player/Tile/map，也不更改 Actor 或启动/重放动作。最终首次区域查询仍有 24.355ms 样本，不声称每次满足 16.7ms 或硬 10ms 限制。GC、调度和未知 Mod 行为不能靠软预算给出硬实时保证。

## 固定世界与最终原生结果

使用前阶段已钉住的 tML 2026.8.3.0，DLL SHA-256 `fcc6a9624b12191be4a15d714a3675440911feb8c1d374642ab1fd686f0da0a5`；引擎/隔离/身份检查未改。源世界 SHA-256 `9f4665e8c6989bc5a523349e1bd4a303895814152170ff6cf9ebc7e862a54482`，每次复制到新 profile，不覆盖现用包/存档、不连接用户游戏或付费模型。

最终包 **0.1.80**，SHA-256 `cbbcfe1d442c6627aaab6907564c423dd62ce1cda03f6c805212dffd5994e913`。最终 profile 为 `ffe0232411c6436898ad14e4e195901f`；WorldSession=`2b2e6a9d81ef4383b1fdf4d77741422f`，Actor slot=254、generation=1。L2 使用 WorldUid=`0529c238e79c44e7bbe2fd3c7648f041`、CharacterId=`294df081bab96bd85f46b67312e3ffb7`；它们与运行期会话身份分工不同。

**隔离夹具事实**：复用已有复制世界 Action/Terrain 门禁，额外 `-companionregiontest` 才在原夹具范围内注入远处铜矿、尖刺与平台；参数门禁在写格前验证。角色起点 X=33630，铜矿 Tile=(2134,241)，尖刺=(2132,241)，都在原 L0 的水平边界外。L1 在完整采样中读到真实 Tile 内容/形状和危险，L0 不含该铜矿；没有以历史图补当前未知。构造地形是诊断数据，不是自动造路或原生自然资源分布的证明。

每个范围采样 5 次，包含第一次查询，不丢慢样本；**15/15 完整采样**，都核验范围、身份、同一 gameUpdate、远处铜矿及危险。尺寸是名义像素矩形对应的 Tile 范围，不是从图形相机截获的视野：

| 名义范围 | Tile/面积 | 完整样本 | 查询投影 ms 最小/中位/最大 | 投影分配字节范围 | data JSON 字节范围 |
|---|---|---:|---|---|---|
| 1280×720 | 80×45 / 3600 | 5/5 | 3.9871 / 4.8353 / 24.3550 | 929544～1176712 | 11981～11997 |
| 1920×1080 | 120×68 / 8160 | 5/5 | 5.9796 / 10.4235 / 11.5308 | 2043776～2079176 | 18397～18400 |
| 上限矩形 2048×1024 | 128×64 / 8192 | 5/5 | 6.2385 / 7.5708 / 14.1398 | 2060960～2060968 | 18651～18653 |

默认 L0 面积 1305，L1 默认请求 8160；这是范围扩展，不是视线/可达性提升的百分比。2560×1440 等更大视口不承诺一次覆盖，超面积请求明确拒绝，不能静默裁小后宣称覆盖完整。

同包真实 Lua Worker 用一次只读 API、0 动作得到区域摘要；原 `query_map` 返回 last_known_state 与既有光照近似来源。随后**一次导航目标、1/1 完成**：目标 X=33822，实际 X=33630→33810.270（+180.270px），89 游戏更新、3 次规划、0 次恢复重规划、MaxError=0。沿用原目标公差与真实到达/支撑/停稳谓词；不是精确停在目标 X。终态为 completed，endEvidenceStatus=known、inputsReleased=true；逐帧轨迹和原始回执保留。

候选包的部分采样和 58ms 冷启动记录按单独组保存，不混算最终成功数或声称同包。最终 15 次没有触采样预算；离线强制慢读反例验证预算退出，早期原生候选也实际出现过部分采样，均保留 not_sampled 事实。

## 离线、兼容与构建

- `tests/run_scene_region_tests.ps1`：16460 项检查通过。以 `d8e98e1` 的真实读器为对照，默认及七个单层/组合逐字段比较 L0（只归一化 sceneId/耗时）；覆盖各形状、重叠危险、世界外/未加载/Mod 碰撞未知、强制采样超时、分组截断、实体显示分离、多位 hex 与节点/字节约束。引擎边界是替身，不冒充 Terraria。
- 同一离线实际读器的 3 个最大面积模式各测 3 次：最终平坦约 6.2～11.9ms，交错危险约 9.7～12.1ms，碎片矿约 10.6～13.7ms；规范化 data 最大 39522 字节。替身每次生成的记录也计入分配，不能将其分配/时间等同原生引擎。
- `run_csharp_tests.ps1 -SceneOnly`：2018 项原空间/共享支撑检查通过；`-NavigationOnly`：M4.1 搜索 187、M4.2 恢复 111、M4.3 行程 23、M4.4 能力 70、导航感知 44 及原落地/钩子通过。
- `run_ai_map_tests.ps1`：14 项地图检查及独立 PowerShell 进程恢复通过，含跨世界/角色拒绝、损坏拒绝、地图/角色世代哈希提交。AiMap/AiMapRuntime/WorldCharacterFiles 源码哈希保留，存储代码未改。
- 定向 IPC、原子协议、297 项适配替身、64 项 M3 任务、30 项聊天通过。游戏线程身份前/后复核、取消/unknown、终态与释放证据契约不变。
- Python 六个指定文件 `test_scene_region test_server_ai_scene test_scene_layers test_ai_map test_server_ai_contract test_navigation_perception`：31 项通过，35.988 秒。单独的 `test_scene_region` 4 项又在隔离快照通过：原有修改的 5 个 Python/Lua 运行时文件替换成提交 HEAD 版本，证明本阶段新增查询不依赖那些未提交改动。原工作区文件没有被替换。
- 构建 0 错误、0 警告；差异/证据哈希检查通过。**未运行全量测试。** 历史 cff5ae7 精简脚本的替身落后于已提交的火把/放置/M1 字段，补齐当前读器编译依赖；本阶段兼容性判据是固定 `d8e98e1`，没有把历史 compaction oracle 的差异当作本阶段修复。

## 复现与证据

```powershell
pwsh -NoProfile -File tests/run_scene_region_tests.ps1
pwsh -NoProfile -File tests/run_csharp_tests.ps1 -SceneOnly
pwsh -NoProfile -File tests/run_ai_map_tests.ps1

& ./tests/verify_navigation_recovery_native.ps1 `
  -TModLoaderPath 'D:/Agent Project/terraria-ai-companion/.local/m4-3-engine' `
  -WorldPath .local/m4-audit/source.wld `
  -PackagePath .local/m4-5-final-build/Mods/CompanionBridge.tmod `
  -Region -RegionIterations 5
```

原生入口先核对钉住引擎、空闲端口，创建新 profile，只清理自己创建的进程。Region 不与 Mobility/Probe/Baseline/Scenes 选择器混用。普通角色的 Buff/装备初态沿用已审查的显式隔离夹具，不进入生产观察路径。

[summary.json](summary.json) 是分组派生结果；[manifest.json](manifest.json) 保存基线、源码/包/源世界/运行时上下文哈希及条目清单；[raw-runs.zip](raw-runs.zip) 无损保存最终/指定候选的回执、轨迹、最终原生日志、测试日志、离线反例、源码快照和实际测试包，共 122 个条目。原始文件不为结论改写。原有修改的运行时代码只记录上下文哈希，不作为源码快照提交。

## 剩余限制

只验收源码、离线和隔离原生层级；未运行图形 Host & Play、真人并发改地形、大规模实体/第三方 Mod 压力或真实模型自主目标。服务器当前 Tile 不是玩家实际视觉/照明，未增加 RGB/亮度、自动插火把或动态轨迹能力。

预算是有限工作量与软采样截止，不是硬实时保证：首查仍有 24.355ms，初始化另有 124.76ms；更慢机器/GC/引擎钩子可能更慢。行优先采样超时时，近身格也可能未采到，须保留未知；导航仍独立读取 L0/原生碰撞。对象分组触限会丢失部分组件详情/精确总数，kindGrid 保留已观察类别/内容键，不证明完整对象或可挖/可达。请求更多层或复杂实体可能触及原有 Worker 输出/lastObservation 预算，宿主原省略/拒绝语义不变，不能以空数据代替未知。

AI Map 的逐格新鲜度、区域入口/地标及已证实通行连接仍未增加；没有把空气连通写成路线事实，没有优化旧全图 overview 扫描或承诺全世界高频查询。客户端全局 Mod 准入、特殊运动和未知碰撞限制沿用 M4.4。本轮不扩展角色能力、改 M3 生命周期或提前实施 M4.6。
