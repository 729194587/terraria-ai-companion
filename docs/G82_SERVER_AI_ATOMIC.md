> **历史阶段资料**：本文记录特定开发阶段的实现或测试，不是当前能力契约。当前实现以源码、运行时能力握手及 `docs/INDEX.md` 中的专题文档为准。本文中的版本、状态、命令和限制均属于原记录时点。

# G8.2 第一阶段：服务器 AI 原子执行适配

基线是 PR #20 的 `cb59d169657b83391d38645fdb95075bc4141706`，正式包版本
`0.1.42`。本分支不修改 main 或 PR #20，保持独立 Draft，交由用户继续审计。

## 实际接入范围

仅接入一个游戏动作：`select_hotbar_slot(slot)`。slot 沿用现有 Schema，表示游戏
按键 **1–9、0**；0 对应最右侧第十格，即原生 inventory 索引 9。支持选择空栏。
`stop_action` 使用既有分发器的取消路径；`wait` 仍是既有 Python 单调时钟等待，
不属于新增游戏动作。

本阶段没有接入 `get_player_state` / `inspect_inventory` 的原子兼容入口，也没有
接入移动、挖掘、使用物品、装备或其他操作。服务器后端在分发前明确拒绝它们；
不伪造地形或物品详细能力。G8.1 的原始 `bind/state` 与
`scripts/ai_player_state.py` 仍可只读查询；其 inventory/selectedSlot 是原生索引。

## 调用链与身份

```text
existing LuaRuntime / bounded Lua Worker
  → existing atomic_operations.dispatch + lifecycle/terminal/stop
  → python/server_ai.py: ServerAiBridge (explicitly chosen backend)
  → 127.0.0.1:18779, G8.1 transport / connection lease
  → original game thread + reservation Gate
  → AiAtomicActions execution receipt
  → ServerAiSystem.Select
  → G6 ActorItemUse.Select → Capture / Idle / selectedItem / native Full sync
  → subsequent game-thread capture confirms selectedSlot
  → existing Python terminal proof → existing Lua result/evidence bundle
```

`AiAtomicActions` 仅记录有界执行状态和回执，没有 Lua、任务编排、寻路或重试逻辑。
原生选栏与同步代码直接复用 G6；未修改 G1–G7 源码。不操作 Main.LocalPlayer，
不改 Main.myPlayer，不从 TCP 工作线程读写 Player。

G8.1 的连接 lease 检查顺序保持：先拒绝重复 bind、无 lease 或 binding 不匹配，
再读取当前原生状态并核对 Player 引用、世界、角色代次和槽位。接受动作时还保存同一
Player 引用，每个处理点再次比对；原生调用内 G6 再次核对身份、生命及物品空闲状态。
对象、世界、代次或槽位改变后不执行剩余步骤，不自动重新绑定/重建 Actor。
引擎指纹、隔离与 quarantine 约束未放宽。

## 执行协议

G8.1 端口与 JSON Lines 包装不变，扩展 `bind` 成功响应的 `data.capabilities`：
`server_ai_atomic_v1`, `atomic_operations_v1`, `hotbar_keys_v1`。
这些能力配合本后端明确的操作白名单使用，不代表全部原子操作已实现。

同一连接先 bind，再携带完整 binding 执行：

```json
{"v":1,"id":"s1","op":"start","binding":{"actor":"...","world":"...","generation":1,"slot":254},"actionId":"32-lowercase-hex-uuid","operation":"select_hotbar_slot","arguments":{"slot":0},"expiresAtUnixMs":1790000000000}
```

示例时间只是字段形状，实际必须为本机当前 UTC 毫秒时间之后最多 2000ms；Python
适配器自动使用 1800ms。接受后换算到服务器单调时钟，renew 不延长执行期限。
actionId 为客户端生成的 32 位无连字符 UUID；同一原生服务中重复 ID 拒绝，不重做。
`arguments` 只允许整数 slot，未知字段/动作/命令、过期或过长的期限均拒绝。

```json
{"v":1,"id":"p1","op":"action","binding":{"actor":"...","world":"...","generation":1,"slot":254},"actionId":"..."}
```

`cancel` 采用相同字段，将 `op` 改为 `cancel`。其他连接即使绑定同一个 AI，
也不能查看/取消该动作；没有“取消当前任意动作”的入口。
错误使用 G8.1 的 `ok:false/error`，新增 `invalid_action_id`、`unknown_action`、
`duplicate_action`、`action_busy`、`actor_not_alive`、`invalid_arguments`、
`request_expired`、`invalid_deadline`、`action_registry_full` 等原因。

回执位于 `data.action`，沿用 Python 生命周期需要的 actionId、operation、session、
generation、worldSessionId、status、reason、durationMs、evidence、endEvidenceStatus
及 endEvidence，附加 actorId/actorSlot 与 executionState。session 为本连接执行归属，
generation 为 AI 角色代次。`evidence.beforeSlot` 是接受时的选中槽位快照。

处理顺序：

1. **accepted**：确认绑定、请求与期限，保存意图；不修改 Player。
2. **running**：下一主线程处理点承接；后续处理点调用 G6 一次。
3. **terminal**：再下一处理点读取同一身份的实际 selectedSlot，只有它与目标索引相同
   才 completed。原生调用返回、发包或 accepted 都不等于成功。

为了复用现有 Python 动作循环，accepted/running 回执的 `status` 均为 `running`，
`executionState` 区分 accepted/running/terminal；终态 status 为 completed、
cancelled、timeout 或 unknown。动作内最多调用一次 G6，不自动补做。

完成证据阶段为 `ServerAI_game_thread_no_input`，包含 gameUpdate、世界、actorId、
actorGeneration、actorSlot、selectedSlot、selectedHotbarKey、selectionVerified
和主线程 observationSequence。只读 lease 校验之后的读取才有资格提供结束证据。
本动作不获取持续移动/使用输入，明确记录 `inputsAcquired:false`；不伪装旧真人
`PostUpdateRunSpeeds` 释放阶段。Python 仅对具有服务器能力且身份匹配的这一阶段开放
验证；completed 还要求 selectionApplied 与 selectionVerified 为真且目标索引一致。
旧真人结束证据验证路径保持有效。

## 取消、异常与容量

- 原生调用前取消/断开/死亡：不修改选中栏，不触碰任何真人或其他 Actor 输入。
- 同一游戏线程内的 G6 调用不可中途撤销。取消发生在选栏后时，不回滚已发生的选栏；
  cancelled 回执保留 selectionApplied 与当前槽位证据。超时后亦不回滚、不假报成功。
- 原生调用或同步异常可能已产生部分效果，返回 unknown，不重试。确认前槽位被外部
  改变也返回 unknown。失去绑定时不能伪造新 Actor 的结束证据。
- 原有 Lua Worker、预算、独占 owner、取消清理与 unknown 会话封锁均保留；适配器
  不自动重连或回退到真人 TCP。开始确认丢失/网络超时按 unknown 处理。
- 全局仅一个进行中的服务器选栏，G6 还与既有行走/挖掘/物品使用互斥。
  保留 G8.1 的连接/行长/队列限制。执行记录上限 1024，不淘汰 ID 后再允许重放；
  达上限需正常退出并新开原生服务器。世界卸载关闭 IPC、清除记录，沿用原角色清理。

## 实际验证（2026-10-27）

未运行全量测试，未启动图形游戏，也未覆盖或删除旧 profile。

| 验证 | 实际结果 |
| --- | --- |
| 正式包 0.1.42 构建 | 0 errors / 0 warnings |
| G8.1 IPC 回归 | 45 项；离线身份替身与真实 loopback |
| 新服务器执行适配 | 40 项离线替身：按键映射、分阶段、下一处理点确认、重复 ID、跨连接归属、取消/断开、死亡、对象/世界/代次/槽位改变、过期/超时、部分效果及异常 unknown |
| 正式包 IL | 21 项；TCP 无 Terraria 访问、原生主线程/Gate、直接调用 G6、无 Player 字段重写、旧真人入口等 |
| Python 定向回归 | 首轮 65 项：新后端 8 项＋原有 atomic_operations/action result contract/LuaRuntime 57 项全部通过；新增 3 项后单独重跑后端 11 项通过 |
| 真人 C# BridgeOnly | 原 TCP、Player hook、输入归属、死亡/重生相关测试通过；离线替身/loopback |
| 真实原生服务器＋实际 Lua Worker | 通过；按键 0 将真实 AI selectedSlot 从 0 改为 9，后续 capture 确认，completed/known，Lua actionState=released，正常退出并释放角色/预留槽位 |
| 本轮真实图形 Host & Play / 远程多人 | **未执行** |

原生证据见 [G82_NATIVE_EVIDENCE.json](G82_NATIVE_EVIDENCE.json)。完整原生结果、Lua
回执及本次进程/世界/时间范围筛选后的日志位于其指定 `.local/companion-g82-native-*`。
Lua 证据 bundle 在 `.local/g82-lua-evidence`，仍由原证据模块生成。
空服原生 Game.Update 暂停，证据 gameUpdate=0，通过原生主线程循环的多次独立
capture 验证选栏；不能称为真人在线帧节奏或远端显示已验证。G8.1 既往用户实测
死亡重生不能替代本阶段动作中的实机生命周期验收。

定向命令：

```powershell
powershell -NoProfile -File scripts/build-companion.ps1
pwsh -NoProfile -File tests/run_server_ai_ipc_tests.ps1 -PackagePath .local/companion-g82-build/Mods/CompanionBridge.tmod
pwsh -NoProfile -File tests/run_csharp_tests.ps1 -BridgeOnly
pwsh -NoProfile -File tests/verify_server_ai_atomic_native.ps1
$env:PYTHONPATH="$PWD/python;$PWD/tests"
& .local/lua-venv/Scripts/python.exe -m unittest test_server_ai_backend test_atomic_result_contract test_atomic_operations test_lua_runtime -v
git diff --check
```

原生脚本默认使用现有 `.local/lua-venv/Scripts/python.exe`，也可传入
`-PythonExecutable`。需要仓库现有 requirements 中的 lupa 2.6；未另建 Lua 环境或编排器。
原 G8.1 原生脚本仍保留原默认 profile/package 路径，新增包装脚本显式选择 G8.2 包与端口。

## 真实游戏验收步骤（待用户执行）

1. 正常退出旧测试游戏/服务器。运行上述 build，然后：
   `powershell -NoProfile -File scripts/prepare-companion-host-play.ps1 -SeedProfile .local/companion-g81-host-20261026-233402-281`。
   可指定自己的旧测试 profile；脚本始终创建新的 G8.2 profile，不更新旧包/存档。
2. 手动运行脚本打印的新 start-host-play.ps1，选择 Host & Play，等待完全进入世界。
3. `python scripts/ai_player_state.py`：记录 AI binding、selectedSlot，确认真人正常移动。
4. `& .local/lua-venv/Scripts/python.exe scripts/server_ai_select.py --key 2`：应看到
   Lua completed、lastAction.completed=true、endEvidenceStatus=known，selectedSlot=1。
   再用只读工具确认实际选中索引 1；真人自己的快捷栏不应被改变。
5. 再执行 `--key 0`，应确认索引 9、selectedHotbarKey=0；不应使用/消耗任何物品。
6. 自然死亡/退出世界期间的动作、同一持续连接的旧 binding、图形显示/远程客户端同步
   另行定向验收。短选栏可能在人工取消前已完成，不能把晚到取消当作原生回滚测试。

当前未接 Agent 的默认启动入口，不改 Prompt；使用显式 ServerAiBridge 才能选择该后端。
PR 保持 Draft，不合并，交用户审计执行边界和证据。
