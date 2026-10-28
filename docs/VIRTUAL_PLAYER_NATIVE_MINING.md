# G7：无客户端 AI Player 定点挖掘实验

> 本文保留 G7 / 0.7.0 的实现和历史验证证据。含 PR #18 修复的当前累计包与统一入口见 [单窗口集成说明](VIRTUAL_PLAYER_SINGLE_WINDOW.md)；当前脚本不再默认构建到下面的历史包路径，旧 profile 保留。

版本 **0.7.0**。基线是 Draft PR #16 的 G6 提交 `ee6c349d67e841bb8984a055a6bb6264e7c77c95`，独立分支 `codex/virtual-player-single-tile-mining`，PR 目标为 `codex/virtual-player-item-use`。不修改 main、PR #15 或 #16。

本轮仅补齐已有 `mine_tile(x,y)` 语义的服务器执行能力与 `/vph` 诊断。旧项目的 atomic_operations、Lua Runtime、Python Agent、TCP 协议、动作生命周期与任务调度器均未重建或接入。后续通过适配层复用旧系统的结果和调度；本实验上下文不是另一套通用框架。

## 本机依据与实现

核对 **Terraria 1.4.4.9 / tML 2026.8.3.0**，沿用 G1 的引擎、Hooks DLL、tML commit 和单模组隔离限制。引擎 SHA256：`FCC6A9624B12191BE4A15D714A3675440911FEB8C1D374642AB1FD686F0DA0A5`。`audit-mining.ps1` 从实际 DLL 只读导出 IL 到 `.local/mining-api-audit/`。

参考用户提供的 TerraPlayerBot `BotWorldActions`、`BotNativeBehaviorCompatibility`、挖掘进度兼容代码，研究原生入口、累积和发布机制；未复制其实现，也未采用全局 netMode/myPlayer 改写。沿用既有 Numen/旧项目审计所确定的职责：底层只执行指定坐标，编排留给原有 Lua 系统。外部 ADR 本轮未重新获取成功，不将其计作新完成的源码核验。

实际调用链与处理：

1. 自然 `Player.Update → ItemCheckWrapped` 的 G6 AI 分支，在 G5 游戏线程/Gate/NativeSurvival 作用域内执行一次 `ItemCheck`，保留 `PlayerLoader.PreItemCheck/PostItemCheck`。
2. `ItemCheck_OwnerOnlyCode` 的 `0x01a7..0x01e8` 更新原生 toolTime；仅在 toolTime=0、动画运行、使用输入有效时进入 `ItemCheck_UseMiningTools_ActuallyUseMiningTool(Item, ref bool, x, y)`。参数来自绑定上下文，不写 `Player.tileTargetX/Y`。非自动重用镐在原生动画结束后重新按下，不伪造动画或攻击速度。
3. 原生工具方法 `0x0473` 调用 `PickTile`，`0x0493` 按 pickSpeed 调用 `ApplyItemTime`。`PickTile` 原样经过 `GetPickaxeDamage`、`WorldGen.CanKillTile`、目标转化判定、`HitTile.AddDamage` 和达到 100 后的原生破坏；镐力、抗性、保护、掉落与 tML 钩子继续由引擎决定。执行器不直接调用 `WorldGen.KillTile`。
4. 原生 `IsTargetTileInItemRange(Item)` 的克隆只把四个共享光标读取改成受线程和身份校验的私有意图坐标读取，保留原生位置、尺寸、tileRange 和真实 Item.tileBoost 算式；不替换原方法、不修改人类全局状态。
5. G6 会跳过工具/放置区域，但保留近战区域；G7 在工具阶段派发定点镐击，并额外跳过 `0x044d..0x0570` 的近战、`ItemCheck_CutTiles` 和额外弹幕，避免挥镐顺便割邻格。真人和 G6 空挥继续原路径。
6. 观察真实 HitTile.AddDamage 返回值，发布消息 **125 SyncTilePicking**，明确填入 AI 槽位。原生 PickTile 的普通破坏消息 **17** 受客户端模式限制，本实验不修改该判断，而在观察到指定原生破坏返回后用消息 **20 TileSquare** 发布实际目标状态；世界掉落由原生创建并经 G5 的消息 **21 SyncItem** 发布。挥动沿用 G6 的 **13/41**。这些服务器调用不等同客户端已接收或显示正确。

安装先验证实际方法结构、关键调用/分支与固定引擎指纹；原生结构未知即拒绝安装，沿用 rollback 和 quarantine，不使用宽松全局补丁。

主要文件位于 `experiments/virtual-player-engine-hooks/`：

- `VirtualPlayerEngineProbe/ActorMining.cs`：固定目标执行、状态、原生进度/破坏观察、发布与释放。
- `VirtualPlayerEngineProbe/MiningPatches.cs`、`ItemUsePatches.cs`、`ActorItemUse.cs`：严格 IL 核验、原生距离克隆和 G6 派发接入。
- `ProbeSession.cs`、`NativeSurvival.cs`、`ActorControl.cs`、`CharacterStore.cs`：命令、销毁/死亡取消、走路/物品/存档互斥。
- `MiningSelfTest.cs`、`validation/MiningOfflineTests.cs`：定向原生诊断与 IL/包检查；故障钩子平时无行为。

## 结果与边界

上下文记录 actionId、Player/Item 对象引用、WorldSession、ActorGeneration、槽位、固定坐标、原始类型；状态复用现有 ActionPhase 的 Running/Completed/Cancelled/TimedOut/Failed，失败原因另有枚举与详情。`mine-result` 日志含结构化结果，`mine-status` 同时报告原目标、当前世界 Tile、挥动次数、镐击次数、进度峰值及破坏证据。

Completed 要求本动作原生镐击中观察到累积阈值和指定非 fail/effectOnly 破坏返回后的空 Tile，再等待原生动画/itemTime/reuseDelay 自然归零。挥镐或部分进度不算完成；移除目标后又被放回也不完成。两次更新间的类型/形状/致动变化，以及原生 KillTile/PlaceTile 的外部介入，包括同类型拆除再放回，都会使动作失败，不接管替换物。普通方块重整造成的帧号变化不当作身份变化。直接内存写入造成的同类型、同形状、无可观测痕迹的移除再恢复，无法用当前 Tile 数据鉴别；G1 仍只允许本实验模组与 tML，未宣称任意第三方模组兼容。

早期 G7 明确支持真实非弹幕、非 channel、无斧/锤能力的快捷栏镐子，以及普通实心单格目标；拒绝家具/箱子、多格、斧锤目标、沙等下落方块和已虚化目标。后续铜斧和 Grass→Dirt 支持见 [R2/R3](R2_R3_COPPER_AXE.md)。无自动换工具、寻路、移动、邻格挖掘或捡矿编排。自 **0.1.56** 起，挖掘期间及结束当帧保留 G5 的原生被动拾取；拾取仍受原生距离、延迟、背包和所有权规则约束。早期探针为分离挖掘和拾取证据而抑制拾取，现已移除此实验限制。背包增加不构成挖掘完成证据；原生目标破坏、身份及输入释放判定保持不变。下方旧实验结果保留其当时场景；新回归与证据见 [挖掘期间拾取验证](evidence/mining-native-pickup/README.md)。

开始与停止保留游戏线程和 Gate；死亡/销毁立即释放输入与本角色使用计数。未知归属/旧代次不写 Player；原生异常不回滚部分世界事务、不重试，保留 reservation 并 quarantine。上限为 600 次物品更新，时限后 TimedOut；游戏线程停顿时不能在后台执行取消。运行期间拒绝 G4 行走、G6 使用/切栏及角色保存；取消不恢复已经原生破坏的方块。

## 已执行验证

构建 **0 errors / 0 warnings**。`verify.ps1 -MiningOnly`：**26 项 G7 + 30 项 G6 + 180 项实验包/生命周期/G2 定向检查通过**。包括实际 DMD 可执行克隆、ILContext 接入、结构变异/重复安装拒绝、无全局目标写入、无直接 KillTile、Gate 和生命周期接入。**未运行仓库全量测试。**

隔离原生服务器正常诊断 **26 项检查通过**，全新私有 profile，复制测试地图，无真人连接：

| 真实铜镐挖石块 | 结果 |
|---|---|
| 目标 | (2108,208)，Stone/type=1 |
| 原生物品更新 / 挥动 / 镐击 | 46 / 2 / 3 |
| 动画峰值 / HitTile 进度峰值 | 22 / 105（阈值 100） |
| 世界结果 | 目标真实消失，原生 StoneBlock 掉落存在，AI 背包未增加 |
| 发布观察 | 125×2、20×1、21×3（包含原生与 G5 发布，不当作三个掉落） |
| 邻格 | 相邻石块、有合法支撑的可割草均保留 |

其余通过：空格/越界/超距/错误工具/不支持目标拒绝，铜镐无法挖黑曜石，GlobalTile.CanKillTile 与 GlobalItem.CanUseItem 拒绝，停止、死亡、超时、失效代次、工具替换、类型替换及同类型拆放检测，真人对象/背包和共享光标/身份不变。正常诊断正常退出；独立异常注入诊断通过输入释放、reservation 保留及 quarantine 断言，然后仅终止其创建的隔离进程，不记作正常恢复。

诊断在 StartServer 后、首个世界帧之前显式调用真实 ItemCheckWrapped，经过已安装的原生镐击/破坏/掉落链；它不验证自然实时世界帧节奏。**Host & Play 图形、真实客户端裂纹/动画/掉落接收、远程多人、晚加入、跨存档往返、其他镐子和复杂世界情形均未实测。PR 保持 Draft。**

## 重现与手动入口

```powershell
pwsh -File experiments/virtual-player-engine-hooks/audit-mining.ps1
pwsh -File experiments/virtual-player-engine-hooks/build.ps1
pwsh -File experiments/virtual-player-engine-hooks/verify.ps1 -MiningOnly
pwsh -File experiments/virtual-player-engine-hooks/verify-native-mining.ps1
pwsh -File experiments/virtual-player-engine-hooks/verify-native-mining.ps1 -Failure -Port 27838
pwsh -File experiments/virtual-player-engine-hooks/prepare-mining-host-play.ps1
```

独立包 `.local/virtual-player-mining-build/Mods/VirtualPlayerEngineProbe.tmod`；独立手动入口 `.local/virtual-player-mining-host/start-host-play.ps1`。准备脚本只复制既有测试存档到独立 profile，不启动游戏、不修改源存档。原 G5/G6 profile 和入口保留。

本次包 SHA256：`5C583A1C7E41FEA5531E2138606FBB1FEC0BDF22C4CB2C45FCF051E488A1E7DA`。证据日志：`.local/mining-offline-checks.txt`、`.local/mining-native-checks.txt`、`.local/mining-native-failure-checks.txt`；正常原生结果在 `.local/mining-native-validation-7fb329ed86e749a2874276a3156b2299/mining-native-result.json`，异常结果在 `.local/mining-native-validation-2c1262d88ca5459fb1893f1a9248ccaa/mining-native-result.json`。

以后手动启动该入口，以原测试角色进入多人 Host & Play，只启用本实验模组。`/vph load`（无档案则 spawn），必要时 `/vph item-fixture`，按提示 `/vph slot <铜镐栏号>`。在 AI 原生距离内选一格普通石块，用世界 **Tile 坐标**执行：

```text
/vph mine <tileX> <tileY>
/vph mine-status
/vph mine-stop
```

观察原生挥动、指定块消失及世界掉落，核对状态由 Running 到 Completed；另测进行中停止、超距、铜镐挖黑曜石和 `/vph test-death`。未手动运行这些步骤，不将服务器结果标成图形验收。
