# Server AI 二维语义场景（0.1.59）

`observe_scene` 在一次绑定 AI 的游戏线程查询中返回 45×29 侧视地图、对象、危险来源和行动候选。目标是省去调用方重建局部空间及落脚坐标的工作。它不选择探索方向、规划路线、设计建筑或执行动作。LLM 根据目标和观察生成 Lua，原导航器继续负责移动。

当前实现与定向自动化已完成；**未部署现用 profile，图形 Host & Play 和真实 DeepSeek 新旧对比待验收**。旧感知接口继续可用，不将构造夹具、自然世界自动化或离线 Worker 当作真实模型验证。

## 接口与阅读方式

服务器握手 `server_ai_scene_v1` 才开放 Lua `api('observe_scene', {})`。客户端玩家路径和旧服务器不宣告此能力。仍只有 `execute_lua` 向服务器模式模型开放，查询沿用原身份、代次、槽位、时效及失败保护。

可选参数均为正整数，只控制详情列表，不改变采样区域：

| 参数 | 默认 | 最大 |
| --- | ---: | ---: |
| `maxObjects` | 16 | 32 |
| `maxRegions` | 12 | 24 |
| `maxPlacements` | 12 | 32 |
| `maxHazards` | 24 | 64 |

真实内容在回执 `data` 内。`map.rows` 和 `geometry.rows` 各 29 行、每行 45 字符；`originX/originY` 是左上角 Tile 坐标，列向右、行向下，1 Tile=16px。采样中心沿用 `observe_terrain`：AI 中心 X 左侧22格、脚底 Y 上方17格。不是相机、照明或视线遮挡图；可观察附近地下事实，不建立长期地图。

| 层 | 内容及限制 |
| --- | --- |
| `geometry.rows` | `.` 非阻挡，`#` 实心，`h` 半砖，`1..4` 斜坡，`=` 水平平台，`a/b` 斜平台，`A` 已驱动失效方块，`?` 未知。非阻挡不等于可走；背景墙不阻挡。 |
| `map.rows` | 在几何上叠加 `T` 树干组件、`O` 矿、`C` 箱/梳妆台、`D` 门、`W` 工作台、`$` 掉落、`N` NPC、`P` 其他玩家、`@` AI、`*` 敌对弹幕。 |
| `hazards.tiles` | 水 `~`、熔岩 `L`、蜂蜜 `H`、微光 `S`，接触伤害 `!`、巨石 `B`、运动模型排除 `M`；详情保留可重叠的 `sources`、Tile 类型/内容键、接触伤害和液体量范围。水平相同来源分组不是碰撞体或避险路径。 |
| `hazards.entities` | NPC 分类、实际边界、当次引用、伤害；敌对弹幕边界/速度/伤害。原分类推断不保证当前攻击或未来轨迹。 |

图符优先级为未知碰撞、Tile 危险、Actor/实体/对象、几何。单个字符无法显示重叠事实，必须结合独立层；实体中心在窗口外但包围盒相交时图标可钳在边缘，实际边界仍在详情中。详情按距离排序后截断，完整图不随列表上限删除标记。

## 对象与覆盖

`objects.items` 提供 `id/kind/typeId/contentKey/observedBounds/observedTileCount/complete/grouping/details`。几何坐标范围是 Tile；实体详情另给世界像素包围盒/速度。NPC 保留原绑定实体引用，掉落保留数组槽位、堆叠和速度，不把槽位当持久身份。

首版分类原版原生树干集合、与 `sense_world` 对齐的19种矿、箱子/梳妆台、门、工作台及相交活跃实体。树干按同类型四邻接聚合，合并相连枝干，但一树仍可能多片、相邻树可能合并；**树 `complete` 永远 false，组件数不是整树数**。矿物只在组件四周均已知且未触及观察边界时标为完整。家具复用 `TileObjectData.TopLeft` 锚点合并，未证明完整占地或内容，箱内物品不读取。其他家具、植物和 Mod 对象未做语义识别。

每个列表分别提供 `matchedCount/returnedCount/omittedCount/truncated`；`objectCounts.observed/returned` 保留各分类计数，避免被截断的远处对象看起来不存在。计数限定为本窗口、支持分类及当前样本。`coverage` 分开报告未读格、未解释碰撞和导航未知格；世界边缘导航未知不能显示为空地。几何完整不代表对象分类完整、候选穷尽或窗口外没有危险。

外层沿用 world/actor/generation/slot、`gameUpdate` 和服务器投影 UTC 时间；内层 `sceneId/gameUpdate/sample.sameGameUpdate` 标识本次采样。ID 只在观察内有效，多个查询不共享快照。`sample.elapsedMilliseconds` 是游戏线程投影耗时，不是网络/模型时间。

## 落脚与放置候选

`landingRegions.items[].target` 已匹配原接口：`minPlayerLeftX/maxPlayerLeftX` 为玩家左边缘世界像素闭区间，`surfaceY` 为脚底世界像素 Y；`feetDeltaY` 为相对当前脚底高度。逐行搜索所有高度，不使用固定脚底、单列地表或探索方向筛选。同高度相邻有效1px位置合并为连续区域。

候选先通过原 `MotionSupport.SafeFlatLanding`，再调用真实 `NativeMotionWorld.SafeFlatLanding`；支持20×42普通角色身体净空、已知干燥安全的水平实心顶面和水平平台。沿用原危险/未知/支撑规则，没有移动仿真、路线搜索、放宽阈值或世界写入。半砖、斜坡及斜平台不作为最终水平落点，仍可由既有导航器按其能力经过。地图可以显示运动 profile 不支持时的几何，行动前必须核对 `actor.navigationProfileSupported`。

```lua
local view = api('observe_scene', {})
-- 根据任务、危险与当前位置选择候选；这里不替 Agent 选择策略。
local region = view.data.landingRegions.items[1]
if region and view.data.actor.navigationProfileSupported then
  local result = api('navigate_to_landing_region', region.target)
  return {result=result, after=api('observe_scene', {})}
end
return view
```

`placementCandidates.items[].target` 是可直接传给 `place_tile` 的真实 Tile x/y；`solidNeighbor` 表示观察到的实心四邻接。首版只提出已知空、干燥、距AI中心≤80px、没有当前玩家占用、实心邻接的保守候选。`placementMaterial` 提示当前选中材料及既有放置白名单状态；它不读取完整库存、不调用原生 CanPlace，不保证原生距离、权限、邻接、材料或其他 Mod 条件一定通过。背景墙附着、家具、多格建筑、全局房屋/建造方案不分析。正确结果仍须实际世界变化、消费和输入释放证据核验，参见[原生放置](SERVER_AI_PLACEMENT.md)。

没有候选只表示本窗口/本规则/当前返回集合不支持选择，不能宣称全局不可达或没有可建位置。旧 `inspect_tile/scan_tiles/scan_entities` 用于细节或按需查询，`resolve_content` 继续解析名称，`sense_world` 保留类别/矿种过滤和其他半径。只需一个类别时，旧接口可能返回更少内容。

## 依据与实现

研究了 Numen 的 [LookAround.java](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/numen/common/src/main/java/com/dwinovo/numen/tools/perception/LookAround.java)、[spatial-perception.md](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/docs/spatial-perception.md) 和 [Terrain.java](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/numen/common/src/main/java/com/dwinovo/numen/nav/Terrain.java)（Terrain blob `3256baf84bdcc37f5c8feaf90997b1432b2eb05d`）。采用“紧凑局部表达 + 复用身体/支撑判断”的分工；Terraria 已是侧视二维，不做 Minecraft 俯视压缩，不将可站立直接编码成可达。

本机 Terrabot 反编译参考：`D:/Agent Project/terrabot/TerraPlayerBot/BotTerrainCommand.cs`、`BotBodyGeometry.cs`、`BotLandingSupport.cs`、`BotPerception.cs`。参考侧视几何、身体包围盒、支撑/平台与对象计数边界；未引入它的程序式探索策略或复制其导航器。

实际实现是 `SceneGeometry.cs` 的纯数据聚合及 `ServerIpc/ServerSceneReader.cs` 的绑定游戏线程适配。复用 `ObservationReader.ReadCell`、`TerrainReader`、`NativeMotionWorld`、`MotionSupport`、`LandingRegion`、NPC分类和家具锚点。模型契约、Schema、C#参数校验、白名单、能力握手及只读分发一并接通。原 Lua 内存/指令/时间/API/动作预算、失败后仅感知及 unknown 隔离不变；没有自动重试。

## 自动化结果与实际边界

原始 payload、失败及每文件哈希见[manifest](evidence/semantic-scene/manifest.json)，简表见[summary](evidence/semantic-scene/summary.json)。最终三次原生运行使用同一0.1.59包 SHA-256 `e891e55d2dea175a4d65e3f502c6dc713244201cd08d3aac6e9ab18585bbc178`，构建0错误/0警告。

固定旧/新感知配方在同一构造场景比较，旧调用是不同时间的样本，新一次查询同时满足四类事实：

| 任务 | 旧 API 次数 | 新次数 | 本次实际减少的工作 |
| --- | ---: | ---: | --- |
| 树木/铜矿定位 | 12 | 1 | 省内容解析、分开扫描和候选详情查询 |
| 四种液体来源 | 5 | 1 | 省8次行列→Tile坐标投影及4格细查 |
| 一个可尝试放置位置 | 4 | 1 | 省玩家/目标/邻格分步查询 |
| 上层平台落脚区间 | 1 | 1 | 旧宿主配方28次坐标运算，新直接传 `target` |

这不是最优旧策略或真实模型性能证明。新完整回执约16664 UTF-8字节，对照旧对象报告6421字节，内容不同，**不能据此宣称 token 更少**。本次构造场景首次投影25.55ms，生成世界六个初次场景查询4.43–26.34ms；是本机观测，不是耗时上限。窗口和列表受限，但每次仍需扫描1305格并批量验证候选，按需精确查询仍有价值。

构造场景用原生树生长/家具放置并封闭四类液体。真实 Lua 8次API/4动作完成观察、选材料、放置并读回、上64px平台、返回地面；两次导航均核验 grounded/safeSupport/settled 及绑定输入释放。此夹具的地形经过构造，不作为自然世界证明。

另复用先前归档的引擎生成世界（seed20261028、源世界哈希见 manifest），不写Tile；诊断只初始化起点并隔离NPC。Lua从新观察直接选区域，无宿主网格重建、无LLM，每组最多一次行动：出生点右向下16px，+32格右向上16px，+32格左向下32px，共3次成功且实际停稳；其余3组没有符合测试方向/不同高度条件的返回候选，明确停止、不重试。**不是6条路线通过，也不证明附近其他目标不可达。**

离线：35个不同Python定向用例（新场景7、旧后端28）；C#场景组1696条断言（包含区间内四分之一像素枚举，非1696个独立场景）；协议/原子/读取/适配/聊天45/40/51/57/30项、最终包62项IL检查通过。独立复制世界的旧读取、导航、使用、挖掘、取消和进程清理回归通过。未运行全量测试。

保留两类早期失败：树木夹具净空不足导致初始化失败，补齐隔离夹具净空；新导航改变角色位置后串行复用旧取消目标，动作在取消前已完成，现将旧动作组放在独立世界执行。没有为夹具失败修改生产动作或放松范围/安全校验。真实DeepSeek选目标/建造效果、Host & Play、真人/敌怪同场、远程同步及复杂Mod场景仍待验收。

## 定向复现

仓库根目录 PowerShell 7，已有依赖/引擎环境。以下均不调用模型；隔离原生脚本启动独立服务、复制世界、写测试夹具或起点并在结束时关闭自身进程，不操作现用profile：

```powershell
pwsh -NoProfile -File tests/run_csharp_tests.ps1 -SceneOnly
& .local/lua-venv/Scripts/python.exe -B -m unittest discover -s tests -p test_server_ai_scene.py -v
& .local/lua-venv/Scripts/python.exe -B -m unittest discover -s tests -p test_server_ai_backend.py -v
pwsh -NoProfile -File scripts/build-companion.ps1 -OutputPath .local/scene-build
pwsh -NoProfile -File tests/run_server_ai_ipc_tests.ps1 -PackagePath .local/scene-build/Mods/CompanionBridge.tmod
pwsh -NoProfile -File tests/verify_server_ai_native.ps1 -Atomic -Reads -Actions -Scene -PackagePath .local/scene-build/Mods/CompanionBridge.tmod -Port 27991 -IpcPort 27992
# 在当前 PowerShell 内传数组；WorldPath 换为已确认的真实生成世界源副本
& ./tests/verify_natural_navigation.ps1 -Scene -Offsets @(0,32,-32) -WorldPath .local/natural-native-15ee9d6959ad4b34aca99f39f0defc97/source.wld -PackagePath .local/scene-build/Mods/CompanionBridge.tmod -Port 27993 -IpcPort 27994
pwsh -NoProfile -File tests/verify_server_ai_native.ps1 -Atomic -Reads -Actions -PackagePath .local/scene-build/Mods/CompanionBridge.tmod -Port 27997 -IpcPort 27998
```
