# Terraria External Brain MVP

- 状态：MVP 已实现。自动化证据和待实测项见第 13 节。
- 目标：让 DeepSeek Harness、Codex 等外部 Agent 通过 MCP 驾驶独立 Server AI Player
- 主要参考：Numen External Brain

## 1. 目标与边界

外部 Agent 负责模型推理和决策；Terraria AI Companion 负责游戏感知、执行、动作生命周期与效果核验。

MCP 不调用模型 API。DeepSeek Harness 继续使用自身配置的 WorkBuddy/账号授权或其他 Provider；Codex 使用自身登录与额度。模型认证、额度和供应商配置留在外部 Agent。

用户自行新建游戏对话并连接 MCP。Mod 不创建、恢复或管理外部 Harness Session。接受外部 Coding Agent 仍有自身系统提示词和全局指令；新对话加游戏 Guide 是 MVP 的上下文方案，不追求强制隔离。

不重写 Lua Runtime、Task Runtime、Server AI IPC、导航、挖掘、建造、装备或动作核验；不增加游戏原子操作。

## 2. 设计决策

1. **复用现有游戏身体。** MCP 只提供外部接入，不另建游戏执行系统。
2. **MCP Server 放在 Python Agent Host。** 与现有 Runtime 共用游戏连接和执行对象。
3. **支持两种 MCP 传输。** Streamable HTTP 默认监听 `127.0.0.1` 的 `/mcp`，可让不同客户端先后连接同一个 Host。stdio 由客户端启动 Python Host，通过进程管道通信。
4. **连接即取得外部驾驶权。** 有效的外部 Brain Session 持有 AI Player 控制租约；同一时刻只有一个决策大脑。断开 Session 即释放租约，不另设 acquire/release 工具。
5. **Native Agent 与 External Brain 互斥。** 外部租约有效时，Native Agent 不发起模型请求或执行动作。释放前必须等待在途调用和游戏动作完成可信清理；`unknown` 时保持隔离，不自动恢复或重放。
6. **不管理外部 Session。** 用户创建新对话；MCP Instructions 和 Guide 提供游戏信息。
7. **不调用模型 API。** 外部 Agent 自己负责模型请求和额度。

## 3. 当前代码基础与接入位置

```text
DeepSeek Harness / Codex
        │ Streamable HTTP / stdio
        ▼
Python Agent Host
  ├─ MCP Server
  ├─ Bridge (默认端口 18777)：聊天事件
  ├─ ServerAiBridge (默认端口 18779)：Actor 绑定与游戏执行
  ├─ LuaRuntime / ToolExecutor
  ├─ TaskCoordinator
  └─ SkillRegistry / Runtime Context
        │
        ▼
CompanionBridge / Server AI IPC
        │
        ▼
独立 Server AI Player
```

MCP Server 应复用同一组 `Bridge`、`ServerAiBridge`、Lua 执行入口、TaskCoordinator 和事件队列。**不得为每个 MCP Session 或每次工具调用新建 ServerAiBridge 并重复绑定 Actor。**

当前实现入口：

- `python/agent.py`：Native Agent 生命周期、聊天事件路由和 Runtime Context。
- `python/server_ai.py`：`ServerAiBridge`、Actor bind handshake 和 IPC。
- `python/lua_agent.py`：`execute_lua` 定义、Server AI Contract 和结果摘要。
- `python/lua_runtime.py`：Lua 执行、预算、取消与清理。
- `python/program_tasks.py`：独立游戏任务协调器。
- `python/server_ai_contract.py`：动态能力清单、API Contract、Lua Runtime Reference。
- `python/gameplay_skills.py`：Skill 索引和按需读取。

外部模式不得要求初始化 `LLMClient` 或配置模型 API Key。若 Native Agent 与 MCP Host 共存，它们必须共享一个执行连接和控制权状态；外部租约期间 Native Agent 暂停。若没有本地模型 Provider，租约释放后 Host 可保持待机。

## 4. MCP 传输与认证

Streamable HTTP 默认端点：

```text
http://127.0.0.1:18790/mcp
```

stdio 使用 `--external-brain --mcp-transport stdio`。客户端启动并拥有子进程，stdout 传输 UTF-8 MCP 消息，诊断输出走 stderr 或本地日志。stdio 无 HTTP 端口和 Bearer Token。

HTTP 认证采用独立随机 Bearer Token：

- Token 仅用于访问 Terraria MCP Server，与模型 API Key 无关。
- Token 不放入 URL、普通日志或 Trace；本地持久化优先保存哈希。
- 默认只监听 loopback，不开放 LAN 或公网访问。
- 校验 `Host` / `Origin`，限制请求体大小与并发。
- MVP 不实现 OAuth、远程访问或公网反向代理。

同一工作区内，使用同一 AI IPC 端口的 External Brain 进程共用独占锁，HTTP 与 stdio 之间也互斥。切换传输前先关闭旧 Host。进程锁的范围是当前工作区，使用不同检出目录时需要自行保证每个 AI Player 只有一个 Host。

## 5. 客户端连接配置

先进入启用 Server AI 的 Host & Play 世界。使用 stdio 时，在 DeepSeek Harness Desktop 的 `cordis.patch.yml` 中添加以下条目。如果已经配置了同名 HTTP 条目，请替换它。Windows 路径按本机安装位置填写。

```yaml
- insert:
    - id: mcp-terraria
      name: '@deepseek-ai/dsh-mcp-client'
      config:
        serverName: terraria
        transport: stdio
        command: 'D:/Agent Project/terraria-ai-companion/.local/lua-venv/Scripts/python.exe'
        cwd: 'D:/Agent Project/terraria-ai-companion'
        args: ['python/agent.py', '--server-ai', '--external-brain', '--mcp-transport', 'stdio', '--timeout', '5']
        toolCallTimeoutMs: 90000
        reconnect:
          enabled: false
```

使用默认游戏 Bridge 端口时，上面的配置会自动启动 Host。端口非默认时，可在 `args` 追加 `--port` 和 `--ai-port`。Harness 发现工具后可直接操作，也可调用 `mcp__terraria__guide` 查看已开放的 API 名称。示例关闭了 Harness 自动重连，游戏连接故障时可确认 Actor 状态后重启 Harness。

### 本机 Host & Play 的 Mod 开关（Windows）

先安装仓库的 Python 环境及 `python/requirements-mcp.txt`。在 Windows 用户环境中一次性配置随机凭据；已有凭据时继续使用原值。在 PowerShell 7 中执行：

```powershell
if (![Environment]::GetEnvironmentVariable('TERRARIA_MCP_TOKEN', 'User')) {
    $token = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    [Environment]::SetEnvironmentVariable('TERRARIA_MCP_TOKEN', $token, 'User')
}
```

让 Codex 从带有该环境变量的环境启动，将生效配置里的 Terraria stdio 条目替换为下方 HTTP 条目。
在 CompanionBridge 的「外部 AI（MCP）」设置中启用本机 HTTP MCP，使用已启用 Server AI 的本机 Host & Play 配置进入世界。
新版 profile 启动器提供项目目录；旧 profile 可在设置中填写绝对路径。游戏聊天显示 `MCP ready` 后即可打开 Codex 对话。
退出世界、关闭开关或关闭游戏会释放该游戏拥有的宿主。启动失败请查看游戏的 client.log；修正后重新切换开关。
首次握手因其他对话占用而失败时，结束占用对话并在原客户端重载 MCP。手动管理 Host 时保持 Mod 开关关闭。

DeepSeek Harness 的 HTTP 插件条目如下：

```yaml
- id: mcp-terraria
  name: '@deepseek-ai/dsh-mcp-client'
  config:
    serverName: terraria
    transport: streamable-http
    url: http://127.0.0.1:18790/mcp
    toolCallTimeoutMs: 90000
    headers:
      Authorization: !!js '`Bearer ${process.env.TERRARIA_MCP_TOKEN}`'
```

Codex（`~/.codex/config.toml`）：

```toml
[mcp_servers.terraria]
url = "http://127.0.0.1:18790/mcp"
bearer_token_env_var = "TERRARIA_MCP_TOKEN"
tool_timeout_sec = 90
default_tools_approval_mode = "prompt"
```

两者继续使用各自已经配置的模型登录、Provider 或额度；MCP 只提供游戏工具，不代替模型认证。

## 5. Instructions、Guide 与 Skills

### MCP Instructions

初始化时返回角色身份、工具用法与任务取消规则。外脑可直接根据玩家目标选择工具。握手支持方向探索时，首屏也会说明 `explore` 的用法。

### Guide

- `guide({})` 按当前连接列出可用游戏 API 的分类与函数名，并附上已注册的玩法攻略索引。有私人笔记时可读取同一角色的笔记索引。
- `guide({"names":["get_player_state"]})` 返回指定函数的参数定义。需要返回结构或示例时，用 Lua 调用 `api("help",{name="get_player_state"})`。
- `guide({"full":true})` 按需展开完整原生契约，包括 Lua Runtime Reference、动作与任务回执规则。

### Lua API Help

参考 Numen 从登记信息生成 API 帮助的做法，Terraria 使用已有的 `api(name, args)` 调用形态。以下每段可单独作为 `execute_lua.source` 提交。 [Numen HelpApi](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/numen-api/common/src/main/java/com/dwinovo/numen/api/sdk/HelpApi.java)

```lua
return api("help")
```

在回执的 `value.categories` 读取当前握手开放的分类和函数。查询一个函数时：

```lua
return api("help", {name="get_player_state"})
```

详情的 `description` 来自现有执行契约，包含用途和函数专属限制。`parameters` 描述当前后端接受的参数，`returns` 概述返回结构。`value.returnsSchema` 是该 API 的 Draft 2020-12 JSON Schema，包含逐字段类型、必填项、可空项和状态分支，`$ref` 均指向同一份文档内的 `$defs`。分类索引同时给出名称和由当前参数 Schema 生成的短签名。完整参数与结果 Schema 可按函数名查询。

只有参数或结果结构随模式变化的接口才提供按参数帮助。当前有 `inspect_inventory`、`observe_scene`、`query_map` 和 `scan_entities`，guide 根据握手列出可用的子集。例如，`api("help",{name="query_map",forArgs={mode="overview"}})` 只列出总览模式可用的参数。

注册表离线审计覆盖 80 个原子操作和 5 个任务 API。在扩展能力测试配置下，71 个直接入口各有一条索引记录和一份完整帮助；另有 5 个操作通过 `submit_task.operation` 提交，9 个共享操作不在服务器白名单内。可运行 `test_server_ai_contract.ResultRegistryTests.test_registry_index_and_full_help` 核对当前注册表。

`statusSemantics` 说明如何解读回执，`limits` 给出类别边界，`example` 提供 Lua 调用示例。已知但当前不可调用的函数返回 `not_exposed`，未知名称返回 `unknown_api`，非法参数返回 `bad_argument`。如果开放的函数缺少已登记的返回契约，帮助返回 `contract_missing`。

依据帮助编写实际查询，例如：

```lua
local result = api("get_player_state", {})
if not result.valid then return result end
return {player=result.player, motion=result.motion}
```

读取回执时使用 Schema 中的实际字段大小写。`get_player_state` 成功时把 `player` 与 `motion` 放在顶层，其他观察通常把内容放在 `data` 下。`binding` 由 Python Bridge 从当前连接附加。`observe_scene` 只有握手开放区域能力才会描述区域结果，其字段由 `includedLayers` 决定。Lua 中用 `json.null` 判断显式空值，用 `nil` 判断字段缺失。

动作的 `valid`、`completed`、`inputsReleased` 和 `effectVerified` 含义各自独立。停止无动作时可能只返回 `noOp=true`。动作或游戏任务进入失败、取消、超时或 `unknown` 分支时，宿主可能终止 Lua，回执留在已有的 `lastAction` 或任务结果中。查询 `await_task` 返回 `waitStatus="timeout"` 时，游戏任务仍可能运行。

v1.2.1 区分宿主的 `supervisorStopRequest` 和动作本身的终态。宿主超时请求停止时，原生动作可能已经完成。若所有权或释放无法确认，回执可能只有 `valid`、`status`、`reason` 等字段。快捷栏选择用 `selectionVerified` 确认，`inputsReleased` 和 `effectVerified` 为 null。其他原生动作使用绑定的释放回执与各自动作证据。字段之间的身份、数值和目标匹配仍由执行器核验。外层 `cleanup` 的超时诊断和 `payloadOmitted` 摘要属于程序回执。

同步 `mine_target` 成功返回采矿动作回执。若提交阶段发生任务冲突，Lua 程序以失败结束，`value` 不包含 `submit_task` 的拒绝对象。任务已受理或受理结果不确定时，可按程序状态、`lastAction` 和任务结果判断进度。

每次帮助查询占一次 Lua API 调用，受原有 Worker 预算、权限和取消限制。帮助由 Python Host 从当前握手和 Schema 生成，本身无游戏 RPC、动作或新鲜观察。MCP 回执仍附带已有运行时上下文。

v1.2 的定向验证使用 `python/requirements-test.txt`。完整能力握手覆盖 30 个原子 API 和 5 个 Program Task API。测试检查 Schema 和引用闭包、字段正负例、Lua Worker 的调用预算以及 MCP Guide 到帮助的路径。离线 C# TCP 宿主用模拟数据验证玩家观察、不可用分支、挖掘回执和任务视图的跨语言投影。隔离 tModLoader 专用服验证了 15 个只读样本，涵盖全部 10 个观察 API。本次未重新执行实机变更动作，深层运动诊断和任务异常分支的类型由源码及测试样本核对。

引入返回 Schema 前，隔离 tModLoader 专用服曾使用 DeepSeek Harness `0.2.2-rc.1` 自带 MCP 插件验证帮助索引、`attack_entity` 的 `not_exposed` 响应及同一会话内的原生玩家观察。

### Skills

- `read_skill` 复用 `gameplay_skills.TOOL` 和 `SkillRegistry.load()`。
- Skill 索引由 Guide 中的 `available_skills` 提供。
- 不建立第二套 Skill 存储或索引，不把全部 Skill 正文塞入 Instructions。

DeepSeek Harness 的 MCP Client 不支持 MCP Prompts，因此 Guide 使用普通 MCP Tool，而不是依赖 Prompt/Resource。

## 6. MCP 工具

| 工具 | 作用 | 实现来源 |
|---|---|---|
| `guide` | 查询能力索引、单项参数或完整契约 | 当前连接的能力分组与原生 API 定义 |
| `execute_lua` | 执行完整 Lua 程序，组合查询、动作和任务 API | 现有 `lua_agent.TOOL` + `ToolExecutor` / `LuaRuntime` |
| `explore` | 沿 left、right、up、down 分段探索，可选填 objective 或 seek | 与内置 Agent 共享 `directional_explorer.run()` |
| `exploration_status` | 查询当前位置的资源与路线，或查看上一趟的逐段证据 | 传入 `lastTrip=true` 读取行程记录 |
| `look_around` | 查看角色周围当前被照亮的地形侧视图 | 无参数。MCP 返回一张 PNG，附角色格坐标和图例。暗区保持未知，按需调用。 |
| `read_skill` | 按 ID 读取一篇玩法 Skill | `gameplay_skills.TOOL` |
| `get_events` | 有界长轮询游戏内指令和系统事件 | 现有 `Bridge` 事件 |
| `say` | 让 AI Player 在游戏内发言 | `ServerAiBridge` 原生 AI chat 路径 |

`execute_lua` 和 `read_skill` 的 Schema、描述与能力门禁复用当前代码。工具列表只暴露当前后端可用的功能。

调用 `explore({"direction":"right"})` 可沿给定方向连续探路。每段从角色实际脚下选择一个前方落区，用现有原生导航走过去，确认释放后观察，再从新位置推进。一次最多 6 段，每段目标最多约 32 格，单段等待上限 12 秒，整趟移动共用 45 秒预算。AiMap 随角色实际位置通过专服游戏更新揭示地形。预算用尽、没有前向位移、缺少可确认的落点、原生导航受阻或控制中断时，本次调用结束。

默认沿途记录资源和通道。可选 `seek` 是每段走完后检查的停止条件，例如 `chest`、`Terraria/Iron`、`passage`，或用 `any` 遇发现就停。可选 `objective` 记录此次意图。是否采集、下行、折返或继续由模型选择。路线和落点使用世界像素，资源锚点使用 Tile 坐标。

`explorationResult` 默认只给出终点、位移、段数、结束原因、最多八个资源线索、近期通道线索及当前位置的出路。去重后的线索超过八条时，先为每种 `kind` 选一条，再优先覆盖同类中的不同 `contentKey`，最后按观察顺序补足。`findingCount` 是去重后的总数。`partialResourceScan` 标记有未完整覆盖的资源观察。资源窗口采样超时后会尝试一次近区扫描，局部落点通过检查时可以继续。用 `exploration_status({"lastTrip":true})` 读取当前租约最近结束行程的逐段回执和采样覆盖；用 `exploration_status({})` 读取现场详情。

矿脉线索的 `anchor` 标识观察到的矿簇。需要采矿时，把 `targetTile` 的格坐标传给 `mine_target`。该坐标来自实际观测到的矿格；专服仍会核验矿格与采矿站位。已经确认消失的矿脉省略 `targetTile`。

`explore`、`execute_lua`、`say` 互斥，等待 `get_events` 可以并行。取消探索或关闭会话时，宿主按已分配的原生任务 ID 清理导航并核验终态和输入释放；结果不确定会隔离宿主。任务 origin 中的 MCP 请求、租约版本与 segment 可关联到原生 taskId 和释放回执。

不增加 `move`、`mine`、`place` 等重复 MCP 原子工具；这些能力继续通过 `execute_lua` 调用现有 Lua API。

### `get_events`

- 接收当前 Server AI Agent 已能接收的事件：授权的本地 AI 聊天指令与 `game_message` 系统消息。
- `wait_seconds` 范围 `0..45`；`0` 表示立即读取队列。
- 45 秒上限为常见 MCP 客户端 60 秒工具超时留出余量。
- 事件使用有界队列、唯一 `eventId` 和明确的丢失/淘汰计数。
- 取走事件只有一个权威入口，避免 `get_events` 与动作回执重复交付同一事件。

### `say`

复用 `ServerAiBridge` 现有 AI 身份发言路径及 reply-turn / Actor 身份校验。不得通过普通玩家聊天入口伪装 AI 身份。只有原生发布得到确认后，才报告发言成功。

## 7. Runtime Context 与动作回执

外部大脑与 Native Agent 应看到同一份游戏状态和动作结果。`execute_lua` 完成后，MCP 返回：

1. 现有 Lua 程序/动作结果摘要；
2. 执行期间到达且尚未交付的游戏事件；
3. 当前 Runtime Context。

`get_events` 返回事件批次时也附带 Runtime Context。应复用 Native Agent 当前构造的数据，必要时提取为共享 helper：

- `runtimeInventory`
- `runtimeLua`
- `runtimeTasks`（当前后端支持时）
- `recentGameEvents` 与覆盖/丢失计数
- 有在途或已完成动作时的 `runtimeOutcome`、任务结果和中断记录

不要为 MCP 另造一套观察协议。保留现有身份、时间、coverage、unknown 和截断语义；未知或过期状态不能伪装成当前状态。

MCP 工具调用成功不等于游戏动作成功。保留 `valid`、`status`、`completed`、`actionId`、`endEvidence`、`taskOutcome` 等现有结果语义。`unknown` 不自动重试、不重放，也不能包装成成功。

## 8. 控制权、事件与取消

### 控制租约

- 有效外部 Brain Session 建立外部驾驶租约。
- 外部租约期间，Native Agent 不发起 LLM 请求，也不执行游戏动作。
- 玩家在游戏内发给 AI 的指令转入外部事件队列，不丢弃。
- 外部 Session 结束、撤销或超时后，等待在途 MCP 调用与游戏动作完成可信清理，再释放租约。
- 只有确认没有未决动作且输入已安全释放，才允许 Native Agent 恢复。
- 如果清理结果为 `unknown`、Bridge poisoned 或 Actor/Generation 失效，保持 fail-closed，不自动切换大脑继续操作。

同一时间只允许一个外部 Brain 身份。MVP 不支持多个 Harness 并行控制同一角色，也不支持多 Actor 批量控制。

### 取消

- MCP `notifications/cancelled` 映射到现有 Lua Runtime 取消机制。
- 取消后等待现有清理流程，保留输入释放和动作结束证据。
- Lua Program 与独立游戏 Task 生命周期不同。取消 MCP `execute_lua` 不代表自动取消已受理的独立 Task；Task 仍通过现有 `cancel_task` 等 API 显式取消。
- 断连或取消后副作用无法确定时返回 `unknown` 并保持执行隔离。

## 9. 日志与诊断

复用现有诊断体系：

- Runtime 摘要：`.local/diagnostics/agent-runtime-*.jsonl`
- 完整 Trace：`TERRARIA_AGENT_TRACE=1`
- 任务证据：`.local/evidence/<task_id>/`

增加 MCP 关联字段，使调用能关联到既有执行链：

```text
mcp_session_id
  → mcp_request_id / mcp_tool_name
  → task_id / lua_run_id / source_sha256
  → lua_call_id / actionId / actionGeneration
  → ServerAiBridge connection_id / request_id
```

默认日志不得记录 Bearer Token。完整 Trace 可能包含 Lua 源码、观察和玩家数据，继续按本地敏感诊断资料管理。

## 10. 实现范围

### 本次实现

1. 在 Python Agent Host 中增加 Streamable HTTP MCP Server。
2. 增加本地 Bearer Token 认证和 MCP Session 生命周期管理。
3. 复用同一个 Server AI 执行连接、Lua Runtime、TaskCoordinator 和 SkillRegistry。
4. 实现 `guide`、`execute_lua`、`read_skill`、`get_events`、`say`。
5. 实现外部 Brain 控制租约、Native Agent 暂停/恢复和事件转发。
6. 接入现有 Runtime Context、动作回执、Trace 和取消清理。
7. 提供 DeepSeek Harness 与 Codex 的连接说明。

### 暂不实现

- 自动创建或管理 Codex / DeepSeek Harness 对话；
- 清理或替换外部 Harness 自身的 System Prompt、全局规则和历史；
- 模型 Provider、API Key、WorkBuddy 反代或订阅额度管理；
- OAuth、远程公网访问、LAN 暴露和多用户权限系统；
- 多个外部大脑同时驾驶、多 Actor 批量调度；
- MCP Resources、Prompts、Sampling、Elicitation 和 MCP Events；
- 新的 Terraria 原子操作、自动学习或长期记忆系统。

## 11. 验收标准

### 协议与连接

- MCP Inspector 能连接 `/mcp`，完成初始化、工具列表和工具调用。
- DeepSeek Harness 通过 `@deepseek-ai/dsh-mcp-client` 的 `streamable-http` 传输连接成功，工具以 `mcp__terraria__<tool>` 注册。
- Codex 能通过 Streamable HTTP 和 Bearer Token 连接同一端点。
- 外部模型调用不依赖 Terraria 项目中的模型 API Key；DSH 继续使用其自身 Provider / WorkBuddy 配置。

### 游戏功能

- `guide` 返回当前 Server AI 握手对应的 Guide 和能力清单。
- `read_skill` 能读取现有 `mining_and_ores` Skill。
- `execute_lua` 能执行只读查询和一项已支持的游戏动作，并返回可信回执。
- `get_events` 能收到授权的游戏内聊天指令和系统消息，长等待可取消。
- `say` 能以 AI Player 身份发言，并核验原生发布结果。

### 控制权与安全

- 外部 Brain 连接期间 Native Agent 不会并行调用模型或执行动作。
- 第二个不同 Brain 身份不能同时接管同一个 Actor。
- 外部 Session 断开时，在途 Program 按现有取消和清理语义收尾；未决 Task 不被误认为已取消。
- 清理可信后控制权释放；`unknown` 或身份失效时不自动恢复/重放。
- 现有 Lua Runtime、Task Runtime、Server AI IPC 和动作回执回归通过。

### 真实验收

使用全新的 DeepSeek Harness 对话，连接 MCP 后读取 Guide，完成一次观察和采矿任务，确认真实地形变化与背包结果；再用 Codex 新对话连接同一端点，完成只读观察与一项安全动作。

隔离专服场景可运行 `./tests/verify_server_ai_native.ps1 -DirectionalExplore external -KeepProfile`。脚本客户端通过 stdio 调用真实 MCP Host，在复制世界中探索、调查矿物并续行。检查输出目录的 `external-explore.json` 和 `external-host.jsonl`，其中包含原生任务 ID、段末观察和 stdio EOF 时的释放回执。

## 12. 参考资料

### Numen

参考源码固定版本：[`d0368fefed4168d1ac9aeae00af4a609edf2211b`](https://github.com/Dwinovo/minecraft-numen/commit/d0368fefed4168d1ac9aeae00af4a609edf2211b)。

- [ExternalHost.java](https://github.com/Dwinovo/minecraft-numen/blob/d0368fefed4168d1ac9aeae00af4a609edf2211b/numen-api/common/src/main/java/com/dwinovo/numen/api/mcp/server/ExternalHost.java)：MCP 到游戏能力的适配。
- [ExternalBrain.java](https://github.com/Dwinovo/minecraft-numen/blob/d0368fefed4168d1ac9aeae00af4a609edf2211b/numen-api/common/src/main/java/com/dwinovo/numen/api/mcp/server/ExternalBrain.java)：统一构造工具回执、事件和 Runtime State。
- [McpInstructions.java](https://github.com/Dwinovo/minecraft-numen/blob/d0368fefed4168d1ac9aeae00af4a609edf2211b/numen-api/common/src/main/java/com/dwinovo/numen/api/mcp/server/McpInstructions.java)：短 Instructions 与 Guide 入口。
- [ExternalHands.java](https://github.com/Dwinovo/minecraft-numen/blob/d0368fefed4168d1ac9aeae00af4a609edf2211b/numen-api/common/src/main/java/com/dwinovo/numen/api/mcp/server/ExternalHands.java)：外部接入层与游戏执行层之间的窄接口。
- [McpMode.java](https://github.com/Dwinovo/minecraft-numen/blob/d0368fefed4168d1ac9aeae00af4a609edf2211b/numen-api/common/src/main/java/com/dwinovo/numen/api/mcp/server/McpMode.java)：连接状态与内外大脑驾驶权。
- [输入管线与 External Brain 设计](https://github.com/Dwinovo/minecraft-numen/blob/d0368fefed4168d1ac9aeae00af4a609edf2211b/docs/input-pipeline.md)：端点、令牌、事件、回执和控制权。
- [MCP 实现 ADR](https://github.com/Dwinovo/minecraft-numen/blob/d0368fefed4168d1ac9aeae00af4a609edf2211b/docs/adr/control/0019-own-mcp-implementation.md)：Numen 因 Java 打包和类加载问题选择自维护 MCP。该原因不直接适用于 Python；本项目优先使用官方 Python MCP SDK，不照搬自实现协议栈的决定。

### 外部 MCP 客户端

- [DeepSeek Harness MCP Client](https://github.com/deepseek-ai/deepseek-harness/tree/master/packages/mcp/mcp-client)：支持 stdio 与 Streamable HTTP；工具注册为 `mcp__<serverName>__<tool>`；服务器 Instructions 会进入 Harness 系统提示上下文，MCP Prompts 不受支持。
- [DeepSeek Harness MCP Client 配置](https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/mcp/mcp-client/README.zh.md)。
- [Codex MCP 官方文档](https://learn.chatgpt.com/docs/extend/mcp)：支持 Streamable HTTP、Bearer Token 和 Server Instructions；工具超时可配置。

## 13. 实现、启动与验证

`python/agent.py --server-ai --external-brain` 在现有 Agent Host 中启动 MCP。它只建立一条聊天 Bridge 和一条绑定 Actor 的执行连接。默认没有本地模型，想在外部租约之间恢复 Native Agent 时才附加 `--with-native-agent` 并配置原有本地模型。请先进入已启用 Server AI 的 Host & Play 世界，再在仓库根目录运行。

```powershell
& .local/lua-venv/Scripts/python.exe -m pip install -r python/requirements-mcp.txt
$env:TERRARIA_MCP_TOKEN = & .local/lua-venv/Scripts/python.exe -c "import secrets; print(secrets.token_urlsafe(32))"
& .local/lua-venv/Scripts/python.exe python/agent.py --server-ai --external-brain
```

默认 HTTP 地址为 `http://127.0.0.1:18790/mcp`，可用 `--mcp-port` 修改。Native 和 MCP 共存时仍只运行这一个 Python Host。从已设置令牌的环境启动 HTTP 客户端，使用第 5 节的 DSH 或 Codex 配置。用 Inspector 时选择 Streamable HTTP 并设置同一 Bearer 凭据。客户端应直连 loopback，配置了 HTTP 代理时应为 loopback 设置代理例外。使用 stdio 时，安装同一份依赖后直接由客户端启动 Host。

Host 只比较令牌哈希，验证 Host 和 Origin，限制请求体为 128 KiB，并限制同时处理的 HTTP 请求数。第二个初始化请求即使使用同一令牌也无法获得已有租约。请求经认证后才进入 SDK，转交时移除 Authorization 头。Host 不持久化令牌。

### SDK 与控制权细节

- 固定官方 Python SDK `mcp==1.30.0`，使用低层 `Server` 与 `StreamableHTTPServerTransport`。保留 `initialize`、Session ID 和显式 DELETE。仅使用 POST JSON 回应，GET 返回 405，游戏事件由 `get_events` 长轮询。 [Python SDK 传输源码](https://github.com/modelcontextprotocol/python-sdk/blob/v1.30.0/src/mcp/server/streamable_http.py) · [Streamable HTTP 规范](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports)
- SDK 把 `notifications/cancelled` 交给对应请求处理器。Host 将取消传给自己持有的 Lua Program，并等待游戏清理。SDK 可先结束该 MCP 请求，宿主保留中断回执，之后可用 `get_events` 查阅。取消单次 Program 后，已受理的独立 Task 仍可运行和查询。 [Python SDK 请求取消源码](https://github.com/modelcontextprotocol/python-sdk/blob/v1.30.0/src/mcp/shared/session.py)
- HTTP 会话在空闲期间保留租约。显式 DELETE、在途 POST 提前断开、SDK Session 结束、游戏连接故障和 Host 停机都会撤销租约。空闲时客户端退出可能没有可靠信号。如果退出后新客户端仍收到 `409 Brain Lease unavailable`，请在 Python Host 窗口按 Ctrl+C，等待清理完成后重启 Host。两种传输在撤销开始时都拒绝新工具和新租约。交接时 Host 显式停止独立 Task，等待在途工具，并核验绑定身份与输入释放。无法确认时隔离 Host，需人工检查 Actor 和回执后重新启动。
- stdio 的 stdin 到达 EOF 后，SDK 会取消在途处理器，Host 等待 Lua 清理、停止独立 Task 并关闭游戏连接。游戏连接故障也会结束子进程，即使客户端仍保持 stdin 打开。空闲本身不会触发关闭。强制结束 Python 时，OS 会释放进程锁，原生 IPC 根据连接断开清理该连接拥有的动作。强制终止可能来不及确认动作结果，重新连接后先观察，按实际状态决定下一步。
- 每次外部 `execute_lua` 使用现有单 Program 预算。游戏查询和动作都通过已有 Lua API，执行类工具互斥。事件由同一个 64 项队列交付给 `get_events` 或 `execute_lua` 的结果，提供淘汰和服务端丢失计数。历史 `recentGameEvents` 用于上下文。租约结束时未取走的指令计入淘汰，防止被下一任大脑当作新授权。
- `say` 需要本租约内一条当前有效的授权本地游戏指令。它沿用客户端授权、reply-turn 与 Server AI 原生发布回执。新指令会使旧的发言授权失效。外部 Program 的取消通过 MCP 请求取消或租约撤销执行。
- 可设置 `TERRARIA_AGENT_TRACE=1` 记录 MCP Session、请求和工具关联字段。Lua 源码、Program 证据包、任务及 Server AI IPC 沿用现有诊断。原始记录包含游戏数据，仅在本地保管。

### 验收步骤

1. 用新的 Inspector 会话初始化，核对基础工具和当前能力支持的可选工具。调用 `guide`、`read_skill({"name":"mining_and_ores"})` 和 `execute_lua`，用 `return api('get_player_state',{})` 验证只读观察。第二个客户端在租约有效时应被拒绝。
2. 从游戏本地发一条 AI 指令，通过 `get_events` 获取，再调用 `say`，检查游戏显示的作者与返回的 `authorSlot`。在空队列发起长轮询，取消后验证同一 Session 仍能调用工具。
3. 在隔离存档中，用新 DSH 对话读 Guide 和挖矿 Skill，采集玩家授权的一格可达矿物，记录目标格、`lastAction.endEvidence` 与前后库存。让 DSH 结束 MCP 会话，再用新 Codex 对话读取同一端点的 Guide，执行只读观察和一项安全动作。如果旧租约仍在，按上文的 Host 停机步骤完成交接。
4. 验证 Native 共存模式时，租约期间用本地聊天发送指令，应只进入外部事件队列。结束会话后观察安全交接，再发送一条新指令验证 Native Agent 恢复。结果未知时检查隔离状态及无重放证据。
5. 使用 stdio 配置启动 Harness 并读取 `guide`，至少等待 122 秒，其间不调用 Terraria 工具。随后运行 `return api('get_player_state',{})`，核对 `status: completed` 和 `actions: 0`。关闭 Harness 后再次启动并读取 Guide，核对前一连接的 `mcp_lease_released` 日志为 `safe: true`。

本次 56 个不同的定向用例通过，覆盖外部 MCP、Native Agent、`mine_target`、独立任务、Lua 取消、Server AI 绑定和回执、Skills 与模型契约。自动化使用官方 Python MCP Client、真实回环 HTTP、现有 Python Bridge 和 Lua Worker 验证协议及五个工具，并使用 C# IPC harness 验证独立 Task 的 Program 取消和租约清理。游戏身体和模型采用测试替身。

stdio 改动通过 14 项定向用例，覆盖 Windows 子进程的协议流、Lua Worker、EOF 时取消在途动作、游戏断线、强制结束后重连、HTTP 会话和独立 Task 回归。使用已安装的 DeepSeek Harness 0.2.2-rc.1 内置 MCP 插件连接隔离的 tModLoader 专用服务器（聊天 Bridge 使用测试替身），插件自动启动 Host 并发现五个工具，空闲 122 秒后成功读取原生 Actor（`actions: 0`）。插件释放并重新连接后再次读取成功，两次会话日志均显示 `safe: true`。桌面模型发起调用、实机挖掘及入包仍待验证。
