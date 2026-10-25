# Issue #10：当前版本的无客户端 Player 引擎钩子实验

2026-10-25。关联 [Issue #10](https://github.com/729194587/terraria-ai-companion/issues/10)；调查基线为未合并的 [Draft PR #9](https://github.com/729194587/terraria-ai-companion/pull/9) 及其 [报告](https://github.com/729194587/terraria-ai-companion/blob/731ee1123091f4a67fa7578c4f6e9b9bc9a69710/docs/VIRTUAL_PLAYER_SPIKE.md)。

**结果：G0 = passed in offline tests；G1 live = partial（0.1.4 单 host 存活及退出清理已有实测，第二真人加入/退出待验）。** 0.1.2 的真实启动因预留判定误报而 blocked；0.1.3 修正监听判定。0.1.4 已在 Host & Play 创建无客户端的真实 Player，持续原生更新并保持引用；用户确认真人控制及手动 despawn 符合预期，随后另一服务器会话记录了带 actor 退世界时的 OnWorldUnload 清理。第二真人暂无法参与，不将多人后续分配标为通过。G2/G3、移动命令、Agent、Lua、档案、物品使用和战斗均未实施。

分支 `spike/virtual-player-engine-hooks` 从 `main` 的 `9a57b3bae8d888f79c2da6c41cbc5b5ec66085c6` 开始，只新增本报告和 `experiments/virtual-player-engine-hooks/`。main、CompanionBridge、Python、Lua、TCP、原子操作和 PR #9 均保持原样。

## G1 实现补齐（当前 Probe 0.1.4）

直接参考 `D:\Agent Project\terrabot\TerraPlayerBot\BotConnectionSlots.cs` 的 `Gate`、`Accept`、`Lease.Publish`，以及 `TerraPlayerBotSystem.cs` 的 `PatchSyntheticPlayerDisconnect` / `IsSyntheticBotSlot`。只沿用槽位事务及对象归属思路，保留本 Probe 的监听前固定预留、严格连接空闲判定和一轮生命周期设计。

- `OnConnectionAccepted` 的整个原生选槽、Reset、Socket 赋值和满服处理，与游戏线程的验证/构造/发布/回滚、整个原生 `UpdateConnectedClients` 共用 `Reservation.Gate`。因此不会在 predicate 返回后、原生活动态写入前插入 spawn，也不会在 accept 事务中途释放预留。锁内不排队等待游戏线程；线程断言在生命周期取得锁前执行。分配器仍只读不可变预留。
- 断线 IL 保护从整个槽位入口移到原生 `StatusText2=""` 后的 `IL_00ab`，跳至原生递增点 `IL_00d4`。只有预留匹配、当前元素与 `Actor` 引用相同、`whoAmI` 匹配且完整连接状态 idle 时，才跳过 `active=false` / `PlayerDisconnect`。待终止审批、连接 Update、断线过渡处理和 `HasClients` 完全保留。仅有预留、尚无 actor 时，不拦截原生 Player 清理。
- `new Player` 的候选对象先保持私有；构造及 ModPlayer 初始化完成后，在同一 Gate 内重新核对原预留、容量、原 inactive 占位引用和连接状态，再发布到 `Main.player` 并仅一次设 active。`PlayerConnect` 返回后再验对象归属。重复 spawn 也重新校验对象/连接，创建和清理拒绝回调重入。
- 构造失败但占位仍已知安全时，释放未使用预留；发布后的连接回调失败按同一 teardown 回滚。构造或回调替换对象、激活恢复占位、改变身份/连接过渡态时，保留预留并 quarantine。断开通知最多一次，且通知返回/抛错后再次验证对象、活动态和连接状态，确认安全才恢复原占位并释放；日志异常仍通过 finally 释放已确认安全的槽位。
- 同步/区块隔离也要求真实 AI 引用归属；非预留真人槽位直接调用原函数。Quarantine 后继续提供每秒诊断快照，但不重试 spawn/清理。重新 Load 清空上轮已正常结束的诊断状态。

当前版本编译零错误、零警告；164 项定向检查通过（59 项原生检查）。新增用例执行实际包内的 `Spawn`、`CheckInitialSlot`、`Finish`、AI 归属谓词、accept/network 包装 IL，仅替换引擎存储及外部效果。验证内容包含构造抛错/替换后抛错、发布后连接失败、回调中替换 Player/激活占位/改身份/再激活 actor/置待终止、重入、错误线程，以及构造过程中并发 accept 被 Gate 阻止。网络包装还在 orig 前检测待终止过渡态，保证未到达窄 IL 保护点的路径也能 quarantine，同时仍调用原生处理。离线验证不运行游戏入口，未运行全量测试；后续用户手工启动的实测记录如下。

## 0.1.4 单 host 实测记录

2026-10-25（以下时间为 UTC+8），使用本报告的隔离 profile 和 0.1.4 原包，未修改代码或重新构建。第一次启用 HEROsMod 的会话在 Mod.Load 触发隔离检查，全部槽位钩子未安装、未创建 actor；禁用 HEROsMod 并重新开服后正常进入。未通过放宽共存限制绕过该检查。

| 项目 | 实际证据与结果 |
| --- | --- |
| 监听前预留及 host 接受 | 17:38:38.816 的 pre-listen-reservation 为 armed=true，预留 slot 254；17:38:38.873 首次监听 entry=1/reserved=true；native-accept 将真人分配至 slot 0 |
| 无客户端 Player 存活 | 17:39:36.316 spawn 于 slot 254；到 17:40:45.975 已持续约 69.66 秒，Pre/PostUpdates=4178、cleanupSkips=351960；active=true、dead=false、sameReference=true、connected=false、Socket=null、连接/终止状态均 idle、fault=null |
| 真人控制及手动 despawn | 用户执行移动/跳跃及 despawn/status 步骤后确认符合预期；报告为用户确认。保留的首轮日志快照截至上述存活样本，不冒充独立保存了手动 despawn 后的 status |
| 带 actor 正常退出世界 | 新服务器 PID 24160：17:45:41.518 spawn；17:45:49.919 原生 host 离开并触发自动关闭，随后正常保存世界；17:45:51.630 记录 teardown，reason=OnWorldUnload、same=true、occupied=false、actor=null、actorSlot=-1、fault=null。该会话恰有一次 spawn 和一次 teardown，全部 16 个 probe 事件均无 fault/quarantine；采集时该服务器 PID 已不存在 |
| 预留释放证据边界 | teardown 记录位于恢复占位之后、finally 的 Reservation.Release 之前，因此该行仍包含 reservation。实际代码在日志后立即执行 Release，未见异常且服务器进程已退出，支持正常退出路径完成；本轮没有释放后的独立运行时快照，不把该日志中的 reservation 误记为 null |
| 第二真人后续加入/退出 | 用户当前无第二真人参与，保留为未验证；离线分配/断开夹具不替代此项 |

本地证据目录（不提交个人日志）：`.local/engine-g1-014-blocked-20261025-173444/`、`.local/engine-g1-live-spawn-20261025-174046/`、`.local/engine-g1-live-world-exit-20261025-174700/`。最后一个目录包含客户端/服务器日志、提取的 probe-events.jsonl 和 summary.json。世界日志记录 GameMode=0（Classic）、6400×1800；不是步骤建议的小型世界，未观察到因此影响本轮槽位/生命周期结果。G1 整体保留 partial，Draft PR 不自动转正或合并。

## PR #11 早期审查修正（0.1.2 / 0.1.3）

针对 [三项审查意见](https://github.com/729194587/terraria-ai-companion/pull/11#issuecomment-6393766604)，补齐了实际 Host & Play 路径和线程证据，并修复清理时连接过渡态保护弱于网络保护的问题。0.1.2 继续修复 `5791b74` 中未 spawn 时的 Player 归属缺口，0.1.3 修复真实开服暴露的监听状态误判；不要部署旧的 Probe 0.1.0/0.1.1/0.1.2。

**当前安装版本的 Host & Play 是双进程机制：一个真人图形客户端，加一个原生无图形服务器子进程。** 前两项意见需要结合这一区别理解：图形进程的 `dedServ=false` 不意味着整个 Host & Play 没有 `dedServ=true` 的服务器。不会把 actor 移到真人图形客户端；图形端现在安装启动观察钩子，服务器子进程安装生命周期钩子。

以下路径均已检查当前 DLL 的完整 IL，并添加定向结构断言，不是依据模式名称推测：

| 进程/阶段 | 当前 DLL 的路径 |
| --- | --- |
| 图形窗口提交 Host & Play 密码 | `Main.OnSubmitServerPassword()` 在 `IL_013f` 设置 `tModLoader.dll -server ...`，含 `-autoshutdown`，传递 `-modpath` 和独立存档目录；随后启动该 Process，再 `StartTcpClient` 连接 host |
| Steam 路径 | `Steam.NetClientSocialModule.LaunchLocalServer` 在已有 Arguments 后追加 Steam/lobby 参数，最终对该 Process 调用 Start；不替换为同进程服务器 |
| 子进程参数分派 | `Program.ProcessLaunchArgs` 识别 `-server`；`LaunchGame_` 设置 Main.dedServ；`RunGame` 根据它进入 `Main.DedServ()`，而图形客户端进入 Game.Run |
| 子进程加载 Probe | `DedServ` 先设 dedServ=true，再调用 `ModLoader.Reload`；Reload 的服务器分支同步执行 Load，然后进入 `DedServ_PostModLoad`，所以生命周期钩子已安装 |
| 模拟线程 | `Program.LaunchGame` 先将当前 Thread 记录为 Program.MainThread；该调用链同步进入 DedServ/scope/服务器循环/Game.Update；Main.Update 再同步执行 ConsumeAllMainThreadActions |

运行时不只信任捕获的线程 ID：服务器 scope 和 StartServer 同时要求 `Main.dedServ && Program.IsMainThread`；spawn、清理与更新检查还要求捕获的 owner ID 一致。日志增加 `role`、`programMainThread` 和 `programIsMainThread`。0.1.2 的真实日志已观察到图形启动、服务器 Load/scope 和线程身份，未证明 actor 存活；0.1.3 live 仍为 not attempted。

第三项已改为统一 `ConnectionIdle` 谓词：必须已知 RemoteClient，且 `!IsConnected && !IsActive && State==0 && !PendingTermination && !PendingTerminationApproved`。初始槽位验证、网络 eligibility、同步/区块隔离及 teardown 共用该策略。任何过渡态、未知连接对象或 Player 引用冲突都进入 quarantine，保留 actor/占位/预留，不修改 Player、不发断开通知、不释放槽位；重复清理与 spawn 被拒绝。pre-clearWorld 因而中止，而非带着未知连接状态继续清世界/撤销钩子。

0.1.2 额外要求 **Player 对象归属可证实**，即使 `Actor == null` 且 RemoteClient 完全 idle。原生初始化后，首次成功的 CheckInitialSlot 仅对非 null、inactive Player 建立一次 `(slot, previous)` 占位基线；spawn 前重验不得采纳替换对象，Spawn 也不再重记 previous。未 spawn 的 teardown 仅允许该已知、引用不变、仍 inactive 的占位对象释放预留；active Player、inactive 替换对象、null 引用、缺失基线或槽位不匹配均 quarantine。已有 actor 时还要求当前元素就是 actor，且待恢复占位非 null/inactive。StartServer 在基线建立前异常退出同样无法证明归属，故保留预留并停止，不冒险释放。

### 首轮真实启动阻塞与 0.1.3 修正

2026-10-25 13:42–13:48（UTC+8），用户运行 `51c4ccc` / Probe 0.1.2，使用独立 profile、新角色/世界并进入 Host & Play。图形 `graphical-host-launch` 的 PID 为 19820；服务器 PID 为 11708，钩子已安装，scope 和 StartServer 均在 thread=programMainThread=4、programIsMainThread=true 上执行。原生接受 host 到 slot 0。尚无父子 PID 枚举证据，不把相关日志当作完整进程树验证。

最小复现：用 0.1.2 新进程 Host & Play → 查看 `pre-listen-reservation` → `detail.armed=false`、`reservation=null`、fault=`Reservation could not arm before listening; fresh Host & Play server process required.`。未执行 spawn，用户随后正常离开并关闭游戏。该轮 world GameMode=3（Journey），偏离计划的 Classic；错误发生在 actor 创建前，不能拿这次运行当作 G1 验收。

原因为 Probe 把 `Netplay.IsListening` 当成实际启动状态。当前 DLL 的 `Netplay..cctor` 在 `IL_0041 ldc.i4.1` / `IL_0042 stsfld IsListening` 默认置 true，彼时 TcpListener 仍为 CLR 默认 null。整个模块只有 InitializeServer 写 TcpListener：`IL_00b1 new TcpSocket`、`IL_00b6 stsfld TcpListener`，然后 `IL_00c2 StartListening`；后者同时处理 Social/TCP。原先模型测试直接传入 false，没有执行包内 StartServer，遗漏了真实初始状态。

0.1.3 不改写 IsListening、TcpListener 或连接字段。StartServer 原函数之前仍发布一次不可变预留，但过晚判定改为 **TcpListener 已存在、监听入口曾调用、accept 曾发生或网络循环已运行** 任一成立即拒绝。新增 On_Netplay.StartListening，在 orig 之前原子计数并记录 `native-listener-entry`；第一次进入而没有预留则禁用 spawn。原生监听和重试的调用及返回值均保留，正常 despawn 释放后再次原生监听不会因此制造新 fault。此设计仍依赖固定版本的调用图和仅 Probe 共存限制，不能覆盖未知 Mod 的外部监听行为。

本地证据：`.local/engine-g1-live-20261025-134210/evidence-startup-20261025-134615/`，含 client/server 日志、probe-events.jsonl、startup-observation.json 和相关完整 IL，不提交用户日志。旧 profile 和证据保留，不在旧会话重试。新构建需用户手动启动新隔离会话；当前 G1 仍未通过。

## 实验决策与范围

采用**监听开始前固定预留、每个服务器进程仅一轮 actor 生命周期**。0.1.4 增加同步 Gate 协调接受连接、网络清理和生命周期写入；不引入动态抢槽或跨线程确认协议。

- 在原生 `StartServer` 之前发布不可变预留，选 `Main.maxNetPlayers - 1`，要求配置容量在 2..255。不会使用 255 哨兵，也不会占 slot 0。
- 所有本机原生接受连接的选槽调用都会经过已修改的分配器；没有先选中候选、再临时发布预留的窗口。
- 预留会暂时减少一个真人可用连接槽位，**即使尚未执行 spawn**。
- 只有游戏/服务器模拟线程可以创建、替换或清理 actor。网络线程在 Gate 内读取预留、AI 引用及连接状态，只修改诊断元数据；原生网络逻辑保留对真人对象的处理。
- despawn 先拆除 actor，再释放预留。本进程不重新 arm；重复 spawn 在 actor 存在时幂等，despawn 后需完全退出 Host & Play 的服务器进程，再开始新的测试会话。
- 仅允许此 Probe 与 tModLoader 默认 Mod 共存。安装其他 Mod（包括 CompanionBridge 或 PR #9 的 Probe）会禁用本实验，避免未调查的重置/分配/断开钩子。

这是固定版本的窄适配器，不是多版本兼容框架，也不是完整的网络玩家实现。G1 阶段只对保留槽位隔离原生离线同步与区块发送，不为 actor 建立客户端可见性。

## 精确版本与证据

| 项目 | 核查值 |
| --- | --- |
| Terraria | 1.4.4.9 |
| tML | 2026.08.3.0；API/包头规范化为 2026.8.3.0 |
| 引擎提交 | `c9545cf27d56c456321e1896274c02adf67d0140` |
| tModLoader.dll SHA256 | `FCC6A9624B12191BE4A15D714A3675440911FEB8C1D374642AB1FD686F0DA0A5` |
| TerrariaHooks.dll SHA256 | `595D26FB3BBE27548D030943B5ED2437F690D1C002129BAD21FAFEAEB8118F7A` |
| 安装路径 | `D:\steam\steamapps\common\tModLoader` |
| 运行时/钩子库 | 游戏随附 .NET 8.0.0、Mono.Cecil 0.11.6、MonoMod.Utils 25.0.10、RuntimeDetour 25.3.2、Core 1.3.2 |

运行时在安装任何钩子前同时核对 Terraria 程序集版本、tML 版本/提交和两个程序集指纹。不匹配则不安装，spawn 无法获得预留。IL 形态不符或部分安装失败时撤销已安装订阅，不提供不安全后备路径。更新 tML 后必须重新调查。

`verify.ps1` 从当前 DLL 导出相关**完整方法 IL**到 `.local/virtual-player-engine-validation/`，包括分配、接受连接、初始化、网络循环、Reset、同步、区块发送、退出与 Mod 卸载。游戏 DLL 与完整反编译内容不提交。

当前 DLL 中的重要顺序：

| 方法 | 关键位置/事实 |
| --- | --- |
| `Netplay.InitializeServer` | `IL_00c2` 已经 `StartListening`，因此在该方法返回后预留会太晚 |
| `Netplay.StartServer` | 先初始化并开始监听，随后在 `IL_0041` 启动 `Server Loop Thread` |
| `Netplay..cctor` / `StartListening` | IsListening 默认 true；TcpListener 默认 null，初始化时创建；StartListening 同时调用 Social/TCP 实现，默认标志不是实际监听证据 |
| `FindNextOpenClientSlot` | 循环体 `IL_0004`，递增点 `IL_0014`；按连接状态选槽 |
| `OnConnectionAccepted` | `IL_001a` 选槽；`IL_002b` Reset；`IL_0038` 赋 Socket；`IL_0070` 再检查可用槽位 |
| `UpdateConnectedClients` | 每槽位入口 `IL_0009`；保护插在断线非活动分支 `StatusText2` 写入后的 `IL_00ab`；递增点 `IL_00d4`；原生停用位于 `IL_00c7` |
| `SyncOnePlayer` | State 非 10 时进入离线路径，最终还会调用 `Player.Hooks.PlayerDisconnect` |
| `Main.UpdateServer` | `IL_01e6..IL_0205` 给活动 Player 调用 `CheckSection`，不要求它有连接 |
| `RemoteClient.CheckSection` | 会更新区块/接收状态；无客户端 actor 不应走这条发送路径 |
| `Main.DedServ_PostModLoad` | StartServer、服务器模拟循环和正常 OnWorldUnload 位于同一原生调用范围 |
| `ModLoader.Mods_Unload` | 先 `WorldGen.clearWorld`，再 `ModContent.UnloadModContent` |
| `ModContent.UnloadModContent` | 先 `MonoModHooks.RemoveAll(mod)`，再 `mod.UnloadContent()`；只靠 Mod.Unload 清理 actor 已经太晚 |
| `WorldGen.SaveAndQuit` | 通过线程池进入保存/退出回调；不能默认每个 OnWorldUnload 都在模拟线程 |

另外，实际 MonoMod 操作上下文把分支操作数包装为 `ILLabel`，运行时 IL 克隆名称含 `类型::方法`。共享补丁同时处理这些真实形态；不是只在原始 Cecil 指令列表上测试后便推断运行时可用。

## 实际拦截点、线程与真人边界

服务器进程十个订阅，图形客户端一个启动观察订阅；其中服务器的清理方法分别有 IL 过滤和 On 计数。无连接状态伪造。

| 拦截 | 线程/频率 | 行为与回滚 |
| --- | --- | --- |
| `On_Main.OnSubmitServerPassword` | 图形进程的引擎主线程，每次 UI 启动 Host & Play | 仅记录 graphical-host-launch，然后调用原生启动/连接路径；不创建进程、不 arm、不创建 actor、不记录密码 |
| `IL_Netplay.FindNextOpenClientSlot` | 接受连接回调所在的 TCP/Steam 路径，每次原生选槽 | 循环体前增加 `ldloc; call IsReserved; brtrue next`；保留原生容量、连接检查及满服行为 |
| `IL_Netplay.UpdateConnectedClients` | 网络循环，断线非活动连接分支 | `StatusText2` 写入后增加三条指令；仅对预留/引用/身份及 idle 连接均确认的 AI，跳过 Player 停用/断开通知 |
| `On_Netplay.StartServer` | 服务器模拟线程，每次开服 | 仅首次且无实际启动证据、外层模拟 scope 已进入时 arm，然后调用 orig；IsListening 仅日志观察，重复开服不重新抢槽 |
| `On_Netplay.StartListening` | 初始化的模拟线程或原生监听重试的网络线程 | orig 前计数并记录预留；首调用无预留则 fault，仍原样调用 orig 并返回原值；不写 Player/世界或网络字段 |
| `On_Netplay.UpdateConnectedClients` | 网络循环 | 在 Gate 内完整调用 orig，完成后增加 networkLoops，排除与发布/回滚的交错 |
| `On_Netplay.OnConnectionAccepted` | TCP/Steam 接受线程，每次接受 | 在同一 Gate 内完整调用 orig，记录实际 Socket 归属槽位；原生选槽/Reset/满服处理保留 |
| `On_NetMessage.SyncOnePlayer` | 依据调用者可在游戏/网络路径 | 非预留槽位直接 orig；预留槽位在 Gate 内检查已确认 AI 引用及 idle 连接，满足才返回 |
| `On_RemoteClient.CheckSection` | 正常活动玩家区块检查路径 | 同上，仅隔离已确认 AI；不伪造 TileSections，不改变真人发送 |
| `On_Main.DedServ_PostModLoad` | 原生 Host & Play 子进程的模拟调用范围 | 校验引擎 Program.MainThread 并捕获 scope 线程；finally 在正常空闲状态下清理，连接异常则隔离并停止 |
| `On_WorldGen.clearWorld` | 依原生调用者而异 | 在任何 clearWorld 原生写入、RemoveAll 之前执行 teardown；有活动预留时若线程不符，直接抛错中止该不支持的清理流程 |

预留读视图用 `Volatile.Read/Write` 发布不可变 `(slot,capacity,session)`，计数用 Interlocked。Gate 是可重入 Monitor，原生 accept/网络 pass 与游戏线程发布/回滚互斥；锁内不等待主线程命令完成，生命周期用 creating/finishing 阻止回调重入。预留只会从一个固定槽位变为空，不会在服务器运行中扩展。

保护真人的规则：

1. AI 的活动态不参与 `HasClients` 计数；不会假扮 host，也不延长 Host & Play 的原生生命周期。
2. 如果归属谓词发现保留槽位出现 connected/IsActive/非零 State/待终止或终止已批准状态，立即 quarantine 并返回 false；原生连接更新/终止/Reset 分支不经过断线保护点，始终执行。未知 Player 引用同样不拦截原生清理；Probe 自身不写该 Player、不释放预留。
3. teardown 同时核对预留、初始占位基线、当前 Player 引用/活动状态和完整连接过渡态。Actor 尚不存在也必须证明当前对象是已知 inactive 占位；异常时不覆盖数组、不调用断开钩子、不将 active 清零，也不清空自己的引用或释放预留。
4. 所有写入先检查引擎 `Program.IsMainThread` 与 `AssertOwner`。图形客户端仅观察原生启动，不安装服务器槽位钩子，不替换 LocalPlayer，不修改任何 Main.myPlayer 或 RemoteClient 字段。

### 创建与清理

`/vph spawn` 在模拟线程重新验证指纹/钩子就绪、session、容量和槽位空闲，再在 Gate 内执行一次 `new Terraria.Player`，私有设置身份、基础生命、尺寸和初始空闲位置；构造完成后重验原预留/占位/连接，才放入专用 Main.player 元素，最后仅一次 `active=true`。原生构造器初始化 ModPlayer 实例；调用 `Player.Hooks.PlayerConnect` 完成原生压力板重置及连接通知，并在返回后再次校验归属。

不调用 `Player.Spawn`/EnterWorld：这些路径有本地 UI/所有者副作用。初始位置仅在世界出生点附近最多向上检查 12 格空位，不清理 Tile。后续完全由原生 Player.Update 更新，没有手动额外 Update、输入注入或逐帧坐标修补。自然重力产生的位置变化不算 G2 验证。

正常空闲且对象归属明确时，despawn 对自己的对象设为不活动，调用一次断开通知；通知返回或抛错后重验预留、actor/占位引用、身份/活动态和连接，仍安全才恢复原生初始化后首次验证的空闲占位对象，再发布空预留。未 spawn 时，只有该占位引用和 inactive 状态均保持才可释放。finally 覆盖断开钩子和日志异常；回调改变归属则停止恢复/释放并 quarantine。重复正常 despawn 无副作用。actor 死亡/不活动时停止，不自动复活。

正常 OnWorldUnload、clearWorld 入口和服务器模拟 scope 的 finally 都可幂等触发此清理。Mod.Unload 只验证早期清理已完成、移除订阅。部分安装失败则在任何服务器启动前逆序撤销订阅。

**不支持活动服务器上的后台/并发热卸载。** pre-clearWorld 保险会在错误线程上中止原生 clearWorld，从而防止接下来先撤销钩子、再留下 actor。若遇到此保险，保存异常和日志，按停止条件处理；不要绕过断言或重试 spawn。正常用户流程是先 despawn 或退出测试世界，再关闭/重载 Mod。强制结束进程不承诺 finally 执行；本实验无档案写入，也不修改安装 DLL。

**quarantine 是停止条件，不是成功清理。** Probe 停止后续动作和自动清理重试，但不冻结引擎、不吞真人原生清理；预留持续保留。该状态可能中止原生清世界/卸载并产生异常，须保存证据并结束这次隔离测试进程，不原地热重载、解除预留或重试 spawn。不能把这一分支记作 G1 通过。

### G1 同步隔离的限制

没有广播 actor 的身份/活动/出生/运动包，也没有修改 SyncOnePlayer 的真人路径。只阻止保留槽位被原生全量同步当作断开的真人；客户端可见性与晚加入的 actor 状态同步尚未实现，不以图形窗口出现新角色为本轮预期。

真人的 SyncConnectedPlayer/晚加入全量同步继续调用原函数；迭代到无连接 AI 槽位时仅隔离那一个槽位。任何需要更广泛吞包、绕过真人同步才能运行的现象都触发停止。G3 不在本次范围内。

## 构建和自动验证

从仓库根目录，以 PowerShell 7 执行；均支持 `-TModLoaderPath`：

```powershell
./experiments/virtual-player-engine-hooks/verify.ps1 -NativeOnly
./experiments/virtual-player-engine-hooks/build.ps1
./experiments/virtual-player-engine-hooks/verify.ps1
```

第一条可先验证原生 IL 与线程/槽位模型；第二条只编译打包；第三条追加实际包审计与包内清理代码验证。构建使用安装随附 dotnet/tModLoader 编译器，输出到 `.local/virtual-player-engine-build/Mods/VirtualPlayerEngineProbe.tmod`，不部署到测试或用户 profile。

验证器使用 tML 随附的 .NET 8 引用包和 .NET 8.0.0 运行时。PowerShell 的 Roslyn 仅编译检查程序；不会将本机 PowerShell 的 .NET 10 当成游戏运行时。没有下载依赖或启动 Terraria 程序入口。

最终本机输出：

```text
Compilation finished with 0 errors and 0 warnings
Packaging: VirtualPlayerEngineProbe
Verification CLR: 8.0.0; no Terraria game process or world.
EngineHookOfflineTests: 59 checks passed; actual IL fixtures only; G1 LIVE NOT RUN.
Engine probe package: loader=2026.8.3.0; bytes=35274; DLL=55296; 164 total checks; G1 LIVE NOT RUN.
```

验证范围：

- 两个固定 DLL 的指纹和已生成钩子、启动/监听/卸载顺序；遍历包含嵌套类型的整个安装模块，断言分配器、Reset 与监听入口的直接方法引用均在已调查路径中。
- 图形 Host & Play → 原生/Steam 子进程 → 参数分派 → 同步 Mod.Load → 模拟 scope → 原生更新/命令队列的完整路径；验证器导出这些完整方法。图形观察钩子和服务器 scope/线程保护另有实际包审计。
- 真实 MonoMod RuntimeDetour 对测试方法的安装、执行变化、Dispose 后恢复；该测试**不 detour 游戏方法**。
- 只反射原生方法体，经过真实 DynamicMethodDefinition → ILContext 路径应用生产补丁；没有调用原生方法或启动世界。
- 对安装 DLL 的完整分配、接受连接、清理函数体使用隔离存储/外部效果替身执行。接受路径包含 Reset/Socket 赋值、满服和监听处理，两个循环仅增加各三条 eligibility 指令。
- 空预留保留原生行为；预留保护、真人更新/断开、满服、即时接受、释放后真人复用、重复/过晚 arm、错误线程，以及保留槽位意外出现真人时仍执行其原生清理。
- 10,000 次原生网络循环与主线程预留读取并发，不反复写 active。连接接受夹具与真实 TCP/Steam 回调调度并非同一件事，没有冒充真实网络压力测试。
- 审计实际 `.tmod`：无 Bridge 引用，无假 RemoteClient/Socket/NPC/进程创建，无连接状态/myPlayer 写入，仅一次 Player 创建和 active=true，无自定义 actor 发包/额外 Player.Update/移动捷径，钩子具备撤销配对。
- 执行实际包内 `Finish` 的完整 IL（替换存储、通知和日志效果），验证正常恢复/释放、异常 finally、错误线程/引擎主线程身份拒绝；对已断开但活动、State=-1/1/3/10、待终止、终止已批准、未知 RemoteClient、Player 引用冲突及 spawn 前过渡态，验证不写 Player、不调用断开、不提前释放，并禁止重复清理。
- 同一实际 teardown IL 额外覆盖 Actor=null、RemoteClient idle 时的 active 原始占位、基线建立前 active Player、inactive 替换对象、null Player 和缺失基线，以及 actor 存在但恢复占位变为 active。逐一验证保留 Player/引用/活动状态/预留，且重复调用仍不能释放；正向验证已知 inactive 原始占位可在未 spawn 退出时释放。
- 读取安装 .cctor 的 IsListening=true 初值和 TcpListener 写入位置，实际执行包内 StartServer/Listen 完整 IL（替换引擎存储及 orig 效果）：在默认标志为 true 时预留先于首个监听；已有 TCP 对象/监听调用/accept/网络循环时拒绝过晚 arm，保留真人 orig 和标志；验证错误主线程、释放后不能 re-arm，以及监听重试/原生返回值。
- 执行实际包内 Spawn/CheckInitialSlot/归属谓词/accept/network 包装，覆盖构造与连接失败、构造后再次验证引用、回调替换或重新激活对象、错误线程与重入，以及构造/发布期间并发 accept 的锁互斥。断线 IL 另验证原生 StatusText2 处理仍执行、仅有预留不保护未知 Player、待终止审批/Reset 不受 synthetic guard 影响。

夹具对象不是游戏内 Player/Socket。验证器的 G1 LIVE NOT RUN 仅指离线检查不运行游戏；后续 0.1.4 已由用户完成上面的单 host 实测，原生更新、host 命令和带 actor 退世界已有证据。第二真人后续分配/退出、活动服务器热卸载等未验证，不扩大结论。没有运行全量测试。

日志：`.local/virtual-player-engine-native.log`、`.local/virtual-player-engine-build.log`、`.local/virtual-player-engine-validation.log`。原生及补丁后 IL 在 `.local/virtual-player-engine-validation/`。这些本机生成内容不提交。

## 用户操作：全新 Host & Play G1

以下步骤需用户手工执行；使用 0.1.4 新进程、新 profile。不会自动启动游戏。已准备的本地构建与启动入口见本节末。

### 隔离、备份与启动

1. 完全退出相关 tML 图形客户端和 Host & Play 服务器进程，保留 Steam 正常运行。
2. 使用全新 profile、新角色、新小型 Classic 世界，不导入原世界/角色，不用云存档。若复用专用实验 profile，先退出游戏，完整复制到一个不存在的备份目录；不要覆盖旧备份。
3. 执行上面的 build/verify；仅手工部署到新目录：

```powershell
$repo = 'D:\Agent Project\terraria-ai-companion'
$tml = 'D:\steam\steamapps\common\tModLoader'
$profile = Join-Path $repo ('.local/virtual-player-engine-host-g1-014-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
if (Test-Path -LiteralPath $profile) { throw '请选择未存在的新 profile，或先备份旧实验目录' }
New-Item -ItemType Directory -Path (Join-Path $profile 'Mods') | Out-Null
Copy-Item -LiteralPath (Join-Path $repo '.local/virtual-player-engine-build/Mods/VirtualPlayerEngineProbe.tmod') `
    -Destination (Join-Path $profile 'Mods/VirtualPlayerEngineProbe.tmod')
Set-Content -LiteralPath (Join-Path $profile 'Mods/enabled.json') -Value '["VirtualPlayerEngineProbe"]'
Push-Location $tml
try { & ./start-tModLoader.bat -tmlsavedirectory $profile }
finally { Pop-Location }
```

4. 确认只启用 VirtualPlayerEngineProbe。新建角色和世界，通过 Multiplayer → Host & Play 进入。只开这一个真人图形窗口；Host & Play 的服务器进程是正常机制，不是第二个 AI 客户端。
5. 图形客户端日志应出现 graphical host-launch hook ready，以及 `graphical-host-launch`（`role=graphical-client`、`dedServ=false`、`programIsMainThread=true`）。服务器日志应先出现指纹匹配、hooks ready，再依次出现 server-scope-enter、pre-listen-reservation 的 armed=true、native-listener-entry 的 entry=1/reserved=true、reservation-ready，且无 fault。预留时 tcpListenerExists=false、listenerEntries=0；legacyIsListening=true 是正常初值。服务器事件的 `role=native-server`、`dedServ=true`；scope、StartServer、首次监听、reservation-ready 的 programIsMainThread=true，thread 应等于 programMainThread；预留建立后还应等于 ownerThread。用进程列表确认两个不同 PID 及父子关系。任一证据缺失就停止，不关闭检查；容量不足 2 时不会 arm。

### 命令与预期

所有命令在服务器排队执行，带 world session，并在执行时重新核对 requester 对象及原生 host 权限。服务端控制台省略 `/`。不新增 TCP 控制端口。Steam host 若被原生权限谓词拒绝，保留证据并停止，不放宽成任意玩家。

| 命令 | 预期 |
| --- | --- |
| `/vph status` | 服务器日志输出 actor/真人快照、预留、进程/线程、网络循环及跳过计数；命令回复简要状态 |
| `/vph allocator` | 在游戏线程只读调用当前实际原生分配器，记录 next；不得等于 reservation.slot，满服可为 -1 |
| `/vph spawn` | 在已有预留内创建一个真实 Player；再次执行不创建第二个 |
| `/vph despawn` | 清理并释放；重复执行幂等；此后本进程不再 arm |

### G1-A：仅 host 图形窗口

1. 在安全出生点让真人静止，执行 status 和 allocator，记录基线。真人应有独立 connected slot；reserved slot 无 connected Socket/State=10；此时 actorSlot=-1。
2. 执行 spawn、status，再执行一次 spawn。期望同一个 actorSlot，`sameReference=true`、`active=true`、`dead=false`，与真人 slot 不同。AI 使用真实 Main.player 元素，但不要求画面出现该角色。
3. 间隔约 5、15、30 秒取 status。`networkLoops`、`loopsSinceSpawn`、actor Pre/PostUpdates 和 cleanupSkips 应持续增加，actor 引用保持，connected=false、remoteState=0、remoteActive=false、pendingTermination=false、terminationApproved=false。actor 名称仅用于辨认，名字本身不是身份/存活证明。
4. 真人手动左右移动、跳跃、打开关闭背包，期间取 status。确认输入没有被接管，真人 slot 不变，其更新计数/位置变化正常。保存录像；静态包检查不能替代这一观察。
5. allocator 始终不得返回 reserved slot。记录服务器进程与图形进程列表，确认没有额外 AI 游戏客户端。

### G1-B：真人后续分配与断开

离线测试已覆盖全部其他槽位被占时分配器返回 -1；真实连接仍需用户验证，不能仅凭 allocator 输出就标 live pass。

如果要完成这部分，请另一位**真人**从另一机器加入相同测试服务器。只检查服务器 `native-accept` 的实际 slot、真人正常控制/退出以及原有 host/actor 状态；不做 G3 可见性或动画验证，也不启动 AI 客户端。

- 有真人空槽时，新真人应分配到非 reserved slot；host 与 actor 不应被重置。
- 真人退出后，其原生断开/Reset 正常，actor 仍存活。
- 若配置容量被预留与真人占满，后续连接可以收到满服或无法加入，具体表现需记录；不能要求为真人抢占 AI 槽位。
- despawn 释放后，该 slot 应重新成为原生可分配容量。可用 allocator 和后续真人连接记录交叉核查。

若没有第二真人连接证据，G1 的这项保持未验证；不要将 G1 整体标为 passed in live game。

### G1-C：清理与重新开始

1. 执行 despawn、status、再次 despawn。期望 actorSlot=-1、reservation=null，无 fault，真人仍正常。teardown 日志记录顺序是先恢复占位、后释放预留，因此 teardown 当条可能仍带预留；以之后的 status 确认最终释放。
2. 同一会话再 spawn 应拒绝，要求新的服务器进程，这是刻意限制。
3. 完全退出并新建一次 Host & Play 服务器进程；重新 spawn 后直接正常退出世界，验证 `OnWorldUnload` 或 server-scope 的 teardown，不能残留 actor/预留。保存这次日志后再重启，避免日志被覆盖。
4. 另一次全新服务器会话不 spawn 就退出，验证空预留也释放。回到模组菜单后再卸载/重新加载 Probe，检查没有 hook/unload 错误。不要在活动服务器上强行热卸载以绕过清理。

若 actor 死亡/不活动、引用改变、线程不符、权限失败、初始化失败、出现任何连接过渡态或 fault，停止该轮并保存证据。未隔离且能正常退出时使用正常退出流程；若已出现 teardown-quarantined，不重试 despawn/清世界/热卸载，按上面的 quarantine 停止条件结束隔离测试进程。不得通过补设 active、改 State、释放预留或修改 myPlayer 继续。

### 本次已准备的 0.1.4 构建

构建文件：`.local/virtual-player-engine-build/Mods/VirtualPlayerEngineProbe.tmod`，SHA256 为 `66C21539E4BCF892373BE5657C8BC57418DC24212CBCE0160610FF9522E0DE4E`。

已创建隔离目录 `.local/virtual-player-engine-host-g1-014-20261025-171417/`，创建时仅部署并启用 Probe，副本哈希已核对；随后用户用它完成了上面的实测，目录目前已有世界/角色，不再视为全新目录。后续运行前确认只启用 Probe。从仓库根目录手工运行：

```powershell
& './.local/virtual-player-engine-host-g1-014-20261025-171417/start-host-play.ps1'
```

启动脚本先验证部署包哈希，再以该目录为 `-tmlsavedirectory` 调用原生启动器；新建角色与小型 Classic 世界后按上面的 G1-A/B/C 执行。该 profile、脚本和构建仅是本机产物，不提交用户数据；其他机器使用前面的 build/verify 与部署步骤。

## 日志采集、回滚和结论记录

JSON 行带 `probe=VirtualPlayerEngineProbe`，包括 UTC、进程 ID/role、world session、预留 session/slot/capacity、当前/owner/programMainThread 和 programIsMainThread、netMode/myPlayer、actor 的位置/速度/生命状态及连接/终止状态、原生更新次数、真人快照和 teardown。`worldSnapshot=false` 的图形启动/网络日志只记录元数据，humans 空数组不表示无人连接。

```powershell
$repo = 'D:\Agent Project\terraria-ai-companion'
$tml = 'D:\steam\steamapps\common\tModLoader'
$evidence = Join-Path $repo ('.local/engine-g1-evidence-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $evidence | Out-Null
Copy-Item -LiteralPath (Join-Path $tml 'tModLoader-Logs') -Destination (Join-Path $evidence 'host-logs') -Recurse
Get-ChildItem (Join-Path $evidence 'host-logs') -Recurse -Filter '*.log' |
    Select-String -Pattern 'VirtualPlayerEngineProbe|G1 |Starting tModLoader|STOP|Exception|Terminating' |
    Set-Content -LiteralPath (Join-Path $evidence 'probe-lines.txt')
Get-CimInstance Win32_Process | Where-Object { $_.Name -match 'dotnet|tModLoader' } |
    Select-Object ProcessId, ParentProcessId, Name, CommandLine |
    Out-File (Join-Path $evidence 'processes.txt')
Copy-Item -LiteralPath (Join-Path $repo '.local/virtual-player-engine-validation.log') -Destination $evidence
```

以启动输出为准检查实际日志目录；本机通常为安装目录的 `tModLoader-Logs`，必须包含服务器日志，不能只保存图形客户端日志。第二位真人若参与，另存其连接/退出时间及相关日志。记录命令顺序、UTC、世界/角色、版本哈希、前后快照和录像。分享前检查日志中的个人标识、路径和地址。

回滚：先 despawn 或正常退出测试世界，确认服务器进程结束；以后不再使用这个实验 profile。安装 DLL 未修改，不需要修复安装。需要恢复 profile 时，将完整备份复制到一个新的空目录，再用 `-tmlsavedirectory` 指向恢复副本，不覆盖已有 Worlds/Players，不递归删除用户数据。

| Gate | 本次结果 | 不足以证明的事项 |
| --- | --- | --- |
| G0 | passed in offline tests | 164 项定向检查和独立编译通过，不能证明 0.1.4 真实游戏行为 |
| G1（0.1.2） | blocked | 真实启动/钩子/线程链已观察，但预留误判失败，未 spawn、未进入存活测试 |
| G1（0.1.3） | not attempted | 已修复监听判定；由 0.1.4 接续实现 |
| G1（0.1.4） | partial live verification | 单 host 无客户端存活和退世界清理有日志证据，真人控制/手动 despawn 经用户确认；第二真人后续加入/退出保留待验，释放后快照与完整 PID 父子枚举未单独采集 |
| G2/G3 | not attempted / out of scope | 没有动作、AI、完整玩家同步、可见性或动画验收 |

停止条件：任何分配器/接受路径绕过预留、跨线程 Player 写入、真人被重置/清理被吞、需要广泛改写同步、钩子回滚失败或上述异常卸载保险触发，都停止投入当前 live 方案并提交最小复现。不给未实测能力标通过，不自动合并 Draft PR。
