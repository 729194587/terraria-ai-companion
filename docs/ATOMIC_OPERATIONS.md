# 原子操作契约

原子操作是 Agent 可调用的单一游戏意图。模型只通过 `execute_lua` 提交程序；Lua 的 `api(name, args)` 由 Python 宿主验证、按后端能力过滤，再经 IPC 交给 C# 游戏侧执行。

## 定义与开放

- 参数 Schema、工具名称和默认描述定义在 `python/atomic_operations.py` 的 `TOOLS` / `SCHEMAS`。
- Server AI 的能力投影由 `python/server_ai.py` 的 `operations_for_capabilities()` 和 `python/server_ai_contract.py` 的 `exposed_operations()` 完成。
- C# Bridge 仍需执行自己的协议、能力、参数、Actor 和游戏状态校验。Python 白名单或模型可见工具不是执行许可。
- 客户端与 Server AI 的工具集合不相同；同名操作可能有更窄的服务器参数或支持范围。以实际连接的能力握手和对应后端契约为准。

## 操作类别

| 类别 | 代表操作 | 语义 |
| --- | --- | --- |
| 只读观察 | `get_player_state`、`observe_terrain`、`observe_scene`、`inspect_tile`、`scan_tiles`、`scan_entities`、`inspect_inventory`、`query_map` | 返回带范围、身份、时间或截断信息的快照；不是执行许可 |
| 角色与物品 | `select_hotbar_slot`、`use_item`、`transfer_inventory_item`、`swap_inventory_slots`、`hold_inventory_item` | 依赖当前 Player、选中物品或新鲜库存 Token；按操作核验原生状态 |
| 世界修改 | `move_input`、`navigate_to_x`、`navigate_to_landing_region`、`mine_tile`、`mine_wall`、`place_tile`、`place_wall`、`place_object`、`hammer_tile`、`interact_tile`、`craft_recipe` | 由游戏线程调用 Terraria 原生路径；动作完成需操作专属效果证据 |
| 复合任务 | `mine_target` | 游戏侧任务运行时管理生命周期，内部复用导航和挖掘动作 |
| 宿主控制 | `wait`、`stop_action`、`observe_exploration` | 等待、取消或有界探索辅助；不直接代表游戏效果 |

完整参数、范围和当前后端支持列表以代码 Schema 和能力契约为准，不在文档中复制一份易漂移的 API 清单。

## 坐标与观察

- Tile 坐标以格为单位，世界像素以像素为单位；Tile 为 16×16 像素，Y 轴向下。每个 API 的坐标类型由 Schema/描述明确。
- 观察结果必须同时考虑 World/Actor/Generation 身份、采样 Tick、bounds、coverage、unknown 和截断标记。
- 未返回的对象、Tile 或层不等于不存在或为空；未加载、未采样、超范围和未知 Mod 行为不能折叠为空气。
- 观察候选是有界快照，不保证目标仍存在、可达、可放置或可执行。执行前仍需重新校验。

## 结果与失败语义

- `dispatchState=not_admitted`：宿主确认请求未受理，尚未创建游戏动作。
- `dispatchState=admitted`：动作已进入执行生命周期；不代表动作已开始或效果已发生。
- `dispatchState=unknown`：请求是否受理或副作用无法确定。不得自动重放或换入口重试。
- `completed` 必须由对应操作的实际效果和结束证据支持；输入释放、原生函数返回或程序正常结束都不足以单独证明成功。
- `failed`、`cancelled`、`timeout` 与 `unknown` 各自保留语义。取消可能发生在部分副作用之后，不能假定回滚。
- 结束证据应区分目标变化、物品数量/实例、位置/支撑、输入释放和任务状态。某一项已确认不代表其他项也已确认。

## 库存操作

库存变更只作用于受支持的普通背包槽。调用方先读取目标槽及新鲜 Token，再提交搬运、交换或快捷栏持用。Token 绑定 World、Actor Generation、槽位和物品基础实例信息；它不覆盖所有 Mod 自定义 Item 状态。异常或不确定副作用保持 unknown，不尝试自动回滚。

细节见 [背包操作](INVENTORY_OPERATIONS.md)。

## Server AI 特殊边界

- 所有读写绑定独立服务器 AI Player，不回退到真人 Player 或客户端本地 Player。
- Server AI 的 `use_item`、库存、合成、导航、放置和任务 API 可能比客户端版本更窄。
- `mine_target` 只接受受支持的观察目标或明确坐标；任务生命周期见 [任务运行时](M1_TASK_RUNTIME.md)。
- `observe_scene` 的 L0/L1、数据层和编码见 [语义场景](LOCAL_SEMANTIC_SCENE.md)。
- 服务器动作执行边界见 [Server AI Actions](SERVER_AI_ACTIONS.md)。

## 代码入口

- Schema 与通用分发：`python/atomic_operations.py`
- Server AI 能力投影：`python/server_ai.py`、`python/server_ai_contract.py`
- 游戏侧 IPC 和原生执行：`CompanionBridge/ServerIpc/`、`CompanionBridge/ServerPlayerRuntime/`
