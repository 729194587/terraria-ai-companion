# G6：无客户端 AI Player 快捷栏与物品使用

> 本文保留 G6 / 0.6.0 的历史实现与验证范围。当前累计包、构建/验证/Host & Play 入口见 [单窗口集成说明](VIRTUAL_PLAYER_SINGLE_WINDOW.md)；旧 profile 和历史包不删除。

版本 **0.6.0**，分支 `codex/virtual-player-item-use`，以 PR #15 最新提交 `bc005238434b4149e12166b69b13f338df53e064` 为基础。独立 Draft PR；不修改 main 或 PR #11–15 的分支。本轮范围是木剑、铜镐的**空挥**和苹果消耗，不包括定点挖方块、通用物品管理或自动战斗。

## 本机调用链依据

核对本机 Terraria **1.4.4.9** / tModLoader **2026.8.3.0**，继续使用 G1 的引擎、Hooks DLL 和 tML commit 指纹。引擎 SHA256：`FCC6A9624B12191BE4A15D714A3675440911FEB8C1D374642AB1FD686F0DA0A5`。`audit-item-use.ps1` 只读导出实际 IL 至忽略目录 `.local/item-use-api-audit/`；没有引入 TerraPlayerBot 反编译源码。

1. `Player.Update(int)` 在原生物品阶段调用 `ItemCheckWrapped(int)`。包装函数包含手柄光标、SmartCursor、LockOn、配方刷新等全局 UI 行为。AI 在此调用位置执行一次 `Player.ItemCheck()`，保留 `PlayerLoader.PreItemCheck/PostItemCheck`；真人仍调用原包装函数。
2. `ItemCheck_Inner()` 的非本地分支先递减动画/时间，再回到共用的开始使用判定。`controlUseItem && releaseUseItem && itemAnimation == 0` 经原生 CanUseItem 后进入 `ItemCheck_StartActualUse`、`ApplyItemAnimation`、`CombinedHooks.TotalUseAnimation`。不需要放开整段本地输入分支。
3. `ApplyItemTime` 经原生 UseItem、CombinedHooks.TotalUseTime、SetItemTime，递增 ItemUsesThisAnimation。食物会走这一路；木剑、铜镐无目标空挥的 itemTime 为 0，不能把它改成物品 useTime 来制造“成功”。完成由真实动画、itemTime、reuseDelay 自然归零决定。
4. `ItemCheck_OwnerOnlyCode` 在 `0x017d..0x018a` 因 myPlayer 身份返回。AI 例外进入原生 channel/toolTime 更新段，再跳到原生近战段；跳过光标驱动的工具、放置和配饰弹幕区域。保留原生近战命中盒、命中冷却与伤害调用，但本轮未单独验证武器伤害。铜镐只验证空挥，不调用依赖全局 tileTarget 的挖掘入口，不改写真人光标目标。
5. `ItemCheck_Inner` 的食物 Buff（`0x071d..0x0728`）及消耗段（`0x166c..0x1677`）仍有本地限制。仅为正在执行且完整验证的 AI 开放；继续由原生 AddBuff、ItemLoader.ConsumeItem、数量扣减和空物品清理决定结果。最后一个消耗品的 Main.blockMouse 写入被 AI 专属分支跳过；快捷栏限制为 0–9，不进入鼠标物品槽 58 的分支。
6. 原生消息 **13 PlayerControls** 同步 selectedItem、controlUseItem、位置/速度，由远端原生更新驱动动画；**41 ShotAnimationAndSound** 序列化 itemRotation 和动画数，但当前 tML 接收端明确丢弃动画数，不能声称它直接同步动画计数。动作开始确认后、期间和结束发送原生包，结束通过现有 G2 Full 更新物品/Buff。被原生规则拒绝的动作不会发送按下输入。客户端可见效果仍待实测。

## 绑定、取消与存档保护

`ActorItemUse.Context` 捕获实际 Player/Item 引用、WorldSession、ActorGeneration、selectedItem。命令排队时已捕获角色身份；执行命令与原生更新均在游戏线程、G1 Gate 内。原生例外还必须处于 G5 NativeSurvival 线程局部作用域，确认 Main.player 原位引用及完整 idle 连接状态。所有 IL 修改先校验方法结构、分支目标、关键字段/调用和异常区；不匹配拒绝安装，沿用现有 rollback。

- 只允许 WoodenSword、CopperPickaxe、Apple，单次按下，最多 180 次物品更新；走路/切栏/再次使用与正在进行的物品动作互斥。
- 不伪装 Main.myPlayer、Main.LocalPlayer、客户端连接；不手工调用 Player.Update，不用手工扣物品、补 Buff 或设置动画来声称使用成功。
- 完成时由原生计数自然结束；取消时清除自己拥有角色的输入、channel 和使用计数。死亡在原生 KillMe 前取消；G1 销毁前也取消。物品对象被替换则停止，不覆盖替换物。过期代次不写新角色；未知归属保留预留并 quarantine。
- 原生异常可能发生在部分消费之后：释放输入、quarantine，不重试或逆向回滚未知物品事务。G5 的 quarantine 禁止进程内卸载/恢复规则继续有效。
- 进行中的使用禁止角色保存，并在设置 failedSave 标记之前拒绝；已完成的原生背包状态仍走 G3.1 存档。原有存档机制没有改成自定义物品序列化。
- `/vph item-fixture` 是显式测试数据：只向三个空快捷栏放入木剑、铜镐、苹果×3，缺少空栏就拒绝，不覆盖已有物品。默认 spawn/load 不赠送测试物品，不应用 debug-style。显式加入的物品是真实背包数据，随后 `/vph save` 会保存它们。

## 验证结果与边界

已执行构建：**0 errors / 0 warnings**。没有运行仓库全量测试。

已执行 `verify.ps1 -ItemUseOnly`：**30 项 G6 定向检查 + 180 项实验包/生命周期/G2 定向检查**。覆盖实际 DMD/ILContext、结构变异与重复 patch 安全拒绝、真人原指令保留、上下文绑定、单次原生入口、Gate/异常释放、默认不初始化物品、死亡/销毁接入、保存拒绝和实际消息 41 接收行为。

已执行两个独立原生服务器诊断，使用复制地图及全新私有 profile，无图形客户端或真人连接：

| 原生动作 | 动画峰值 | itemTime 峰值 | 物品检查调用数 | 数量 |
|---|---:|---:|---:|---|
| 木剑空挥 | 20 | 0 | 21 | 1 → 1 |
| 铜镐空挥 | 22 | 0 | 23 | 1 → 1 |
| 苹果 | 17 | 17 | 18 | 3 → 2，原生 WellFed |
| 最后一个苹果 | 17 | 17 | 18 | 1 → 0，原生清空槽 |

每次只启动一次动画，正常完成后动画/时间及使用输入归零。正常诊断还验证显式停止、死亡停止、原生 CanUseItem 拒绝、过期排队代次、执行代次失效、物品对象替换，以及全局身份/鼠标/目标、另一 Player 引用和背包不变，并正常保存退出。

故障诊断通过原生 GlobalItem.CanUseItem 注入异常，确认输入释放、角色原位保留、数量不变、预留保留和 quarantine。诊断专用 hook 默认无行为，只有显式服务器诊断能启用。该场景按设计不能正常卸载，由脚本仅终止自己启动的隔离进程，**不把这项记为正常退出或故障恢复成功**。

诊断在 StartServer 后、首个世界帧之前显式建立测试环境，在 G5 作用域中逐次调用真实原生物品入口；这证明服务器原生 API 和计数行为，不证明实时世界帧节奏或客户端显示。离线检查另核对了真实 Update 的调用位置。G1–G5 未改的全部能力没有在本轮重跑真实游戏。

**单窗口 Host & Play：未执行。远程多人：未执行。实际挖掘、近战伤害、晚加入时正在进行的挥动、真实存档后的物品使用往返：未验证。** 客户端可能存在时序/显示差异，不能凭服务器结果标记可见性通过。

证据：`.local/item-use-offline-checks.txt`、`.local/item-use-native-checks.txt`、`.local/item-use-native-failure-checks.txt` 及其中列出的隔离 profile/result JSON。

## 构建与简短 Host & Play 步骤

包：`.local/virtual-player-item-use-build/Mods/VirtualPlayerEngineProbe.tmod`。

SHA256：`6120CD215CFDB4D723F196BBCDB7A6940CA4FB9F5AE6C8648EE4F8D882DF0E6C`。

`prepare-host-play.ps1` 创建独立 `.local/virtual-player-item-use-host`，首次复制完整 G5 测试角色、地图和 AI 私有档案，后续不覆盖存档。它不启动图形客户端。原 G5 profile 与启动入口保留。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ".local/virtual-player-item-use-host/start-host-play.ps1"
```

以后有空时，用同一个角色、地图进入 **多人 → Host & Play**，保持真实 host 客户端在线且只开本实验模组：

```text
/vph load
/vph item-fixture
/vph slot <聊天里木剑的栏号>
/vph use
/vph item
```

没有 AI 档案时用 `/vph spawn` 替代 load。每次 use 等待约一秒，再查看 item：应显示“已完成”，当前动画/时间 `0/0`。随后对提示的铜镐、苹果栏分别重复 slot/use/item；观察原生挥动、苹果数量减少一次。铜镐不应被当作已经验证的挖方块功能。可用 `/vph use-stop` 停止，或 `/vph test-death` 检查死亡停止及原有复活；复活后必须发起新动作。需要保留实际背包变化时，在动作结束后 `/vph save`。

可重现命令：

```powershell
pwsh -File experiments/virtual-player-engine-hooks/audit-item-use.ps1
powershell -File experiments/virtual-player-engine-hooks/build.ps1
pwsh -File experiments/virtual-player-engine-hooks/verify.ps1 -ItemUseOnly
pwsh -File experiments/virtual-player-engine-hooks/verify-native-item-use.ps1
pwsh -File experiments/virtual-player-engine-hooks/verify-native-item-use.ps1 -Failure
```
