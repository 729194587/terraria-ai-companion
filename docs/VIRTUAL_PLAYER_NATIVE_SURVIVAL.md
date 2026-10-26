# 无客户端 AI 的拾取、受击、死亡与原生重生

版本 **0.5.0**，独立分支 `codex/virtual-player-native-survival`，基于 G4 / PR #14 的 `7fb2ae5f97c80783558064f6ca3fd0418d7be6c1`。保留 G1 槽位生命周期、G2 原生同步、G3.1 档案和 G4 控制，未修改这些旧分支或 main。独立 PR 保持 Draft。

## 问题与本机 API 依据

用户实测发现怪物不伤害 AI、物品能吸附却不入包。背包数据已存在，缺少原本由所属客户端承担的执行入口。
核对对象仍是本机 tModLoader **2026.8.3.0** / Terraria **1.4.4.9**，沿用固定 engine/hooks SHA256 与 tML commit，未知版本拒绝安装。审计输出在 `.local/damage-api-audit/` 和 `.local/survival-api-audit/`，不将反编译源码加入仓库。

- `Player.GrabItems(int)` 已检查物品有效性、预留归属、延迟、容量、CanPickup、范围和碰撞，但 `0x009f..0x00a5` 只允许 Main.myPlayer 进行实际入包。
- `Player.Update` 的本地分支跳过服务器 AI 的 `Update_NPCCollision()`；该函数本身具有原生 NPC 碰撞、CanNPCHitPlayer、伤害计算、Hurt、免疫和击退逻辑。
- 普通敌对弹幕的 `Projectile.Damage()` 玩家伤害尾段排除服务器并使用 Main.myPlayer。中间同时包含友方、NPC、PvP 等路径，不能直接替换所有全局身份读取或重跑完整方法。
- `Player.Hurt(HurtInfo,bool)` 会原生扣血，但致死 KillMe 在 `0x0979..0x0984` 限制为本地玩家。上层 Hurt 的部分闪避也有身份门槛。
- `Player.KillMe` 调用 **`GetRespawnTime(pvp)`** 设置 respawnTimer，原生函数含世界难度、附近 Boss、PvP 和特殊世界规则。该函数在当前 DLL 中不是公开 API；验证通过反射调用，实际死亡仍由原生 KillMe 调用它。
- `Player.UpdateDead()` 本身会每次原生更新递减计时并运行 ModPlayer.UpdateDead，但计时结束只为本地玩家调用 Spawn。
- `Player.Spawn(PlayerSpawnContext)` 可复用原生生命恢复、出生位置、免疫与 OnRespawn。入口却无条件写 Main.LocalPlayer.creativeInterface，必须限定 AI 调用跳过；其他 UI/地图/真人文件分支继续保留原来的身份判断。
- `DropCoins()` 有同步 Main.mouseItem 的本地行为；AI 死亡掉币不能碰它。KillMeForGood 可触及真人 active file，因此硬核 AI 档案在发布前明确拒绝，本实验不实现永久死亡档案管理。

## 执行与隔离

`NativeSurvival` 在游戏线程与 G1 Gate 内建立线程局部作用域，绑定实际 Player 引用、WorldSession、ActorGeneration。每个特殊原生入口都只允许这次精确绑定；还检查 Main.player 原位引用、槽位身份和完整 idle 连接状态。没有伪造连接、没有改写 Main.myPlayer/Main.netMode、没有用 Main.LocalPlayer 驱动 AI。
作用域前后核对 Main.myPlayer、mouseItem、ActivePlayerFileData 和全部 Player 数组引用。发生异常或归属改变时 quarantine，保留角色和预留，不重复执行已部分发生的物品/伤害事务，不回滚未知对象。

### 拾取

严格校验原生 GrabItems 的结构后，给入包身份门槛增加精确 AI 例外，继续执行原生 CanPickup / OnPickup / PickupItem / GetItem。原生算法负责堆叠、剩余物、容量和物品数据。
正常 Player.Update 的这一次原生 GrabItems 就能拾取，不另造背包或再次手工入包。事务前后比较世界物品引用/type/stack/active，为实际变更发送原生 SyncItem，再通过 G2 Full 同步角色；满包未变更时不删除物品、不制造重复副本。
`/vph bag` 只读显示最多八种物品的合计数量，避免大量聊天输出；不是新的角色背包管理 UI。G3.1 仍保存原生物品和 Mod 数据。

### 受伤

- 在已确认 AI 的一次自然 Player.Update 后，仅补一次原生 Update_NPCCollision，不放开包含 UI、聊天等功能的整个本地玩家分支。
- 敌对弹幕保留原公共 Damage 的真人/NPC行为。运行时从固定 DLL 生成一个**私有 DMD**，仅保留原生 CanDamage/命中特殊状态前缀 `0..0x06bf` 与敌对玩家尾段 `0x4e43..0x515f`；移除中间的 NPC/友方/PvP 路径，两个目标槽位读取改为经过作用域验证的 AI 槽位。私有副本的服务器排除判断单独绕过，不改变全局 netMode 或公共方法。
- 保留原生伤害倍率、命中盒、Colliding、CanBeHitByProjectile、Hurt、冷却槽、闪避和 StatusPlayer，不直接减 statLife。严格检查切片边界、分支和关键调用，失败就撤销安装。
- AI 专属 Hurt/死亡作用域允许原生致死判断和闪避 hook。受伤后发送原生 PlayerHurt 和 PlayerLifeMana；真人调用直接透传。

本轮范围为 NPC 接触和普通敌对弹幕。未宣称补齐 PvP、友方爆炸、全部环境伤害或本地专属附加战斗效果（例如荆棘反伤），第三方 Mod 仍受既有隔离约束，不声称全面兼容。

### 死亡与重生

死亡使用原生 KillMe、PreKill/Kill hooks 和掉落逻辑，发送原生 PlayerDeath。AI 原生掉币时跳过 Main.mouseItem 写入，保留真人存档与 UI 分支；不调用 saveToonWhilePlaying 或 SetAsActive。
死亡先取消 G4 输入和动作，AI 仍 active 并保留原槽位/对象，进入已验证的 ManagedDead 状态。G1 不再将这种正常死亡当作丢失角色而销毁，G2 允许其身份同步。
每次自然 Player.UpdateDead 递减原生计时；到零后，调用 FindSpawn 和受隔离保护的原生 Spawn(ReviveFromDeath)，不重建角色，不覆写装备/背包，随后递增运行时 ActorGeneration，旧动作不能恢复。最后发送 PlayerSpawn 与 G2 Full。

没有另设固定重生秒数：实际 KillMe 使用同一个原生 GetRespawnTime。当前 DLL 的基线为普通模式 600 tick、专家/大师模式 900 tick；附近 Boss 情况通常为 1200/1800 tick，特殊世界和 PvP 仍由原函数决定。60 tick 名义上为一秒，暂停或卡顿不能被误认为墙钟计时错误。

死亡时原生 PlayerDeath 只发送一次；周期 Tick 不反复发送 Spawn。后加入客户端的 SyncOnePlayer 仍获取身份/装备，再发送携带**剩余 respawnTimer** 的原生 PlayerSpawn(SpawningIntoWorld)，保持死亡状态；正式复活使用 ReviveFromDeath。客户端显示与远程晚加入仍待实测。
死亡期间拒绝显式保存并提示等待复活，不把这个普通拒绝标为保存失败；复活后继续 `/vph save`。G3.1 仍为显式档案保存，本轮没有启用后台自动保存或真人存档操作。

## 验证

```powershell
./experiments/virtual-player-engine-hooks/build.ps1
./experiments/virtual-player-engine-hooks/verify.ps1
./experiments/virtual-player-engine-hooks/verify-native-survival.ps1
```

| 层级 | 结果 |
| --- | --- |
| 编译 | 0 errors / 0 warnings，0.5.0 |
| 定向离线 | **404 项通过**：239 G1/G2/包与生命周期，35 G3.1，85 G4，45 新增原生结构/身份门槛/切片检查；未运行全量测试 |
| 隔离原生服务器 API | **通过**：完整原生拾取、部分堆叠、满包、NPC 接触扣血/免疫/击退、真实敌对弹幕、致死/取消移动、原生难度/Boss计时、死亡快照、计时结束原生 Spawn、新代次、拾取物品保存/销毁/加载 |
| 新版 Host & Play | **未执行** |
| 远程多人、晚加入客户端 | **未执行** |

原生诊断只在显式 `-survivalselftest` 下运行，使用新建 workspace profile 和复制的测试世界，不读取真人角色文件。诊断通过直接调用原生死亡更新 helper 快速走完计时，确认 expected-1 次更新仍 dead/剩余 1，最后一次才复活；**没有把快速 API 循环称为真实 10 秒墙钟或完整 Host & Play 测试**。
测试还确认普通/专家及附近 Boss 的 GetRespawnTime 分别返回 600/900/1200/1800。特殊世界、困难角色掉落分支及全部物品类型未逐一实测；硬核角色明确不支持。

本次交付包 SHA256：`57CA71E6433FE6B771350FF8D532A51A9DE710D8968CD58E8647F0DC750E09EF`。
离线输出：`.local/survival-verification.txt`。
原生报告：`.local/survival-native-validation-d0ef63e5749a4fcb81b5425a0c1080bb/survival-native-result.json`，`result=passed`，所有所列能力为 true，`deathTimer=600`；hostAndPlay / remoteMultiplayer 均明确为 not executed。

## 单窗口 Host & Play

独立入口会复制已有 G4 角色、地图和 AI Characters；原 G4 profile 保留，不需要重建角色/世界。关闭旧游戏后运行：

```powershell
./experiments/virtual-player-engine-hooks/prepare-host-play.ps1
./.local/virtual-player-survival-host/start-host-play.ps1
```

使用已有角色和地图进入 Host & Play：

1. `/vph load b15f0e`（本机此前档案；其他机器用 profiles 找短 ID），再 `/vph bag`、`/vph life`。
2. 向 AI 扔少量普通石块/木材，等接触后再 bag；应入包、地面物品消失，数量对应，没有反复复制。背包数据有了不代表新增了可操作的背包窗口。
3. 让普通敌怪接触 AI，再 life，观察扣血和击退。连续重叠应遵守免疫帧。敌对弹幕另外验证，不以 test-hurt 代替。
4. 可用 `/vph test-hurt` 显式调用原生 Hurt 做诊断，遵守原生免疫；用 `/vph test-death` 显式调用 KillMe 检查死亡流程。**测试死亡会按角色难度发生正常掉币/掉落**，仅在这个隔离 profile 测试。
5. life 应显示死亡及剩余计时，控制停止。等待原生计时结束，AI 原生重生并恢复生命，再 life。不要先 despawn/load，也不要把正常等待状态当作需要重新创建角色。
6. 再次 walk 确认新代次能控制；save、despawn、load 后 bag，确认拾取物仍可恢复。怪物造成的死亡、诊断死亡和客户端视觉分别记录，不能混为同一项已验证。

新命令每次只回复一段简短状态。详细日志为 native-hurt、native-death（实际计时与原生预期）、native-respawn（新代次）、native-survival-stopped。故障时保留数据与预留，不自动重试部分事务。
