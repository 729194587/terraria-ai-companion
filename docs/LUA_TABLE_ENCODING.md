# Lua 返回值编码 v1

Worker 的普通纯数组返回 JSON 数组，纯字符串键对象返回 JSON 对象；未标记空表 `{}` 为对象，
`json.array()` 为空数组，`json.object()` 为空对象，`json.null` 保留显式 JSON null。
数字、布尔、字符串及正常嵌套值沿用现有 JSON 语义。整数键必须是连续 `1..n`，不转成字符串键。

自然的混合表不再丢数据或误报数据超限：

```lua
return {10, 20, 30, summary = "collected"}
```

对应 `value`：

```json
{"$luaTable":{"version":1,"kind":"mixed","array":[10,20,30],"fields":{"summary":"collected"}}}
```

数组部分在 `array`，字符串键部分在 `fields`。嵌套混合表按同一规则递归编码。
字符串键 `"1"` 与整数键 `1` 独立，不合并。不要把此封套当普通业务对象或只读取其中一部分。

`$luaTable` 是返回值编码保留键。任何纯对象包含该键时，自动转义为
`{"$luaTable":{"version":1,"kind":"object","fields":原对象字段}}`，即便其值看起来不像封套。
因此用户手写同形对象不能冒充混合表。混合表的 `fields` 容器本身不作对象封套解释，键原样保留，
包括空字符串、`$luaTable` 与 `"1"`；各字段**值**继续递归编码。

解码规则：普通数组逐元素解码，普通对象逐字段值解码。遇到唯一键为 `$luaTable` 的封套时，
按 version=1/kind 分派：mixed 重建连续数组部分与独立字符串字段部分；object 重建纯对象。
封套中的 `fields` 按键原样迭代，只对其值递归解码，不再将整个 fields 容器当封套读取。
解码 mixed 时保留两部分，不能合并成数字字符串键对象，也不能丢弃字段后当纯数组。

稀疏/零/负整数键、非整数数字键、布尔或 table/function 键不作隐式转换，返回
`serialization_error` 并给出有界位置/原因；循环引用同样拒绝。共享但不循环的子表可重复编码为快照。
`json.array` 搭配字符串字段、`json.object` 搭配整数键视为显式标记冲突，不静默丢字段。
非有限数、函数等非 JSON 值，以及无法表示为 UTF-8 的 Lua 字节字符串也是 serialization_error。
字节数、键长度、深度、节点数等既有真实资源限制仍为 `value_limit`，上限不增加。

本契约只扩展脚本 **return 值**。API 参数仍须符合普通 JSON schema，不使用此封套传递混合参数；
混合 API 参数在 Worker 内作为 serialization_error 拒绝，不发出游戏请求。
没有增加沙箱函数、权限、缓存或动作；unknown、continuationAllowed、输入释放及 lastObservation 的
既有宿主语义不变。lastObservation 是宿主保存的原始只读回执，不使用本脚本返回值编码改写。

阶段一定向验证（2026-10-29，基于 0.1.62 / 62a594e）：在 `tests/` 中使用
`../.local/lua-venv/Scripts/python.exe -B -m unittest <以下目标> -v`。

| 目标 | 结果 |
| --- | --- |
| `test_lua_arguments test_atomic_operations test_lua_coordinates test_lua_observability` | 36 项通过 |
| `test_lua_tables`（新增模型契约/沙箱测试前） | 7 项通过 |
| `test_lua_tables.LuaTableCodec.test_model_contracts_include_encoding_and_minimal_example test_lua_tables.LuaTableCodec.test_private_identity_check_does_not_expand_sandbox` | 2 项通过 |
| `test_lua_tables.LuaTableCodec.test_sparse_special_keys_cycles_and_explicit_tag_conflicts_are_serialization_errors`（补充 UTF-8 键/值后） | 1 项重跑通过 |
| `test_lua_runtime test_lua_recovery test_server_ai_scene test_server_ai_crafting` | 53 项通过 |

合计 98 个不同测试方法，含真实隔离 Lua Worker 与模拟游戏后端，不是原生 Terraria 隔离测试。
覆盖安全预检续行、unknown/取消/超时屏障、输入释放、观察保留、场景/采集/合成/放置相关回执；
未运行无关全量测试、真实 DeepSeek、原生隔离游戏或 Host & Play，未构建/替换 Mod 包。
原始输出保存在本地 `.local/stage1-arguments-final.log`、`stage1-tables-tests.log`、
`stage1-table-contract-tests.log`、`stage1-table-special-keys.log`、`stage1-regressions.log`。
