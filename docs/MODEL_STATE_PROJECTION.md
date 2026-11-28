# 模型状态投影与上下文压缩

Agent 在构造模型请求时，会把经过校验的游戏观察投影为更紧凑的模型可见状态，并对较早的对话历史进行压缩。原始观察和规范对话历史保留在宿主侧。

## 游戏观察投影

`validate_observation()` 先检查观察协议版本、采样阶段、世界与观察身份、地形编码、覆盖计数和实体唯一性。

`project_model_observation()` 深拷贝已校验的快照，移除完整 Player 观察中的以下高体积字段：

- inventory
- equipment
- dyes
- miscEquipment
- miscDyes
- buffs

投影同时增加 `inventoryScope`，提示模型按需使用 `inspect_inventory` 获取背包或装备信息。其他观察字段保留在投影中。

局部背包查询（selected、hotbar、item 等模式）走独立 API，不经过完整观察投影。

内置聊天 Agent 每次决策收到背包物品种类与数量、空格数和手持物品。手持数量已计入物品总数。获取槽号、散堆位置或操作令牌时，按需调用 `inspect_inventory`。专服常驻提示词列出当前连接的能力类别、可复用模块的用途和完成判据。Agent 按需用 Lua Help 查询签名、返回字段和运行细节，见 [Lua Agent](LUA_AGENT.md#api-发现)。

## 对话历史压缩

`ContextManager` 在构造模型请求副本时，以 user-turn 为边界压缩较早历史。规范对话历史保持不变，压缩结果只用于后续模型请求。

工具回执摘要作为历史索引，记录 `lossy`、`currentState=false` 和省略数量。最近轮次与当前任务上下文优先保留。

新播报和有变化的笔记目录在完整工具批次之间作为宿主数据进入历史。压缩时保留预算内近期播报的编号、游戏更新号和有界文本。如果最新笔记目录所在的消息被压缩，该目录随压缩摘要提供。

Token 预算估算使用 UTF-8 字节数除以 3 的近似值，实际 token 用量以模型服务端统计为准。相关成本记录见 [成本指标](COST_METRICS.md)。

## 实现入口

- `python/observation.py`：观察校验与模型投影。
- `python/agent_context.py`：请求上下文预算和历史压缩。
- `python/agent.py`、`python/agent_run.py`：模型请求与执行反馈。
