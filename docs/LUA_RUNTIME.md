# Lua 编排 Runtime

## 0.1.69：P0 安全失败恢复与库存范围（2026-11-01）

起点核对：本轮工作区 HEAD 为 `da0aae65c956884f5a7b0028e4bc03561c7b9380`；`build.txt`、部署回执均为 0.1.68，现用 profile 包磁盘 SHA-256 为 `e215ecb715bff66abfa31240b1bab3290baa8e3e2e318a344a7b3565f0e439b8`，与前轮原生证据一致。开始时已有 Python 探索/诊断改动未提交，保留且不纳入本次修复；未把包版本等同于所有 Python 源码已提交，未宣称运行时日志独立证实了包哈希。

恢复始终由宿主在把结果发送给 Worker **之前**判断：真实 `failed`、有效且一致的 actionId/session/world/actor/generation/slot、同操作、known 游戏线程结束证据、明确已释放输入且 effectVerified=false。未知/缺失/空值/错误类型不作零值或 false。规则不依赖 reason 文本：

| 情况 | 允许返回 Lua 的必要证据 |
| --- | --- |
| 本地 bad_argument | 已知 API 在分发前 schema/范围校验失败，且调用在受控 pcall 内；返回 false 和结构化错误/diagnostics，计 API 次数，无游戏 RPC；动作参数同样适用，未知 API 仍终止 |
| 既有安全预检拒绝 | 原 mutationAttempted=false/effectStatus=not_started、未取得输入及全部绑定/释放条件保留 |
| mine_tile | Ticks/Swings/Strikes/PeakDamage/Transforms 均为整数 0；targetUnchanged 明确 true，targetChanged/nativeDestroyed/targetAbsent/destroyedByActor 明确 false，effectStatus=not_started、inputsAcquired=false |
| place_object | nativePlacementEvents/consumedCount 为整数 0；targetChanged/uncertainNativeEffect/placedByActor/targetPresent/fullFootprintVerified 明确 false，objectPlacement=true、effectStatus=not_confirmed；曾启动使用动画或取得输入不证明有游戏效果，但必须确认最终释放 |

`mine_tile` 的 C# 回执额外按原挖掘匹配规则重新核对已捕获的目标类型/形状/驱动状态（斧类及装饰目标含原帧），用 targetUnchanged=true 独立确认未变化；未捕获或不匹配为 false，不把未捕获伪装成已发生变化。旧后端缺少此确认字段时继续阻断新的挖掘恢复，既有安全预检和导航降级仍保留。此变化只补充观察证据，不改变挖掘执行。输入释放不等于无游戏效果；任何非零挖掘效果、放置/消费/目标变化、unknown/timeout/cancelled、断连/协议/身份异常或释放不确定仍在当前调用处终止 Worker，按原规则清理及隔离。导航 `observe_scene → 一次 move_input` 的宿主授权保持独立，不被一般恢复覆盖。

恢复不新建 Worker，原局部变量保留；不自动重试、不移动/选工具、不重置调用/动作/累计 Lua 时间预算。脚本可以依据失败结果读取实际状态并自行编排后续操作。`actionResults`、`lastAction`、`lastObservation` 和 actionSuccess/taskOutcome 语义保持：恢复后的程序正常返回，不会把曾失败的动作标为成功。

库存契约同步改为必填 mode，见 [完整范围说明](ATOMIC_OPERATIONS.md#inspect_inventory)。遗漏在本地校验产生 bad_argument，可由受控 pcall 接收；C# 亦拒绝遗漏，无 selected 默认值。`inspectedSlots` 表示实际读取的 player.inventory 索引；`inventoryScope.arrays` 对 full 额外列出原版装备/社交栏、染料、misc 装备/染料、全部 Buff 槽。item 成功解析名称后搜索完整 player.inventory，返回匹配 slots 只是子集；歧义/未知名称在搜索前结束，实际检查槽为空。不查询银行/箱子/Main.mouseItem/Mod 自定义槽，不能把局部查询当作完整背包。

验收入口：`tests/test_p0_recovery.py` 使用真实 Worker/Dispatcher/loopback 和游戏替身；C# `-ServerReadsOnly`、`-InventoryOnly` 验证真实共享读取实现及参数，IPC/正式包 IL 检查与 Mod 构建另记。未进行本轮图形实机、真实模型或原 0.1.67 场景重演；历史 play3 回执用于核对原始全零事实，不改写原始文件。

实际验证：126 个不同 Python 定向用例最终通过（P0 新矩阵、pcall/Runtime、Agent、库存、既有恢复、导航降级、契约及部分结果）；pcall 与既有观察各一项首次触发用例自身截止，原记录保留，单独复验通过，未放宽预算。新增 P0 夹具首轮请求计数跨子用例累积，修正测试计数后矩阵通过；未因此放宽产品谓词。精确暂存源码副本额外 4 项关键用例通过，排除原有未提交探索改动的依赖。C# 共享读取 16 项及扩展/库存定向组、IPC/215 项适配器替身/76 项正式包 IL 通过，0.1.69 构建零错误/警告；未跑全量测试。原始日志与包/源码哈希见 [P0 验证清单](evidence/p0-recovery-inventory/manifest.json)。本轮未部署现用 profile。

## Lua 失败后的部分动作结果（2026-10-31）

模型可见的 `execute_lua` 回执新增 `actionResults`，由 `Agent.observe_lua` 收集的宿主动作结果经 `lua_agent.summarize` 投影。无需让失败的 Worker 继续运行或返回。`items` 按 `sequence` 排序，每项含 `phase`、`summaryOnly` 和真实 `result`；`phase=api` 为已回调的动作，`terminal` 为 Runtime 仅保留的终态，`cleanup` 为额外的清理观察，不表示重复执行同一动作。重复的 lastAction/cleanup 不重复计数。

通常完整保留动作回执，包括操作/动作 ID、状态、失败原因、效果与输入释放证据。例如先选栏成功、后续使用失败时，历史分别保留 `completed` 与 `failed`；后者为 `unknown` 时也保持 unknown，即使 `endEvidenceStatus=known`、`inputsReleased=true`。释放证据只证明控制输入已释放，不证明动作效果已确定。

`actionResults` 整体 JSON（`ensure_ascii=False`、默认分隔符）的 UTF-8 大小上限为 **32,768 字节**，包括列表与元数据；该额度独立于既有 value/lastAction/lastObservation 上限。先为最后终态和早期进度保留紧凑摘要，再利用余量保留完整回执。摘要只保留有界标识、真实状态/原因和可用的释放布尔证据；不补造成功或安全结论。`summaryOnly=true` 明确表示其他字段或字符串尾部已省略。

- `totalCount`：投影前收到的结果数，包含必要的独立清理观察；不等于执行动作次数。
- `returnedCount` / `omittedCount`：返回项数 / 整条未返回数，两者之和为 totalCount。
- `summaryOnlyCount`：返回项中仅摘要的数量；完整回执数为 returnedCount 减去该数。
- `statusCounts`：省略前各真实状态的数量，不是任务成功统计。
- `truncated`：存在摘要或整条省略；`byteLimit`：历史的总大小上限。

`value` 仍只在 Lua 正常返回时表示程序返回值；程序失败后不把部分结果放进 value。`programCompleted/actionSuccess/taskOutcome` 的计算、`lastObservation/lastAction`、失败停止、取消清理、输入释放、unknown 隔离及不自动重试均沿用现有机制。部分结果可见不授予续行权限。

模型获得回执后可解释真实部分进度。正常模型最终回复中的解释不再被通用 Lua 失败文本覆盖；宿主仍在前面保留有界失败/unknown 提示，并沿用整条聊天长度上限。无模型回复或预算耗尽时仍使用原有确定性后备文本。

定向证据：`tests/test_lua_partial_results.py` 使用真实 Worker/Dispatcher/Agent 与游戏、模型替身验证成功后 failed/unknown、跨程序动作屏障、解释保留、正常返回及纯 Lua 错误。另以大回执和 256 项历史验证精确字节上限、摘要/省略计数、真实释放证据和最终 unknown 保留。不调用真实模型或游戏；本轮无 Mod 代码变更。

验证结果：新增部分结果 7 项、既有 Agent 21 项、Runtime 33 项、恢复/观察 6 项、服务端契约 8 项，共 75 个不同用例通过；精确暂存源码副本额外复验新增 7 项通过，排除已有未提交 Worker/探索改动的依赖。未运行全量测试；真实模型最终措辞和图形游戏任务待实测。

## 阶段二：受控 pcall

`pcall(f, ...)` 经私有宿主回调调用 Lua 函数，未开放原生 pcall；成功返回 `true, ...`，
保留多返回值和中间/尾部 nil。普通 Lua 运行时错误返回 `false, 有界错误字符串`（最多 512 字符）。
所有已知 API 在**分发前本地 schema/跨字段范围校验**拒绝时，返回
`false, {status='bad_argument', error, argumentOperation, diagnostics?, diagnosticsTruncated?}`。
可在同一程序内选择正确参数再查询；拒绝仍消耗调用预算，不产生游戏 RPC，不替换 lastObservation。
未放在 pcall 内的 API 参数错误沿用原终态及诊断。未知 API 不可恢复；已知动作 API 的本地参数错误可在受控 pcall 内返回，未分发任何动作。

```lua
local ok, result = pcall(function()
    return api('scan_tiles', {width=3}) -- 本地 schema 拒绝
end)
if not ok then
    return {rejection=result, observed=api('inspect_inventory', {})}
end
return result
```

基于 b2f255c 的修正：动作未 completed 且宿主未授予 `continuationAllowed=true` 时，
父进程在收到真实回执后终止当前程序，不把失败结果交回 Worker。pcall（包括嵌套）不返回，
后续日志、查询、计算和动作均不执行；脚本 value=null、programCompleted=false，
真实终态、结束/效果证据保留在宿主 lastAction、trace 与 on_result。已知 failed/timeout/cancelled
沿用该程序终态，unknown 仍为 action_unknown 并执行原隔离，不降级为普通失败。
只有原有宿主证据明确授权的安全预检拒绝可返回 Lua 并继续，pcall 此时可返回 true 与拒绝回执；
这不证明动作完成。continuationAllowed 判定、失败屏障、身份与预算不变，Lua 自报不能授权。
只读原生拒绝回执也按原接口返回，不转成可捕获异常。通信、协议、宿主异常及不确定状态不纳入恢复。

指令 hook 调用私有宿主终止回调，设置当前 Worker 无法清除的终止标记；受控 pcall 不会将其当作
普通 LuaError。Lua 内存错误按 LuaMemoryError 单独处理并强制退出；API/log 的宿主异常也不可恢复。
unknown、取消、墙钟、API/动作预算继续由父进程监督并终止 Worker，脚本没有捕获入口。
指令计数不重置；错误恢复、嵌套调用及私有封装执行均受原预算。未开放 xpcall、print、debug、
元表或其他标准库；输入释放、隔离、返回值编码、lastObservation 和游戏能力不变。
私有封装保留多返回值时仍受既有节点上限约束；恢复不会获得额外的内存或返回值额度。

本次动作失败语义修正（基于 b2f255c）：真实 Lua Worker 的 15 项 pcall 测试和 84 项相关回归
全部通过，包含无 pcall/嵌套 pcall 后的日志与查询均不执行、真实终态与释放证据保留、安全预检
续行、预算、unknown/取消/超时、Agent 跨程序屏障和失败合成。没有 Worker 泄漏。
在 `tests/` 执行：

```powershell
../.local/lua-venv/Scripts/python.exe -B -m unittest test_lua_pcall -v
../.local/lua-venv/Scripts/python.exe -B -m unittest test_lua_runtime test_lua_recovery test_lua_agent test_server_ai_crafting test_atomic_operations test_lua_arguments -v
```

原始输出为 `.local/pcall-action-terminal-tests.log`、`.local/pcall-action-terminal-regressions.log`。
本次未部署；未运行真实模型、原生 Terraria/Host & Play 或无关全量测试。

以下为 b2f255c 初始实现的历史验证，其动作失败续行语义已被以上修正替代。
2026-10-29 定向验证（基于 32ceba2；lupa 2.6 / Lua 5.4）：新增 `test_lua_pcall` 的
15 个方法分批通过，覆盖嵌套/多返回值、本地只读拒绝、真实终态与安全预检续行、unknown、
事件/任务取消、墙钟、指令 hook（含 sort 回调）、内存、日志/调用/动作预算、错误协议帧和进程回收。
另运行以下 105 项相关旧回归：

```powershell
# 在 tests/ 中运行；以下为最终源码的复现命令
../.local/lua-venv/Scripts/python.exe -B -m unittest test_lua_pcall -v
../.local/lua-venv/Scripts/python.exe -B -m unittest test_lua_runtime test_atomic_operations test_lua_arguments test_lua_tables test_lua_recovery test_lua_coordinates test_lua_observability test_lua_agent -v
```

旧回归初跑 104 通过、1 项已有断言过时：客户端工具说明不应列出服务器专属 API。
只校准 `test_lua_agent.StoreTests.test_model_contract_uses_existing_atomic_schemas`，该方法单独重跑通过；
没有为通过测试扩展能力，也没有重复运行其余已通过的测试。
原始日志为 `.local/stage2-pcall-tests.log`、`stage2-pcall-edge-tests.log`、
`stage2-pcall-protocol-tests.log`、`stage2-regressions.log` 和 `stage2-contract-regression.log`。
这些是实际隔离 Lua Worker、模拟游戏/本地 HTTP 模型及 loopback 后端验证；未调用真实模型，
未运行原生 Terraria 隔离场景、图形 Host & Play 或无关全量测试，未构建或部署新 Mod。

## 阶段一参数诊断（基于 0.1.62）

API schema 拒绝仍使用程序终态 `bad_argument`，不冒充原生动作失败，也不会发起该 API 的游戏 RPC。
共享校验器返回 `diagnostics`（最多 8 项）与 `diagnosticsTruncated`，每项包含字段 path、稳定问题 code、
有界 message 和可确定的 expected/actual/allowed 等 schema 事实；不回显参数值。
`error` 保留有界摘要，`failure.call` 保留原 API 名、调用编号和源码行。
`scan_tiles` 必须提供整数 `minX/maxX/minY/maxY` 与整数 ID 数组 `tileTypes`；不是字符串数组。
`resolve_content` 必须提供 `kind=item|tile|wall` 与字符串 `query`，`id` 并非该 API 的参数。
缺失、未知、类型、枚举等问题尽可能一次报告；有多个问题时最多报告 8 项并标记截断。
已有跨字段约束仍返回真实有界错误摘要，不猜测缺少 schema 的接口结构。

## 阶段一混合表返回值

`return {10,20,30,summary="collected"}` 自动使用版本化 `$luaTable` 封套，分别保留连续数组与字符串字段。
纯数组/对象沿用 JSON 语义；保留键冲突、嵌套、空表和解码规则见 [返回值编码 v1](LUA_TABLE_ENCODING.md)。
循环、稀疏或不支持的键/值返回 `serialization_error`；真实资源超限仍为 `value_limit`。
API 参数仍为普通 JSON schema，沙箱权限与动作安全语义不变。

## 当前失败恢复契约（0.1.62）

`lastObservation` 独立于脚本返回的 `value`：宿主保存本程序最后一次成功只读 API 的
`operation/callId/runId`、可用的 `gameUpdate/sceneId`、身份与采样时间，以及 `result` 原始只读回执。
它经过现有 JSON 类型/深度/节点校验并与 Worker 数据分离，最多 64 KiB；超过上限时
`payloadOmitted=true`、`result=null`、`omissionReason=last_observation_limit`。完整性、未知、截断
保留在 `result` 内，不改写成完整观察。后续程序可使用模型从该回执提取的仍有效数据，
不必仅因脚本异常重复查询；没有新增跨程序可写状态、隐式重放或观察 API。
观察仍带原来的身份、范围和时效，移动/世界变化或时效不明时需重读，不能凭保存成功宣称仍然新鲜。
运行时异常保留真实失败状态与 `value=null`。整段 Lua 先编译，语法错误不会执行其前面的 API；
没有成功只读调用的程序不会伪造 `lastObservation`。unknown 后的连接隔离和输入释放契约不变。

动作默认非 completed 即终止当前 Lua 程序。唯一续行例外是宿主按真实终态设置的
`continuationAllowed=true`：必须 failed，`mutationAttempted=false`、`effectStatus=not_started`，
绑定动作身份匹配，输入从未取得且已确认释放，结束证据 known，且没有消费/原生事件或不确定副作用。
不按 reason 字符串判断，不接受 Lua 自报；允许程序选择不同参数/候选，宿主不自动重试。
所有尝试照常消耗原调用、动作及墙钟预算；Agent 整轮只沿用该宿主判定，不重置预算。
已开始失败、取消、超时、断连、身份/释放未知及 unknown 继续保持硬屏障。

纯字符串白名单仅新增 `string.sub` 和 `string.len`；用 `string.sub(row,start,end)`，
`row:sub()` 仍不可用。未恢复字符串 metatable，也不开放其余 string 库、load/debug/os/io/网络或宿主对象。
`observe_scene.data.geometry.originX/originY` 与 `map` 共用 Tile 原点；Lua 行列从 1 开始，
Tile 坐标为 `(originX+column-1, originY+row-1)`，解析 45×29 网格不需要逐格 inspect。

原实机问题的逐候选核对、限制及本轮定向验证见
[原始证据与分析](evidence/lua-recovery/analysis.md)。以下保留历史阶段记录。

2026-10-28 自主编排：Lua 纯计算白名单为 `math.abs/min/max/sqrt/ceil/floor/tointeger/huge/pi`
和 `table.insert/remove/sort/concat/pack/unpack`，用于距离、排序、列表和局部有限状态。
仅复制这些 Lua 函数到每次新建环境，不开放 random、元表、加载器、原生 pcall、协程、文件、网络、系统或 Python 对象。
局部 table 生命周期为一次程序；不能借此跨程序恢复失败动作。sort 比较函数仍受指令 hook，列表分配受内存限制，
C 库调用仍受父进程墙钟截止与 Worker 终止约束；API/动作预算不变。
`math.huge` 仅可作内部比较哨兵，非有限数不能返回/传API。`table.pack` 含 n 字段，
阶段一现可作为混合表返回，保留数组与 n；稀疏表仍拒绝。无新增固定游戏任务函数。

G8.5 坐标修复：
原子参数验证返回规范化副本：整数Schema接受无小数的有限浮点值（如2113.0），发送游戏端前
转为JSON整数；小数、布尔、非有限数和超限值仍拒绝，number字段保留原精度。
详见 [G8.5实机坐标修复](G85_SERVER_AI_PERCEPTION.md#实机反馈修复浮点位置到tile坐标)。

0.1.40增加源码行号、受限log、任务证据包与独立动作预算，当前行为见 [可观测性与预算](LUA_OBSERVABILITY.md)。

第二阶段已接入 Agent/LLM，使用方法与新增宿主策略见 [Lua Agent](LUA_AGENT.md)。下文保留第一阶段设计与验证记录。

基线：干净的 `feat/atomic-operations-v1` / `ae4d8037bbfee1c32560df7e4b61f28aee621833`。
采用当前 0.1.37 原子操作、多人与输入释放契约；没有合并旧实验分支。
`lua_worker.py` 改编自 `origin/spike/lua-orchestrator-prototype` 的
`prototypes/lua_executor/executor.py`：保留 spawn Worker、显式 Lua 5.4、白名单环境、
指令 hook、内存限制，替换其模拟 dispatcher 和单向回执为双向有界 JSON 通道。

本阶段提供完整程序入口、固定程序和离线验证，不修改 LLM 工具列表或提示词。
无需修改、构建或部署 Mod；真实游戏运行要求已加载支持目标操作的当前 Mod。

## 安装和执行

在仓库根目录，Python 3.10+（本次验证 Windows / Python 3.11.8）：

```powershell
python -m venv .local/lua-venv
.local/lua-venv/Scripts/python.exe -m pip install -r python/requirements.txt
.local/lua-venv/Scripts/python.exe python/lua_runtime.py examples/lua/inspect.lua
.local/lua-venv/Scripts/python.exe python/lua_runtime.py examples/lua/select_and_verify.lua --seconds 15
```

先进入测试世界，停止使用同一 Bridge 端口的 Agent/其他脚本。默认端口 18777；
`--port` 指定另一个 Bridge，`--timeout` 是单次 Bridge RPC 超时，`--seconds` 是整段程序期限。
第一个示例只读取；第二个会选择快捷键 1，等待真实完成及释放证据后重新查询背包。
本次没有在真实游戏中执行这两份示例，不将其写为游戏验收通过。

可供未来 Agent 直接复用的宿主入口：

```python
runtime = LuaRuntime(bridge)
result = await runtime.execute(source, chat_id=current_chat_id,
                               limits=Limits(seconds=60), cancel=cancel_event)
```

`chat_id` 原样传给现有 dispatcher，沿用游戏端聊天身份与代次检查；本地 CLI 省略它，
仍使用 expectedGeneration。`cancel` 是可选 asyncio.Event。宿主 task.cancel() 会在收尾后
重新抛出 CancelledError；此时用 `runtime.last_result` 或 trace 查看收尾结果。
用脚本调用 Python 时必须遵守 multiprocessing spawn 的 `if __name__ == '__main__'` 入口要求。
宿主接口按现有 Agent 的单事件循环使用，不承诺跨线程/跨事件循环共享同一 Bridge。

## 执行链与 API

```text
完整 Lua 程序（独立 Worker）
  api(name, arguments) → 编号 JSON 请求 → Python 父进程
  atomic_operations.validate → await atomic_operations.dispatch
  Bridge / TCP → 当前 C# 游戏线程、导航与原子动作
  查询结果或终态 + endEvidence → 编号 JSON 回包 → 新建 Lua 数据 → 继续下一行
```

Bridge、socket、续租和动作监督始终在父进程。Lua 的同步回调阻塞 Worker 等待结果，
父事件循环保持可运行；不在 Agent 事件循环嵌套 asyncio.run，不把 coroutine/Python 对象交给 Lua。
同一时刻最多有一个 Lua API 请求，动作真实终态或失败返回后才可继续。

API 名称、参数和能力要求直接复用 `atomic_operations.TOOLS/SCHEMAS/validate/dispatch`，
目前27项（含宿主等待）；新增接口见 [基础能力扩展](ATOMIC_FOUNDATIONS.md)与 [背包操作](INVENTORY_OPERATIONS.md)，完整列表见 [原子操作说明](ATOMIC_OPERATIONS.md)。例如：

```lua
local inventory = api('inspect_inventory', {mode='hotbar'})
if not inventory.valid then return {ready=false, observation=inventory} end
local chosen = api('select_hotbar_slot', {slot=1})
if chosen.valid and chosen.completed then
    local after = api('inspect_inventory', {mode='selected'})
    return {selected=chosen, after=after}
end
return {selected=chosen}
```

不另建游戏 API Schema，不新增 TCP 控制协议。感知结果只需按 valid 判断可用性，不要求具有
`status=completed`。动作保留 valid/status/completed/actionId/session/actionGeneration/
controlGeneration/targetRegion/evidence/endEvidence 等原字段。`valid=true` 不等于成功；
`use_item.completed` 仍仅表示原生使用周期结束，不能替代效果确认。没有自动选工具、挖掘恢复或重试。

宿主回执的 `status=completed` **仅表示 Lua 程序正常返回**。它可能返回一个失败动作；
应同时查看 `lastAction`、`actionState` 和 `value`，不能据此宣称用户游戏任务完成。
CLI 退出码 0 同样只表示程序正常返回且动作状态可知。

## 生命周期、取消和未知状态

- 开始前拒绝已有运行中/未取得释放证据的动作，拒绝正在进行的其他 atomic dispatch。
  一个 Bridge 同时只允许一段 Lua 程序，进程内最多两段（不同 Bridge）。不排队迟到程序。
- Lua 持有 Bridge 会话所有权直至收尾结束。atomic dispatch 与底层 Bridge.command 都检查所有权，
  防止另一 Agent 轮次或旧 move 接口插入动作；to_thread 继承 ContextVar 身份。
- 每个动作复用 start_atomic、expectedGeneration、chatId、100ms 轮询/renew、原有绝对期限与
  750ms 游戏租约；轮询验证 actionId/session/generation/world/operation/targetRegion 一致。
  不把启动 ACK 当完成，不以杀死 Worker 作为游戏停止证据。
- 已确认释放的 failed/timeout/cancelled 保留在宿主真实回执中并终止当前程序；只有
  continuationAllowed=true 的安全预检拒绝返回 Lua。不自动恢复，pcall 不能吞掉终止。
  已被失败屏障阻止的行动请求同样终止当前程序。未知参数/API 终止程序，不发起对应动作。
- 本地能力检查的确定拒绝使用 `PreDispatchRejected`（仍是直接调用方可捕获的 BridgeError）。
  宿主保留 `valid=false,status=failed,completed=false,dispatchState=not_dispatched`，
  缺少原有安全续行证据时终止当前程序。没有启动动作时保持
  `actionState=idle`；此前已有已确认释放的动作时保留 `released`，不为拒绝伪造结束证据。
  仅专用拒绝类型且生命周期没有派发/动作/不确定标记时才走此路径，不能凭回执文字
  或 `start_task is None` 判断安全。动作派发器的启动前查询超时/通信故障、派发后回执不明及真实终态
  未知仍按原有规则封锁 Bridge。
- 脚本墙钟到期、取消、Worker 退出：先终止 Worker 冻结新请求，然后只取消正在 await 的
  dispatch 一次，留出独立收尾期限。若 start 回执还在途，沿用 dispatcher 的 shield-start 等待并
  精确取消取得身份的动作。重复 task.cancel 不打断这段必要收尾。
- 必须经过现有 stop/terminal 的同身份终态与游戏线程 endEvidence 校验，才能报告
  `actionState=released`；它表示 Bridge 输入已经释放，不表示惯性、弹幕或原生动画均被撤销。
- 缺证据、动作身份变化、通信歧义、supervisorError 或收尾超过预算：报告
  `actionState=unknown`，关闭连接停止续租，并把该 Bridge 对象永久标记不可复用。
  即使创建另一个 LuaRuntime，也不能继续调用该对象；不自动重连。
  关闭连接和租约过期是保护机制，**不是已经停止的证据**；游戏线程暂停时需恢复后才能处理撤销。
- `status=timeout/cancelled/worker_error` 与 `actionState=idle/released/unknown` 分开记录。
  调用数保留已经接收的请求数，未知收尾不会被描述为“没有发生动作”。已完成修改不回滚。

## 数据与限制

嵌套 JSON 对象变为字符串键 Lua table；JSON 数组为 1 起始索引的连续 table；
布尔、有限数和 UTF-8 字符串原样传递。返回值中的 JSON null 使用纯 Lua 的 `json.null` 哨兵，
不会因 nil 自动删除对象键/数组元素。比较示例：`r.target == json.null`。

来自宿主的空数组/空对象保留类型。脚本创建的 `{}` 默认对象，`json.array()` 创建空数组；
`json.array({1,json.null,3})` 保留中间 null；`json.object()` 显式创建对象。
非空纯连续整数键 table 自动识别数组；连续整数键与字符串键混合的返回表使用上述版本化封套。
稀疏数组、循环、函数、userdata、非有限数和过深值拒绝；错误与资源限制的区分见编码契约。
nil 返回表示 JSON null，nil table 字段仍遵守 Lua 自身的删除语义。

| 资源 | 默认/上限 |
| --- | --- |
| 程序 UTF-8 | 16 KiB；仅文本 load，拒绝字节码 |
| 整段墙钟 | 默认 60s，宿主可配 0.05–300s；包含启动/查询/游戏动作等待 |
| 收尾预算 | 独立默认 3s，可配 0.05–10s；另有短进程回收/关闭开销，非硬实时保证 |
| API 请求数 | 默认 64、最多 256；失败尝试和 blocked 请求也计数 |
| 变更动作 | 默认8、最多32次尝试；停止不占用，Agent整轮与直接动作共享 |
| 阶段日志 | 每程序32条、每条512 UTF-8字节；独立于API次数 |
| Lua 指令 | 默认 1,000,000、最多 10,000,000；每 100 条 hook 检查 |
| Lua allocator | 默认 16MB、最多 64MB；不等于进程 RSS 限额 |
| 单个 API 请求帧 | 16 KiB；现有游戏 TCP 仍有独立 4096 字节请求约束 |
| 单个 API 返回/脚本值 | UTF-8 JSON 256 KiB；深度 24、节点 20,000、对象键 256 bytes |
| Worker 帧 | 值预算 + 1024 bytes 协议开销；API与阶段日志各自编号递增 |
| 回执 | 有界脚本值、末动作和最多 256 条简短调用记录；程序状态与动作状态分开 |

超大感知返回报告 result_limit 并终止，不能静默删掉嵌套数据、数组条目或成功证据。
应使用 inspect_inventory 的 selected/hotbar/item 模式、有界 scan_tiles 等已有接口。
若动作回执自身超限，宿主保留标记 payloadOmitted 的末动作摘要；不会把裁剪数据交给 Lua。

环境只开放 type/tonumber/tostring/pairs/ipairs/next/assert/error、api、json helpers和受限log。
没有 io/os/package/require/load/debug/python、原生 pcall/xpcall/coroutine、元表接口或 print；
移除字符串默认元表，Python eval/builtins/属性访问关闭。每次运行创建全新 VM，变量不跨程序保留。
这提供语言权限和资源边界，**不是 OS 级敌对代码沙箱**：Worker 仍继承宿主系统权限，
没有 Windows Job Object/低权限令牌、RSS/CPU 系统配额或原生 VM 漏洞防护。

## 日志与验证

复用 agent_trace 的 JSONL、脱敏和 5MiB 轮转；CLI 默认启用，嵌入调用沿用宿主 trace 配置。
`lua_start` 记录源码 SHA256/长度/预算；`lua_api_start/result` 记录参数与原始结果；
`lua_cleanup` 记录已知动作身份及收尾；`lua_end` 记录总回执。lua_run_id 与现有
generation/chat_event_id/tool_call_id、IPC 请求/回执可关联。脚本正文不自动写入日志；
调用方需另行保存版本或生成源码以便复现。错误包含有界 Lua 行号/栈信息。

```powershell
.local/lua-venv/Scripts/python.exe -m unittest discover -s tests -p 'test_lua*.py' -v
.local/lua-venv/Scripts/python.exe -m unittest discover -s tests -v
pwsh -NoProfile -File tests/run_csharp_tests.ps1
```

测试层次明确区分：

1. `test_lua_runtime.py` 使用真实 Lupa/Worker 和生产 dispatcher，游戏后端为 Python 替身：
   多步条件、Schema、嵌套对象/数组/null、失败后阻止行动、资源/权限、取消/重复取消、
   延迟 start、缺释放证据、收尾超时、Worker 崩溃和会话互斥。
2. `test_lua_protocol.py` 使用真实 Lua → dispatcher → Bridge → localhost TCP → 当前 C#
   TcpControlServer/ControlState/玩家挂钩/导航规划执行和 endEvidence；仅原生世界/物理使用已有
   RegionWorld 替身。验证跳跃导航实际达到模拟落脚区，再由 Lua 分支调用 stop_action。
3. 真实 Terraria 尚未验证：单人、多人服务器确认、P 中断、暂停/失焦、真实物品效果与世界切换。
   不把以上离线结果当成这些场景的验收结论。

2026-10-23 验证记录：完整 Python 回归 345 项通过、现有 C# 离线回归通过。
之后补充数值边界用例与 CLI 输入上限，最终 Lua 专项 23 项全部通过（含真实 C# TCP 协议测试）。
日志保存在本机 `.local/lua-stage1-python-tests.log`、`.local/lua-stage1-csharp-tests.log`、
`.local/lua-stage1-final-lua-tests.log`，不提交生成日志或虚拟环境。

## 第二阶段接入

已基于 execute 接入 Agent 工具、Lua API 描述/提示词、脚本保留、成本统计、任务级结果解释、
新聊天取消/关闭等待及宿主集成测试；见 [第二阶段实现](LUA_AGENT.md)。真实游戏最小验收仍待第三阶段。
若计划接受主动敌对代码，先补 OS 权限/进程资源隔离；现有实现不作此安全承诺。
不在本阶段引入 Skill 系统、扩充原子能力或改造感知系统。
