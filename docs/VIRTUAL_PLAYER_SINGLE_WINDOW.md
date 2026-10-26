# 无客户端原生 Player：单窗口累计集成

2026-10-26，版本 **0.7.1**。按用户 GitHub 审计结论，在 G7 `3f51c6b3736e612f5e2bb942d75155ffbc95e152` 上创建独立分支 `codex/virtual-player-single-window-consolidation`，累计 Draft PR 的 base 为 **main**。不合并 main，不修改/关闭 PR #11–#18 的分支或 PR。

## 整理内容与来源

- 保留 G1–G7；G7 `ActorMining.cs`、`MiningPatches.cs`、`MiningSelfTest.cs` 和 G6 物品执行实现未改写。
- 移植 PR #18 的 `e964895476999478bd137b6128f876d87d7888aa`：`ItemOwnershipPatches.cs`、`NativeItemOwnership.cs`、`PickupOwnershipSelfTest.cs`、`PickupOwnershipOfflineTests.cs` 及独立诊断。继续由原生 FindOwner 决定候选和 21/22 通知，只为已验证的 AI 原持有者开放，不改真人路径、不直接赠送背包物品。
- 在 `EngineHooks.InstallServerHooks` 同时安装拾取、挖掘和 G6 钩子；拾取诊断并入已有 StartServer 完成后的诊断派发，移除 PR #18 为独立开发而增加的第二个 StartServer 包装。每组诊断仍在独立服务器中运行。
- 定向恢复 PR #12 `63f44eb67f533a4217691a5d926c6949777c58cc` 中遗失的网络测试：两项真实原生调用链检查，真人持 buffer/网络持 Gate 的交错回归，AI 晚加入与同步接收并发，错误线程拒绝及无预留原路径检查。保留后续 NativeSurvival 测试映射。G7 已有 `IsNetworkActor` 非预留快路径和 `MainNetworkTick` 的 Gate→buffer 锁顺序实现，本轮没有盲目 cherry-pick 或重复更改这些生产逻辑。
- 恢复并更新 [G2 网络报告](VIRTUAL_PLAYER_NETWORK_SYNC.md) 的 0.2.3 死锁定位、修复和当时启动握手实测；历史结果单列，不把旧图形验收转记为当前累计包通过。保留 [G6](VIRTUAL_PLAYER_NATIVE_ITEM_USE.md)、[G7](VIRTUAL_PLAYER_NATIVE_MINING.md) 和 [拾取归属报告](VIRTUAL_PLAYER_PICKUP_OWNERSHIP_FIX.md)。

未修改旧项目的原子操作框架、Lua、Python Agent、TCP 协议或调度器。仍是固定 Terraria **1.4.4.9 / tML 2026.8.3.0**、指纹校验、单模组隔离的实验包。

## 统一入口

仓库根目录，PowerShell 7：

```powershell
pwsh -File experiments/virtual-player-engine-hooks/build.ps1
pwsh -File experiments/virtual-player-engine-hooks/verify.ps1
pwsh -File experiments/virtual-player-engine-hooks/verify-native.ps1
pwsh -File experiments/virtual-player-engine-hooks/prepare-host-play.ps1
```

`build.txt` 为版本来源，`probe-paths.ps1` 为当前构建/验证/Host 路径来源。全部独立原生测试消费同一份包，不再默认误用 G5/G6 或拾取分支的旧产物。`verify.ps1` 默认运行本实验完整定向测试，包含 G6/G7 原生 IL 测试和拾取检查；原来窄验证开关保留。`verify-native.ps1 -Suite Mining`、`-Suite PickupOwnership` 等可单独运行；原有存档、生存、物品、挖掘和拾取 `verify-native-*.ps1` 仍可直接使用。

| 当前产物 | 路径 |
|---|---|
| 模组包 | `.local/virtual-player-single-window-build/Mods/VirtualPlayerEngineProbe.tmod` |
| 原生各组日志/汇总 | `.local/virtual-player-single-window-validation/` |
| 当前手动入口 | `.local/virtual-player-single-window-host/start-host-play.ps1` |
| 当前 profile 升级备份 | `.local/virtual-player-single-window-backups/` |

`prepare-host-play.ps1` 首次从完整 G7 测试 profile 复制角色/世界/AI 档案到新目录，后续换包前完整备份，保留已有存档。`prepare-mining-host-play.ps1` 保留为当前入口的兼容别名；`prepare-pickup-fix.ps1` 保留为显式指定源/新目标目录的隔离准备方式，使用当前累计包。旧 G1–G7、拾取分支 profile 与历史包均不删除。

本机已准备当前入口，**未启动图形游戏**。源 `.local/virtual-player-mining-host` 的 25 个 Players/Worlds/AI 档案文件，复制前后与目标逐文件 SHA256 一致；源文件未变，既有 profile 路径均仍存在。实验脚本与生成的启动脚本均通过 PowerShell 语法检查。

## 本次实际验证

构建 **0 errors / 0 warnings**。完整定向离线验证 **490 项 PASS**，只针对本实验，**未运行仓库全量测试**：

| 类别 | 检查数 |
|---|---:|
| G1/原生 IL/实际包/生命周期前置 | 173 |
| G2 网络同步，含恢复的调用链与并发回归 | 73 |
| G3.1 存档 | 35 |
| G4 行走 | 85 |
| G5 生存 | 45 |
| G6 物品 | 30 |
| G7 挖掘 | 26 |
| PR #18 拾取归属 | 23 |

统一原生入口 **7 组全部通过**，共启动 8 个隔离服务器进程（存档组跨两次进程），使用各自新 profile 和地图副本：

| 组别 | 实际结果 |
|---|---|
| Persistence | 原生角色保存/加载、损坏档案拒绝、跨进程恢复通过 |
| Survival | 原生拾取、部分堆叠/满背包、NPC 接触/弹幕伤害、免疫、死亡计时、复活和存档通过 |
| ItemUse | 木剑/铜镐空挥，苹果 3→2 和 1→0 原生消费；拒绝、绑定、停止、死亡释放通过 |
| ItemUseFailure | 原生异常后释放输入，保留对象/预留并 quarantine；仅终止脚本创建的进程 |
| Mining | 铜镐挖 Stone：46 次物品更新、2 次挥动、3 次镐击，HitTile 峰值 105/100；目标实际消失、世界掉落存在、邻石和有支撑的草保留；26 项检查通过 |
| MiningFailure | 原生异常释放和 quarantine 断言通过；仅终止脚本创建的进程 |
| PickupOwnership | 对照重现未修补的旧 AI 归属及缺少通知；修复后转真人候选并恰好发送 21/22 各一次；真人路径、无重复包、keepTime 和 AI 原生拾取通过 |

两个故障组的受控进程终止是预期保护行为，不记作正常卸载或故障恢复。拾取组的“真人候选”是私有 profile 中临时 Player 夹具，无真实连接、不伪造 Socket/RemoteClient；不据此宣称真实真人拾取延迟已现场复测。

本次包 SHA256：`45DE497ECC934A5860D077DF0B3E142437AFAF37B76299F3662815E046D1CCDE`。

本地证据：`.local/single-window-offline-checks.txt`、`.local/single-window-native-checks.txt`、`.local/single-window-profile-checks.json`；原生汇总 `.local/virtual-player-single-window-validation/native-All-summary.json` 绑定本次版本、包哈希、各组状态和日志路径。个人存档、日志和构建产物不提交。

`git diff --check` 通过。累计 diff 仅涉及实验目录、相关报告及 README 入口链接；生产挖掘代码相对 G7 无差异，旧分支保留。

## 未覆盖与手动复测

**当前累计包的图形 Host & Play、真人拾取延迟现场复测、远程多人/真实晚加入、客户端实际接收和显示未执行。** 受控并发夹具不是真实网络压力测试；逐次原生 API 诊断不验证自然实时帧节奏。历史 G1–G6 的用户实测按对应报告保留，不扩大为本次集成验收。Lua/Agent 适配不在本轮范围。累计 PR 保持 Draft，不合并 main。

以后手动运行当前 `start-host-play.ps1`，使用复制的测试角色/世界进入多人 Host & Play，只有一个真人图形窗口。依次检查：`/vph load`（无档案则 spawn）、`status` 与 `/vpc <slot>`；G4 `walk/stop`；G6 选真实物品后 `use/item/use-stop`；G7 选真实铜镐后 `mine <tileX> <tileY>/mine-status/mine-stop`。另让真人靠近原先更接近 AI 的怪物掉落，观察能否正常接管拾取。保留未执行项目和日志，不用服务端调用成功代替客户端目视证据。
