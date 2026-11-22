# R3-C2 多 Mod 专服兼容性实施报告

2026-11-22 在 Windows 本机运行 Terraria 1.4.4.9、tModLoader 2026.09.3.1 Dedicated Server。每个组合使用复制的世界和独立存档。灾厄为 CalamityMod 2.2.4，依赖 CalamityModMusic 2.1。RecipeBrowser 为 0.12.0.3。Quality of Terraria 为 ImproveGame 1.8.2，依赖 SilkyUIFramework 0.3.0。“单独”组合包含该 Mod 的运行依赖。

## 架构变化

- G1 启动按引擎指纹和核心 Hook 校验，不再枚举允许启动的 Mod。槽位分配、连接所有权、身份同步、死亡与存档保护仍在原游戏线程和预留槽位上验证。原生变更执行一半后出现未知错误时，保留原有隔离行为。
- 启动可选适配器前记录 Hook 回滚位置。排序、制作、闪避和物品接收界面适配可以分别降级。绑定回执携带引擎版本、实际加载包和失败适配器原因；制作的查询与执行入口共同检查能力。
- 灾厄修改了 Hurt 的 IL。闪避适配器按局部玩家比较、分支及五个对应的调用点验证结构，允许代码偏移变化。主动攻击可选择暂时接触伤害为零但有基础伤害的敌人；接触威胁反射仍要求当前伤害。
- 实测 Recipe Browser 的 GlobalItem 创建和拾取回调会转发给客户端界面。专服没有该界面，原路径在制作台制作时抛出空引用异常。独立适配器验证其通知方法的四条指令，仅在界面实例为空时跳过这次通知，保留实际制作、拾取及其他 Mod 回调。匹配失败会报告原因并限制依赖该通知的能力。参见 [Recipe Browser 源码](https://github.com/JavidPack/RecipeBrowser/blob/1.4/RecipeBrowserGlobalItem.cs)。
- 已正常保存且保留槽位的角色可通过显式 spawn 重新打开本世界角色档。重新生成后发布新的 Player 引用和 generation；失败、隔离或未完成保存的会话不走该重开路径。

## 实测矩阵

| 加载组合 | 绑定、销毁、同服重新生成 | 跨进程世界及角色重载 | 采集、拾取、移动、制作 | 装备 | 基础战斗 | TCP 槽位 |
|---|---|---|---|---|---|---|
| 原版 | 通过 | 通过 | 通过 | 诊断 Mod 饰品 | 通过 | 通过 |
| Calamity | 通过 | 通过 | 通过 | 灾厄饰品及药水 | 通过 | 通过 |
| Recipe Browser | 通过 | 通过 | 通过 | 诊断 Mod 饰品 | 通过 | 通过 |
| Quality of Terraria | 通过 | 通过 | 通过 | 诊断 Mod 饰品 | 通过 | 通过 |
| 三 Mod 同载 | 通过 | 通过 | 通过 | 灾厄饰品及药水 | 通过 | 通过 |

- 每组通过正常绑定和原版物品查询。同载时在同一绑定读取 geometry 场景层，解析到 2671 个灾厄物品、47 个 ImproveGame 物品、0 个 Recipe Browser 物品。实查 ImproveGame/ActuationRodMkII 并找到一条配方。灾厄查询覆盖普通说明、Shift 扩展及 King Slime Lore。数字 ID 仅对当次加载有效。
- 玩法链通过 Lua 和 IPC 执行原生挖掘、移动、拾取木材和制作工作台，检查产物及库存。制作夹具还覆盖缺材料、缺站点、特殊环境、部分成功及过期令牌。灾厄单独及组合环境中均装备 FungalSymbiote，并验证真实药水消耗一件。其余组合使用 CompanionBridge 诊断饰品验证 Mod 槽位权限。
- 每组基础战斗均检查武器 Type 280 的原生目标击败回执。持久化夹具检查装备、173 件木材、前缀、收藏标记、热键、地图和角色标识，经过一次保留槽位的销毁与重建，并在第二个专服进程读回同一角色。死亡路径在第二轮触发。
- TCP 用例验证真人槽接受连接、满员拒绝、多余连接退出后槽位可复用，以及 AI 绑定保持。该用例在游戏握手前结束。真人验收服已启动并成功绑定 AI；操作者的 Steam 正在运行其他游戏，**真人进出、客户端看到 AI、移动与装备的客户端同步未验证**。
- 早期测试分别遇到真实 Mod 回调异常、夹具 JSON 文件短暂共享冲突和 TCP 重连超时。文件共享失败在修改夹具世界前重试；TCP 容量拆成独立场景。在修正后的单独组合服测试中，制作与 TCP 均完成。测试脚本的汇总 JSON 有嵌套深度上限，详细回执见各场景原始文件。

## 重现与证据

从仓库根目录运行以下定向入口；各次运行创建独立 profile。在同机执行多个场景时使用不同端口，或依次运行。

```powershell
./scripts/build-companion.ps1
./tests/run_server_ai_ipc_tests.ps1
./tests/verify_mod_compatibility.ps1 -Crafting
./tests/verify_mod_compatibility.ps1 -Scenario Restore
./tests/verify_mod_compatibility.ps1 -Scenario Equipment
$env:COMPANION_COMBAT_CASE='weapon-trigger'
$env:COMPANION_TRIGGER_ITEMS='280'
./tests/verify_mod_compatibility.ps1 -Scenario Combat
./tests/verify_mod_compatibility.ps1 -Scenario Network
```

本机测试驱动日志位于 `.local/r3c2-gameplay.log`、`r3c2-final-mixed.log`、`r3c2-cycle.log`、`r3c2-gear2.log`、`r3c2-combat.log`、`r3c2-calcombat.log`、`r3c2-network.log` 和 `r3c2-final-net.log`。各日志含原始 profile 路径。组合服的独立玩法证据为 `.local/companion-g82-native-ffbe05b6881e4d64a49ff1d22a456183/` 下的 `handshake.json`、`mod-queries.json`、`item-info.json` 和 `crafting-results.json`；网络证据为 `.local/companion-g81-native-f9b9c4975ff2438fb5b4bead9531ad38/result.json`。同载重建证据见 `.local/world-character-native-4404da69ec5346b5aec4a2c1f69da86f/round-1-result.json`，generation 为 2。

## 仍有边界

逐项能力仍由握手及原生检查决定。可查询 Mod 配方，现有制作执行器仅支持其覆盖的原版配方、站点和环境；Mod 回调配方仍有明确的不支持原因。商店、交易、重铸及部分个人库存操作保留其子系统限制。导航依赖已支持的玩家物理配置。战斗实测覆盖普通敌人，未验证全部 Mod 武器、Boss、客户端 UI 或所有 Mod 自定义存档字段。其他 Mod 和版本可复用相同矩阵；新增适配应定位到具体冲突回调，保留核心身份、网络和存档校验。
