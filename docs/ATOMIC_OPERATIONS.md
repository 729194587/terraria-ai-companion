# 原子操作

原子操作是 Agent 可调用的单项游戏意图。模型通过 Lua 程序组合这些操作；宿主负责参数校验与分发，游戏侧执行器在 Terraria 更新线程中完成实际行为。

## 定义与开放

操作 Schema、名称和描述定义在 `python/atomic_operations.py`。Server AI 根据连接能力生成可见操作集合，相关逻辑位于 `python/server_ai.py` 和 `python/server_ai_contract.py`。

客户端控制与独立 Server AI Player 使用不同后端。相同名称的操作可能有不同参数和支持范围，实际可用集合由当前连接的 Help 契约提供。

## 操作类别

| 类别 | 代表操作 | 作用 |
| --- | --- | --- |
| 观察 | `get_player_state`、`observe_terrain`、`observe_scene`、`inspect_tile`、`scan_tiles`、`scan_entities`、`inspect_inventory`、`query_map` | 获取玩家、地形、实体、库存和探索记忆 |
| 角色与物品 | `select_hotbar_slot`、`use_item`、`transfer_inventory_item`、`swap_inventory_slots`、`hold_inventory_item`、`equip_item` | 管理快捷栏、物品使用、库存和装备 |
| 世界操作 | `move_input`、`navigate_to_x`、`navigate_to_landing_region`、`mine_tile`、`mine_wall`、`place_tile`、`place_wall`、`place_object`、`craft_recipe` | 移动、采集、放置和合成 |
| NPC 与经济 | NPC 对话、商店报价/交易、护士治疗、重铸、住房与队伍操作 | 使用原生 NPC 服务和世界规则 |
| 复合任务 | `mine_target`、`combat_attack`、`boss_encounter`、`build_place`、`collect_drops` | 由游戏侧任务运行时持续推进 |
| 宿主控制 | `wait`、`stop_action`、任务查询与取消 | 协调程序和动作生命周期 |

完整参数、范围和能力要求以当前 Schema 和 Help 为准，避免在文档中维护另一份完整 API 清单。

## 坐标与观察

Tile 坐标以格为单位，世界像素以像素为单位；一个 Tile 为 16×16 像素，世界 Y 轴向下。每个操作的坐标语义由其参数描述定义。

观察结果包含适用的世界与 Actor 身份、采样更新、空间范围、覆盖和截断信息。场景观察、区域查询与持久化地图分别服务于即时动作、区域认知和战略定位。

## 结果语义

动作回执区分请求受理、执行终态和操作效果。常见终态包括 completed、failed、cancelled、timeout 和 unknown；具体字段由操作契约定义。

例如：
- 导航核对真实位置、支撑和停稳。
- 挖掘核对目标 Tile 与原生破坏结果。
- 放置核对目标 Tile/Object 和物品消耗。
- 交易核对报价、货币和库存变化。
- 战斗核对目标实例、命中来源和生命变化。

库存、导航、战斗及持续任务的详细语义分别见对应专题。

## 代码入口

- Schema 与操作分发：`python/atomic_operations.py`
- Server AI 能力投影：`python/server_ai.py`、`python/server_ai_contract.py`
- Python/C# 结果契约：`python/api_result_contracts.py`
- 游戏执行：`CompanionBridge/ServerIpc/`
- Actor 专属操作：`CompanionBridge/ServerPlayerRuntime/`
