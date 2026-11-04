# R1：服务器 AI 新角色初始化

正式包版本：CompanionBridge 0.1.50。
后续 0.1.51 Host & Play 已确认新角色铜短剑、铜镐、铜斧各一件，见 [实机 r6 回执](evidence/20261027-chop-cycle/receipts/r6.json)；该结果不替代所有存档/重生边界验收。

## 修复边界

`ProbeSession.Spawn()` 优先消费 `CharacterStore.Load()` 准备的原生档案；只有没有档案的新角色才调用 `CreateNewCandidate()`。
固定引擎 Terraria 1.4.4.9 / tModLoader 2026.8.3.0 的 `Player` **没有 `SetDefaults()` 方法**。构造函数已经调用 `PlayerLoader.SetupPlayer` 初始化 ModPlayer 和基础属性，但不设置初始库存。初始装备来自 `UICharacterCreation.SetupPlayerStatsAndInventoryBasedOnDifficulty()` 的非 Journey 分支。

因此直接初始化尚未发布的 Player：构造完成后设置 softcore difficulty=0 和沿用的名称 `G31_NoClient_Probe`，依次设置 `CopperShortsword`、`CopperPickaxe`、`CopperAxe`，按原版调用物品 `Prefix(-1)`，初始化新角色 creative 数据，并调用 `PlayerLoader.GetStartingItems(..., false)` / `SetStartInventory()`。不构造图形创建角色界面，不激活角色文件，不使用 LocalPlayer。

所有初始化都在 `CharacterIo.Run` 隔离作用域中完成，检查全局玩家、活动档案和 myPlayer 未改变；验证角色模式、名称、大小、生命、死亡状态和三格库存。之后才设置预留槽位、出生点旁的无碰撞位置和运动状态，并复用原有 lease/placeholder/connection 二次验证、generation 递增及 connect 同步流程。构造函数和库存 ModPlayer 钩子均不在身份绑定后再次执行。

加载路径保留原生 `.plr/.tplr` 的库存；重复 spawn 仍直接返回已有对象；死亡仍由 `NativeSurvival` 对同一对象调用原生死亡计时和 `Spawn(ReviveFromDeath)`。不增加复活补发、空槽修复或旧档案迁移。

持久化仍采用既有显式 save / load 路径：保存后通过 load 恢复同一档案，包括重启或先 despawn 后重新加载。无档案的 spawn 表示创建新角色，不表示恢复之前未保存的角色；本次未增加自动保存/自动选择档案。已有空背包档案不会自动获得三件工具。已有引擎隔离限制仍只允许本 Mod 和 tModLoader。

## 定向验证

未运行全量测试。构建零错误、零警告。定向 IPC/原子动作/读取/聊天测试和正式包 46 项 IL 检查通过。

隔离原生验证使用正式 `.tmod`、复制的测试世界和独立档案，通过两个进程验证：

- 新角色前三格顺序、stack=1、softcore、名称、槽位、生命和活动状态；重复 spawn 保持同一对象及完整状态。
- 原生 KillMe、完整原生死亡倒计时和重生，确认同一对象、generation+1、三件装备各一件。
- 主动移除斧头，保存后两次 despawn/load，再启动第二进程加载；确认修改保留、不补发斧头，重复 spawn 状态不变。
- 沿用保存失败保留活角色、损坏档案拒绝及 character IO 全局隔离检查。

结果见 `docs/evidence/R1_NEW_CHARACTER_NATIVE.json`。重跑隔离验证：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File experiments/virtual-player-engine-hooks/verify-native-persistence.ps1 -PackagePath .local/companion-g85-build/Mods/CompanionBridge.tmod -ModName CompanionBridge -StarterInventory -WorldPath .local/virtual-player-control-host/Worlds/test.wld -Port 27875
```

## 真实游戏验收

新包位于 `.local/companion-g85-build/Mods/CompanionBridge.tmod`，需要部署到 Host & Play 实际使用的独立 profile；原启动 profile 不会自动替换。本次未启动图形客户端。

使用新包创建新 AI，检查库存前三格及客户端装备显示，再执行死亡重生检查。保存档案、重新载入及重新召唤应使用同一档案，检查工具仍各一件；可以先移动或移除一件工具，确认加载不会恢复成初始模板。Host & Play 的客户端同步和真实聊天模型操作仍待游戏验收。装备存在不代表已经扩展了 `use_item` 执行白名单，本次没有修改动作能力。
