# 0.1.22 游戏系统提示感知

## 原因与事件来源

原 `ChatBridgeSystem` 只 Hook `ChatHelper.DisplayMessage`，且只转发 `author < Main.maxPlayers`、作者不是本地玩家且仍活动的多人玩家消息；`author=255` 的系统消息被过滤。即使将该过滤去掉，直接走 `Main.NewText` 的本地提示也不会经过这个 Hook。

本次只读检查本机 Terraria 1.4.4.9 / tModLoader 2026.08.3.0 DLL 的 IL，确认：

- `ChatHelper.DisplayMessage` 的系统作者为 255；显示路径调用 `Main.NewTextMultiline`，也可能暂存后显示。
- `NPC.CountKillForBannersAndDropThem` 构造 `Game.EnemiesDefeatedAnnouncement` 或 `Game.EnemiesDefeatedByAnnouncement`。单人调用 `Main.NewText`；服务端通过 `ChatHelper.BroadcastChatMessage` 广播给客户端。这是旗帜击杀里程碑提示，不是每一次击杀都会发出的事件。
- `Player.KillMe` 的死亡文字路径包含系统广播和本地 `Main.NewText`。

因此保留原玩家过滤，另将 `DisplayMessage` 的作者 255 转为被动提示，并补接 `On_Main.NewText_string_byte_byte_byte` 与 `On_Main.NewTextMultiline`。嵌套调用只捕获最外层，避免 ChatHelper 和底层显示重复产生事件；自己发送回复、缓存重放期间抑制捕获。没有读取 NetworkText 私有字段、按中文文本猜测事件类型，或为每次 NPC 死亡扫描世界。

## 协议及 Agent 行为

握手新增 `game_messages_v1`。系统提示使用独立事件：

```json
{"v":1,"type":"game_message","eventId":"instance:sequence","text":"本地化后的提示原文","source":"chat_helper","gameUpdate":123,"receivedAt":"UTC时间","textTruncated":false,"droppedBefore":0}
```

`source` 为 `chat_helper`、`main_new_text` 或 `main_new_text_multiline`。这些提示是客户端收到的文本，可能来自原版、服务端或 Mod，本地文本来源不保证都是官方系统事件。死亡期间仍允许捕获；既有玩家事件、Observation 与死亡后保留连接的逻辑不变。

系统事件使用独立的 32 条有界发送队列，不占玩家聊天队列，也不更新 `latestChatId`、控制租约或控制代次。队列满时只丢系统提示，不因提示突发断连；下一条成功入队的系统事件以 `droppedBefore` 报告此前丢失条数。若之后再无提示，该数不会单独推送。单条文本最多 1024 个 UTF-16 字符，超长明确标记 `textTruncated`。空白提示忽略。

Python Bridge 将该事件与玩家聊天、命令回执分别分发，并验证字段、对近期重复 eventId 去重。Agent 只保留最近 32 条，记录缓存淘汰和发送端丢失计数：

- 系统提示本身不调用 LLM、不发聊天、不修改历史或打断正在处理的玩家问题。
- 下一条玩家聊天启动时，在本次模型上下文中插入 `recentGameEvents` 与覆盖说明；不将该块反复追加到持久聊天历史。
- 模型运行期间新到达的提示留待后续玩家聊天使用，不重启当前请求。
- 提示作为普通 JSON 数据，不作为系统指令。系统提示中的累计击杀数不能解释成当前画面中的敌人数。

仅覆盖当前 TCP 连接期间的近期提示，不重播连接前历史。没有消息不能证明没有发生相应游戏事件；不涵盖所有 UI 数值、浮动战斗文字或没有调用这些入口的 Mod 自绘提示。未来如有具体遗漏，依据入口诊断补接，而不假定所有画面文字都已捕获。

## 诊断与验证

启用 `TERRARIA_AGENT_TRACE=1` 后记录 `game_message_received`，包含来源、原始提示、客户端更新号／接收时间、截断标记、发送端丢失计数及缓存淘汰计数。无需新增模型调用来诊断事件。没有新增默认逐帧日志，也不记录 API Key。

新增 C# 测试覆盖嵌套去重、自身回复／缓存抑制、异常后恢复、真实 loopback TCP 提示、死亡期间聊天身份保留及截断。Python 测试覆盖去重和回执交错、字段错误、1000 条提示缓存有界且零模型调用、提示不取消已有回复、后续聊天获得近期事件且历史不累积事件块。原聊天、抢占、Observation、死亡生命周期和控制回归一并执行。

构建产物隔离于 `.local/game-messages-build/Mods/CompanionBridge.tmod`，不部署、不启动游戏。真实验收仍需：多人旗帜击杀里程碑、本人及其他玩家死亡、单人本地提示、本人聊天无回声触发；随后询问“刚才发生了什么／击杀提示是多少”，核对 Trace 和回答，并确认连续系统提示不会单独触发模型请求。

本次结果：161 项 Python 离线测试通过，新增系统提示测试 5 项；C# 新增捕获／传输及全部原有回归通过；compileall、git diff --check 通过。本机 Mod 0.1.22 构建完成，0 错误、0 警告。未调用真实模型，未启动或重启游戏，未部署、提交或推送。
