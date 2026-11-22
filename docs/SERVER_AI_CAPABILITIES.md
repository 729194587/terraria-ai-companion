# Server AI 能力契约

Server AI 的模型可见 API 由当前 IPC 连接能力生成。能力投影将服务端运行时实际开放的操作整理为模型可查询的接口目录。

## 能力来源

能力由 CompanionBridge 在 IPC bind 握手中声明。Python 侧结合握手能力、服务端白名单、操作 Schema 和描述注册表，生成当前连接可用的 API。

主要入口：

- `python/server_ai.py`：根据能力生成 Server AI 操作集合。
- `python/server_ai_contract.py`：生成模型可见的能力说明和 Lua Help。
- `python/atomic_operations.py`：共享操作 Schema 与参数校验。
- `CompanionBridge/ServerIpc/`：服务端能力声明、协议和执行实现。

## 多 Mod 专服

绑定回执的 `data.compatibility` 给出实际引擎、已加载的 Mod 名称与版本，以及本次初始化失败的可选适配器和原因。Python 可从 `bridge.hello["compatibility"]` 读取；当前连接开放的 API 仍由 `capabilities` 决定。诊断字段为空集合时，单项配方、武器或操作仍可能有自己的支持范围。实测环境、用例和限制见 [R3-C2 实施报告](architecture/R3_C2_MOD_COMPATIBILITY.md)。

## 能力分类

Capability Manifest 将操作按领域组织，并标记 Supported、Limited 和 Not exposed 等状态。分类说明帮助模型理解当前后端的能力范围；单项 Help 提供参数、返回字段和使用示例。

能力集合会随服务端版本和连接握手变化。查询与动作分别描述，便于 Agent 判断当前能够观察什么、执行什么。

## 新增或修改能力

新增接口时，沿调用链维护以下内容：

1. C# 能力旗标和 IPC 操作。
2. Python Schema、后端分发和结果契约。
3. Server AI 能力投影与 API Help。
4. Lua 模块或 Gameplay Skill（如该能力需要组合使用）。
5. 对应的 Python、C# 和原生场景测试。

## 相关文档

- [API 契约](API_CONTRACT.md)
- [原子操作](ATOMIC_OPERATIONS.md)
- [Lua Agent](LUA_AGENT.md)
- [游戏侧任务运行时](M1_TASK_RUNTIME.md)
