# 无客户端原生 Player：当前单窗口集成入口

当前包 **0.7.1**：G1–G7 + PR #18 拾取归属修复 + 恢复的 G2 死锁并发回归。完整范围、实际结果与历史来源见 [集成报告](../../docs/VIRTUAL_PLAYER_SINGLE_WINDOW.md)。这是实验包；图形和远程多人复测不由离线结果代替。

从仓库根目录依次运行（PowerShell 7）：

```powershell
pwsh -File experiments/virtual-player-engine-hooks/build.ps1
pwsh -File experiments/virtual-player-engine-hooks/verify.ps1
pwsh -File experiments/virtual-player-engine-hooks/verify-native.ps1
pwsh -File experiments/virtual-player-engine-hooks/prepare-host-play.ps1
```

`verify.ps1` 默认运行本实验完整定向验证，包含 G2 并发、G3.1、G4、G5、G6、G7 和拾取归属测试；不运行仓库全量测试。`verify-native.ps1` 默认顺序运行 7 组独立服务器诊断（存档组有两次进程启动），只修改新 profile 的地图副本；两个故障组按设计由脚本终止自己创建的 quarantine 进程。需要收窄时用 `-Suite Mining` 或 `-Suite PickupOwnership`，其他独立 `verify-native-*.ps1` 入口仍保留并使用当前统一包。

当前输出由 `probe-paths.ps1` 定义：

- 构建：`.local/virtual-player-single-window-build/Mods/VirtualPlayerEngineProbe.tmod`
- 验证日志/汇总：`.local/virtual-player-single-window-validation/`
- 手动启动：`.local/virtual-player-single-window-host/start-host-play.ps1`

准备脚本不启动图形游戏，首次复制完整 G7 测试角色/地图/AI 档案到新目录，后续保留存档并在换包前备份。旧 G1–G7、拾取修复 profile 和历史报告均保留；`prepare-mining-host-play.ps1` 是当前准备入口的兼容别名。

以后手动启动上述 `.local` 入口，经原生 Host & Play 进入。`/vph load`（无档案则 spawn）、`/vph status`、`/vpc <AI slot>` 检查可见性；`walk/stop`、`use/use-stop`、`mine <tileX> <tileY>/mine-status/mine-stop` 分别检查动作。拾取复测要观察真人对原先归 AI 的怪物掉落物是否能正常拾取。未实际执行的图形项目继续记为未测试。
