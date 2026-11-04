# R2 / R3：铜斧原生使用与目标格砍树

PR #25，分支 `codex/g85-server-ai-perception`，正式包 **0.1.51**。
用户提供的 0.1.50 Host & Play 记录 `runs/20261027-initial-tools/` 确认初始装备和镐挖可用，但铜斧被两个独立准入检查拒绝。本次未修改这些用户证据。

## 根因和最小修复

R2：`ActorItemUse.Supported()` 未包含 CopperAxe。将它加入已有显式白名单后，`ServerAiNativeActions.Start()` 自动复用该判断。保持原生 `ItemCheck()`、CanUseItem、单次按下、动画启动/自然结束核验、超时和输入释放。现有补丁跳过共享光标工具阶段，空挥不产生指定格砍树动作；成功仅确认一次使用周期，不能确认命中或采集。

R3：`ActorMining.SupportedTool()` 只允许纯镐；目标过滤排除斧类格；伤害和破坏归属只观察 PickTile 路径。固定版本真实原生调用链为：

```text
原生 ItemCheck -> G6 owner 工具阶段 -> ActorMining.Strike
 -> Player.ItemCheck_UseMiningTools_ActuallyUseMiningTool(item, ref hitWall, x, y)
    -> 普通格 / pick > 0：PickTile（原路径不变）
    -> tileAxe / axe > 0：计算斧伤 -> WorldGen.CanKillTile
       -> HitTile.AddDamage -> 达到 100 -> WorldGen.KillTile
       -> 原生树木连带破坏、掉落、ApplyItemTime
```

核对安装 DLL：Terraria 1.4.4.9 / tModLoader 2026.8.3.0，沿用运行时精确指纹。斧分支关键 IL 为 CanKillTile `0x028a`、AddDamage `0x02e9`、阈值 `0x02ee`、成功 KillTile `0x03c7`、未破坏效果 `0x040f`、ApplyItemTime `0x0453`。`MiningPatches.Validate()` 增加这些保护点和原生树底判断检查，未知结构拒绝安装。没有新增 IL 跳过 CanUse/CanKill 的分支。

仍调用已有原生工具委托；只扩展**铜斧**，目标同时要求 `Main.tileAxe[type]` 和 `TileID.Sets.IsATreeTrunk[type]`，拒绝普通方块、家具、虚化目标和其他斧类对象。未开放任意斧头、锤或混合工具。原生斧伤在 CanKillTile 之后由同一 Actor 的 HitTile 观察，达到阈值且本动作原生 KillTile 返回后目标为空才归属破坏。没有直接删除 Tile 或调用 KillTile 来执行动作。

保留 Player/Item 对象、world session、generation、槽位、类型、prefix、镐力校验，加入斧力和树格帧校验。保留原对象/连接/Reservation/Quarantine、互斥、80 像素保守距离及原生距离、600 次物品更新/外层 10 秒预算、取消、死亡、终态输入释放和 unknown 契约。`NoPickPower` 旧枚举名兼容保留；详情会说明斧力及保护后的零伤害。

## 回执和证据边界

| 事实 | 证据 |
|---|---|
| 动作未启动 | `Ticks=0` 或空挥 `Starts=0 / AnimationPeak=0`；失败，`effectStatus=not_started` |
| 原生挥动发生 | `Swings / Starts / AnimationPeak`，仍可能被取消、超时或失败 |
| 指定格被本动作原生破坏 | `destroyedByActor / targetAbsent / native_tile_destroyed`，终态再核验 |
| 整树倒下/消失 | 另行观察原有树格，不由单个目标为空推断 |
| 木材掉落 | 另行观察世界真实 Item 和数量 |
| 木材实际入包 | 另行查询 AI 库存增量，可以发生在动作期间；不由动画、目标变化或掉落推断 |

API 未增加“整棵树采集完成”语义。**0.1.56 修正**：移除早期探针沿用的挖掘拾取抑制，动作期间与结束当帧均保留普通玩家的原生被动拾取，见[回归证据](evidence/mining-native-pickup/README.md)。拾取不改变 `mine_tile` 的成功条件；目标终态后仍可能有其他树格、掉落或物理变化。树根/中段的连带破坏范围由原版决定。下方历史测试仍按原执行版本解读。

## 已执行测试

未运行全量测试，未调用外部模型分析服务，未启动图形客户端。

- **离线** `tests/run_server_ai_tool_tests.ps1`：19 项物品补丁 + 30 项挖掘补丁检查通过，包含真实 DLL/DMD、斧类保护点变异拒绝和无全局目标写入。
- **离线/loopback** `tests/run_server_ai_ipc_tests.ps1 -PackagePath ...`：现有 IPC、动作生命周期、取消/unknown、读取、聊天相关组通过，正式包 **49 项**检查通过。
- **隔离原生，无真人客户端**：正式包 ItemUseSelfTest 通过。铜斧 `Starts=1`、动画峰值 29、30 次物品更新后自然结束，stack=1，首帧同步不丢按下；原生 CanUse 拒绝、取消和超时均释放。
- **隔离原生，无真人客户端**：MiningSelfTest **38 项**检查通过，另包含 TreeMiningSelfTest 的细分断言。铜镐石块 46 次更新、3 次击打、进度 105 后破坏并掉石块；两次 Grass -> Dirt -> 空格回归通过。
- 同次铜斧测试对 `WorldGen.GrowTree` 真正长成的普通树砍伐：274 次物品更新、9 次挥动、13 次斧击、进度 104；8 个原有树格全部消失，世界新增木材 11。目标破坏时背包木材增量为 0；随后把诊断 AI 放到树底并运行完整原生世界帧，实际入包 5。此处只证明真实拾取发生，**不声称收齐全部 11 个木材**，也不证明自主寻路采集闭环。
- 错工具组合、家具、无效坐标、超距、CanKill/CanUse 拒绝、外部目标替换、取消、超时、原有同类型拆放/工具替换/旧代次/死亡检查通过。原生异常注入验证释放、保留 reservation、quarantine，然后只终止该诊断进程；不是恢复成功。
- 编译零错误、零警告；包和完整结果见 [R2/R3 原生证据](evidence/R2_R3_COPPER_AXE_NATIVE.json)。

诊断在 StartServer 的专用入口显式调用真实 ItemCheckWrapped，并用完整 Main.Update 验证后续掉落物物理与拾取。完整世界帧会正常更新 Tile 光标（本次 `(0,0)` -> `(5,5)`）；严格的光标隔离在物品帧边界检查，世界帧后继续检查真人对象、库存和 myPlayer。首轮旧“世界帧前后光标必须相同”断言失败，修正测试边界后最终通过；未放宽生产隔离。

定向原生重跑入口（PowerShell，单独运行，各复制一份测试世界）：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File experiments/virtual-player-engine-hooks/verify-native-item-use.ps1 -PackagePath .local/companion-g85-build/Mods/CompanionBridge.tmod -ModName CompanionBridge -Port 27881
powershell.exe -NoProfile -ExecutionPolicy Bypass -File experiments/virtual-player-engine-hooks/verify-native-mining.ps1 -PackagePath .local/companion-g85-build/Mods/CompanionBridge.tmod -ModName CompanionBridge -Port 27883
```

## 补录：0.1.51 Host & Play 基础采集实机通过

用户提供 `runs/20261027-chop-cycle/` 的 2026-10-27 实机记录，现逐字节归档于
[原始证据目录](evidence/20261027-chop-cycle/verdict.md)，16 个文件的路径、长度及 SHA-256 见
[manifest](evidence/20261027-chop-cycle/manifest.json)。源目录保留，历史回执中的 `None: ` 未重写。
这是 Host & Play 实机，区别于上文无真人客户端的隔离原生测试。

| 已通过行为 | 对应原始回执 |
|---|---|
| 新角色铜短剑、铜镐、铜斧各一件，槽 0/1/2 | [r6](evidence/20261027-chop-cycle/receipts/r6.json) |
| 铜斧 mine_tile(2103,210)，274 ticks、9 swings、13 strikes、进度104，指定树格变空 | [r8](evidence/20261027-chop-cycle/receipts/r8.json) |
| 世界中观察到 Terraria/Wood ×2，距 AI 约66像素 | [r9](evidence/20261027-chop-cycle/receipts/r9.json) |
| 导航目标 x=33668，实际到 x=33675.117，背包新增 Wood ×2，近处掉落列表清空 | [r10](evidence/20261027-chop-cycle/receipts/r10.json) |
| 铜斧 use_item 单次原生周期完成，Starts=1、AnimationPeak=29、cycleVerified=true；effectStatus 仍为 unconfirmed | [r11](evidence/20261027-chop-cycle/receipts/r11.json) |
| 铜镐挖地面 (2106,210)：Grass→Dirt→空，31 ticks、2 strikes，背包新增 DirtBlock ×1 | [r12](evidence/20261027-chop-cycle/receipts/r12.json) |

首次原生距离检查拒绝，Ticks/Swings/Strikes=0；显式导航靠近后再次发起才成功（[r7](evidence/20261027-chop-cycle/receipts/r7.json)、r8）。不把此记录概括为“任意80像素内必可达”。
Wood ×2 入包由库存增量直接确认，附近掉落清空是辅助证据；没有独立拾取动作回执。
这证明基础的分段导航、砍树、靠近拾取和工具使用闭环；不是自主寻找/规划/连续采集完整任务、整片树林清理或复杂环境验收。

## 新版回执原因修复与后续验收

**0.1.51 基础采集已由上节实机证据确认；0.1.52 的原因码修复尚未进行图形验收**，见 [回执原因说明](ACTION_RECEIPT_REASONS.md)。新构建位于 `.local/companion-g85-build/Mods/CompanionBridge.tmod`；当前启动 profile 未随本次构建自动替换。需退出游戏后部署并校验启动脚本 hash，或用现有 prepare 脚本创建新 profile。不要直接覆盖正在运行的包。下列步骤保留作新版重验入口。

1. 启动 0.1.51，进入 Host & Play，核对真实 AI 铜斧所在槽（新角色通常为游戏键 3）。停止 Agent 自动动作，下面脚本直接使用现有 Lua Runtime，不调用模型。
2. 在树外先执行空挥，观察一次原生动画和自然结束，查看结果 `Starts=1 / cycleVerified=true / inputsReleased`。不得把这次动画算作砍树。

```powershell
& .local/lua-venv/Scripts/python.exe scripts/server_ai_actions.py --use-key 3 --output .local/r2r3-host-play/axe-air.json
```

3. 用现有感知 API 确认一棵普通树的靠近底部树干坐标 `(X,Y)`，导航到目标中心 80 像素以内并满足原生距离。先记录背包；用下面命令替换实际 X/Y。历史参数 `--pick-key` 表示本次目标操作前选栏，仍可选铜斧，无 API 变更。

```powershell
& .local/lua-venv/Scripts/python.exe scripts/server_ai_verify.py --output .local/r2r3-host-play/bag-before.json
& .local/lua-venv/Scripts/python.exe scripts/server_ai_actions.py --pick-key 3 --mine-x X --mine-y Y --output .local/r2r3-host-play/tree.json
```

4. 分别记录斧头动画、指定树格和整树变化、木材世界掉落。`tree.json` 必须有 `native_tile_destroyed / destroyedByActor / targetAbsent` 和已释放终态。观察掉落后让 AI 走近，再运行只读库存脚本保存 `bag-after.json`，以 Wood 等真实掉落类型的库存增量确认拾取；没有增量就记为未拾取。
5. 独立用铜镐挖普通方块检查成功；铜斧挖普通方块、铜镐砍树应在启动前拒绝。另验收途中停止、超距、目标被真人替换和断线；失败/取消/unknown 都不得声称目标已成功采集。

未覆盖：图形远端树倒下效果/整树同步、真实延迟和自然动作墙钟节奏、复杂树根/中段/相邻树、棕榈/宝石/灰烬等其他树种、液体/斜坡/受保护世界条件、多人竞争/晚加入、所有断线时序、其他斧头和完整自主寻找并收齐掉落循环。不能保证每棵树都能在现有 10 秒预算内砍完；超时保留已发生的原生效果，不回滚或自动重试。
