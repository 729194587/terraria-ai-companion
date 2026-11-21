# Terraria Typed API Result Contract v1.2 — 实现规范

> 本文件与 `TERRARIA_TYPED_API_RESULT_CONTRACT_DESIGN_v1.2.md` 配套。  
> 基线：`codex/unified-autonomous-navigation@9fb82fd9ec7c2b68bb1c71e0607fd7e918e1013c`。若代码已变化，以当前源码实际输出为准并在结果中列出差异。

## 1. 变更清单（优先只改这些文件）

| 文件 | 指定变更 |
|---|---|
| **新增** `python/api_result_contracts.py` | `$defs`、按 API 注册的输出 Schema、能力条件、`schema_for`、覆盖检查 |
| `python/server_ai_contract.py` | `api_help()` 的单项详情添加 `returnsSchema`；保留原字段、能力过滤、错误语义；修正观察封套文字描述；Native Agent 也能发现 Help |
| `tests/test_server_ai_contract.py` | 全量/子集能力覆盖、Schema 合法性、Help、引用、字段精确性 |
| `tests/test_external_brain.py` | Guide → Help → Lua 只读链路及 Help 不产生游戏动作 |
| `tests/ApiContractProtocolHost.cs` | 可复用的 C# 协议/序列化样本；优先复用现有测试宿主 |
| `tests/test_api_contract_ipc.py` | 验证 C# → Bridge → Lua 的最终返回形状 |
| `docs/TERRARIA_EXTERNAL_BRAIN_MVP.md` | 补充 API Help `returnsSchema` 的用法与验证范围 |

可按需要添加 `tests/test_api_result_contracts.py` 和 `tests/fixtures/api_results/`。不要求新框架，也不要求将 C# 动作实现大规模改成 DTO。

## 2. 指定接口

在 `python/api_result_contracts.py` 提供等价于以下职责的入口：

```python
def schema_for(name: str, *, capabilities=()) -> dict:
    """一个 API 的 Draft 2020-12 自包含返回 JSON Schema；返回独立副本。"""

def registered_names() -> set[str]:
    """已具有返回契约的 API 名称。"""

def missing_for(exposed_names) -> set[str]:
    """已开放而未注册返回契约的 API。"""
```

实现方式可使用静态字典和构造函数，不强制编写类型 DSL。`schema_for()` 的 `$defs` 只包括本次引用到的共享定义和递归依赖；引用不存在应在测试中报错。

`python/server_ai_contract.py`：

- 保留 `server_definitions(bridge)` 为**可调用名称及输入参数 Schema**来源；用它得到开放集合后再查询 `api_result_contracts.schema_for()`。
- `api_help(bridge, arguments)` 在指定 `name` 查询的既有返回中增加 `returnsSchema`，保留 `returns` 概述、原有 status/error；`api("help")` 索引不返回批量 Schema。
- 对当前开放、但尚无返回契约的 API，不能静默回退到通用 `{...}`。测试必须将其判为覆盖缺陷；实际处理选明确的本地文档错误而非假文档。
- Native Agent 的非 compact `server_contract()` 也提示 `api("help")` 的可用性，但不在初始上下文注入所有返回 Schema；External Brain 的 compact Guide 保持精简。
- `OBSERVATION_RESULT` 不应继续声称 Server AI 查询封套必然有 `binding`，实际来源以 `ServerAiQueries.Read` 为准。
- `python/lua_runtime.py` 的 Help 分发不需要更改（除非测试发现确切集成问题）。MCP 工具列表保持五项。

## 3. API 覆盖列表

**实际开放集合动态取得**：`server_ai_contract.server_definitions()`。以下为审计提交的完整能力示例，并非固定白名单。

| 类别 | 名称 |
|---|---|
| 观察（10） | `get_player_state`, `inspect_inventory`, `observe_terrain`, `inspect_tile`, `scan_tiles`, `scan_entities`, `resolve_content`, `observe_scene`, `query_recipes`, `query_map` |
| 宿主等待（1） | `wait` |
| 动作（19） | `craft_recipe`, `transfer_inventory_item`, `swap_inventory_slots`, `hold_inventory_item`, `equip_item`, `unequip_item`, `move_input`, `navigate_to_x`, `navigate_to_landing_region`, `select_hotbar_slot`, `use_item`, `navigate_to`, `mine_target`, `mine_tile`, `place_tile`, `place_object`, `place_rope`, `move_on_rope`, `stop_action` |
| Program Task（5） | `submit_task`, `query_task`, `await_task`, `cancel_task`, `resume_task` |

`atomic_operations.py` 另有未在当前 Server AI 完整握手中开放的 API 定义；不要求本版本提前为它们伪造返回结构。但未来一旦开放，覆盖测试就应提示补上。

## 4. 按实际调用路径提取结果字段

以下是**代码核对路线**，不是逐字段清单。优先看 Python 最终返回分支，再追到 C# 实际数据生产位置。

| 结果族 | Python 入口 | C# 来源 / 特别注意 |
|---|---|---|
| 观察封套 | `atomic_operations._dispatch()` 中 `query_atomic` | `ServerAiQueries.Read` 的 `available,worldSessionId,actorGeneration,actorSlot,gameUpdate,observedAtUnixMs,clock,data`；`data` 可有能力相关字段 |
| `get_player_state` | `_dispatch` 专属展开分支 | `ServerAiQueries.GetPlayer` / `ObservationReader.ReadPlayer`；**成功时 `player,motion` 在顶层而不是 `data`** |
| `inspect_inventory` | 通用观察投影 | `AtomicOperations.ReadInventory`；`full`、`item`、`selected/hotbar/slots` 的返回键不同；Server AI 还会补充功能装备槽信息 |
| 地形/Tile/实体/内容 | 通用观察投影 | `ServerAiQueries`、`AtomicQueries`、`ObservationReader`、`EntityReader`；包括未知/截断/分类子对象 |
| `observe_scene` | 通用观察投影 | `ServerSceneReader` + `SceneRegion` / `SceneGeometry`；`server_ai_scene_region_v1` 控制区域能力；`layers` 决定条件字段 |
| 配方、地图 | 通用观察投影 | `ActorCrafting`、`AiMap.Query`；分别建模 recipe、地图行/图例；地图是已知探索记忆而非实时世界 |
| 动作通用回执 | `atomic_operations.result()`、`not_admitted_result()`、`terminal()` | `AiAtomicActions.Snapshot()`、`EndEvidence`；正常受理、拒绝、最小 unknown 结构不同 |
| 动作专属 `evidence` | 上述 Action Result 的 `evidence` | `ServerAiNativeActions.Poll()`；`ActorInventory.Evidence`、`ActorEquipment.Evidence`、`ActorCrafting.Evidence`、`ServerAiDirectInput.Evidence`、`ServerAiRope.Evidence` 等 |
| 导航/挖掘/放置 | 同上 | `LocalMotionController.NavigationSnapshot`、`ActorMining`、`ActorItemUse`、`ActorPlacement`；**默认 C# 序列化的 PascalCase 不能猜成 camelCase** |
| 复合游戏 Task | 原子 `navigate_to` / `mine_target` 动作结果 | `TaskRuntime.TaskSlot.View()`、`AutonomousNavigationTask.Progress`、`NativeNavigationWorld`、`NativeMineTargetWorld`；递归 `currentChild`、`result` |
| Program Task | `program_tasks.TaskCoordinator` | 与原子 Task 回执不同；`await_task.waitStatus` 不是 Task status；`submit_task` 仅当前支持 `mine_target` |
| `wait` / `stop_action` | `_dispatch` 的专属分支 | `wait` 是 Host monotonic clock；`stop_action` 包括 `noOp` 与停止异常 |

优先复用这几组公共 Schema：`ObservationEnvelope`、`ItemData/CompactItem`、`TileCell`、`Scene*`、`ActionReceipt/EndEvidence`、按操作区分的 `Evidence`、`TaskView/TaskReceipt`。不要把所有 evidence 简化成一个任意字典。

## 5. 必须逐项检查的真实差异

1. **字段大小写**：对默认 C# Record/匿名对象、`SceneRegion.Json` 专用 camelCase、Python 投影分别核对，不做全局命名推断。
2. **可选与 null**：既可能缺字段，也可能明确序列化 `null`；例：`NavigationSnapshot.BlockingTiles` 在 null 时有 `JsonIgnore`。
3. **能力条件**：当前 `observe_scene` 的 local / region，以及 `layers` 的差异；普通观察的 `data` 不应被误认所有模式都有同一对象。
4. **动作状态**：`valid` 是宿主包装的有效性，不等于效果已验证；已受理后的 `unknown` 必须保留不确定性；释放输入不能代替世界效果确认。
5. **任务结构**：C# 接受阶段的短 `TaskState`、运行中的完整 `TaskSlot.View()`、Python `TaskCoordinator` 对外视图和可能出现的未知任务，都要能表达。
6. **实际来源**：对于 `PhysicsState` / `NavigationSnapshot` / `MobilityProfile` 等嵌套复杂类型，从 C# Record 字段与实际 JSON 样本核对，不靠人工猜测子结构。
7. **Lua 可见值**：测试包含 Python 结果投影之后的值；尤其 `get_player_state` 的成功/不可用两种分支。

Schema 的 `description` 应写明坐标单位、`unknown`/`truncated`/`complete` 等字段含义。对于源码不能确定的字段，先列出待验证证据，不要凭想象填一个非常具体但错误的类型。

## 6. 测试任务与验收标准

### 6.1 注册表与 API Help

- 使用当前完整能力 fixture 断言 35 个名称都可在 Help 查询到对应 `returnsSchema`；其他能力组合以实际 `server_definitions()` 集合为准动态检查，不只是比较手写名单。
- 每份 Schema 由 `jsonschema.Draft202012Validator.check_schema()` 检查；验证所有 `$ref` 解析，`schema_for` 返回独立副本。
- `not_exposed` / `unknown_api` / Help 参数错误不变，Help 不产生游戏动作，仍占原 Lua API 调用预算。
- `api("help")` 索引不包含 35 份返回详情，compact Guide 不显著扩大。

### 6.2 返回值 fixture 与负例

- 每个开放 API 至少有一个能够匹配 Schema 的代表性最终返回 fixture。
- 重要条件分支至少分别验证：`get_player_state` 成功/不可用；背包多 mode；`observe_scene` local/region/不同 layer；实体种类；正常/截断/不完整观察；受理/拒绝/unknown 动作；`stop_action` no-op；Task 受理/查询/结束/未知、`await_task` 等待超时。
- 对关键字段类型、缺失 required、错误的 nullable 和大小写添加负例，保证 Schema 不只是接受任意 JSON。

### 6.3 跨语言校验

- 优先扩展现有 `tests/ApiContractProtocolHost.cs` 与 `tests/test_api_contract_ipc.py`，让代表性 C# 实际序列化产物通过 Server AI TCP → Python Bridge/Dispatcher → Lua Worker，再与对应 Schema 校验。
- C# 协议测试宿主使用的**模拟游戏数据**只能证明序列化与投影形状，不能冒充真实 tModLoader 游戏逻辑。必要时增加隔离游戏服务端只读 smoke test 的检查清单。
- 如果个别深层嵌套对象仅有真正游戏运行时才能构造，完成报告需列出哪些分支尚未得到实机证据，并说明已有静态源码/样本依据。不可将未覆盖当作测试通过。

### 6.4 回归与交付

- 现有 `tests/test_server_ai_contract.py`、`tests/test_external_brain.py`、Lua 相关测试通过；协议宿主测试在具备其既有环境依赖时执行。
- Native Agent 与 External Brain 的 Helper 文档共用同一 Schema 来源；五个 MCP 工具接口不变。
- 原有权限、动作预算、任务隔离、输入释放、unknown 停止与证据核验逻辑不改变。
- 提交报告列出：修改文件、实际覆盖的开放 API 数量、主要条件分支、测试命令与结果、未能实机验证的内容。

## 7. 推荐实现顺序

1. 在 `api_result_contracts.py` 定义公共封套和输出 Schema 查询接口；先打通 `api_help` 的 `returnsSchema`。
2. 依次建立观察、动作和 Program Task 的输出契约，以已有序列化代码为准补齐嵌套类型。
3. 添加动态覆盖测试与 JSON fixture 校验，并校准 PascalCase/camelCase、null/缺失和多分支结构。
4. 扩展 C# 协议样本，跑既有回归测试，更新使用说明。

遵循这个顺序可以先稳定接口，再用测试驱动填满实际字段，不需要重新研究 Numen 或重新讨论 MCP 架构。
