# API Help 与 Gameplay Modules 实现记录

本文记录 API 自描述与内置 Lua Gameplay Modules 的实现结果。当前架构见 [API Help 与 Gameplay Modules](AGENT_API_AND_GAMEPLAY_MODULES_DESIGN.md)。

## API Help

现有 `api("help")` 扩展为动态 API 目录和单项帮助。参数 Schema 来自操作定义，返回 Schema 来自返回契约注册表，示例及补充说明由文档元数据提供。

Help 根据当前 Bridge 能力过滤操作，并保留客户端与 Server AI 后端之间的差异。

## Module Registry

新增 `python/lua_modules.py` 和 `python/lua/modules/`。Registry 为模块声明名称、摘要、依赖 API、导出函数和调用签名。Worker 根据当前连接开放的操作加载可用模块，并将其挂载到 `terraria` 命名空间。

## 首批模块

- `terraria.crafting.make`：查询配方，按产出数量计算批次，逐批执行并累计真实产出。
- `terraria.building.construct`：先调查 Blueprint，再提交施工任务并等待结果。
- `terraria.npc.talk_to`：发现并接近指定 NPC，建立对话。

模块通过宿主 API 回调执行底层操作，沿用 API Schema、预算和任务回执。

## 主要实现入口

- `python/api_documentation.py`
- `python/api_result_contracts.py`
- `python/lua_modules.py`
- `python/lua/modules/`
- `python/server_ai_contract.py`
- `python/lua_worker.py`

## 当前测试入口

- `tests/test_server_ai_contract.py`
- `tests/test_lua_modules.py`
- `tests/test_lua_worker.py`
- `tests/test_crafting_module.py`
- `tests/test_building_module.py`

具体测试数量和历史提交基线可从 Git 历史追溯；当前行为以实现和当前测试为准。
