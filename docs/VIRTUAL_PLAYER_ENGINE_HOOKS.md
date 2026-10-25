# Issue #10：当前版本的无客户端 Player 引擎钩子实验

2026-10-25。关联 [Issue #10](https://github.com/729194587/terraria-ai-companion/issues/10)；调查基线为未合并的 [Draft PR #9](https://github.com/729194587/terraria-ai-companion/pull/9) 及其 [报告](https://github.com/729194587/terraria-ai-companion/blob/731ee1123091f4a67fa7578c4f6e9b9bc9a69710/docs/VIRTUAL_PLAYER_SPIKE.md)。

**结果：G0 = passed in offline tests；G1 = not attempted。** 实现了可供用户手工验证的独立 `VirtualPlayerEngineProbe`，未部署、未开服、未创建用户角色/世界。真实 Player 是否在 Host & Play 中持续存活、真人输入是否不受影响以及正常退出是否满足预期，均尚未实测。构建或夹具通过不是 G1 live pass。G2/G3、移动命令、Agent、Lua、档案、物品使用和战斗均未实施。

分支 `spike/virtual-player-engine-hooks` 从 `main` 的 `9a57b3bae8d888f79c2da6c41cbc5b5ec66085c6` 开始，只新增本报告和 `experiments/virtual-player-engine-hooks/`。main、CompanionBridge、Python、Lua、TCP、原子操作和 PR #9 均保持原样。

## 实验决策与范围

前置离线检查验证了槽位过滤、原生连接接受、清理分支和线程所有权后，才实现真实 Player 创建。采用**监听开始前固定预留、每个服务器进程仅一轮 actor 生命周期**，避免本轮引入动态抢槽、接受连接锁或清理循环确认协议。

- 在原生 `StartServer` 之前发布不可变预留，选 `Main.maxNetPlayers - 1`，要求配置容量在 2..255。不会使用 255 哨兵，也不会占 slot 0。
- 所有本机原生接受连接的选槽调用都会经过已修改的分配器；没有先选中候选、再临时发布预留的窗口。
- 预留会暂时减少一个真人可用连接槽位，**即使尚未执行 spawn**。
- 只有游戏/服务器模拟线程可以创建、替换或清理 actor。网络线程只读取原子发布的预留和连接状态，增加诊断计数。
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
| `FindNextOpenClientSlot` | 循环体 `IL_0004`，递增点 `IL_0014`；按连接状态选槽 |
| `OnConnectionAccepted` | `IL_001a` 选槽；`IL_002b` Reset；`IL_0038` 赋 Socket；`IL_0070` 再检查可用槽位 |
| `UpdateConnectedClients` | 每槽位入口 `IL_0009` 在待终止检查之前；递增点 `IL_00d4`；原生停用位于 `IL_00c7` |
| `SyncOnePlayer` | State 非 10 时进入离线路径，最终还会调用 `Player.Hooks.PlayerDisconnect` |
| `Main.UpdateServer` | `IL_01e6..IL_0205` 给活动 Player 调用 `CheckSection`，不要求它有连接 |
| `RemoteClient.CheckSection` | 会更新区块/接收状态；无客户端 actor 不应走这条发送路径 |
| `Main.DedServ_PostModLoad` | StartServer、服务器模拟循环和正常 OnWorldUnload 位于同一原生调用范围 |
| `ModLoader.Mods_Unload` | 先 `WorldGen.clearWorld`，再 `ModContent.UnloadModContent` |
| `ModContent.UnloadModContent` | 先 `MonoModHooks.RemoveAll(mod)`，再 `mod.UnloadContent()`；只靠 Mod.Unload 清理 actor 已经太晚 |
| `WorldGen.SaveAndQuit` | 通过线程池进入保存/退出回调；不能默认每个 OnWorldUnload 都在模拟线程 |

另外，实际 MonoMod 操作上下文把分支操作数包装为 `ILLabel`，运行时 IL 克隆名称含 `类型::方法`。共享补丁同时处理这些真实形态；不是只在原始 Cecil 指令列表上测试后便推断运行时可用。

## 实际拦截点、线程与真人边界

总计九个订阅，其中同一个清理方法分别有 IL 过滤和 On 计数；无连接状态伪造。

| 拦截 | 线程/频率 | 行为与回滚 |
| --- | --- | --- |
| `IL_Netplay.FindNextOpenClientSlot` | 接受连接回调所在的 TCP/Steam 路径，每次原生选槽 | 循环体前增加 `ldloc; call IsReserved; brtrue next`；保留原生容量、连接检查及满服行为 |
| `IL_Netplay.UpdateConnectedClients` | 网络循环，每槽位每轮 | 待终止分支前增加三条 eligibility 指令，只跳过被本 Probe 预留且无任何连接活动/终止状态的槽位 |
| `On_Netplay.StartServer` | 服务器模拟线程，每次开服 | 仅首次且尚未监听、外层模拟 scope 已进入时 arm，然后调用 orig；重复开服不重新抢槽 |
| `On_Netplay.UpdateConnectedClients` | 网络循环 | 原样调用 orig，完成后增加 networkLoops；不访问/修改 AI Player |
| `On_Netplay.OnConnectionAccepted` | TCP/Steam 接受线程，每次接受 | 原样调用 orig，记录实际 Socket 归属槽位；不延迟、替代或伪造连接 |
| `On_NetMessage.SyncOnePlayer` | 依据调用者可在游戏/网络路径 | 仅对保留且无连接状态的槽位返回；所有真人调用保留 orig |
| `On_RemoteClient.CheckSection` | 正常活动玩家区块检查路径 | 仅对无连接保留槽位返回；不伪造 TileSections，不改变真人发送 |
| `On_Main.DedServ_PostModLoad` | 服务器模拟调用范围 | 记录并约束 scope 线程，finally 保证该线程在离开模拟范围时清理 actor/预留 |
| `On_WorldGen.clearWorld` | 依原生调用者而异 | 在任何 clearWorld 原生写入、RemoveAll 之前执行 teardown；有活动预留时若线程不符，直接抛错中止该不支持的清理流程 |

预留读视图用 `Volatile.Read/Write` 发布不可变 `(slot,capacity,session)`，计数用 Interlocked。不持锁调用原生代码，也没有网络线程等待游戏线程的协议。预留只会从一个固定槽位变为空，不会在服务器运行中扩展。

保护真人的规则：

1. AI 的活动态不参与 `HasClients` 计数；不会假扮 host，也不延长 Host & Play 的原生生命周期。
2. 如果保留槽位意外出现 connected/IsActive/非零 State/待终止状态，eligibility 立即返回 false，保留真人的原生更新/断开/Reset，并记录 fault；下一游戏更新停止 actor。不会为了留住 AI 而吞掉真人清理。
3. teardown 同时核对 Player 引用和连接。如果槽位已变成真人，不覆盖数组、不调用该槽位的断开钩子，也不将其 active 清零。
4. 所有写入都先 `AssertOwner`。图形客户端不安装引擎钩子，不替换其 LocalPlayer，不修改任何 Main.myPlayer 或 RemoteClient 字段。

### 创建与清理

`/vph spawn` 在模拟线程重新验证指纹/钩子就绪、session、容量和槽位空闲，再执行一次 `new Terraria.Player`，放入专用 Main.player 元素，设置身份、基础生命、尺寸和初始空闲位置，最后仅一次 `active=true`。原生构造器初始化 ModPlayer 实例；调用 `Player.Hooks.PlayerConnect` 完成原生压力板重置及连接通知。

不调用 `Player.Spawn`/EnterWorld：这些路径有本地 UI/所有者副作用。初始位置仅在世界出生点附近最多向上检查 12 格空位，不清理 Tile。后续完全由原生 Player.Update 更新，没有手动额外 Update、输入注入或逐帧坐标修补。自然重力产生的位置变化不算 G2 验证。

despawn 对自己的对象设为不活动，调用一次断开通知，恢复最初的空闲占位对象，再发布空预留。finally 覆盖断开钩子和日志异常。重复 despawn 无副作用。actor 死亡、失去活动态或引用变化时停止，不自动复活/修复。

正常 OnWorldUnload、clearWorld 入口和服务器模拟 scope 的 finally 都可幂等触发此清理。Mod.Unload 只验证早期清理已完成、移除订阅。部分安装失败则在任何服务器启动前逆序撤销订阅。

**不支持活动服务器上的后台/并发热卸载。** pre-clearWorld 保险会在错误线程上中止原生 clearWorld，从而防止接下来先撤销钩子、再留下 actor。若遇到此保险，保存异常和日志，按停止条件处理；不要绕过断言或重试 spawn。正常用户流程是先 despawn 或退出测试世界，再关闭/重载 Mod。强制结束进程不承诺 finally 执行；本实验无档案写入，也不修改安装 DLL。

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
EngineHookOfflineTests: 44 checks passed; actual IL fixtures only; G1 LIVE NOT RUN.
Engine probe package: loader=2026.8.3.0; bytes=31138; DLL=44544;
74 total checks; G1 LIVE NOT RUN.
```

验证范围：

- 两个固定 DLL 的指纹和已生成钩子、启动/监听/卸载顺序；遍历包含嵌套类型的整个安装模块，断言分配器、Reset 与监听入口的直接方法引用均在已调查路径中。
- 真实 MonoMod RuntimeDetour 对测试方法的安装、执行变化、Dispose 后恢复；该测试**不 detour 游戏方法**。
- 只反射原生方法体，经过真实 DynamicMethodDefinition → ILContext 路径应用生产补丁；没有调用原生方法或启动世界。
- 对安装 DLL 的完整分配、接受连接、清理函数体使用隔离存储/外部效果替身执行。接受路径包含 Reset/Socket 赋值、满服和监听处理，两个循环仅增加各三条 eligibility 指令。
- 空预留保留原生行为；预留保护、真人更新/断开、满服、即时接受、释放后真人复用、重复/过晚 arm、错误线程，以及保留槽位意外出现真人时仍执行其原生清理。
- 10,000 次原生网络循环与主线程预留读取并发，不反复写 active。连接接受夹具与真实 TCP/Steam 回调调度并非同一件事，没有冒充真实网络压力测试。
- 审计实际 `.tmod`：无 Bridge 引用，无假 RemoteClient/Socket/NPC/进程创建，无连接状态/myPlayer 写入，仅一次 Player 创建和 active=true，无自定义 actor 发包/额外 Player.Update/移动捷径，钩子具备撤销配对。
- 执行实际包内 `Finish` 的完整 IL（替换存储、通知和日志效果），验证占位恢复、断开一次、释放顺序、异常 finally、错误线程拒绝、真人冲突和未 spawn 即退出。

夹具对象不是游戏内 Player/Socket。未验证真实 Mod.Load 安装链、Native Player.Update 的场景表现、Steam host 权限、原生退出/热卸载的真实时序或真人客户端。没有运行全量测试。

日志：`.local/virtual-player-engine-native.log`、`.local/virtual-player-engine-build.log`、`.local/virtual-player-engine-validation.log`。原生及补丁后 IL 在 `.local/virtual-player-engine-validation/`。这些本机生成内容不提交。

## 用户操作：全新 Host & Play G1

以下步骤需用户手工执行；本次没有部署或启动。

### 隔离、备份与启动

1. 完全退出相关 tML 图形客户端和 Host & Play 服务器进程，保留 Steam 正常运行。
2. 使用全新 profile、新角色、新小型 Classic 世界，不导入原世界/角色，不用云存档。若复用专用实验 profile，先退出游戏，完整复制到一个不存在的备份目录；不要覆盖旧备份。
3. 执行上面的 build/verify；仅手工部署到新目录：

```powershell
$repo = 'D:\Agent Project\terraria-ai-companion'
$tml = 'D:\steam\steamapps\common\tModLoader'
$profile = Join-Path $repo '.local/virtual-player-engine-host-20261025'
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
5. 在服务器日志先确认：指纹匹配、hooks ready、server-scope-enter、pre-listen-reservation 的 armed=true、reservation-ready，且无 fault。失败则停止，不关闭检查。容量不足 2 时不会 arm。

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
3. 间隔约 5、15、30 秒取 status。`networkLoops`、`loopsSinceSpawn`、actor Pre/PostUpdates 和 cleanupSkips 应持续增加，actor 引用保持，connected=false、remoteState=0、remoteActive=false。actor 名称仅用于辨认，名字本身不是身份/存活证明。
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

若 actor 死亡/不活动、引用改变、线程不符、权限失败、初始化失败、出现 connected 状态或任何 fault，停止该轮，保存证据并正常退出。不得通过补设 active、改 State 或修改 myPlayer 继续。

## 日志采集、回滚和结论记录

JSON 行带 `probe=VirtualPlayerEngineProbe`，包括 UTC、服务器进程 ID、world session、预留 session/slot/capacity、当前/owner 线程、netMode/myPlayer、actor 的位置/速度/生命状态及真实连接状态、原生更新次数、真人快照和 teardown。`worldSnapshot=false` 的网络日志只记录元数据，humans 空数组不表示无人连接。

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
| G0 | passed in offline tests | 74 项定向检查和独立编译通过，不能证明真实 Mod 启动与游戏行为 |
| G1 | not attempted | Host & Play 原生 Player 存活、真人输入/后续连接、正常退出均需按以上步骤实测 |
| G2/G3 | not attempted / out of scope | 没有动作、AI、完整玩家同步、可见性或动画验收 |

停止条件：任何分配器/接受路径绕过预留、跨线程 Player 写入、真人被重置/清理被吞、需要广泛改写同步、钩子回滚失败或上述异常卸载保险触发，都停止投入当前 live 方案并提交最小复现。不给未实测能力标通过，不自动合并 Draft PR。
