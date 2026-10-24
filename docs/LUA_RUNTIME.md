# Lua 编排第一阶段

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
- 已确认释放的 failed/timeout/cancelled 原样返回 Lua，可据其分支并继续感知；本段程序之后的
  行动请求返回 blocked，不自动恢复。未知参数/API 终止程序，不发起对应动作。
- 本地能力检查的确定拒绝使用 `PreDispatchRejected`（仍是直接调用方可捕获的 BridgeError）。
  Lua 收到 `valid=false,status=failed,completed=false,dispatchState=not_dispatched`，
  可继续只读感知，但本段程序的后续行动仍被失败屏障阻止。没有启动动作时保持
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
非空纯连续整数键 table 自动识别数组；混合键、稀疏数组、循环、函数、userdata、
非有限数和过深值拒绝。nil 返回表示 JSON null，nil table 字段仍遵守 Lua 自身的删除语义。

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
没有 io/os/package/require/load/debug/python、pcall/xpcall/coroutine、元表接口或 print；
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
