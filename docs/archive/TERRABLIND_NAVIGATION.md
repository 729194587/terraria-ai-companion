# TerraBlind 连续导航集成原型（0.1.16）

0.1.79 / M4.4：当前支持 Basic 与经钉住引擎实测的原版 Swiftness 数值配置（普通行走/跳跃）；能力变化作废旧计划并沿安全释放/停稳路径重规划。未知装备、特殊运动及额外加载 Mod 仍保守拒绝。本文后续版本段落为历史记录；当前实现、11场原生验证及限制见 [M4.4 报告](evidence/m4-4/README.md)。

当前0.1.55的能力审查、探索层接通和32场隔离原生地形矩阵见 [L2二维地形通过](L2_TERRAIN_TRAVERSAL.md)。下文保留历史实现与验证记录。

## 0.1.27：多人玩家更新隔离（2026-10-21 修复，2026-10-22 已部署 B，待游戏复测）

本次实测报告目标 X=34094.1，运行采样中 frame 始终为 1、completedActions 始终为 0，但实际 X 持续变化，最后以 `replan requires safe foot support and body clearance` 失败。`CompanionPlayer.PostUpdateRunSpeeds` 对远程玩家同样执行；远程玩家不满足 CanControl，原分支会调用全局 `GameNavigation.Cancel`，清空本地任务及 Before/After 配对。现在入口先排除非本地玩家；本地玩家的取消、死亡、手动输入和租约处理保持原样。

`tests/PlayerHookOfflineTests.cs` 通过最小 Terraria 桩编译并调用实际玩家钩子，配合现有真实 NavigationTask、规划器、执行器及矩形碰撞替身，交错远程和本地钩子，验证帧推进、动作完成、单格障碍任务停稳抵达、终态保留、远程输入不变，以及本地死亡和手动控制。移除新增入口保护的临时源码副本会在“Remote player must not cancel the shared local task”断言失败，修复源码通过。此测试不模拟原生 StepUp 或真实多人时序。

落脚检查保留所有原阈值和检查范围，新增失败原因：身体碰撞/未知危险格坐标、实际脚部接触宽度，以及重新规划时的实际位置和速度。旧日志未包含地形及该检查瞬间的状态，无法确定原场地落脚拒绝的具体子条件，因此没有放宽安全规则，也不声称已消除所有单格障碍问题。完整 Python 227 项、完整 C# 回归、compileall、diff 检查通过；本机 Mod 构建零错误零警告，产物 `.local/player-hook-build/Mods/CompanionBridge.tmod`。2026-10-22 已备份旧包并部署至 `.local/m12-b/Mods`，SHA-256 核对一致；仍待真实游戏复测结果。

## 0.1.26：近目标运动状态与制动（2026-10-21，未游戏复测）

依据 `.local/navigation-live-01.json`：目标 X=33823.395，失败采样位置 `(33822.105,3718)`、Vx=2.399997、Vy=0，距目标约 1.29px；规划器 `expansions=0/plannedActions=0`。`StateSpacePlanner` 的 `H=max(0,有向剩余距离)/MaxRun` 在该状态约为 0.43，满足旧的 `H<=4`，根节点在生成候选前就被当作已接近目标退出。回溯根节点没有动作；`NavigationTask` 同时要求速度不超过 0.1 才抵达，因此将空路径报为 `no safe local route`。不是该次展开预算耗尽；不应通过增大预算或放宽抵达速度来处理。

先将报告中的精确运动状态放进已有碰撞替身（平地高度平移到报告的 Y），修复前离线测试得到相同的零展开空路径。报告没有完整 Tile 或逐帧轨迹，所以该替身只复现已确定的规划判定错误，不冒充真实场地回放。修复前输出保存于 `.local/near-goal-red.log`。

修复内容：

- `PhysicsSimulator.Predict` 允许显式的 direction=0、hold=0、walkFrames=0 制动候选，执行完整稳定尾段；只有这种纯制动不要求新增水平进展。仍逐帧调用同一运动模型、SafeSweep、下降上限和最终 SafeLanding，并要求连续两帧稳定。普通步行、跳跃的进展/安全判据保持。
- `StateSpacePlanner` 对近目标且能安全停在原有到达区域内的状态优先交出该制动动作；最终目标的搜索结束还需停稳和安全支撑，不能仅凭距离返回空动作。局部 128px 中途目标仍是可保速交接的检查点，不要求每个中途目标都停下。
- `NavigationTask` 将原任务的水平穿越方向传给规划器，防止已经略过目标后重规划反转语义，或恰在目标 X 时方向变成零。仍由原任务/执行器执行、核验反馈后抵达；没有修改 12px 有向水平到达范围、0.1 速度门槛、控制租约或取消机制。该目标本来就是水平穿越，不是精确 XY 站位。
- 检查 `LocalMotionController` 后沿用其逐帧输入、实际/预测偏差保护及动作尾段完成反馈，无需另加“附近就成功”分支。

回归覆盖真实失败状态、左右方向、目标 X 恰相等/略过目标、必须先有完整制动反馈再抵达、已停稳无需多余动作、危险/未知制动拒绝及不可穿越高墙仍失败。此前 Buff 门禁修复的未提交修改保留。有限窗口、物理模型适用范围与碰撞替身限制仍在；离线通过不能证明实际游戏或多人/Mod 钩子组合已可靠。

本轮验证：完整 Python **227 项通过**，`tests/run_csharp_tests.ps1` 完整 C# 回归、`compileall`、`git diff --check` 通过。本机构建 **0 错误、0 警告**，0.1.26 包位于 `.local/near-goal-build/Mods/CompanionBridge.tmod`。未启动游戏、部署、调用真实模型、提交或推送；仍需后续真实场地确认预测/实际制动与任务生命周期。

## 0.1.25：Buff 门禁修复

0.1.25 门禁修复验证：完整 C# 离线回归、Python 导航 CLI 7 项及 `git diff --check` 通过；本机 tModLoader 构建 0 错误、0 警告。包在 `.local/navigation-buff-build/Mods/CompanionBridge.tmod`，后续已按用户指示部署至 `.local/m12-b/Mods`；用户此次导航报告已通过 Buff 门禁并到达目标附近，随后暴露上节的规划判定错误。代码仍未提交或推送。

2026-10-21（当前 0.1.25）Buff 门禁修复：`navigate.py` 仍使用受限基础物理模型，但不再拒绝所有 Buff。新增 `NavigationBuffPolicy`，明确放行本次同位置只读观察中的 `MonsterBanner`（147，旗帜战斗效果）和 `BabyBird`（216，雀随从维持效果）；本机 Terraria 1.4.4.9 的 BuffID/Player.UpdateBuffs 与旗帜攻击、防御入口经只读 IL 核对，这两种原版效果不修改玩家运动。不取消玩家 Buff，不要求离开旗帜范围。其余未审查/Mod Buff 仍拒绝，速度/跳跃/重力参数、装备、特殊状态、地形危险、掉血与控制租约保护保持原限制；不是通用能力感知导航。下文历史“无 Buff”验收要求应理解为“除这两种已确认效果外无其他 Buff”。

`unsupported navigation state` 现在列出实际拒绝项：Buff 的 slot/type/contentKey/timeTicks，装备槽位/类型，或 wet、extra_jump_usable 等状态。仅参数不匹配时才附 `unsupported_physics`，避免基础参数正确却因 Buff 被拒时只打印参数。离线测试覆盖两种效果的放行、与 Swiftness 等效果混用时仍拒绝、未知/Mod/相邻 ID 不误放行、过期槽位及拒绝信息；既有物理/安全测试继续适用。真实游戏复测尚未进行。

最新状态：0.1.16 已部署至 `.local/tmodloader/Mods`，并通过本次右向连续通行游戏实测。用户确认从障碍上跳下后继续行走、连续跳跃并越过另一障碍。报告为 `arrived`，同一 generation=3 内完成 21 段动作、3 次跳跃；三次跳跃交接速度分别为 2.7199996、2.8599992、2.24px/tick，均 `settled=false`、`maxError=0`。最终位置 X=34567.723、目标 X=34567.656，停稳速度为零。成功报告保存为 `.local/navigation-0.1.16-success.json`。这确认本次右向场地的端到端链路，不代表左向、所有地形或中断专项均已实测。代码仍未提交。以下保留先前集成与验收记录。

0.1.15 报告中预测/实际落点同为 `(33971.63,4342)`，预测/实际水平速度同为 2.6399996、Vy=0、maxError=0，但返回 `jump landing mismatch`。原回执未暴露支撑帧数和跳跃标志，不能仅凭报告断言是哪一个布尔条件失败。检查发现原生模拟接触标志可能在下降被碰撞截短、垂直速度仍为正时成立，而真实地面观察在普通平地要求 Vy=0；原预测提前计入了这一帧。新增碰撞替身专门保留这次接触的正 Vy，位置/速度保持一致、实际着地按零 Vy 观察，在修改前复现落地校验失败。

0.1.16 的预测跳跃只有在 Grounded 且 Vy 接近零、跳跃已释放、跳跃计数归零及支撑安全时才累计交接帧；接触尚未稳定时保持方向输入。新增测试修复后通过，原单次落地和保速交接回归保持通过。若仍失败，回执会列出 airborne、grounded、support、supportFrames、releaseJump、jumpCounter、vxError、vy。完整 C# 回归和本机 tModLoader 构建通过，零错误零警告；本次右向游戏修复结果见上文。

本轮将连续导航放进 C# 游戏行为层。Python 提交水平目标、续租和收集反馈，不负责判断何时跳跃或串联单次跳跃。代码未提交。0.1.11–0.1.14 已先后部署至 `.local/tmodloader`；0.1.11、0.1.12 的右向任务到达目标，0.1.13–0.1.14 实测确认存在障碍顶下行和边缘支撑误拒的问题。0.1.15 修正支撑检查，尚未部署及游戏复测。

## 实际检查与取舍

沿用本地 `.local/terrablind-reference`，核对 HEAD 为 `26361f488da986ee8739dded76fabedfa04f6aa8`，没有混用远端最新版本。上游 README 声明 MIT，继续保留 `ThirdParty/TerraBlind/LICENSE`，本轮补充 `NOTICE.md` 的改造范围。上游部分物理注释引用 1.4.5.4，因此没有覆盖已有针对 Terraria 1.4.4.9 的校正。

固定提交中的调用关系：

| 模块 | 实际职责与依赖 | 本轮处理 |
| --- | --- | --- |
| `Perception/PhysicsSimulator.cs` | `Step`、参数与轨迹模拟，调用原生碰撞 | 继续使用 M1.5 已改造的物理核心及 `NativeMotionWorld`；新增可配置步行段长度 |
| `Actions/JumpExecutor.cs` | 枚举 hold / 水平输入时段，模拟后交给 `Debug/ReplaySystem.Load`；更新时读取真实落点 | 继续由已有 `LocalMotionController` 执行与校验；未导入其假初始速度和全局回放队列 |
| `Perception/PhysicsRecorder.cs` | `Capture` 记录真实逐帧位置、速度与输入；本身不是寻路或自动纠偏器 | 保留已有 expected/observed 反馈，新增持久化的最近一次落点回执 |
| `Nav/StateSpacePlanner.cs` | 真实物理状态搜索，输入枚举 → 模拟边 → 速度标签支配 → 优先队列 → 回溯动作帧；有 grounded partial route | **实际移植并适配搜索内核**，见本地同名文件及 NOTICE |
| `Nav/RecedingNav.cs` | 近目标 Stand 分支调用 `Plan`，Found/Partial 均可交 `DispatchPlan`；其他分支沿场推进并调用脱困系统，完成后比较真实落点 | `NavigationTask` 适配其规划、派发、反馈后重新规划的生命周期；未整类导入 |
| `StateSpacePlanner.DispatchPlan / StartSteps / TickSteps` | 当前状态空间导航有自己的边执行链，同时分派放置、挖掘等行为 | 用现有 Bridge 输入钩子和单动作执行器承接帧输入，避免引入世界修改系统 |
| `Nav/ActionGraphPlanner.cs` | 另一套 stand-cell 动作图，依赖 `PathPlanner`、`DigTable`、`MazeWand`、物品槽检查等 | 固定提交全仓未找到 `ActionGraphPlanner.Plan` 调用；不把它误认为当前主执行链，不导入 |

TerraBlind 使用的就是 `Main.LocalPlayer`，不是自定义伙伴实体。真正阻碍完整搬入的是 `MazeWand` 距离场、`PathPlanner` 地形查询、`ClearWay`/`DigTable` 挖掘、`NavCoordinator` 物品槽、平台与桥梁执行器、调试可视化及静态任务状态。即使关闭挖掘/放置候选，原始 `Plan` 入口仍会访问这些依赖。最短可验证路径是移植独立搜索部分，并复用已经与本机版本对齐的物理与控制适配器。

本轮不是完整 TerraBlind 导航系统的直接移植。搜索代码保留其 `CellKey`、速度 `Label`、`Dominated`、hold 枚举、frontier/came-from、grounded partial fallback 和动作回溯结构；模拟边、目标语义、启发式和预算经过适配。未引入 TerraGuardians 或新增依赖。

## 新链路

```text
Python navigate.py：提交 navigate(targetX)，每 100ms 续租，查询 get_navigation
  → ControlState：游戏线程消费命令，检查 expectedGeneration，维持 750ms 租约
  → CompanionPlayer.PostUpdateRunSpeeds / GameNavigation
  → NavigationTask：从真实位置/速度/跳跃状态开始规划
  → StateSpacePlanner：搜索多个安全步行/跳跃动作，回溯动作帧
  → LocalMotionController：只执行首段，逐帧重新检查当前地形
  → CompanionPlayer.PostUpdate：比较预测与真实位置/速度，验证停稳落点
  → NavigationTask：计数并保存落点，从实测状态重新搜索，直到 arrived/failed
```

导航沿水平目标推进，左右均支持。`targetX` 是玩家包围盒左上角的像素 X；到达语义是沿初始方向到达距目标 12px 内或越过目标，并在安全地面停稳，**不是精确 XY 站位**。一次任务中可以连续跨越多个障碍，不需要 Python stop/start。0.1.12 的普通步行只执行安全预测的移动前缀，保留速度进入下一段；0.1.13 将同样的交接方式扩展到跳跃。最终接近目标和无法安全交接的位置仍执行完整刹车尾段。

跳跃保速交接要求先腾空，再连续两帧安全支撑、跳跃已释放且跳跃计数归零。第一帧落地后仍保持方向输入再走一帧，与上游额外地面步的处理一致。预测器继续模拟完整刹车尾段，确保该落点具备安全制动路径；仅安全移动交接点作为本段执行终态。执行器核对两帧实际支撑及末帧速度，不因取消强制刹停而取消落地验证。单次 `predicted_walk.py` 使用默认预测方式，仍完整落地停稳。

0.1.14 将导航预测的下降上限独立设为 64px（四格）：步行相对最近一次支撑高度，跳跃相对该段起点。原先沿用的 16px 限制无法从两格高障碍走下或跳下，所有候选都被拒绝。完整轨迹的危险/未知 Tile、窗口、支撑、落地及刹车检查仍保留；旧单次预测入口默认仍为 16px。无路时回执现在包含实际展开次数、规划耗时及前三类候选被拒原因，不再显示默认零计数和笼统提示。

0.1.15 修正共同使用的原生落点支撑检查：原先要求 28px 的扩展包围盒下每列都有支撑，合法的障碍边缘站位也会失败。现在按真实 20px 身体检查碰撞，累计脚底与支撑面高度差在 4px 内的列投影宽度，要求至少 8px；不要求身体外侧虚拟边距有地面。仍拒绝危险/未知 Tile、身体嵌入和不足阈值的边角接触。游戏 `NativeMotionWorld` 与离线测试调用同一份 `MotionSupport.SafeLanding`，不再仅由碰撞替身猜测支撑规则。起步失败原因也区分玩家未着地和脚底/身体检查失败。

搜索每次以最多 128px 的水平子目标推进，复用原有 45×29 Tile 局部窗口。预算为最多 32 次展开、候选之间检查 25ms，外层沿用 50ms 超时失败保护；单个候选不会被中途抢占，因此不是严格的实时期限。预算耗尽时只接受确实前进的安全落地 partial route；没有可用动作就停止。

每个节点包含真实物理状态；候选方向为前后两个方向，hold 为 0、2、4…14、15。步行提供 24 帧与 6 帧移动候选，后者用于缩短障碍前的接近段；每个候选仍预测并校验完整刹车尾段。保速步行节点取实际执行前缀的终态，包含残余速度，动作成本也只计算执行帧数；跳跃额外计入 20 帧偏好成本，减少可走地形上的不必要跳跃。预算耗尽的 partial route 按累计成本加剩余距离估计选择，不再只选择最远落点。

规划能回溯多条边，但每次只执行第一条，避免把后续预测起点误当作实测状态。动作交接点须有安全支撑，位置容差为 X 8px / Y 4px、速度相对预测终态误差不超过 0.1；最终刹停和不支持保速交接的预测终态速度为零。逐帧偏差仍使用原有阈值。连续四段没有刷新前进位置或总动作达到 256 时失败。

`get_navigation` 增加 `targetX`、`completedActions`、`completedJumps`、`expansions`、`plannedActions`、`planMilliseconds` 和 `lastLanding`。最后一个字段在下一段执行期间仍保留上一次已验证动作的预测/实际交接位置和最大误差，便于低频监督者采样。0.1.12 增加 `predictedVelocity`、`actualVelocity`、`settled`：保速步行交接时 `settled=false` 合法；0.1.13 的保速跳跃也允许 `jump=true, settled=false`，最终到达仍须停稳。

相同目标的续租不重启任务。目标改变、模式改变、超时后重启都会改变 generation，旧监督者不能续租新任务。P、死亡、退出、断线沿用现有清理路径；失败或已到达的任务不会因普通续租恢复执行。到达或失败后等待 Python 发 guarded stop 或租约到期释放自动输入。

## 与已有入口的关系

- `predicted_walk.py`：保留，仍是单次跳跃的预测/实际对照工具。
- `terrain_walk.py`：保留，仍是 Python 地形规则实验。
- 工作区原有未提交的 `continuous_walk.py`：保留，仍是在 Python 中反复启动单次执行器，作为对照。
- 新 `navigate.py`：目标与任务监督入口，使用 C# 状态搜索与连续执行，作为本轮验收入口。

## 构建与真实游戏验收

构建命令沿用 README，把输出目录改为 `.local/navigation-build`；产物是 `.local/navigation-build/Mods/CompanionBridge.tmod`，版本 **0.1.15**。本轮不自动替换正在使用的包。完全退出测试客户端后复制到实际配置的 `Mods` 目录并重启；最近一次实际配置是 `.local/tmodloader`。

准备普通无装备、无 Buff、无坐骑的角色，关闭自动暂停并松开操作键。选择可回退的干燥平地，在前方放两个两格宽、两格高障碍，建议间隔约 20 格；目标放在第二个障碍之后。运行：

```powershell
python python/navigate.py --direction right --tiles 40 --seconds 40 --output .local/navigation-right.json
```

若第二个障碍距起点超过 40 格，应适当增加 `--tiles`（最多 200）。反方向布置或移到障碍另一侧，用 `--direction left` 复测。验收应同时看到：角色连续越过两个障碍；输出 `completedJumps >= 2`；`completedActions` 持续增加且 generation 保持一致；最终 `arrived`、真实地面停稳；`lastLanding` 的预测和实际落点处于容差内。JSON 保存动作计数变化事件与最近 30 次状态采样，不是每个游戏 tick 的完整录像。

另外在执行中按 P、关闭脚本、测试不可跨越高墙：分别确认不会自动恢复、断线/租约释放、无路后失败停止。多人 A/B 场景需再次确认仅 B 被控制、A 能观察运动。离线碰撞替身无法证明这些真实游戏行为。

## 验证与后续

2026-10-20 用户实测 0.1.14 两次起步即失败，均为 `replan requires safe ground support`。第二次只读查询确认玩家 `(34108.727,4310)`、速度零、grounded/supported=true；障碍顶为列 2132–2133、行 272，脚底高度 4352。身体左侧略伸出障碍，但仍有约 16.7px 支撑；原检查连扩展边距都要求支撑而误拒。第一次位置由目标减 640px 得到 `(33923.72,4310)`，其身体外 4px 边距跨入空气，也触发同类拒绝。

0.1.15 以该真实 Tile 布局和两个报告坐标直接测试共享支撑实现；新增完整支撑、8px 支撑、1px 边角拒绝、空气但 Grounded 标志仍为真、身体嵌入、危险/未知支撑及半砖高度检查。完整 C# 回归和本机 tModLoader 构建通过，零错误零警告；实际起步及连续通行仍待复测。此前只使用替身的落点测试未能覆盖原生支撑边距规则，此次测试直接覆盖提取后的同一份代码。

2026-10-20 用户反馈 0.1.13 只跳一次便停止，可能落在障碍顶，之后重发命令无法走动。最新 `.local/navigation.json` 为 `failed / no safe local route within search/window budget`，不是到达。已保存到 `.local/navigation-0.1.13-failure.json`。只读 `get_terrain` 确认玩家 `(33925.15,4310)`、速度零、支撑有效，正站在两格宽两格高方块上，普通地面玩家 Y 为 4342，下降 32px。源码仍限制下降 16px，新增测试在修改前复现相同无路失败。

0.1.14 已通过从障碍顶左右下行、先落在障碍顶再下行并越过另一个障碍、超过 64px 下降拒绝及失败原因/真实规划计数回执测试。此前碰撞替身默认不允许障碍顶作为落点，漏掉了该真实可达状态；本轮明确补上允许站顶的场地。完整 C# 回归和 tModLoader 构建通过，零错误零警告；实际游戏修复效果尚待复测，不能据此认定所有跳后停止原因均已消除。

2026-10-20 用户右向实测 0.1.12：命令 `--direction right --seconds 20`，未指定 `--tiles` 因此仍为 40 格目标；最终 `arrived`，13 段动作、3 次跳跃，各段回执 `maxError=0`。步行交接速度维持约 2–3px/tick，`settled=false`；跳跃回执仍为零速度、`settled=true`，解释了用户观察到的跳后停顿。最终位置 34041.844，目标 34048.73，相差约 6.89px，在到达容差内；停止原因为目标到达而非失败或 20 秒超时。

0.1.13 新增回归覆盖左右跑跳连续方向输入、连续跨障碍后的非零速度跳跃交接、最终停稳、预测与实际连续两帧支撑、保速交接不可用时完整停稳回退，以及速度、跳跃释放、短暂失去支撑时拒绝交接。旧单次跳跃停稳契约保持。完整 C# 回归及 tModLoader 构建通过，零错误零警告；真实游戏跑跳交接尚待复测。

2026-10-20 用户提供 0.1.11 右向运行日志及体感：40 格目标最终 `arrived`，12 段动作、6 次跳跃，各段回执 `maxError=0`，最终位置距目标约 6.55px；采样规划耗时约 0.8–17ms。用户观察到步行断续、浅坑与障碍前提前跳跃，但未被拦住。该结果确认这次右向混合地形任务成功，不代替左向、P/断线或其他场地验收。

由日志可定位 0.1.11 每段步行约 32px 后强制停稳，反复加速；短步行的刹车成本及 partial route 最远落点规则又容易偏好跳跃。0.1.12 按上文修改交接状态与选路成本。新增离线回归覆盖左右平地跨多个交接点保持速度、平地零跳跃、最终停稳、三格宽一格深的可走浅坑不跳跃、单次展开预算下仍优先步行，以及障碍前存在安全短接近段；原多障碍/落点/偏差停止回归保留。0.1.12 构建零错误零警告，完整 C# 回归通过；真实游戏复测尚未执行。

2026-10-20：Python **104 项通过**；C# 实际 IPC/租约/控制代次回归、物理回归、左右目标到达、同一 generation 连跨两个障碍、多动作回溯、落点持久反馈、偏差失败和非法目标测试通过。tModLoader 2026.08.3.0 / Terraria 1.4.4.9 构建 **0 错误、0 警告**。含本机 TCP 监听的测试在允许 loopback 的执行环境运行；默认沙箱拒绝监听，不作为代码失败。真实游戏连续导航仍待上述验收。

仍有限制：仅水平穿越目标；局部搜索不保证全局可达或最短路径；复杂陷阱可能需要先远离目标而当前 partial/progress 规则会拒绝；不支持挖掘、放置、深落差、液体、特殊装备/Buff；失败停止而非复杂自动恢复。安全支撑至少要求 8px 列投影宽度，会拒绝仅有更小边角接触的站位。碰撞仍读当前 Tile，不能保证路径不会在之后被敌人或其他玩家改变。

下一步优先在原场地复测 0.1.15：先从当前障碍顶部原站位发命令，确认支撑检查通过并下行继续；再从地面出发连续越过两处障碍，检查跑跳交接及最终停稳。如果再次停止，保留完整报告。`--seconds` 是运行上限，默认目标为 40 格；需更长路线时显式增大 `--tiles`。之后再考虑 XY 目标、上游距离场和受限脱困。


## Server AI 水平安全短程行走（2026-10-30）

仅ServerAiNavigation开启SafeGroundWalk，客户端及默认NavigationTask不启用；LandingRegion二维目标保持StateSpacePlanner路径。水平目标先尝试既有24/6帧行走预测，复用PhysicsSimulator的完整制动尾段，要求起点和每一预测帧均为原SupportedCheckpoint + SafeFlatLanding、保持原高度。无法确认则调用原StateSpacePlanner，原搜索/危险/支撑/64px下降阈值不变。

快路只产生一条Prediction/Route，仍由LocalMotionController逐帧重算输入后果、对比实际位置/速度，NavigationTask核验检查点或进入落地确认；取消、输入释放、4次无进展、256动作和unknown仍由原链路处理。不是持续盲走，不缓存旧世界路线、不移动Player位置/速度或修改Tile。已出现地形变化时沿用现有检查与偏差容差处理，不保证能在环境变化前预知危险。

新增NavigationSnapshot字段：planKind为safe_ground_walk或state_space；searchNumber累计完整StateSpacePlanner调用（包括其零扩展制动分支），fastWalkPlans累计短程计划。planNumber仍包含两类计划，不能把所有plan日志都算A*搜索。快路的expansions=0、timeBudgetReached=false表示没有搜索，不是“已搜索全世界”。reachedGoal只描述计划预测，实际到达仍独立验证。

[成对对照、原生记录和未覆盖边界](evidence/safe-ground-walk/analysis.md)：双向平地512px完整规划器调用25→0，计划25→10，执行202帧和最终位置一致；五段128px完整规划器15→0，但计划仍15次、执行仍350帧。数据是正式物理/任务代码在离线碰撞替身上的5组配对，不是图形游戏FPS/耗时收益证明。没有flat证明时调用次数不降，复杂地形回退可能多付出两次有界预测；二维目标、绕路、深下降及窄支撑脱困不在本轮改善范围。

162个定向断言、NavigationOnly/VerticalOnly/BridgeOnly、IPC及Python导航回执4项通过；同包隔离原生96px水平导航3次快路/0次完整规划器、3个核验检查点MaxError=0并到达，另验证取消及旧动作。Mod构建0错误0警告，未跑全量测试，未部署现用profile。未进行本轮图形Host & Play、真实模型自主行走、真人/敌怪干扰或复杂地形原生矩阵。

参考Numen [61994fc Trip](https://github.com/Dwinovo/minecraft-numen/blob/61994fc00dd5f3fc963978362f156237f3801596/numen/common/src/main/java/com/dwinovo/numen/nav/Trip.java)的计划/执行分层，以及本机既有解压Terrabot BotFollowGround/BotFollowProgress的廉价地面检查与进展跟踪思路。原压缩包未定位；实际读取源码路径与SHA在证据references.json。本轮未复制这些源码/宽松接受分支或引入其按键与退避策略；预测和验证仍来自本仓库。[完整证据索引](evidence/safe-ground-walk/manifest.json)。
