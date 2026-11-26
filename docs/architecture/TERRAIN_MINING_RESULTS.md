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
