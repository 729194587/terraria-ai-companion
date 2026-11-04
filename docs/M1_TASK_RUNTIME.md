# 游戏侧任务运行时

本文说明当前 Server AI 高层游戏任务的执行契约。原子动作接口见 [原子操作](ATOMIC_OPERATIONS.md)，Agent/Lua 调用方式见 [Lua Runtime](LUA_RUNTIME.md) 和 [Server AI Actions](SERVER_AI_ACTIONS.md)。

## 职责

长时间身体操作由游戏侧任务运行时推进，不依赖 LLM 逐 Tick 轮询。Agent 决定目标，Lua 组织程序，C# 任务运行时维护任务生命周期并调用既有原生动作。

当前代表性高层任务为 `mine_target`：选择受支持的镐、检查交互范围、寻找站位、导航、重新验证目标并执行挖掘。它是有限范围的单格目标任务，不是完整矿脉采集或全局探索。

## 调用与目标

Server AI 在运行时宣告 `server_ai_tasks_v1`，且具备对应原子操作与动作能力时才开放任务 API。旧服务器在 RPC 前拒绝；普通客户端 TCP 不支持该操作。

优先把 `observe_scene` 返回的结构化 `target` 原样传给 `mine_target`：

```lua
local scene = api('observe_scene', {layers={'objects'}})
for _, object in ipairs(scene.data.objects.items) do
    if object.kind == 'ore_component'
        and object.contentKey == 'Terraria/Copper'
        and object.target then
        return api('mine_target', {target=object.target})
    end
end
return {status='no_returned_target'}
```

观察目标绑定 World Session、Actor Generation/Slot、采样 Tick、时间和目标令牌。目标过期、身份变化、字段被改写或目标发生修订时拒绝执行。也支持明确指定绝对 Tile 坐标，但会在任务预检时捕获目标并执行同样的后续重验。观察结果受范围、采样和截断限制；没有返回候选不证明世界中不存在该资源。

## 生命周期

- 一个外部任务对应一个根 Action；导航和挖掘作为内部子任务运行，不重新派发 IPC 或创建第二个外部 Action。
- 任务由游戏更新驱动；同一 GameUpdateCount 不重复推进。受理本身不启动任务。
- Program 正常结束、普通新聊天或等待超时不等于取消已受理任务。明确停止、取消、断连、身份失效和截止时间按任务取消契约处理。
- 等待超时只结束等待，不延长或取消游戏任务期限。
- 子任务复用绑定 Actor 的原生执行上下文；父任务只有在目标效果、身份和输入释放均得到核验后才报告完成。
- 失败、取消、超时和 unknown 保持不同结果。unknown 不重放、不通过新入口绕过失败屏障。
- 任务结果通过既有 Action 查询/回执中的 task 信息交接；终态结果去重并有界保留。

## 安全与效果核验

任务执行始终绑定 World、Generation、Slot 和 Player 对象身份。旧身份失效后不得向新 Actor 写入。挖掘完成要求目标被本次原生挖掘破坏、目标状态复核为空且输入已释放；这不证明掉落物已进入背包。

任务使用 Terraria 原生导航、挖掘和输入路径。站位预检、目标重验和 IL 引擎指纹是执行安全的一部分，不应通过放宽阈值或直接改写 Tile/Player 状态规避失败。

## 当前范围与限制

- `mine_target` 仅覆盖当前实现支持的普通原版矿物单格目标、受支持工具和普通运动 profile。
- 不提供完整矿脉采集、自动挖通阻挡、全局寻矿、复杂洞穴策略、动态战斗/避敌或拾取归属保证。
- 任务运行时不提供通用并行、抢占、持久化或进程重启恢复。
- 隔离原生验证不等于图形 Host & Play、远端客户端同步或真实 LLM 长期任务验收。

实现入口：`CompanionBridge/ServerIpc/TaskRuntime.cs`、`MineTargetTask.cs`、`NativeMineTargetWorld.cs`、`ServerAiNativeActions.cs`，以及 Python 侧 `server_ai.py`、`program_tasks.py`。
