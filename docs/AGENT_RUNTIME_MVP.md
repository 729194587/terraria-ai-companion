# 模块一 Agent Runtime MVP 实施记录

2026-11-02。本轮按 `AGENTS.md` 和本地设计输入 `docs/AGENT_RUNTIME_MVP_DESIGN.docx` 实施。任务指定的 `docs/AGENT_RUNTIME_MVP_DESIGN.md` 在工作区不存在，实际读取同名 DOCX；没有改写或提交原设计文件。工作区原有探索 helper、Lua Runtime/Worker、诊断和原生验证修改保持原样。

## 实施前核对

| 优先级与设计要求 | 原有实现及差异 | 本轮处理 |
| --- | --- | --- |
| P0 Run 生命周期 | 已有 generation、Task 取消、回复授权，但正常模型结束可能掩盖 Lua/动作失败；没有独立 Run 记录 | `AgentRun` 保存身份、阶段、终态、原因、预算、取消信号和实际计数；区分 completed/failed/cancelled/superseded/budget_exhausted |
| P0 上下文与压缩 | 已有请求副本、动态背包和旧 Lua/观察回执过期机制；没有整体输入预算、Working/Reactive Compaction | `ContextManager` 对请求副本估算与压缩；Canonical History 不因压缩而改变 |
| P0 工具执行边界 | Agent 内直接启动 LuaRuntime，并承担执行和资源结算 | `ToolExecutor` 负责单段程序、结果汇总、运行时回执和 elapsed；Worker 与游戏清理继续使用原 LuaRuntime |
| P0 中断事实交接 | Runtime 清理后保存 `last_result`，但 Agent 遇到取消就抛出，下一轮没有中断回执 | 清理后写入有身份的 interruption 记录；新 Run 等待清理后注入 `interruptedRuns` |
| P0 分层结果和最终回复 | 已有 programCompleted/actionSuccess/taskOutcome，但 taskOutcome 无已验证复合任务完成分支；部分直接失败回复覆盖全部模型说明 | 重用原 `verified_server_mine_task`；增加 goalOutcome，保留模型解释与建议，补宿主事实说明并过滤常见无证据整体成功措辞 |
| P1 异步衔接与失败恢复 | 已有 asyncio 队列、游戏线程分发和旧 generation 丢弃；模型请求失败立即结束 | 保留事件入口；明确等待工具清理的边界，瞬时模型错误最多重试一次，上下文超限最多压缩恢复一次，均计入整轮模型预算 |
| P1 指标与异常一致性 | 已有 Trace/Evidence/成本计数；异常回复部分未存入 History | 补上下文前后估算、触发原因、Run/Program/Action/Task/Goal 终态及中断事实；已确认发送的异常回复也写入 History |

## 文件职责与数据流

- `python/agent.py`：继续拥有聊天入口、整轮预算、旧客户端工具适配、generation 与回复发布；接入三个小模块，不替换控制后端。
- `python/agent_run.py`：Run 记录、结果分层、取消事实投影与最终文字守护。
- `python/agent_context.py`：估算、Working/Reactive 请求投影和有损历史摘要。
- `python/agent_tools.py`：Lua 执行边界；`python/lua_agent.py` 保留模型契约和工具结果归一化。
- `python/llm.py`：只分类可重试错误与上下文超限，不在 HTTP 客户端隐藏重试。

一轮数据流：聊天入队 → 撤销旧回复/取消旧 Run → 等待执行清理 → 冻结本轮 History 与中断事实 → 注入当前背包/剩余预算 → 预算与请求投影 → 模型 → ToolExecutor → 原 LuaRuntime → 原子分发与原 C# Task Runtime → 完整回执批次提交 → 最终文字整理 → 原回复授权 → 保存实际发送文本。

Run 阶段为 starting/running/model/tools/finalization/ended。未开始就被替代的协程也会落终态。普通新指令使旧 Run 为 superseded；明确的“停止”“停下”“停止行动”“stop”（兼容 `!ai` 前缀）与关闭使其为 cancelled。停止请求在清理后直接回复，不额外调用模型或发起游戏变更。最终发布仍检查 generation/chatId 和既有服务器授权。

Lua 内部仍顺序执行，已有原子调用负责提交并等待真实游戏任务终态；不新增异步任务 API。`mine_target` 的内部导航和挖掘子任务继续由既有 C# Task Runtime 驱动，保持单根任务、单活动子任务及原身份/效果/输入释放检查。

## 上下文和结果契约

默认输入估算预算 48,000，可由 `--context-input-tokens` 配置，包含工具定义。估算使用 UTF-8 字节数/3 向上取整，是估算而非 provider token 实测；配置时须给输出留出模型窗口。超过预算先整理较旧完整轮次，优先保留近两轮及本轮原文；只在 user 边界切分，不拆散 assistant/tool 配对。摘要是最多 12 KiB 的历史索引，显式标注 lossy、omittedRecords、非当前状态，保留可容纳的执行状态、身份和 evidence 引用。不能容纳当前轮/系统/工具时停止为 budget_exhausted，不静默删除这些数据。

摘要和候选投影均在副本构造，成功后才返回调用方；失败没有 Canonical History 提交步骤。动态背包、事件和运行时状态不因请求投影而累积到 Canonical History。Reactive Compaction 由明确的 provider 上下文错误触发，同一模型决策最多恢复一次；网络/超时/指定瞬时 HTTP 错误最多重试一次。无论恢复顺序如何，实际请求都占原整轮模型次数；不会重新调用已经执行过的 Lua 或原子动作。

中断记录保留已结算动作、最后活动动作/调用、取消原因、Run/Task 阶段、释放状态、未确认效果序号和 Evidence 路径。它是历史事实，不授予重试权限。进程内最多保留最近 8 次，coverage 明确给出未包含的更早记录数。输入释放字段缺失即未知；旧证据不能替代新观察。

- Program Outcome：只表示程序如何结束，正常 return 不是目标完成。
- Action Outcome：沿用真实动作回执与输入释放核验；`use_item` 的原生周期不证明实际效果。
- Task Outcome：只有复用原核验通过的 `mine_target` 才能确认为该任务 completed；一项已核验任务后续失败为 partial；未知动作或未知清理不能归成已确认未完成。
- Goal Outcome：普通自然语言目标没有通用完成契约，保守为 unknown；已完成动作后失败可保留 partial 进度。旧 MovementLoop 的明确位置目标仍使用已有抵达证据。单格挖完不推出整片矿脉采完或物品入包。

## 定向验证

仅模拟模型/游戏、真实隔离 Lua Worker 和 localhost Bridge；没有付费模型请求或实际游戏动作。沙箱内 asyncio 在初始化 socketpair 时阻塞，终止后改在沙箱外执行这些本地测试。

```powershell
$env:PYTHONPATH='python;tests'
& .local/lua-venv/Scripts/python.exe -B -m unittest test_agent test_request_context -q
& .local/lua-venv/Scripts/python.exe -B -m unittest test_agent_runtime_mvp test_lua_agent test_lua_partial_results -q
& .local/lua-venv/Scripts/python.exe -B -m unittest test_server_ai_agent test_mine_target test_exploration_agent -q
& .local/lua-venv/Scripts/python.exe -B -m unittest test_atomic_operations -q
```

| 覆盖 | 实际结果 |
| --- | --- |
| Agent 与请求上下文 | 20 项通过 |
| 新运行时与 Lua/部分回执 | 45 项通过；最后补未知清理语义后，受影响的 23 项及新增的无动作回执未知终态 1 项通过；共 46 个独立用例 |
| 服务器 Agent、mine_target 和探索衔接 | 26 项通过；mine_target 集成用例新增 taskOutcome=completed 与 goalOutcome=unknown 断言 |
| 客户端原子操作 | 11 项通过，1 项既有失败：`test_schema_dispatch_and_tool_names` 硬编码 31 个工具，当前实现为 34。相关源码/测试本轮均未修改，HEAD 的断言也是 31；未为本轮放宽断言 |
| 差异检查 | `git diff --check` 通过；保留已有工作区修改和未跟踪文件 |

因此相关核心用例共 92 项通过，加客户端原子测试 11 项通过，另有上述 1 项未解决的既有断言失败。日志在 `.local/runtime-mvp-final-*.log`、`.local/runtime-mvp-unknown-final.log`、`.local/runtime-mvp-unknown-run.log`、`.local/runtime-mvp-atomic.log`。未运行全量测试；没有 C# 源码修改，未构建、部署或开展原生隔离/图形/远端验证。

推送前按差异块仅暂存本轮运行时改动，从 Git index 导出独立 Python/Lua 快照，排除工作区既有探索、Worker 和诊断修改。该快照按上述前三组命令复验，20/46/26 项共 92 项全部通过；日志为 `.local/runtime-mvp-staged-agent.log`、`.local/runtime-mvp-staged-lua.log`、`.local/runtime-mvp-staged-server.log`。

## Numen 核对范围

本轮实际通过 GitHub 读取 `1.21.1`，固定到提交 `1a90f11c1ecbbda662c5a8fc0629c667bd519db3`：

- [AgentLoop](https://github.com/Dwinovo/minecraft-numen/blob/1a90f11c1ecbbda662c5a8fc0629c667bd519db3/agent/src/main/java/com/dwinovo/numen/api/agent/loop/AgentLoop.java) / [RunEnd](https://github.com/Dwinovo/minecraft-numen/blob/1a90f11c1ecbbda662c5a8fc0629c667bd519db3/agent/src/main/java/com/dwinovo/numen/api/agent/loop/RunEnd.java)：唯一生命周期与旧 Run 回调隔离、DONE/FAILED/HALTED 分离、受限 overflow recovery。
- [Program](https://github.com/Dwinovo/minecraft-numen/blob/1a90f11c1ecbbda662c5a8fc0629c667bd519db3/agent/src/main/java/com/dwinovo/numen/api/agent/loop/Program.java) / [ScriptCall](https://github.com/Dwinovo/minecraft-numen/blob/1a90f11c1ecbbda662c5a8fc0629c667bd519db3/script/src/main/java/com/dwinovo/numen/script/ScriptCall.java)：Dispatch/Await/Done、程序终态和有序调用账分开，中断不能抹掉已发生的事实。
- [TaskResult](https://github.com/Dwinovo/minecraft-numen/blob/1a90f11c1ecbbda662c5a8fc0629c667bd519db3/numen-api/common/src/main/java/com/dwinovo/numen/api/task/TaskResult.java)：游戏任务的结构化结算独立于程序 return。
- [Compactor](https://github.com/Dwinovo/minecraft-numen/blob/1a90f11c1ecbbda662c5a8fc0629c667bd519db3/agent/src/main/java/com/dwinovo/numen/api/agent/memory/Compactor.java)：预算触发、保留近期原文、摘要失败不破坏会话。这里使用本地确定性摘要与请求投影，不引入 Numen 的 Memory/多次模型摘要及持久化架构。

只借鉴职责与契约，没有复制 Java/Minecraft 代码或引入完整事件总线、并行任务树、导航重写。

## 剩余限制

自然语言目标的通用完成契约仍不存在，普通成功动作后的整体目标为 unknown。文字守护过滤常见无证据成功表述并补宿主事实，不是任意自然语言的形式化验证；最终事实依据仍是结构化回执。历史摘要有损且 Canonical History 仍在进程内保留，未实现跨进程恢复或长期历史内存上限。多次大体积中断回执与当前轮合计超出输入预算时会明确停止，不能假装这些证据不存在。模型窗口与 tokenizer 未自动识别，需按实际 provider 配置输入预算。
