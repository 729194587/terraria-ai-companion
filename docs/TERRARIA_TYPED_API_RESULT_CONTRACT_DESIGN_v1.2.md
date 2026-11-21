# Terraria Typed API Result Contract v1.2 — 设计规范

> 状态：设计基线，供 Codex 按文实现与测试；本文件不代表功能已完成。  
> 仓库：`729194587/terraria-ai-companion`；分支：`codex/unified-autonomous-navigation`；审计提交：`9fb82fd9ec7c2b68bb1c71e0607fd7e918e1013c`。  
> Numen 参考版本：`Dwinovo/minecraft-numen@d0368fefed4168d1ac9aeae00af4a609edf2211b`。

## 1. 目标与范围

让 External Brain 和 Native Agent 对**当前实际开放**的 Lua 游戏 API，按需取得完整、逐字段、可机器解析的**返回值契约**：字段名、类型、含义、嵌套结构、枚举、必需/可选、可空和条件分支。最终交互保持：

```lua
return api("help")
return api("help", {name="get_player_state"})
```

单项 Help 增加 `returnsSchema`，保留已有 `parameters`、`returns`、`description`、`statusSemantics`、`limits`、`example`。索引仍然轻量，Guide 不展开全部返回 Schema；五个 MCP 工具不变。

本阶段不增加游戏动作、重设计 MCP、修改动作执行语义，也不引入生产运行时的 Schema 校验。

## 2. 从 Numen 借鉴什么

Numen 使用 `@Fn` / `@Doc` / Java Record / `RecordCodec` / `ScriptType` 共享输入、返回类型和字段文档，由 `ApiDocs` 生成 Lua 帮助和元数据。**它并不是简单地为每个 API 单独手写 JSON Schema。**

Terraria 的 C# 游戏端负责序列化，Python 负责 Lua/API Help，无法直接复用 Java 的 Record-Codec 机制。因此本版采用跨语言适配：**统一的模型可见返回契约注册表 + 从真实序列化边界取得的测试样本**。这比单纯复制注释强，但不声称 Python 定义会自动约束 C# 运行时。

## 3. 返回契约的边界

`returnsSchema` 描述的是 **Lua `api(name,args)` 最终收到的值**，不是 C# 游戏内部 DTO，也不是 IPC 原始 Reply 或整个 `execute_lua` 的 MCP 返回结构。

当前有四类边界：

1. **观察 API**：C# `ServerAiQueries.Read` → Python `atomic_operations._dispatch` → Lua。普通观察保留 `data`；`get_player_state` 成功时把 `data.player` / `data.motion` 展开到顶层。不可混用。
2. **动作 API**：C# `AiAtomicActions.Snapshot` 和 `ServerAiNativeActions` → Python `atomic.result()`、`not_admitted_result()` 等 → Lua。需要覆盖已受理、不受理和不确定等真实分支。
3. **Program Task API**：Python `program_tasks.TaskCoordinator` 返回任务协调视图；不能把它和 C# `TaskSlot.View()` 或原子 `mine_target` 回执视为同一个对象。
4. **本地等待与停止**：`wait`、`stop_action` 各有本地/特殊结果分支。

**字段命名按实际 wire JSON 保留**。部分 C# 默认序列化的 Record / 匿名字段仍为 PascalCase，例如挖掘证据中的 `X`、`Y`、`Ticks`，导航快照中的 `Status`、`BlockingTiles`；观察场景采用自己的 camelCase 序列化设置。不得仅为文档整齐而改名。

## 4. 结构与接口决策

建议建立 `python/api_result_contracts.py`，其职责限定为：

- 存储 JSON Schema Draft 2020-12 的共享 `$defs` 和 API 输出 Schema；
- 为一个 API 生成**自包含**的 Schema，带齐引用的本地 `$defs`；
- 对 `observe_scene` 等受握手能力影响的结果提供能力特化；
- 提供“已开放但缺少结果契约”的覆盖检查；
- 返回独立副本，避免调用方修改共享定义。

内部可用 Python 字典、函数工厂和共享构造器；不强制另起复杂的 DSL 或类层级。

约定 API Help 的单项结果：

```json
{
  "valid": true,
  "status": "ok",
  "name": "get_player_state",
  "description": "…",
  "parameters": {},
  "returns": "…",
  "returnsSchema": {"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object"},
  "statusSemantics": "…",
  "limits": "…",
  "example": "…"
}
```

上面的 Schema 仅演示字段位置，不是 `get_player_state` 的实际完整定义。

现有 `server_ai_contract.server_definitions()` 仍然是**当前开放 API 和参数定义**的权威来源。`api_help()` 对现有定义附加返回 Schema；返回契约注册表**不能反向决定开放权限**。

## 5. Schema 表达规则

- 采用 Draft 2020-12；本地 `$ref` / `$defs` 复用结构，禁止未解析的远程引用。
- `required` 只列运行时该分支保证出现的字段；缺失字段与 `null` 分开表达。
- 对明确的状态集合使用 `enum` / `const`；不可把任意原因文本误设为封闭枚举。
- 参数模式、layer、成功/未知等真实变体使用 `oneOf` / `anyOf` / 条件组合；不同分支应可区分，不应为形式上的全面而一律宽放为 `object`。
- `additionalProperties` 仅用于真实的动态字典，并定义值类型；不允许用不受约束的 `{}`、`object` 代替已知嵌套结构。
- `description` 记录字段意义、单位与证据边界，尤其世界像素 vs Tile、时间戳 vs 游戏 Tick。
- 允许确有扩展性的透传对象保留额外字段，但已知字段仍要精确描述；不为达到“完整”而猜测不确定的类型。
- 一次 Help 返回一个独立 JSON Schema；不把全量 `$defs` 插进 Guide 或全部 API 的 Help Index。

需要共享的主要类型族：观察封套、玩家/物品、Tile/实体、场景图层、动作回执与结束证据、各动作 `evidence`、导航状态及 Task 视图。共享以**真实序列化结构一致**为前提。

## 6. 三组重点契约

### 6.1 Observation

覆盖 `get_player_state`、`inspect_inventory`、`observe_terrain`、`inspect_tile`、`scan_tiles`、`scan_entities`、`resolve_content`、`observe_scene`、`query_recipes`、`query_map`。

`inspect_inventory` 的 `full`、`item`、`slots/hotbar/selected` 有不同 `data` 分支；`observe_scene` 的 local/region 能力及所选 `layers` 会改变存在的字段；`scan_tiles` 的 `complete` / `truncated` / `unknownCount` 不能被折叠为简单成功布尔值。

### 6.2 Action

公共 `ActionReceipt` 与每个操作的 `evidence` 分开描述。区分 `admitted` / `not_admitted` / minimal unknown；区分 `status`、`completed`、`inputsAcquired`、`inputsReleased`、`effectVerified`、`endEvidence`。`stop_action` 的 `noOp` 是单独分支，不代表目标动作成功。

操作专属证据至少包含：快捷栏选择、物品使用、挖矿、移动输入、导航、绳索移动、放置、制作、背包变更、装备变更及 `navigate_to`/`mine_target` 的任务证据。

### 6.3 Program Task

`submit_task` / `query_task` / `await_task` / `cancel_task` / `resume_task` 的对外视图、C# `TaskSlot.View()`、递归子任务和最终动作回执分层建模。`waitStatus="timeout"` 只表示一次等待结束，不表示游戏任务已超时；`unknown` 不允许当作可安全重试。

## 7. 一致性策略与安全语义

1. 静态 Schema 从当前源码的实际返回投影建立；不从输入参数 Schema 反推输出结构。
2. 验证**最终 Lua 可见结果**，不能只校验 C# 内部匿名对象。
3. 有代表性的 C# 序列化样本应从真实 C# serializer/协议代码路径产生，再经过 Python 投影；手写 JSON 只能作补充测试，不能替代跨语言核对。
4. 复杂游戏运行分支暂时无法在离线 fixture 产生时，应明确记录未实机验证的情况，不能把 fixture 覆盖声称为全部真实游戏路径已证实。
5. Schema 只用于文档和测试；不根据 Schema 校验失败改变已经发生副作用的动作终态，避免将 `unknown` 降格为普通 `failed`。

核心决策准则：**文档宁可明确某部分尚无验证证据，也不能伪造精确性。**

## 8. 验收口径

- 当前完整能力握手示例的 **30 个原子 API + 5 个 Program Task API** 全部有返回契约；运行时实际集合动态计算，不写死 35。
- API Help 能按当前能力查到单项 `returnsSchema`，未开放/未知 API 的既有错误语义不变。
- JSON Schema 自身有效，引用闭包完整，公共结构/条件分支足以指导 Lua 正确读取数据。
- 测试可发现新增开放 API 未注册返回契约；复杂类型和跨语言字段大小写由样本校验。
- Native Agent / External Brain 使用同一注册表、同一 Help 实现；初始 Guide 不膨胀为全量输出文档。
- 不改变 MCP 工具集、游戏操作、能力握手、动作预算、输入释放及失败隔离逻辑。

## 9. 主要源码与参考

- Terraria：`python/server_ai_contract.py`、`python/atomic_operations.py`、`python/program_tasks.py`、`python/lua_runtime.py`、`python/agent.py`、`python/external_brain.py`。
- C# 输出边界：`CompanionBridge/ServerIpc/ServerAiQueries.cs`、`ServerAiNativeActions.cs`、`AiAtomicActions.cs`、`TaskRuntime.cs`；`CompanionBridge/Common/Systems/AtomicQueries.cs`、`ObservationReader.cs`；`CompanionBridge/ServerIpc/ServerSceneReader.cs`；相关 `Actor*` 执行文件。
- Numen：`docs/adr/control/0009-api-registration-sdk.md`、`ApiDocs.java`、`RecordCodec.java`、`LuaCodecs.java`、`ScriptType.java`、`ApiTester.java`。
