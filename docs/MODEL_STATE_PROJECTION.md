# 模型可见状态投影

状态投影在不修改完整游戏观察协议的前提下，减少发送给模型的冗余状态。原始观察仍由宿主完整校验并保留；投影只作用于模型请求副本，不回写规范历史或游戏状态。

## 当前规则

- 完整观察先由 `validate_observation()` 校验 schema、采样阶段、世界/观察身份、摄像机帧、地形编码、覆盖计数和实体唯一性。
- `project_model_observation()` 深拷贝已校验快照，仅从 `player` 移除 `inventory`、`equipment`、`dyes`、`miscEquipment`、`miscDyes` 和 `buffs`。
- 投影增加 `inventoryScope` 提示按需调用 `inspect_inventory`；Mod 自定义状态仍标记为未知。
- 其他字段原样保留，包括未来新增的安全字段。局部背包查询、selected/hotbar/item 模式不经此完整观察投影。

## 上下文历史压缩

`ContextManager` 在构造模型请求副本时，按 user-turn 边界压缩较早历史；规范对话历史不被改写。工具回执摘要只作为索引，明确标记 `lossy`、`currentState=false` 和被省略记录数量。最近轮次及当前任务尽量原样保留；无法满足输入预算时明确拒绝请求，不悄悄裁掉当前任务或工具定义。

Token 预算估算是 UTF-8 字节数除以 3 的近似值，不是模型 tokenizer 结果；实际 token usage 以服务端返回为准。成本统计见 [成本指标](COST_METRICS.md)。

## 实现入口

- `python/observation.py`：完整观察校验与模型投影。
- `python/agent_context.py`：请求上下文预算和历史摘要。
- `python/agent.py`、`python/agent_run.py`：模型请求与执行反馈。
