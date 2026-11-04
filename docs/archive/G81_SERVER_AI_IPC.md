> **历史阶段资料**：本文记录特定开发阶段的实现或测试，不是当前能力契约。当前实现以源码、运行时能力握手及 `docs/INDEX.md` 中的专题文档为准。本文中的版本、状态、命令和限制均属于原记录时点。

# G8.1 CompanionBridge server AI: read-only IPC

基线：`main` / `2903551`。正式包版本 `0.1.41`。不修改 Lua Runtime、
atomic_operations、Agent Loop、旧 TCP 协议或动作证据契约。

## 实现与边界

`CompanionBridge/ServerPlayerRuntime` 是从 G1–G7 原位置提升的同一份运行时源码，
保留旧 namespace，使原生诊断、身份/生命周期测试继续审计相同符号。
`EngineHooks` 改为由 Mod 持有的运行时；正式入口为 `CompanionBridge`，实验入口为
`ProbeMod`。实验构建脚本暂存共享源码后构建独立探针，无两份挖掘/同步实现。

正式包仅在原生服务器进程、显式启用后安装既有严格引擎钩子。仍要求
Terraria 1.4.4.9 / tML 2026.8.3.0 的确切 DLL 指纹，且仅启用 CompanionBridge
与 tModLoader。不支持同时加载实验包；不放宽其他 Mod 的隔离限制。
原有 reservation、Gate、对象引用校验、死亡处理、quarantine 和卸载前保险保持有效。
正式图形客户端继续使用原有真人 TCP（默认 18777）、输入与按键路径。
实验命令 `/vph`、`/vpc`、`/vptarget`、`/vpmine` 和选框在正式包中禁用。
正式真人存档不写入实验持久化 marker。

`ServerIpc/ServerAiSystem.cs` 在世界加载回调仅记录实例：本机原生
`OnWorldLoad` 实际运行于后台线程。监听器与 AI 必须等待原生 `StartServer`
返回，在 `Program.MainThread` 上创建。每个原生服务器进程只自动尝试创建一次新 AI；
不会自动读取/覆盖旧 AI 档案，也不在对象丢失后补建。

`AiReadOnlyServer.cs` 的 TCP 工作线程没有 Terraria 引用，只排队文本请求。
游戏线程在 Gate 内调用 `AiReadOnlyProtocol.cs`，再由 `NetworkStateSync.Require`
核对当前 Player 对象、代次、世界、预留槽位与空闲原生连接，复制状态。
对象替换触发既有隔离；不会退回 Main.LocalPlayer，不修改 Main.myPlayer。
断开、取消、请求过期与卸载丢弃待处理请求，卸载清除连接绑定并关闭监听器。

无真人连接时，原生专用服务器暂停 Game.Update。已核对本机 IL：服务器循环仍在
原生主线程调用 `Main.OnTickForThirdPartySoftwareOnly`；该回调只服务只读查询，
不伪造客户端、不改变 HasClients、不手动调用 Player.Update。此时 tick/位置可保持不变。

## 启动

构建与准备只写入 `.local` 隔离目录，不启动图形游戏，不更新旧 profile：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-companion.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/prepare-companion-host-play.ps1 -SeedProfile .local/virtual-player-single-window-host
```

准备脚本打印新的 `start-host-play.ps1`。手动运行它，选择测试角色/世界的
Multiplayer > Host & Play。可不提供 SeedProfile，自行创建测试角色/世界。
启动器设置 `COMPANION_SERVER_AI=1`、`COMPANION_AI_PORT=18779`；原生服务器子进程
继承环境，图形客户端仍仅运行原有真人 TCP。启动器校验包哈希与启用的 Mod。
旧的实验启动器仍使用实验包，不会提供正式 IPC。

原生专用服务器也可指定 `-companionserverai -companionaiport 18779`。
不启用时不安装 AI 引擎钩子、不创建 AI、不启动新端口。
AI 端口须与 `-companionbridgeport` 不同；端口占用时拒绝启动服务。

```powershell
python scripts/ai_player_state.py
python scripts/ai_player_state.py --port 18779
```

## IPC v1

只监听 `127.0.0.1:18779`，UTF-8 JSON Lines，每个响应一行。
默认真人 TCP 18777 与游戏服务器端口 7777 不变。每个 TCP 连接先绑定一次：

```json
{"v":1,"id":"b1","op":"bind"}
```

成功响应形状：

```json
{"v":1,"id":"b1","ok":true,"binding":{"actor":"opaque-object-id","world":"world-session-id","generation":1,"slot":254},"data":null,"error":null}
```

`bind` 是唯一的身份发现入口；状态查询必须回传完整 binding，且使用同一 TCP 连接：

```json
{"v":1,"id":"s1","op":"state","binding":{"actor":"opaque-object-id","world":"world-session-id","generation":1,"slot":254}}
```

`data` 包含 `tick/name/active/dead/ghost/position/velocity/life/lifeMax/selectedSlot/selectedItem/inventory`。
位置、速度采用原生像素单位；背包 slot 与 selectedSlot 均从 0 开始。
背包逐项提供 slot、type、stack、prefix、name；只含 inventory，不含盔甲/箱子。
每份状态均在同一个游戏线程处理点复制；actor ID 是对象身份的不透明标识，slot 不是身份。
角色复活代次、对象或世界改变后旧绑定失效；需要新连接重新 bind，不能原连接静默换人。
协议先验证连接 lease：重复 bind、未绑定的 state、与连接 binding 不一致的 state
均在调用引擎 capture 前拒绝；通过后仍二次核对当前 Actor 引用、世界、代次与槽位。

错误响应 `ok:false`，`error` 为 `invalid_request`、`unsupported_operation`、
`identity_unavailable`、`engine_unavailable`、`not_bound`、`already_bound` 或 `stale_binding`。
未知字段、重复字段、缺失绑定、错误协议版本被拒绝。没有动作/背包修改/退出服务命令。
连接断开、非法 UTF-8、超过 4096 字节行、队列溢出或超时会关闭连接。
最多 4 个连接、64 个全局排队请求、每连接响应队列容量 16、每游戏线程处理点最多 16 个请求；
排队请求 4 秒失效，响应等待上限 5 秒。端点为本机开发查询，不含远程认证或加密。

## 验证（2026-10-26）

未运行全量测试。下表保留初次实现的历史回归结果；本次审计修订重跑正式包构建、
IPC、正式包 IL、日志证据过滤测试及隔离原生 G8.1，不重复运行未修改的 G1–G7 回归。

| 验证 | 结果与证据性质 |
| --- | --- |
| 正式包与实验包构建 | 初次实现均 0 errors / 0 warnings；本次正式包重建亦 0 errors / 0 warnings |
| G8.1 IPC | 本次 45 项离线替身/真实 loopback 检查通过；新增拒绝请求前 capture 计数不变，以及实际替身对象/世界/代次/槽位变化后的二次校验 |
| 正式包 IL 审计 | 19 项通过；实际构建 DLL，含 TCP 零 Terraria 访问、后台世界加载不创建监听器/角色、原生启动顺序、正式真人存档 marker 保护 |
| 现有 BridgeOnly（初次实现） | 真人 TCP、Player hook、输入归属及死亡/重生回归通过；离线替身与真实 loopback，非图形验证 |
| 共享运行时 MiningOnly（初次实现） | 266 项通过；本机真实 DLL/IL、编译后的控制/生命周期替身、73 项 G2 含并发锁顺序回归；非图形验证 |
| 原生 G7 Mining（初次实现） | 37 项通过；真实 ItemCheck/PickTile、进度、破坏、掉落与发包，石块与草皮泥土；启动阶段显式驱动，非实时帧节奏 |
| 原生日志证据过滤 | 混入其他进程、其他世界、PID 复用但运行时间外的事件，不能使测试通过；本次事件被接受；6 项不完整/不安全 teardown 反例均拒绝 |
| 正式原生 G8.1 | 本次隔离复制世界中真实 native server；实际 TCP 与 Python 查询、请求 binding 不匹配/动作拒绝、正常退出与本次进程的角色清理及 reservation 释放通过 |
| 图形 Host & Play / 远程多人 | 本轮均未执行，不计为通过 |

本轮首次原生运行暴露了后台世界加载时序：提前开放监听器会使启动中的请求超时。
已改为原生 StartServer 完成后创建服务并重测通过。一次退出验证亦因重定向控制台未能
触发原生退出而失败；最终验证采用显式 `-companionaiipctest` 诊断开关，测试脚本
在隔离 SavePath 写入 `g81-test-stop`，由原生主线程请求正常 Disconnect；该测试开关
不属于 IPC 协议，也不会在普通启动器中启用。

最终原生证据与包哈希见 [G81_NATIVE_EVIDENCE.json](G81_NATIVE_EVIDENCE.json)。
原生 binding 不匹配测试仅将请求 generation 改为当前值加一，未引发实际角色代次变化；
不属于原生旧代次生命周期测试。真实对象、世界、代次和槽位变化的拒绝由离线替身验证，
真实原生死亡/重生代次变化仍未执行，生产安全约束未放宽。
日志检查同时匹配本次 Process ID、绑定 WorldSession 与进程起止 UTC；
仅在这些事件内核验 spawn、角色清理与 reservation 释放。匹配事件保存为对应
`.local/companion-g81-native-*/server.log`，不以共享安装日志中的历史 teardown 判定通过；
本进程 stdout/stderr 同样保存在该隔离目录。正式世界未修改。
本轮新准备的 Host & Play 入口仅准备，未运行。历史用户确认的 G7 挖掘不能替代 G8.1 图形验收。

定向重跑命令（测试编译使用 PowerShell 7；本机可用 Codex bundled pwsh.exe）：

```powershell
pwsh -NoProfile -File tests/run_server_ai_ipc_tests.ps1 -PackagePath .local/companion-g81-build/Mods/CompanionBridge.tmod
pwsh -NoProfile -File tests/run_server_ai_native_evidence_tests.ps1
pwsh -NoProfile -File tests/run_csharp_tests.ps1 -BridgeOnly
pwsh -NoProfile -File experiments/virtual-player-engine-hooks/verify.ps1 -MiningOnly
powershell -NoProfile -File experiments/virtual-player-engine-hooks/verify-native.ps1 -Suite Mining
pwsh -NoProfile -File tests/verify_server_ai_native.ps1
git diff --check
```

Host & Play 人工验收：查询 AI 与真人分别位于不同槽位；真人移动/聊天和旧 TCP 保持正常；
观察 AI 的原生更新与死亡代次变化；退出世界时旧连接不可继续读取；新开原生服务器后
旧绑定必须拒绝。图形可见性、真人在线时的持续更新、AI 死亡重生、网络重连、
quarantine 的实时注入及多 Mod 组合仍未现场验证。AI 当前只创建临时新角色，不自动
加载/保存历史 AI 档案；挖掘、物品使用、Lua 适配与完整原子操作迁移留待后续阶段。
