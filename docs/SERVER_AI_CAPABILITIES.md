# Server AI 能力契约

Server AI 暴露给模型的 API 必须来自运行时能力握手与服务端白名单的交集。模型提示、文档或客户端工具列表不能扩大执行端实际开放的能力。

## 能力来源

- `server_ai.operations_for_capabilities()` 根据 IPC bind 握手投影服务器 API 白名单。
- `server_ai_contract.exposed_operations()` 将握手投影、Bridge 白名单和已描述的服务器 API 取交集。
- 未知能力旗标不扩大权限。能力缺失时，调用应在进入游戏动作前明确拒绝。
- Capability Manifest 区分 Supported、Limited 和 Not exposed。只读观察接口与修改世界的操作分开列出；相关观察能力不代表对应动作已开放。
- API Contract 从实际工具 Schema 生成，参数结构以执行端 Schema 为准。服务器端对特定操作的限制必须在契约描述和执行器校验中保持一致。

## 维护要求

新增或修改 Server AI API 时，核对能力旗标、Bridge 白名单、Schema、Python 分发、C# IPC/执行端、模型契约和定向测试。不能只更新 Prompt 或 Manifest。

能力开放只说明接口可调用，不代表所有装备、地形、实体或 Mod 场景都已实机验证。有限支持范围和 unknown 结果必须显式呈现。

## 实现入口

- `python/server_ai.py`
- `python/server_ai_contract.py`
- `python/atomic_operations.py`
- `CompanionBridge/ServerIpc/`
