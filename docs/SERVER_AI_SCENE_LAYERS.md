# observe_scene 按需数据层

M4.5 / 0.1.80 增加握手能力 `server_ai_scene_region_v1` 与显式 `scale='region'`：默认 120×68、最多 8192 Tile，使用 schemaVersion=3 的无损行编码、独立未知/危险/对象网格和有界聚类。默认 L0 仍为 45×29、schemaVersion=2，七层及候选不变。范围、解码、预算、原生证据与限制见 [M4.5 报告](evidence/m4-5/README.md)。

当前 schemaVersion=2 的固定说明精简、候选 validation 迁移及最新字节数见
[场景说明精简](SERVER_AI_SCENE_COMPACTION.md)。本页旧字段及测量描述记录 cff5ae7 的 schemaVersion=1；
layers 白名单、includedLayers、配套字段和省略语义在新版保持不变。

基于 61bf2d7；仅增加可选 `layers` 参数和 `data.includedLayers`。没有新 IPC 操作，
API/动作预算、原生动作、Lua 失败屏障、unknown 隔离和输入释放均不变。

```lua
local view = api('observe_scene', {layers={'geometry','landingRegions','hazards'}})
return view
```

`layers` 是非空、无重复的字符串数组，最多七项，名称区分大小写。Python schema/共享校验器与
C# AtomicParameters/Reader 均拒绝空数组、非数组、未知层、重复值或非字符串；不会回退为完整响应。
原有 maxObjects/maxRegions/maxPlacements/maxHazards 继续有效，即使关联层没选，也验证参数合法性。

| 数据层白名单 | 同时返回的配套字段 |
| --- | --- |
| map | 无；保持原 45×29 图符、图例和叠加优先级 |
| geometry | 无；原 45×29 碰撞形状和 Tile 原点 |
| objects | objectCounts |
| hazards | 无；原 tiles/entities 分类 |
| landingRegions | 无；原几何候选及原生安全复核 |
| placementCandidates | placementMaterial |
| objectPlacementCandidates | objectPlacementMaterial；集合内完整 diagnostics 保留 |

不传 `layers` 返回全部七层，所有原字段和值保持原义，仅新增 includedLayers。
显式传入全部七层与默认相同。includedLayers 总按上表顺序列出实际层，不依赖参数顺序。
未请求的层及其配套字段**省略**，不返回 null、空集合或伪造零计数。缺少候选字段不能解释为没有候选。
旧 Mod 不支持 layers 时会拒绝参数；没有 includedLayers 的旧回执不能假装已经按需选择成功。

始终返回 schemaVersion、sceneId、gameUpdate、bounds、tileSize、actor、coverage、sample、limits，
并保留原外层世界/Actor 绑定及采样元数据。coverage/unknown 仍描述整个原观察范围；
其 scope 字符串说明支持范围，不表示未请求的详细层已经返回。采样仍为一次绑定的游戏线程查询。
map/geometry 同时返回时原点一致；未返回它们时仍可从 bounds 定位范围。
单次采样的候选、完整性、截断和省略语义不变，多次调用不承诺同一游戏更新。

Agent 可按任务选层：碰撞/落脚可用 geometry、landingRegions，并按风险加入 hazards；
对象/资源定位可选 objects，需要空间结构再加入 map/geometry；放置选对应候选层。
这不是强制组合，也不增加自动选位、自动放置、缓存或重试；综合探索可继续请求完整观察。

## 实际计算依赖

- 始终读取原 1305 个 Tile 与导航分类，保留未知、碰撞、液体、接触危险及运动排除的基础事实，
  保持 coverage 和任何选中候选的安全语义。没有按层缩小观察窗口。
- 仅 map/geometry 需要几何行字符串；用单个 Lazy 共享，map-only 不返回 geometry。
  map 保留内部 Tile 对象分类、实体/弹幕标记与危险覆盖，保持未知 > 危险 > Actor/实体/对象 > 几何的原优先级。
- 仅 objects 做对象组件聚类、注册家具锚点解析、对象排序和计数；map 不依赖这些详细聚类结果。
- 仅 hazards 做 Tile 危险汇总和动态危险排序；map 所需危险源分类仍保留。
- 仅 landingRegions 创建 NativeMotionWorld、读取该路径所需 grounded 状态、枚举落脚区并运行原安全复核。
- 两类放置候选只在各自请求时生成，材料检查也分别按层执行；工作台定义、完整占地/支撑与诊断只在对象候选层执行。
  玩家碰撞框列表只在请求任一种放置候选时收集。
- map/objects/hazards 都没选时不扫描动态实体；items/玩家对象只服务 map/objects，弹幕只服务 map/hazards。
  共享分类和部分临时描述仍可能在所需依赖中产生，未声称所有相关工作都归零。

## 固定场景 UTF-8 JSON 字节实测

测试运行**实际 ServerSceneReader**，采用确定性的引擎边界替身和实际 SceneGeometry/MotionSupport。
固定 45×29 场景包含地面、树、矿物、工作台、液体、接触危险、未知格及 NPC/掉落/玩家/敌对弹幕。
对照 Reader 取自 git 中固定的 61bf2d7；默认响应逐字段比较只去掉新增 includedLayers，
统一 sceneId 和 sample.elapsedMilliseconds 两个易变值。各层内容、配套字段与共同元数据也逐字段一致。

下表是该实际 Reader **data 对象**用默认 JsonSerializer camelCase 配置序列化的 UTF-8 字节数，
同样统一上述两个易变字段；不包含外层 IPC/Lua 回执。不是生产世界平均值，也不是 token 或成功率测量。
原始测量数值保存在 [byte-sizes.json](evidence/scene-layers/byte-sizes.json)。

| 请求 | 字节数 | 相对完整响应减少 |
| --- | ---: | ---: |
| 默认全部 / 显式全部 | 17071 | 0 |
| map | 3155 | 13916 |
| geometry | 2858 | 14213 |
| objects | 5485 | 11586 |
| hazards | 2849 | 14222 |
| landingRegions | 1528 | 15543 |
| placementCandidates | 2748 | 14323 |
| objectPlacementCandidates | 5804 | 11267 |
| geometry + landingRegions + hazards | 4783 | 12288 |
| objects + map | 7414 | 9657 |
| 两种 placement 候选 | 7326 | 9745 |

## 定向验证

```powershell
pwsh -NoProfile -File tests/run_scene_layer_tests.ps1
pwsh -NoProfile -File tests/run_csharp_tests.ps1 -SceneOnly
pwsh -NoProfile -File tests/run_csharp_tests.ps1 -NavigationOnly
pwsh -NoProfile -File scripts/build-companion.ps1 -OutputPath .local/scene-layers-build
# 以下在 tests/ 中执行
../.local/lua-venv/Scripts/python.exe -B -m unittest test_scene_layers test_server_ai_scene test_lua_arguments test_lua_runtime test_lua_recovery -v
../.local/lua-venv/Scripts/python.exe -B -m unittest test_server_ai_backend.BackendTests.test_workbench_capability_and_full_receipt_required test_server_ai_backend.BackendTests.test_workbench_lua_uses_existing_dispatch_and_unknown_barrier test_server_ai_backend.BackendTests.test_workbench_explicit_cancel_preserves_unknown_release test_server_ai_backend.BackendTests.test_workbench_deadline_cancel_unknown_poisoned_lua_without_retry -v
```

结果：实际 Reader 离线 256 项检查通过；场景几何 1906 项检查通过；导航定向 harness 通过；
Python 57 + 4 项通过（真实隔离 Lua Worker / loopback 模拟后端，无真实模型）。
完整 Mod 构建 0 错误、0 警告，只生成包，未部署。
Reader 测试以边界替身计数确认落脚 world/安全验证、家具锚点、材料/定义、实体分类在未请求时不调用，
并验证未请求工厂不会执行、完整单格/工作台候选与诊断不变、地图覆盖顺序及每个单层/组合的省略语义。
未运行无关全量测试、原生 Terraria 隔离场景或 Host & Play，未测试真实 Mod 钩子/多人运行时负载，
没有实测生产环境耗时、模型 token 或任务成功率收益。

原始输出：`.local/scene-layer-reader-tests.log`、`scene-layers-geometry.log`、`scene-layers-navigation.log`、
`scene-layers-python-tests.log`、`scene-layers-placement-regressions.log`、`scene-layers-build.log`。
大小和完整样本：`.local/scene-layer-tests/byte-sizes.json`、`full.json`。
该定向脚本需 git 中存在固定 61bf2d7 对照提交，缺少时明确失败，不悄悄改用当前实现作为基线。
