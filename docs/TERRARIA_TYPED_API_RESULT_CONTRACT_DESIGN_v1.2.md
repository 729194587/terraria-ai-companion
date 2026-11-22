# Typed API Result Contract v1.2：设计记录

本文记录 API 返回值契约注册表的设计背景与关键决策。该设计已进入当前代码，日常接口维护以 `python/api_result_contracts.py`、`python/server_ai_contract.py` 和 [API 契约](API_CONTRACT.md) 为准。

## 设计目标

让 Native Agent 与 External Brain 能够按需查询当前开放 Lua API 的结构化返回值说明，包括字段名、类型、含义、嵌套结构、枚举、可选字段和条件分支。

模型通过以下 Help 入口查询：

```lua
return api("help")
return api("help", {name="get_player_state"})
```

单项 Help 提供 `returnsSchema`，索引保持轻量，不在总目录中展开全部返回 Schema。

## 契约边界

返回契约描述 Lua `api(name, args)` 最终收到的值。它与 C# 内部 DTO、IPC 原始 Reply、Python Task Coordinator 视图以及整个 `execute_lua` MCP 返回结构是不同层次。

设计区分四类返回：

1. 观察 API：游戏侧查询经 Python 分发后形成 Lua 观察结果。
2. 动作 API：包含已受理、不受理和结果不确定等执行分支。
3. Program Task API：描述 Python 侧任务协调视图。
4. 本地等待与停止：`wait`、`stop_action` 有独立返回结构。

字段名称以实际 wire JSON 为准，不以语言层类型名称推断序列化结果。

## 跨语言实现

C# 负责游戏状态采样和序列化，Python 负责 Lua API Help。设计采用 Python 侧统一返回契约注册表，并使用真实序列化边界的测试样本核对字段。

这套机制让模型可以发现结构化返回信息，同时保持 C# 游戏执行与 Python 契约描述之间的边界。

## 当前实现入口

- `python/api_result_contracts.py`：返回值 Schema 注册表。
- `python/server_ai_contract.py`：Help 生成与能力过滤。
- `tests/test_server_ai_contract.py`、`tests/test_api_contract_ipc.py`：契约与序列化验证。
- [API 契约](API_CONTRACT.md)：当前接口调用模型。
- [External Brain](TERRARIA_EXTERNAL_BRAIN_MVP.md)：外部 Agent 的 Help 使用方式。

原始 v1.2 设计基线及其逐项实施过程可通过 Git 历史追溯。
