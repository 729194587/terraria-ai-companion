# G8.4：游戏聊天 → 现有 Agent/Lua → 服务器 AI

基线 PR #23 `c0ddfea`；独立分支 `codex/server-ai-chat-agent`，包版本 `0.1.45`。
不修改已有 PR、main、G1–G7 执行实现或旧 profile，不合并。

## 使用方式

先退出旧测试游戏和旧 Python Agent，更新包，并建立新的隔离 Host & Play profile。
`0.1.44` 的图形客户端没有本次聊天入口，仅更新服务器或 Python 不够。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-companion.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/prepare-companion-host-play.ps1 -SeedProfile .local/companion-g82-host-20261027-111108-230
# 手动运行刚打印的 start-host-play.ps1；选择 Multiplayer > Host & Play
& .local/lua-venv/Scripts/python.exe scripts/server_ai_verify.py
& .local/lua-venv/Scripts/python.exe python/agent.py --server-ai --port 18777 --ai-port 18779 --timeout 5
```

沿用项目 `.env` 或环境变量中的 `LLM_BASE_URL`、`LLM_API_KEY`、`LLM_MODEL`，
不增加配置文件或模型客户端。不要把密钥粘贴到游戏聊天。真实模型请求可能产生费用。
Agent 会输出 ready 和本地诊断日志路径；不要同时运行占用同一聊天 Bridge 的旧 Agent。
本阶段限定一组匹配的本机 Host & Play/端口；多实例时必须自行确保两个端口对应同一局。

在**本地玩家自己的游戏聊天框**输入（`!ai` 后必须有空白和非空任务）：

```text
!ai 查询你的位置和生命
!ai 向右移动约80像素
!ai 查询你的快捷栏，告诉我木剑和铜镐在哪个按键
!ai 使用你背包里的木剑挥动一次，不要攻击或挖掘
!ai 用你的铜镐挖掉我明确指定的方块，坐标是 <实际tileX>, <实际tileY>，不要移动或挖邻块
```

坐标占位符须换成本局实际格；1 Tile=16 世界像素。身体中心格不是画面方块定位工具。
当前没有地形/实体观察，模糊的“那个石块”“脚下”应询问实际格，不猜测坐标。
AI 初始未必有工具，正式模式不会发测试工具；可在测试世界让其原生拾取玩家交付物品。
移动先选安全平地、完整支撑和充足头部空间；模型不承诺原生规划一定可达。

普通聊天、其他玩家的 `!ai`、系统消息和 AI 回复不触发新任务。新本地 `!ai` 指令会
取消旧轮并等待现有 Lua 清理；例如 `!ai 停止` 会先撤销旧轮，再由 Agent 处理该指令。
Ctrl+C 退出时等待既有动作清理并关闭两个连接。未知执行/绑定失效不自动重连或重试，
检查日志与现场后明确重启。回复通过本地玩家聊天连接发出并标注 `[AI]`，不伪装成
保留槽位的原生网络客户端，也不声称 AI 自己建立了客户端连接。

## 复用与通道边界

```text
本地游戏聊天 !ai <task>
 -> ChatBridgeSystem 的原生 outgoing chat hook
 -> TcpControlServer :18777，local_ai_command 事件
 -> 原 Agent Loop / LLMClient -> execute_lua
 -> 原 LuaRuntime / Worker / atomic_operations
 -> ServerAiBridge :18779 -> 原游戏线程队列 / G4/G6/G7 -> 独立 AI Player
 -> 实际结束证据 -> 原 Agent Loop -> send_ai_chat -> 原 ChatBridgeSystem.Send
```

- `ChatBridgeSystem` 保留原显示/其他玩家聊天入口，增加本地原生发送捕获；先调用
  原发送一次，再在游戏线程发布通过 `AiChatInput` 严格前缀校验的任务。
  现有 `GameMessageCapture.Suppress` 覆盖 AI 回复发送，原生自回显也被原 author
  过滤排除；即使回复文字包含 `!ai`，也不能成为任务。
- 新能力 `local_ai_chat_v1`。事件仍是 chat_message，但带
  `source=local_ai_command,isSelf=true`。Python Bridge 仅额外放行这种本地事件，
  新 Agent 模式再次校验来源并去重；旧 Agent 仍忽略 isSelf 消息。
- `ControlState` 增加独立的最新 AI chatId，`send_ai_chat` 沿用原有有界文本、队列和
  游戏线程 Send，检查当前聊天连接和最新 AI 指令；普通聊天不会覆盖 AI 回复 ID。
  新指令拒绝旧回复，离开世界清空 AI 回复 ID，重连重新初始化。没有获取真人控制输入。
- `Agent` 只增加可选 chat_bridge；现有 self.bridge 在此模式下始终是 ServerAiBridge。
  状态、runtimeInventory、Lua、取消全部走 AI 后端；聊天回复只走 chat_bridge。
  原 `run`/CLI 增加显式模式和端口参数；不带 `--server-ai` 时保留原真人/其他玩家聊天
  和 `--local-input` 行为，不兼容的参数/客户端能力明确拒绝，不回退操纵真人。
- 仍使用原 LLMClient、Agent Loop、ProgramStore、Lua Worker、分发器、共享预算、
  结果核验、取消/失败/unknown fencing。未新建 Agent、编排器或执行协议。
  新消息清理顺序由原 `_wait_lua_cleanup` 保证；断开聊天通道会撤销 Agent 任务。
  程序在未行动前发生语法/脚本错误时，沿用既有规则允许在预算内修正；游戏动作失败
  后停止变更，不自动重试。模型服务失败返回有界失败说明。

## 模型实际可用能力

新模式的模型工具只有 `execute_lua`。其系统契约由**当前已连接后端的能力白名单**
过滤现有 Schema 生成，不注入旧真人 SYSTEM 或全量工具；伪造其他直接工具名也在
Agent 路由前拒绝。Lua 内仍由原后端白名单再次校验。

| Lua API | 范围 |
| --- | --- |
| `get_player_state {}` | 当前绑定服务器 AI 的真实位置、速度、生命等现有投影 |
| `inspect_inventory` | 既有 selected/hotbar/item/full/slots 只读模式；未知数据不伪造，无修改 token |
| `select_hotbar_slot {slot=...}` | 游戏按键 1..9、0，沿用真实选栏及下一处理点核验 |
| `navigate_to_x {targetX=...}` | 原导航与实际到达/停稳证据，仍有支撑、地形、Buff 等限制 |
| `use_item {}` | G6 木剑/铜镐/苹果白名单，无定向 target；只证明使用周期，效果未确认 |
| `mine_tile {x=...,y=...}` | 当前真实镐、固定目标、保守及原生距离/保护规则，实际破坏才成功 |
| `wait {milliseconds=...}` | 可取消的宿主等待，不推进游戏帧 |
| `stop_action {}` | 仅现有连接拥有的动作，不接管其他连接的动作 |

没有把未接入的摄像机、地形、实体感知、二维落点、战斗、放置、背包修改等暴露为可用。
use_item 在模型契约中仅显示空参数，背包描述明确服务器限制。runtimeInventory 也从
服务器 AI 读取；未知/读取失败显示 unavailable，不拿真人背包或空列表充数。
旧 MovementLoop 的真人观察授权元数据不注入新模式的工具结果。

## 验证记录与边界

没有运行全量测试。命令：

```powershell
$env:PYTHONPATH="$pwd/python;$pwd/tests"
& .local/lua-venv/Scripts/python.exe -m unittest test_server_ai_agent test_lua_agent test_agent test_server_ai_backend -q
pwsh -NoProfile -File tests/run_csharp_tests.ps1 -BridgeOnly
pwsh -NoProfile -File tests/run_server_ai_ipc_tests.ps1 -PackagePath .local/companion-g82-build/Mods/CompanionBridge.tmod
pwsh -NoProfile -File tests/verify_server_ai_agent_native.ps1
# 可选：明确调用已配置的真实模型，最多4次模型请求，复制测试世界
pwsh -NoProfile -File tests/verify_server_ai_agent_native.ps1 -LiveModel
git diff --check
```

- 正式包构建：0 error / 0 warning；34 项正式包 IL 检查通过，包括原生聊天发送
  hook 的安装/卸载、回复 suppression 和不写 Player 输入。
- IPC/动作/读取定向 45+40+15+28 项通过。新增 C# 聊天 18 项通过，使用生产前缀解析、
  ControlState、TCP 队列和真实 loopback，验证独立回复 ID、普通聊天隔离、旧回复和
  世界退出拒绝；原真人 Bridge/输入/死亡组通过。
- Python 相关四模块定向回归曾通过 61 个测试（包含当时6个新测试）；随后增加模型
  失败回复、禁止真人后端回退、精确有界回复历史测试并收紧服务器专用上下文，最终9个
  新增测试全部通过。覆盖两条真实 TCP
  连接、原 HTTP LLMClient 的模型替身、真正 Lua Worker、生成代码和动作回执、能力
  白名单、普通聊天/自身回显/重复事件、取消旧任务、身份未知不重连、服务失败与真人
  路由不混用。聊天/世界存储仍是替身，不是图形游戏验收。
- **隔离原生 + HTTP 模型替身**通过：同一 Agent/Lua 驱动真实 AI 移动，并在回复
  sink 记录结果。复制世界、独立 27871/27872；继续检查原选栏/读取/使用/挖掘/取消及
  正常退出、角色清理、reservation 释放。聊天事件/sink 是替身。
- **隔离原生 + 实际配置模型**通过：4 次真实模型请求、3 份生成程序；实际 AI
  x `33616 → 33666.395`、y `3462`、最终速度 `(0,0)`，目标 x=33664，44 次原生
  更新，完成和输入释放证据已核验。模型得到结果后产生 `[AI] 已完成…` 回复，由测试
  sink 收到。首份程序调用不存在的 `json.encode`，在行动前 script_error/idle；模型
  后续修正，只有1次游戏动作，未重试失败动作。随后契约明确列出 json 的三项 helper，
  没有为模型放宽沙箱或添加 encode。原有 taskOutcome 仍保持 unknown，没有因为模型
  自报成功改写通用任务证据契约。详见 [原生 Agent 证据](evidence/G84_NATIVE_AGENT.json)。

原生实验仍显式使用 G8.3 的诊断整帧驱动：无客户端专用服会暂停 Game.Update，故
复制世界内由双诊断参数和 marker 允许完整原生 Main.Update，不直接 Player.Update、
不改 HasClients。启动前拒绝已占用的测试端口；日志按本次 PID/世界/时间筛选。
这不能算图形 Host & Play 通过。
模型配置/密钥不进入提交的证据。

## 待用户执行的 Host & Play 验收

1. 使用新包 0.1.45 启动上文新 profile，确认 server_ai_verify 读取 AI；启动
   `python/agent.py --server-ai` 并看到 ready。
2. 在本地游戏聊天发 `!ai 查询你的位置和生命`，核对回复来自 AI 后端，而非真人位置。
   发普通聊天、其他玩家发 `!ai`、观察 `[AI]` 回复，确认均不产生额外模型任务。
3. AI 脚下铺完整安全平地，本地聊天发 `!ai 向右移动约80像素`。观察真人不受控制、
   只有 AI 行走并停稳；对照 Lua/原生结束回执和游戏内 `[AI]` 回复。
4. 交付木剑/铜镐后先查询真实快捷栏，再明确授权单次使用或实际格的单方块挖掘。
   不让模型猜屏幕目标；确认动画、实际方块变化、邻块保留及如实回复。
5. 较长动作中发送新的 `!ai` 指令或 Ctrl+C，确认旧轮停止、无旧回复和残留输入；
   分别测试聊天断开、AI 断开、服务错误，不能变成真人动作或自动重连。

本次**未执行新的图形客户端聊天框 → 模型 → AI → 图形聊天回复全链路**。PR #23
已有的移动/挥剑/挖掘实机结果不替代本次 Agent/聊天验收。持续旧连接跨真实世界/
角色代次变化、动作中死亡重生、远程多人、晚加入、多实例配对及全面真人并行控制
仍未验证。PR 保持 Draft，不合并。
