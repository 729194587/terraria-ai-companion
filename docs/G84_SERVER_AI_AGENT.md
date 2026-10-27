# G8.4：游戏聊天 → 现有 Agent/Lua → 服务器 AI

基线 PR #23 `c0ddfea`；独立分支 `codex/server-ai-chat-agent`，当前包版本 `0.1.47`。
不修改已有 PR、main、G1–G7 执行实现或旧 profile，不合并。

## 使用方式

先退出旧测试游戏和旧 Python Agent，更新包，并建立新的隔离 Host & Play profile。
`0.1.44` 没有本次聊天入口；`0.1.45` 会借本地玩家发送回复。此处需同时更新图形
客户端、服务器和 Python 到 `0.1.47`，旧能力组合明确拒绝，不回退真人发言。

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
检查日志与现场后明确重启。回复先经本地聊天 Bridge 检查当前指令身份，再经已绑定
的服务器 AI IPC 在游戏线程以真实 AI 槽位广播，作者、名称标签和头顶气泡属于该 AI。
仍可保留 `[AI]` 文本标记，但不借真人发送，不伪造客户端或 Main.myPlayer。

## 复用与通道边界

```text
本地游戏聊天 !ai <task>
 -> ChatBridgeSystem 的原生 outgoing chat hook
 -> TcpControlServer :18777，local_ai_command 事件
 -> 原 Agent Loop / LLMClient -> execute_lua
 -> 原 LuaRuntime / Worker / atomic_operations
 -> ServerAiBridge :18779 -> 原游戏线程队列 / G4/G6/G7 -> 独立 AI Player
 -> 实际结束证据 -> 原 Agent Loop -> 客户端 authorize_ai_reply（不发言）
 -> 已绑定 AI IPC say -> 原生 BroadcastChatMessageAs(AI槽位,...)
```

- `ChatBridgeSystem` 保留原显示/其他玩家聊天入口，增加本地原生发送捕获；先调用
  原发送一次，再在游戏线程发布通过 `AiChatInput` 严格前缀校验的任务。
  现有 `GameMessageCapture.Suppress` 覆盖 AI 回复发送，原生自回显也被原 author
  过滤排除；即使回复文字包含 `!ai`，也不能成为任务。
- 新能力 `local_ai_chat_v1`。事件仍是 chat_message，但带
  `source=local_ai_command,isSelf=true`。Python Bridge 仅额外放行这种本地事件，
  新 Agent 模式再次校验来源并去重；旧 Agent 仍忽略 isSelf 消息。
- `ControlState` 保留独立的最新 AI chatId，`authorize_ai_reply` 沿用原有有界文本、
  队列和游戏线程校验，但不发送聊天。检查当前连接和最新 AI 指令；普通聊天不覆盖 AI 回复 ID。
  新指令拒绝旧回复，离开世界清空 AI 回复 ID，重连重新初始化。没有获取真人控制输入。
- `Agent` 只增加可选 chat_bridge；现有 self.bridge 在此模式下始终是 ServerAiBridge。
  状态、runtimeInventory、Lua、取消全部走 AI 后端；回复授权走 chat_bridge，原生作者
  发布走 AI 后端。没有把真人状态当作 AI 状态。
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

## Host & Play 验收步骤

2026-10-27 用户已完成状态、移动与 AI 作者显示三项基础验收，见文末实机补录。
下面仍保留复现步骤；其他场景不能据此标为通过。

1. 使用新包 0.1.46 启动上文新 profile，确认 server_ai_verify 读取 AI；启动
   `python/agent.py --server-ai` 并看到 ready。
2. 在本地游戏聊天发 `!ai 查询你的位置和生命`，核对回复来自 AI 后端，而非真人位置。
   发普通聊天、其他玩家发 `!ai`、观察 `[AI]` 回复，确认均不产生额外模型任务。
3. AI 脚下铺完整安全平地，本地聊天发 `!ai 向右移动约80像素`。观察真人不受控制、
   只有 AI 行走并停稳；对照 Lua/原生结束回执和游戏内 `[AI]` 回复。
4. 交付木剑/铜镐后先查询真实快捷栏，再明确授权单次使用或实际格的单方块挖掘。
   不让模型猜屏幕目标；确认动画、实际方块变化、邻块保留及如实回复。
5. 较长动作中发送新的 `!ai` 指令或 Ctrl+C，确认旧轮停止、无旧回复和残留输入；
   分别测试聊天断开、AI 断开、服务错误，不能变成真人动作或自动重连。

0.1.45 用户曾反馈聊天任务链路成功但回复显示本地玩家作者；该旧结果本身不构成
0.1.46 作者修复通过。用户随后重启 0.1.46 并确认了新结果，详见文末。PR #23 已有
移动/挥剑/挖掘实机结果不替代完整 Agent 链路下的物品使用/挖掘验收。持续旧连接跨真实世界/
角色代次变化、动作中死亡重生、远程多人、晚加入、多实例配对及全面真人并行控制
仍未验证。PR 保持 Draft，不合并。

## 0.1.46：修正回复的真实作者

本机原生 IL 确认 `BroadcastChatMessageAs(byte,NetworkText,Color,int)` 将作者字节交给
`NetTextModule.SerializeServerMessage`。客户端 `DisplayMessage` 用该槽位访问
`Main.player[author].name` 和 `chatOverhead`。因此仅加 `[AI]` 前缀不能改变真人作者。

新发布路径不增加游戏原子操作：

- 客户端能力 `ai_reply_authorization_v1`，校验最新 AI chatId 后返回 authorized，不
  调用原真人 Send；Python 再核对当前轮，随后在 AI 连接发 say。
- 服务器能力 `server_ai_chat_v1`，say 复用现有游戏线程队列、Gate、连接 lease，
  对象引用/世界/代次/槽位二次检查；作者由当前真实 AI 的 whoAmI 决定，不能由请求指定。
- 文本限 400 UTF-16 字符、单行；请求有效期最多 2 秒，messageId 去重，最多 1024 条。
  广播前先记不确定结果，部分失败和丢回执不得重发；Python 关闭不确定的连接，不退回
  真人发送。断开、旧世界、旧代次、对象替换等拒绝发布。
- 旧 send_ai_chat 保留兼容旧调用，但新版 Agent 不再使用它。此版仅在授权后检查
  Python 当前轮，仍存在旧回复排队后遇到新指令的竞态；下节 0.1.47 增加最终发布栅栏。

修复定向验证：Python Agent/后端 32 个测试通过（包括两项新增的旧回复拒绝、丢回执
不重发/不真人回退）；C# 客户端聊天 20 项、服务器原生聊天协议检查及正式包 36 项 IL
检查通过，原真人组与原 IPC/动作/读取组保留通过。构建 0 error / 0 warning，未跑全量。
复制世界中实际调用原生 AI 作者广播，核对 authorSlot=254、绑定元数据、输入释放和
退出清理；该隔离测试的输入与客户端授权仍是替身，不单独证明图形显示。后续用户实机
确认另记于下节。隔离证据见 [原生作者证据](evidence/G84_NATIVE_CHAT_AUTHOR.json)。前节 0.1.45 的
真实模型记录保留为历史证据，不冒充已使用修复后的广播路径。

## 2026-10-27 Host & Play 实机验收补录（0.1.46）

基于实现提交 `a204ef3`。用户手动启动
`.local/companion-g82-host-20261027-135050-952/start-host-play.ps1`，通过本地游戏聊天
使用现有 Agent/实际模型/Lua/ServerAiBridge。用户明确回复“成功。”，随后确认
“测试完成，移动和状态应该都差不多了。”，涵盖 AI 名称、角色头顶气泡、状态与移动
的基础链路。图形结论来自用户确认，不是代理操作或自动识别游戏画面。

保存的 [实机证据](evidence/G84_HOST_PLAY_ACCEPTANCE.json) 与原生隔离实验分别记录。
包为 `0.1.46`，SHA256
`2942AB3ACDD169A898B7BEC0FF429634DE1281E2DE1B6AD62F9F3260EC7EC079`。
实际日志时间为北京时间 13:55:11–13:56:20（UTC 05:55:11–05:56:20）；
runtime 来源 `.local/diagnostics/agent-runtime-20261027-135511-525063.jsonl`，两个任务
始终关联 world `1cae24b86eb54f75962a3de38e6f30aa`、AI slot `254`、actor generation `1`。
Python 的轮次 generation=1/2 不代表角色已发生生命周期变化。

| 实机项目 | 用户确认和保存的日志证据 |
| --- | --- |
| 状态查询与模型回复 | 第1轮 1份 Lua/1次 API/0次动作，真实状态为 position=(33680,3318)、life=100/100、velocity=(0,0)，Agent 正常结束，原生 AI 作者回复一次 |
| 约80像素向右移动 | 第2轮目标 targetX=33760，实际 x=33680→33752.59，y=3318→3302；53次原生更新、最终速度(0,0)、arrived=true、status=completed、known 结束证据、inputsReleased=true，Lua actionState=released。实际水平位移72.59像素，按既有12像素水平到达规则通过，不宣称精确移动80像素 |
| AI 作者/名称/气泡 | 用户确认修复后的发言不再显示自己，而显示 AI 名称与 AI 头顶气泡；本次进程14024在游戏线程记录2次 ai-chat，authorSlot=254、sameReference=true、world/generation与动作一致。保留原生记录的UTC、更新号及原始行SHA256，不提交无关聊天内容 |

两轮各正常结束且在本窗口共记录6次模型请求、2次原生作者发布；不能从这个有限窗口
外推为所有普通聊天、远程前缀或回显洪泛场景通过。移动轮有一次行动前的脚本错误：
模型访问错误层级的 position 字段，得到 script_error/idle、actions=0；随后在原预算内
修正，实际只有1次移动尝试。没有隐藏错误、重试失败游戏动作或放宽运行时约束。
两轮记录中的通用 `task_outcome` 仍为 unknown；实机验收结论不改写原有任务证据契约。
移动记录 CompletedJumps=0，Y变化也不计作原生跳障碍验收。

仍未验证：完整 Agent 聊天链路下的物品使用/挖掘，实机动作中取消/超时/断开，动作中
死亡重生，持续旧连接跨真实对象/世界/角色代次变化，左移/跳障碍/地形和工具矩阵，
远程多人/晚加入，多实例配对及全面真人并行控制，图形掉落/拾取和 Host & Play 退出时
角色/reservation清理。旧阶段动作实机和隔离取消/清理结果不能替代上述项目。

本次仅补录文档与证据，复核现有日志、包哈希、动作/发布元数据及JSON一致性；没有
修改功能实现、重启游戏、调用模型、重跑构建或全量测试。PR #24 保持 Open / Draft，
不合并。

## 0.1.47：新指令与旧回复发布的竞态修复

根因：取消 `asyncio.to_thread` 的等待不会停止工作线程；客户端 `authorize_ai_reply`
返回后，旧 `say` 仍可能等待 Python 执行锁、写锁或服务器游戏线程。仅在 await 返回后
检查当前 chatId，不能阻止已经排队的旧回复广播。

- `_send_reply` 携带不可变 Agent generation 和取消 Event。新指令立即撤销旧 token；
  授权前后、取得执行锁后、取得写锁后分别检查；失败/预算回复也携带原轮次。
- 客户端 `ai_reply_authorization_v2` 要求正整数 `replyTurn`，仅检查当前 AI chatId，
  回传 `{chatId,replyTurn}`，不发送真人聊天。Python 必须核对这两个值。
- 服务器 `server_ai_chat_v2` 新增连接内单调递增的 `reply_turn` 元数据请求；仍要求
  原 binding，对真实 Actor 引用/世界/角色代次/Slot 二次检查后才 ACK。新轮模型调用前
  等待 ACK；Agent 关闭也撤销旧轮。`replyTurn` 与角色 generation 是不同概念。
- AI TCP 读取线程在将新轮请求排入游戏队列前更新连接内栅栏，只操作协议元数据；
  断线立即撤销。`say` 必须携带相同轮次，最终 `AiReplyFence.Publish` 在与轮次更新
  共用的锁中调用原生广播。因此新轮元数据已到服务器、但新轮游戏线程 ACK 尚未处理时，
  已在队列里的旧 `say` 也不能广播。无 Terraria 状态在线程间读取或修改。
- ServerAiBridge 改为单连接按请求 ID 分派回包，仅序列化 socket 写入；轮次控制绕过
  Lua 执行所有权/执行锁，可以越过等待旧 `say` 回包的线程，不增加第三条 TCP 通道。
  原执行命令仍持有原锁与 Lua 所有权。真实 AI 作者、Gate/生命周期、期限、messageId
  去重、广播前未知标记及丢回执关闭连接/不重试/不回退真人均保留。

**精确边界**：服务器收到新轮元数据并取得栅栏锁是服务端撤销的线性化点；它与进入原生
广播互斥。已经进入广播的消息不能追回。游戏输入、Python 收到事件、服务器收到栅栏
是跨进程事件，通知传输途中不能承诺撤回已开始的广播；客户端授权或 say 排队本身
不再视为不可撤销的发布。无 ACK 的新轮不得开始模型任务或声称绑定已生效。

定向验证（未运行全量）：

- Python `test_server_ai_agent test_server_ai_backend test_agent test_lua_agent` 共70项通过；
  随后单独新增的授权错配用例通过，共71个不同用例。覆盖授权迟到、取消后工作线程继续、
  等待执行锁、同一 TCP 上旧 say 等待回包时新轮越过、真实 Lua 与原真人 Agent 回归。
- `run_csharp_tests.ps1 -BridgeOnly` 通过，客户端聊天25项及原真人 Bridge/生命周期组；
  `run_server_ai_ipc_tests.ps1` 通过：IPC45、动作40、读取15、原生适配替身28、聊天30项。
  新增真实 loopback 确定性排队测试：先排 say1、再排 turn2、最后才泵游戏线程，原生
  回调次数为0；say2随后只发布1次，回调始终在游戏线程。
- 正式包构建0错误/0警告；正式包39项 IL检查通过，包含 TCP/栅栏零Terraria访问、最终
  Publish 栅栏、真实 whoAmI 广播与无身份伪装。`git diff --check` 通过。
- PowerShell 7 隔离原生服务器通过，复制世界/独立端口27871/27872。实际 Agent/Lua
  读取位置、移动并释放、原生作者254广播一次；advance到轮次2后，直接发送轮次1的
  say得到 superseded。正常退出0、角色清理/reservation释放通过；日志限定本次PID、
  世界和进程时间。[本轮证据](evidence/G84_REPLY_TURN_FENCE.json)。
  这里使用模型 HTTP 替身与客户端授权替身；不冒充真实模型或图形验收。原生旧轮拒绝是
  顺序验证，并发时序由离线真实 TCP 测试控制。原生脚本需 pwsh/PowerShell 7，Windows
  PowerShell 5首次尝试因缺少 ProcessStartInfo.ArgumentList 在启动前失败，未计作通过。

必须同时重建/重载客户端、服务器和 Python 为0.1.47（v2能力）；不兼容组合拒绝。
启动入口和双端口不变。真实 Host & Play 尚未重测本补丁：建议安全平地连续发送两个
`!ai`，让第二条在第一条回复前取代它，确认仅最新回复显示 AI 名称/气泡。该人工操作
不能稳定卡住毫秒级竞态，确定性证据来自上述并发测试。此前0.1.46实机记录保留为历史，
不标记0.1.47实机通过；远程多人、持续旧连接跨真实生命周期及动作中死亡重生仍未验证。
