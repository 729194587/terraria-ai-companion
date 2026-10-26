# G2：无客户端 Virtual Player 的原生网络同步与可见性

> 当前累计单窗口入口见 [单窗口集成说明](VIRTUAL_PLAYER_SINGLE_WINDOW.md)。本报告保留 G2 历史证据与 0.2.3 死锁修复记录；下面的 241 项及 0.2.x 包路径是当时结果，不能当作当前包的验证结果。整理分支已恢复 `63f44eb` 的两项原生调用链检查与四项并发/线程回归，当前验证重新覆盖 73 项 G2 检查。后续 G3–G7 不扩大这里记录的历史实测范围。

2026-10-25。基线为 [Draft PR #11](https://github.com/729194587/terraria-ai-companion/pull/11) 的 `e5e5b2050ba7e49be520189eb08b96c18aa483b7`（G1 实现 `bac3a2d`，其后补充实测报告）。G2 使用独立分支 `codex/virtual-player-network-sync`，Draft PR 的 base 为 `spike/virtual-player-engine-hooks`。不推送 G1 分支、不改 main、不扩大 PR #11 的 diff；G1 技术报告保持原样。

| 验证层级 | 本次结果 | 证据范围 |
| --- | --- | --- |
| 离线定向验证 | 241 项通过，编译零错误/零警告 | 168 项 G1 前置/包审计及 73 项 G2 检查；不启动 Terraria 世界 |
| G2 单窗口 Host & Play | **passed for tested subset（0.2.1）** | 两端日志确认客户端收到 AI 状态及手动离开；用户确认画面可见、debug-style 切换与恢复正常 |
| 0.2.2 黄色加入/离开提示 | **not attempted in live game** | 新功能通过离线本地化参数、颜色、去重及失败测试，尚未观察游戏画面 |
| 0.2.2 Host & Play 再次启动 | **blocked：已定位锁顺序死锁** | 接受 host 后、创建 AI 前挂起；0.2.3 已修复并通过并发回归 |
| 0.2.3 Host & Play 重试 | **passed for startup/handshake** | 用户确认正常进入原世界；日志确认新钩子安装、host 加入，无挂起警告或 Probe fault |
| G2 远程多人、后加入客户端 | **not attempted** | 原生晚加入方法体仅在隔离夹具中执行，不能当作远程实测 |
| G2 动态移动、带 AI 退世界 | **not attempted** | 本轮 AI 静止并手动 despawn，没有动态运动或带可见 AI 直接退出的验收 |

G1 曾完成的单 host 存活/清理实测不转记为 G2 通过。本轮没有全量测试，也没有角色存档、挖矿、Lua、Agent、自动移动或战斗集成。

## 0.2.1 单窗口实测记录

2026-10-25 18:37–18:40（UTC+8），用户手工运行新版隔离 profile，依次执行 status/spawn、客户端快照、两次 debug-style、resync 和 despawn/status。使用包版本 0.2.1，构建与消息机制未作现场修改。

| 项目 | 实测证据 |
| --- | --- |
| 创建与持续发布 | 18:37:42.393 spawn 于 slot 254；该 Player 在服务器 active=true、sameReference=true、connected=false、Socket=null，完整连接状态 idle；到 18:39:59.522 手动清理前生命周期约 137.13 秒 |
| 客户端状态 | 18:38:26.786 的 client-slot-snapshot 为 active=true、name=G2_NoClient_Probe，位置 (33680,3318) 与服务器一致；helmet=0，shirt RGB=(175,165,140)，没有默认测试头盔或强制测试配色 |
| 画面与测试覆盖层 | 用户明确确认“看到了，切换和恢复都正常”；据此将可见、debug-style 外观/装备切换与恢复记录为用户目视通过。未单独采集覆盖层生效期间的 /vpc 快照或录像，不将其误写为中间状态日志证据 |
| 离开与恢复后状态 | 18:39:59.522 teardown 的 reason=authorized despawn、same=true、occupied=false；18:40:11.386 客户端 active=false，helmet=0、shirt RGB=(175,165,140) 与初始值一致，保留名字为原生 inactive 的预期表现 |
| 释放后状态 | 18:40:26.656 status 为 actor=null、actorSlot=-1、reservation=null、fault=null，fullSnapshots=30、controlPackets=1371、departures=1。该会话的 Probe 事件无 fault/quarantine |

计数表示服务器的原生同步调用完成，不代表每个包均有客户端交付确认；本轮仅证明所采集的静止位置、外观/装备及离开行为。背包/染料/loadout 的不重置回归仍是离线执行证据，未在游戏内逐物品检查。远程多人、真实晚加入、AI 动态运动及 G2 带 actor 退世界保留未实测，不将本轮扩大为 G2 全部能力通过。PR 继续 Draft。

两端完整日志及提取事件仅保存在本地 `.local/engine-g2-live-single-window-20261025-184226/`，包含 client.log、server.log、两端 probe-events.jsonl 和 summary.json；不提交个人日志。

## 独立实现与原生边界

参考本地 `D:\Agent Project\terrabot\TerraPlayerBot\BotPlayerStateSync.cs` 的原生全量同步与公告隔离思路，以及 `BotCompatibility.cs` 中的原生控制/装备消息用途；没有复制反编译源码或引入其程序集。实现位于现有实验目录，仅在 G2 分支变更。

继续使用 G1 的监听前预留、原生分配器过滤、accept/network/lifecycle 共用 Gate、Program.IsMainThread + owner 校验、引用基线、quarantine 及每服务器进程一轮生命周期。只允许 Probe 与 ModLoader 共存，仍不创建 AI Socket/RemoteClient、不把连接 State 写成 10、不更改 Main.myPlayer、不通过每帧补设 active 保活。

版本仍固定 Terraria 1.4.4.9 / tML 2026.08.3.0、引擎提交和两个 DLL SHA256；具体值见 [G1 报告](VIRTUAL_PLAYER_ENGINE_HOOKS.md)。指纹失败或 IL 安装失败时拒绝 spawn，逆序撤销已装订阅。

### 0.2.1：角色数据与测试外观分离

移除 0.2.0 的 NetworkStateSync.Configure 及默认 spawn 对它的调用。默认创建只保留实验 Player 的槽位/名称、基础生命和出生位置等生命周期设置，不再覆盖发型、配色、selectedItem、背包或装备，不赠送火把/头盔。NetworkStateSync 仅消费已经初始化的角色状态并同步，Full/Tick/晚加入/Leave 不初始化或重置角色数据；现有 IL、消息类型、路由、频率及失败策略保持不变。

测试外观独立放在 ProbeDebugAppearance，只有显式 `/vph debug-style` 才将衣服改紫色并临时装备铁头盔。它保存原衣服颜色与原头盔 Item 对象，第二次调用恢复原对象（包括 stack、prefix 和附加数据），不修改物品栏或发型，不生成火把。恢复前若头盔已被其他逻辑替换，明确拒绝覆盖该对象。同步期间不会重新应用测试外观，调试覆盖层不写角色档案。

正式角色数据初始化与网络同步必须分离。既定后续方向为：**G3 优先恢复已有角色档案；没有档案时按原版新角色规则初始化**，随后交给本同步层发布。当前仍是 engine Player 构造实验，没有实现正式新角色的初始物品规则、档案加载或保存，不将 new Player 构造器等同于完整原版新角色流程。

### 0.2.2：原生黄色加入/离开提示

ProbeAnnouncements 在成功的初次全量同步及 spawn 日志之后，调用原生 ChatHelper.BroadcastChatMessage，以 `Lang.mp[19].Key` 和 Player 名称创建 NetworkText；正常清理完成断开回调且再次确认槽位/对象安全后，用 `Lang.mp[20].Key` 发送离开提示。颜色与当前 DLL 的真人公告一致，为 RGB=(255,240,20)。各客户端按自己的语言解析游戏原有文案，例如“G2_NoClient_Probe 加入了世界”及“G2_NoClient_Probe 已离开”。

该提示使用独立生命周期状态去重，继续跳过 SyncOnePlayer 中的 RemoteClient 公告状态写入。重复 spawn、resync、周期刷新、debug-style 和晚加入的状态同步不重复发送 AI 加入消息；初次同步失败的创建/回滚不发送加入或离开提示。失去归属时不替未知 Player 发消息。广播调用前先记为已尝试，部分发送后抛错也不自动重试，避免重复黄字。

聊天广播属于提示效果，失败只计入 noticeFailures 并写警告，不设置同步 fault、不撤销有效 AI、不阻塞已确认安全的清理。网络状态发布/离开包原有的严格失败与 quarantine 策略保持不变。日志新增 joinNotices、leaveNotices、noticeFailures；它们表示调用结果，不是客户端交付确认。此功能尚未游戏实测，不复用 0.2.1 的通过结果。

### 0.2.3：修复 host 加入时的消息缓冲锁死锁

2026-10-25 19:06–19:09（UTC+8），固定 profile 的 0.2.2 服务器已监听并接受 slot 0，但在首次 host 握手处理时挂起，watchdog 连续报 Server hung。此时 Actor 尚未创建，不能归因为角色/地图导入或黄色公告。用户再次开服时，旧 PID 20052 仍监听 7777，第二服务器因端口占用失败。

保存日志/存档后，使用 Microsoft dotnet-stack 8.0.547301 读取挂起服务器的实际线程栈：主线程为 `CheckBytes → GetData → SyncConnectedPlayer → SyncOnePlayer → IsNetworkActor`，持有原生 MessageBuffer 锁后等待 Gate；网络线程为 `NetworkTick → UpdateConnectedClients → RemoteClient.TryRead → AsyncReceive → ServerReadCallBack → ReceiveBytes`，持有 Gate 且同步执行接收回调，等待该 MessageBuffer。形成 `buffer → Gate` 与 `Gate → buffer` 的循环等待。此前离线 RemoteClient.Update 替身未模拟同步接收回调，单窗口已通过的会话也未触发该竞争。

修复包含两点：非预留槽位的 IsNetworkActor 在取得 Gate 前直接返回 false，真人判断不触碰该锁；服务器新增 On_Netplay.UpdateInMainThread 包装，在原生 CheckBytes 获取缓冲锁之前取得 Gate，与网络线程统一为 `Gate → buffer`。这也覆盖 active AI 被原生晚加入循环同步的路径，不能只靠真人快速路径掩盖晚加入时的同类风险。无预留时直接调用 orig；有预留时校验 owner/Program.IsMainThread，保留原生接收、发送及真人同步逻辑。

新增完整安装模块调用关系检查，确认服务器 CheckBytes 位于 UpdateInMainThread 范围；实际包内并发回归复现缓冲锁与 Gate 交错，验证真人判断无需等 Gate，AI 晚加入与同步接收在统一锁顺序下完成，并检查错误线程拒绝、释放预留后原生路径保留。测试用有超时的缓冲锁获取避免回归自身永久挂起，不冒充真实联机压力测试。

日志、线程栈及当时的角色/世界备份位于 `.local/engine-g2-hang-20261025-191137/`；仅结束了已确认挂起的测试服务器 PID 20052，后续确认旧图形客户端已退出、端口释放。0.2.3 已备份并部署到固定 profile，12 个角色/世界文件哈希未变。启动脚本新增 7777 监听检查，遇到遗留服务器时给出占用 PID，避免继续重复启动。

用户随后重试并确认正常进入原世界。19:23:19 日志确认 0.2.3 及 UpdateInMainThread 钩子安装；19:23:33 两端分别记录 host 已加入和 Entering world，原 Classic 世界仍为 4200×1200。服务器 PID 26204 的保存日志中已有一次 AI spawn、无 Server hung 警告或 Probe fault。这是修复后真实启动/握手通过的证据，不能扩大为黄色提示画面、远程晚加入或动态运动均已通过。两端日志、Probe 事件及摘要保存在 `.local/engine-g2-hangfix-live-20261025-192424/`。

### SyncOnePlayer 的窄 IL 适配

当前安装 DLL 的 `NetMessage.SyncOnePlayer(int,int,int)` 先读取 Player.active，再以 `RemoteClient.State == 10` 决定是否发送全量负载；无客户端 AI 的 State=0 会进入离开路径。G2 保留原生负载序列化，增加八条 IL 指令和一个 bool local：

1. 入口缓存本次 `IsNetworkActor(slot)` 判断。它要求服务器、钩子 Ready、无 fault/quarantine、预留匹配、Main.player 元素与 Actor 引用相同、whoAmI 匹配、Player 活动且未死亡，连接对象已知且完全 idle。
2. 在 `IL_0012` 的连接检查前，只有上述 bool=true 才跳转原生 `IL_0025` 全量负载。真人仍执行原 State 比较；不改连接字段，连栈上的 State 值也不伪造。
3. 原生 `PlayerLoader.SyncPlayer` 后、`IL_036f` 的公告边界前，只有该 bool=true 才返回。保留 AI 的原生负载，但不改写 RemoteClient.IsAnnouncementCompleted/Name；AI 黄字由独立生命周期管理，真人公告与离线 PlayerDisconnect 路径保留。

匹配前检查静态 void/三个 int 参数、四个原生局部变量、异常区数量、精确 guard/payload/boundary/offline 偏移、字段/调用类型、唯一 State 与 SyncPlayer 引用、分支目标及末尾 PlayerDisconnect。同时支持 RuntimeDetour 的 DMD 名称及 MonoMod ILLabel。全部检查在修改指令/locals 前完成；重复补丁或不支持的结构抛出明确异常，没有宽松后备路径。

原有 On_SyncOnePlayer 不再吞掉已确认 AI 的负载：它在 Gate 内校验身份、线程并调用 orig，随后再次校验引用，计数完成的快照。非预留槽位直接 orig；预留槽位意外出现未知/真人对象时，Probe 保留隔离元数据，但原生真人同步路径仍执行。CheckSection 继续只隔离无连接 AI 的发送端区块逻辑，不伪造区块状态。

### 初次、持续与晚加入同步

- 构造时仅设置实验名称 `G2_NoClient_Probe` 等生命周期元数据，保留构造后的角色外观和物品数据；初始空位检查位于世界出生点右侧 64 像素，最多向上寻找 12 格空位。确认原预留与原占位引用不变后才发布。
- PlayerConnect 后调用已适配的原生 SyncOnePlayer，广播 PlayerActive、PlayerInfo、PlayerControls、生命/魔力、装备、物品栏、染料、loadout 及 ModPlayer 的原生负载。参数和字段编码全部由引擎负责；没有自建身份/装备协议。
- PostUpdatePlayers 每 6 个游戏 tick 最多发送一次原生 `PlayerControls`（13），以约 10 Hz 发布当前的位置、速度、方向、控制位与 selectedItem。安装 DLL 的该 serializer 字段已离线核查。
- 每 300 tick 使用原生全量负载刷新一次当前外观/装备；`/vph debug-style` 显式启用/恢复测试覆盖层并全量发布，`/vph resync` 只强制重发当前数据。当前仅一个 actor，采用五秒全量刷新保证覆盖，尚未优化为装备变化差量协议。
- 原生 `SyncConnectedPlayer` 的 active-player 循环会调用同一个 SyncOnePlayer，传入加入者 remote id；因此无需额外接管真人加入流程即可向晚加入者定向发送当前 AI 状态。夹具验证了这条完整原生调用路径与路由，真实晚加入尚未执行。

本轮同步当前服务器状态，不产生自动行走指令，也不额外调用 Player.Update/Spawn/Teleport。AI 静止为正常情况；消息字段/发送频率通过离线检查不等于 AI 行走动画已实测。

### 离开与失败处理

正常 despawn 在原生对象和预留仍完整时发送原生 PlayerActive（14，active=0），成功构造消息后再停用对象、执行一次 PlayerDisconnect、重验身份/连接、发送生命周期离开黄字、恢复占位并释放。不调用真人 SyncDisconnectedPlayer，原生真人同步路径保持不变。重复正常 despawn 无新增离开包或黄字。

第一次全量调用前就记为“可能已被客户端看到”，因为调用抛错前可能已发送部分负载。发送异常设置 terminal fault，停止后续控制/全量发布；若对象和连接仍安全，生命周期会尝试一次离开消息补偿，再按 G1 清理。若离开消息也失败、对象被替换或连接过渡态不明，保留 Player/预留并 quarantine，不覆盖未知元素，不将槽位交给真人重用。网络发送不是事务，已发送的消息不能撤回。

Full/Tick/Leave 在取得 Gate 前和内部均校验游戏线程；不在锁内排队等待游戏线程。断开回调之后的引用/活动态复核和 world/session/host 授权继续保留。quarantine 是停止条件，不是成功清理，不能原地反复重试或强行解除预留。

## 构建、定向检查和隔离目录

PowerShell 7，从仓库根目录运行（脚本支持 `-TModLoaderPath`）：

```powershell
./experiments/virtual-player-engine-hooks/build.ps1
./experiments/virtual-player-engine-hooks/verify.ps1
./experiments/virtual-player-engine-hooks/prepare-host-play.ps1
```

prepare-host-play 默认复用固定目录 `.local/virtual-player-network-host/`，不再每版创建空存档。首次准备时自动从已有完整 G2 实验目录复制 Players、Worlds 及相关设置；本机来源为 `.local/virtual-player-network-host-20261025-182751/`，原目录保留。之后不再导入或覆盖角色/世界，只更新模组、启动脚本和构建说明，保留 enabled.json。模组包变更前完整备份固定目录到 `.local/virtual-player-network-backups/<时间戳>/`；准备时检测到 tModLoader 运行则拒绝更新。

同版本重复测试可在同一图形客户端退出世界后重新 Host & Play，继续使用原角色和地图。升级前关闭 tModLoader 及服务器，运行 build/verify/prepare 后仍用同一固定启动入口。只有需要全新隔离基线时才显式执行 `prepare-host-play.ps1 -Fresh`，创建独立的空 profile；不把“新服务器进程”误解为必须新角色/新世界。

此次仅修改准备脚本及文档，未改变 0.2.2 模组。已核对导入、重复执行以及用旧包/启动器模拟 0.2.1→0.2.2 更新后，全部 12 个 Players/Worlds 文件与源目录逐字节哈希一致，目标包哈希与已验证构建一致，升级备份已生成，启动脚本语法检查通过。未为脚本改动重跑 C# 检查或全量测试，也未启动游戏。

G2 输出目录与原 G1 分开：

- 包：`.local/virtual-player-network-build/Mods/VirtualPlayerEngineProbe.tmod`，版本 **0.2.3**，43670 字节。
- SHA256：`5FFA59C1E9A4C6E041B5D0CAB2BE74EFB86C64AE0ADE169F885E0A47F6DD820F`。
- 日志：`.local/virtual-player-network-build.log`、`.local/virtual-player-network-validation.log`；原生/补丁 IL 检查产物在 `.local/virtual-player-network-validation/`。
- 默认入口：`.local/virtual-player-network-host/start-host-play.ps1`，部署 0.2.3，并保留已有角色/世界；无需重建。旧临时 G2/G1 profile、用户日志及升级备份保留。

本机输出：

```text
Compilation finished with 0 errors and 0 warnings
G2NetworkSync: 73 targeted checks passed; SINGLE-WINDOW LIVE NOT RUN; REMOTE MULTIPLAYER LIVE NOT RUN.
Engine probe package: loader=2026.8.3.0; bytes=43670; DLL=73728; 168 prerequisite/package + 73 G2 = 241 total targeted checks; SINGLE-WINDOW LIVE NOT RUN; REMOTE MULTIPLAYER LIVE NOT RUN.
```

验证器使用安装随附 .NET 8 引用/运行时，执行完整原生 SyncOnePlayer、SyncOnePlayer_ItemArray、SyncConnectedPlayer，以及包内归属判定、同步包装和 NetworkStateSync 方法；仅替换引擎存储、传输与无关世界/公告效果。上述 SINGLE-WINDOW LIVE NOT RUN / REMOTE MULTIPLAYER LIVE NOT RUN 仅表示该离线验证器不运行游戏入口或网络；之后用户完成的真实单窗口结果单独记录在上节，不混用两类证据。

新增检查覆盖：原生控制 serializer 的位置/速度/方向/控制位/selectedItem；真实 DMD/ILContext 安装形态；guard、公告边界、断开调用或签名不匹配时无局部修改；完整真人同步前后负载/公告/断开效果一致；预留槽位意外为真人/替换对象时原生路径一致；AI 引用/身份/连接/线程校验；native full 负载与晚加入路由；控制限频、装备/外观重发；初始/持续/离开失败、部分发送后的离开补偿、未知对象不发送离开；实际 Spawn/Finish 在初次同步失败后回滚、离开失败时保留 actor/预留。计数代表调用/序列化完成，不是客户端收包确认。

0.2.1 额外填入已有角色的名称、发型、皮肤样式、全部配色、selectedItem，以及背包/装备/染料/misc/loadout 中的自定义 type、stack、prefix 和附加数据，执行初次全量、重同步、控制包、周期刷新、原生晚加入和离开路径，逐项比较数据及 Item 对象引用不变。另审计默认 Spawn 不调用调试初始化、同步适配器无角色字段写入/SetDefaults，执行实际调试覆盖层以验证原物品保留、精确恢复和外部替换拒绝。

0.2.2 执行包内公告方法，核对原生 Lang.mp[19/20] 本地化键、Player 名称、黄色 RGB、仅排除 AI 槽位的广播路由、重复与晚加入不重播、初始创建失败无公告、失去归属无离开公告，以及部分发送后抛错时不重试且不影响 Player 生命周期。实际 Spawn/Finish 夹具另验证重复 spawn 仅一次加入通知、正常清理一次离开、初次状态同步失败时均不通知。真人原生同步/公告基线比较继续通过。

## 用户手工验证：一个图形窗口

1. 首次切到固定目录或升级模组时，先关闭上一轮 tModLoader 及其服务器，保留 Steam 正常运行，手工启动固定 profile：

   ```powershell
   & 'D:\Agent Project\terraria-ai-companion\.local\virtual-player-network-host\start-host-play.ps1'
   ```

   启动脚本校验包哈希，并对 enabled.json 中额外启用的 Mod 明确报错，不替用户修改启用列表。游戏内也只启用 Probe，避免重现 HEROsMod 导致的加载阻塞。
2. 选择已有实验角色及 Classic 世界，经 Multiplayer → Host & Play 进入；只有从未创建过存档或显式选择 -Fresh 时才创建一次。留在世界出生点附近。`/vph status` 应为 reserved/no actor、fault=none；服务器应有 G2 network hooks ready、armed=true 及 reserved=true 的首监听记录。
3. `/vph spawn`：回复给出 actor slot（本机通常为 254）。预期在出生点右侧约四格看到 Player，悬停/名称识别为 G2_NoClient_Probe；左下角出现一次原生黄色加入消息，重复 spawn 不再出现。默认外观保留 engine Player 构造值，不应有测试赠送的头盔或火把。仍只有一个真人图形窗口，AI 无客户端连接。若看不到，记录画面与两端日志，不以服务端 spawn 成功代替可见性验收。
4. `/vpc 254`（以实际 slot 替换）：这是图形客户端本地只读诊断，应显示 active=true、相同名称和服务器对应的位置/头盔。客户端日志有 client-slot-snapshot；名字仅用于诊断，服务端归属从不依赖名字。
5. 在 5、15、30 秒取 `/vph status` 与 `/vpc 254`：服务器 active/sameReference=true、connected=false、fault=none，full/control 计数增长，客户端仍可见。手动移动、跳跃、切换真人装备，确认操作对象仍是自己，AI 世界位置没有随镜头错误漂移；未测试到的 AI 运动动画另记未验。
6. 先记录默认 `/vpc 254`，再执行 `/vph debug-style`：预期 AI 衣服变紫色、临时装备铁头盔；`/vpc 254` 的头盔与 client.log 中 shirt 数值应变化，真人外观不变。执行 `/vph resync` 并等待周期刷新，覆盖层应保持；再次 `/vph debug-style` 应恢复原头盔和衣服颜色，继续 resync 不应重置它们或新增角色/产生真人重复加入公告。
7. `/vph despawn`：预期 AI 从画面消失，左下角出现一次原生黄色离开消息；`/vpc 254` 为 active=false（名字可能保留，这是原生 inactive 的正常表现）；`/vph status` 为 actorSlot=-1、reservation=null、fault=none、left=1。重复 despawn 不重复发离开包或黄字；等待五秒、resync 或 debug-style 不应产生额外加入公告。本进程不再 spawn。
8. 如需验证带可见 AI 的退世界：另开全新 Host & Play 服务器、spawn 后直接正常退出世界。保存服务器 OnWorldUnload teardown 与客户端退出日志。尚未执行就保留 not attempted。

用户操作过程中，日志以当前安装目录 `D:\steam\steamapps\common\tModLoader\tModLoader-Logs` 的 client.log/server.log 为准，另存截图/录像、命令顺序与时间。出现 fault、quarantine、未知引用或明显真人同步异常就停止；本次代码不自动解除预留或重试发送。普通安全状态可 despawn/正常退出；quarantine 时保留证据后结束隔离测试进程。

## 远程多人验收（留待有参与者时）

在 AI 已存在并显式启用 debug-style 后，让另一位真人从另一机器正常加入；对方应直接获得当前 AI 身份、位置和装备，host/加入者均正常控制。加入者退出后 AI 仍存在；host despawn 后各端同时消失。检查 native-accept 的实际槽位不等于 AI 预留，原生真人公告/断开仍正常。没有真实执行就继续标 not attempted，不需要为完成本轮离线实现而临时找人。

回滚前先结束游戏及本次服务器，可使用原 G1 构建/profile，或将固定目录的完整升级备份复制到另一空目录并通过其中的启动器运行，不覆盖当前角色/世界。0.2.x 与 0.1.4 内部 Mod 名相同，必须用独立 profile 分别启用，不同时加载两个版本。Draft PR 不自动转正或合并。
