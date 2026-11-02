# M2 API Contract 与 Tool System 实施记录

2026-11-02。设计基线为 PR #25 的 `b6c2e46f0fd78980e405fe204945d8a42cb9fa6c`；按 [M2 设计](API_TOOL_SYSTEM_MVP_DESIGN.md) 实施，并保留工作区原有探索、诊断、Lua Worker 和原生验证脚本修改。正式隔离包版本为 **0.1.74**，没有部署现用 profile，也没有进入 M3。

## 修改职责

| 文件 | 职责与修改 |
| --- | --- |
| `python/server_ai.py`、`python/control.py` | 保留握手/传输/动作归属；增加 `NotAdmittedError`，仅在启动拒绝带 `not_admitted` 且响应绑定匹配时使用。丢失、超时、错误绑定或无证明的旧拒绝仍保守处理 |
| `python/atomic_operations.py` | 明确 `HOST_OPERATIONS/NATIVE_OPERATIONS`；Server AI `use_item(target)` 在任何 RPC 前拒绝，通用客户端保留 target；记录受理阶段，保留身份/结束证据/专属效果、停止原因及公共输入状态 |
| `python/server_ai_contract.py`、`python/lua_agent.py`、`python/agent.py` | 保留服务端单 `execute_lua` 和三方交集；补 Manifest 的既有 `mine_target`、修正库存变更边界文案；仅有可靠白名单的客户端过滤直调工具和 Lua API 描述，无白名单的旧协议保持兼容 |
| `python/lua_runtime.py`、`python/agent_run.py` | 只作 API 拒绝衔接：可信未受理不会变为动作 unknown 或隔离连接；仍终止本程序，`continuationAllowed` 不扩大。取消等待启动拒绝的路径也保留明确拒绝，不伪造输入释放 |
| `CompanionBridge/AtomicParameters.cs` | 独立纯参数校验，使用 `AtomicParameterException` 分类 JSON 类型/字段/范围错误；Unicode 长度按 JSON Schema 的 Unicode 标量计算；观察目标的 null 字符串明确拒绝；提取纯服务端动作限制供原生执行器复验 |
| `CompanionBridge/ServerIpc/ServerAiNativeActions.cs` | 继续在执行端调用通用校验与 Server AI 专属限制，无新原生动作或支持范围扩展 |
| `CompanionBridge/ServerIpc/AiReadOnlyProtocol.cs` | 启动成功/拒绝返回受理标记；查询只把明确参数异常映射为 invalid_request，实际读取异常（包括引擎 FormatException/InvalidOperationException）保持 engine_unavailable |
| `CompanionBridge/ServerIpc/AiAtomicActions.cs` | FIFO 有界保留终态身份；活动动作不回收；Stop 保留可靠原生终态，另用 stopRequest 记录取消/超时触发原因 |
| `tests/fixtures/api-contract-vectors.json`、`test_api_contract_mvp.py`、`AtomicParameterContractTests.cs`、`run_api_contract_tests.ps1` | 202 个共享向量实际执行 Python 与 C# 校验，包含所有原生操作的有效案例及所有权集合、跨字段、类型、边界、Unicode、后端收窄 |
| `ApiContractProtocolHost.cs`、`run_api_contract_host.ps1`、`test_api_contract_ipc.py` | 启动实际 C# TCP/协议/注册表代码，以 Actor/读取替身连接生产 Python Bridge/dispatcher 和真实 Lua Worker，验证确定拒绝与参数/引擎错误分类 |
| `test_server_ai_contract.py`、`test_server_ai_backend.py`、`test_atomic_operations.py`、`test_atomic_result_contract.py`、`test_lua_runtime.py`、`test_lua_agent.py` | 能力组合、白名单、集合归属、loopback 拒绝/unknown、取消及失败屏障回归；监督超时保留实际停止终态；同步受理状态字段命名 |
| `ServerAiAtomicOfflineTests.cs`、`ServerAiReadProtocolTests.cs`、`ServerAiNativeActionOfflineTests.cs`、`run_server_ai_ipc_tests.ps1` | 超容量运行、重复 ID/回收/Forget、查询异常分类、取消与全部原生终态竞争矩阵 |
| `CompanionBridge/build.txt`、`ServerAiPackageTests.cs` | 隔离正式包版本 0.1.74 及包身份核验；原 86 项 IL 检查保留 |
| `docs/ATOMIC_OPERATIONS.md`、`docs/SERVER_AI_ACTIONS.md` | 更新当前参数、受理/终态、历史保留和旧包兼容语义入口 |

## 关键决策与必要调整

1. **复用能力映射，不新建注册系统。** Server AI 仍仅开放 `execute_lua`，最终 API 来自 handshake 投影、Bridge 白名单和已描述 API 的交集。扩展原能力组合测试，将库存与任务两个已实现的可选旗标纳入，共 4096 种组合。发现 Manifest 漏列已有 `mine_target`，以及库存旗标已打开时仍写“不可搬运”的文案；仅修正文档投影，不新增能力。
2. **正面受理证据，不能猜 reason。** 只有实际 C# `Handle` 在创建 Entry 前返回的明确拒绝带 `dispatchState=not_admitted`，Python 再核对绑定才提升为确定拒绝。原子分发记录 `admitted`，失联或回执身份丢失保持 unknown。旧服务器没有标记时不根据 `action_busy` 等文字推断安全。`duplicate_action` 仅表示本次请求没有新受理，不否认原动作可能已经产生效果。
3. **保留双重校验与实际 wire 规范化。** Python 的整数值浮点仍无损规范化成 JSON 整数；共享向量显式保存这类案例的 `wireArguments`，C# 独立验证真正发出的整数 JSON。分数、bool、越界值不会被取整。没有为了 C# `GetInt32` 而重写执行器或扩大后台任务体系。文本 maxLength 对齐 Unicode 标量，不让 UTF-16 代理对计为两个字符；没有改变世界/Actor 身份检查或引擎版本边界。
4. **终态按事实，触发原因作元数据。** 原生 Cancel 返回可信 completed/failed/timeout/cancelled 时使用其结果；completed 还需 EffectVerified，所有可靠终态都需 InputsReleased。unknown、不释放或仍 running 均为 unknown。无 Handle 的受理阶段停止沿用原安全取消。`stopRequest` 独立记录请求 status/reason，不能覆盖真实失败/成功。源码核对还发现 Python 监督截止分支同样会强改 timeout，已同步保留实际 stop 终态，用 `supervisorStopRequest` 单独记录宿主超时原因；新增五种终态竞争回归。已有 Python operation-specific verifier 保持原门槛。
5. **1024 个全局身份的计数窗口。** 只在新 GUID 已通过受理检查后，为其腾出空间，按结束顺序回收最旧终态。活动 Entry 从不进入回收队列，Forget 仅请求取消；保留窗口内重复 ID 拒绝，回收后查询 unknown_action。每次新请求使用新 GUID，不保证永久历史或跨进程恢复。
6. **明确拒绝不自动重试。** 当前 Lua 程序依旧被终止；只有既有宿主验证的 continuation 规则可续行，reason 字符串和脚本自报均不能授权。小量 Runtime/AgentRun 改动只转交 API 边界证据，没有 M1 循环重构或 M3 新调度。

## 实际定向验证

使用现有 `.local/lua-venv`。没有运行全量测试。涉及 localhost 的测试在沙箱外执行；沙箱首次拒绝 Server AI 监听，不是产品失败。

```powershell
$env:PYTHONPATH='python;tests'
& .local/lua-venv/Scripts/python.exe -B -m unittest test_api_contract_mvp test_server_ai_contract test_atomic_operations -q
& .local/lua-venv/Scripts/python.exe -B -m unittest test_server_ai_backend -q
& .local/lua-venv/Scripts/python.exe -B -m unittest test_lua_runtime test_lua_agent test_atomic_result_contract test_agent_action_interruption -q
& .local/lua-venv/Scripts/python.exe -B -m unittest test_api_contract_ipc -q
pwsh -NoProfile -File tests/run_api_contract_tests.ps1 -OutputPath .local/m2-csharp-contract.json
pwsh -NoProfile -File scripts/build-companion.ps1 -OutputPath .local/m2-build
pwsh -NoProfile -File tests/run_server_ai_ipc_tests.ps1 -PackagePath .local/m2-build/Mods/CompanionBridge.tmod
```

| 范围 | 实际结果 |
| --- | --- |
| 共享参数 | 202 个向量 Python/C# 一致；覆盖 Host/native 集合、设计列出的八组跨字段约束、后端 use_item 收窄、整数值浮点规范化和 Unicode/null |
| 模型能力 | 4096 组映射/Manifest/definitions/示例/引用检查通过；客户端过滤、旧无能力信息兼容、未知 API 及全局定义不被修改通过 |
| Backend | 40 项：39 项首轮通过；唯一 0.1 秒 socket 测试未到 start 就超时，单独复测通过。新增可信拒绝、无证明/错绑定 unknown、拒绝等待中取消和后端 target 预校验均通过 |
| Lua/结果/中断 | 79 项运行完成，仅旧结果契约中的 2 个方法产生 6 条失败记录（含子测试）；LuaRuntime、Lua Agent、直接动作中断本轮无新增失败 |
| 基线对照 | `test_atomic_result_contract` 的 15 项在 b6c2e46 独立快照中复跑，6 条失败名称与当前完全相同；4 个相关源码/测试文件逐字节核对为基线。它们仍期待旧 M1 的 completed Run 终态和原样回复；未为 M2 改动 M1 行为或放宽旧断言 |
| 真实跨层 | 实际 C# 主机 → Python Bridge/dispatcher → Lua Worker，拒绝不隔离、不续行动作，下一程序仍可查询；参数类型错误为 invalid_request，真正读取故障为 engine_unavailable，通过 |
| 独立暂存快照 | 排除工作区原有未暂存修改后，69 项 M2/能力/原子/Lua/直接中断定向测试通过；最终监督超时修正后补跑停止契约与 Backend deadline unknown 共 10 项通过，包含五种终态竞争子案例。旧 deadline 测试的 confirmed 预期同步为真实 cancelled，超时触发原因另行断言 |
| C# Server AI 定向组 | IPC 45、原子 47、读取 68、原生适配 287、任务 48、聊天 30 项全部通过。包含 1300 次连续完成、活动记录/重复 ID/回收/Forget，以及 Cancel/timeout × 原生六种状态 × 两种释放状态的竞争矩阵 |
| 游戏包 | 0.1.74 隔离构建 0 错误、0 警告；86 项包 IL 检查通过。SHA-256：`1453e2574c644192bdb1eb8c2bc26b69361a513019f1c64550b085a61ef3273d` |

首轮共享向量把两个 `number` 字段的合法小数错标为拒绝，修正测试预期后通过；扩展 capability 旗标后，旧“库存能力缺失”的测试前提需要显式去掉库存旗标，修正后对应测试通过。未为测试改变生产范围、动作公差或 unknown 门槛。

本地日志：`.local/m2-contract*.log`、`m2-backend.log`、`m2-lua.log`、`m2-result-baseline.log`、`m2-ipc-python.log`、`m2-ipc-package.log`、`m2-build-final.log`、`m2-staged.log`、`m2-staged-supervisor-final.log`。它们区分首次失败、基线、修正与复验，没有改写原始游戏证据。

## 未解决问题与验证限制

- 上述 6 条 M1 旧结果断言仍未解决；超短 socket 测试在高并发/调试负载下存在时序敏感性，单独复测通过不代表永不抖动。
- 新受理拒绝标记需要服务器加载新包；旧包仍保守 unknown，不自动升级、重连或重放。
- 1024 是记录保留窗口，不是永久幂等存储。回收后的旧 ID 不保证重复提交拒绝，因此调用方必须使用每次新生成的 GUID；没有跨进程恢复。
- 共享矩阵证明所列边界和实际 wire 的一致性，不声称穷举所有 JSON 输入、Mod 内容或原生环境。
- C# TCP 主机与执行器/Actor 是离线替身；没有图形 Host & Play、真实付费模型、远端多人、原生世界中 1300 动作或第三方 Mod 验收。新包仅构建，没有部署现用包/角色/世界。
- M3 Program/Task Runtime、后台 API、并行抢占/恢复、导航/玩法算法和通用插件系统均未实施。M2 完成后暂停，等待架构审查。
