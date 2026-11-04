# Lua 返回值编码

Lua Worker 将程序返回值转换为受限 JSON 数据。普通纯数组编码为 JSON 数组，普通纯字符串键表编码为 JSON 对象；空表默认是对象。用 `json.array()` 标记空数组，用 `json.object()` 显式标记对象，用 `json.null` 表示 JSON null。

## 混合表

返回值同时含连续正整数键和字符串键时，使用保留封套 `$luaTable`，分别保留数组部分与字段部分：

```lua
return {10, 20, 30, summary = "collected"}
```

```json
{"$luaTable":{"version":1,"kind":"mixed","array":[10,20,30],"fields":{"summary":"collected"}}}
```

嵌套混合表递归使用相同编码。整数键 `1` 与字符串键 `"1"` 是不同键，不合并。纯对象若包含保留键 `$luaTable`，会用 `kind="object"` 的封套转义，避免普通业务数据被误认为编码标记。

解码时按封套的 `version` 和 `kind` 恢复混合表或纯对象；`fields` 中的键原样保留，只递归解码字段值。

## 拒绝条件与边界

- 稀疏或非正整数数组键、非整数/布尔/table/function 键、循环引用、函数、非有限数字和无效 UTF-8 字符串会产生 `serialization_error`。
- 节点数、深度、字节数等真实资源上限触发 `value_limit`。
- `json.array` 与字符串字段、`json.object` 与整数键冲突时拒绝，不静默丢字段。
- 此编码只用于 Lua **return 值**。API 参数仍须是普通 JSON 对象/数组，不接受混合表封套。

实现位于 `python/lua_worker.py`（`TABLE_ENCODING_KEY`、`plain()`）和模型契约 `python/lua_agent.py`。返回值编码不改变动作、unknown、取消、输入释放或宿主保留的 `lastObservation` 语义。
