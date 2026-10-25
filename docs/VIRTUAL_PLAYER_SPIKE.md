# Issue #8：无客户端 Virtual Player 实验

实验日期：2026-10-25。分支：`spike/virtual-player-multiplayer`，基点和未改动的本地 `main` 均为 `9a57b3bae8d888f79c2da6c41cbc5b5ec66085c6`。关联 [Issue #8](https://github.com/729194587/terraria-ai-companion/issues/8)。

**决定：停止在现有原生生命周期上实现无连接 Player；先评估是否值得单独维护引擎层实验。** G0 找到三个可重复的原生阻塞：无连接 Player 被网络循环停用、活动 Player 槽位不构成连接预留、原生全量同步将 `State != 10` 的槽位广播为离线。当前提交是能编译的**负向诊断探针**，`spawn` 会明确拒绝，未实现可存活的 Virtual Player。不能将此结果解读为所有改写引擎方案均不可能。

遵循 Issue 的停止条件：没有通过伪造 `RemoteClient`、恢复 `active`、替换 NPC 或启动 AI 客户端来绕开阻塞。没有修改 CompanionBridge、Python、Lua、TCP 或现有多人权限路径。没有部署包、打开游戏、创建或修改用户角色/世界。

## 验证状态

| Gate | 状态（Issue 枚举） | 证据与边界 |
| --- | --- | --- |
| G0 | passed in offline tests | 调查安装 DLL、固定提交的官方源码；执行完整原生生命周期 IL 的隔离夹具；找到阻塞。此状态仅指调查/离线复现完成。 |
| G1 | blocked | 原生槽位生命周期、分配及同步与无连接 actor 冲突。Host & Play 未实测；探针拒绝插入 actor。 |
| G2 | not attempted | G1 未通过，不能验证服务端移动、跳跃、碰撞及真人输入不受影响。 |
| G3 | not attempted | 无 actor，也未启动第二个人类观察客户端。不存在远端可见性证据。 |

单独的构建结果：0 errors、0 warnings，成功生成独立 `.tmod`。33 项定向检查通过。**构建、IL 复现和包审计均不等价于 G1/G2/G3 的 live pass。** 没有运行全量测试。

## G0：版本、来源和可重复调查

| 项目 | 本机观测 |
| --- | --- |
| 安装目录 | `D:\steam\steamapps\common\tModLoader` |
| Terraria | `1.4.4.9`，程序集版本同为 `1.4.4.9`，不能据此判断 tML 版本 |
| tModLoader | `2026.08.3.0`（包头规范化显示 `2026.8.3.0`），stable |
| 引擎提交 | `c9545cf27d56c456321e1896274c02adf67d0140` |
| DLL SHA256 | `FCC6A9624B12191BE4A15D714A3675440911FEB8C1D374642AB1FD686F0DA0A5` |
| 工具 | 安装随附 .NET 8、Mono.Cecil 0.11.6、PowerShell 7 的 Add-Type |

版本来自本机启动日志，构建器再次确认包头版本；DLL 哈希在离线检查中重新计算。运行时探针同时核对 `BuildInfo.tMLVersion`、`CommitSHA` 和 DLL 哈希，任一不匹配便拒绝变更类命令，要求重新执行 G0。该指纹匹配只表示“调查过这个二进制”，不表示无连接 actor 受支持。

官方参考均固定到上述提交，避免用滚动 stable 文档代替安装版本：

- [Main 补丁](https://github.com/tModLoader/tModLoader/blob/c9545cf27d56c456321e1896274c02adf67d0140/patches/tModLoader/Terraria/Main.cs.patch)、[Player 补丁](https://github.com/tModLoader/tModLoader/blob/c9545cf27d56c456321e1896274c02adf67d0140/patches/tModLoader/Terraria/Player.cs.patch)。
- [Netplay 补丁](https://github.com/tModLoader/tModLoader/blob/c9545cf27d56c456321e1896274c02adf67d0140/patches/tModLoader/Terraria/Netplay.cs.patch)、[RemoteClient 补丁](https://github.com/tModLoader/tModLoader/blob/c9545cf27d56c456321e1896274c02adf67d0140/patches/tModLoader/Terraria/RemoteClient.cs.patch)。
- [NetMessage 补丁](https://github.com/tModLoader/tModLoader/blob/c9545cf27d56c456321e1896274c02adf67d0140/patches/tModLoader/Terraria/NetMessage.cs.patch)、[MessageBuffer 补丁](https://github.com/tModLoader/tModLoader/blob/c9545cf27d56c456321e1896274c02adf67d0140/patches/tModLoader/Terraria/MessageBuffer.cs.patch)。
- [ModPlayer](https://github.com/tModLoader/tModLoader/blob/c9545cf27d56c456321e1896274c02adf67d0140/patches/tModLoader/Terraria/ModLoader/ModPlayer.cs)、[ModCommand](https://github.com/tModLoader/tModLoader/blob/c9545cf27d56c456321e1896274c02adf67d0140/patches/tModLoader/Terraria/ModLoader/ModCommand.cs)、[CommandLoader](https://github.com/tModLoader/tModLoader/blob/c9545cf27d56c456321e1896274c02adf67d0140/patches/tModLoader/Terraria/ModLoader/CommandLoader.cs)。

公开仓库是补丁，并不包含所有原版函数的完整函数体。下面的偏移、调用顺序和分支结论来自**本机安装 DLL 的 Cecil 调查**；验证脚本把所需完整 IL 导出到 `.local/virtual-player-validation/`，不提交游戏 DLL 或完整反编译游戏源码。

### 真实 Player 更新与上下文

`Main.DoUpdateInWorld` 在 `IL_0017` 调用 `SystemLoader.PreUpdatePlayers`，遍历 `Main.player` 的活动元素，在 `IL_004f` 调用 `Player.Update(index)`，于 `IL_010f` 调用 `PostUpdatePlayers`。因此真实 Player 的物理/动画更新入口确实存在，不需要用 NPC 模拟外观。但这个循环并不管理连接存活或槽位所有权。

`Player.Update(int)` 含 `PlayerLoader.PreUpdate`（`IL_05a2`）、`PreUpdateMovement`（`IL_8232`）、`DryCollision`（`IL_82d6` 等）、`SlopingCollision`（`IL_83ce`）、`ItemCheckWrapped`（`IL_85aa`）、`PlayerFrame`（`IL_85b0`）。重力、跑步、跳跃以及碰撞逻辑都在原生调用链中，不能以不断设置 position 代替它们，也不能仅凭函数存在就宣称服务器能可靠控制 actor。

键盘/触发器处理和 `SetControls` 位于 `index == Main.myPlayer` 的分支内（`IL_09a8` 至 `IL_1913`，`SetControls` 在 `IL_1010`）。不能假定远端或虚拟 Player 会运行本地输入钩子。多个物品、伤害和 UI 路径也有 `myPlayer` 判断，移动可行性不能推广为采矿/战斗可行性。

`Main.LocalPlayer` 的真实 getter 是 `Main.player[Main.myPlayer]`。`Player.Update` 开头创建的 `Main.CurrentPlayerOverride` 只临时设置 `_currentPlayerOverride`，影响 `Main.CurrentPlayer`；它**不修改** `Main.myPlayer` 或 `Main.LocalPlayer`。在服务器上把真人 LocalPlayer 替换成 actor 是错误的上下文修补方法。

### 阻塞 B1：连接清理先于“持久 Player”承诺

`Netplay.ServerLoop` 在自己的网络线程中反复调用 `UpdateConnectedClients`（`IL_000e`），不是 `ModSystem` 的游戏更新线程。

`UpdateConnectedClients` 对每个槽位的行为：

1. 已连接：调用 `RemoteClient.Update` 并计入 `HasClients`。
2. 无连接但 `IsActive`：设置 `Connection lost` 的待终止状态。
3. 无连接且不活动：对 `slot < 255`，在 `IL_00c7` 写入 `Main.player[slot].active = false`；如果之前活动，在 `IL_00cf` 调用 `Player.Hooks.PlayerDisconnect(slot)`。
4. 待终止已批准：先把 State 清零，同步断开，再 `RemoteClient.Reset`。

`IsConnected()` 本身检查实际 `Socket.IsConnected()`，`State=10` 不会让它变成 true。反复在 `PreUpdatePlayers` 恢复 `active` 会与网络线程竞争，绕过清理并产生反复断开钩子；不能据此形成可靠生命周期。

### 阻塞 B2：Player 活动状态不预留连接槽位

`FindNextOpenClientSlot` 只扫描 `< Main.maxNetPlayers` 的 `RemoteClient.IsConnected()`，完全不读取 `Main.player`。`OnConnectionAccepted` 取得槽位后调用 `Clients[slot].Reset()`。后者在 `IL_001e` 创建新的真实 `Player`，于 `IL_0023` 替换 `Main.player[slot]`。

因此在游戏线程“检查空闲、再写入 Player”不是与网络线程原子协调的操作。使用较大的空槽也只能减少概率，不能保证不抢占/覆盖真人。探针只记录 candidate 快照，**绝不视其为预留，也不写入数组**。

### 阻塞 B3：原生多人同步绑定连接状态

加入流程包括真实连接接受、版本/模组协商、加载 Player、世界数据和区块请求、PlayerSpawn。在安装 DLL 的 `MessageBuffer.GetData` message 12 分支中，服务器把 payload 的玩家编号替换为连接的 `whoAmI`，调用 `Player.Spawn`；首次出生在 `IL_256b` 把连接 State 从 3 变为 10，随后在 `IL_2588` 调用 `SyncConnectedPlayer`。分配一个对象没有完成这些步骤。

`SyncOnePlayer(slot,toWho,fromWho)` 在 `IL_0019` 读取 `Clients[slot].State`，`IL_0020` 对 `!= 10` 跳至 `IL_03f7`，将活动值强制设为 0，在 `IL_040e` 广播 `PlayerActive(14)` 的离线状态。该分支甚至使用广播参数，不是只给新加入者发送。`SyncConnectedPlayer` 会对活动玩家复用此路径，因此晚加入者会触发相同阻塞。

State=10 路径才发送活动、身份、控制、生命、装备及 ModPlayer 数据。手工只发送 4/13/14 等少数包也不能修复后续加入、离开、完整同步和所有权冲突。

`Main.DoUpdate` 仅在 `netMode == 1` 时调用 `TrySyncingMyPlayer`。后者以 `Main.myPlayer` 对照 `clientPlayer` 克隆，发送 `PlayerControls(13)` 和其他状态，再执行 `SendClientChanges` / `clientClone`。服务器缺少一个天然的“本地 AI 客户端差分源”。

在接收端，message 13 的 `IL_2736` 至 `IL_2744` 明确在服务器上把 payload slot 覆盖为连接 `whoAmI`，然后读取输入、position、velocity，并转发。不能让真人连接向另一 slot 发送控制包来冒充 actor 所有者。包 4 是身份，12 是出生，13 是控制/位置/速度，14 是活动/离线，16 是生命；身份同步不等价于加入成功或远端动画正确。

### 支持钩子与后续引擎实验的界线

本探针仅使用已支持的 `ModCommand`、`QueueMainThreadAction`、`OnWorldLoad/Unload`、`PostUpdatePlayers`、`ModPlayer.PreUpdate/PostUpdate` 和只观察的 `HijackSendData`。后者始终返回 false，保留原生序列化；计数只表示进入 SendData，**不是成功投递、客户端接收或渲染确认**。

若要继续真实 Player 路线，必须单独设计无连接 actor 的槽位预留/释放、网络线程清理例外、加入/晚加入/断开同步、持续服务器状态广播以及所有权规则。针对 `ServerLoop`、分配器或 `SyncOnePlayer` 的 On/IL detour 属于实验性引擎拦截，不是上述支持钩子的保证；还需要解决跨线程原子协调、版本漂移和卸载回滚。本提交没有实现这些改动，也没有把它们列为已验证能力。

## 本地构建与自动复现

在 PowerShell 7 中，从仓库根目录执行；两个脚本均可传 `-TModLoaderPath`：

```powershell
./experiments/virtual-player/build.ps1
./experiments/virtual-player/verify.ps1
```

构建器实际执行安装随附的 `dotnet/dotnet.exe tModLoader.dll -server -build <repo>/experiments/virtual-player/VirtualPlayerProbe -tmlsavedirectory <repo>/.local/virtual-player-build`。这是编译打包调用，不是创建世界或开服测试。产物：`.local/virtual-player-build/Mods/VirtualPlayerProbe.tmod`。

2026-10-25 本机结果：

```text
Compilation finished with 0 errors and 0 warnings
Packaging: VirtualPlayerProbe
Package audit: loader=2026.8.3.0; bytes=22327; DLL=31232.
NativeLifecycleTests: 33 checks passed;
SHA256=FCC6A9624B12191BE4A15D714A3675440911FEB8C1D374642AB1FD686F0DA0A5;
G1-G3 LIVE NOT RUN.
```

离线复现的关键设置是一个已连接真人槽位 0、一个活动但无连接的虚拟槽位 1。脚本从安装 DLL 复制 **完整的** `UpdateConnectedClients` 和 `FindNextOpenClientSlot` IL，保留所有 opcode 和分支，仅将存储及外部副作用映射到夹具。执行后：分配器仍返回 1；原生网络循环停用槽位 1 并触发断开钩子；已连接真人夹具保持活动。额外复现 State=10 仍被停用、IsActive 伪造引发待终止和下一轮重置、满槽返回 -1。

这些夹具中的 Player/RemoteClient **是测试存储替身，不是 Terraria 活体角色**。未启动图形、真实 Socket、`Main` 静态初始化器、世界模拟或完整 Player.Update。真实 Reset/SyncOnePlayer 的结构和所有权分支通过固定 DLL 的 IL 断言验证，没有声称执行完整握手或网络传输。

产物审计直接解包本次 `.tmod` 的 DLL，检查其身份、独立性、无引擎字段写入、无 Player 更新/出生/传送调用、无 Player/NPC/RemoteClient 分配、无发送 actor 包或开启 socket/进程、游戏线程排队及原生主机权限检查、正确的 World|Console 命令路由。

原始本机输出在 `.local/virtual-player-build.log`、`.local/virtual-player-validation.log`；可复现导出的 IL 在 `.local/virtual-player-validation/*.il.txt`。它们被忽略，不包含在提交中。DLL 指纹变化或断言偏移变化会失败，不能直接删掉断言继续用旧结论。

## 当前探针的命令与记录

| 命令 | 执行位置/权限 | 当前确定的实现 |
| --- | --- | --- |
| `/vplayer status` | 多人在服务器执行；只读 | 输出会话、netMode、ticks、actorSlot=-1、G1 状态，完整快照写服务器日志 |
| `/vplayer spawn` | 服务器控制台或原生 Host & Play host | 检查会话、权限、指纹和空槽；记录 candidate，明确拒绝创建 actor |
| `/vplayer move left` / `right` / `jump` / `stop` | 同上 | 明确拒绝，G1 blocked，未实现运动 |
| `/vplayer despawn` | 同上 | 幂等清除 candidate 观察；无 actor/槽位需要回滚 |
| `/vplayerlocal` | 真人客户端本地，只读 | 记录该客户端实际 Main.player 快照，用来与服务器区分 |

服务器控制台省略 `/`。`/vplayer` 的 World|Console 路由由 tML 处理，不开放额外 TCP 控制口。主机授权使用服务器的 `NetMessage.DoesPlayerSlotCountAsAHost`：已完成连接的 slot 且远端地址为原生认定的本地主机；不猜 slot 0、不信任名字或客户端自报。原生主机判断不适配某种 Steam 连接时应拒绝并记录，不能放开为任意真人。这个分支未做 live 授权测试。

所有命令经 `QueueMainThreadAction`，执行前重新检查 world session 和 requester 的 Player 对象身份。菜单、断线、会话切换和不支持版本都拒绝变更。ModPlayer 计数器仅记录自然发生的更新，探针不调用 Player.Update，不改变控制键、坐标、速度、连接状态或存档。世界卸载和模组卸载记录 teardown；没有创建的 actor，也没有需要跨会话恢复的引擎状态。

JSON 日志带 `probe=VirtualPlayerProbe`。字段包括 UTC、session、world ID、线程、ticks、netMode、myPlayer、actorSlot、candidate、真实连接列表、玩家输入/位置/速度/动画帧、Pre/PostUpdate 次数、同步尝试和 teardown。candidate 是可能变化的观察编号；`actorSlot=-1` 才是当前事实。客户端没有服务器连接列表，日志明确标注 unavailable，不能把空列表当成“服务器没有真人”。

## 人工 runbook：隔离环境、备份与恢复

以下步骤供用户明确选择后执行；本次没有部署或启动。只使用新的本地测试角色和小型新世界，不导入已有存档，不用云存档。G1 期间只开一个真人图形窗口，Host & Play 自动启动的服务器进程是正常机制，不是 AI 客户端。

1. 完全退出 tML，保留 Steam 正常运行。记录安装版本。若要重用旧的实验 profile，先在退出后用 `Copy-Item -LiteralPath <旧profile> -Destination <全新backup目录> -Recurse` 完整备份；备份目录必须未存在。推荐直接使用下面全新路径，无需触碰旧存档。
2. 构建/验证通过后，用下列命令**手工部署到全新测试 profile**。如果路径已存在，脚本停止，另选名称；不要覆盖。

```powershell
$repo = 'D:\Agent Project\terraria-ai-companion'
$tml = 'D:\steam\steamapps\common\tModLoader'
$profile = Join-Path $repo '.local/virtual-player-live-host-20261025'
if (Test-Path -LiteralPath $profile) { throw '请选择未存在的新测试 profile，或先备份旧目录' }
New-Item -ItemType Directory -Path (Join-Path $profile 'Mods') | Out-Null
Copy-Item -LiteralPath (Join-Path $repo '.local/virtual-player-build/Mods/VirtualPlayerProbe.tmod') `
    -Destination (Join-Path $profile 'Mods/VirtualPlayerProbe.tmod')
Set-Content -LiteralPath (Join-Path $profile 'Mods/enabled.json') -Value '["VirtualPlayerProbe"]'
Push-Location $tml
try { & ./start-tModLoader.bat -tmlsavedirectory $profile }
finally { Pop-Location }
```

3. 模组界面确认只启用 VirtualPlayerProbe，没有 CompanionBridge 或其他模组。新建一个测试角色和一个 Classic 小世界（例如 `VPProbe_G1`），通过 Multiplayer → Host & Play 进入，设置测试密码。记录世界/角色名称及开始时间，不复用 `.local/m12-a`、`.local/m12-b`。
4. 测试后正常 Save & Exit 并完全退出。保存 profile 和下面的日志证据即可停用：以后不使用该 profile。无需删除或回滚用户正常配置。
5. 如果从某个专用实验 profile 备份恢复，退出所有相关 tML 进程，先将当前实验目录完整复制到另一个新归档路径，然后把备份复制到**另一个空目录**，用 `-tmlsavedirectory <恢复副本>` 启动；不在已有 Worlds/Players 上覆盖恢复，不执行递归删除。

## G1 人工复核：当前版本预期阻塞，不能通过

1. 只有 host 真人窗口时先执行 `/vplayer status` 和 `/vplayerlocal`，记录时间。服务器日志应为 netMode=2、一个真实 connected slot；客户端本地快照 netMode=1。主机不是服务器的 Main.LocalPlayer。
2. 执行 `/vplayer spawn`，再执行 `/vplayer status`。当前包预期 `spawn-refused`、G1=blocked、actorSlot=-1，并给出 candidate 或“无空槽”。不会出现新角色，也不会增加 RemoteClient 连接。若返回 unsupported-engine，先核对版本/哈希，不能关闭检查。若权限被拒绝，保存 host predicate 失败证据，不能放宽授权。
3. 真人手动左右走、跳跃，间隔约 5 秒各执行一次 status/local。记录真人位置/速度和 Pre/PostUpdate 次数。当前探针不应接管输入；此项仍需人工观察，不因离线夹具的真人槽位测试就标 live pass。
4. 尝试 move，再连续执行两次 despawn。预期前者拒绝、后者幂等。正常退出/重进世界后 session 必须变化、candidate=-1。保存 teardown。此项仅验收负向探针的行为，仍不满足 G1 成功条件。

G1 真正的放行标准：服务器上出现**另一个真实 Main.player[slot]**，并持续存活；其 slot 与 host 分离、没有对应已连接 Socket/第二 AI 客户端，跨多个网络循环不被清理、不争用后续真人槽位。当前代码主动不做这个不安全实验，**到此停止**。

## G2 条件式验收：本提交不执行

仅在后续独立设计解决 B1–B3、G1 在真人游戏通过后再启用。当前所有 move 命令都拒绝；下面是放行后的人工验收要求，不是本包已有能力：

1. 在新测试世界找平地、低台阶、墙和浅坑，保存 actor 与 human 的前置快照、actor 输入与原生更新计数。
2. 先静止观察重力落地，再做右移→停止→左移→停止，每段约 60–120 tick；期间真人反向移动并打开/关闭 UI，确认双方输入互不接管。后续实现必须给移动输入设置有界租期。
3. 跳跃应有负 vy、上升、顶点、下落、落地；墙应阻止穿越，低顶阻止穿顶，坑中仍有重力。对照逐 tick 或高频日志，确认位置变化来自原生 Player.Update/碰撞，不是逐帧 position 写入或 Teleport。
4. 比较前后 position/velocity、control flags、Pre/PostUpdate 和碰撞采样，记录掉线、死亡、卸载、重复 spawn/despawn 的清理结果。缺少实时物理轨迹或真人对照不得通过。

## G3 条件式验收：必须由第二个人类观察

G1、G2 未 live pass 时不得进入 G3。第二客户端只由真人操作，可在另一机器或另一真人进程；不能用 AI 客户端代替无客户端 actor。本提交没有启动它。

1. 后续通过 G1/G2 的版本在 host 上创建 actor 并开始有界移动，然后让第二位真人加入。观察端使用独立新 profile，安装相同探针/模组集，记录版本、UTC 和世界身份。
2. host `/vplayer status` 记录服务器快照，两位真人各 `/vplayerlocal` 记录本地数组。按 world、actor slot、UTC/动作顺序对应，session 只在单进程内有效，不能期待各进程 GUID 相同。actor 编号在所有快照应一致，真人连接编号应互异；服务器应只有两个人类连接，actor 无连接。
3. 两位真人同时观察 actor 左右移动和跳跃动画；比较位置/速度趋势和 body/leg frame，允许网络延迟，不要求每帧逐字节相等。屏幕上的同名角色、只有 host 能见或只有包发送日志都不够。
4. observer 退出后重新加入，覆盖晚加入全量同步；再在 actor 活动时让另一个真人连接，核查 slot 分配冲突。检查 PlayerActive/SyncPlayer/PlayerControls/PlayerSpawn 是否缺失、重复离线、错误所有权或发生重置。
5. despawn 后两端均应消失；重复 despawn、host 退出/重进、断线后应无幽灵角色。保存退出前后服务器/客户端快照与视频。只有第二真人明确看到相同 actor 运动、动画和清理才能标 `passed in live game`。

## 日志采集与实测记录模板

本机安装的 tML 日志在 `$tml/tModLoader-Logs`，包含图形客户端与 Host & Play 的服务器日志；同时检查启动输出是否使用了其他日志路径。**在重启/重新构建前复制整个日志目录**，避免覆盖/轮转。第二台机器各自复制自己的目录，不能只采 host 客户端日志。

```powershell
$repo = 'D:\Agent Project\terraria-ai-companion'
$tml = 'D:\steam\steamapps\common\tModLoader'
$evidence = Join-Path $repo ('.local/virtual-player-evidence-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $evidence | Out-Null
Copy-Item -LiteralPath (Join-Path $tml 'tModLoader-Logs') -Destination (Join-Path $evidence 'host-logs') -Recurse
Get-ChildItem (Join-Path $evidence 'host-logs') -Recurse -Filter '*.log' |
    Select-String -Pattern 'VirtualPlayerProbe|Starting tModLoader|Connection lost|Terminating|Exception' |
    Set-Content -LiteralPath (Join-Path $evidence 'probe-lines.txt')
Get-FileHash -LiteralPath (Join-Path $tml 'tModLoader.dll') -Algorithm SHA256 |
    Format-List | Out-File (Join-Path $evidence 'engine-hash.txt')
```

另存每个命令的 UTC/顺序、屏幕录像/截图、host/observer 各自进程数量与角色、世界/会话对应关系、build/verify 输出。日志中的 connected count 是服务器当时的 socket 快照，不是跨线程一致性证明；packet-attempt 的 delivered 始终 unknown。候选编号被真人占用是可能的观察结果，当前包不会改动它。

每个 gate 使用：`状态 / 引擎版本及哈希 / 环境及进程角色 / 前置快照 / 操作与时间 / 后置快照 / 服务端日志 / 各真人客户端日志与录像 / 观察结论 / 未覆盖项`。没有记录时保留 not attempted；失败时记录 blocked 和最短复现，不补写“通过”。分享日志前自行检查路径、Steam 标识和地址等个人信息。

## 对未来 ActorContext 的影响（只调查，不实施）

| 现有概念 | 将来需要明确的 ActorContext 内容 |
| --- | --- |
| `Main.LocalPlayer` | 图形客户端拥有的真人，与被执行动作的 actor 分离；不能隐式作为所有动作目标 |
| `Main.myPlayer` | 网络所有权/客户端身份，不应临时改成 actor slot 来解锁 owner-only 分支 |
| `Main.CurrentPlayer` | 原生 Update 的临时上下文；不代表连接身份或动作权限 |
| `ModPlayer` hooks | 按绑定 Player 取 actor 身份、world session 和权限；不同钩子运行范围不同，SetControls 不能覆盖所有远端 Player |
| 原生物品使用 | 必须单独调查 owner-only、目标坐标、projectile owner、库存及消息副作用；移动的结果不能外推为工具可用 |
| 多人 authority | 需要角色 slot、实例身份、session、控制权来源、输入租期及同步策略；保持真人 connection→whoAmI 约束 |

本实验不迁移上述调用，也不接入 Agent。下一步应由独立决策选择是否承担引擎生命周期改写的维护成本，再决定是否继续真实 Player；替代架构另提，不能静默改用 NPC 或额外游戏客户端。Draft PR 保持实验状态，不自动合并；恢复实现后仍须用户真实完成 G1–G3。
