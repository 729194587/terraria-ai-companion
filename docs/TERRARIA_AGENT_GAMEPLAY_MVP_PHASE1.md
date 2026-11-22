# Terraria Agent Gameplay MVP 第一阶段实现记录

本阶段依据 [Gameplay MVP v0.1 设计](TERRARIA_AGENT_GAMEPLAY_MVP_DESIGN_v0.1.md) 实现。Server AI 系统提示先区分聊天、只读查询和行动委托。明确委托且能安全推进时，本轮使用现有 Lua 工具完成观察、行动和核验，再按实际回执回复。Manifest 和 API Contract 纳入握手开放的功能装备和原版绳索接口。系统提示复用现有动态上下文。

## 注册与调用

在 [内置玩法攻略目录](../skills/) 增加受版本管理的 `<ID>/SKILL.md`。文件使用 UTF-8，`---` 围起的 YAML 头包含 `name`、`description`、`game_version`、`scope` 四个单行文本字段，随后是 Markdown 正文。ID 与目录名一致，匹配 `[a-z][a-z0-9_]{0,63}`。每篇最多 16 KiB，描述最多 160 字符，最多注册 32 篇。新增 Skill 或修改元数据后重启 Python Agent 刷新索引。

系统提示的 `available_skills` 仅注入 ID 和描述。`read_skill({"name":"mining_and_ores"})` 按需读取正文，返回 `valid`、`name`、`description`、`game_version`、`scope` 和 `content`。未知 ID 返回 `unknown_skill`，非法参数由 Agent 返回工具错误。模型输入只用于查表。加载一次占一次模型工具调用，Lua 的查询、动作与运行时间预算保持原值。Markdown 与示例均作为文本回到模型，实际游戏操作仍由现有 `execute_lua`、Lua Runtime 和 IPC 链路执行。

[挖矿与矿石攻略](../skills/mining_and_ores/SKILL.md) 覆盖局部识别、安全站位、单格挖掘、按能力接近，以及矿格移除和库存增量的分别核验。参考来源列于攻略末尾。攻略适用于现有 Server AI 接口，具体可达性、开路许可和挖掘效果取决于当前游戏状态与宿主回执。

## 验证范围

初版的两组定向 Python 回归通过，共 73 个不同用例，覆盖注册与非法路径拒绝、Server AI 能力握手、装备回执、聊天入口、结构化挖掘、场景层和任务协作。

运行组为 `test_gameplay_skills`、`test_server_ai_contract`、`test_server_ai_equipment` 和 `test_mine_target.MineTaskAgentTests`（17 个用例），以及 `test_mine_target`、`test_server_ai_agent`、`test_scene_layers`、`test_server_ai_scene` 和 `test_m3_program_tasks`（57 个用例，含前组的一项集成测试）。系统提示测量脚本和 `git diff --check` 通过。

第一阶段提供仓库内置攻略。外部攻略、全局寻矿、连续开采与拾取归因仍受现有能力限制。原生挖掘实机和真实模型的行为仍待验证。

## 完整采矿示例

攻略现在提供一段可直接提交给 `execute_lua` 的单格铜矿程序。它在局部快照中筛选与角色同高、侧面暴露的近处矿格，复查目标和移除后的脚底支撑，原样传递场景签发的 `mine_target` 令牌。结果分别记录原生动作核验和同一角色的库存净增。该筛选主动排除局部危险、未知几何和远处矿格。

本次 13 个不同的定向用例通过，覆盖 `test_mine_target`、`test_gameplay_skills` 和 `test_mining_support_projection`。聊天集成使用替代模型、真实 Lua Worker 与本地回环 Bridge，原样执行 Skill 的单段 Lua，核对传入游戏端的结构化目标、调用顺序及入包结果。另有同一示例的危险与支撑停机、挖掘成功但入包未确认用例。

用回环集成 Trace 验证了下方指标提取命令。该次记录从聊天接收到挖掘派发为约 941 ms，共 3 次替代模型调用，模型工具顺序为 `read_skill → execute_lua`。替代回执显示矿格移除及库存净增 1，整体目标仍为 `unknown`。替代模型未报告 Token 用量，这组数据只验证提取流程。

## DeepSeek 实测步骤

1. 使用对应当前提交的 CompanionBridge 包。运行下列命令构建包和准备隔离 profile。`-SeedProfile` 可选，使用时指向已准备好的测试场景副本。执行准备命令打印的 launcher，再从游戏进入 **Multiplayer → Host & Play**。

   ```powershell
   pwsh -NoProfile -File scripts/build-companion.ps1 -OutputPath .local/agent-build
   pwsh -NoProfile -File scripts/prepare-companion-host-play.ps1 -PackagePath .local/agent-build/Mods/CompanionBridge.tmod
   ```

2. 在复制的世界中准备平坦、干燥的原版测试区域。可用 `& .local/lua-venv/Scripts/python.exe scripts/ai_player_state.py --port 18779` 读取 AI 状态。让 AI 在稳定地面停住，主背包有可用的镐和可容纳铜矿的空间。留一个独立铜矿格，与 AI 身体中段同高，放在侧面相隔一格左右的位置，留出无阻挡的水平通道，矿格不充当脚底支撑。记录 AI 初始位置、目标 Tile 坐标、矿石库存基数及游戏难度。场景要求局部 45×29 范围无液体、接触危险、敌怪和未知几何。保留这份初始 profile，重复实验时从同一份副本启动。
3. 按 `.env.example` 在本地配置 `LLM_BASE_URL=https://api.deepseek.com`、`LLM_MODEL=deepseek-flash` 和自己的密钥。Agent 会发送 `thinking.type=disabled`、`tool_choice=auto`。启动新 Agent 会话并启用 Trace。以下命令使用默认端口，自定义端口时与 launcher 对齐。 [DeepSeek Chat Completions API](https://api-docs.deepseek.com/zh-cn/api/create-chat-completion/)

   ```powershell
   $env:TERRARIA_AGENT_TRACE = '1'
   pwsh -NoProfile -File scripts/start-server-ai-agent.ps1
   ```

4. 从本地游戏聊天只发送一次以下指令，等 AI 回复并在 Agent 中记录 `agent_end`。记录实际选择和结果。预期路径是 `read_skill → execute_lua → 文本回复`，Lua 顺序是 `observe_scene → inspect_tile → inspect_inventory → mine_target → inspect_inventory`。出现危险、拒绝或未知结果时，保留本次记录并恢复初始场景再测。

   > 请参考 mining_and_ores 攻略，用完整 Lua 示例采一格附近暴露的铜矿，允许任务在安全前提下按现有契约局部开路。完成后告诉我目标格是否移除，以及背包铜矿净增多少。

5. 在另一 PowerShell 中提取同一会话、同一聊天轮次的记录。首次行动耗时以 Agent 收到聊天到第一条 `mine_target` IPC `start` 请求的差值计，单位毫秒。它衡量派发，受理及终态另看回执。模型调用数包含请求重试。Token 使用服务端报告的输入、输出和缓存命中统计，字段缺失保留未知。

   ```powershell
   $trace = Get-ChildItem .local/diagnostics/agent-trace-*.jsonl |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
   if (!$trace) { throw 'Trace file missing' }
   $tracePaths = @(($trace.FullName + '.1'), $trace.FullName) | Where-Object { Test-Path -LiteralPath $_ }
   $events = Get-Content -LiteralPath $tracePaths -Encoding utf8 |
       ForEach-Object { ConvertFrom-Json -InputObject $_ }
   $chat = $events | Where-Object {
       $_.type -eq 'chat_received' -and $_.chat.text -like '请参考 mining_and_ores 攻略*'
   } | Select-Object -Last 1
   if (!$chat) { throw 'Mining chat event missing' }
   $turn = $events | Where-Object {
       $_.chat_event_id -eq $chat.chat_event_id -and $_.generation -eq $chat.generation
   } | Sort-Object sequence
   $start = $turn | Where-Object {
       $_.type -eq 'server_ai_ipc_request' -and $_.request.op -eq 'start' -and
       $_.request.operation -eq 'mine_target'
   } | Select-Object -First 1
   $cost = $turn | Where-Object type -eq 'chat_cost' | Select-Object -Last 1
   $end = $turn | Where-Object type -eq 'agent_end' | Select-Object -Last 1
   $lua = $turn | Where-Object type -eq 'lua_tool_result'
   $mining = $lua | Where-Object operation -eq 'mine_target' | Select-Object -Last 1
   $program = $turn | Where-Object { $_.type -eq 'tool_result' -and $_.result.programCompleted -ne $null } |
       Select-Object -Last 1
   [ordered]@{
       chatId = $chat.chat_event_id
       firstActionMs = if ($start) { ([datetimeoffset]$start.timestamp - [datetimeoffset]$chat.timestamp).TotalMilliseconds }
       modelCalls = $cost.model_calls
       modelTools = @($turn | Where-Object type -eq 'tool_start' | ForEach-Object { $_.call.function.name })
       luaApis = @($lua | ForEach-Object { $_.operation })
       tokens = $cost.tokens
       actionId = $start.request.actionId
       actionStatus = $mining.result.status
       endEvidence = $mining.result.endEvidence
       inventoryNetChange = $program.result.value.inventoryNetChange
       taskOutcome = $end.task_outcome
       goalOutcome = $end.goal_outcome
       stopReason = if ($end.action_reason) { $end.action_reason } else { $program.result.reason }
       programStatus = $program.result.value.status
   } | ConvertTo-Json -Depth 10
   ```

将结果与 Git 提交、包版本、模型、固定指令及初始场景信息一起存入 `.local/`。首次派发为空时报告无派发或缺证据，并区分模型未调用工具、只读停止与工具拒绝。矿格移除以已知结束证据和已核验效果为准，库存以查询净增为准。`goalOutcome=unknown` 时保留原值。原始 Trace 含聊天和世界状态，仅在本地保管。当前尚无该流程的 DeepSeek 实测数据。
