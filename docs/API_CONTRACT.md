# API 契约

Terraria AI Companion 的模型通过 `execute_lua` 提交 Lua 程序。Lua 使用 `api(name, args)` 调用当前连接开放的游戏接口；Python 宿主负责契约校验和分发，C# Bridge 在游戏线程执行查询或动作。

## 契约分层

| 层 | 实现 | 职责 |
| --- | --- | --- |
| 输入定义 | `python/atomic_operations.py` | 操作名称、参数 Schema、描述和默认值 |
| Server AI 投影 | `python/server_ai.py`、`python/server_ai_contract.py` | 根据连接能力生成服务器模型可见 API |
| Lua Runtime | `python/lua_runtime.py`、`python/lua_worker.py` | 执行程序、管理预算、转发 API 调用 |
| Python 分发 | `python/atomic_operations.py`、`python/server_ai.py` | 校验参数、选择后端、归一化回执 |
| 游戏执行 | `CompanionBridge/ServerIpc/`、`CompanionBridge/ServerPlayerRuntime/` | IPC 校验、Actor 绑定、游戏线程调度和原生操作 |

## 能力发现

Server AI 的 API 集合由 IPC bind 握手能力、服务端白名单和模型契约共同形成。能力清单随连接生成，客户端控制与独立服务器 AI Player 分别投影。

模型可以通过 `api("help")` 查看当前可用 API，通过 `api("help", {name="..."})` 查询单项参数、返回结构、状态语义和示例。Help 来自当前连接的契约投影。

## 调用流程

```text
execute_lua
  → Lua Worker
  → api(name, args)
  → Python Schema 校验与后端分发
  → TCP / Server AI IPC
  → C# 协议和 Actor 校验
  → Terraria 游戏线程
  → 查询结果或原生动作回执
  → Lua Worker
  → Agent
```

只读查询读取当前游戏状态。动作由游戏线程和对应执行器处理；持续动作可以创建独立 Task，由游戏更新推进。

## 结果状态

| 状态 | 含义 |
| --- | --- |
| `not_admitted` | 宿主确认请求未进入游戏动作生命周期 |
| `admitted` | 请求已受理，进入动作生命周期 |
| `completed` | 操作目标及结束条件已核验 |
| `failed` | 已知未达到操作目标 |
| `cancelled` | 按取消语义终止，可能已有部分副作用 |
| `timeout` | 达到任务或执行期限 |
| `unknown` | 受理状态或副作用无法确定 |

各操作使用自己的完成条件，例如导航检查实际落脚，挖掘检查 Tile 变化，交易检查货币和库存，战斗检查目标实例的命中与死亡证据。

## Program 与 Task

Lua Program、单次 Action 和游戏侧 Task 各自拥有生命周期。Program 返回或等待结束不会自动终止已经提交的 Task。持续任务可通过 `submit_task`、`query_task`、`await_task` 和 `cancel_task` 协调。

详细执行方式见 [Lua Agent](LUA_AGENT.md)、[Lua Runtime](LUA_RUNTIME.md) 和 [游戏侧任务运行时](M1_TASK_RUNTIME.md)。

## 相关专题

- [原子操作](ATOMIC_OPERATIONS.md)：操作类别和调用语义。
- [Server AI 能力契约](SERVER_AI_CAPABILITIES.md)：能力投影和维护入口。
- [Lua 返回值编码](LUA_TABLE_ENCODING.md)：Lua Table 到 JSON 的编码。
