# Typed API Result Contract v1.2：实现记录

v1.2 将结构化返回值 Schema 接入 Server AI Lua Help，使模型和 External Brain 能够查询当前 API 的返回结构。

## 实现内容

- 新增 `python/api_result_contracts.py`，集中注册 API 返回 JSON Schema。
- `python/server_ai_contract.py` 在单项 Help 中提供 `returnsSchema`。
- Help 按当前连接开放的 API 和能力过滤返回契约。
- 通过测试样本核对 Python、C# IPC 和 Lua 最终返回形状。
- 为 Native Agent 与 External Brain 提供相同的按需查询入口。

## 设计要点

返回 Schema 描述 Lua Worker 实际收到的值。观察、动作、Program Task 和本地等待/停止具有不同的结果结构，测试按实际调用路径提取样本。

注册表中的共享定义按引用依赖组织。测试检查 Schema 引用完整性、能力子集、字段结构和跨语言序列化结果。

## 维护入口

- 实现：`python/api_result_contracts.py`
- Help：`python/server_ai_contract.py`
- 操作定义：`python/atomic_operations.py`
- 测试：`tests/test_server_ai_contract.py`、`tests/test_api_contract_ipc.py`

当前接口行为见 [API 契约](API_CONTRACT.md)。本文件保留 v1.2 实现阶段的技术记录，不再作为待实施任务书。
