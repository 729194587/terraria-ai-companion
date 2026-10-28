# 单窗口 Host & Play 自主探索 MVP

沿用 PR #25 的 G8.5 开发线。当前客户端/服务器使用 CompanionBridge **0.1.49**，
支持本地普通聊天直接进入Agent，`!ai`仍兼容。旧0.1.48需要保存退出、换新包并重启。
本机 `.local/start-client.ps1` 已指向用已保存测试存档生成的新profile，旧profile保留。
main、PR #24 不变，PR 保持 Draft。

## 启动与玩家入口

已经加载0.1.49时可继续运行游戏。先 Ctrl+C 停止旧 Agent，等待输入清理后，在项目根目录运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/start-server-ai-agent.ps1
```

使用现有 `.env`，不增加模型客户端或配置文件；默认聊天18777、AI18779，可用入口参数
`-Port`、`-AiPort` 修改。不要同时启动两个接管同一聊天通道的 Agent。

如果游戏尚未启动，本机已准备的 G8.5 客户端入口是：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .local/start-client.ps1
```

进入 Multiplayer → Host & Play → mio → test 后再启动 Agent。新环境没有该本地快捷入口时，
使用 `scripts/build-companion.ps1 -OutputPath .local/companion-g85-build`，再用
`scripts/prepare-companion-host-play.ps1 -PackagePath .local/companion-g85-build/Mods/CompanionBridge.tmod`
（可选SeedProfile）生成新profile，手动运行其打印的start-host-play.ps1。不要复制诊断原生profile当正式入口。

在**本地玩家自己的游戏聊天**中输入：

```text
开始探索附近，遇到危险就停下
向右探索附近，最多走4段，不要挖掘或使用物品
向左探索附近，最多走6段
停止
```

一个探索指令授权本轮连续观察与移动，模型调用一次 `execute_lua`，例如：

```lua
return explore({direction='auto', maxSteps=6})
```

中间各段由同一 Lua 程序自行完成，不等用户逐段下指令，也不需要逐段调用模型。
有界探索停止后回复实际结果并等待新指令，不在后台永久运行。
普通观察/移动指令不代表探索授权。新本地普通聊天会取消旧轮；`停止`不启动新的探索。普通问候只对话，不代表移动授权。
旧`!ai`前缀仍可用；其他玩家、系统消息、AI回显不成为新任务。斜杠/非Say游戏命令不进入Agent。
Ctrl+C 仍走原 Agent 关闭与动作清理。

## 执行方式与复用

```text
本地普通探索聊天 → 原 Agent / LLMClient → execute_lua
  → 原 LuaRuntime / Worker → explore(options) 纯 Lua 组合函数
  → 原 get_player_state / observe_terrain / resolve_content / scan_tiles / scan_entities
  → 选择局部目标 → 原 navigate_to_x → 核验 known 结束回执 → 读实际状态和地形
  → 更新本轮进度记忆 → 下一段或停止
```

`python/lua/explore.lua` 是随 Runtime 提供、可审阅的 Lua 策略函数，不是新的游戏原子API，
不能写 `api('explore', ...)`。它没有 Bridge、线程、网络、文件或新游戏执行器。
字符串单格访问只作为trusted helper的局部函数传入，没有向模型开放string库。
所有游戏操作仍经过原Schema/能力白名单、Lua所有权、共享预算和服务器身份栅栏。
只有后端同时支持完整感知和导航时，模型契约才介绍探索helper。

复用 `TerrainReader` 45×29碰撞图、`AtomicQueries`扫描/解析、原NativeNavigation、
动作接受/运行/终态/known释放回执、ServerAiBridge绑定、Agent新轮取消/旧回复栅栏、
原Runtime清理和诊断日志。没有接入依赖真人的旧continuous_walk控制入口。

对照了 Numen 的 [ADR 0005](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/docs/adr/control/0005-atomic-commands-composed-by-scripts.md)：
原子动作处理自身生命周期，秒级观察/选择/顺序进入脚本；固定流程可作为随项目发布的内置脚本。
沿用既有Lua54沙箱与预算，没有迁入其Minecraft执行器或另建Agent。

## 循环、记忆和调整

初次读取实际AI状态和地形，绑定本轮世界/角色代次/槽位，并解析普通树类型。
每段移动前扫描与本段允许步幅关联的保守分类敌对NPC包络，扫描局部普通树格作为有限地标。
地标按(x,y)去重，最多32格，保留类型、实际采样更新号和UTC毫秒。
**树木格数不是树的棵数**；扫描返回截断和未知覆盖，不根据注册表或空结果猜测世界。

0.1.55 起的 L2 方向过滤见 [二维地形通过](L2_TERRAIN_TRAVERSAL.md)。候选支持完整方块、平平台、普通地面斜坡和半砖，在当前脚底上下4格范围找表面，相邻表面变化最多2格，允许两端有支撑的最多3格空缺；未知/危险、过高障碍、未知形状和缺少远端落点仍拒绝。这不是物理可达性证明，跳跃、制动及安全落地仍由原生导航完整预测/执行/核验决定。目标会尝试缩短；`auto`默认先右，当前方向无候选时可在**发动作前**换向，
最多两次；明确left/right只探索该方向。任何已发动作失败都不换向重试。

本轮记忆包括实际起终位置、已走过的水平区间、64像素访问桶、已完成段、
候选决策、累计实际水平路程、连续未扩展水平范围的次数。目标访问桶达到两次就拒绝，
连续三段新增水平范围不足16像素则停止，避免来回走。
每段导航必须valid/completed且known结束证据，然后重新读取实际位置和地形。
指定方向实际位移不足24像素即no_progress，生命下降/死亡停止。

这是**单次运行的水平探索记忆**，不是永久地图、洞穴拓扑或跨任务数据库。
新聊天/世界/角色变化不继承旧执行状态；取消后不自动恢复。
一次Lua运行只允许调用explore一次；整轮动作/API/时间预算不会因提交新程序而重置。

| 参数 | 默认 | 范围/含义 |
| --- | --- | --- |
| direction | auto | auto / left / right |
| maxSteps | 6 | 1..8段；上限仍受整轮剩余动作预算约束 |
| stepPixels | 128 | 64..128像素目标步幅；候选可缩短到32像素或剩余行程，仍需通过进展检查 |
| maxDistance | 768 | 64..1024像素累计水平路程预算；发目标前预留原导航12像素到达容差，可提前结束 |

原Runtime默认仍是整轮共享64次API、8次动作、60秒Lua执行墙钟、指令/内存上限。
默认六段约33次API、6次导航；预算不足时由原Runtime拒绝继续并清理正在运行的动作。
模型请求另受原Agent模型预算控制；Lua60秒不是包含模型网络请求的整轮聊天耗时。
到达规则有容差，不能把“每段目标64像素”说成实际精确移动64像素。

## 停止与证据

| 情况 | 行为 |
| --- | --- |
| step_limit / distance_limit | 本次有界探索结束，停止后报告 |
| no_safe_direction / revisit_limit / reversal_limit / no_new_ground / no_progress | 保守停止，记录原因，不无限重复 |
| nearby_hostile / health_decreased / actor_unavailable / unsupported_motion_state | 风险或角色状态停止，不战斗/挖掘/拾取 |
| action_failed / action_timeout / action_cancelled / action_unknown | 本轮不再发移动；保留实际原子结束证据 |
| Runtime timeout / call_limit / action_limit / 取消 / 断连 | 原动作清理、释放核验；无法证明释放则unknown并关闭异常连接，不重连或重试 |

正常返回的 `value.kind=exploration_report` 包含steps、decisions、visited、distancePixels、
landmarks和reason。它仍是Lua报告，不能代替原子的endEvidence，也不把通用taskOutcome=unknown
改成“世界探索完成”。未知结果不得称为安全停止。

`agent-runtime-*.jsonl`继续记录每次API、动作、取消、token和成本。
新增 `lua_exploration_report` 保留有界进度摘要，明确标注script_self_report；
`lua_progress`记录每段目标、实际位移和停止原因。异常退出可能没有完整report，仍保留原API/清理日志。
证据包同时固定模型source和探索helper源码及SHA256，Worker执行父进程传入的同一版本，
不会在新Worker中悄悄读到另一个helper版本。该证据不是离线重放世界物理的能力。

## 实机验收（由用户执行，尚未标为通过）

1. **平地连续探索**：选足够长的平地，AI停稳、附近无敌怪。发送“向右探索附近，最多走4段”。
   预期一次指令自行完成多段观察/导航，段间可能短暂停稳，但不等待新用户指令。
   对照日志多次navigate_to_x、每段实际位移和known输入释放；最终报告step_limit或其他真实原因。
2. **简单障碍**：在可见地形内准备1–2格高、1–2格宽的普通完整方块障碍，头部留足净空，
   后方有完整落脚面，再发同样指令。可尝试原生规划；若导航拒绝，必须停下并如实报告。
   不能把合成地形测试当作图形跳障碍已通过。
3. **调整策略**：右侧近处有高墙/缺口，左侧安全，发送“开始探索附近”。预期发动作前缩短目标或换向；
   发现两侧不适合就停止。不会挖墙、跳入未知区或在失败后反复撞墙。
4. **取消**：探索途中发 `停止`，确认旧轮停止、不再发后续段，无旧回复插入；
   或Ctrl+C停止Agent并等待清理，再检查AI停止输入。保留实际位移，不宣称回滚已走距离。
5. **预算与风险**：发“向右探索8段”，应在段数/行程/时间/危险中的最早条件结束。
   阻挡、怪物接近、生命下降等场景核对停止原因；不要用危险存档验证死亡风险。
6. **日志核对**：观察的玩家必须是AI而非真人；输入/输出token主要在本次循环前后发生，
   段间API调用不等于模型请求。请保留本次runtime日志便于后续迭代。

## 验证与边界

没有运行全量测试。定向测试使用真实Lua Worker、真实loopback TCP、原Agent和HTTP模型替身，
覆盖连续多段、简单障碍候选、换向/缩短步长、危险/未知/缺口拒绝、无进展、重复访问、
伤害/敌怪、API/动作/时间预算、动作超时、取消、unknown不重试、模型契约门控和helper源码固定。
测试中的地形与运动是合成状态，不是原生物理。

### 移动前威胁包络修复（基于847c942）

旧版固定96像素半径确实不能覆盖128像素目标可能经过的区域。现在每段先算有效步幅：
`a=min(stepPixels,maxDistance-distancePixels-12)`；不足最短32像素时直接停止，避免无用扫描。
一次敌对NPC扫描使用半径 `a+236`，其中236=原96像素保守间距+128像素相对运动/快照老化余量
再加原导航12像素到达容差。默认128像素步幅对应364像素半径，64像素步幅对应300。
这是围绕当前AI中心的距离包络，覆盖拟水平移动及身体/运动余量；不是完整轨迹碰撞或敌人行为预测。

复用原ScanEntities“按距离排序后截断”的保证，仍仅请求最近1个hostile_creature。
若最近敌对NPC距离为d，进一步限制本段步幅为 `min(a,d-236)`：例如d=300时最多走64像素。
不足32则nearby_hostile停止；否则用原有地形/访问记忆过滤重新选择该上限内的候选。
允许的目标不会向上取整。使用全向半径而非前方扇区，因此自动换向和更短候选复用同一快照，
没有额外扫描；身后/隔墙敌人也可能导致保守缩短或停止。缺少最近敌人距离时拒绝开始移动。

**这只是移动前检查。** 每段仍只有一次NPC扫描，不在native导航执行过程中轮询NPC。
动作结束后的生命/位置/下一段NPC检查是下一动作的决策边界，不是当前动作的实时避险或伤害中断。
128像素运动余量是MVP保守启发值，未按所有NPC速度/大小或最坏导航耗时校准；高速/大型敌人、
新生成/突入实体、投射物、分类为unknown的Mod NPC及过期快照仍可能造成伤害。
原有导航/动作生命周期/取消/记忆/停止规则不变。

`lua_exploration_report.lastThreatCheck`记录pre_move_snapshot、半径、余量、拟步幅、允许步幅、
最近距离和观察时间。每个正常段仍是5次API；连续三段仍18次总调用、3次NPC扫描。
不因换向、距离缩短或maxResults=1截断再次扫描；扩大半径可能增加匹配/排序工作，返回实体上限仍为1。
该修复新增8项真实Lua/loopback TCP测试，覆盖原96像素盲区、96/128/236/268/364边界及小数边界、
64/96/128步幅和剩余行程、最近敌人/友方过滤、换向复用、缺失距离拒绝及无重复扫描。
本次新增8项测试和原18项探索/Agent回归通过；后续增加的提前行程停止调用数断言也单独验证。
原生JSON证据属于847c942的原MVP，不作为本次威胁包络的实机验收；本次未运行全量测试、
未重建Mod、未重新启动原生服务器或图形游戏。需要重启Agent加载新helper。

既有Lua Runtime/坐标/可观测性/服务器Agent四模块65项通过。新增探索用例共18个：
首次循环15项中14项通过，动作超时测试因过短总时限/混淆原子超时与程序超时失败；
调整为检查原子supervisor超时和known释放后，最终受影响范围5项通过，包含2项完整Agent聊天测试
及新增helper源码固定用例。其余新增用例保留通过；未运行全量测试，也未反复重跑整组回归。

847c942原生进程、实际路程及helper源码SHA256保存在JSON证据中：一次程序18次API、3次导航，
仅2次HTTP模型替身请求，最终速度(0,0)，退出码0。较早原生样例把3×64目标等同192像素
实际路程，因原导航到达容差提前触发行程停止；现在按实际路程扣预算并预留12像素，
最终使用256像素预算验证三段。没有为通过测试放宽生产动作、取消或unknown规则。

原生验证使用复制世界、独立27891/27892、既有显式诊断整帧机制；原Agent在一次Lua中
连续向左探索三段，模型HTTP和聊天授权是替身。原生AI作者回复、known释放、正常退出及
角色/reservation清理一并检查，见 [原生证据](evidence/G85_EXPLORATION_NATIVE.json)。
没有启动图形客户端或调用外部模型；Host & Play与真实模型的探索闭环仍待用户验收。

```powershell
$env:PYTHONPATH="$pwd/python;$pwd/tests"
& .local/lua-venv/Scripts/python.exe -m unittest test_exploration_threats -q
& .local/lua-venv/Scripts/python.exe -m unittest test_autonomous_exploration test_exploration_agent.ExplorationAgentTests -q
& .local/lua-venv/Scripts/python.exe -m unittest test_lua_runtime test_lua_coordinates test_lua_observability test_server_ai_agent -q
pwsh -NoProfile -File tests/verify_server_ai_native.ps1 -Port 27891 -IpcPort 27892 -PackagePath .local/companion-g85-build/Mods/CompanionBridge.tmod -Atomic -Reads -Actions -Agent -Exploration
```

未覆盖：跨任务地图/长期后台探索、多层洞穴与通道图、复杂坡/平台/液体/Mod地形，
保证越过任意简单障碍、战斗/逃跑策略、工具获取/砍树/挖掘/拾取，远程多人/晚加入，
实机死亡重生和持续旧连接跨世界/对象替换、所有GUI取消/断线时序。
敌对NPC包络检查是保守分类快照，不是完整威胁预测；伤害检查在动作边界，
不承诺原生移动过程不会受伤。具体物理与角色安全继续受原导航支持范围约束。
