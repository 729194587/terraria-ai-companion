# M4.2：导航动作内部的安全恢复

日期：2026-11-03。基线 PR #25、`codex/g85-server-ai-perception`、`62faea98708dcdd6cabe9f8b00091b19780bf92d`；开始时本地和远端 HEAD 一致、PR 为 Open / Draft。原有 Python/Lua、`verify_server_ai_native.ps1` 和未跟踪设计/实验文件保留，不作为本轮源码改动提交。

**结论：实现及离线验收完成；原生恢复验收被引擎指纹门禁阻塞，不能宣称实机通过。** 本轮只实现 M4.2，不包含 M4.3 Trip/长距离搜索、装备能力或地图扩展。没有运行全量测试，没有合并或部署现用 profile。

## 可重复反例与结果

在现有矩形碰撞夹具、Basic 物理和同一目标 X=1800 下，角色从 `(1600,1558)` 越过障碍；在一次真实模拟跳跃首次落地时仅注入一次 +24px 的诊断扰动。然后全部运动继续使用原 `PhysicsSimulator.Step`，不注入后续位置/速度。

| 同条件对照 | 结果 | 更新次数 | 实际终点 | 恢复/恢复后规划 |
|---|---|---|---|---|
| 原 M4.1 Task/Controller 源码副本 | failed：`prediction diverged: position=24.00px velocity=0.00px/tick` | 47 | `(1710.28,1558)` | 0 / 0 |
| M4.2 Task/Controller | arrived，原水平到达谓词核验通过 | 116 | `(1804.48,1558)` | 1 / 1 |

这是**离线实验结果**，不是原生 Terraria 验收。对照使用相同物理/碰撞夹具及 M4.1 搜索规则，旧类从基线源码导出，仅改名以便同进程比较；生产规划器增加的候选过滤在旧链路未传入。原始输出及对照源码/runner 在 ZIP 的 `offline/baseline-comparison*`、`offline/Legacy*` 等条目。

另有离线场景：普通安全位置偏差后同一动作到达 X=1791.5198；新低墙出现后恢复一次，到达 X=1799.32；新高墙场景恢复一次后在 X=1620 以 `navigation_search_expansion_budget` 有据停止，没有将预算停止改写为不可达。以上使用原目标 X=1800 的“安全停稳且越过到达带”契约，不承诺精确停在 X=1800。

## 实现与边界

- `LocalMotionController` 用明确的 `MotionFailureKind` 区分路线偏差与不安全失败。预测偏差、跳跃落点不符及下一步与当前路线明显不符可以请求内部恢复；危险/未知地形、引擎异常等不沿此通道恢复。沿用原误差阈值；下一步检查在发出不适用输入前保守停止。
- `NavigationTask` 保持原 session/generation/目标，停止旧 executor，并进入非终态 `recovering`。旧边作废；不会递增它的 CompletedActions/CompletedJumps，也不会将安全停稳直接视为到达原目标。
- 恢复只发出中性输入，自然制动/下落。每次被消费的 Before/After 配对必须由适配器核验控制标志确已释放；`NativeNavigation.After` 检查 Player 的左右、跳跃、上下与物品/格子使用输入。缺失/false 释放证据立即 unknown；单独或重复 After 不能提供第二次确认。
- 使用原 `SupportedCheckpoint`、SafeLanding、SafeSweep，要求连续两次安全、跳跃已释放、计数器清零且速度在原停稳公差内的观察；每次最多60个配对更新。未确认落地/危险保持 unknown。原匹配落点的落地确认路径保留；已确认的不同落点转入上述释放确认，不能绕过此门槛。
- 下一次规划前再次核验真实 checkpoint；如果它已经改变或不安全，保持 unknown。恢复只从当前身体状态重规划；完整目标仍由原 Arrived/水平谓词独立核验。
- 每个导航动作最多3次恢复尝试，进度不重置这个预算；原四次停滞、256条已核验边、原生1800更新和 M3/IPC 截止均保留，不因恢复重新计时。
- `NavigationFailureMemory` 只属于当前动作。对失败动作的方向、jump hold、walk stride 与附近已确认状态做有界隔离：X±32px、Y±8px、Vx±0.5；120个本导航更新后失效。它不是永久 Tile 障碍事实，也不是安全公差。不同动作仍可尝试；旧动作终态后不会因记忆失效重新启动。
- 快速地面行走与 `StateSpacePlanner` 共用隔离检查。被隔离候选不能成为完整 incumbent 或部分路线；零路线中若有隔离拒绝，以 `navigation_recovery_blocked` 原因停止；恢复次数耗尽为 `navigation_recovery_limit`。两者不会获得 Python 的有限 Direct Input 降级资格。普通确定性 no_route/profile/no_progress 仍沿既有契约。
- Snapshot 增量提供 RecoveryCount（尝试）、RecoveryReplans（实际规划尝试）、RecoveryReason、RecoveryPosition、RecoveryInputsReleased、FailedEdgeCount；`navigation_recovery` 事件随次数变化输出，终态保留这些诊断。对外 ActionId、M3 生命周期和输入结束证据不新增/替换。
- 同身份终态不能通过更改目标重新执行；显式取消对旧 session/generation 保留拒绝入口。新身份清除旧恢复状态和动作记忆。服务器的 Actor/world/slot/对象身份、生命周期、取消、未知及租约校验没有更改。

## 实际验证

| 层级 | 已执行结果 |
|---|---|
| 恢复定向 | 最终 `-RecoveryOnly` 111项检查通过：不同跳跃落点继续、动态墙、释放确认、重复After、空中未落地、危险、取消/代次/超时/unknown/终态、独立预算、记忆过期及快速路径不能绕过记忆 |
| 导航/M4.1 | `-NavigationOnly` 通过（当时恢复110项；随后补充跳跃落点反例并单独通过111项），包含M4.1的187项搜索检查、44项导航感知、落地确认、连续运动、取消及玩家钩子 |
| 目标/高度 | `-VerticalOnly`、`-LandingOnly` 通过，保持原区域/高度/平台/拒绝及协议/释放行为 |
| IPC/M3 | `run_server_ai_ipc_tests.ps1` 通过，含协议、289项原生适配替身、64项任务、30项聊天；新增恢复预算/隔离拒绝断言后单独适配器291项通过 |
| Python | `test_navigation_recovery` 8项通过，31.558秒；原有限降级、unknown、取消、身份、能力和共享预算保持通过。使用现有未提交 runtime，未将其纳入源码提交 |
| 构建 | 最终0.1.77包构建0错误、0警告；包哈希见manifest。构建环境为当前新引擎，不等于受支持原生运行验证 |
| 新原生脚本 | Python/PowerShell语法检查通过；执行尝试在Actor创建前被门禁拒绝；随后加入从既有EngineHooks常量读取指纹的启动前检查，确认不启动服务器即明确拒绝 |
| 原生恢复/图形/模型 | **未验收**：原生实际执行0个恢复场景，图形Host & Play、真实模型均未运行 |

## 原生阻塞证据

**观察**：当前安装为 tML `2026.9.3.1`，`tModLoader.dll` SHA-256 为 `bf2ca4d53512d064fae664170a0d21ecc5e2ed51b5e917d871ec78cc449263e9`。生产 `EngineHooks.cs` 钉住的是 `2026.8.3.0`、引擎 SHA-256 `fcc6a9624b12191be4a15d714a3675440911feb8c1d374642ab1fd686f0da0a5` 及对应 hooks/commit。

实际日志明确为 `Unsupported engine/hooks fingerprint; no hooks installed.`、`G0 blocked; spawn disabled.`；因此没有 Actor、没有 `action-fixture.json`、没有恢复动作/原生轨迹。脚本等待夹具超时后，只终止本轮自己启动的隔离进程。原始日志、首次候选包哈希、后续启动前拒绝均归档；不能把它们包装成原生安全恢复通过。

定向检查了本地 `.local` 和现有 tModLoader 安装，未找到完整受支持引擎副本。未下载/替换安装，未修改EngineHooks、IL指纹、隔离条件或旧存档。第一份候选包的失败尝试与最终包哈希分别记录；最终包仅有构建/离线证据。

## 复现及剩余风险

```powershell
pwsh -NoProfile -File tests/run_csharp_tests.ps1 -RecoveryOnly
pwsh -NoProfile -File tests/run_csharp_tests.ps1 -NavigationOnly
$env:PYTHONPATH="$pwd/tests;$pwd/python"
& .local/lua-venv/Scripts/python.exe -B -m unittest test_navigation_recovery -v
# 原生复测必须先取得完整且指纹匹配的原受支持安装，并用它重新构建。
pwsh -NoProfile -File scripts/build-companion.ps1 -TModLoaderPath <受支持安装> -OutputPath .local/m4-2-pinned-build
pwsh -NoProfile -File tests/verify_navigation_recovery_native.ps1 `
    -TModLoaderPath <受支持安装> -WorldPath <已确认的复制世界源> `
    -PackagePath .local/m4-2-pinned-build/Mods/CompanionBridge.tmod
```

原生脚本准备了位置偏差、新低墙/高墙、空中扰动后自然落地、恢复中取消五个场景；夹具只有在原隔离Action/Terrain门禁和额外 `-companionnavrecoverytest` 下才注入一次扰动。它们尚未在受支持引擎跑通，脚本断言与真实原生时序仍有待验证，不能当作已有成功记录。

恢复依赖已验证的普通物理和局部搜索；不保证所有动态障碍可绕行。动作签名隔离是有界策略，会临时排除某些可行候选，也可能允许不同参数再次遇到同一障碍；独立恢复预算防止无界循环。没有新增专门等待动态障碍消失的机制。中性输入期间的真实原生制动、客户端输入干扰、复杂斜坡/平台碰撞、敌怪/伤害、掉线和精确取消竞态仍需受支持引擎补验。M4.1 的局部窗口、非最优水平启发式和有限预算限制继续存在。

[manifest.json](manifest.json) 保存源码、包、引擎与ZIP逐条SHA-256；[raw-runs.zip](raw-runs.zip) 保存离线结果、旧/新源码对照、构建及原生拒绝证据。本轮不宣布M4.2原生验收通过，也不进入M4.3。
