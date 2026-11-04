# observe_scene 固定说明精简（schemaVersion=2）

基于 cff5ae7，阶段二最后一项。只改场景响应 DTO 和固定说明；没有修改放置执行、导航执行、
pcall、动作失败/unknown 屏障、输入释放、预算、角色存档或 layers 选择规则。

实现文件：`CompanionBridge/SceneGeometry.cs`、`CompanionBridge/ServerIpc/ServerSceneReader.cs`、
`python/atomic_operations.py`、`python/lua_agent.py`。
定向测试：`tests/SceneGeometryTests.cs`、`tests/SceneReaderLayerTests.cs`、`tests/run_scene_layer_tests.ps1`、
`tests/test_scene_layers.py`；相关场景文档已链接当前契约，原始对照样本另存本任务证据目录。

## 审计与保留决定

确定性 45×29 fixture 的 map/geometry 有 21/29 行、1290/1305 格相同；几何行有 5 段连续重复，
语义图有 10 段。这个重合率只描述该 fixture，不能推广到其他世界。
更直接的反例：同一实体/弹幕标记或液体覆盖下，11 种不同几何形状可呈现同一个 map 图符。
因此 `map.rows` 不是 `geometry.rows` 的可逆编码，不能删除 geometry。

两套 rows 均保留原 29 行、每行 45 字符，originX/originY 不变。没有 RLE：它虽然能利用某些
均质行，却需要额外展开重复次数、恢复行号；本轮没有真实模型解码验证，不为字节缩减改变
现有直接行号/Tile 坐标及 string.sub 使用方式。对象/危险覆盖、空中/平台/半砖/斜坡的区别仍由
独立几何层和结构化对象/危险事实共同表达。

## 精简范围与迁移

| 路径 | schemaVersion=2 |
| --- | --- |
| map.legend / map.priority | 删除固定副本，图例与优先级统一放入 observe_scene 工具契约 |
| geometry.legend / geometry.meaning | 删除固定副本；rows 和原点保留 |
| coverage.objectScope / treeIdentity / candidateScope | 删除固定范围解释，移入工具契约；所有扫描/未知计数和 geometryComplete 原样保留 |
| objects.items[].grouping | 按 kind 可确定的分组约定移入工具契约 |
| Tile 对象 details.identity | 删除重复的观察作用域提醒；anchorTile、observedTargetTile 等事实保留 |
| hazards.meaning | 删除固定说明；tiles/entities 及各自全部计数、items、截断字段保留，包括空集合 |
| 三类候选 items[].validation | 提升为各自集合的 validation，文本不变，整组只返回一次；matchedCount=0 时省略该说明 |

三类候选为 landingRegions、placementCandidates、objectPlacementCandidates。调用方读取
`collection.validation`，不再读取 `collection.items[i].validation`。它仍只说明几何预筛选，
不是已执行原生放置或已证明路径可达的证据。截断至零个返回项但 matchedCount>0 时仍保留集合说明。
其他字段的值、JSON 类型、数组顺序、Tile/像素坐标、范围和时效均不变；schemaVersion 从 1 升为 2。
Python 工具说明为全量图符、对象分组与候选限制的集中契约，Agent 指令指向该契约。
旧 schemaVersion=1 响应可能仍携带原说明字段，不能假定其已经精简。

保留了以下看似重复但有必要的字段：

- includedLayers 与 layers 的省略语义不变，未请求不等于不存在。
- 所有完整性、知识状态、危险来源及分类依据、pickup 未核验状态、未知和截断/省略计数。
  空危险集合不说明未知格内或观察范围外安全，不合并 tiles/entities 两种语义。
- 整个 objectPlacementCandidates.diagnostics（包括 classification 和首例）、两类材料信息，
  包括对象 anchorMeaning；没有借精简修改先前的放置诊断契约。
- 一份全局 limits、采样 phase 及所有身份/角色/覆盖/采样事实。

## 精简前后实测

测试分别编译 cff5ae7 的 Reader **和其 SceneGeometry record 定义**，避免用新 DTO 序列化旧 Reader
而污染基线。运行实际 Reader / SceneGeometry / MotionSupport，引擎边界为离线替身。
精确比较仅允许上表字段变动与版本号变化；其余所有字段与 JSON 类型逐项相等。
规范化只统一 sceneId 和 sample.elapsedMilliseconds，其他事实不改写。

以下为原固定 fixture 的 data 对象 UTF-8 JSON 字节数，默认 camelCase JsonSerializer，
不含外层 IPC/Lua 包装。不是生产世界平均值，不代表 token 或任务成功率改善；集中工具契约
本身也占系统上下文，本轮没有测量端到端模型 token 成本。

| 请求 | cff5ae7 | 本次 | 减少 |
| --- | ---: | ---: | ---: |
| 默认全部 / 显式全部 | 17071 | 13452 | 3619 |
| map | 3155 | 2171 | 984 |
| geometry | 2858 | 2181 | 677 |
| objects | 5485 | 4081 | 1404 |
| hazards | 2849 | 2205 | 644 |
| landingRegions | 1528 | 1034 | 494 |
| placementCandidates | 2748 | 1552 | 1196 |
| objectPlacementCandidates | 5804 | 4620 | 1184 |
| geometry + landingRegions + hazards | 4783 | 3956 | 827 |
| objects + map | 7414 | 5520 | 1894 |
| 两类放置候选 | 7326 | 5440 | 1886 |

原始数值见 本地临时字节测量结果，
审计数据见 本地临时审计结果。
规范化完整样本：精简前后样本（本地临时文件）。
额外固定场景涵盖截断、半砖/全部实心斜坡/平台、四种液体、世界边界、空危险但存在未知、
候选为空且完全未加载；它们也逐字段比较精简前后保留事实。

## 定向复现

```powershell
pwsh -NoProfile -File tests/run_scene_layer_tests.ps1
pwsh -NoProfile -File tests/run_csharp_tests.ps1 -SceneOnly
pwsh -NoProfile -File tests/run_csharp_tests.ps1 -NavigationOnly
pwsh -NoProfile -File scripts/build-companion.ps1 -OutputPath .local/scene-compact-build
# 以下在 tests/ 执行
../.local/lua-venv/Scripts/python.exe -B -m unittest test_scene_layers test_server_ai_scene test_lua_arguments test_lua_runtime test_lua_recovery -v
../.local/lua-venv/Scripts/python.exe -B -m unittest test_server_ai_backend.BackendTests.test_workbench_capability_and_full_receipt_required test_server_ai_backend.BackendTests.test_workbench_lua_uses_existing_dispatch_and_unknown_barrier test_server_ai_backend.BackendTests.test_workbench_explicit_cancel_preserves_unknown_release test_server_ai_backend.BackendTests.test_workbench_deadline_cancel_unknown_poisoned_lua_without_retry -v
```

实际 Reader/按需层/精简对照 593 项检查、场景几何 1932 项检查、导航定向 harness 通过；
完整 Mod 编译 0 错误、0 警告，只生成独立包，未部署。
Python 场景/按需层/Runtime/恢复 57 项、工作台回归 4 项全部通过。测试使用真实隔离 Lua Worker、loopback 模拟后端和离线引擎替身，
不等同于原生 Terraria 或 Host & Play 验收。未运行无关全量测试、真实模型或角色持久化工作。

原始输出 `.local/scene-compact-reader-final.log`、`scene-compact-geometry.log`、`scene-compact-navigation.log`、
`scene-compact-python.log`、`scene-compact-placement.log`、`scene-compact-build.log`。
完整规范化前后样本在 `.local/scene-compact-tests/`，新目录不会覆盖上一任务的样本。
对照脚本需要 git 中存在固定 cff5ae7，缺少时明确失败，不悄悄改用当前版本作为基线。
