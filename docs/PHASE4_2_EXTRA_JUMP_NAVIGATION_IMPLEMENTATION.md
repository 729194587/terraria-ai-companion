> **历史阶段记录**：本文保留对应阶段的设计、实现过程与当时验证结果。当前导航架构和能力说明见 [NAVIGATION.md](NAVIGATION.md)；本文中的分支、提交、待办和测试状态均属于记录时点。

# Phase 4.2 ExtraJump 导航接入 MVP

日期：2026-11-06。基线 `d238728`，分支 `codex/unified-autonomous-navigation`。依据 [v0.1 设计](PHASE4_2_EXTRA_JUMP_NAVIGATION_DESIGN_v0.1.md) 实现。

## 实现与关键决策

### 一个经过验证的原生模型

`ExtraJumpModel` 按 Phase 4.1 动态注册快照的 **FullName** 选择模型。MVP 只支持 `Terraria/CloudInABottleJump`，要求它是唯一 Enabled 的 ExtraJump、且未 blockExtraJumps。不是按装备 ID 判断，也不调用能力钩子探测参数。Cloud in a Bottle 的物品 ID 仅出现在明确门控的隔离测试装备准备中。

两个 EngineProfiles 对应的实际 DLL / IL 与原生运行结果共同确认：Cloud 持续倍率为 0.75，继承空的水平速度钩子；PerformJump 设 `velocity.Y = -Player.jumpSpeed`、`jump = int(Player.jumpHeight * 0.75)`。基本参数下，真实触发后为 jump=11、vy=-4.61（含当帧重力）。模型使用已采集的有效物理参数，不重复调用 GetDurationMultiplier、CanStart、OnStarted 或 ModPlayer 扩展钩子。真正触发仍由正常 Player.Update 的 JumpMovement / ProcessJumps 决定。

未知 ExtraJump、多个 Enabled 能力、翅膀、火箭靴及其他未建模模式不因本阶段获得支持。G1、引擎指纹、Actor/world/generation/slot、租约、取消及 unknown 边界保持。

### 沿用物理状态与动作搜索

- `PhysicsParameters` 增加可选模型；`PhysicsState` 增加 ExtraAvailable、ExtraActive、ExtraUsed。完整状态继续用于精确搜索标签、续接和缓存，未用粗粒度键取代速度、跳跃计数、输入释放状态或资源。
- `Step` 表达普通跳跃、松键、再次按下的转换。空中一段只消费一次；即使 Available 与 Active 同时为 true，也不能绕过 ExtraUsed 重复使用。
- `Predict` 保留原碰撞、安全 sweep、落脚和制动尾部。新增额外跳跃帧及第一段 hold 的计划元数据。ExtraJump 动作最多模拟 140 帧，以容纳两段上升与完整制动；普通动作仍为 90 帧。原生碰撞窗口和安全落差没有放宽。
- `StateSpacePlanner` 在普通候选后增加四个基于 JumpHeight 的松键/再按下时间候选，覆盖不同上升/下降阶段。不是完整逐帧空中 A*，不使用固定下降速度阈值。
- Surface Graph 只扩大已建模能力的连接候选范围（水平上限 256 像素、额外上升估计），完整路线仍由现有物理规划器和原生 Collision 验证。原有 Walk / StepUp / StepDown 与预算机制不变。
- 搜索学习键包括触发时间；实际失败记忆也区分额外跳跃时机，避免把不同输入序列混为同一失败边。续接按真实状态重新模拟原序列，不把两段 Jump=true 帧数误当成连续按住。

### 真实触发、版本与恢复

原生 `NativeMotionWorld.Observe` 从同一动态快照读取已建模资源，保留真实 player.jump / releaseJump。执行仍只写现有控制输入；不设置 releaseJump、不调用 PerformJump、不直接改速度或位置来触发。

`LocalMotionController.After` 对 ExtraJump 计划逐帧比较实际 Active、Available、jump 计数和输入释放状态；原有位置/速度误差门继续使用。按键请求不等于激活。Active=true 且 Available=true 仍表示已经激活；若与 Cloud 模型预期资源不同，则按资源偏差处理，不假定可以无限重复。

Phase 4.1 原始采样保持真实值。只有当前实际状态与已执行计划的上一帧匹配，才允许 `MobilityTracker` 将 Cloud 的预期 Available / Active 转换作为资源演进消费，保持 Revision。Enabled、注册项/顺序、block、有效物理参数及其他离散能力状态仍严格比较。落地前后的原生资源刷新允许在真实有支撑的边界核对；资源突变不被无条件忽略。

触发失败、counter 不符、碰撞或路线偏移继续进入原有 NavigationTask 释放输入、真实安全检查点确认、恢复与重规划。不会复活终态或 unknown，也不在已失效轨迹上继续按旧输入。落在其他安全支撑处由原有恢复机制从实际状态接续；没有额外的空中完整搜索器。

### 对设计文字的必要澄清

1. 模型必须显式选定已核验能力；动态发现注册项本身不足以推导轨迹。MVP 采用 Cloud 的小型模型适配，不建立装备数据库或通用 Mod 预测框架。
2. 原生资源刷新在 PostUpdateRunSpeeds 之前，触发在之后，jump=0 后的 Active 清理也在更新中发生。预期状态核验必须区分采集边界和更新后观察，不能看到 Available 改变就统一 InvalidateMobility。
3. 两版原生还可能在 velocity.Y=0 等情况下刷新跳跃。MVP 只在真实安全支撑处把边界刷新视为正常接续；碰撞等导致的意外空中资源变化仍按偏差恢复，不由此推断可重复空中动作。

## 修改文件

生产代码：

- 新增 `CompanionBridge/Motion/ExtraJumpModel.cs`。
- 扩展 `PhysicsSimulator.cs`、`StateSpacePlanner.cs`、`SurfaceGraph.cs`、`NativeMotionWorld.cs`。
- 扩展 `LocalMotionController.cs`、`NavigationTask.cs`、`NavigationKnowledge.cs`、`NavigationFailureMemory.cs`。
- 在既有 `GameNavigation.cs`、`MobilityProfile.cs`、`MovementSnapshot.cs` 中接入模型准入与经过核验的预期资源转换。

验证入口：新增 `tests/ExtraJumpNavigationTests.cs`、`tests/native_extra_jump.py`；扩展 `run_csharp_tests.ps1`、`verify_navigation_recovery_native.ps1`、`verify_movement_engine_contract.ps1`、`ServerAiPackageTests.cs`。`ServerAiTerrainFixture.cs` 的装备、地形、触发抑制和位移注入仅在既有隔离复制世界加 `-companionextrajumptest` 门控下执行，不能成为正式执行方式。

## 定向验证

没有运行仓库全量测试；未部署到现用 profile，没有调用模型。

| 检查 | 实际结果 |
| --- | --- |
| `run_csharp_tests.ps1 -MobilityOnly` | Phase 4.1 快照 116、既有 Mobility 70、ExtraJump 28 项通过。覆盖资源不重复消费、Active+Available、预期/意外变化、counter、触发失败、位移、不同安全落点与模拟顶棚接触后的恢复。 |
| `run_csharp_tests.ps1 -NavigationOnly` | Phase 1–3 定向回归通过：局部改造 143、Surface Graph 1288、Trip 68、Recovery 111、Planner 187、Perception 44 及 landing/motion/hook。新增 ExtraJump 独立入口已在后续 MobilityOnly 中运行；最终 NavigationOnly 也接入同一测试。 |
| `verify_movement_engine_contract.ps1 -EngineProfile ...` | 两引擎各 7 项通过：状态 getter 只读、AnyExtraJumpUsable 钩子路径、Cloud 0.75 倍率、空水平钩子及原生启动字段。 |
| 两引擎 `build-companion.ps1` | `2026.09.3.1` 与 `2026.08.3.0` 均 0 errors / 0 warnings。 |
| 两引擎 IPC / Task Runtime / 包检查 | IPC 45、atomic 48、read 68、native adapter 300、Task Runtime 92、chat 30；每包 98 项 IL 检查通过，包括不得从运动层直接调用 ExtraJump 钩子及实际状态确认入口。 |
| Python `test_server_ai_landing.py` | 5 项通过：真实到达/落脚证据、取消和 unknown 禁止重放。 |
| 双引擎隔离原生 ExtraJump | 高 8 格平台、两级高台连续使用、一次触发被抑制、激活后位移、自主 navigate_to 根任务全部完成。正常场景恢复次数为 0；失败/偏移场景各恢复并重规划 1 次。每次真实激活均记录 FullName、Active、Available、vy 和 jump。 |
| 原有 Phase 1–3 原生回归 | 34 个 mine_target、23 个自主导航/Phase 3、20 个双向落脚场景及独立放置通过；18 个落脚场景到达，2 个超高平台预期安全失败。unknown、材料不足、目标变化和串行动作语义符合预期。 |

原生 ExtraJump 均使用正式 Player.Update、正式输入及真实 Collision；图形 Host & Play 未执行。初次根任务测试曾误读既有嵌套回执，修正测试读取层级后复测通过，未通过修改产品结果来绕过断言。

本地证据（不提交）：

- 当前引擎全部五个最终场景：`.local/navigation-recovery-native-c6096c67ec6049b5a4f10d30bf9ddd11/`。
- 上一引擎全部五个场景：`.local/navigation-recovery-native-ef1a7084273e41bb935dd1987bc0a0c9/`。
- 两引擎的正常场景均 0 次恢复，触发失败/偏移各 1 次恢复与 1 次重规划；最后一条成功边的 MaxError 均为 0（不代表偏移帧本身没有偏差）。
- 原有原生回归：`.local/companion-g81-native-b6d8f5f38fd041a6aabbf3ff3722d1dc/result.json`，57 条任务记录、20 条落脚场景、result=passed、exitCode=0。此轮包 SHA256 为 `2E1AE1ABBFBC656FA5139808B5CAF338E6FFA4602E89479E892A76ECAA880EBB`；之后仅补充说明注释、把专项 fixture 从 7 格提高到 8 格，正式导航逻辑不变。最终包另通过上述双引擎 ExtraJump 专项和 IPC/IL 检查。
- 各目录含 `extra-jump-results.json`、原始 Lua 回执、逐帧 terrain-motion trace 和 manifest。两级高台观察到两次不同 Active 起沿，证明实际落地补充后能继续使用。
- 最终专项采用 8 格高度，避开普通满跳加原生 StepUp 的边界；离线同步比较普通模型无完整路线、ExtraJump 模型存在完整安全路线，不以一次搜索失败宣称世界不可达。
- 当前包：`.local/phase4-2-build-current/Mods/CompanionBridge.tmod`，SHA256 `D4FBA2FA94B654A762490187374CDA5FEEBBB3FFE1F4067B652A038473D3E28D`。
- 上一包：`.local/phase4-2-build-previous/Mods/CompanionBridge.tmod`，SHA256 `EECBB53567C34B3A0EFE215579B5E68E1C8A862AAEBA1267EC71B589149934E6`。

## 尚未验证与手动步骤

未做图形 Host & Play、自然地形大跨度跨沟专项、网络延迟/敌人干扰、真实非预期安全平台落点专项、真实顶棚碰撞专项。后两项已用离线实际状态注入覆盖恢复路径，原生位移场景已验证回到真实安全状态后再规划，但不能代替所有平台/碰撞组合。多 ExtraJump、其他原版能力、第三方 Mod、自定义水平钩子、飞行及火箭靴均未纳入支持。

手动图形验证应使用新的隔离 profile / 世界：

1. 通过 `scripts/prepare-companion-host-play.ps1 -PackagePath .local/phase4-2-build-current/Mods/CompanionBridge.tmod` 准备新 profile，按脚本打印的 launcher 进入 Host & Play；核对实际加载包、引擎、Server AI 绑定 Actor 与端口。不要覆盖现用存档。确认控制的是绑定 AI Player，非真人。
2. 只给绑定 Actor 启用一种 Cloud ExtraJump；去掉其他 ExtraJump、翅膀、火箭靴和坐骑。准备无液体/敌人的宽平台，上升 8 Tile、间隔约 6 Tile，目标顶部有足够落脚空间。世界像素 1 Tile=16，y 向下；目的地 y 是脚底高度。
3. 仅提交 `return api('navigate_to',{x=<目标世界像素X>,y=<平台顶部世界像素Y>})`。不通过 Agent 指定松键或跳跃时间。观察普通跳跃、松键、原生 Cloud 激活与真实停稳；检查 SourceTick、能力状态及任务结果，没有真实落脚不得记成功。
4. 再增加一级相同台阶，或把目标移到下一级高台，检查落地恢复 Available 后能再次触发。隔离自动重现命令为 `tests/verify_navigation_recovery_native.ps1 -ExtraJump -Scenes cloud_chain -EngineProfile <版本> -WorldPath <原始测试世界> -PackagePath <对应包>`。
5. 为失败恢复专项使用同脚本 `-Scenes cloud_fail cloud_offset`：显式门控下只抑制一次请求或注入一次位移，观察输入释放、实际安全落点、重规划与到达。图形真实扰动测试可在安全场景中改变装备或路径；若旧模型失效，应释放并报告真实结果，不要求无条件到达。
6. 增设较低且安全的备用平台、低顶棚，分别验证非预期落点与碰撞；保留实际轨迹及回执，与离线证据分开报告。测试取消时确认所有输入释放；终态 unknown 后不要自动重试。

判断：实现与隔离原生证据构成 Cloud 单能力 Phase 4.2 MVP 闭环；不构成完整 Phase 4 或任意 ExtraJump / Mod 的兼容声明。
