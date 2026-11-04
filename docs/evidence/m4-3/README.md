# M4.3：行程级长距离规划与接续

2026-11-03。起始基线：PR #25，`codex/g85-server-ai-perception`，本地/远端均为 `167a5cf46a2ed49ab06481afceb892d6fa533b5e`，Open / Draft。原有 Python/Lua、原生脚本和未跟踪设计/实验文件保留；本轮不提交它们，不合并，不部署现用 profile。

**验收结论：M4.3 的单目标连续性、部分路线交接、有界退让/重复检测及原生到达/取消，在下列范围内通过。** 固定自然世界新旧均完成5/6目标；收益主要是减少重新搜索与保留可执行路线，不宣称自然世界完成率提高。没有扩大原搜索半径、装备支持或地图范围，没有运行全量测试、图形Host & Play或真实模型。

## 实现与兼容性

现有 `navigate_to_x` / `navigate_to_landing_region` 仍是唯一导航入口；一个原目标、ActionId、session/generation 与既有M3寿命贯穿整个行程。M4.2已经能逐边向远目标推进，本轮没有将其描述成“旧版完全不能长距离导航”，也没有另建任务运行时。

- `NavigationTask` 保留已规划的安全动作前缀与当前边索引。实际 checkpoint 按原支撑、速度、位置、跳跃释放规则核验后，才交接下一条边；完整路线结束则从真实位置规划下一局部窗口。局部 `ReachedGoal` 不等于原目标到达。
- 接续前使用当前身体、相同物理参数和当前 `IMotionWorld` 重新预测整条下一边及制动尾；落点/速度必须与旧边吻合，否则丢弃旧后缀并重新规划。下一帧继续沿用 `LocalMotionController` 的当前世界检查。不是盲放缓存帧，也不把旧地形当实时事实。
- 实际运动偏差继续走M4.2的中性输入、真实释放、连续两次安全停稳和有界恢复。恢复后清空旧后缀，从实际位置规划；未确认落地、危险、身份改变、取消、unknown或外部终态均不继续。
- `NavigationTripProgress` 管实际到达过的 checkpoint、最佳目标距离、退让额度及重复状态。除减少剩余距离外，在距最佳进展锚点X/Y各128px内，最多允许6个新的非改善落脚点；这不是简单放大原“四次没有水平进展就失败”的阈值。回到已知状态不再获得新探索额度，真正改善目标距离才补充额度。
- 重复策略使用8px位置格与0.5px/tick水平速度桶，同一状态允许到访两次；不同动量不直接视为同一重复状态。此策略是有界行程循环限制，不是物理等价或搜索完备性证明；M4.1仍保留完整精确PhysicsState标签。
- `StateSpacePlanner` 的默认行为保持原M4.1规则。只有行程传入的、已通过原预测/支撑规则的候选，在没有更佳正向部分路线时才能作为新探索前沿；原128px目标窗口、192px路线X跨度、32次扩展、25ms预算、64px下降限制不变。
- 使用现有 `query_map` 的同一个 `AiMap` 存储作为**近等分安全退让候选的排序提示**：仅服务器游戏线程、当前Owned Actor、Ready状态、WorldUid和CharacterId均匹配时只读采样。历史未知/固体/平台不是执行许可；当前预测先验证安全，提示不能使危险、未知或未观测轨迹通过。客户端路径不依赖服务器历史地图；没有新建map.bin或拓扑。
- 行程保留1800更新上限、256条完成边和M4.2最多3次恢复；增加128次搜索和3200ms累计新规划预算，均不随局部接续/恢复重置。重复/行程预算原因经IPC明确传递，不能获得Python有限Direct Input降级资格。原30秒M3执行截止及原生1800更新保护不变。
- 增量诊断：TripUpdates、ContinuedEdges、InvalidatedRoutes、TripPlanningMilliseconds、DetourCheckpoints、VisitedCheckpoints、RepeatRejections。原PlanNumber表示新规划次数，ContinuedEdges表示验证后采用的后续边；它们不会伪装成新的外部动作。

`PhysicsSimulator`、`NativeMotionWorld`、`MotionSupport`、EngineHooks、M3 TaskRuntime与 `python/navigation_recovery.py` 没有修改。没有直接写位置/速度执行导航、后台读取Player/Tile、自动重连或在终态之后重放。

## 离线反例：必要退让

在现有碰撞框架中构造低顶：地面Y=1600，屋顶X `[1600,1664]`、Y `[1520,1536]`，后接64px高障碍；角色从 `(1620,1558)` 去X=1820，需要先退到屋檐外再上跳，随后经较低支撑回到地面。

| 同条件Task源码对照 | 结果 | 更新数 | 最小X | 最终X | 新规划数 |
|---|---|---:|---:|---:|---:|
| M4.2 | failed / navigation_stalled | 72 | 1573.6001 | 1615.8401 | 4 |
| M4.3 | arrived | 153 | 1573.6001 | 1816.72 | 2 |

**实验结果**：旧搜索并非完全找不到退让动作，而是行程交接/单调进展守卫使它在目标前终止；新实现保留路线并核验4个退让checkpoint后完成同一原目标。旧Task仅从基线导出改名，使用相同物理、当前默认无额外回调的搜索和同一碰撞夹具；源码/runner/原始输出在ZIP。它是离线几何反例，不冒充原生屋檐验收。

双向平地一次1536px目标均实际到达：546次更新、49条完成边、13次新规划、36条接续边。另验证缓存前方新墙使后缀失效、不同动量重复状态、取消后缓存不能执行、累计更新/搜索/耗时预算，以及历史提示不能批准危险。

## 固定自然世界原生对照

M4.2阶段的引擎阻塞本轮已在**独立目录**解决：下载[官方2026.08.3.0发布包](https://github.com/tModLoader/tModLoader/releases/download/v2026.08.3.0/tModLoader.zip)，引擎 SHA-256 `fcc6a9624b12191be4a15d714a3675440911feb8c1d374642ab1fd686f0da0a5`、hooks SHA-256 `595d26fb3bbe27548d030943b5ed2437f690d1c002129bad21fafeaeb8118f7a` 均匹配原门禁。使用现有.NET 8 runtime，不替换Steam安装或放宽指纹。0.1.77基线从原HEAD导出，只附加与新包相同的诊断计时/快照；运动源码保持旧版。

固定world来自历史 `natural-navigation/raw-runs.zip` 的 `world/source.wld`，seed `20261028`，SHA-256 `9f4665e8c6989bc5a523349e1bd4a303895814152170ff6cf9ebc7e862a54482`。两组逐场起点X/Y、Grounded、SafeSupport、Settled全部匹配；各自新profile/原生Basic角色配置、相同安全设置。World/Actor/generation/slot与生命周期分别见各组原始报告，不把跨进程标识当成同一个Actor。

每场只提交**一次** `navigate_to_x`，Lua预算明确限制一个动作、一个API，不调用explore/LLM，不失败重试，不改Tile；仅场景初始化注入起点。NPC战斗排除，世界正常原生更新。1536px超过两个45Tile宽局部窗口。

| 场景 | 旧/新完成 | 旧/新净水平位移(px) | 旧/新新规划数 | 旧/新重规划数（不含首次） | 旧/新Tick P95(ms) |
|---|---|---|---|---|---|
| 出生点向右768 | 是/是 | 779.277 / 757.195 | 18 / 11 | 17 / 10 | 2.8664 / 2.5173 |
| 出生点向左768 | 是/是 | 764.590 / 764.590 | 12 / 12 | 11 / 11 | 1.7237 / 1.8419 |
| 出生点向右1536 | 是/是 | 1547.430 / 1526.390 | 47 / 22 | 46 / 21 | 2.5157 / 1.7870 |
| 出生点向左1536 | 是/是 | 1535.027 / 1535.027 | 40 / 31 | 39 / 30 | 2.8474 / 2.4955 |
| offset -231向右768 | 是/是 | 773.680 / 773.680 | 22 / 16 | 21 / 15 | 2.8242 / 2.7478 |
| offset -231向左768 | 否/否 | 272.432 / 250.771 | 16 / 9 | 15 / 8 | 2.5041 / 2.2121 |

**观察**：目标完成均为5/6；新规划155→101，重规划149→95，新版实际接续59条边。失败样本旧为 `navigation_stalled`，新为 `navigation_search_timeout`，保留候选拒绝中的未知/危险、无稳定落点等事实；两组均known释放，未将预算结束包装成全局不可达。新版失败样本净推进更少，不能隐藏该退化。

所有完成动作另行检查真实Grounded/SafeSupport/Settled、原到达谓词、**释放前**真实速度、known结束与输入释放；到达不能由零预测误差推断。水平API允许越过目标后安全停稳，并不承诺精确停在目标X；表中位移是净ΔX，完整行走量另存summary。

计时在隔离夹具中用Stopwatch包围实际 `Main.Update(GameTime)`，包含原生游戏、导航、日志等主更新工作；采样范围是有导航快照的诊断更新，包含终态结算附近样本，不等同纯输入帧。全部样本合并旧/新中位数1.2223/1.1768ms、P95 2.6441/2.2817ms、最大146.7056/142.8146ms。原始逐Tick数据全部保留。

新规划累计测得1646.1227→940.7048ms，**不包含后续边重新验证的成本**，不能当作整个控制器CPU节省量。动态世界、JIT/GC、OS调度和预算检查会改变轨迹及计时；这是一组固定输入观察，不是统计性能保证。最大整Tick峰值出现在首次场景；推断与预热/JIT等开销有关，但未拆分归因。

**执行方式决策**：保留游戏线程上的有界搜索与单边完整复核，不引入不安全的后台世界读取。典型Tick P95低于3ms、规划次数已减少，原Task.Before的50ms保护仍在；冷启动整Tick峰值是未消除的实机风险，25ms搜索预算并非抢占式硬实时上界。尚不能据此承诺所有场景无卡顿。

## 同包原生安全与生命周期

最终0.1.78包额外通过：位置偏移后恢复一次并到达、新低墙经新规划/真实跳跃越过、新高墙有界停滞失败、空中扰动只发中性输入待自然落地后恢复、恢复中取消。每场一个外部动作；取消后重复查询仍为同一actionId/cancelled，known输入释放，旧动作不重启。

四个场景在一轮中通过；取消场景单独定向复验通过，全部为同一个最终包哈希。两次脚本问题原始证据也保留：首次误要求低墙一定触发恢复，而真实结果已安全完成且正常重新规划；随后取消的末尾物理观察没有新输入注入，脚本错误索引null。修正为验证真实跳跃/重新规划，以及只允许已取消末尾样本无注入且物理输入清空；位置/空中偏差的释放与两次停稳门槛没有放宽。没有重写旧回执，未倒填0.1.77/M4.2历史原生验收。

## 定向验证、复现与剩余风险

- `-NavigationOnly`通过：23项行程、111项M4.2恢复、187项M4.1搜索、44项导航感知及原落地确认/连续运动/玩家钩子。`-VerticalOnly`、`-LandingOnly`通过。
- IPC/原生适配替身297项、M3任务64项、聊天30项及其余协议检查通过；Python `test_navigation_recovery` 8项通过（40.139秒）；AI Map 14项及跨进程恢复通过。
- 最终0.1.78构建0错误、0警告，包SHA-256 `5056f789aafda3b64fc6ae32ba3e2b4a3e49683fc5dfa32dd578abf944128f52`。source/zip哈希见manifest。原有未提交Python runtime用于两组驱动，输入字节作为证据保存，没有作为本轮源码提交。
- 超时：行程更新、累计搜索/耗时预算与M3截止/unknown契约有定向离线证据；原生实际观测了搜索预算失败，**没有强制等待真实30秒动作超时**。图形同场、远端收包、真实模型、任意装备/Mod或所有自然地图均未验收。

```powershell
pwsh -NoProfile -File tests/run_csharp_tests.ps1 -TripOnly
pwsh -NoProfile -File tests/run_csharp_tests.ps1 -NavigationOnly
# 完整官方原版本解压到隔离目录，使用指纹匹配的引擎及.NET 8
pwsh -NoProfile -File scripts/build-companion.ps1 -TModLoaderPath <隔离原引擎> -OutputPath .local/trip-build
& ./tests/verify_natural_navigation.ps1 -TModLoaderPath <隔离原引擎> `
    -WorldPath <固定source.wld> -PackagePath .local/trip-build/Mods/CompanionBridge.tmod `
    -Offsets @(0,-231) -Trip -Port 27981 -IpcPort 27982
# M4.2兼容/取消补验；可用 -Scenes @('cancel_air') 定向复测
& ./tests/verify_navigation_recovery_native.ps1 -TModLoaderPath <隔离原引擎> `
    -WorldPath <固定复制世界源> -PackagePath .local/trip-build/Mods/CompanionBridge.tmod
```

剩余限制：局部搜索仍不完备/不保证最优；退让额度和重复桶是有界策略，可能拒绝更复杂的合法绕行；历史排序没有新增拓扑或实时可信度保证。无可用前缀的局部预算停止仍可能终止行程，不会凭空声称另有可达路。复杂洞穴失败样本仍在，冷启动峰值仍在；新的长距离原生对照为水平目标，区域目标兼容主要由定向高度/协议夹具验证。若后续证据要求分时搜索，必须另行提供只读快照、游戏线程版本与执行前复核，不能直接把原生Player/Tile查询移到后台。

[summary.json](summary.json) 为逐场完整指标；[manifest.json](manifest.json) 为来源/配置/哈希/范围；[raw-runs.zip](raw-runs.zip) 无损保存90份对照、逐Tick、Lua/回执、源码对照、测试及脚本失败/复验证据。原型阶段另一次运行保留在.local且明确不参与最终验收。本轮只完成M4.3，不进入M4.4～M4.6。
