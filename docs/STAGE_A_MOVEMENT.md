# 0.1.24 阶段 A：模型决策的有限基础移动闭环

前置探索观察／成本统计已按要求单独提交为 `299e3c6`。阶段 A 代码保持未提交，未推送、未部署；不调用真实模型或启动游戏。

## 流程与预算

模型根据玩家请求识别附近坐标目标，优先探索观察，随后自行决定 left/right 和 50–2000ms 时长。程序没有计算方向、预排动作列表或引入脚本导航器，只核验观察证据、动作结果及限额。

典型最长默认流程：

```text
模型1 → get_exploration_observation → 结果返回模型
模型2 → move(方向,时长,首次可附坐标目标) → 结果返回模型
模型3 → get_exploration_observation → 结果返回模型
模型4 → move(重新决定方向和时长) → 结果返回模型
模型5 → get_exploration_observation → 结果返回模型
模型6 → move(再次依据最新观察决定) → 结果返回模型
模型7 → get_exploration_observation → 结果返回模型
模型8 → 最终回复，程序核验／提供明确终止结果
```

- 默认每条聊天最多 **7 次工具调用、8 次模型调用、6000ms 累计请求移动时长**，即最多 3 次“完整观察—动作—再观察”中的动作（单次上限仍为 2000ms）。因为动作耗时可以短，实际动作次数也受工具上限约束，不保证特定距离。
- 每个模型响应最多执行一个工具调用。若模型同时返回多个调用，整批不执行，为每个 ID 回传拒绝结果，要求重新决策；拒绝尝试也消耗工具预算。不会挑第一步执行后接着运行同批中基于旧状态的动作。
- 要执行工具，必须还留有一次模型调用用于接收结果。最后一次模型调用若仍要求工具则不执行，返回预算终止反馈，不在模型永远看不到结果的情况下开始动作。
- 剩余工具、模型和移动预算附在工具结果 `movementLoop` 中；有新聊天时旧任务被取消，新任务才有独立预算，不跨聊天恢复旧动作。
- 累计时长按请求值预留，拒绝／取消／不确定不退还，不依赖模型或不可靠的耗时回执计算“退款”。

## move 的兼容扩展

原有参数继续可用：

```json
{"direction":"right","duration_ms":500}
```

附近坐标目标可在第一次真正移动前指定：

```json
{"direction":"right","duration_ms":800,
 "target_position":{"x":1234,"y":560},"arrival_tolerance":16}
```

坐标表示期望**自身碰撞框左上角**的世界像素位置，X 右、Y 下；容差为欧氏距离，默认 16、范围 1–32 像素。模型从用户请求和观察中识别／转换目标，不是本地程序解析任意自然语言目标。目标及容差设定后本轮不允许更换，不能移到一半把目标改成当前位置冒充成功。

程序对“已到达”的核验仅能证明模型设定的坐标目标，不证明模型正确理解了用户指代。未设坐标目标的旧调用仍能限时移动，但最终不能认证抵达。NPC 等会移动的实体只有当时的坐标，不进行自动目标追踪；难以识别的目标应让模型说明并停止，不猜位置。

## 观察、进展与终止

首次移动必须基于本轮新读取的 exploration 或完整 observation；旧聊天历史、get_state 和动作 before/after 不能单独授权下一步。get_state 和完整接口仍可单独读取，不改变它们的底层返回行为。

边界修复采用严格授权：普通 get_state 查询（包括读取失败）之后，下一次移动返回 `terrain_observation_not_refreshed`，必须补充探索／完整观察；没有任何先前观察或仍在等待动作后观察时，返回 `fresh_exploration_or_full_observation_required`。普通查询不会清空原证据、刷新原采样接收时钟、清除动作后观察要求，也不比较缺失的世界／控制字段来虚构状态变化。

`movementLoop.terrainObservationStatus` 区分 `missing`（无证据）、`authorized`（有效地形观察已授权）、`consumed`（动作已使用，需后续观察）、`not_refreshed`（普通查询没有提供新观察）、`invalid`（实际观察校验失败／缺少地形／过期），`observationReason` 说明原因。实际观察必须包含支持的地形结构及有效边界，只有玩家／摄像机而无 terrain 的 partial 结果不能授权移动。地形格、语义矩形的详细校验仍由原有读取／投影逻辑负责。

移动完成后必须再次观察：同一 worldSessionId、新 observationId、严格更大的 gameUpdate、recent_render 摄像机、非 stale/unavailable，且玩家非死亡／幽灵。观察在 Python 收到后超过 30 秒不能再授权移动，需重新读取；这是决策等待上限，不能保证模型推理期间世界静止。

每次成功动作后的新观察比较实际位置：有目标时检查距离减少，无目标时检查水平位移。连续两次未达到 1 像素的进展会锁定 no_progress（包括连续远离目标），不再次执行同样动作。此判断只负责停止，不制定绕路动作。

终止状态：`arrived`、`stopped`、`failed`、`budget_exhausted`、`no_progress`、`unknown`、`cancelled`。工具结果携带当前状态／原因，最终写入 `movement_loop_end` Trace 及 `Agent.last_movement_result`。模型收到结果后仍可观察、读取细节或停止，但终止锁定后不能继续 move。

终态只通过 `MovementLoop.finish()` 确定，一旦确定便不覆盖。再次调用 stop_movement 仍执行原停止接口，但其返回的动作状态不替换任务已有的 arrived/no_progress/cancelled 等状态或原因；没有终态时才记录 stopped/unknown。

`completed` 仅说明单次输入结束。只有最新观察满足固定目标容差才标 arrived；已经在容差内时不启动动作。预算最后一次观察若确认抵达，则允许如实报告成功；未确认时明确报告未抵达。模型最终文字不能覆盖此核验：移动任务的游戏回复由终止状态生成，避免模型口头声称到达而实际尚未抵达。没有移动请求的普通聊天保持模型原有回复。

缺少动作后观察、观察失败、世界变化、死亡、无进展、动作 rejected/unknown/cancelled 均不会继续移动；unknown 不盲目重试。模型调用失败或预算耗尽也会尽可能发送简短的未确认结果；断线时无法承诺聊天发送成功。失败／取消请求不回填“已成功”的聊天历史。

## 跨模型等待的控制安全

已有绝对截止时间、750ms 租约、按动作 ID 取消、手动接管、会话断开及新聊天抢占机制保留。没有延长单次按键时间，模型推理不承担续租。

为覆盖“某次动作已经结束、模型正在思考下一步时死亡并复活”的间隙，C# 在 get_observation 回执外壳增加可选 `controlGeneration`：在服务观察的游戏更新与 ControlState 锁内复制，不修改 observation schema v1，不获取／延长控制租约。TCP 握手声明 `movement_loop_v1`。

Python 将采样代次传给 move；开始前既比较 get_movement 的当前代次，也由原 move_for(expectedGeneration) 防护检查后的竞态。正常开始／结束各使服务端代次加 1，闭环核对正常完成后代次为采样代次 +2；额外死亡／接管／取消等变化会中止旧循环。后续观察也不能静默接受外部变化后的新代次并继续旧任务。

旧 Mod 的只读工具仍兼容，但没有采样控制代次时阶段 A 拒绝开始移动，返回 control_generation_unavailable_update_bridge。真实测试需要部署 **0.1.24**，不能只重启 Python。

## 文件与验证

- `python/movement_loop.py`：每条聊天的观察证据、固定目标核验、进展／预算与终止保护，无动作规划。
- `python/agent.py`：默认预算、串行工具派发、结果回传、优先探索提示、明确终止回复与 Trace。
- `python/movement.py`：可选 expected_generation 和终态控制代次，保留独立 move 原用法及 ID 清理。
- `python/exploration.py`：转发同次观察控制代次；不新增采样或 IPC 查询。
- `ControlState.cs` / `TcpControlServer.cs` / `build.txt`：新增可选观察回执代次、能力标记和版本 0.1.24。

离线测试使用 mock 模型、明确标注的合成位置与已有脱敏真实观察结构，不把 fake 位移当 Terraria 物理验收。覆盖多次依据新位置改变方向／时长、整批调用拒绝、缺少观察、预算与最后一轮成功、累计时间、连续无进展、目标不一致、目标两轴距离、未知／死亡／接管代次变化、复活后旧任务不恢复、新聊天抢占与原工具回归。C# 覆盖观察代次序列化及死亡／接管后的旧代次拒绝。

边界修复验证：全部 **214 项 Python 离线测试通过**（其中闭环专项 28 项）；`tests/run_csharp_tests.ps1` 全部 C# 回归通过；`compileall` 和 `git diff --check` 通过。tModLoader 2026.08.3.0 stable / Terraria 1.4.4.9 构建 CompanionBridge 0.1.24 成功，**0 错误、0 警告**，包位于 `.local/stage-a-boundaries-build/Mods/CompanionBridge.tmod`，未复制到客户端配置。

## 导航 A0：移动结果语义（Python 适配）

A0 基线为 `codex/agent-trace` 的 `f0a0f1a`，开始时工作区干净，与导航设计报告的实现描述一致。本次只适配现有水平移动结果，不增加动作、导航工具、C# 协议或运动模型。

原有 `valid/status/completed/cancelled/reason/actionId`、位置、代次和 `movementLoop` 字段保留，新增语义：

| 字段 | 含义 |
| --- | --- |
| `execution` | 单次执行状态、是否由 Bridge 确认、注入输入是否已释放。`completed` 不表示目标到达；`inputReleased=null` 表示未知，不承诺停稳 |
| `effects` | 已知前后位置及 `displacementWorldPixels`；两端缺任一端时 knowledge=unknown、差分=null。只表示观测位置差，`attributedToAction=null`，不归因惯性、其他玩家或世界事件 |
| `goal` | 原始 move 接口没有目标参数，返回 not_applicable；Agent 将本轮 `movementLoop.goal` 附给 move。目标 `kind=position_xy`，使用碰撞框左上角的世界像素 XY 与原有欧氏距离容差，不能用 CLI 的水平穿越 arrived 替代 |
| `movementLoop.goal` | `achieved/not_achieved/unknown/not_applicable`。有效观察给出目标判定及 worldSessionId/observationId/gameUpdate/位置/代次证据；动作一开始即清除旧目标判定，等待新观察。取消、unknown、世界/控制变化后不通过后续观察恢复旧任务 |
| `observation` / `movementLoop.observation` | 原始反馈仍要求动作后地形观察；闭环返回当前所需观察。A0 的 `endGameUpdate=null` 表示未提供结束 tick；A0.1 补齐的证据与降级规则见下节 |
| `stop_movement.execution` | 描述停止请求本身；`targetAction` 描述所查询的原动作及其身份，legacy completed 仍可能是历史动作 completed。`cancelRequested` 说明是否尝试发送精确取消；noOp 表示不需发送取消，unknown 时 noOp=null。停止不覆盖任务已有终态、目标证据或原动作 effects |

`effects.source=bridge_movement_feedback` 表示 Bridge 动作快照差；有符合原规则的新地形观察后，闭环可记录 `fresh_terrain_observation` 差分及其两端引用。原始工具结果不被之后观察回写。无位移但输入完成时，execution=completed、effects 的差分可为零；新观察目标未到时 goal=not_achieved。只有动作反馈、有位移但缺新观察时，goal=unknown。没有设置目标时为 not_applicable。

终态保持既有锁定规则；已确认抵达后的停止不抹除 achieved/evidence。普通状态查询仍不能授权移动或确认动作后目标，过期相机/同 tick 新 ID 也不能补足证据。预算、50–2000ms 时长、750ms 租约、死亡/手动接管和精确取消逻辑保持不变。A0 没有将末次 running 状态当最终位置，也没有补造 ACK 丢失后的动作结果。

测试沿用 MovementBridge/Scene/GuardTests；覆盖真实工具分发下的到达、碰墙、抵达后停止，以及取消前位移、ACK 丢失、过期/同 tick/状态查询、死亡/世界/代次变化。所有位置与碰撞场景仍是离线夹具，不代表真实游戏验收。

A0 本轮验证：移动专项 **45 项通过**，完整 Python **221 项通过**；`tests/run_csharp_tests.ps1` 完整 C# 回归、`compileall`、`git diff --check` 通过。本机 tModLoader 构建 **0 错误、0 警告**，产物 `.local/a0-build/Mods/CompanionBridge.tmod`；只运行编译打包模式，未启动游戏会话、部署、调用真实模型、提交或推送。

## A0.1：动作输入释放的更新编号证据

本轮开始时 HEAD 仍为 `f0a0f1a`，保留 A0 的 6 个未提交修改文件；没有回退原修改。A0.1 在实际游戏线程边界补齐证据，需要小幅扩展 C#，不改运动模型或控制预算。

### 采集时机与计数依据

1. `ControlState.FinishMovement` 仍处理正常到期、主动 cancel/stop、失租、死亡/控制撤销和内部手动接管，并撤销旧 `_input`。此处只改变原有终态，**不采集结束编号**；TCP 请求时间、请求入队时间、UTC endedAt 都不能充当游戏编号证据。
2. 动作开始时由 `CompanionSystem.UpdateExperiment` 将同一 `ObservationReader.WorldSessionId` 传入动作记录。正常角色在 `CompanionPlayer.PostUpdateRunSpeeds` 的入口、任何新 Bridge 输入注入之前，检查旧动作已终止、动作/控制 session 仍匹配、世界相同、旧动作已不持有外部控制，且玩家 left/right/jump 控制位均已释放。只有此时调用 `ConfirmMovementInputReleased` 记录当前 `Main.GameUpdateCount`。该检查不清空玩家按键，不改变人类输入或原控制机制；控制位未释放就继续等待证据。
3. 死亡角色通过本地玩家 `UpdateDead`，在原 `RevokeControl` 路径之后并确认 dead/ghost 时记录独立的 `UpdateDead_after_revoke` 边界。死亡中途撤销、暂停或未再进入玩家钩子时，证据暂缺，不把撤销调用时刻当结束时刻。
4. 结束编号 E 是“已确认停止旧动作输入”的保守边界，可以晚于逻辑终止/ACK，不声称是停止请求到达 tick，也不表示速度为零或物理立即停止。首份有效证据锁定；后续查询、重复停止或后续玩家钩子不改写 E。

本轮对本机 tModLoader.dll 使用 Mono.Cecil 只读核对：SHA-256 为 `FCC6A9624B12191BE4A15D714A3675440911FEB8C1D374642AB1FD686F0DA0A5`。`Main.get_GameUpdateCount` 读取 `_gameUpdateCount`（UInt32，API 扩展为 ulong）；`Main.DoUpdateInWorld` 在 `SystemLoader.PostUpdatePlayers`（IL_010f）之后才于 IL_0114–011b 递增。因此玩家钩子采 E 后的同 tick 观察不能作为动作后证据；下次 `pre_DoUpdate` 的编号严格大于 E 才满足排序。观察读取与结束钩子均在游戏线程；TCP 线程既不读取游戏计数也不制造释放证据。该结论针对本机版本，未修改游戏程序集。

### 兼容协议与 Python 判定

现有 v1 命令、动作 ID、旧状态与位置字段保留。`movement.action` 新增开始世界 `worldSessionId`，以及：

```json
{
  "endEvidenceStatus": "known",
  "endEvidence": {
    "worldSessionId": "world",
    "gameUpdate": 80,
    "phase": "PostUpdateRunSpeeds_before_bridge_input"
  }
}
```

`endEvidenceStatus` 为 `not_ended`（运行中）、`pending`（已终止，尚未确认玩家释放边界）、`known` 或 `unknown`（例如开始世界缺失）。缺失/未知时 endEvidence=null。另一个有效 phase 为 `UpdateDead_after_revoke`；结束 receipt 嵌在原 action 下，不与其他动作混用。

Python 将有效 receipt 映射到 `observation.endGameUpdate/endEvidence/actionId`，闭环保存对应 actionId，成功目标证据中同时保存 `actionEnd` 与后续 observation 引用。只接受 Bridge 确认的终态、known receipt、非负整数编号、非空世界与已知边界阶段；旧结果、bool/负数编号、请求时间阶段、动作结果 unknown、早于动作前观察的结束编号均不能授权验证。`execution.inputReleased=true` 现在要求有效玩家释放证据，单独 terminal ACK 不足。

正常完成 ACK 若仍为 pending，Python 在**原有动作 deadline（请求时长 + 原有 1 秒余量）**内轮询原 action 的 receipt；终态不续租，不延长动作期限，不重启动作。超出余量仍无证据则保留 execution=completed 及已有位移，闭环终止为 unknown，goal 不报 achieved。取消仍沿用原精确 actionId 取消/立即终止路径；取消回执若早于玩家钩子，可以暂缺 E，不能据此验证抵达。后续只读 get_movement 可在边界经过后返回已锁定证据。

动作后验证同时要求原规则与新规则：同世界、同预期控制代次、有效且非过期的地形观察、新 observationId、比动作前观察更新，且 **observation.gameUpdate > E**。编号等于 E 或介于动作前编号与 E 之间均拒绝，保持 needsObservation 和目标未知；本轮按原终态保护停止，不因后来的观察自动恢复任务。普通状态查询依然不能替代地形观察。没有实际启动动作的 already-at-goal 判定仍只依赖当前有效观察，不要求不存在的动作 receipt。

### 生命周期边界与局限

生产环境 P 接管实际执行 `CompanionBridge.ToggleManual → StopControl → Disconnect/Reset`，会清掉旧动作及连接；不能将离线 `ControlState.ToggleManual` 的可保留 receipt 误称为生产 P 回执。生产接管/退出/断线/reset 后没有足够证据时，Python 保持取消或未知，不恢复旧任务；不跨新 session 回收旧 receipt。

暂停不改变原租约和绝对期限。若控制已终止但玩家钩子未运行、或真实控制位仍被按住，不记录 E；没有大于 E 的有效观察时不确认目标。世界改变或玩家不再更新也可使 receipt 永远不可取得，这是安全降级，不能靠墙钟推算。UInt32 计数回绕不尝试拼接；严格排序失败时保守停止。动作原 afterPosition 仍是终止逻辑处快照，不保证与释放边界同阶段；抵达仍需边界后的新观察。未做真实游戏、暂停恢复、失焦或 Mod 钩子组合验收；离线测试模拟生命周期和控制位门禁，不能证明运行时所有钩子组合。

A0.1 验证：移动专项 **51 项通过**，完整 Python **227 项通过**；完整 C# 回归、`compileall`、`git diff --check` 通过。新增回归覆盖 E 前/等于 E/大于 E、首次刷新后仍检查结束边界、世界/过期/旧协议/非法证据、pending ACK 与暂停、正常到期/取消/死亡/手动/失租的边界确认、重复停止不改写 E，以及 reset 丢弃旧回执。本机 Mod 构建 **0 错误、0 警告**，产物 `.local/a01-build/Mods/CompanionBridge.tmod`；未启动游戏会话、部署、调用真实模型、提交或推送。

## 后续真实游戏验收

人工确认并部署 0.1.24 后，在安全平地、附近目标与同一高度下：

1. 指定一个近处位置，确认 Agent 使用探索观察，经过至少两次独立决策接近目标；日志应交替出现 observation→move→observation，而非并行预排。
2. 观察接近目标时是否缩短动作、必要时纠正方向；抵达只以新观察核验，碰撞或预算不足应说明未确认抵达。
3. 指定被墙挡住的位置，确认无进展后停止，不持续顶墙；先不测跳跃、越障或自动绕路。
4. 在模型思考间隙发送“停下”或手动接管，确认旧循环取消。可在两次动作之间正常死亡／复活，无需强求在两秒动作内操作。
5. 检查普通聊天、完整观察、死亡后持续连接和复活后的新用户移动请求仍正常。

真实模型是否可靠选择目标坐标／时长、碰撞／惯性造成的接近效果，以及新预算下 Token 和响应延迟，仍需实测。本阶段不是自动寻路或无限探索。
