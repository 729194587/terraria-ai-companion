# Exploration V2 实现与实测

在 Terraria 1.4.4.9、tModLoader 2026.09.3.1 的独立服务器上，V2 走过了同一迷宫的下层东支、上层东支和上层西支，也实际挖穿了相邻房间的竖墙。两个自然世界均获得了逐秒位置轨迹。第一个世界两种算法到达近似相同的最深处，第二个世界出现战斗和换气中断。方向偏好、局部拓扑和实际导航能力在这些场景中分别起了作用。

## 实现与状态

| 阶段 | 行为及证据 |
| --- | --- |
| 1. 实际访问与候选 | AiMap v4 为每个有脚印的 32×32 块保存位图；按角色实际脚底中心采样，相同位置只计一次，瞬移两端之间不插值。照亮的地形独立保存。旧 v1–v3 地图升级时从空的访问历史开始。Host 从原生 landing regions 和 ScenePassages 的几何 flood fill 取候选，按世界对齐的 12 格带、高度与连续区间聚合。墙两侧和不同楼层可产生不同目标。 |
| 2. 目标与失败 | Visit Frontier 需目标区间的实际脚印。Map Frontier 需指定的未知见证格已被揭示。Host 在世界 UUID、角色 ID 对应的 SQLite 中保存活动、待探索、暂缓与已调查目标，以及资源发现。失败保存目标周围 5×6 格地形、接近位置和原因。默认冷却 90 秒，也可以从对侧重新尝试；局部地形不变时最多三次。连续四次没有目标证据返回检查点。 |
| 3. 共享控制器 | Lua 的 `explore()`、自主模式和 MCP 的 `explore({})` 使用同一循环。可选 `direction` 仅用于候选之间的宏观偏好，`seek` 可请求发现后返回。先考虑自然连通落区，再复核旧支路、其他落区与地图候选，实际移动和地形改造均由 `navigate_to` 完成。软预算 45 秒，已派发的一段可继续；外层 180 秒保护会取消并核验终态。跨调用重新观察，保存档中没有可执行租约。旧协议端保留方向入口。 |
| 4. 原生验证 | 可控地形、自然世界固定决策对照，以及由子智能体通过本地 stdio MCP 自行选工具的自由驾驶，全部使用真实 Server AI Player。 |

接口细节见 [AiMap](../AI_MAP_V1.md)、[Lua](../LUA_AGENT.md) 和 [MCP](../TERRARIA_EXTERNAL_BRAIN_MVP.md)。[原设计](EXPLORATION_V2_DESIGN.md)列出了各阶段的验收条件。

## 可控地形与反射

| 场景 | 实际行为 |
| --- | --- |
| 洞穴 | 无方向调用在 197.61 秒内派发 7 段，5 个目标取得调查证据，轨迹最大下降 10 格、横移 54.2 格。到过右支，未证明进入左支。 |
| 折返与上下双支 | 4 次调用、15 段，7 个目标有实际脚印。先发现铜，`mine_target` 在 (2103,219) 返回 `mine_target_verified`。重新探索后，70.99、85.17、144.08 秒的轨迹分别进入下层东、上层东、上层西的独立验收区。局部失败后，先前保存的目标 4 被重新选中并调查成功。薄地板下的密室未进入。 |
| 竖向隔墙 | 首先到达本侧自然连通目标。第二段选择另一侧不连通目标，任务 `1fff5a105af146a1bb9774cfcc902da1` 核验挖除 3 格，最终脚底中心越过墙并留下访问记录。该轮 13 段全部确认释放，4 个目标有调查证据。 |
| 投射物与战斗 | 自然世界出现同一任务 ID 从 `navigating` 进入 `task_interrupt_combat` 并继续导航。受控飞弹试验中，导航 `c284408b4eb94d60a7fa3f17bb00f130` 和接手防御的 `5ecc1c28b7cc43ba8f84a3062c9fc1f8` 均完成并释放输入。 |
| 突发进水 | 初测与时序复测均在水淹头部后返回 `native_navigation_landing_unconfirmed`，虽已释放输入，调查仍失败。修订在低氧且有活动导航时于 Player.Update 前向现有换气反射检查实际头部碰撞。原始进水场景复测中，任务 `0881db03301a4792ae9b7d0af0a3251d` 经 `paused/task_interrupt_breath → running → navigation_destination_verified`，同一任务释放输入，实际新增 16 个脚印、完成 1 个目标。同一专服随后通过采矿换气回归。 |

[轨迹与逐任务回执](../../tests/fixtures/exploration-v2-field.json.gz)包含关卡验收区和地形改造计数。[反射证据](../../tests/fixtures/exploration-v2-reflex.json)包含修订前后的回执、暂停阶段、低氧及重新接触空气时的采样、原始文件 SHA-256。

## 自然生成世界对照

每对运行从相同世界文件的独立副本、相同坐标和 100 生命开始。开荒工具为原生铜短剑、铜镐、铜斧，统一为无前缀。两算法使用相同观察权限、向右的固定宏观偏好及 300 秒外层调用预算，无客户端时驱动原生游戏帧。保留敌怪、液体、昼夜和死亡机制，无夹具传送或地形注入。到期停止启动新调用；耗时包含此前已启动的调用及其收尾。危险中断可以提前结束。配对按世界文件与模组包的 SHA-256 校验，敌怪行动没有锁步同步。

| 种子 | 算法 | 实际秒 | 调用 | 导航完成 / 派发尝试 | 新增脚印格 | 最大横移 / 下探（格） | 终止 |
| --- | --- | ---: | ---: | ---: | ---: | ---: | --- |
| 20261127 | 原方向 | 305.73 | 17 | 16 / 31 | 401 | 254.8 / 48.0 | 检查点，15 次调用达到原单段时限 |
| 20261127 | V2 | 304.36 | 4 | 41 / 45 | 502 | 254.9 / 48.0 | 检查点，40 个目标有调查证据 |
| 20261028 | 原方向 | 92.02 | 13 | 8 / 13 | 290 | 183.6 / 19.0 | 战斗目标失效，4 次派发未受理 |
| 20261028 | V2 | 356.58 | 6 | 78 / 79 | 1091 | 256.3 / 40.0 | 76 个目标有证据，换气找不到出口，取消并释放 |

以上两对使用同一模组包（哈希前缀 `0df7b41fbe1f`）。第一个种子中 V2 减少了返回宿主的次数，获得了更多独立脚印，最深位置与基线相同。第二个种子的运行时间差异很大，不能由累计脚印数推算单位时间优势。表中最大位移由约每秒一次的原生位置样本计算，脚印数取游戏端实际访问累计值之差。旧控制器没有 V2 的目标调查计数。

换气修订后，用最终模组包（`a4e5d497220b`）再次配对运行 20261028。V2 在 125.67 秒完成 32/33 段、新增 327 个脚印，最大横移 183.8 格、下探 19 格，战斗目标失效后取消并释放。旧算法的最后一个位置样本生命值为 0，最大横移 188.3 格、下探 42 格；162.69 秒时下一次资源观察返回 `stale_binding`，验证进程以错误结束。其 11 次派发中 8 次完成、3 次未受理，清理回执确认租约释放。角色绑定已失效，终场脚印数缺测。这次旧算法下探更深，也实际死亡，说明单个种子上的一次结果不足以判断生存能力或探索深度的稳定提升。

## 自由驾驶与资源调查

子智能体根据真实 MCP 回执自行决定后续动作。初轮在发现锡后查询矿点，随后 `mine_target` 因 `action_busy` 未受理。Lua 曾把这类无 actionId、未取得输入的拒绝错误记为未知，MCP 因此中止。修订仅对明确的未受理回执保留先前的身体状态。缺少终态或释放证据时仍停止派发。

复测持续 300.31 秒。子智能体先无方向探索，再用 `inspect_tile` 和 `scan_tiles` 检查铁矿，两处抽查均确认是铁，区域扫描返回 89 格铁矿。开采请求 `38a75a96276e4be1a3e5c3f492776aef` 已受理，因搜索窗口中没有开挖方案而结束并释放。随后向右的探索因身体忙未受理，下一次带向下偏好的探索完成。3 次探索共 40 次导航派发尝试，其中 39 段完成，1 次未受理；新增 616 个脚印，最大下探 48.5 格。此轮验证了调查后续接；成功采矿后的续接由可控迷宫提供证据。自由驾驶使用 Codex 子智能体和本地 MCP，未调用 DeepSeek。

## 复核

从仓库根目录运行。Python 使用安装了项目依赖的环境。以下均为定向检查。

```powershell
./scripts/build-companion.ps1
./tests/run_ai_map_tests.ps1
python -m unittest discover -s tests -p test_exploration_v2.py
python -m unittest discover -s tests -p test_spatial_controller.py
python -m unittest discover -s tests -p test_directional_explorer.py
python -m unittest discover -s tests -p test_autonomous_exploration.py
python -m unittest discover -s tests -p test_external_brain.py -k test_explore
python -m unittest discover -s tests -p test_ai_map.py
python -m unittest discover -s tests -p test_server_ai_contract.py -k helper_dependencies_and_runtime_limits
python tests/exploration_evidence.py tests/fixtures/exploration-v2-field.json.gz
```

C# 编译 0 错误、0 警告。AiMap 的 25 项检查和独立进程读回通过。上述 Python 文件或过滤依次通过 7、5、18、6、6、3、1 项。压缩包可离线检查目标回执对应、终态释放、对照起点与实际分支进入。每条轨迹样本依次为经过秒数、游戏更新号、角色左边 X、脚底 Y（像素）和生命值。每次运行保留 profile 名及原始结果哈希，可在本机 `.local/<profile>/` 复查。

```powershell
./tests/verify_server_ai_native.ps1 -DirectionalExplore spatial -KeepProfile
./tests/verify_server_ai_native.ps1 -DirectionalExplore maze -KeepProfile
./tests/verify_server_ai_native.ps1 -DirectionalExplore divider -KeepProfile
./tests/verify_navigation_recovery_native.ps1 -Liquid -Scenes breath_explore,breath_work
$env:COMPANION_COMBAT_CASE='work-bolt'
./tests/verify_server_ai_direct_input_native.ps1 -Combat -KeepProfile
```

自然对照分别指定 `-ExplorationAlgorithm directional` 与 `-ExplorationAlgorithm v2`，并向两个命令传入同一个 `-WorldPath`。首轮无现成世界时，可先按种子生成，保留该次 profile 中的 `source.wld` 供后续使用。自由驾驶的 `-ExplorationSeat` 需要一个新目录名；测试进程创建该目录并以本地 stdio 连接真实 MCP Host。驾驶端按每次响应决定下一调用。

```powershell
./tests/verify_natural_navigation.ps1 -WorldPath $worldPath -Seed 20261127 -ExplorationSeconds 300 -ExplorationAlgorithm directional
./tests/verify_natural_navigation.ps1 -WorldPath $worldPath -Seed 20261127 -ExplorationSeconds 300 -ExplorationAlgorithm v2
./tests/verify_natural_navigation.ps1 -WorldPath $worldPath -ExplorationSeconds 300 -ExplorationAlgorithm driver -ExplorationSeat $newSeat
python tests/exploration_driver.py $newSeat explore '{}'
```

## 尚未验证或解决

- 在薄地板下方看见密室，并未促成实际下挖进入；竖墙通过的结果仅适用于已实测的改造场景。地图落点、照明边界或可见资源仍可能超出现有导航和开挖搜索能力。
- 本批自然轨迹完成的是 Visit Frontier。Map Frontier 的未知见证格生成、揭示与无关观察去重已经过定向测试，自然世界中尚缺一个由它主导且成功的实测段。
- 两个种子均为小样本。自然世界分支缺少受控迷宫一类的独立验收区，不能单凭脚印、横移或下降量判定进入了几个不同洞穴。
