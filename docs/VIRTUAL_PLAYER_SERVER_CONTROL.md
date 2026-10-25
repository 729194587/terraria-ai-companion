# G4：服务器侧 AI Player 最小控制原型

版本 **0.4.0**，分支 `codex/virtual-player-server-control`。从 PR #13 的指定提交 `c043b99d021b19bc55e21ed2590e5edd4d5c7f8d` 直接分叉；不修改 main 或 G1/G2/G3 分支。独立 PR 保持 Draft。

本轮仅实现固定短距离向右移动、完成/超时和主动停止。复用 G1 发布/销毁、G2 原生同步、G3.1 档案，不添加寻路、战斗、挖矿、Lua、Agent、角色管理 UI 或新存档格式。

## 实际更新机制与注入依据

核对已安装 tModLoader **2026.8.3.0** / Terraria **1.4.4.9** DLL，继续使用 EngineHooks 中已固定的 engine/hooks SHA256 与 tML commit；未知 DLL 会拒绝安装 hooks。未照搬客户端输入实现，也未引入反编译代码。

本机 IL 调用链：

1. `Main.DoUpdateInWorld`：先 `SystemLoader.PreUpdatePlayers()`，再循环 active Player，通过 `Player.Update(index)` 更新，随后 `SystemLoader.PostUpdatePlayers()`。原生调用点为 IL `0x004f`。
2. `Player.Update(int)`：`0x09ae` 在 index != Main.myPlayer 时跳至 `0x1913`，跳过本地输入段；`PlayerLoader.SetControls(Player)` 位于被跳过的 `0x1010`。因此给服务器 AI 覆盖 `ModPlayer.SetControls()` 无法注入本地输入。
3. `PlayerLoader.PostUpdateRunSpeeds(Player)` 位于 `0x4649`，紧接着 `0x464f` 调用 `Player.HorizontalMovement()`；这一阶段在上述本地输入段之外，可以用于服务器 AI。
4. `HorizontalMovement()` 读取 controlRight、runAcceleration 等字段进行原生水平加速。普通水平加速段没有 myPlayer 限制；函数末尾独立的割草机分支有本地玩家限制，不把整个函数误称为“无本地分支”。
5. `PlayerLoader.PreUpdateMovement(Player)` 在 `0x8232`，已晚于水平加速，不选择它作为方向输入注入点。普通干地随后在 `0x82d6` 调用 `DryCollision(bool,bool)`。后者调用原生 TileCollision，更新 Player.position，且没有 myPlayer/netMode 门槛。
6. `NetMessage.SyncOnePlayer` 的 `0x005b` 准备消息 13，在 `0x0073` 调用原生 SendData；G2 的 Full 可以同步位置/速度/控制位，现有 SendData serializer 检查覆盖这些字段。

选择：`ProbePlayer.PostUpdateRunSpeeds()` 只向正在受控且已确认归属的 Player 注入输入。服务器安装 `On_Player.Update` 包装器，围绕自然原生调用观察状态和执行 finally；**没有额外调用 Player.Update、没有写 Main.LocalPlayer/Main.myPlayer、没有直接改 position 或 teleport**。未添加新的控制 IL 补丁。
审计快照保存在本机 `.local/control-api-audit/`；定向测试重新检查安装 DLL 的关键调用和边界。

## Actor 上下文与线程边界

`ActorAction<Player>` 绑定：

- 实际 Player 对象引用，必须 ReferenceEquals；不是名字、槽位或存档 ID。
- `ProbeSession.WorldSession` 世界会话。
- `ProbeSession.ActorGeneration` 运行时角色代次。每次 G1 发布前递增；即使加载同一份档案，也不能复用前一轮上下文。它与磁盘存档的文件代次是不同概念，不写入角色档案。

`walk` / `stop` 在排队时捕获实际引用、世界会话和角色代次，到游戏线程执行时重新校验；旧世界或旧对象的排队命令不能作用于占用同一槽位的新角色。
所有控制写入必须在引擎 MainThread 和 G1 生命周期 owner 线程，持有同一个 Reservation.Gate。控制包装器在完整 AI 更新期间持 Gate，沿用 G2 的 Gate→MessageBuffer 锁顺序。真人 Update 调用直接透传，包含其异常行为。

每次受控更新前、注入时、更新后和释放前检查对象/世界/代次、Main.player 引用、槽位身份与原生连接完整 idle 状态；健康检查复用 G2 Require。死亡、ghost、冻结、石化、蛛网束缚、tongued、shimmering、坐骑、睡眠/坐下、电影控制、退出中或非有限物理数值均不继续动作。

## 确定性动作与停止

`/vph walk` 固定目标为起点向右 **64 px（4 格）**，最多 **90 次受控原生更新**，不接受任意控制脚本。开始时要求水平静止，避免把已有水平惯性当作本次移动。

- 每次自然更新在选定阶段清除 left/up/down/jump/useItem/useTile，仅设置 right=true；原生代码负责加速、地形、碰撞和位置更新。
- 包装器返回后读取实际服务器 position.X，记录累计 dx 和更新次数；dx>=64 才 Completed。因单步物理推进可能略超过 64 px，不直接钳制坐标。
- 未到目标且达到 90 次为 TimedOut；遇阻不报告完成。不寻路、清理地形或自动跳跃。
- 结束、显式 stop、已知归属的角色销毁或 AI 更新异常时，清除上述控制位，并将 **velocity.X=0** 主动制动；保留 velocity.Y，不冻结重力或垂直碰撞。
- 每次更新 finally 都释放已知 AI 的输入。运行中不制动，下一次原生移动阶段重新注入，避免异常留下“按住右键”。重复 stop 不把 Completed 改成 Cancelled。
- 运行时先在输入仍可见的时点调用既有 G2 Tick，再进入 finally；否则仅在 PostUpdatePlayers 发包会看见已释放的 right 位。G2 仍每 6 个 game tick 限流，原 PostUpdatePlayers 的 Tick 不会重复发包。
- 完成/超时/stop 请求既有 G2 Full，发送最终位置、释放后的输入和水平零速度。同步故障沿用 G2 Fault 处理，不因一次动作故障重复尝试不安全的网络路径。

控制器的 Completed 代表这次动作达到距离并执行停止，不代表角色此后永久固定在世界坐标；风、碰撞、敌人击退等原生环境仍可能造成后续运动。服务器暂停时更新预算也暂停，不以墙钟秒数驱动。

动作异常只停止该已确认 AI 的控制，并记录失败；不会捕获真人的原生异常。如果更新中发生对象替换、连接过渡态或世界/代次变化，失效上下文，不覆盖未知 Player。当前世界/代次仍匹配但槽位归属异常时进入 quarantine，保留预留。此时 **不为了“释放输入”去写未知对象**。
正常 G1 Finish 先完成归属检查，再 Retire 上下文和释放输入，随后走原 G2 离开同步与 G1 占位恢复。G3.1 保存/加载不保存或恢复动作；新对象必须重新 `/vph walk`。

## 验证结果与边界

执行：

```powershell
./experiments/virtual-player-engine-hooks/build.ps1
./experiments/virtual-player-engine-hooks/verify.ps1
```

编译 **0 errors / 0 warnings**。没有运行仓库全量测试。

| 层级 | 本轮结论 |
| --- | --- |
| 定向离线检查 | **359 项通过**：239 个 G1/G2/生命周期/包检查，35 个 G3.1 文件/API 检查，85 个 G4 控制/当前 IL/包检查 |
| 控制夹具 | 直接编译生产 ActorAction/ActorControl 源码，用显式 Terraria 存储/传输替身和简单确定性物理夹具驱动；完成、停止、遇阻超时、每帧释放、最终发包参数、异线程、死亡/禁用、重复帧、日志异常、原生异常、同步异常、失效引用/世界/代次、连接过渡和未知对象不写入通过 |
| 本机引擎更新结构 | 从实际 DLL 读取并验证调用链、SetControls 本地门槛、注入点顺序、水平加速段、碰撞对 position 的原生更新、编译包不调用 LocalPlayer/手动 Update/teleport，检查通过 |
| 完整原生服务器 G4 移动 | **未执行**；夹具位移不等于实际世界物理位移 |
| 单窗口 Host & Play G4 | **未执行**；未声称客户端位置已实测同步 |
| 远程多人 G4 | **未执行** |

先前 PR #13 的单窗口持久化结果不作为 G4 移动通过的证据。本轮也未重新执行 G3 的无客户端原生持久化/故障注入测试。
G4 的简单夹具物理明确不是 Terraria physics；不以其完成距离/更新次数证明真实地形中的行走结果。尚需验证原生服务器位移、停止后速度、客户端观察与重复动作。

0.4.0 包 SHA256：`9D29167DEF64D7320142D541AB09F28A5C1A3668D9F926D7A564D7D73D0D1E15`。
构建：`.local/virtual-player-control-build/Mods/VirtualPlayerEngineProbe.tmod`。离线输出：`.local/g4-verification.txt`。

## 单窗口 Host & Play 验证

退出旧游戏，用独立 G4 入口（不是 G3 入口）。首次准备会复制已有 G3 测试角色、地图及 AI Characters 档案，后续保留同一个 profile；更新包前备份整个 G4 profile，原 G3 profile 不变。

```powershell
./experiments/virtual-player-engine-hooks/prepare-host-play.ps1
./.local/virtual-player-control-host/start-host-play.ps1
```

进入已有角色/地图的 Host & Play。使用旧档案时先 `/vph profiles`，再 `/vph load <短ID>`；本机刚完成 G3 测试的档案为 `b15f0e`。也可以用 `/vph spawn` 新建空白实验 Player。不需要 fixture、重新造角色/世界或记完整 ID。
确认 AI 前方有约 5 格可通行地面，等待站稳、退出坐骑。默认预留槽位是 254；以 spawn/load 回复为准。逐条执行：

```text
/vpc 254
/vph walk
```

观察 AI 向右走约 4 格并自行停下，约 2 秒后：

```text
/vph motion
/vpc 254
```

预期 motion 为“已完成”，dx>=64 px，更新数<=90；客户端 x 增加、vx=0、right=false。再等待约 2 秒并 `/vpc 254`，在无击退/传送带等外力下应保持水平停止。不要把 TimedOut 或 Failed 当作通过。
`walk` / `motion` / `stop` 每次只给一句简短聊天反馈；详细数据记录日志，不要求记 ID 或从大段聊天中摘数。

继续检查：

1. 再次 walk，确认第二次也能完成；可 walk 后立即 stop，确认显示“已停止”，动作不继续。若动作已完成，stop 保留“已完成”结果。
2. 在右侧已有障碍物前 walk，确认最多 90 次更新后报告超时，不穿墙或自动跳跃。
3. walk 过程中正常 despawn，再 load 同一档案，不发 walk 时 AI 不应继承旧动作。重新 walk 才创建新代次上下文。
4. 正常 save/despawn/load 后再 walk，确认角色数据沿用 G3.1，移动上下文没有混入档案。
5. 若出现 fault/quarantine、位置异常或输入不释放，停止这轮动作；保留日志，不重复尝试覆盖槽位。

## 日志取证

提供控制动作与客户端两侧记录：

- 服务器 `control-start`：world、generation、slot、起始 x、目标与预算。
- 服务器 `control-result`：同一 world/generation、实际 x/vx/right、累计 dx、更新次数、phase/reason；外层 ProbeSession 日志同时提供真实 Actor 引用匹配、同步计数和 fault。
- 图形客户端 `client-slot-snapshot`：由 `/vpc <slot>` 产生，包含实际客户端位置、速度、controlRight。

只有看到同一动作的服务器位移和 Completed/释放结果，再结合客户端前后快照与用户观察，才将单窗口 G4 对应能力标为通过。远程多人需要独立执行，不从 Host & Play 推断。
