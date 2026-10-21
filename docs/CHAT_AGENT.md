# 最小聊天 Agent（当前 Mod 0.1.18）

本轮实现远端玩家聊天推送 → Python 模型调用 → 可选 `get_state` → 游戏内回复。只向模型开放这一个只读工具。没有新增导航、战斗、建造、Skill、Memory 或任务调度；会话只保存在本次 Python 进程内。

## 实施前确认

- 初始 `git status --short` 为空，没有覆盖已有未提交修改。
- 本机 tModLoader **2026.08.3.0 stable / Terraria 1.4.4.9**；项目导入安装目录的 `tMLMod.targets`，Client Mod，.NET 8。
- 用安装自带 Mono.Cecil 只读检查 `tModLoader.dll`：`ChatHelper.DisplayMessage(NetworkText, Color, byte)` 的第三参数是消息作者；小于 255 时使用 `Main.player[author].name`，255 为系统消息。实际 `On_ChatHelper.DisplayMessage` 钩子已经由本机编译器验证。
- `ChatHelper.SendChatMessageFromClient(ChatMessage)` 经 `NetTextModule.SerializeClientMessage` 发往服务器；`new ChatMessage(string)` 使用 `SayChatCommand`，不会将模型文本解释成本地 Mod 命令。官方接口参考：[ChatHelper](https://docs.tmodloader.net/docs/stable/class_chat_helper.html)。没有假设存在 `ModSystem.OnChat`。
- 同一 DLL 还确认 `NetTextModule.DeserializeAsClient` 调用 `DisplayMessage`；服务器 `SayChatCommand.ProcessIncomingMessage` 调用 `BroadcastChatMessageAs` 保留作者索引，聊天接收与发送调用链闭合。
- 原 TCP 为 loopback 单控制连接、JSON Lines v1、递增 id、750ms 运动租约。后台只入队，`CompanionSystem` 的 `On_Main.DoUpdate` 在游戏线程读取状态、执行队列。P、断线、世界变化仍撤销会话。
- `get_state` 返回 `state: {inWorld, position:{x,y}, velocity:{x,y}, life, lifeMax}`；菜单状态为 false 和 null。
- `get_terrain` 同时返回同帧 `state` 和 `terrain: {originX, originY, tileSize, rows, playerWidth, playerHeight, grounded, supported}`，网格 45×29。`supported` 是运动模型对角色状态的兼容性标志，**不是地面支撑**。
- 原 Python 仅用标准库，无现成模型客户端。新增一个依赖 `httpx`，直接调用兼容 Chat Completions 的 HTTP API，支持异步取消，不引入 SDK 或配置框架。

## 改动文件

| 文件 | 用途 |
| --- | --- |
| `CompanionBridge/Common/Systems/ChatBridgeSystem.cs` | 接收聊天显示事件、按作者过滤、游戏线程发送普通聊天 |
| `CompanionBridge/EntitySnapshot.cs`、`Common/Systems/EntityReader.cs` | 有界实体快照、保守分类与游戏线程读取 |
| `CompanionBridge/TcpControlServer.cs` | 主动事件、并发安全写入、能力声明 |
| `CompanionBridge/ControlState.cs` | send_chat 解析、排队、聊天代次校验 |
| `CompanionBridge/Common/Systems/CompanionSystem.cs` | 将游戏线程发送动作传给现有队列 |
| `CompanionBridge/build.txt` | 版本 0.1.18 |
| `python/control.py` | 复用唯一 Bridge，独立接收线程、id 分发、事件去重 |
| `python/llm.py` | 可取消的兼容 API 客户端 |
| `python/agent.py` | 会话、抢占、工具循环、真实状态组合 |
| `python/requirements.txt`、`.env.example`、`.gitignore` | 单一依赖和环境配置、忽略本地密钥文件 |
| `tests/test_agent.py`、`tests/BridgeOfflineTests.cs` | 聊天、API、工具、抢占和 TCP 回归 |
| `tests/test_entities.py`、`tests/run_csharp_tests.ps1` | 实体工具结果、大回执、分类/方向/过滤/序列化测试 |
| `README.md`、本文件 | 使用方法与验收边界 |

## 协议

保持 v1、请求 id 及原有命令不变。hello 的 capabilities 新增 `chat_events`、`send_chat`。

主动事件没有请求 id：

```json
{"v":1,"type":"chat_message","eventId":"instance-guid:1","senderId":0,"senderName":"Human","text":"你有多少血？","receivedAt":"2026-10-20T10:00:00.0000000+00:00","isSelf":false}
```

eventId 为 Mod 实例 GUID + 单调递增序号；重复文本仍是不同消息。时间为 Mod 捕获时 UTC，不冒充玩家发送时刻。Python 按 eventId 去重。仅多人客户端、有效远端玩家作者会进入事件通道；本地角色索引和系统作者均被排除。A 为真人、B 为唯一 AI 的双客户端设置是当前使用前提；无法识别其他远端角色是否也由第三方机器人控制。团队/服务端改写为系统作者的消息不会当作真人消息。

发送请求：

```json
{"v":1,"id":3,"cmd":"send_chat","text":"我读取到生命值为 80/100。","chatId":"instance-guid:1"}
```

成功回执 `{"v":1,"id":3,"ok":true,"status":"sent","mode":"none"}` 表示游戏线程已调用发送接口，**不是服务器投递确认**。过期 chatId 返回 `ok:false,status:"superseded"`；不在世界或没有发送回调为 `not_ready`；接口异常为 `chat_send_failed`。文本限单行 400 UTF-16 单元，Agent 将换行折为空格，超长回复截断并加 `...`，历史保存实际发送文本。

C# hello、回执和事件都经过唯一 `WriteJson`，由同一个 SemaphoreSlim 保护。事件有独立有界队列和写任务，不等待未完成的游戏线程回执；队列溢出会断开并撤销会话，避免默默丢消息。请求行上限从 1024 增至 4096 字节，以容纳中文回复；Python 回帧现限 65536 字节，以容纳有界实体列表；请求行仍限 4096 字节。事件只在连接期间接收，不重播离线消息。

Python 一个接收线程持续读取，根据 type/id 分发；请求调用可并发，发送顺序与 id 分配受锁保护。请求超时关闭连接，避免继续使用回执状态不确定的连接。不轮询聊天记录。主异步循环的 200ms 超时仅检查断线，不控制聊天触发时机。

## Agent 与工具

每条事件立即追加 user 历史，并新建异步运行，取消上一运行。每轮最多 **4 次模型调用、3 次工具调用**。向模型发送系统指令、历史、唯一的 get_state 工具定义。工具名与空对象参数都经校验，不支持的调用和读取失败返回 `valid:false,error`。完整 assistant/tool 消息批次按 `tool_call_id` 保存到历史；抢占中的未完成批次不提交，因此不会留下无结果的工具调用。所有真人消息保留，已完成工具批次可供下轮参考。

模型 HTTP 请求可取消；每次 await 后还校验运行代次，丢弃不响应取消的迟到结果。仅当前运行可发回复，且游戏线程发送前再次核对最新 chatId，覆盖“旧发送已入队、新消息随后抵达”的竞态。已完成发送的消息无法撤回。

get_state 复用 Bridge.get_state 和 Bridge.get_terrain；最终 player 和 terrain 采用后者同一游戏更新的快照，避免组合两个时刻的角色/地形。返回 Python 读取开始和接收时间区间、有效性、真实网格、分类 legend、附近实体快照（旧 Mod 则明确不可用）。位置为像素、TileSize=16，Y 向下。`?` 为未知/未加载，`!` 合并液体和危险 Tile；不将未知误报为“未发现”，不识别掉落物或宝箱。

工具结果新增 `spatial`，仅由同一 `get_terrain` 回执的 state/terrain 确定性计算，原始 player、terrain、rows 和 legend 保留不变。`playerAnchors` 给出碰撞矩形中心、底边中心（feet）、顶边中心（head）的世界像素、向下取整的 Tile 坐标及零起点网格行列；不把角色贴图、姿态或重力方向当作已感知。`coordinates` 明确左上角 position、X 向右/Y 向下、索引映射和世界方向，`observedBounds` 给出采样边界；范围外及 `?` 均不能当作空气，`.` 只表示不参与当前碰撞分类。

`nearbySpecialTiles` 列出距角色中心 96px 内最近最多 16 个 `h/1/2/3/4/a/b/=/!/?` 格子，包含采集器类别、网格索引、世界 Tile 坐标、格子中心像素、相对位移、水平/垂直方向及欧氏距离（像素与 Tile）。距离参考为**格子中心到角色中心**，不是碰撞间隙、表面高度或路径长度。排序按距离、行、列固定；提供截断前计数（含零计数）、半径、limit、matchedCount 和 truncated，未列出不代表范围外不存在。原点不一致只警告，仍按实际回执原点映射，不修正数据。所有派生信息明确标记为采集器分类的计算解释，不代表已经在游戏画面中验证半砖、斜坡、可见性或可通行性。

HTTP 失败、超时、JSON/结构异常、调用预算耗尽均明确写 stderr；工具错误交回模型，IPC 断线退出。没有备用虚构回复或游戏状态。日志显示 `Chat received`、`LLM request`、`Tool result`、`Chat sent`，不输出密钥、认证头或 HTTP 错误响应正文。

### 附近实体（0.1.18）

`entities` 由游戏线程在同一次更新、原生更新执行前，与角色和地形一起采集；Python 使用 `get_terrain` 回执中的实体，不组合前一次 `get_state` 的数据。新包构建至 `.local/entity-build/Mods/CompanionBridge.tmod`；本轮不部署或重启，须使用者在保存退出 B 后更新并确认 0.1.18。hello 新增 `nearby_entities` 能力，命令不变。旧包缺少字段时 `available:false`，不当作没有实体。

以 AI 的碰撞矩形中心为参考，读取客户端当前已知、active 且存活的其他玩家和 NPC，排除自身、死亡/幽灵玩家及失活 NPC。范围为中心距离 480px（30 Tile）内，按距离、kind、实例 id 排序，最多返回最近 12 个；`matchedCount` 是截断前数量，`truncated` 显式标记遗漏。实体范围与地形窗口是不同的范围，不保证视线、屏幕可见或完整世界同步。id 是当前槽位，会复用，不是永久身份。

每项包含 kind、实例 id、NPC typeId、类型名/名称、分类及依据、世界中心像素、相对位移、水平/垂直方向和距离。名称/类型名上限各 64 UTF-16 单元，截断时 `nameTruncated:true`。快照含 `sampledAt` UTC、`gameUpdate`、`observerPlayerId` 和 `referenceCenterWorldPixels`。这里只是观察，不触发攻击、寻路或任何实体操作。

分类保留原始 NPC 标志供核查：友好或城镇 NPC 为 `friendly_npc`；小生物注册且接触伤害为零为 `critter`；只有原版、非友好、非城镇、非小生物、正伤害、可追踪且非投射物型 NPC 才推断为 `hostile_creature`。该类**不保证当前攻击意图或实际能伤害玩家**。未知 Mod 生物、非友好但零伤害、不可追踪、矛盾标志或投射物型 NPC 保留 `unknown`，不把 friendly=false 单独作为敌对依据。其他玩家为 `other_player`，不推断 PvP 关系。畸形实体数据降级为实体不可用，角色、原始网格及 spatial 仍可使用。

实现属性已通过本机 tModLoader DLL/IL 核对和编译：`NPC.TypeName`、`GivenOrTypeName`、`NPCID.Sets.CountsAsCritter/ActsLikeTownNPC/ProjectileNPC`、`CanBeChasedBy()`。离线测试覆盖分类、范围/自身过滤、方向、截断、实际 TCP 序列化、旧包兼容和模型工具消息；**真实游戏实体识别尚未验收**。建议新包中依次站近另一玩家、城镇 NPC、小生物和普通敌怪，对照游戏及 Trace 中同次 IPC 回执，并验证 crowded/truncated 情况。

## 配置和运行

在仓库根目录的 PowerShell 中：

```powershell
python -m pip install -r python/requirements.txt
# 编辑仓库根目录 .env，填入 LLM_API_KEY；已预设 DeepSeek Flash。
python python/agent.py --port 18777 --timeout 2
```

BASE_URL 应是 API 根路径，例如以 `/v1` 结尾；程序追加 `/chat/completions`。HTTP 超时默认 30 秒，IPC timeout 默认 2 秒。程序自动读取仓库根目录 `.env` 的三个 LLM 配置项（与运行目录无关），环境变量优先。支持普通 KEY=VALUE 和单/双引号字面值，不展开变量或行尾注释。`.env` 已忽略，不上传；`.env.example` 是可上传 GitHub 的无密钥模板。已按 [DeepSeek 官方文档](https://api-docs.deepseek.com/zh-cn/) 配置 `https://api.deepseek.com` 和 `deepseek-flash`。不要将密钥写入仓库或聊天。进程只保留当前会话，不自动重连。Ctrl+C 退出并关闭 IPC。

先让 A/B 进入同一多人世界，再运行 Agent。A 不加载 Bridge；B 加载 **0.1.18**（实体感知需要新包）。同一端口只能一个 Python 控制连接，不同时运行导航脚本。单人模式没有远端真人消息，因此不是本轮交互验收入口。Agent 不取得运动租约，不需要按 P；P 仍会断开外部会话，之后需手动重新启动 Agent。

本次包已构建至 `.local/agent-build/Mods/CompanionBridge.tmod`，**未部署，未启动/重启客户端**。由使用者按已有 A/B 配置准备新包及客户端，不覆盖角色或世界。重新构建：

```powershell
$tml = 'D:\steam\steamapps\common\tModLoader'
$repo = 'D:\Agent Project\terraria-ai-companion'
Push-Location $tml
try {
    & "$tml\dotnet\dotnet.exe" "$tml\tModLoader.dll" -server -build "$repo\CompanionBridge" -tmlsavedirectory "$repo\.local\agent-build"
} finally {
    Pop-Location
}
```

## 验证和待验收

### 可选本地 Trace

在启动 Agent 的 PowerShell 中显式设置环境变量：

```powershell
$env:TERRARIA_AGENT_TRACE = '1'
python python/agent.py
```

默认关闭；删除变量（`Remove-Item Env:TERRARIA_AGENT_TRACE`）或改为 `0` 后重新启动即关闭。该开关从进程环境读取，不从 `.env` 读取。无需修改 Mod 或重启游戏。

每次 Python 进程使用独立的 `.local/diagnostics/agent-trace-<session_id>.jsonl`，目录已被 `.gitignore` 的 `.local/` 忽略。单文件最大 5 MiB，保留一份同上限的 `.jsonl.1` 轮转备份，每次运行最多约 10 MiB；不同运行的文件需自行定期删除。单条事件超过 5 MiB 时记录 `trace_record_dropped` 和原因，不伪装为完整上下文；轮转可能淘汰较早的运行片段。

Trace 包含游戏聊天、完整模型上下文和地形，**只写本地，不自动上传**。不记录 HTTP 请求头、认证信息、`.env` 或 HTTP 错误响应正文；已知 API Key 和常见认证字段会脱敏。为了脱敏而替换的内容显示为 `[REDACTED]`，其余请求体/回执/工具消息保持原值。不要在聊天中粘贴其他未配置的密钥。日志写入失败会被忽略，不改变请求、工具结果或调度行为；同步本地写盘有少量额外开销。

每行是独立 JSON，公共字段为 `type`、UTC `timestamp`、`session_id`、单调递增 `sequence`。Agent 范围内还有 `chat_event_id`、`generation`；模型事件有 `model_request_id`；工具与其 IPC 事件有 `tool_call_id`，IPC 回执保留原始 `id`。

按 `session_id + generation` 串起：`chat_received` → `agent_start` → `llm_request`/`llm_response`/`llm_return` → `tool_start` → `ipc_request`/`ipc_reply` → `terrain_diagnostic` → `tool_result` → `tool_message` → 下一次 `llm_request` → `reply_prepared` → `send_chat_start`/`send_chat_result` → `agent_end`。`ipc_reply.reply` 是 C# 实际返回的解析后 JSON 对象；`tool_result.result` 是 Python 整理的结果；`tool_message.message.content` 是实际序列化工具消息，后续请求的 `body.messages` 保留模型收到的完整历史。`llm_response.assistant` 是服务端 assistant 消息及结束原因，`llm_return` 是客户端实际交给 Agent 的字段。

失败/取消分别记录 `llm_error`、`ipc_error`、`tool_cancelled`、`agent_failed`、`agent_cancelled`、`agent_task_done` 等；迟到结果记录 `agent_result_discarded` 的具体检查位置和当前代次。`agent_end.outcome` 标明 completed/failed/cancelled/superseded。任务在开始前被取消时只有 `agent_task_done.cancelled=true`，不会伪造开始事件。取消中的后台只读 IPC 线程可能继续返回，Trace 会保留其原代次；这不表示 Agent 仍消费了该结果或发送了回复。`send_chat` 回执仍不等于服务器投递或画面确认。

地形诊断记录位置、宽高、原点、实际行列长度、各字符数量（含零计数）、全部 `h/1/2/3/4` 的零起点行列和世界 Tile 坐标。预期原点严格复用当前 `TerrainReader.Read` 公式：`floor((x + width/2)/16)-22`、`floor((y + height)/16)-17`。无效网格、陌生字符、`?`、原点不一致只产生 warnings，不修改原数据。诊断在工具验证前执行，因此无效原始回执也能留下线索。没有新增 Tile/实体感知能力；测试使用假游戏服务和模拟模型，真实游戏 Trace 文件仍需现场验收。

离线测试：

```powershell
python -m unittest discover -s tests -v
pwsh -NoProfile -File tests/run_csharp_tests.ps1
```

本轮已通过 Python **115 项**（新增 Agent/API/事件/配置测试 11 项）、C# 实际协议/聊天/控制/运动回归，tModLoader 构建 **0 错误、0 警告**；`compileall` 和 `git diff --check` 通过。API 测试用 httpx MockTransport，游戏发送测试用回调替身；TCP 测试使用真实 localhost socket。沙箱初次拒绝 loopback（WinError 10013），在允许本机网络测试的环境重跑通过。这些结果不代表真实 API 或游戏验收通过。

**真实双客户端验收尚未执行。** 本执行环境没有 LLM_BASE_URL/API_KEY/MODEL，也没有由本轮准备的新包客户端；按用户选择先交付代码和步骤，不自动部署或重启。按以下顺序实际验收并记录 A/B 画面与终端日志：

1. A 发送“你好，简短打个招呼”。终端应立即出现 Chat received 和 LLM request；A/B 游戏聊天均看到 B 回复。B 回声不能引发第二条 Chat received 或额外 API 调用。
2. A 发送“读取你的生命、位置和脚下地形后告诉我”。日志应出现 Tool result valid=True 和第二次模型请求；对照 B 画面确认生命/坐标/局部地形。终端的 Chat sent 只能证明调用发送接口，必须以游戏画面验收送达。
3. 连续问“刚才我问了什么？”检查对话连贯。工具对话消息格式已离线测试；真实服务商接受格式仍需此步骤确认。
4. 第一条问复杂状态问题，在 API 尚未返回时立即发送“先回答我：你好”。应立即看到新代次 LLM request，仅新请求最终回答；旧请求迟到不得再回复。两条真人消息均留在进程历史。
5. 使用无效 API 配置启动一轮，确认 stderr 明确失败且游戏无伪造成功回答；恢复配置后重启 Agent。通过服务商超时或离线网络测试超时分支。
6. B 按 P：现有连接断开，Agent 报错退出；不应有迟到回复。按 P 的原手动模式可能开始右移，沿用原控制语义，在安全位置测试并及时关闭。关闭世界或断开 B 连接也应撤销待发聊天。
7. A 不加载 Bridge，B 的 P/控制仍只影响 B。运动租约、断线释放及客户端输入隔离需按既有场景在新包回归；离线回归通过不能替代这些游戏检查。

不将上述待执行项目标记通过。没有提交、推送或部署。
