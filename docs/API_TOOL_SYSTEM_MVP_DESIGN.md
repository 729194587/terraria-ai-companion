# M2 --- API Contract & Tool System MVP 设计与实施文档

日期：2026-11-02

目标仓库：`729194587/terraria-ai-companion`

审查基线：PR #25，`codex/g85-server-ai-perception`，HEAD
`b6c2e46f0fd78980e405fe204945d8a42cb9fa6c`，目标分支 `main`。

本文件对应五模块架构中的 **M2：API Contract & Tool System**。它不是 M3
Program Runtime & Task Runtime 的设计文档。

------------------------------------------------------------------------

## 1. 阶段目标与边界

M2 的目标是让游戏 API
从能力发现、模型可见契约、参数校验、请求分发，到动作终态和效果回执保持一致、可验证、可长期运行。

核心原则：

-   API 只暴露当前后端真实支持且经过授权的能力。
-   Python、Lua、IPC、C# 对同一操作遵循一致的参数和结果语义。
-   参数错误、能力拒绝、服务端未受理、传输结果不确定、原生执行失败必须能够区分。
-   动作完成必须由真实游戏证据确认；启动
    ACK、脚本返回和输入释放都不能单独代表效果完成。
-   `unknown` 必须保留为不确定，不得降级成 `failed` 后自动重试。
-   在现有架构上修正缺口，不另造一套通用 API/插件框架。

M2 不负责重构 Agent Loop、上下文管理、Lua
程序执行模型或长期身体任务调度。上述职责分别属于 M1、M3。M2 可以修正 API
调用边界所需的少量衔接，但不引入后台任务 API、任务事件总线或新的 Task
Runtime。

## 2. 当前实现与差距

### 2.1 Capability Discovery 与模型契约（B1）

已有实现：

-   `ServerAiBridge` 在 bind 握手中取得服务端 capabilities。
-   `server_ai.operations_for_capabilities()`
    将能力旗标投影为可调用操作集合。
-   `server_ai_contract.exposed_operations()` 将 Bridge
    白名单、握手能力和已描述的服务器 API 取交集，按 fail-closed
    原则生成最终集合。
-   `capability_manifest()` 生成按类别组织的 Supported / Limited / Not
    exposed Manifest。
-   `lua_agent.server_contract()` 将角色原则、Manifest、API Contract 和
    Lua Runtime Reference 组合成模型上下文。
-   Server AI 对模型只暴露一个 `execute_lua` 工具；Lua 内部通过
    `api(name,args)` 同步调用已开放 API。
-   `tests/test_server_ai_contract.py` 已穷举 10 个可选 capability 的
    1024 种组合，核对 Bridge 投影、Manifest、API Contract 和示例。

这些是现有设计的核心，应保留。不要把所有原子 API
再暴露成独立模型工具，也不要为此引入完整插件注册、热加载或新的工具市场。

已知差距：

-   旧的直接工具 Agent 路径在 `agent.py`
    中静态拼装工具列表；能力受限的后端可能仍向模型展示最终无法调用的操作。若该路径对应的
    Bridge
    已提供可靠能力白名单，应按实际后端能力过滤模型可见工具；没有能力信息的旧协议必须保持兼容，不能猜测能力。
-   Manifest、API Description
    是人工维护的后端语义投影。原生支持范围变化时，需要同步更新描述与测试，不能只依赖
    Schema 自动生成。

### 2.2 Schema、参数校验与结果契约（B2）

已有实现：

-   `python/atomic_operations.py` 维护 `TOOLS` / `SCHEMAS`、通用
    `validate()` 和操作描述。
-   Python
    校验包含类型、范围、枚举、字符串格式、数组长度/唯一性、未知字段和跨字段约束。
-   `CompanionBridge/AtomicParameters.cs` 在 C#
    执行边界独立校验参数，不信任 Python 已经校验。
-   C#
    对查询和动作分别执行参数校验；服务端原生执行器还可实施后端专属限制。
-   `NativeResult` 将
    `Status`、`Reason`、`Evidence`、`InputsReleased`、`EffectVerified`、`InputsAcquired`
    和 `ReasonDetail` 分开。
-   Python `terminal()`
    对导航、挖掘、背包操作、制作、放置等执行操作进行专属效果核验。

已知差距：

1.  Python 工具定义目前为 34 项，其中
    `wait`、`observe_exploration`、`stop_action` 是 Python
    宿主侧能力，不是 C# `AtomicParameters`
    原生操作。`tests/test_atomic_operations.py` 仍硬编码
    `len(atomic.TOOLS) == 31`，与当前实现不符。应以操作集合和所有权分类断言，不用过时数量掩盖差异。
2.  JSON Schema 无法表达的跨字段规则由 Python `validate()` 和 C#
    `AtomicParameters.Validate()`
    分别实现，需要跨语言接受/拒绝一致性测试。
3.  Server AI 的模型契约将 `use_item` Schema 收窄为仅接受
    `{}`；`ServerAiNativeActions.Validate()` 也拒绝带 `target`
    的请求，但通用 Python `atomic.validate()` 仍接受 `target`。Server AI
    后端限制目前可能直到 C# 才被拒绝，Python 应在发送 RPC
    前执行相同的后端专属预校验。不能因此破坏旧客户端对通用
    `use_item(target)` 的既有支持。
4.  各 API
    的结果字段存在有意差异，但公共状态、身份、结束证据和错误分类必须保持一致；不要为了统一而丢弃操作专属证据。

### 2.3 Validation & Dispatch（B3）

当前主链路：

``` text
Server AI bind handshake
        ↓
operations_for_capabilities
        ↓
server_ai_contract.exposed_operations
        ↓
Capability Manifest + API Contract
        ↓
LLM: execute_lua(source)
        ↓
Lua Worker: api(name, args)
        ↓
LuaRuntime / atomic.dispatch
        ↓
Python 参数与能力预校验
        ↓
ServerAiBridge
        ↓ TCP / JSON
AiReadOnlyProtocol
        ├── query → ServerAiQueries
        └── start / action / cancel → AiAtomicActions
                                      ↓
                              AtomicParameters.Validate
                                      ↓
                              ServerAiNativeActions
                                      ↓
                                  游戏线程
```

应保留：

-   Worker 只传递有界 JSON，不直接持有 Terraria 对象。
-   查询与变更动作分离。
-   Python 预校验与 C# 执行边界独立校验。
-   C# 游戏状态读取和原生动作在游戏线程执行。
-   动作 Handle 与 Lua Worker 生命周期分离。
-   动作身份、World、Actor、Generation、Slot 校验。
-   `PreDispatchRejected` 用于表达 Python 本地、RPC 发送前的明确拒绝。
-   Start ACK 仅代表受理，不代表原生动作已执行或完成。

已知问题：

-   `AiAtomicActions.entries` 当前最多允许 1024 条记录，但终态 Entry
    不回收；`End()` 清除 `active`，不会从 `entries`
    删除历史记录；`Forget(connection)`
    只请求取消活动动作。服务器累计完成 1024 个动作后，可能因
    `action_registry_full` 永久拒绝新动作，直到整体 Clear。
-   现有 Python/C#
    离线测试分别使用模拟执行器或协议替身，不能完整证明两端参数接受/拒绝矩阵一致。

### 2.4 Error Semantics & Feedback（B4）

统一理解以下动作终态：

  状态          语义
  ------------- ------------------------------------------------------
  `completed`   原生动作终止，操作专属效果已验证，结束证据可信
  `failed`      已确认动作未达到操作目标；不能仅因缺少成功回执就判定
  `timeout`     截止时间耗尽，且清理与实际终态已按证据结算
  `cancelled`   取消已确认，输入释放及副作用状态按证据记录
  `unknown`     是否执行、是否产生副作用或是否释放输入无法确认

`unknown` 不等于
`failed`。输入释放不等于效果确认。取消不回滚已发生的世界变化。动作结果不确定时不得自动重放。

已知问题：

1.  **服务端明确拒绝被升级为 unknown（P1）**\
    `AiAtomicActions.Handle()` 对
    `action_busy`、`actor_not_alive`、`unsupported_operation`、`invalid_arguments`、`request_expired`、`invalid_deadline`
    等情况会在创建动作 Entry 前明确拒绝。`ServerAiBridge` 能收到明确
    `ok=false` 回执，但 `atomic_operations._dispatch()` 在 `start_task`
    已创建后遇到异常，会把 `lifecycle.uncertain` 设为 True。LuaRuntime
    随后可能把已明确未受理的请求升级为 `action_unknown` 并隔离
    Bridge。必须区分"服务端明确未受理"和"请求发出后没有可信回执"。
2.  **取消/超时请求可能覆盖原生终态（P2）**\
    `AiAtomicActions.Stop()` 调用原生 Cancel
    后，只特殊处理了"取消请求与已完成动作竞争"的情况。若原生已经明确
    `failed`，外部取消仍可能把它覆盖为 `cancelled`；若原生已经明确
    `completed`，超时请求也可能覆盖成 `timeout`。必须保留 Cancel/Poll
    实际观察到的可靠原生终态。
3.  **查询参数类型错误可能误报引擎故障（P2）**\
    `AiReadOnlyProtocol.Handle()` 的查询分支只把 `FormatException`
    映射为 `invalid_request`，其他异常映射为 `engine_unavailable`。例如
    JSON 类型错误导致 `GetString()` 抛出
    `InvalidOperationException`，会被误报为引擎不可用。应使用明确的参数校验异常分类；真正的引擎读取异常仍应是
    `engine_unavailable`。
4.  **跨层测试不足（P2）**\
    需要覆盖明确拒绝、传输不确定、终态竞争和参数错误分类，而不只是单层
    Mock。

## 3. 目标架构与职责

### 3.1 Capability 与模型可见契约

-   `server_ai.py`：握手、能力投影和 Bridge
    传输身份；`operations_for_capabilities()`
    继续作为服务端能力到操作集合的确定性映射。
-   `server_ai_contract.py`：只负责模型可见 Manifest、API 描述和 Runtime
    Reference；`exposed_operations()` 必须取握手投影、Bridge
    白名单、已描述服务器 API 的交集。
-   `lua_agent.py`：Server AI 继续只向模型暴露 `execute_lua`；完整原子
    API 契约在系统上下文中动态生成。
-   未知 capability、未知 API 和过宽的旧白名单不得扩大权限。
-   相关只读 API
    只能标记为"相关只读能力"，不能被描述为缺失变更操作的等价替代。

### 3.2 API Schema 与验证所有权

-   `atomic_operations.py` 继续维护通用 Python Schema 和参数校验。
-   C# `AtomicParameters` 继续在实际 IPC/执行边界独立校验；不能删除 C#
    校验并信任 Python。
-   Python 宿主专属操作与 C#
    原生操作应有明确集合归属。当前宿主专属集合为
    `wait`、`observe_exploration`、`stop_action`；不要要求它们出现在 C#
    原生参数验证集合中。
-   后端专属限制应在 Python 发送 RPC 前检查，同时由 C#
    保留最终防线。Server AI `use_item` 不支持 target；旧客户端通用 API
    仍可保留现有 target 能力。
-   Schema 与代码补充约束共同构成 API Contract。跨字段约束不能只写在
    Description，也不能只存在于单侧校验。

### 3.3 Query / Action 分发

-   Query 只读取状态，不获取动作控制权、不创建动作 Entry。
-   Action 必须先完成参数、能力、身份和截止时间检查，再受理。
-   C# 受理后返回 action identity/状态快照；真实原生 Start/Poll/Cancel
    在游戏线程推进。
-   Python 只有在收到同一
    actionId、session、world、actor、generation、slot
    的可信终态，并通过操作专属效果验证及结束证据检查后，才能返回
    `completed`。
-   单活动动作互斥和既有动作控制边界继续保留。
-   不将 API 调用改造成后台任务系统；长时任务与模型调用解耦属于 M3。

## 4. 必须落实的契约

### 4.1 能力与工具暴露

1.  继续以 bind handshake 为能力事实来源。
2.  `operations_for_capabilities()` 对未知旗标 fail closed。
3.  `exposed_operations()` 继续执行三方交集。
4.  Manifest 的 `callableApis`、`notExposed`、API Contract definitions
    和示例必须来自同一个最终集合。
5.  Server AI 模型工具列表必须保持只有 `execute_lua`。
6.  对仍使用直接工具列表的 Agent 路径，在 Bridge
    明确提供能力白名单时过滤不可调用工具；旧协议没有能力信息时保持既有兼容行为，不推断新能力。

### 4.2 参数 Schema 与跨语言一致性

-   对象默认拒绝未知字段；必填字段、类型、有限数值、范围、枚举、长度、数组唯一性必须明确。
-   Python `bool`
    不得作为整数通过；浮点整数值可按既有契约无损规范化，分数不得静默取整。
-   C# 对应检查必须拒绝相同的非法输入。
-   对跨字段约束建立共享测试向量或等价的跨语言接受/拒绝矩阵，至少覆盖：
    -   `mine_target`：只能是 `target` 或绝对 `x/y` 两种形式之一。
    -   `query_map`：mode 与参数组合、cellSize 限制。
    -   `scan_tiles`：边界顺序和面积上限。
    -   `inspect_inventory`：mode 与 item/slots 参数的互斥/依赖关系。
    -   背包操作：sourceSlot 与 destinationSlot 不得相同。
    -   `scan_entities`：typeIds/npcFilter 与 category 的依赖关系。
    -   `move_input`：jumpHoldUpdates 不得大于 maxUpdates。
    -   `navigate_to_landing_region`：X 区间必须有序。
-   Server AI `use_item` 的 target 限制必须在 Python 后端预校验和模型
    Schema 中一致体现，C# 继续复验。
-   用操作集合断言替代 `len(atomic.TOOLS) == 31`
    之类易过期的总数断言。明确区分通用 API、宿主专属 API、Server AI 暴露
    API、C# 原生操作。

### 4.3 动作注册表生命周期

-   活动 Entry 永远不能被回收。
-   终态 Entry 必须有界保留，不能无限累积。
-   保留近期 `actionId`
    的重复提交拒绝和终态查询能力；回收最旧终态记录时不得误删活动动作。
-   记录保留窗口内重复 actionId 必须返回
    `duplicate_action`，不能重复执行。
-   被回收的旧 actionId 查询应明确返回
    `unknown_action`；客户端必须为每次新提交生成新的 GUID
    actionId，不得依赖无限期历史记录。
-   `Forget(connection)`
    继续请求取消该连接的活动动作；连接清理不能直接删除尚未结算的活动
    Entry。
-   清理策略、容量和查询语义写入代码注释/文档，并由超过当前 1024
    条容量的离线测试验证长期运行不会永久卡死。

### 4.4 明确拒绝与不确定结果

必须区分三个层次：

-   `dispatchState=not_admitted`：本地或服务端明确拒绝，能证明动作未受理/未开始。
-   `dispatchState=admitted`：服务端已创建动作记录，后续执行由 actionId
    查询。
-   `dispatchState=unknown`：请求已发出但无法确定服务端是否受理，或回执/身份/传输丢失。

具体要求：

-   本地能力/参数预校验拒绝继续使用 `PreDispatchRejected`
    或等价的明确类型。
-   服务端在创建 Entry 之前返回的明确拒绝必须保留为确定拒绝，不能因为
    start coroutine/task 已创建就标成 uncertain。
-   TCP 超时、断线、响应缺失、响应身份不匹配等仍然是
    unknown；不得重连后自动重放原变更请求。
-   明确未受理不等于自动重试授权。是否允许 Lua
    当前程序继续，仍受现有宿主 continuation 规则约束；不扩大
    `continuationAllowed` 范围，不依据 reason 字符串放行。
-   已受理动作的 unknown
    仍须隔离/阻止后续变更，直到通过既有安全恢复边界重新建立可信状态。

### 4.5 终态竞争

-   取消/超时是停止请求，不是事实终态。
-   `Stop()` 请求原生 Cancel 后，若 Cancel/Poll 返回已确认的
    `completed`、`failed`、`timeout` 或
    `cancelled`，应按真实原生终态及其证据结算。
-   若原生结果为 `unknown` 或输入释放未确认，最终必须是 `unknown`。
-   若原生仍在执行，只有在取消和释放得到确认后才可报告
    `cancelled`/`timeout`。
-   保留独立的停止触发原因/请求原因，不能用它覆盖真实原生结果。
-   不能因为 `completed`
    与取消竞争就重复执行，也不能因为取消/超时请求先到就丢失已知的
    `failed` 或 `completed`。

### 4.6 查询参数错误分类

-   参数结构/类型/范围错误使用明确参数异常，映射为 `invalid_request`
    或稳定的 `invalid_arguments`。
-   未知查询操作映射为 `unsupported_operation`。
-   只有身份捕获、游戏线程读取或真实引擎故障才映射为
    `engine_unavailable`。
-   不要用宽泛 catch
    把参数错误和引擎故障混为一类；同时不要把真实引擎异常误报成参数错误。

### 4.7 结果与证据

保留现有结构化结果，不把所有 API 强行压成单一布尔值。

动作回执的公共事实至少保持：

-   `status`、`reason`、`reasonDetail`
-   `operation`、`actionId`、session/World/Actor/Generation/Slot 身份
-   操作专属 `evidence`
-   `endEvidence`、`endEvidenceStatus`
-   `inputsAcquired`、`inputsReleased`、`effectVerified`
-   `completed` 只能由真实 status 和对应证据推导，不能由脚本 value 或
    Start ACK 推导

查询结果继续区分
`available`、身份、采样时间、coverage、unknown、truncated
等字段。多次查询不保证同一游戏帧；不能把缺失/未加载/截断解释成空或不存在。

保留现有 operation-specific
verifier（例如导航落点、挖掘移除、背包变化、制作、放置和
`mine_target`）及 `unknown`
保守语义。若没有对应效果证据，不得为了统一结构而降低完成门槛。

## 5. 文件职责与实施落点

  ------------------------------------------------------------------------------------------------------------
  文件                                                   M2 职责与预期处理
  ------------------------------------------------------ -----------------------------------------------------
  `python/server_ai.py`                                  保留 bind handshake、能力投影和 Bridge
                                                         身份/传输约束；明确服务端拒绝与传输不确定的异常类别

  `python/server_ai_contract.py`                         保留 Manifest 和动态契约生成；确保最终暴露集合与
                                                         Bridge/握手一致

  `python/atomic_operations.py`                          通用 Schema、Python
                                                         参数校验、后端预校验、Query/Action
                                                         dispatch、终态与效果验证

  `python/lua_agent.py`                                  Server AI 继续只暴露 `execute_lua`；确保 API
                                                         Contract/Runtime Reference 与实际后端能力一致

  `python/agent.py`                                      如直接工具路径仍需能力过滤，只做与已有 Bridge
                                                         能力信息相容的工具投影，不重写 Agent Loop

  `CompanionBridge/AtomicParameters.cs`                  保留独立 C# 校验；补齐异常类型与 Python
                                                         接受/拒绝矩阵的一致性

  `CompanionBridge/ServerIpc/AiReadOnlyProtocol.cs`      区分参数异常和引擎故障，保持 binding/identity
                                                         fail-closed

  `CompanionBridge/ServerIpc/AiAtomicActions.cs`         修复终态 Entry 回收、明确拒绝和取消/超时终态竞争

  `CompanionBridge/ServerIpc/ServerAiNativeActions.cs`   保留原生执行前最终校验和后端限制；与 Python Server AI
                                                         预校验一致

  `tests/test_server_ai_contract.py`                     能力组合、Manifest、工具投影和 Schema 快照测试

  `tests/test_atomic_operations.py`                      参数校验、Host-only/API 集合归属和 dispatch
                                                         预校验测试

  `tests/test_server_ai_backend.py`                      Loopback IPC、明确拒绝与传输不确定区分

  `tests/test_atomic_result_contract.py`                 结果语义、身份、结束证据和竞态回归

  `tests/ServerAiReadProtocolTests.cs`                   查询协议、参数错误和引擎错误分类

  `tests/ServerAiAtomicOfflineTests.cs`                  动作受理、拒绝、终态、重复 ID、注册表回收和身份保护

  `tests/ServerAiNativeActionOfflineTests.cs` / 相关 C#  原生参数校验和执行器边界
  测试
  ------------------------------------------------------------------------------------------------------------

Codex
可以根据源码现状调整具体文件拆分，但必须保持上述职责和行为契约，不要求机械地按文件列表改动。

## 6. 实施要求

直接实施完整 M2 范围，不需要先提交独立计划等待批准。

建议按以下工作流组织改动：

### M2.1 --- 能力与 API Contract 对齐

-   核对 capability 到操作集合的唯一投影。
-   对有能力信息的直接工具后端过滤不可用 API。
-   检查 Manifest、Schema、Description 和 Runtime Reference 的一致性。
-   区分 Host-only、C# 原生和 Server AI 暴露 API。
-   修正过时的 API 数量断言。

### M2.2 --- Python/C# Validation 一致性

-   建立共享测试向量或等价跨语言矩阵。
-   补齐跨字段约束的一致性。
-   加入 Server AI 后端专属 Python 预校验。
-   保留 C# 独立校验。

### M2.3 --- Dispatch、生命周期与错误语义

-   区分明确未受理和传输 unknown。
-   修复动作注册表终态记录回收。
-   修复取消/超时覆盖原生终态。
-   修复查询参数异常分类。
-   保留 actionId、身份绑定、效果证据和输入释放门禁。

### M2.4 --- 回归、文档与验收

-   运行定向 Python 测试和现有 C# 离线测试。
-   更新 API/能力文档。
-   对未通过测试进行基线对照，明确新增失败与既有失败。
-   汇总实际修改、测试结果和剩余限制。

这些是 Codex 完成整个 M2
的内部实施分组，不要求用户在每一小组之间重新确认。完成 M2
后再进行整体审查。

## 7. 验收标准

### 能力与模型契约

-   Server AI 模型工具列表仍只有 `execute_lua`。
-   Manifest、API Contract 和示例严格匹配最终能力集合。
-   未知 capability 和过宽白名单不扩大权限。
-   有可靠能力信息的直接工具路径不再向模型暴露明确不可用操作。
-   旧协议兼容路径不被臆测性过滤破坏。

### Schema 与校验

-   Host-only API 与 C# 原生 API 的归属有明确集合和测试。
-   不再依赖 `31` 这种过时总数判断正确性。
-   Python 与 C# 对共享 API 的有效/无效参数矩阵一致。
-   Server AI `use_item(target)` 在 Python 发送 RPC
    前被拒绝，通用客户端行为保持兼容。
-   C# 执行边界仍独立验证参数。

### 分发与注册表

-   明确拒绝不会创建动作，也不会被报告为 unknown。
-   传输不确定仍保持 unknown，不自动重放。
-   超过 1024 次连续完成动作后，服务端仍可继续受理新动作。
-   活动 Entry 不被清理；近期重复 actionId 不会重复执行。
-   旧记录被回收后，查询结果明确，不伪造历史终态。

### 终态与错误

-   `failed` 不被外部取消覆盖。
-   已确认 `completed` 不被后到的 timeout/cancel 请求覆盖。
-   原生终态不确定或输入释放未确认时仍为 unknown。
-   错误参数不再误报 `engine_unavailable`。
-   Start ACK 不被当成动作完成。
-   只有操作专属效果证据和可信结束证据齐备时才能报告 completed。

### 测试与交付

-   相关 Python 定向测试通过。
-   C# Server AI 协议、参数和动作离线测试通过。
-   新增回归测试覆盖本文件列出的 P1/P2 问题。
-   `git diff --check` 通过。
-   Codex
    报告修改文件、关键设计决策、测试命令/结果、未解决问题和验证限制。
-   不把未运行的全量测试、实机测试或多人验收描述为已通过。

## 8. 明确非目标

M2 不实现：

-   M3 的 Program Runtime & Task Runtime 重构。
-   `setTask` / `task_finished` 等后台任务 API 或新的事件总线。
-   通用插件注册、热加载、动态代码扩展或 API 市场。
-   并发身体任务调度、任务抢占/恢复体系。
-   新的导航、感知、挖掘、建造或战斗算法。
-   全量重写 Python Agent、LuaRuntime 或 C# 原生执行器。
-   为追求抽象统一而删除操作专属 evidence 或放宽 `unknown` 门禁。

## 9. Numen 对照结论

Numen 对 API 层最值得借鉴的是：

-   小而有界的观察、背包和交互原语。
-   API 组合由上层程序完成，而不是把每个复合行为都做成巨大工具。
-   直接调用原生游戏规则，并返回可核验的真实结果。
-   API 描述应明确前置条件、支持范围和结果语义。

Terraria AI Companion 已经具备类似的基本方向：单一 `execute_lua`
入口、Lua 内部 `api(name,args)`、有界原子 API、C# 原生执行和结构化
evidence。

因此 M2 应收敛现有契约和执行边界，不复制 Minecraft 三维参数，不引入完整
Numen 工具注册系统，也不提前引入 Numen 的后台任务生命周期。后者属于 M3
的审查与设计范围。

## 10. 最终设计结论

M2 的工作不是重新发明工具系统，而是把现有 API Contract & Tool System
中已经形成的能力边界、参数契约、分发语义和证据规则真正闭合。

保留 Capability Handshake、动态 Manifest、单一 `execute_lua`
模型入口、Python/C# 双重校验、Query/Action 分离、原生动作 Handle 和
operation-specific evidence。

优先修复明确拒绝与 unknown
混淆、终态竞争覆盖、动作注册表永久增长、查询错误分类和跨语言校验漂移。

完成后，M2 应提供一套可发现、可调用、可验证、错误语义一致且能长期运行的
API 基础设施，为 M3 Program Runtime & Task Runtime 提供稳定契约。
