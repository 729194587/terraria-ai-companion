# API 契约

## 模型入口

模型只通过 Agent 注册的 `execute_lua` 工具提交程序。Lua 程序使用 `api(name, args)` 调用宿主开放的游戏接口；模型不直接调用 TCP/IPC，也不直接访问 Terraria 对象。

## 契约来源

| 层 | 权威来源 | 职责 |
| --- | --- | --- |
| 公共 Schema | `python/atomic_operations.py` 的 `TOOLS` / `SCHEMAS` | 操作名称、参数类型、必填项和通用约束 |
| Server AI 投影 | `python/server_ai.py`、`python/server_ai_contract.py` | 根据能力握手和服务端白名单生成模型可见 API |
| Python 宿主 | `python/atomic_operations.py`、`python/server_ai.py` | 参数校验、后端收窄、分发、预算和回执归一化 |
| 游戏侧执行 | `CompanionBridge/ServerIpc/`、`CompanionBridge/ServerPlayerRuntime/` | IPC 校验、Actor 绑定、游戏线程调度、原生动作和效果证据 |

修改接口时必须沿完整调用链同步契约；只修改模型描述或 Manifest 不会开放执行能力。

## 能力发现

Server AI 工具集合来自实际 IPC bind 握手。Python 将握手能力、Bridge 白名单和已定义操作取交集；未知能力不扩大权限。旧服务器缺少某能力时，应在 RPC/动作派发前拒绝，不假设它支持新参数或会自动回退到旧行为。

客户端控制与独立 Server AI Player 是不同后端。同名操作可能有不同参数和更窄的服务器支持范围。应按连接后端生成工具列表，不将客户端能力清单复制给服务器模型。

## 校验与分发

- Python 使用共享 JSON Schema 校验类型、范围、必填项和额外属性，并对整数值数字做无损归一化。
- Server AI 可在共享校验后增加后端限制；后端限制不能放宽共享 Schema。
- C# 对来自 IPC 的请求再次校验协议、能力、Actor 身份、World、Generation、Slot 和游戏状态。
- Query 与 Action 分开处理。只读查询不应意外取得输入或修改世界；动作由游戏线程和对应原生执行器处理。
- Lua 不能声明自身有权继续执行，也不能覆盖宿主给出的动作终态或失败屏障。

## 结果状态

| 字段/状态 | 含义 |
| --- | --- |
| `not_admitted` | 宿主确认未受理；没有创建游戏动作 |
| `admitted` | 请求已受理进入动作生命周期；不代表原生效果已发生 |
| `unknown` | 受理或副作用无法确定；保持隔离，不自动重放 |
| `completed` | 操作专属效果及结束条件均已核验 |
| `failed` | 已知未达到操作目标 |
| `cancelled` | 按取消语义终止；可能已有部分副作用 |
| `timeout` | 截止时间到达；不等同于安全完成或回滚 |

输入释放、原生函数返回、IPC ACK、Lua 正常返回都不能单独证明目标完成。具体操作需要核验自己的效果：例如 Tile 是否被本动作破坏、物品数量是否变化、放置物体是否完整存在、角色是否真实落地并停稳。

## 预算和任务

Lua 调用次数、动作次数、指令/内存和墙钟时间由宿主监督。持续游戏动作可以有独立 Action/Task 生命周期；Program 返回或等待超时不自动取消已受理的游戏 Task。任务 API 见 [游戏侧任务运行时](M1_TASK_RUNTIME.md)，Lua 预算和取消见 [Lua Runtime](LUA_RUNTIME.md)。

## 相关专题

- [原子操作](ATOMIC_OPERATIONS.md)：调用与结果语义。
- [Server AI 能力契约](SERVER_AI_CAPABILITIES.md)：能力白名单与模型投影。
- [Server AI 动作](SERVER_AI_ACTIONS.md)：服务器侧原生执行边界。
- [背包操作](INVENTORY_OPERATIONS.md)：库存 Token 和物品变更。

## Server AI 的 Lua Help

`return api("help")` 返回当前连接的分类索引。用 `return api("help",{name="craft_recipe"})` 查询具体操作。`parameters` 取自该连接的函数定义，`returnsSchema` 是包含所引用 `$defs` 的独立 JSON Schema。查询未开放的操作返回 `not_exposed`。Help 在宿主本地读取元数据，占用一次 Lua 调用预算。

`returns` 和 `statusSemantics` 解释回执字段与终态。`prerequisites` 说明需要的授权或观察，`examples` 提供当前连接可以调用的 Lua 示例，`related` 列出已开放的相关操作。依赖缺失时示例数组可为空。旧调用方仍可读取字符串 `example`。箱子搬运示例先读取两侧槽位，购买示例先扫描 NPC，再用同一实例的报价令牌购买。
