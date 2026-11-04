# M4.4：随角色成长的 Mobility Profile

后续基于 `619eac9` 的快速切换、到期临界帧及第三方 Mod 门禁核查见 [CLOSEOUT.md](CLOSEOUT.md)。它补充并收窄下文自然到期与 Mod 兼容性的验证结论；本报告及原始证据保留为原阶段记录。

日期：2026-11-03。基线PR #25、`codex/g85-server-ai-perception`、`39a988746e464a6b30e44688e9dfa1326373fc38`；开始时本地/远端HEAD一致，PR为Open / Draft。原有Python/Lua、原生验证脚本修改及未跟踪设计/实验文件保留，不作为本轮源码提交。

**阶段结论：M4.4在下述范围内通过。** 新增已验证的原版Swiftness数值配置；Basic回归通过，能力变化使旧计划失效，同一动作释放/停稳后使用新版本规划。最终包11个隔离原生场景符合预期：9个导航完成、2个未验证配置拒绝。没有放开装备、翅膀/额外跳跃等特殊运动，没有实施M4.5，没有运行全量测试、部署现用profile或合并PR。

## 实现与逻辑契约

- 新增数据型 `MobilityProfile` / `MobilityTracker`：effectivePhysics、20×42身体尺寸、availableModes、unsupportedEffects、revision、sourceTick及导航session/generation。world/Actor/slot/外部Action归属继续由已有M3外层绑定提供；子对象中的session是导航控制session，不是另一个任务或授权令牌。
- NativeNavigation在真实 `PostUpdateRunSpeeds` 输入边界读取 `Player.runAcceleration/maxRunSpeed/accRunSpeed/runSlowdown/gravity/maxFallSpeed` 与当前 `Player.jumpSpeed/jumpHeight`。模板只用于核对适用性，**预测始终使用当次原生读取值**；没有通过Buff名称推导或向Player写入这些参数。
- 原 `OrdinaryRejection()` 保留并演进：Basic或BuffID.Swiftness(3)必须与对应实测参数匹配；只开放普通地面行走和普通跳跃。原山体/湿润/滑动/坐骑/额外跳跃/翅膀/钩爪/异常身体与装备拒绝保留。147/216原非运动Buff例外保留；其余未知Buff仍拒绝。
- 额外加载的未知Mod明确返回 `unvalidated_loaded_mod`；即使当前数字碰巧等于Basic也不宣称支持。服务器此前已有只允许ModLoader+CompanionBridge的隔离门禁；客户端导航现在也采用这个保守限制，不保证此前任意工具Mod组合继续可用。
- revision随有效物理、能力类别、身体/拒绝事实或控制作用域变化而更新；正常采样tick推进不改变支持配置的revision。Profile包含自有的效果快照，不能因后续输入数组修改而改写旧证据。未知配置的availableModes为空；Supported仅表示适用配置，不证明空间安全或目标到达。
- 首条计划记录 `PlanMobilityRevision`。能力变化时清除旧配置的失败动作记忆、停止当前executor并作废旧后缀；不重置M3任务、行程进度、累计预算或恢复上限。执行中的旧边复用M4.2中性输入、真实释放与连续两次安全停稳确认；不将旧能力路线误记为新能力下的永久障碍。已完成checkpoint且没有旧输入待执行时，可直接重新规划。
- 本轮高速主动移除Swiftness时，仅同session/generation、此前受支持Swiftness→当前已匹配Basic的已知转换，暂时保留3.95px/tick速度上限以自然制动；回到Basic速度后清除。没有对陌生起始高速、其他Buff或新连接泛化放行。本轮在约3.8px/tick消退时实际核验了20个旧/新版本不一致但只发中性输入的更新。
- 版本失效处理不能复活terminal；取消/unknown/身份变化继续按原M1～M3保护停止。`NavigationTask`与快速步行仅增加已验证数值族适用性；`PhysicsSimulator.Step/Predict`方程、`NativeMotionWorld`、`MotionSupport`、控制对象、碰撞/下降限制、引擎指纹和原API白名单未改。

Profile作为现有导航快照的增量诊断字段提供；没有新增原子API、第二个Agent Loop、特殊运动执行器或自动使用药水功能。

## 原生参数依据

使用M4.3已准备的官方隔离tML `2026.8.3.0`：引擎SHA-256 `fcc6a9624b12191be4a15d714a3675440911feb8c1d374642ab1fd686f0da0a5`，hooks `595d26fb3bbe27548d030943b5ed2437f690d1c002129bad21fafeaeb8118f7a`。没有放宽检查或修改现用Steam安装。

**观察/实验结果**：先保留旧M4.3产品门禁，仅加入隔离采样夹具。Basic到达；Swiftness在第一次更新被旧门禁拒绝，实际X不变。输入边界已采到如下有效值：

| 字段 | Basic | 原版Swiftness |
|---|---:|---:|
| runAcceleration | 0.08 | 0.099999994（匹配模板0.10） |
| maxRunSpeed | 3 | 3.75 |
| accRunSpeed | 3 | 3 |
| runSlowdown | 0.2 | 0.2 |
| gravity / maxFallSpeed | 0.4 / 10.01 | 0.4 / 10.01 |
| jumpSpeed / jumpHeight | 5.01 / 15 | 5.01 / 15 |
| width / height | 20 / 42 | 20 / 42 |

这些是钉住引擎的实测字段，不是“速度药水应当加25%”的推算。容差沿用原0.001；没有接受任意更快参数。步进公式仍是原已审查的参数化运动规则，本轮通过不同配置真实Player.Update逐帧对照验证适用性，不能推广成完整原生Player/任意Mod模型。

## 最终同包原生矩阵

0.1.79包SHA-256 `1f7d4d30c831a0f1490ab0a1594e12e7ac70d28997e9a18f6377515226c1799d`。复制相同源世界到新profile，只有显式Action/Terrain门禁及 `-companionmobilitytest` 才允许诊断夹具构造平地/障碍/台阶、初始化装备/Buff、或在指定更新添加/移除Buff；这些注入不是正式执行路径。导航全过程只用原生输入、碰撞与自然制动。NPC战斗由既有隔离夹具排除；脚本只调用Lua/IPC，不使用LLM。

| 场景 | 结果 | 导航更新数 | 最终X | 版本/效果事实 |
|---|---|---:|---:|---|
| Basic平地384px | completed | 161 | 34021.652 | 与旧门禁探针Basic的更新数、终点一致 |
| Swiftness平地384px | completed | 134 | 34015.840 | 原旧门禁为failed且X=33630；现在使用真实0.10/3.75 |
| Basic两格障碍 | completed | 180 | 34015.900 | 实际跳跃、落地、停稳 |
| Swiftness两格障碍 | completed | 157 | 34014.130 | 实际跳跃、落地、停稳 |
| Basic上48px区域 | completed | 70 | 33732.520 | 固定X区域、脚底高度与平坦支撑 |
| Swiftness上48px区域（右） | completed | 59 | 33738.300 | 同一几何区域谓词 |
| Swiftness上48px区域（左） | completed | 59 | 33521.700 | 镜像方向核验 |
| 动作中添加Swiftness | completed | 146 | 34019.434 | revision 8→9；4个旧版本中性更新；一次恢复/重规划 |
| 高速动作中移除Swiftness | completed | 177 | 34016.773 | revision 10→11；先前Vx=3.7999976；20个旧版本中性更新 |
| 未验证WellFed(26) | expected failed | 1 | 33630 | unsupported_buff及实测0.096/3.6000001不匹配，零位移 |
| 未验证Hermes Boots(54) | expected failed | 1 | 33630 | slot=3装备及accRunSpeed=6不匹配，零位移 |

每场只提交一个外部动作。全部终态检查known输入释放；完成场景独立核验真实grounded、安全支撑、停稳、释放前零速度及原目标谓词。所有已记录受控轨迹最大预测误差为0，但到达不是由该数值代替证明。水平目标允许跨过目标后停稳，表中不是精确X命中承诺。

所有真实注入样本的Profile.SourceTick均匹配输入记录tick，session/generation匹配导航；版本不一致的支持配置更新中，左右/跳跃输入全部为false，随后计划版本切换到新revision。未重启外部ActionId或导航session，也未把旧边算作完成。

基础探针的原始汇总曾带有一个未注入输入时遗留的诊断样本；最终夹具只在有对应输入记录时输出inputPhysics，派生summary也仅选择真实注入样本。原始探针字节保留，未把它改写成最终包数据。早期候选运行留在.local，不与最终包混算。

## 定向验证与验收范围

- `-MobilityOnly`/`-NavigationOnly`最终56项能力检查通过：采样tick不引起版本抖动、数值/身份变化、未知参数/模式/Mod/装备拒绝、效果快照、同动作升级/降级、终态不复活和区域导航。正确性夹具预热两种预测配置，未扩大生产预算。
- 原M4.1 187项搜索、M4.2 111项恢复、M4.3 23项行程、44项导航感知，以及原落地、连续运动、玩家钩子通过；`-VerticalOnly`通过。
- IPC/适配替身297项、任务64项、聊天30项及协议检查通过；Python `test_navigation_recovery` 8项通过（45.225秒），原WellFed拒绝后的有限降级、unknown、取消与共享预算保留。
- 正式包构建0错误、0警告；47份原始证据ZIP逐条及源码/包/summary SHA-256已核对。未运行全量测试。

**剩余边界**：只验证Basic与原版Swiftness的干燥普通20×42角色。所有功能装备仍拒绝，特殊运动模式没有可靠拆分/验证，本轮未实现；未知Mod、额外跳跃、翅膀、坐骑、特殊表面、任意Buff组合均不构成支持承诺。客户端额外Mod的保守拒绝是明确兼容性限制；原生Mod组合未测试，也没有为测试删除引擎隔离。自然Buff到期临界帧、空中Buff切换、特殊装备移除后的残留运动、伤害/断线精确竞态、图形同场和真实模型未新增原生验收；现有有界落地/unknown、身份、取消和终态保护继续适用。

本轮没有重新跑M4.3完整自然世界矩阵，也不把11个受控场景推广为所有地形成功率。Basic原生平地/障碍/上台与原有定向回归未观察到退化；搜索的有限预算、非最优启发式、冷启动耗时等原限制不变。

```powershell
pwsh -NoProfile -File tests/run_csharp_tests.ps1 -MobilityOnly
pwsh -NoProfile -File tests/run_csharp_tests.ps1 -NavigationOnly
pwsh -NoProfile -File scripts/build-companion.ps1 -TModLoaderPath <指纹匹配隔离引擎> -OutputPath .local/mobility-build
& ./tests/verify_navigation_recovery_native.ps1 -TModLoaderPath <指纹匹配隔离引擎> `
    -WorldPath <确认的复制世界源> -PackagePath .local/mobility-build/Mods/CompanionBridge.tmod -Mobility
# 先观察而不把成功作为要求：加-Probe；定向场景可用-Scenes @('remove')。
```

[summary.json](summary.json) 保存参数、切换点、旧计划中性更新与每场终态；[manifest.json](manifest.json) 保存来源、包/源码与ZIP哈希及验证范围；[raw-runs.zip](raw-runs.zip) 无损保存原始回执、逐帧数据、探针与定向检查。保持Draft，等待M4.4审查，不进入M4.5。
