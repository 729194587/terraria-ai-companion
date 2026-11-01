# M1：游戏侧任务运行时与 mine_target（0.1.73）

## 实现与调用

Agent 保持原来的 `execute_lua` 通道。新增高层 API `mine_target`，由游戏侧完成选镐、判断交互范围、寻找站位、导航、重验、挖掘和结算。一次调用只创建一个外部 Action；中间无需 LLM 再次决策或客户端轮询。

```lua
local scene = api('observe_scene', {layers={'objects'}})
for _, ore in ipairs(scene.data.objects.items) do
    if ore.kind == 'ore_component' and ore.contentKey == 'Terraria/Copper' and ore.target then
        return api('mine_target', {target=ore.target})
    end
end
return {status='no_returned_copper_target', truncated=scene.data.objects.truncated}
```

直接传 `ore.target`，不从字符地图换算、不抄写或重建坐标。游戏侧从真实连通分量中选择离观察者最近的一格；不会误选包围盒内不属于该分量的同类矿物。目标代表单格，不代表整个矿脉；没有返回候选不证明完整世界不存在矿物。保留 `api('mine_target',{x=...,y=...})` 绝对 Tile 坐标入口，供玩家明确指定位置等场景使用，不能与 target 混用。

能力 `server_ai_tasks_v1` 同时依赖现有 `server_ai_atomic_v1/server_ai_actions_v1`；旧服务器在 RPC 前拒绝，普通客户端 TCP 明确拒绝该操作。目标是新增可选字段，不改变已有 scene schemaVersion=2 的原字段。老 scene 不具备该字段时不得自行制造观察目标。

目标对象含 kind、x/y、typeId、worldSessionId、actorGeneration、actorSlot、observedGameUpdate、observedAtUnixMs、targetToken。服务端保存 Actor 对象身份、采样形状和修订计数，逐字段检查对象没有被改写；30 秒或 600 游戏更新任一过期即拒绝。最多保留 256 个观察令牌；512 个目标修订监视项使用单调修订号，淘汰使旧比较失效。原生 KillTile/PlaceTile 路径即使同类型替换也使旧令牌失效。只给绝对坐标时，在任务预检时捕获目标类型/形状/修订，随后同样重验。

## 生命周期与执行边界

- `TaskRuntime.cs` 提供 `CompanionTask`、`TaskRecord`、`TaskSlot`、`AbstractCompanionTask.RunChild` 和原生动作叶任务。单根任务与当前活动子任务，不实现并行、抢占、持久化或后台多任务。
- `TaskSlot` 在真实 GameUpdateCount 增加时最多推进一次；受理当刻不执行任务，重复 IPC Pump/IdleTick 不启动或推进子任务。取消、断连、身份失效和墙钟截止不依赖游戏继续 Tick，仍可及时停止。
- root taskId 等于唯一外部 actionId；内部子 taskId 为 root/approach、root/mine，直接进入 `ServerAiNativeActions.StartCore` 的内部执行路径。没有再次调用顶层 IPC、`AiAtomicActions.Handle` 或新增外部 Action 记录，也不创建第二个执行器改变驱动归属。
- 预检使用原 `ActorMining.Healthy`、原导航 profile 与绑定检查。只选择现持的纯镐，或快捷栏中第一个受支持纯镐，经原生 `ActorItemUse.Select` 选中；随后固定槽位、物品引用、type/prefix/pick/tileBoost 并重验。
- MineTargetTask 先检查实际站位。不能直接挖掘时，只创建一个导航子任务；完成后重新观察，再创建一个挖掘子任务。子任务必须有原生效果和输入释放证明，父层结算还复核真实 Actor 身份及输入释放，不一致保持 unknown；未完成、失败、取消、超时或 unknown 均不会被包装为成功。
- 父/子任务均有游戏更新截止，根任务 2700 更新、导航 1800、挖掘 600；根 Action 另有 55 秒墙钟截止。子任务不冻结根截止。外部取消/超时原因传入根记录；未知清理或副作用保持 unknown。身份变化只废弃记录，不用旧令牌写新 Actor。
- M1 恢复次数为零：没有自动重试、换路重试、挖遮挡、相邻格补偿或回滚。已知失败也会停止当前任务；Lua 原失败屏障和 unknown 连接隔离不变。

任务状态通过原 `action/get_movement` 回执的 `task` 字段提供；执行后的 `evidence.task` 保存同一任务记录。字段包括 taskId/taskName、parentTaskId、phase/status、worldSessionId/actorGeneration/actorSlot、observedGameUpdate、deadlineGameUpdate、currentChild、progress、lastResult、reason/result。progress 含目标、工具槽、阶段、成功/已结算子任务数量和子任务历史；lastResult 保留原生导航/挖掘证据。受理阶段或原生准入拒绝尚未创建任务槽时，仍提供明确的状态对象。完成必须是目标被本次原生挖掘破坏、目标仍为空及输入已释放，不证明掉落已入包。

## 站位判定

`ActorMining.QueryReach` 复用已钉住引擎版本的 `IsTargetTileInItemRange` 克隆函数（原矩形范围、tileRangeX/Y、工具 tileBoost），再叠加既有 80px 保守门禁。假设站位使用脱离 Main.player 的浅复制 Player，仅给已审计的只读范围函数读取位置/宽高；不修改真实 Player，不调用复制体 Update/移动/物品使用，也不改全局鼠标目标。IL 安装校验保留精确指纹，并拒绝范围函数出现写字段或方法调用。

服务端在目标附近最多 13×13 个粗候选点上检查连续 9px 的完整区间，要求每点通过原生交互范围、现有 NativeMotionWorld 安全平面落脚与 MiningSupportImpact 移除目标后支撑不变检查，再生成 LandingRegion。路径仍交给原 ServerAiNavigation/NavigationTask；到位后按真实角色位置重新验证同一范围和支撑，才进入 ActorMining。不以中心距离或普通落脚候选代替挖掘可达性，不放宽原导航/落点阈值。

## 验证（2026-11-01）

最终正式包 `0.1.73`，SHA-256 `0e48c0c6dea9634f1bcf102f2d48e43551650449d18ee18a8c1e6d5afe6c1d01`，构建 0 错误、0 警告。[原始证据、命令和逐文件哈希](evidence/m1-task-runtime/manifest.json)。

| 类型 | 实际结果 |
| --- | --- |
| 任务运行时离线 | 48 项检查：预检/单次启动、重复更新不推进、父任务不提前完成、结果与历史、取消/超时/死亡、Actor/World/Generation/slot 变化、准备/启动/轮询异常、效果不足及目标改变。不把执行器替身称为 Terraria 实测 |
| C# 协议与包 | IPC 45、原子 40、读取 63、原生适配替身 239、聊天 30、正式包 86 项 IL 检查通过；其中检查内部子执行不进入 IPC/顶层 Action |
| 场景与原生 IL | 场景 2018 项（含包围盒内独立矿物不能被归错分量）；G6 19 / G7 30 / placement 119 / crafting 11 项钉住方法检查通过 |
| Python | 新功能及真实 Agent + HTTP 模型替身 + Lua Worker/loopback 6 项、既有服务器后端 36 项通过；结构化目标原样传递、一个工具执行只提交一个外部任务、证据缺失/伪造为 unknown、unknown 不提交第二任务 |
| 隔离原生任务 | 16 场：需要接近/已经可挖、阻挡、无工具、8 秒完全无轮询仍自主完成、绝对坐标、目标变型/同型替换、改写代次/世界/槽位/类型/坐标、真实墙钟过期、冻结游戏更新仍能查询/取消、取消活动导航子任务 |
| 同包旧行为 | 39 个库存场景、7 次真实库存 Lua/IPC 动作、原读取/选栏/导航/使用/挖掘/取消/进程清理通过 |

日志核对 16 个外部根任务、9 个内部原生子启动；子 taskId 与任务历史一一对应，各启动仅一次，没有外部 Action 派发。第一次 M1 原生运行继承旧苹果测试的 WellFed，被现有 profile 门禁拒绝；隔离夹具清空 Buff 后复验，生产条件/阈值未放宽。第一次成功运行的外层报告深度不足，但独立原始任务结果完整；最终 harness 仅在 Tasks 模式提高报告 JSON 深度，最终归档无截断。

## 未覆盖与限制

未运行全量测试、图形 Host & Play 或真实付费 LLM，未验证远端客户端收包；用户现用 profile 保留此前部署的 0.1.72。原生 deadline/身份/异常主要由离线替身及既有原子设施覆盖，本轮不声称所有此类竞态都在原生环境重演。

仅普通原版矿物单格、现有普通运动 profile、快捷栏工具和局部预测窗口。没有完整矿脉采集、自动挖通阻挡、全局探索、复杂洞穴/多高度矩阵、动态避敌、地形坍塌保证或拾取归属验收。任意绕过原生 hook 的直接 Tile 改写并恢复全部采样字段不能被修订监视完整识别；Mod 自定义状态未知。任务选镐可能改变当前选栏，失败/取消也不回滚。

## Numen 参考范围

参考提交 `dcbcf5627225b0b600175e26fe7682fc3ba14f52` 的 [Task/TaskRecord/TaskSlot](https://github.com/Dwinovo/minecraft-numen/tree/dcbcf5627225b0b600175e26fe7682fc3ba14f52/numen-api/common/src/main/java/com/dwinovo/numen/api/task)、[AbstractCompanionTask.runChild](https://github.com/Dwinovo/minecraft-numen/blob/dcbcf5627225b0b600175e26fe7682fc3ba14f52/numen/common/src/main/java/com/dwinovo/numen/task/base/AbstractCompanionTask.java)、[Target](https://github.com/Dwinovo/minecraft-numen/blob/dcbcf5627225b0b600175e26fe7682fc3ba14f52/numen-api/common/src/main/java/com/dwinovo/numen/api/sdk/Target.java)、[ScanApi/ScanOps](https://github.com/Dwinovo/minecraft-numen/tree/dcbcf5627225b0b600175e26fe7682fc3ba14f52/numen/common/src/main/java/com/dwinovo/numen/tools) 和 [DigCompanionTask](https://github.com/Dwinovo/minecraft-numen/blob/dcbcf5627225b0b600175e26fe7682fc3ba14f52/numen/common/src/main/java/com/dwinovo/numen/task/dig/DigCompanionTask.java)。借鉴生命周期、持续驱动同一子任务、结构化观察对象直接提交及交互目标约束导航的思想，独立编写 C# 实现；不复制 Java 源码或引入 Numen 的调度/恢复策略。
