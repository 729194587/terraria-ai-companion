# G2：无客户端 Virtual Player 的原生网络同步与可见性

2026-10-25。基线为 [Draft PR #11](https://github.com/729194587/terraria-ai-companion/pull/11) 的 `e5e5b2050ba7e49be520189eb08b96c18aa483b7`（G1 实现 `bac3a2d`，其后补充实测报告）。G2 使用独立分支 `codex/virtual-player-network-sync`，Draft PR 的 base 为 `spike/virtual-player-engine-hooks`。不推送 G1 分支、不改 main、不扩大 PR #11 的 diff；G1 技术报告保持原样。

| 验证层级 | 本次结果 | 证据范围 |
| --- | --- | --- |
| 离线定向验证 | 216 项通过，编译零错误/零警告 | 167 项 G1 前置/包审计及 49 项 G2 检查；不启动 Terraria 世界 |
| G2 单窗口 Host & Play | **not attempted** | 本次只准备构建/profile，尚未观察客户端画面或实际收包 |
| G2 远程多人、后加入客户端 | **not attempted** | 原生晚加入方法体仅在隔离夹具中执行，不能当作远程实测 |

G1 曾完成的单 host 存活/清理实测不转记为 G2 通过。本轮没有全量测试，也没有角色存档、挖矿、Lua、Agent、自动移动或战斗集成。

## 独立实现与原生边界

参考本地 `D:\Agent Project\terrabot\TerraPlayerBot\BotPlayerStateSync.cs` 的原生全量同步与公告隔离思路，以及 `BotCompatibility.cs` 中的原生控制/装备消息用途；没有复制反编译源码或引入其程序集。实现位于现有实验目录，仅在 G2 分支变更。

继续使用 G1 的监听前预留、原生分配器过滤、accept/network/lifecycle 共用 Gate、Program.IsMainThread + owner 校验、引用基线、quarantine 及每服务器进程一轮生命周期。只允许 Probe 与 ModLoader 共存，仍不创建 AI Socket/RemoteClient、不把连接 State 写成 10、不更改 Main.myPlayer、不通过每帧补设 active 保活。

版本仍固定 Terraria 1.4.4.9 / tML 2026.08.3.0、引擎提交和两个 DLL SHA256；具体值见 [G1 报告](VIRTUAL_PLAYER_ENGINE_HOOKS.md)。指纹失败或 IL 安装失败时拒绝 spawn，逆序撤销已装订阅。

### SyncOnePlayer 的窄 IL 适配

当前安装 DLL 的 `NetMessage.SyncOnePlayer(int,int,int)` 先读取 Player.active，再以 `RemoteClient.State == 10` 决定是否发送全量负载；无客户端 AI 的 State=0 会进入离开路径。G2 保留原生负载序列化，增加八条 IL 指令和一个 bool local：

1. 入口缓存本次 `IsNetworkActor(slot)` 判断。它要求服务器、钩子 Ready、无 fault/quarantine、预留匹配、Main.player 元素与 Actor 引用相同、whoAmI 匹配、Player 活动且未死亡，连接对象已知且完全 idle。
2. 在 `IL_0012` 的连接检查前，只有上述 bool=true 才跳转原生 `IL_0025` 全量负载。真人仍执行原 State 比较；不改连接字段，连栈上的 State 值也不伪造。
3. 原生 `PlayerLoader.SyncPlayer` 后、`IL_036f` 的公告边界前，只有该 bool=true 才返回。避免 AI 改写 RemoteClient.IsAnnouncementCompleted/Name 或冒充真人入服公告；真人公告与离线 PlayerDisconnect 路径保留。

匹配前检查静态 void/三个 int 参数、四个原生局部变量、异常区数量、精确 guard/payload/boundary/offline 偏移、字段/调用类型、唯一 State 与 SyncPlayer 引用、分支目标及末尾 PlayerDisconnect。同时支持 RuntimeDetour 的 DMD 名称及 MonoMod ILLabel。全部检查在修改指令/locals 前完成；重复补丁或不支持的结构抛出明确异常，没有宽松后备路径。

原有 On_SyncOnePlayer 不再吞掉已确认 AI 的负载：它在 Gate 内校验身份、线程并调用 orig，随后再次校验引用，计数完成的快照。非预留槽位直接 orig；预留槽位意外出现未知/真人对象时，Probe 保留隔离元数据，但原生真人同步路径仍执行。CheckSection 继续只隔离无连接 AI 的发送端区块逻辑，不伪造区块状态。

### 初次、持续与晚加入同步

- 构造时私有设置 `G2_NoClient_Probe` 的身份、橙色头发/蓝绿色衣服、铜头盔和第 0 格火把；初始空位检查移至世界出生点右侧 64 像素，最多向上寻找 12 格空位。确认原预留与原占位引用不变后才发布。
- PlayerConnect 后调用已适配的原生 SyncOnePlayer，广播 PlayerActive、PlayerInfo、PlayerControls、生命/魔力、装备、物品栏、染料、loadout 及 ModPlayer 的原生负载。参数和字段编码全部由引擎负责；没有自建身份/装备协议。
- PostUpdatePlayers 每 6 个游戏 tick 最多发送一次原生 `PlayerControls`（13），以约 10 Hz 发布当前的位置、速度、方向、控制位与 selectedItem。安装 DLL 的该 serializer 字段已离线核查。
- 每 300 tick 使用原生全量负载刷新一次外观/装备；`/vph style` 立即切换蓝绿/紫色衣服与铜/铁头盔并全量发布，`/vph resync` 强制重发。当前仅一个 actor，采用五秒全量刷新保证覆盖，尚未优化为装备变化差量协议。
- 原生 `SyncConnectedPlayer` 的 active-player 循环会调用同一个 SyncOnePlayer，传入加入者 remote id；因此无需额外接管真人加入流程即可向晚加入者定向发送当前 AI 状态。夹具验证了这条完整原生调用路径与路由，真实晚加入尚未执行。

本轮同步当前服务器状态，不产生自动行走指令，也不额外调用 Player.Update/Spawn/Teleport。AI 静止为正常情况；消息字段/发送频率通过离线检查不等于 AI 行走动画已实测。

### 离开与失败处理

正常 despawn 在原生对象和预留仍完整时发送原生 PlayerActive（14，active=0），成功构造消息后再停用对象、执行一次 PlayerDisconnect、重验身份/连接、恢复占位并释放。不会调用真人 SyncDisconnectedPlayer 来制造公告或额外断开副作用。重复正常 despawn 无新增离开消息。

第一次全量调用前就记为“可能已被客户端看到”，因为调用抛错前可能已发送部分负载。发送异常设置 terminal fault，停止后续控制/全量发布；若对象和连接仍安全，生命周期会尝试一次离开消息补偿，再按 G1 清理。若离开消息也失败、对象被替换或连接过渡态不明，保留 Player/预留并 quarantine，不覆盖未知元素，不将槽位交给真人重用。网络发送不是事务，已发送的消息不能撤回。

Full/Tick/Leave 在取得 Gate 前和内部均校验游戏线程；不在锁内排队等待游戏线程。断开回调之后的引用/活动态复核和 world/session/host 授权继续保留。quarantine 是停止条件，不是成功清理，不能原地反复重试或强行解除预留。

## 构建、定向检查和隔离目录

PowerShell 7，从仓库根目录运行（脚本支持 `-TModLoaderPath`）：

```powershell
./experiments/virtual-player-engine-hooks/build.ps1
./experiments/virtual-player-engine-hooks/verify.ps1
./experiments/virtual-player-engine-hooks/prepare-host-play.ps1
```

G2 输出目录与原 G1 分开：

- 包：`.local/virtual-player-network-build/Mods/VirtualPlayerEngineProbe.tmod`，版本 **0.2.0**，42274 字节。
- SHA256：`C82E6E9B4604E25524D0B59DAE1B7058F08F1E9C3CBDAA5F42730800E78C2696`。
- 日志：`.local/virtual-player-network-build.log`、`.local/virtual-player-network-validation.log`；原生/补丁 IL 检查产物在 `.local/virtual-player-network-validation/`。
- 已准备：`.local/virtual-player-network-host-20261025-181203/`，只部署并启用 0.2.0；尚未启动游戏或创建世界/角色。原 G1 profile、0.1.4 包与用户日志保留。

本机输出：

```text
Compilation finished with 0 errors and 0 warnings
G2NetworkSync: 49 targeted checks passed; SINGLE-WINDOW LIVE NOT RUN; REMOTE MULTIPLAYER LIVE NOT RUN.
Engine probe package: loader=2026.8.3.0; bytes=42274; DLL=71168; 167 prerequisite/package + 49 G2 = 216 total targeted checks; SINGLE-WINDOW LIVE NOT RUN; REMOTE MULTIPLAYER LIVE NOT RUN.
```

验证器使用安装随附 .NET 8 引用/运行时，执行完整原生 SyncOnePlayer、SyncOnePlayer_ItemArray、SyncConnectedPlayer，以及包内归属判定、同步包装和 NetworkStateSync 方法；仅替换引擎存储、传输与无关世界/公告效果。没有运行游戏入口、创建真实网络连接或验证字节在客户端呈现。

新增检查覆盖：原生控制 serializer 的位置/速度/方向/控制位/selectedItem；真实 DMD/ILContext 安装形态；guard、公告边界、断开调用或签名不匹配时无局部修改；完整真人同步前后负载/公告/断开效果一致；预留槽位意外为真人/替换对象时原生路径一致；AI 引用/身份/连接/线程校验；native full 负载与晚加入路由；控制限频、装备/外观重发；初始/持续/离开失败、部分发送后的离开补偿、未知对象不发送离开；实际 Spawn/Finish 在初次同步失败后回滚、离开失败时保留 actor/预留。计数代表调用/序列化完成，不是客户端收包确认。

## 用户手工验证：一个图形窗口

1. 退出上一轮 Host & Play 及其服务器，保留 Steam 正常运行。手工启动新 profile：

   ```powershell
   & 'D:\Agent Project\terraria-ai-companion\.local\virtual-player-network-host-20261025-181203\start-host-play.ps1'
   ```

   启动脚本校验包哈希，并对 enabled.json 中额外启用的 Mod 明确报错，不替用户修改启用列表。游戏内也只启用 Probe，避免重现 HEROsMod 导致的加载阻塞。
2. 新建角色及 Classic 世界，经 Multiplayer → Host & Play 进入。留在世界出生点附近。`/vph status` 应为 reserved/no actor、fault=none；服务器应有 G2 network hooks ready、armed=true 及 reserved=true 的首监听记录。
3. `/vph spawn`：回复给出 actor slot（本机通常为 254）。预期在出生点右侧约四格看到蓝绿衣服、铜头盔的 Player，悬停/名称识别为 G2_NoClient_Probe。仍只有一个真人图形窗口，AI 无客户端连接。若看不到，记录画面与两端日志，不以服务端 spawn 成功代替可见性验收。
4. `/vpc 254`（以实际 slot 替换）：这是图形客户端本地只读诊断，应显示 active=true、相同名称和服务器对应的位置/头盔。客户端日志有 client-slot-snapshot；名字仅用于诊断，服务端归属从不依赖名字。
5. 在 5、15、30 秒取 `/vph status` 与 `/vpc 254`：服务器 active/sameReference=true、connected=false、fault=none，full/control 计数增长，客户端仍可见。手动移动、跳跃、切换真人装备，确认操作对象仍是自己，AI 世界位置没有随镜头错误漂移；未测试到的 AI 运动动画另记未验。
6. `/vph style`：预期 AI 衣服变紫色、铜头盔变铁头盔；`/vpc 254` 的头盔与 client.log 中 shirt 数值应变化，真人外观不变。再执行一次应恢复。`/vph resync` 后仍是同一 AI，不应新增角色/产生真人重复加入公告。
7. `/vph despawn`：预期 AI 从画面消失；`/vpc 254` 为 active=false（名字可能保留，这是原生 inactive 的正常表现）；`/vph status` 为 actorSlot=-1、reservation=null、fault=none、left=1。重复 despawn 不重复发离开消息。本进程不再 spawn。
8. 如需验证带可见 AI 的退世界：另开全新 Host & Play 服务器、spawn 后直接正常退出世界。保存服务器 OnWorldUnload teardown 与客户端退出日志。尚未执行就保留 not attempted。

用户操作过程中，日志以当前安装目录 `D:\steam\steamapps\common\tModLoader\tModLoader-Logs` 的 client.log/server.log 为准，另存截图/录像、命令顺序与时间。出现 fault、quarantine、未知引用或明显真人同步异常就停止；本次代码不自动解除预留或重试发送。普通安全状态可 despawn/正常退出；quarantine 时保留证据后结束隔离测试进程。

## 远程多人验收（留待有参与者时）

在 AI 已存在并切换过 style 后，让另一位真人从另一机器正常加入；对方应直接获得当前 AI 身份、位置和装备，host/加入者均正常控制。加入者退出后 AI 仍存在；host despawn 后各端同时消失。检查 native-accept 的实际槽位不等于 AI 预留，原生真人公告/断开仍正常。没有真实执行就继续标 not attempted，不需要为完成本轮离线实现而临时找人。

回滚使用原 G1 构建和 profile，先结束本次服务器，不覆盖旧角色/世界。0.2.0 与 0.1.4 内部 Mod 名相同，必须用独立 profile 分别启用，不同时加载两个版本。Draft PR 不自动转正或合并。
