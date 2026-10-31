# Lua 的服务器 AI 移动、使用与目标格工具操作

当前增量：PR #25 / CompanionBridge **0.1.60** 新增原生配方查询与合成，见[合成接口、材料/站点/环境边界及实测](SERVER_AI_CRAFTING.md)。

前序增量：**0.1.58** 接通基础 `place_tile`，见[原生放置与验证](SERVER_AI_PLACEMENT.md)。
历史增量：**0.1.51** 增加铜斧空挥及原生树干砍伐。
实现、分层证据和实机步骤见 [R2/R3 验证说明](R2_R3_COPPER_AXE.md)；下方历史验证不代表本轮图形验收。
已补录 0.1.51 的 Host & Play 基础采集通过记录；**0.1.52** 将成功/失败原因码与诊断详情分离，见 [回执原因说明](ACTION_RECEIPT_REASONS.md)。
**0.1.53** 收紧导航移动交接点并增加有界落地确认；未确认以unknown返回，详见 [东向导航修复](NAVIGATION_LANDING_CONFIRMATION.md)。输入释放与角色停稳分别报告。

基线：PR #22 `899417b`；独立分支 `codex/server-ai-actions`，正式包 `0.1.44`。
本次在既有 G8.2 读接口和动作执行适配上扩展，不引入另一套 Lua Runtime、
Schema、调度器、Agent Prompt 或真人控制入口。历史报告和实验 profile 保留。

## 接入范围

| 原子操作 | 语义和结束证据 |
| --- | --- |
| `navigate_to_x {targetX=...}` | 玩家左上角水平世界像素；复用原有滚动导航、局部规划、物理预测与限制。原生实际位置/速度、地面支撑及原导航 arrived 判定确认后结束；30 秒上限 |
| `navigate_to_landing_region {minPlayerLeftX=...,maxPlayerLeftX=...,surfaceY=...}` | **0.1.57**：玩家左边缘 X 闭区间及脚底表面 Y，均为世界像素；复用同一二维规划器与原生移动链。实际进入区域、安全平面支撑并停稳后完成；30 秒上限，见[二维落脚](SERVER_AI_LANDING_REGION.md) |
| `use_item {}` | 当前真实选中物品完成一次 G6 原生使用周期；白名单 WoodenSword、CopperPickaxe、CopperAxe、Apple。必须单次启动、观察到原生动画并自然耗尽计时；3 秒上限。完成不证明命中，`effectStatus=unconfirmed`；原生未启动为 failed / `not_started` |
| `mine_tile {x=...,y=...}` | 固定 Tile 坐标，当前纯镐挖普通方块或 SmallPiles（185）环境碎堆，或铜斧砍原生斧类树干；保留 80 像素保守检查和 G7 原生距离/工具/保护规则。本 AI 原生工具链必须破坏指定目标、无外部替换且目标仍为空；10 秒上限。树的连带倒下和掉落由原版处理 |
| `place_tile {x=...,y=...}` | **0.1.58**：用当前选中的泥土块、石块或木材向空 Tile 发起一次原生放置；保留 80px 与原生范围/邻接/保护规则。目标由本 AI 原生放成、同次 ItemCheck 消耗 1 件、计时结束并释放后完成；10 秒上限。未确认消耗的原生放置为 unknown，不回滚或重试 |

`select_hotbar_slot`、`get_player_state`、`inspect_inventory`、`wait`、`stop_action`
沿用。游戏快捷键 1–9、0 对应内部栏位 0–9。工具操作不自动选工具、移动或
派发拾取动作；普通镐挖不挖邻格，砍树允许原生整树连带变化。**0.1.56** 已移除
早期 G7 的拾取抑制，挖掘期间继续原生被动拾取；`native_tile_destroyed` 不代表木材已进入背包。

明确未接入：按 Down 直接穿平台、定向 `use_item.target`、其他物品类型、
攻击、其他放置类型（家具/平台/墙等）、墙/锤击、背包修改、额外感知工具。未知操作在协议/后端白名单处拒绝；
有 target 的使用请求明确返回 `targeted_use_not_supported`，不会忽略目标后执行。
导航继续拒绝现有模型不支持的装备、Buff、移动配置和地形；吃食物后可能因此拒绝导航。

## 复用链路与安全边界

```text
既有 Lua Worker -> atomic_operations.dispatch -> ServerAiBridge
 -> 独立 AI IPC 队列 -> 游戏线程/Gate -> AiAtomicActions
 -> ServerAiNativeActions -> G4 自然移动边界 / G6 / G7
 -> 原生 Player.Update、ItemCheck、PickTile/HitTile
 -> 游戏线程观察实际结果、释放输入 -> 既有 terminal 校验器 -> Lua
```

- 原 `GameNavigation` 主体改为可实例化 `NativeNavigation`，真人静态入口仍使用
  自己的实例；服务器实例使用同一个原有算法，规划状态不跨角色共享。
- `ActorControl` 只增加窄移动 driver，在原 G4 `Update` 和
  `PostUpdateRunSpeeds` 边界接入服务器导航。每个自然更新仅调用一次原 Update；
  finally 释放输入，明确停止沿用 G4 的水平制动，不设置位置/垂直速度。
- G6/G7 主体、原生补丁和同步不重写；只将忙碌检查扩展到新导航 driver。
  没有直接调用 `WorldGen.KillTile`，没有修改 `Main.myPlayer/Main.LocalPlayer`。
- 网络线程仍只交换有界文本和队列任务。绑定连接 lease 后，每次请求再次检查实际
  Player 引用、WorldSession、Generation、Slot；原生执行前和观察时也检查归属。
  不回退真人角色，不重试未知结果；世界切换/对象替换后的旧请求拒绝。
- 三类动作与原选栏共用既有有界动作表、互斥和 actionId。销毁/死亡沿用 G1–G7
  Retire，断开/取消/超时在游戏线程释放对应 context。卸载清理异常进入 quarantine，
  不能把清理失败标成完成或释放未知角色槽位。

参考了 [Numen ADR 0005](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/docs/adr/control/0005-atomic-commands-composed-by-scripts.md)
的单一意图、Lua 组合职责，以及本机 TerraPlayerBot 反编译的 BotWorldActions /
BotNativeBehaviorCompatibility 控制与挖掘机制。实际执行使用本仓库 G4/G6/G7，
未复制这些项目的执行系统。已复核固定引擎原生调用链；未知 DLL 指纹继续拒绝安装。

## IPC 与证据契约

仍是 loopback `18779`、协议 v1。新增窄能力 `server_ai_actions_v1`，仅安装原生
执行适配器时发布；旧服务不宣称支持。Python 看到该能力才开放原三个操作；二维落脚
还必须同时具备 `landing_region_navigation_v1`，旧后端不会因 Schema 已存在而开放新动作。
基础放置还必须具备 `server_ai_place_tile_v1`；`use_item` 的旧物品白名单没有因此扩充。

```json
{"v":1,"id":"start-1","op":"start","binding":{"actor":"...","world":"...","generation":1,"slot":254},"actionId":"32位小写UUID十六进制","operation":"mine_tile","arguments":{"x":2109,"y":217},"expiresAtUnixMs":0}
```

示例中的身份/坐标需来自本次会话，expiresAtUnixMs 需为当前 UTC 毫秒值加 1–2000，
示例零值会被拒绝。请求有效期与执行预算不同：已接受导航仍最多执行 30 秒。
`action`/`cancel` 携带同连接 binding 和 actionId；没有跨连接接管或重绑定。
`renew_move` 在 Python 后端映射为观察，不延长服务器绝对执行期限。

接受先返回 `status=running, executionState=accepted`，后续为 running，最后 terminal；
不能凭接受回执、动画或单次 native 调用判 completed。结束状态沿用
completed/failed/cancelled/timeout/unknown。原 select 的 no-input 证据保留；三类
新动作使用 `ServerAI_game_thread_inputs_released`，附 operation、游戏更新序号、
world/actor/generation/slot、inputsAcquired、inputsReleased 和 effectVerified。

Python terminal 除身份/世界检查外，核对释放证据、operation 和分别对应的
arrived / cycleVerified / destroyedByActor+targetAbsent+未替换。取消清零动画计时
不会产生 cycleVerified。挖掘观察单独提供挥动、镐击、进度峰值、目标变形、原生破坏
及目标现状；部分进度取消不会变成成功。错误、未知清理或证据缺失保留 unknown，
既有 Lua 预算、取消、证据存储和会话 fencing 不变。

## 自动化与原生验证

本次没有运行全量测试。定向验证命令（脚本测试需 PowerShell 7/.NET；Windows
沙箱外允许本机 loopback/worker 线程）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-companion.ps1
pwsh -NoProfile -File tests/run_server_ai_ipc_tests.ps1 -PackagePath .local/companion-g82-build/Mods/CompanionBridge.tmod
pwsh -NoProfile -File tests/run_csharp_tests.ps1 -BridgeOnly
pwsh -NoProfile -File tests/run_csharp_tests.ps1 -MotionOnly
pwsh -NoProfile -File experiments/virtual-player-engine-hooks/verify.ps1 -MiningOnly
$env:PYTHONPATH="$pwd/python;$pwd/tests"
& .local/lua-venv/Scripts/python.exe -m unittest test_server_ai_backend test_atomic_operations test_atomic_result_contract -q
pwsh -NoProfile -File tests/verify_server_ai_actions_native.ps1
git diff --check
```

结果：构建 0 error / 0 warning；IPC 45、原选栏 40、读协议 15、新执行替身 28
项检查通过；正式包 31 项 IL 检查通过。Python 后端 21 个测试、原分发器/动作结果
契约 26 个测试通过。原真人 TCP/输入/死亡、运动/落点等所调用定向组通过。
G6/G7 严格 native IL 19/22 项、包内 G6/G7 30/29 项及该入口的既有前置检查通过。
这些是离线替身、碰撞模拟或真实 DLL 的结构检查，不能替代图形多人验收。

隔离原生结果和可核查数字见 [原生证据](evidence/SERVER_AI_ACTIONS_NATIVE.json)。
使用复制的 test.wld、独立端口 27861/27862、仅 CompanionBridge，并由真实 Lua
Worker 和 IPC 驱动真实 AI。未启动图形客户端、未连接/停止用户现有服务器。
日志扫描 server*.log，但只保留本次 PID、世界会话和进程时间窗口的事件。
正常退出、角色清理、reservation 释放也要求本次事件。

**无客户端的原生服务器会暂停 Game.Update。** 因此隔离动作测试显式使用
`-companionactionstest`、`-companionaiipctest` 和复制 profile 内 marker 三重入口。
测试 fixture 在复制世界建立平地/石块并提供真实工具；在原服务器主线程调用完整
原生 `Main.Update(GameTime)`，与已审查 `DedServ_PostModLoad` 的虚调用一致。
没有手动调用 Player.Update、伪造客户端或修改 HasClients。生产和 Host & Play
profile 不开启此入口；没有真人连接时，生产动作只会等待原生帧或按期限停止。

原生测试覆盖 Lua 移动、挥剑、苹果消耗、铜镐挖石块、实际掉落，以及三类动作进入
原生更新后的取消、部分挖掘进度、空目标拒绝。掉落在真实破坏帧记录；结束后的原生
空闲拾取可能使最终世界物品列表变空，记录最终背包与该事实，不伪称掉落仍在地面。
苹果核验 stack 2→1，不把自然生命变化归因为苹果治疗。

开发中修正的验收问题：Lua 返回表混合数组/对象索引、错误预期苹果必定回血、取消
目标超出原生镐距、以及结束后才找掉落导致空闲拾取的竞态。均修正测试/证据采样；
没有放宽生产距离、物品使用或掉落规则。取消证据亦专门验证强制清理不等于自然周期完成。

## Host & Play 手动验收

以下为可复现步骤；2026-10-27 已由用户完成三项基础实机验收，证据范围见下节。
其余扩展场景仍待测试。先退出旧测试实例，再建立新的隔离 profile；不要覆盖旧
profile 或正式存档：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-companion.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/prepare-companion-host-play.ps1 -SeedProfile .local/virtual-player-single-window-host
# 手动运行刚打印的 start-host-play.ps1，选择 Multiplayer > Host & Play
& .local/lua-venv/Scripts/python.exe scripts/server_ai_verify.py
& .local/lua-venv/Scripts/python.exe scripts/server_ai_actions.py --help
```

1. 先确认读结果绑定同一个 AI，并由真实背包确认工具所在快捷键。正式模式不发测试
   工具；可以在测试世界让 AI 原生拾取玩家交付的木剑、铜镐。空栏会明确失败。
2. AI 附近准备平坦、安全、无敌怪的地面；真人保持静止。读取 AI position.x，取其
   加 48 为明确目标，例如实际读取为 33616 时才使用 `--target-x 33664`。运行
   `scripts/server_ai_actions.py --target-x <实际目标像素>`；观察仅 AI 行走、停稳，
   对照结果和再次读取的位置。先测右、再测左；跳障碍另作待验收项目。
3. 用 `--use-key <木剑实际快捷键>` 观察单次 AI 挥剑、动画结束，真人不挥剑；检查
   cycleVerified 和 released。具体命中、伤害未在本轮证明。
4. 坐标是世界 Tile 格，1 格=16 像素，原点左上、Y 向下。AI 身体中心所在格约为
   `floor((position.x+10)/16), floor((position.y+21)/16)`。这只是身体中心参考格，
   **不是画面中某块方块的自动定位结果**；脚边方块可能比该格低一行。先确认附近
   实际测试泥土/石块的占用格，不要指定脚下支撑。以
   `--pick-key <铜镐实际快捷键> --mine-x <tileX> --mine-y <tileY>` 执行，观察
   目标真正消失、邻块不挖，结果 destroyedByActor/targetAbsent 为 true。
   原生距离可能比 80 像素更严格，不可达会拒绝，不会自行走近。
5. 重复指定已挖空格应 failed；选择木剑挖掘、超距目标应拒绝。用 Lua 运行取消
   或切断自己的 AI IPC 连接，检查停止后没有残留移动/使用输入。完整原生自动化已
   检查三类取消，图形显示/包到达仍需人工观察。

## 2026-10-27 Host & Play 实机补录

本次由用户手动启动 `.local/companion-g82-host-20261027-111108-230/start-host-play.ps1`，
进入图形 Host & Play 并执行现有 Python/Lua 验收入口。包为 `0.1.44`，SHA256
`071EDEAA49F35FDF88EEFB7649C2F0B77F7BB97328AAF9203F948A2C75A00E26`。
用户在会话中逐项报告成功，并在最终确认指定石块消失、南瓜和支撑地面保留。
这是**用户操作和视觉确认 + 实际动作回执复核**，不是代理自动操作图形客户端的测试。
与前节隔离原生服务器证据分别保存：[Host & Play 证据](evidence/SERVER_AI_ACTIONS_HOST_PLAY.json)。

三个成功程序绑定相同 AI：actor `2635ed5b6f4b4a1cba3e90dfc2e95f2e`、world
`1d1984b9c6214927aa89da9644729da5`、generation `1`、slot `254`。
它们是独立 CLI 连接，不构成持续旧连接跨生命周期测试。

| 实机项目 | 本地回执及用户确认 |
| --- | --- |
| 向右导航 | `--target-x 33728`；x `33680 → 33730.395`、y `3318`，44 次原生更新，最终速度 `(0,0)`，arrived=true；用户确认成功 |
| 木剑单次使用 | AI 原生拾取后木剑位于内部 slot 1 / 快捷键 2；`--use-key 2`，选栏与使用共 2 项动作，Starts=1、AnimationPeak=20、Ticks=21、cycleVerified=true；用户确认成功。命中/伤害仍 unconfirmed |
| 铜镐挖指定石块 | 铜镐内部 slot 2 / 快捷键 3、镐力 35；`--pick-key 3 --mine-x 2111 --mine-y 209`，选栏与挖掘共 2 项动作。46 次更新、2 次挥动、3 次镐击、PeakDamage=105；nativeDestroyed/targetAbsent/destroyedByActor=true、targetChanged=false；用户确认石块消失、南瓜和支撑地面保留 |

三份成功结果均为 status=completed、failure=null、actionState=released，原生动作
endEvidenceStatus=known、inputsReleased=true。AI 生命保持 100；各动作结束的
gameUpdate 分别为 13120、32608、68843。没有把接受回执、挥动或进度单独计为成功。

同时记录两次实机拒绝，以免只保留成功结果：

- 初次站位只有 4px 有效脚底支撑，低于既有 8px 门槛；导航在起点 `(33680,3318)`
  拒绝，arrived=false、输入释放。用户补齐完整地面后再次导航成功，未放宽安全阈值。
- `(2111,208)` 为空格，返回 InvalidTarget 并释放输入。后续只读检查确认画面中石块
  在 `(2111,209)`；修正目标后挖掘成功。外层 script_error 来自验收 Lua 的 assert，
  对应动作本身为明确 failed，不是未知执行结果。南瓜本身未作为挖掘目标验证。

本次没有单独验证图形中的掉落生成/拾取、苹果使用、动作中取消/超时/断开、左移、
原生跳障碍、不同地形/工具/保护目标、真人持续并行操作、持续旧连接跨真实角色代次/
世界切换拒绝、动作中死亡重生、远程多人和晚加入同步；这些仍为**未验证**。
隔离原生测试中的掉落、取消、退出/角色清理/reservation 释放也不能据此升级为
Host & Play 通过。旧阶段读/选栏实机结果与本次三项动作结果分开记录。

本次补录只更新文档与保存的证据，没有修改功能实现、重新启动游戏或重跑测试。
PR #23 保持 Draft，不合并。


## 只读采矿支撑影响评估 V1（2026-10-30）

服务器 `inspect_tile({x,y})` 增加可选 `data.miningSupportImpact`；参数和原有查询/采矿协议不变。旧后端缺少此字段表示未提供评估，不等同于无风险。客户端查询不增加此字段。

```lua
local r=api('inspect_tile',{x=2197,y=231})
local impact=r.data and r.data.miningSupportImpact
-- 先检查impact存在及impact.available；current/afterRemoval可能为json.null。
-- 不据此自动挖掘或假设能够下落/返回。
return impact
```

字段契约：

- `targetX/targetY`为Tile坐标；`pose`为采样时20×42普通重力身体的左上角px/py、速度vx/vy及原grounded/jump状态，单位世界像素，Y向下；绑定身份、gameUpdate、UTC仍在原查询信封中。
- `current`和`afterRemoval`各含`supportPixels`及`rejection`，复用MotionSupport原有身体净空、坡/半砖/平台表面和支撑扫描；达到8px时rejection可为null，低于时保留原解释。无法完成支撑扫描时supportPixels为null，未读区域不当作空地。
- `available=true`仅表示两边支撑数值均可评估，不是允许采矿或证明站位/导航安全。`affectsCurrentStance`比较原姿态的支撑宽度与该扫描拒绝信息，未知时null；`supportDeltaPixels=afterRemoval-current`，未知时null；`minimumSupportPixels=8`仅解释已有导航支撑阈值，不作为新的采矿门槛。
- `reasons`解释无法判断的情况，如未知目标/邻格、液体或危险未建模、身体相交、非grounded、非20×42或不支持玩家profile/重力；即使available为true，current/afterRemoval.rejection仍可能指出已知的支撑不足。
- `scope`明确只比较冻结当前姿态、grounded标志和邻格的单格移除反事实。每次查询重新采样，每个采样类别在本次比较中只读取一次；不是整个世界的原子快照，原inspect_tile与评估也是游戏线程内的先后读取。

评估通过只读委托覆盖目标分类为非阻挡，不写Main.tile，不调用原生运动/挖掘，不推进世界。未知目标不被强行清空；含液体/危险时不会把它们随Tile一并删除。未模拟坠落、落点、连锁破坏/掉落块、其他Tile联动或返程路径，也不核验工具、距离与挖掘许可。背景墙、液体、邻格等实际状态不会被修改。

本轮未改ActorMining、ServerAiNativeActions的执行/拒绝规则、Lua失败/unknown屏障或导航阈值。仍需由调用者显式选择动作并检查真实回执；评估只读0动作。v6证据表明两次移除在原姿态下都是16→0px，而4px来自下落后的另一姿态；2.562px是另一位置的独立拒绝，不能把它们当作本评估的落点预测。[历史核对、派生fixture和定向结果](evidence/mining-support-v1/analysis.md)。

复现：`tests/run_csharp_tests.ps1 -MiningSupportOnly`、`-NavigationOnly`；Python仅`test_mining_support_projection.py`；原生只用隔离placement/mining脚本及本轮构建包。具体包SHA-256、原始日志见[证据manifest](evidence/mining-support-v1/manifest.json)。
