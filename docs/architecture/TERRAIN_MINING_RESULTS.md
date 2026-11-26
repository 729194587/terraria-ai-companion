# 地形感知采矿的原生对照

基线为 `fe6190efd8250b2dbe8e8f16e11f36c41e4fcce2`。虚拟碰撞与真实 Player 的第一轮对照在 `d210443` 通过后，单格和矿簇接入共享规划。以下结果来自候选提交 `f76300c34ceca3e5529c7549f6fde66f29dedd4d`，引擎为 tModLoader 2026.09.3.1，候选包 SHA-256 为 `A5DA84D333E8FA8CE5470BB03951FA0BD9CA37C0892AC332F137CC2ACEAE4AE3`。

每次受控任务重建夹具。自然任务分别启动独立存档副本，每个副本只提交一次 `mine_target(cluster=true)`。比较器核对了源世界哈希、起点、矿种、目标数和起始矿物库存。两边都隔离敌怪。逐格证明、子动作回执、实测停稳坐标、规划依赖范围、选路成本及重复样本见[机器可读对照数据](../../tests/fixtures/terrain-aware-mining-comparison.json)。

## 预测与执行

规划前后原生地形签名一致。随后在实际改后地形上对真实 Player 重放相同控制输入。首次试验发现单格落阶会立即贴地，修正后使用原有运动容差复测。下表使用最终包。五项最终工具触及、停稳和安全支撑检查均通过。

| 动作 | 原生帧数 | 最大位置误差 px | 最大速度误差 px/更新 |
| --- | ---: | ---: | ---: |
| 干燥全砖上步行 | 35 | 0 | 0 |
| 穿过可通行瓦砾、药草和罐子 | 35 | 0 | 0 |
| 基础跳跃后刹停 | 47 | 0 | 0 |
| 失去支撑后下降一格 | 2 | 0 | 0 |
| 失去支撑后下降三格 | 17 | 0 | 0.000000954 |

在完整任务夹具中，所有障碍均由选定的原生采矿动作清除。水平两道墙的预测包含两处不同的挖掘站位。实际执行依次清掉第一道墙的三格、向内移动、清掉第二道墙的三格、移动并取得目标矿物证明。

## 场景结果

更新数从任务开始计量。重复三次的受控性能用例给出中位数和范围。规划毫秒为游戏线程执行接近搜索的累计计时，排除了跨更新等待。基线受控包测量工作位、接近扫描和开路报价探针，候选包测量完整接近规划时间片。两个指标都不含原生移动子任务内部规划和整服负载。自然基线使用原始包，没有接近规划计时。所有列出的候选终态均确认释放输入。

| 场景 | 预期 → 候选实际 | 目标证明 / 待处理 | 基线更新 | 候选更新 | 基线 / 候选规划 ms |
| --- | --- | ---: | ---: | ---: | ---: |
| 原地可采 | 直接 → 直接，零清障 | 1 / 0 | 27 | 27 | 0 / 0 |
| 便宜绕行 | 绕行 → 绕行，零清障 | 1 / 0 | 150（149–180） | 142（138–161） | 71 / 77 |
| 快镐，同一落差 | 开路 → 清两格后下落 | 1 / 0 | 308（307–386） | 102（102–103） | 553 / 179 |
| 慢镐，同一落差 | 绕行 → 绕行，零清障 | 1 / 0 | 347（344–347） | 165（162–166） | 552 / 183 |
| 薄墙 | 开路 → 清三格，零失败导航 | 1 / 0 | 951，预算退出 | 309（308–313） | 959 / 269 |
| 连续两道墙，矿簇入口 | 分段开路 → 清六格，零失败导航 | 1 / 0 | 905，预算退出 | 467 | 786 / 458 |
| 连续两道墙，单格入口 | 分段开路 → 清六格，零失败导航 | 1 / 0 | 484（484–484） | 467（465–471） | 166 / 450 |
| 第一段后仍有不可挖墙 | 保留障碍 → 零清障，预算退出 | 0 / 1 | 905 | 902 | 786 / 866 |
| 清第一格后替换远端路径 | 重规划 → 保留一格障碍破坏证明，预算退出 | 0 / 1 | 905 | 908 | 766 / 1322 |
| 挖掘中取消 | 停止 → 已取消，零清障 | 0 / 1 | 906 | 137 | 916 / 258 |
| 首次破坏后取消 | 停止 → 已取消，一格普通障碍已破坏 | 0 / 1 | 907 | 137 | 928 / 252 |
| 墙体支撑落沙 | 保留支撑 → 零清障，预算退出 | 0 / 1 | 926 | 902 | 935 / 929 |
| 自然埋藏铜矿 (2044,424) | 下降采集 → 清十格，实测下降 80 px | 11 / 0 | 1260 | 1262 | 未采集 / 159 |
| 自然换位铁矿 (2213,519) | 换位采集 → 四次导航，零清障 | 15 / 0 | 942 | 1147 | 未采集 / 207 |
| 自然大矿簇 (2071,466) | 增量批次 → 采完整簇，零普通清障 | 22 / 0 | 1007 | 1005 | 未采集 / 0 |
| 自然封闭铅矿 (2092,484) | 有界终态 → 预算退出，零清障 | 0 / 13 | 1140 | 594 | 未采集 / 355 |
| 自然埋藏铁矿 (2037,349) | 下降采集 → 清十二格，实测下降 128 px | 5 / 0 | 858 | 921 | 未采集 / 95 |

成本交叉夹具中的快镐绕行报价为 70，开路报价为 58。慢镐绕行报价为 106，开路报价为 136。两条候选都包括首次目标采矿。薄墙和双墙矿簇在基线未完成，表中按新增能力记录。

单格双墙的接近规划时间从 166 ms 上升到 450 ms，任务更新中位数减少 17。自然换位铁矿比基线多用 205 个更新。此次运行的目标破坏证明都是 15 格，实际铁矿入包数量分别为 14 和 15。大矿簇破坏 22 格，两次入包数量分别为 20 和 19。上述数值分别来自原生破坏回执和背包快照。

## 定向验证命令

在仓库根目录使用 PowerShell 7。下列 `$package` 指向所建的 `Mods/CompanionBridge.tmod`，`$world` 指向对应的初始世界。种子 1606771215 的初始 SHA-256 为 `918E33D72AE761F990CAA31E0734DD04AB375B3B4F4582702FCBF087D00707F0`，种子 20261028 为 `A9FEB51BB2B71BAF4C04FEC9C8617EC5683DC94CF7A0506EB200139802701223`。

```powershell
./scripts/build-companion.ps1 -OutputPath .local/terrain-mining-verified
$package='.local/terrain-mining-verified/Mods/CompanionBridge.tmod'
./tests/run_csharp_tests.ps1 -LocalEditsOnly
./tests/run_server_ai_ipc_tests.ps1 -NavigationLeaseOnly
$env:PYTHONPATH='python'
& .local/lua-venv/Scripts/python.exe -B -m unittest discover -s tests -p test_excavation_contract.py -v
& .local/lua-venv/Scripts/python.exe -B -m unittest discover -s tests -p test_mine_target.py -v
./experiments/virtual-player-engine-hooks/verify-native-mining.ps1 -ModName CompanionBridge -PackagePath $package
./experiments/virtual-player-engine-hooks/verify-native-mining.ps1 -TerrainApproach -ModName CompanionBridge -PackagePath $package
./tests/verify_server_ai_native.ps1 -Actions -NavigationSafety -NavigationSafetyCases surface_detour,drop13 -PythonExecutable .local/lua-venv/Scripts/python.exe -PackagePath $package
./tests/verify_natural_navigation.ps1 -Mining -MiningCases $case -WorldPath $world -PackagePath $package
```

基线自然组使用从 `fe6190e` 直接构建的包。受控组使用 `python tests/prepare_terrain_mining_baseline.py <新目录>` 导出该版本并加入相同的夹具与计时探针，然后用引擎的 `dotnet/dotnet.exe tModLoader.dll -server -build <导出目录>/CompanionBridge -tmlsavedirectory <输出目录>` 构建。回执验证器接受两个受控 profile、原生采矿 profile 及五对自然回执，参数见 `python tests/verify_terrain_mining_results.py --help`。

**已通过。** 最终包构建零错误、零警告。局部地形离线检查 238 项，导航租约 4 项，Python 矿簇契约 3 项、单格契约 6 项。隔离专服覆盖原生逐帧运动、已有连续采矿与绑定失效、受控场景、自然场景，以及无改造地表绕行和 13 格受控下降。受控比较器核验首次清障之前的完整预测、步骤与实际首击坐标、终态和输入释放。

**失败。** `./tests/run_csharp_tests.ps1 -SafeGroundWalkOnly` 在候选版和审计基线均触发离线断言 `world without flat-proof capability opts out`。该旧测试预期缺少平地专用证明时拒绝快走，当前 `SafeGroundWalk.TryPlan` 使用逐帧支撑检查。用相同的定向编译入口复现了基线失败。此次修正了该开关原本会误编译无关服务器代码的问题。

**未执行。** 全量测试、图形联机和战斗压力下的自然采矿。清障预测限于基础配装、建模的干燥全砖及已对照的可通行装饰。斜坡、半砖、平台、液体工程和特殊运动清障未作原生正例验收。

## 45b3de2 有界性能复核

本轮直接对照为 `45b3de2`，`fe6190e` 用于解释历史行为。两边的自然换位铁矿使用种子 20261028 的同一份世界、同一出生装备和相同起点。每次从世界副本启动独立专服。首轮发现原生角色会随机生成初始工具词缀，正式对照将夹具内三件初始工具设为无词缀，并逐次核对起始背包。正式基线从 45b3de2 导出，只移入相同的自然夹具。双墙分段探针使用原版受控夹具。双墙使用相同存档、站位、两道泥墙和无词缀骨镐，按原夹具的顺序重建三次。基线与候选专服串行运行。逐次任务 ID、包哈希、阶段更新、站位、动作和分段计数见[复核数据](../../tests/fixtures/mining-performance-45b3de2.json)。

### 自然铁矿为何多停两次

历史版先按覆盖数、再按成本试探原生工作位。实采批量为 **1、8、6**。直接基线把移动下界与首次采矿报价相加，按 8 的整数倍分档，档内优先选覆盖更多矿格的站位。起点后的第一次换位，覆盖 2、4、8 格的报价分别为 50.0、60.5、65.8，优先探测的最近站位成功抵达。第二次换位时，覆盖 2 格的 55.7 与覆盖 6 格的 61.0 跨过了绝对桶边界 56。此后每次停稳都按真实站位重报，整簇分成 **1、2、2、8、2**。四个导航子任务全部成功，普通障碍破坏为零。15 格矿物都有角色破坏证明。

唯一的规划改动是从本次候选的最低报价起算这 8 个更新。第二次换位直接走到覆盖 6 格的位置，实采变成 **1、2、6、4、2**。第一次换位中覆盖 8 格的报价比最低报价高 15.8，仍在另一个成本档。复测保留了 4 次导航和 5 批采集，收益来自更省时的落脚点。矿物执行阶段两边均为 690 更新。直接基线带探针的一轮中，原生工作位枚举和路线验证合计 115.9 ms，其中地表路线搜索 87.8 ms。虚拟地形搜索为 21.4 ms。所有分档比较仅决定先验证哪个工作位，实际路线报价与清障选择仍使用原生验证结果。

### 单格双墙的规划时间去了哪里

下表按独立诊断包的三次样本给出中位数。计时取游戏线程 `Stopwatch`，只包围方法调用或迭代器的单次 `MoveNext`，排除跨更新等待。父项包含子项，存在嵌套的行不能相加。历史版计时覆盖旧的接近扫描与开路探针，每轮最大值是单次探针时长。直接基线和候选的最大值均为完整接近规划时间片。原生执行子任务的内部耗时不在其中。

| 阶段 | 历史版调用 / ms | 直接基线调用 / ms |
| --- | ---: | ---: |
| 真实地形候选与路线搜索合计 | 185 / 127.9 | 872 / 368.9 |
| 其中 `SurfaceNavigator.Advance` | 81 / 95.6 | 192 / 367.3 |
| 其中候选落脚安全检查 | 未单列 | 675 / 0.8 |
| 其中批量报价、首次采矿报价 | 未单列 | 23 + 15 / 0.2 |
| 虚拟地形搜索合计 | 无同口径阶段 | 106 / 31.6 |
| 其中分支地形运动、理想平地步幅、下降预测 | 无同口径阶段 | 90 + 30 + 15 / 25.0 + 2.3 + 0.4 |
| 原生旁路步幅预测 | 未单列 | 8 / 0.4 |

直接基线发现完整清障路径后，还会搜索当前地形上是否有更便宜的绕路。双墙中，四个候选路线探测各用完 48 个原生搜索时间片，未找到可与开路比较的完整绕行。地表路线搜索占完整规划约 91%，历史单格路径每个候选最多用 16 个时间片。各候选目标区域不同，单个探测未找到路线不足以排除相邻目标，缩短搜索可能让本可绕行的角色开挖。此次保留搜索预算与运动验证。实际执行的十个动作逐个匹配首击前的预测，含两段各三格清障和四次移动，随后采到目标。三次复测的清障、移动和目标采矿分别为 126、152 和 25 更新。

### 同条件复测

每格给出三次样本的中位数及范围。历史自然铁矿沿用一次存档回执。采集批次只计目标矿物。毫秒按游戏线程耗时统计，包含调度抖动。

| 场景与版本 | 任务更新 | 累计规划 ms | 最长片 ms | 导航 | 采集批次 |
| --- | ---: | ---: | ---: | ---: | ---: |
| 换位铁矿 · 历史 | 942 | 未采集 | 未采集 | 2 | 3 |
| 换位铁矿 · 直接基线 | 1144（1140–1149） | 174.9（152.7–198.0） | 15.54（11.87–20.22） | 4 | 5 |
| 换位铁矿 · 候选 | 1093（1092–1098） | 155.9（125.2–182.8） | 16.98（11.17–24.70） | 4 | 5 |
| 单格双墙 · 历史探针 | 478（475–478） | 130.0（125.5–133.2） | 3.23（2.17–3.77） | 6 | 1 |
| 单格双墙 · 直接基线 | 456（449–461） | 386.7（368.3–420.0） | 5.08（4.66–7.01） | 4 | 1 |
| 单格双墙 · 候选 | 454（454–455） | 393.1（388.6–393.5） | 5.22（4.94–5.86） | 4 | 1 |

铁矿中位数节省 51 个任务更新。按阶段统计，移动 175 → 137，规划 277 → 264。样本中的最慢铁矿规划片为 20.22 → 24.70 ms。

### 定向验证与复现

在仓库根目录执行。导出包使用上文的 tModLoader `-server -build <导出目录>/CompanionBridge -tmlsavedirectory <输出目录>` 命令构建。两个固定装备的无探针包用于正式对照，计时包用于归因。自然命令对每个正式包各运行三次，受控命令一次包含三次单格双墙。`$controlProfile` 取受控命令打印的目录。

```powershell
python tests/prepare_mining_performance_probe.py --fixture-only .local/perf-direct-src
python tests/prepare_mining_performance_probe.py .local/perf-probe-src
python tests/prepare_mining_performance_probe.py --historical .local/perf-history-src
./scripts/build-companion.ps1 -OutputPath .local/perf-final
./tests/verify_natural_navigation.ps1 -Mining -MiningCases change-stance -WorldPath $world -PackagePath $package
./experiments/virtual-player-engine-hooks/verify-native-mining.ps1 -TerrainApproach -ModName CompanionBridge -PackagePath $package
python -c "import sys; from pathlib import Path; sys.path.insert(0,'tests'); from verify_terrain_mining_results import controlled; controlled(Path(sys.argv[1]), True)" $controlProfile
./tests/run_csharp_tests.ps1 -LocalEditsOnly
./tests/run_server_ai_ipc_tests.ps1 -NavigationLeaseOnly
```

**通过。** 两个正式包各通过 12 个受控场景、22 次任务，换位铁矿各完成三次。便宜绕行零清障。快镐开路 58 对绕行 70，慢镐绕行 106 对开路 136。连续双墙的单格与矿簇入口均逐动作核对。挖掘中取消留下零格破坏，首格后取消留下一个破坏证明。远端墙变化后重规划并在预算内终止。受控回执均确认输入释放。局部地形离线检查 238 项、导航租约检查 4 项通过。

**失败与未执行。** 初测的起始装备一致性检查未通过，随机词缀样本从正式对照中排除。正式定向回归没有失败项。未运行全量测试、其余自然矿簇和战斗压力测试。三次重复仅支持本夹具上的比较。

## 共享导航专服对照

直接基线为 `f26a632183156b06353be565f3db194f0bde45a4`。tModLoader 2026.09.3.1 上，两个包各执行 25 个受控场景、45 次任务。基线导出只同步专服夹具和滚动规划计时器。比较器核对开始前的世界、附属存档与包哈希，以及每对任务的站位、目标、工具、词缀和支撑库存。换位铁矿还核对完整初始背包、选中槽、翅膀及云跳状态。[逐次测量和原生回执](../../tests/fixtures/shared-terrain-navigation.json)

表内为基线 → 开发版。多次运行取各项中位数。累计规划与最长片以毫秒计，统计游戏线程的接近规划和滚动地形候选评估。移动子任务的内部搜索、逐帧执行及输入释放计入任务更新数。每次运行各自的最长片都保存在 JSON 中。

| 场景 | 每版次数 | 任务更新 | 累计规划 ms | 最长规划片 ms | 导航次数 | 实挖障碍 | 实放支撑 |
|---|---:|---:|---:|---:|---:|---:|---:|
| 目的地绕障 | 1 | 166 → 192 | 103.86 → 104.24 | 10.13 → 7.54 | 2 → 3 | 0 → 0 | 0 → 0 |
| 目的地快镐 | 3 | 274 → 66 | 740.86 → 82.49 | 7.65 → 3.82 | 0 → 0 | 2 → 2 | 0 → 0 |
| 目的地慢镐 | 3 | 328 → 220 | 669.32 → 152.94 | 4.46 → 3.70 | 0 → 3 | 2 → 0 | 0 → 0 |
| 目的地薄墙 | 1 | 226 → 265 | 170.70 → 174.77 | 5.69 → 4.91 | 2 → 3 | 3 → 3 | 0 → 0 |
| 目的地双墙 | 3 | 976 → 373 | 107.83 → 156.45 | 4.29 → 4.75 | 2 → 3 | 6 → 6 | 0 → 0 |
| 目的地跨沟 | 3 | 244 → 278 | 99.70 → 294.60 | 3.73 → 5.61 | 3 → 2 | 0 → 0 | 2 → 3 |
| 目的地落在新支撑上 | 1 | 151 → 166 | 37.09 → 100.54 | 3.35 → 4.57 | 3 → 3 | 0 → 0 | 2 → 2 |
| 单格矿双墙 | 3 | 455 → 444 | 391.63 → 347.39 | 6.01 → 5.10 | 4 → 4 | 6 → 6 | 0 → 0 |
| 矿簇入口双墙 | 1 | 459 → 443 | 408.01 → 349.54 | 5.38 → 6.28 | 4 → 4 | 6 → 6 | 0 → 0 |
| 采矿跨沟 | 3 | 902 → 352 | 198.55 → 152.14 | 5 → 4.89 | 1 → 4 | 0 → 0 | 0 → 3 |
| 自然换位铁矿 | 1 | 1096 → 1088 | 158.75 → 116.01 | 10.96 → 12.40 | 4 → 4 | 0 → 0 | 0 → 0 |

**取舍与执行。** 同一处薄地板，目的地快镐报价为开路 48、绕行 116，实挖两格。慢镐开路升至 120，角色改走绕行，零清障。矿物夹具保持快镐 58 对 70、慢镐 136 对 106。双墙目的地从 (2168,196–198) 开始逐格破坏，经过两个实际落脚，再破坏 (2173,196–198)。六次破坏都有执行者证明。单格与矿簇入口也逐动作核对了预测坐标和真实落脚。自然铁矿两版均为四次导航、五批采集、15 格目标证明。

**增加的成本。** 目的地薄墙由基线的一批原生清障改为三次逐动作清障，另多一个移动子任务。目的地绕障多一个停稳检查点。跨沟时基线分段放两块后交回原生移动，开发版为完整干地行走轨迹放三块，角色首次切换到材料槽后能力签名改变，余程重新报价。该夹具中，这带来了额外的搜索、原生放置和停稳时间。对于采矿，同样的三块支撑将原先耗尽预算的任务变为可验证的采集。规划对每个缺口试走剩余步幅，已能证明通行就停止耗材。沟中目的地另验证了两次放置各消耗一件，角色最终左缘 34718.83、脚底 3184，停在刚铺的支撑上。

**失效与预算。** 工具被移除、目标被外部更换、第二道墙变成不可清除和首格清障后取消均只留下已发生的一次障碍破坏。被替换目标标为 changed；未经开采的目标保留 pending。放置后取消保留一块已消耗支撑的回执。只有一块材料时，采矿未进行地形改造，耗尽原有 900 更新的接近预算后停止。以上任务均核对终态输入释放。

### 复现命令

`$world` 为受控专服原始存档，SHA-256 为 `918e33d72ae761f990caa31e0734dd04ab375b3b4f4582702fcbf087d00707f0`。`$ironWorld` 为种子 20261028 对应的自然世界副本，SHA-256 为 `a9feb51bb2b71baf4c04fec9c8617ec5683dc94cf7a0506eb200139802701223`。`$py` 指向项目 Lua 测试环境的 Python。脚本每次复制输入世界并打印结果目录。

```powershell
python tests/prepare_shared_navigation_baseline.py .local/shared-base
./scripts/build-companion.ps1 -OutputPath .local/shared-new
. ./scripts/engine-profile.ps1
$engine = Resolve-CompanionEngine
$src = (Resolve-Path .local/shared-base/CompanionBridge).Path
$out = [IO.Path]::GetFullPath('.local/shared-control')
Push-Location $engine.Path
try { & ./dotnet/dotnet.exe tModLoader.dll -server -build $src -tmlsavedirectory $out } finally { Pop-Location }
$baseline = Join-Path $out 'Mods/CompanionBridge.tmod'
$candidate = '.local/shared-new/Mods/CompanionBridge.tmod'
foreach ($package in $baseline,$candidate) {
    ./experiments/virtual-player-engine-hooks/verify-native-mining.ps1 -SharedRoutes -ModName CompanionBridge -WorldPath $world -PackagePath $package
    ./experiments/virtual-player-engine-hooks/verify-native-mining.ps1 -TerrainApproach -ModName CompanionBridge -WorldPath $world -PackagePath $package
    ./tests/verify_natural_navigation.ps1 -Mining -MiningCases change-stance -WorldPath $ironWorld -PackagePath $package -PythonExecutable $py
}
python tests/verify_shared_navigation.py --world $world --shared $sharedBase $sharedNew --mining $miningBase $miningNew --natural $naturalBaseJson $naturalNewJson --output .local/comparison.json
./tests/verify_server_ai_native.ps1 -Actions -NavigationSafety -NavigationSafetyCases surface_detour,drop13,long -WorldPath $world -PackagePath $candidate -PythonExecutable $py
./tests/verify_navigation_recovery_native.ps1 -Grapple -Scenes ledge,cancel -WorldPath $world -PackagePath $candidate -PythonExecutable $py
./tests/verify_navigation_recovery_native.ps1 -Wings -Scenes angel,bone -WorldPath $world -PackagePath $candidate -PythonExecutable $py
./tests/run_csharp_tests.ps1 -LocalEditsOnly
./tests/run_csharp_tests.ps1 -MiningSupportOnly
./tests/run_csharp_tests.ps1 -WorldJourneyOnly
./tests/run_csharp_tests.ps1 -GrappleOnly
./tests/run_csharp_tests.ps1 -AirOnly
./tests/run_server_ai_ipc_tests.ps1 -NavigationLeaseOnly
$env:PYTHONPATH = "python;tests"
& $py -m unittest -q test_mine_target test_excavation_contract test_navigation_recovery test_landing_navigation
```

**其余通过项。** 交付包构建为零错误、零警告。专服自然埋藏铁矿验证 5 格、埋藏铜矿验证 11 格、大矿脉验证 22 格；封闭矿脉按预算终止，保留 13 个待采目标并释放输入。普通地表绕行、13 格落差和 49.95 秒长途均在最终目标安全停稳，长途使用一个续程检查点。钩爪验证发射、挂接、脱钩落稳以及取消清理；天使翼与骨翼单段均确认消耗翼时并落稳。离线局部改造 238 项、采矿支撑 30 项、钩爪几何 14 项、空中运动 75 项、导航租约 4 项及 6321.1 格旅程通过。

**既有失败。** `-Wings -Scenes angel,bone_chain` 在两个版本均到达骨翼连续飞行的最终安全落区，但轨迹缺少中途停稳且翼时全满的帧，`native_wings.py` 的中途断言失败。Python 定向组 23 项通过 22 项，剩余的客户端 TCP 落区测试依赖 `-LandingProtocolHost`；直接在基线重放也因离线编译缺少 `CompanionBridge.ServerIpc` 引用而报 CS0234。以上分别保留为失败项。

**未执行。** 全量测试、全部装备组合、跨版本模组兼容和战斗压力测试。

## 失败落点、建筑保护与平台铺路

本轮直接基线为 `4144bf1929d5b5559a44fb18f70f5a12b863f9e9`。在 tModLoader 2026.09.3.1 真实专服上，受控场景复用相同的世界和附属存档副本。下表记录一次任务；耗时只累计根任务的接近规划和滚动地形评估。每次实际放置均有坐标、原生放置事件和一次扣料。单格与矿簇分别核验破坏者、命中和格子消失。混合背包将泥块放在更靠前的槽，结束时 8 个泥块均保留。[17 个场景的用料、终态和计时](../../tests/fixtures/protected-platform-navigation.json)

| 平台场景 | 任务更新 | 累计规划 ms | 最长片 ms | 消耗木平台 | 结果 |
|---|---:|---:|---:|---:|---|
| 目的地跨沟 | 255 | 227.30 | 13.04 | 3 | 到达并停稳 |
| 单格铁矿 | 296 | 113.58 | 8.48 | 3 | 采完 1 格 |
| 双格铁矿 | 323 | 115.40 | 4.85 | 3 | 采完 2 格 |
| 平台、泥块均可用 | 236 | 144.91 | 4.26 | 3 | 到达并停稳 |
| 已有平台起步 | 246 | 190.01 | 5.39 | 3 | 继续铺路并到达 |
| 终点在新铺平台 | 164 | 101.18 | 4.97 | 2 | 最终脚印覆盖新平台 |
| 宽沟矿簇 | 353 | 90.57 | 4.71 | 5 | 在沟内新平台上续铺并采完 2 格 |

**修改、拒绝和中断。** 目的地在天然泥墙上挖开 3 格。相同材料加保护区或有效住房背景墙后，目的地耗尽局部候选，墙体完整。采矿接近保护区时以搜索预算耗尽收束，2 格铁矿仍待采。运行中给正在开挖的泥墙加标记阻止了第一格破坏；受保护的沟内没有发生放置。椅子、箱子两列支撑、保护标记存档往返、普通开关门与导航通行均通过专服验证。

仅有 1 个平台时，矿簇任务在第 902 次更新用尽预算，库存保留。在原生放置已发生、动作仍未结算时分别移走余料、破坏平台和取消，三次都保留一次原生放置和一次扣料，两格目标均待采，输入全部释放。前两次返回 `unknown / placement_stack_changed`、`unknown / placement_target_changed` 并停止；后一回执的格子已空。取消返回 `cancelled / explicit_stop`，格子仍在。

**原生预测和失败记忆。** 平台水平行走 35 帧、从下穿过并落在平台上 46 帧、向下穿过并落在石面上 17 帧，每帧位置最大误差均为 0 像素，最大速度误差小于 0.000001 像素/更新。失败落点测试用实际路线搜索证明未变时拒绝、相关地形变化后可选。移动角色、滚动窗口、切换选中槽和编辑无关格均保留记录。更改一个落点的依赖保留另一落点的拒绝，修改可用材料重新开放候选；已记账的 87 次更新完整保留。

**既有定向对照。** 直接基线和本版配对验证 45 次任务，覆盖便宜绕行、快慢镐交叉、薄墙、连续双墙、实心支撑、预算、地形变化及取消。目的地双墙三轮中位数均为 373 次更新、3 次导航和 6 格实挖。实心桥目的地从 278 降至 229 次更新，采矿从 352 降至 287 次，均消耗 3 块；规划器可以继续执行自己切换材料槽后的路线。快镐直挖的中位规划时间从 82.49 增至 130.39 ms，仍然只挖 2 格，规划时间包括新增保护复核，未进一步拆分 CPU 开销。自然换位铁矿两版均为 4 次导航、5 批、15 格，零清障。更新数 1088 → 1091，累计规划 116.01 → 124.97 ms，最长片 12.40 → 18.22 ms。另一个自然大矿脉实采 22 格。

在仓库根目录执行以下定向命令。`$world` 为选定的测试世界，`$py` 为项目测试 Python，`$ironWorld`、`$largeWorld` 为固定的自然矿脉世界副本。比较器接受各脚本打印的 profile 或结果文件路径。

```powershell
./scripts/build-companion.ps1 -OutputPath .local/protected-platform
$pkg = '.local/protected-platform/Mods/CompanionBridge.tmod'
$native = @{ModName='CompanionBridge'; PackagePath=$pkg; WorldPath=$world}
$replay = './experiments/virtual-player-engine-hooks/verify-native-mining.ps1'
& $replay @native
& $replay @native -PlatformRoutes
python tests/verify_platform_routes.py $platformProfile
& $replay @native -SharedRoutes
& $replay @native -TerrainApproach
./tests/verify_natural_navigation.ps1 -Mining -MiningCases change-stance -WorldPath $ironWorld -PackagePath $pkg -PythonExecutable $py
./tests/verify_natural_navigation.ps1 -Mining -MiningCases large-vein -WorldPath $largeWorld -PackagePath $pkg -PythonExecutable $py
python tests/verify_shared_navigation.py --baseline-revision 4144bf1929d5b5559a44fb18f70f5a12b863f9e9 --shared $baseShared $newShared --mining $baseOre $newOre --natural $baseIron $newIron --world $world --output .local/protection-paired.json
./tests/verify_server_ai_direct_input_native.ps1 -TestScript native_door.py -WorldPath $world -PackagePath $pkg -PythonExecutable $py
./tests/verify_server_ai_native.ps1 -Housing -Actions -WorldPath $world -PackagePath $pkg -PythonExecutable $py
./tests/run_csharp_tests.ps1 -LocalEditsOnly
./tests/run_csharp_tests.ps1 -MiningSupportOnly
./tests/run_server_ai_ipc_tests.ps1 -NavigationLeaseOnly
```

构建零警告、零错误。原生采矿和预测夹具通过 212 项检查，8×8 住宅施工验房、局部改造 238 项、采矿支撑 30 项和导航租约 4 项通过。开发中的记忆测试曾干扰后续测试的装备槽，恢复夹具的原始装备后复跑通过。未运行全量、图形客户端联机、斜平台预测和跨版本 Mod 平台兼容测试。
