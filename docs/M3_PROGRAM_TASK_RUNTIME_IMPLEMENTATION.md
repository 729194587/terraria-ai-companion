> **历史阶段资料**：本文记录特定开发阶段的实现或测试，不是当前能力契约。当前实现以源码、运行时能力握手及 `docs/INDEX.md` 中的专题文档为准。本文中的版本、状态、命令和限制均属于原记录时点。

# M3 Program / Task 协作实施记录

2026-11-02；起点为当前分支 `codex/g85-server-ai-perception` 的 `09bb4fef86a32519496363c7c20e6ef63f36fc79`。依据 [M3 设计](M3_Program_Task_Runtime.md)，保留原有未提交探索、Worker 和诊断修改。没有部署或付费模型调用。

## 修改职责

| 文件 | 职责 |
| --- | --- |
| `python/program_tasks.py` | 连接内任务回执邮箱；ACK、按 taskId 查询/等待/取消及显式恢复；完成序号、消费水位、有界保留；仅缓存服务器观察与结果，不推进游戏状态 |
| `python/server_ai.py` | 同一连接的 start/action/cancel，独立于 Program 所有者；保留绑定、动作身份、能力和 C# 连接归属检查，不重连/重放 |
| `python/lua_runtime.py`、`python/lua_agent.py` | 复用 Worker 的 api 协议处理任务协作；具备 M3 能力时原 mine_target 为提交并等待的兼容入口；活动任务仍投影 running；保留预算和 unknown 屏障 |
| `python/atomic_operations.py` | 原 stop_action 也取消独立任务；效果和释放核验仍用原 terminal |
| `python/agent.py` | runtimeTasks、新聊天保留任务、明确停止核验、迟到结果通知 Run；通知 Run 禁止新任务/身体变更；保留 generation 回复授权 |
| `python/agent_run.py` | Run/Program/Action/Task/Goal 分层；宿主结构化事实回复与模型故障兜底 |
| `python/server_ai_contract.py` | 按服务端能力及白名单暴露宿主协作 API/Manifest，仍只有 execute_lua 一个模型工具 |
| `MineTargetTask.cs`、`NativeMineTargetWorld.cs`、`TaskRuntime.cs`、`AiAtomicActions.cs` | 游戏 Tick 最小危险中断、正常子任务清理和父目标完成竞态；显式 M3 能力围栏；不改导航/挖掘执行机制 |
| `tests/test_m3_program_tasks.py`、`ApiContractProtocolHost.cs` | 真实 C# TCP/注册表 + Python Agent + Lua Worker 的跨层替身测试；参数、邮箱和通知权限 |
| `TaskRuntimeOfflineTests.cs`、`ServerAiPackageTests.cs` | 危险、释放、停止竞态及包 IL 检查 |
| `test_agent_runtime_mvp.py`、`test_atomic_result_contract.py`、`test_lua_agent.py` | 更新 M3 分层结果与宿主事实反馈断言；原 mine_target 效果验证测试不改动 |
| `CompanionBridge/build.txt` | 隔离包版本 0.1.75 |

## 关键契约与调整

1. **C# 仍是执行权威。** 复用现有 Action Entry、TaskSlot、MineTargetTask 及子任务；Task 身份就是原 Action GUID。Python 只保留收到的观察与结算。游戏自主推进，不依赖 Program 等待或每 Tick 模型调用；断连沿用原取消，无进程重启恢复。
2. **生命周期独立。** submit_task 仅等 ACK。正常 return、Program 资源终止、取消等待或普通新聊天不取消已受理 Task。原 `api('mine_target',args)` 仍返回原终态格式，但 Program 被替代后 Task 继续。其他普通原子动作保持原作用域清理。
3. **等待不改变执行期限。** await_task 到期返回 `waitStatus=timeout`，Task 可以仍 running；不取消、不延长既有游戏/监督期限。明确停止和 stop_action 才进入正常任务取消，停止成功仍需可信终态与游戏线程释放证据。
4. **受理不猜测。** 新提交使用新 GUID，保留 M2 not_admitted/unknown/duplicate 约束。ACK 丢失、目标/操作/身份变化、释放或效果缺证据不重试。unknown 即使消费后仍留在 active 未决投影并阻止身体变更。
5. **有界交接。** 终态只结算一次；最多保留 64 条，不回收未消费或 unknown 状态腾位。每批最多 8 条，复用原 32 KiB action-results 摘要；resultSummaryOnly、剩余计数和消费水位明确。模型得到本轮有效响应或宿主事实回复发布成功后才消费对应批次；宿主计算使用内部完整回执。迟到结果不能覆盖已结算事实。
6. **最小危险处理采用终止及显式新受理。** 游戏 Tick 检查相对初始生命下降、熔岩接触和 160 世界像素内有接触伤害的非友好活动 NPC，沿原 Stop 清理子任务并保留结构化原因/证据，不发起战斗/躲避。无进展沿原导航失败链处理。只有可信 cancelled、task_interrupt_* 原因及真实释放证据才允许显式 resume_task；新 GUID/resumedFrom，游戏侧重新 Prepare 验证环境、目标、工具和位置。不复用旧句柄或自动恢复，unknown/明确停止/普通失败不可经此入口恢复。
7. **结果通知不授予变更权限。** 迟到结果唤醒通知 Run，复用最后真人聊天身份和新 generation 回复授权；该 Run 的 Program 禁止新任务、恢复或普通身体变更，避免宿主通知成为自动重试授权。
8. **结果分层。** 最终模型失败只令 Run 失败，已完成 Program/Task 保持原事实。多个 Task 可产生 TaskOutcome=partial。自然语言 Goal 缺少明确目标契约时保守 unknown；只有宿主显式目标契约匹配且有已验证目标效果才能 Goal partial，选工具/导航不够；不新增自然语言目标分类器。
9. **执行事实回复由宿主构造。** 有动作、任务事实、Program 失败或执行状态不确定时，用结构化模板同时呈现成功、失败/unknown、子步骤、物品效果限制及模型故障；模型文本不作为执行事实输出。普通聊天及正常完成的纯只读 Program 保留模型回答，让玩家获得观察中的实际信息；原有整体目标声明限制仍保留。没有读取的脚本自述不作为执行事实。
10. **独立上下文。** 后台观察不继承旧 Program 的命令所有者、Run trace 或已结束 evidence bundle。真实跨层测试发现并修正了旧上下文继承及兜底已反馈结果未消费造成再次唤醒的问题。

## Lua 宿主 API

不是新增 C# 原子操作或模型工具。仅服务端同时有 server_ai_atomic_v1/server_ai_actions_v1/server_ai_tasks_v1/server_ai_program_tasks_v1、mine_target 白名单和任务传输时开放。新的显式能力围栏避免向旧包承诺 M3 的危险保护与停止竞态；旧包保持原 mine_target 同步作用域取消语义，不暴露新协作 API。

| API | 参数与返回 |
| --- | --- |
| submit_task | `{operation='mine_target',arguments=原参数}`；taskId/status/dispatchState/valid/completed/origin/retryAllowed。ACK 不等于完成 |
| query_task | `{taskId=32位小写十六进制GUID}`；最新核对状态及可用 result；终态为缓存的不可变结算 |
| await_task | `{taskId,milliseconds=1..30000}`，默认 1000；等待到期不取消 Task |
| cancel_task | `{taskId}`；取消及终态释放核验 |
| resume_task | `{taskId}`；仅上述已安全释放的危险中断，显式新受理 |

```lua
local t = api('submit_task', {operation='mine_target', arguments={x=10,y=20}})
return {taskId=t.taskId,status=t.status} -- Program 结束，Task 继续
```

后续 Program 使用模型上下文 runtimeTasks 提供的明确 taskId。runtimeTasks 不是 Lua 可写全局。活动/未决任务与新身体任务冲突时明确拒绝；替换前须取消并确认释放。

## 已执行验证

没有运行全量测试。Python 复用 `.local/lua-venv`，localhost/Worker 在沙箱外运行。最终隔离包 `.local/m3-final-build/Mods/CompanionBridge.tmod` 构建 **0 错误、0 警告**，SHA-256：`6631F4A9603D16396868366C93E5BB9E684824EC145E4C21AF98B41910BA2A1E`。早期无能力围栏的中间包另留在 `.local/m3-build`，不是最终交付包。

- 跨层覆盖：Program 结束、等待到期、取消等待、新聊天、明确停止、ACK 前取消/ACK 丢失、身份/释放不确定、迟到结果、去重、恢复、旧同步入口、stop_action、通知权限和最终模型故障。
- C#：IPC 45、原子 48、读取 68、原生适配 287、Task Runtime 64、聊天 30 项通过；危险子任务释放、清除危险不自动恢复、停止时父目标独立核验均覆盖。
- 最终正式包 **90 项 IL 检查通过**，包含 M3 能力围栏、绑定 Actor 健康/熔岩/NPC 检测、游戏 Tick 停止、不改写 Player 运动/控制字段。
- 独立 index 快照排除了原有探索/Worker/诊断修改：78 项定向回归通过（M3、M1 结果/中断、既有 mine_target、M2 能力交集/重复 ID/未受理）；最后补充活动状态投影后 24 项通过。最终能力围栏下 M3 的 17 项实际用例全部通过，同命令额外两个测试选择器类名误写为 AgentTests，产生 loader 错误；改用 AgentLuaTests 单独复跑两项通过，不把 loader 错误计为 M3 产品失败或掩盖原日志。
- Lua/聊天另 40 项运行完成：38 项通过，两个旧 TaskOutcome 断言按 M3 更新后单独复验通过。旧 M1 六条结果断言也已在 78 项结果组中修正并通过；没有放宽效果或释放校验。
- 日志：`.local/m3-staged-final.log`、`m3-staged-status-final.log`、`m3-capability-final.log`、`m3-lua-chat-final.log`、`m3-lua-assertions-final.log`、`m3-csharp-capability-final.log`、`m3-build-final.log`。早期 `m3-python-initial/second`、`m3-regression-initial`、`m3-results-final`、`m3-sync-and-authority` 等日志保留，未改写原始证据。

## 审查收尾：D1 / A1（2026-11-03）

- D1：依据宿主 trace 确认程序至少调用过一个只读 API，且只调用读取/宿主等待、正常完成、无变更尝试或未决执行状态、无任务事实时，保留模型基于观察生成的实际答案。动作、活动/未消费任务结果、Program 失败和 unknown 继续使用宿主事实模板；无读取的脚本自报不授予执行事实。原有整体目标声明过滤和各层结果核验不变。
- A1：monitor 的部分异常列表确实遗漏 RuntimeError 等普通异常。仅把 `_run` 的通用出口扩大为 Exception，沿原 `_unknown` 保留 taskId、结算及通知一次，并记录 `game_task_monitor_failed` 的异常类型和原因。不捕获 BaseException/CancelledError，不引入重连、恢复或重放。
- 修改：`python/agent.py`、`python/program_tasks.py`、`tests/test_lua_agent.py`、`tests/test_m3_program_tasks.py` 及本说明。新增矿石/地形/背包只读答案、纯脚本自报仍受限、活动任务读取仍受宿主约束，以及 monitor 启动/轮询异常的回归。
- 独立 index 快照 **33 项定向测试通过**：整个 test_lua_agent、M3MailboxTests，以及活动任务读取、Program 取消/显式取消、新聊天/停止、迟到结果通知、通知变更禁令、释放 unknown 禁止重放、最终模型故障等 7 项跨层场景。普通 monitor 异常包含启动/轮询两个子案例，只读回答包含三类查询子案例。日志 `.local/m3-review-fixes-final.log`；无全量测试。此轮仅 Python/测试/文档改动，没有重新构建或部署游戏包；跨层执行器与模型仍为替身。

## 限制与依据

端到端使用实际 C# TCP/注册表、生产 Python Agent/Bridge、真实 Lua Worker，但 Actor/原生执行器及模型为替身；不是 Terraria 世界验收。没有图形 Host & Play、远端多人、真实 NPC 危险时序、复杂洞穴或真实模型策略验证，没有部署用户包/profile/世界。

本阶段只对既有 mine_target 提供独立复合任务；没有通用后台导航/战斗库、原地暂停状态恢复、进程重启恢复或大型事件总线。危险规则是保守最小策略，不覆盖所有敌怪、投射物、陷阱或第三方 Mod。执行反馈采用有限模板；无明确自然语言目标契约时不自动判目标部分/全部完成。

Numen 实际读取固定提交的 [Program](https://github.com/Dwinovo/minecraft-numen/blob/1a90f11c1ecbbda662c5a8fc0629c667bd519db3/agent/src/main/java/com/dwinovo/numen/api/agent/loop/Program.java) 和 [TaskResult](https://github.com/Dwinovo/minecraft-numen/blob/1a90f11c1ecbbda662c5a8fc0629c667bd519db3/numen-api/common/src/main/java/com/dwinovo/numen/api/task/TaskResult.java)：采用程序结束后仍转交任务结果、任务事实独立的思路，不照搬 Java 类结构、任务错误续行许可或恢复策略。
