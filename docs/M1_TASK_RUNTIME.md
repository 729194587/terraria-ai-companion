# 游戏侧任务运行时

游戏侧任务运行时在 Terraria 游戏更新中推进持续动作。Agent 决定目标，Lua 组织调用，C# 运行时管理任务生命周期并执行原生操作。

## 任务模型

一个外部任务对应一个根 TaskSlot。任务可以在内部顺序执行多个子动作，例如导航、挖掘、放置和物品持用。父任务保留整体目标与进度，子任务复用同一绑定 Actor 和游戏执行上下文。

当前代表性高层任务包括：

- `mine_target`：围绕指定矿物目标完成工具选择、站位搜索、导航、局部开路和原生挖掘。
- `navigate_to`：在局部路线与地形改造候选之间选择，并连续推进至目标落脚区域。
- `build_place`：调查 Blueprint、选择施工目标和工作位置，组合库存、导航与原生放置。
- `combat_attack` / `boss_encounter`：持续推进攻击、目标追踪、命中确认与战斗终态。

## 提交与查询

内置专服聊天 Agent 可用 `schedule_player_goal` 登记同一玩家交代的 2–8 个有序步骤，例如走到矿区、挖指定格、拾取矿石、返回。还支持单格放置和蓝图施工。导航目标是脚底的世界像素，挖格和放置用 Tile 坐标。每步保留原生任务 ID，确认效果与释放后才进入下一步。收集步骤若无实收或只完成部分则停下。一次已核验的进展刷新后续步骤的五分钟启动窗口，玩家叫停会撤销余下步骤。

任务 API 通过 Server AI 能力握手开放。典型流程：

```lua
local accepted = api('submit_task', {
    operation = 'mine_target',
    arguments = {target = target}
})

if accepted.taskId then
    return api('await_task', {
        taskId = accepted.taskId,
        milliseconds = 5000
    })
end
return accepted
```

提交返回任务标识和当前状态。Agent 可通过 `query_task` 查询进度、使用 `await_task` 等待一段时间，或通过 `cancel_task` 取消任务。等待超时只结束本次等待，不改变任务自身期限。

## 游戏更新驱动

TaskSlot 在游戏更新中推进任务。同一个 GameUpdate 不会重复执行 Tick。任务记录包括：

- taskId、任务名称和父任务 ID；
- World、Actor Generation 与 Player Slot；
- 当前阶段、状态、原因和截止更新；
- 当前子任务、进度及最近结果。

任务可按已核验进展续期；导航根任务使用进展租约，普通任务使用自身截止时间。子任务串行运行，终态结果写入父任务进度。

## 生命周期

任务经历 Prepare、Start、Tick 和 Stop：

1. **Prepare**：检查当前 Actor、目标、能力和任务前置条件。
2. **Start**：取得执行资源并开始原生动作。
3. **Tick**：每个游戏更新推进任务并更新进度。
4. **Stop**：取消或结束子任务、释放输入并整理结果。

同一 Actor 同一时刻由一个身体动作所有者控制。任务交接会先处理现有动作，再核对新任务所需的角色状态。

自动自卫和主动换气可以暂停挖矿、导航、拾取、建造、探索及跟随。根任务保留原 taskId、目标和已确认的工作，暂停期间仍可查询和取消。自动行为成功并释放输入后，原任务从当前角色位置重新评估下一步。取消原任务、角色死亡、断连或自动行为失败会终止这次续做。一次性的物品、按键和交易操作沿用各自的终态回执。

## 结果与证据

任务终态包括 completed、failed、timeout、cancelled 和 unknown。回执包含任务进度、子动作摘要、输入释放状态和操作相关效果证据。

父任务根据子任务结果和自己的目标条件决定最终状态。例如：

- 挖掘任务确认目标 Tile 被本次原生挖掘破坏。
- 导航任务确认角色实际落入目标区域并停稳。
- 建造任务结合施工记录和最终 Blueprint 差异确认完成情况。
- Boss Encounter 根据本场根实例及成员死亡证据判断胜负。

任务历史只保留有界数量的子动作明细，同时保留累计进度和终态摘要。

## 取消、超时与角色生命周期

取消和超时会向当前子任务传递停止请求。任务在游戏线程清理输入并收集最终证据。角色死亡、世界卸载、Actor 换代或绑定变化会影响任务是否能够继续；普通任务结束，明确支持复活等待的 Boss Encounter 可进入等待阶段。

Lua Program 与游戏 Task 生命周期相互独立。Program 返回或等待超时不会自动终止已提交的游戏任务。

## 实现入口

- `CompanionBridge/ServerIpc/TaskRuntime.cs`：TaskSlot、TaskRecord 与通用生命周期。
- `CompanionBridge/ServerIpc/ServerAiSystem.cs`：游戏更新驱动。
- `CompanionBridge/ServerIpc/ServerAiNativeActions.cs`：动作与任务创建。
- `CompanionBridge/ServerIpc/AutonomousNavigationTask.cs`：导航与挖掘任务。
- `CompanionBridge/ServerIpc/BuildPlaceTask.cs`：建造任务。
- `CompanionBridge/ServerIpc/CombatController.cs`、`BossEncounterTask.cs`：战斗任务。
- `python/program_tasks.py`：Python 侧任务提交、查询和结果交接。
