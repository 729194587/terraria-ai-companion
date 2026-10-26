# 0.7.2：状态、鼠标选框与物品首帧同步

基于累计 Draft PR #19 的 `6668088`，修复单窗口实测中状态不可读、挖掘需要手算坐标及首次 use 可能不显示动画的问题。保留原有原生挖掘规则、host 授权、游戏线程/Gate、归属和异常保护，不修改 main 或 PR #11–#18。

## 如何测试

**先保存并完全退出游戏及 Host & Play 服务器。** 当前运行中的 0.7.1 包被进程锁定，尚未换成新包；准备脚本现在会在备份/复制前检查实际文件锁并明确拒绝，不强制结束进程。

PowerShell 执行：

```powershell
pwsh -File 'D:/Agent Project/terraria-ai-companion/experiments/virtual-player-engine-hooks/prepare-host-play.ps1'
& 'D:/Agent Project/terraria-ai-companion/.local/virtual-player-single-window-host/start-host-play.ps1'
```

确认模组 **0.7.2**，进入原测试世界。需要时 `/vph load`（无档案才 spawn）。

1. `/vph status`：聊天分行显示在线/死亡/隔离状态、槽位、生命、Tile 位置、选中物品和使用状态、行走及挖掘结果，不再只有英文阶段和网络计数。
2. `/vptarget`：打开目标预览。关闭聊天，把鼠标指向 AI 附近的普通石块，显示黄色方块选框、世界 Tile 坐标和类型。鼠标在聊天、背包、地图或其他 UI 上时不重选目标。
3. 保持指向后打开聊天，目标自动冻结，输入 `/vpmine`。它通过原生聊天通道提交原有 `/vph mine x y`；服务器仍重新检查 host、AI 身份、真实镐子、距离与目标。命令只提交固定一格，不寻路、不换工具。
4. `/vph mine-status` 查看结果，`/vph mine-stop` 停止；再次 `/vptarget` 关闭选框。手输坐标命令继续可用。
5. `/vpc <AI槽位>` 除立即显示客户端状态，还开启该槽位的只读物品动画观察。再测首次 `/vph use`，客户端日志将记录控制输入/动画开始和结束的 `client-item-transition`，便于与服务器结果对应。

当前客户端目标只是操作预览，不是服务器已接受或动作已完成的证明。若点到 AI 够不到的方块，服务器会拒绝，不自动移动。

## 同步修复依据

0.7.1 真实日志中首次木剑 use 于 18:16:31.933 完成，服务器动画峰值20、启动1次；反推首帧为 gameUpdate 27466，恰好落在6帧控制同步周期。原实现先发按下，再在 finally 释放输入，PostUpdatePlayers 同帧可能立刻发送松开；当前 tML 消息41又丢弃动画计数，因此服务器 completed 不能证明客户端播放了动画。日志未记录当时客户端逐帧状态，不把该推断写成图形复现证据。

现在 G6 的正常首帧保留真实使用输入到本世界帧结束，后续自然 ItemCheck 立即恢复单次按键；G7 保留正在执行的持续挖掘输入，破坏/取消后释放。所有停止、死亡、拒绝和异常分支仍立即释放。未设置客户端动画计数，不修改 Main.myPlayer/LocalPlayer 或真人光标，不用额外动画包掩盖动作失败。

选框坐标在本机已核对的本地 PreItemCheck 世界鼠标坐标空间读取，绘制时经世界渲染矩阵和 UI 比例投影，不额外对鼠标再应用缩放。选框仅客户端读取世界数据；`/vpmine` 复用原生 World 命令分发，没有新增 TCP、Lua 或 ModPacket 协议。换世界清空目标和观察对象。

## 本次验证与边界

- 构建 **0 errors / 0 warnings**；`verify.ps1 -MiningOnly` **263 项 PASS**，包含 G6/G7、G2 73项并发/网络检查，以及14项新增状态/鼠标入口/真实 FNA 投影检查。**未运行仓库全量测试。**
- 原生物品回归：木剑、铜镐、苹果和最后一个苹果，在真实首次 ItemCheck 后强制周期与全量同步同时到期；同帧四次控制序列化均保留按下，下一次物品检查释放，仍只启动一次动画。可读状态包含实际槽位/运行状态及死亡状态；原有消费、停止、绑定保护保持。
- 原生挖掘回归 **27项通过**：同类首帧同步检查通过；石块仍46次更新、2次挥动、3次镐击、进度105/100后实际破坏，原生掉落及邻格保留。
- 物品/挖掘独立异常诊断通过输入释放及 quarantine 断言，仅终止脚本创建的隔离进程，不记为正常恢复。
- 新增文件锁检查已在真实锁定的旧包上验证：提前拒绝，不新增备份、不改包或启动器。第一次遇到锁之前生成的完整备份保留在 `.local/virtual-player-single-window-backups/20261026-185009-892/`，旧部署包哈希仍为0.7.1的 `45DE497E...`。

**新选框的图形对齐、聊天实际显示、首次 use 客户端动画、远程多人均待真人复测。** 投影计算检查不等于已打开图形游戏，服务器同步回归也不等于客户端已显示。旧累计整理的490项/7组结果仍按 [0.7.1报告](VIRTUAL_PLAYER_SINGLE_WINDOW.md) 保留，本轮未把它们重记为0.7.2全部通过。

包：`.local/virtual-player-single-window-build/Mods/VirtualPlayerEngineProbe.tmod`；SHA256 `495A83803D5A5B69FAAB51954910D89A9F293FE93FF3B97C5228C51625E846BB`。

证据：`.local/probe-072-offline-checks.txt`、`.local/probe-072-native-use.txt`、`.local/probe-072-native-regressions.txt`、`.local/probe-072-locked-profile-check.txt`。真实游戏日志快照在 `.local/live-item-use-audit-20261026-182116/` 与 `.local/live-status-target-audit-20261026-184404/`。个人日志、存档、构建产物不提交。
