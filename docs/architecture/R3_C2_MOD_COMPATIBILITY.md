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

## ImproveGame 1.8.2 的服务器 AI 行为（2026-11-26）

在种子 1606771215 的自然世界副本，AI 位于槽位 254。仅向铜矿 (2175,571) 调用一次单格 `mine_target`，右侧 (2176,571) 是另一格铜矿。开启 `SimpleVeinMining` 时，两格均被破坏，背包铜矿从 0 增至 2。关闭时，右侧矿格保留，背包从 0 增至 1。两次均确认输入释放。

适配器在 ImproveGame 的连锁包接收函数读取矿工槽位时，核对游戏线程、AI 绑定、原生挖掘输入、已确认破坏的种子坐标和类型，再使用实际挥镐者的槽位。后续矿格破坏、物品生成及拾取由游戏和 ImproveGame 执行。可选钩子的结构校验失败时，绑定诊断会记录原因。单格回执仍只证明指定格的镐击。

复测入口为 `tests/verify_natural_navigation.ps1 -ImproveGameVeinMining On -WorldPath <原始世界副本>`，将 `On` 改为 `Off` 可运行对照组。可通过 `-WorkshopRoot` 指定含 ImproveGame 和 SilkyUIFramework 的创意工坊目录。

| ImproveGame 设置 | 专服 AI 核查 |
| --- | --- |
| 无 Boss 复活加速 | 同一场景的原生死亡实测，缩短比例 0% 时 600 次更新后复活，50% 时 300 次更新后复活。 |
| 进服自动加入红队 | 两组均开启，AI 初始队伍均为 0。ImproveGame 在多人客户端的进世界回调中安排加入红队。服务器 AI 可通过 `set_team(team=1)` 显式加入。 |
| 同队无限药水 | CompanionBridge 在专服读取同队在线玩家同步过来的背包，为 AI 刷新药效。队伍、距离、数量和共享开关均参与判断。 |
| 禁止墓碑 | ImproveGame 在原生 `DropTombstone` 入口检查开关并可直接返回。自然世界副本的两次受控软核 AI 死亡中，开关开启和关闭时，死亡当帧都未观察到 AI 所有的墓碑弹幕，因此该场景无法区分开关造成的差异。 |
| 背包旗帜免放置生效 | 代码核查发现，`BannerPatches.TileCountsAvailable` 在专服模式跳过背包旗帜及其队伍共享逻辑。要让服务器 AI 获得该效果也需要适配；本次未做战斗实测。 |

### 修复前的普通药效、无限药水与持续时间

使用 tModLoader 2026.9.3.1，在三个独立专服进程中测试原版铁皮药水。诊断夹具仅向 AI 的选中槽放入指定数量的药水，随后观察 360 次游戏更新，再通过 Lua 调用一次 `use_item`。夹具没有添加 Buff。每组同时读取原生角色和 `inspect_inventory(mode="full")` 的活跃 Buff、剩余时间及防御。

| 加载的 Mod 与初始数量 | 放在背包的 360 次更新 | 主动使用后的数量 | 实际防御 | 两次使用后采样之间的更新数 | 两次读到的铁皮剩余时间 |
| --- | --- | --- | --- | --- | --- |
| ImproveGame，1 瓶，门槛 30 | 从未出现铁皮 | 1 → 0 | 0 → 8 | 129 | 28,800 → 28,800 |
| ImproveGame，40 瓶，门槛 30 | 从未出现铁皮 | 40 → 40 | 0 → 8 | 132 | 28,800 → 28,800 |
| 关闭 ImproveGame，40 瓶 | 从未出现铁皮 | 40 → 39 | 0 → 8 | 130 | 28,800 → 28,800 |

两组 ImproveGame 均开启 `NoConsume_Potion` 与 `ShareInfBuffs`，AI 独自处于无队伍状态。三次主动使用均返回 `native_item_cycle_verified` 并释放输入。物品使用回执证明完成了一次使用，采样另行证明药效实际进入了该角色。

本机引擎的 `Player.UpdateBuffs` 只对 `whoAmI == Main.myPlayer` 的角色执行常规倒计时，并保留 `TimeLeftDoesNotDecrease` 例外。服务器 AI 能喝药并获得铁皮的防御，但三组都遇到普通剩余时间停滞。这个问题独立于 ImproveGame。铁皮之外，有独立计时规则的 Buff 仍需分别核查。

ImproveGame 的 `ApplyBuffItem.ConsumeItem` 会在原生使用物品时检查药水种类和堆叠门槛。背包自动生效则由另一条路径负责。`InfiniteBuffModPlayer.PostUpdateBuffs` 在专服直接返回。它的收集与应用函数还会读取 `Main.LocalPlayer`，并为符合条件的普通 Buff 刷新 30 次更新的持续时间。组队扫描也仅在多人客户端运行，按双方同属一个非零队伍和 `ShareRange` 判断，范围设为 −1 表示无限距离。[上游无限 Buff 实现](https://github.com/487666123/ImproveGame/blob/1.4.4/Modules/InfiniteBuff/InfiniteBuffModPlayer.cs) · [物品判定](https://github.com/487666123/ImproveGame/blob/1.4.4/Common/GlobalItems/ApplyBuffItem.cs) · [队伍及距离规则](https://github.com/487666123/ImproveGame/blob/1.4.4/Helpers/PlayerHelper.cs)

### 其他值得区分的设置

下表根据本机 ImproveGame 1.8.2 的实际方法核查。各项尚未逐一做游戏内开关对照。

| 设置 | 对服务器 AI 的判断依据 |
| --- | --- |
| 满血复活、死亡保留 Buff、额外 Buff 栏 | 满血复活在角色的复活及更新回调写生命和魔力。保留 Buff 修改原生死亡清理，并按 Buff 类型筛选。额外栏位由 Mod 向引擎申请。三项应分别查复活值、死亡前后 Buff 和角色槽位容量。 |
| 弹药免耗、工具加速、拾取范围、堆叠上限 | 分别有原生弹药消耗、工具使用时间、物品拾取和物品初始化回调。已有执行入口，优先用弹药差额、挥镐间隔、实际拾取距离或最大堆叠做对照。 |
| 随身增益家具、花园侏儒、水蜡烛 | 依赖随身物品扫描。普通增益可映射为 Buff；侏儒和蜡烛还涉及静态标记及场景统计，需要核验幸运或刷怪相关效果。 |
| 扩大放置距离、随身制作站及队伍共享制作站 | 放置距离逻辑检查本地玩家并修改静态范围。制作站有接收具体玩家的路径，部分环境判定读取本地玩家，共享又依赖客户端的队友扫描。应分别验证 AI 实际放置或制作是否成功。 |
| 地图标记、中键打开储物空间、快捷微光、快速护士 | 对应地图绘制、鼠标、按键或对话入口。客户端显示偏好可以留在客户端。若 AI 需要同一玩法，应验证对应动作及产物。 |
| 草药生长、城镇 NPC 刷新、晶塔环境限制 | 入口在世界更新、NPC 或晶塔规则。无须为每个 AI 复制一份规则，但 Agent 自身的前置检查能否识别规则变化仍需单测。 |

### 修复后的专服行为

普通计时进入引擎自己的倒计时与过期槽清理分支。身份检查只放行本次原生更新绑定的 AI，`TimeLeftDoesNotDecrease` 和各 Buff 的原生更新顺序继续由 Terraria 处理。导航夹具也直接观察真实迅捷到期。

加载 ImproveGame 时，服务器每 15 次更新检查一次 AI 随身物品与符合条件的在线队友背包。药水收集、药水袋展开及物品到 Buff 的映射使用 ImproveGame 的实现。适配器对合并的来源应用 AI 的禁用列表和 Mod 冲突规则，用原生 `AddBuff` 刷新 30 次更新的短药效。已经喝下的长药效继续按原时长计时。掉到门槛以下、换队、超距、断线或关闭共享后，相应来源停止刷新。本次范围是随身药水及其普通 Buff，储存网络、家具、旗帜、特殊幸运值及 AI 向真人提供药效属于其他功能。

定向回归使用独立世界和正常登录游戏端口的 tModLoader 协议客户端。供药者通过游戏包同步自己的物品、队伍和位置。断言同时检查专服接收的背包、AI 的 Buff 槽和实际防御。该客户端没有图形界面。

| 场景 | 专服结果 |
| --- | --- |
| 普通铁皮到期 | 喝药后剩余值逐更新减一。夹具把已喝下的药效调至最后 6 次更新，随后同一角色的铁皮槽消失，防御从 8 回落到 0。同时观测的锐化 Buff 保持 90，符合引擎的免倒计时规则。 |
| 自身药水、门槛 30 | 29 瓶时无铁皮，30 瓶时自动获得 8 防御，移走后回到 0。药水收在随身药水袋中也能生效。达到门槛时主动用药不消耗，移走后主动药效继续倒计时。 |
| 玩家供药、AI 空背包 | 同队相距 4 格时获得铁皮。共享半径为 50 格，移至 60 格后防御回到 0，返回范围内重新获得。供药者换队、清空药水或断线后铁皮到期。关闭共享时，同队相距 4 格也无增益，AI 自身药水照常生效。 |
| 未加载 ImproveGame | 普通铁皮正常生效和到期，主动使用从 30 瓶减到 29 瓶。 |

下列定向命令覆盖原版对照、ImproveGame 共享开关、与灾厄及 Recipe Browser 同载，以及导航中途迅捷过期。

```powershell
./scripts/build-companion.ps1
./tests/verify_mod_compatibility.ps1 -Scenario PotionBuffs -Cases Quality,Vanilla,Combined
./tests/verify_navigation_recovery_native.ps1 -Mobility -Scenes expiry
```
