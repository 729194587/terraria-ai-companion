# Terraria AI Companion 导航架构设计研究

研究日期：2026-10-21。状态：设计建议，未实施。范围：`codex/agent-trace`，本地 HEAD `f780adfee5553c8344793e13ec8aa5485e7c1a36` 加研究开始时已有的工作区修改。本文只新增文档，不修改生产代码，不提交、推送或操作游戏。

文中 **[现有]** 表示已核对本仓库代码，**[建议]** 表示拟议契约或实现，**[待验证]** 表示 API 文档、离线替身或历史报告尚不足以证明真实游戏行为。上游设计决定只作为参考，不是本项目已实现能力。

## 1. 结论与实际基线

建议沿用 Python Agent → 现有 TCP Bridge → C# 游戏线程控制与局部预测架构。LLM 选择目标、解释场景和决定是否继续；导航系统根据真实身体、当前能力、世界几何与任务约束规划并执行短段，重新观察后续接。先统一事实、能力和结果契约，再将已有越障原型接入 Agent；不另引入导航服务、Lua、Minecraft 运行时或全局寻路框架。

### 1.1 已阅读的本地实现与证据

| 文件与符号 | 已有行为及设计影响 |
| --- | --- |
| [`ObservationReader.Read / ReadPlayer / ReadCell`](../CompanionBridge/Common/Systems/ObservationReader.cs)、[`ObservationSnapshot`](../CompanionBridge/ObservationSnapshot.cs) | 游戏线程按需复制 viewport 范围的客户端已知数据；世界事实在 `pre_DoUpdate`，camera 来自 `PostDrawTiles`。有 world/session、observation、tick、覆盖率和 unknown 标记，不能声称同一渲染帧或服务端权威快照 |
| [`validate_observation`](../python/observation.py) | 校验版本、范围、编码、coverage、实体身份与采样 tick；`valid` 不等于安全或完整 |
| [`project_observation / _terrain`](../python/exploration.py)、[`探索观察说明`](EXPLORATION_OBSERVATION.md) | 一次完整观察的纯投影；语义矩形保留形状、机关、液体与危险属性；`supportColumns` 从角色脚底向下找潜在 solid/solidTop，不检查整个身体净空、可达性或安全落点 |
| [`MovementLoop`](../python/movement_loop.py)、[`Agent._run / TOOLS`](../python/agent.py)、[`阶段 A`](STAGE_A_MOVEMENT.md) | Agent 暴露观察、水平 `move`、`stop_movement`；单次工具调用，不预排动作批次。每动作后重新观察；默认 7 次工具、8 次模型、6000ms 累计请求移动时长。目标为碰撞框左上角 XY；默认到达半径 16px，可设 1–32px |
| [`move / stop_movement`](../python/movement.py) | 50–2000ms 有界水平输入；100ms 轮询/续租；取消精确 actionId，处理启动 ACK 丢失、完成与续租竞争。completed 不承诺位移、抵达或停稳。stop 仅针对 bounded movement，不是所有导航/手动控制的全局停止 |
| [`ControlState`](../CompanionBridge/ControlState.cs)、[`CompanionSystem.UpdateExperiment`](../CompanionBridge/Common/Systems/CompanionSystem.cs) | TCP 排队、游戏线程消费；750ms 控制租约、绝对动作期限、session/generation、chat 取代保护；查询不续租。P 接管、死亡/幽灵、离开世界、断线撤销控制；复活不恢复旧动作 |
| [`CompanionPlayer.PostUpdateRunSpeeds / PostUpdate`](../CompanionBridge/Common/Players/CompanionPlayer.cs) | 在 JumpMovement 前注入输入，之后比较真实状态；原生对象不交给 Python，原生碰撞不能移到后台线程直接调用 |
| [`GameNavigation.Ordinary / Parameters`](../CompanionBridge/Motion/GameNavigation.cs)、[`PhysicsSimulator`](../CompanionBridge/Motion/PhysicsSimulator.cs) | 已读取真实速度参数、基础跳跃参数及 jump/releaseJump；仍用 `IsBasic`、20×42 身体、普通重力/干燥/无装备 Buff 等条件收窄适用范围。不是通用能力感知导航 |
| [`TerrainReader`](../CompanionBridge/Common/Systems/TerrainReader.cs)、[`TerrainClassifier`](../CompanionBridge/TerrainSnapshot.cs)、[`NativeMotionWorld`](../CompanionBridge/Motion/NativeMotionWorld.cs)、[`MotionSupport`](../CompanionBridge/Motion/MotionSupport.cs) | 另有 45×29 局部字符地图与原生碰撞适配；区分半砖、四种斜坡、单向平台，所有液体目前按危险拒绝。落点检查实际脚底投影至少 8px 支撑。grounded 是受限推导，不是 Player 的通用属性 |
| [`StateSpacePlanner.Plan`](../CompanionBridge/Motion/StateSpacePlanner.cs)、[`NavigationTask`](../CompanionBridge/Motion/NavigationTask.cs)、[`navigate.py`](../python/navigate.py) | 已有状态空间搜索与连续越障 CLI；最多 32 次展开、候选间检查 25ms，外层 50ms 保护；每次执行第一条边后从实测状态重搜。只支持水平穿越 targetX，不等于 Agent 的 XY 到达 |

研究开始时已有未提交修改：`docs/STAGE_A_MOVEMENT.md`、`python/agent.py`、`python/movement_loop.py`、`tests/test_movement_loop.py`。本文采用这些实际内容，不回退，也不将其归为本文改动。

**关键区别**：Agent 阶段 A 尚无跳跃/寻路工具，不代表 Bridge 没有跳跃/寻路原型。CLI 的 `navigate` 任务和 Agent 的 `move` 闭环当前并未统一。前者允许沿初始方向越过目标后在安全地面停稳；后者按 XY 欧氏距离核验。将 CLI 的 `arrived` 原样用于精确 XY 目标会错误报成功。

### 1.2 已有测试能证明什么

| 测试/记录 | 已覆盖及证据边界 |
| --- | --- |
| [`test_observation.py`](../tests/test_observation.py)、[`ObservationOfflineTests.cs`](../tests/ObservationOfflineTests.cs) | 编码、相机几何、版本、partial/stale、实体身份、按需一次采样、观察不续租；不能证明所有 Mod 碰撞和同步年龄 |
| [`test_exploration.py`](../tests/test_exploration.py)、[`真实 fixture 来源`](../tests/fixtures/exploration-real-observation.md) | 真实 8470 格、46 实体、329 候选样本逐格保留探索语义；斜坡/半砖/多液体/未知/死亡等组合有合成测试，不能冒充真实场地覆盖 |
| [`test_movement.py`](../tests/test_movement.py)、[`test_movement_loop.py`](../tests/test_movement_loop.py) | ACK 丢失、精确取消、租约竞争、无进展、预算、观察授权 consumed/not_refreshed/invalid、同 tick 新 ID 不算动作后新观察、死亡/代次变化不恢复；位置由 mock 提供，不证明 Terraria 物理 |
| [`BoundedMovementOfflineTests.cs`](../tests/BoundedMovementOfflineTests.cs)、[`DeathLifecycleOfflineTests.cs`](../tests/DeathLifecycleOfflineTests.cs)、[`test_control.py`](../tests/test_control.py) | 游戏线程消费、绝对期限不被续租延长、错误 action/session/generation、死亡撤销及重生后的旧命令拒绝；包含 IPC 测试，不是多人游戏实测 |
| [`MotionOfflineTests.cs`](../tests/MotionOfflineTests.cs) | 共享形状/脚底支撑、左右到达、多障碍、保速交接、偏差失败、危险拒绝、导航租约。大部分碰撞世界是替身；调用共享 `MotionSupport` 仍不等于运行了原生碰撞 |
| [`TERRABLIND_NAVIGATION.md`](TERRABLIND_NAVIGATION.md)、本地 `.local/navigation-0.1.16-success.json` | 历史右向报告 arrived、21 段、3 次跳跃、generation=3；有真实运行依据，但只证明该版本和场地。不是本文重测，也不能外推左向、所有平台/斜坡、多人、中断或装备能力。文档保留早期失败记录，不能把旧“待复测”段落当最新结论 |

## 2. Numen：可借鉴的分层与不可照搬的机制

已读取 `Dwinovo/minecraft-numen` 默认分支 `1.21.1` 的树及下列源码/设计文档，固定研究版本 **`a9c30823682d81d931df58336d6a7ba4e8d0d930`**。链接均固定到该提交，避免后续上游变化混入结论。

| 具体来源 | 实际依据 | 对本项目的决定 |
| --- | --- | --- |
| [空间感知设计](https://github.com/Dwinovo/minecraft-numen/blob/a9c30823682d81d931df58336d6a7ba4e8d0d930/docs/spatial-perception.md)、[LookAround.java](https://github.com/Dwinovo/minecraft-numen/blob/a9c30823682d81d931df58336d6a7ba4e8d0d930/numen/common/src/main/java/com/dwinovo/numen/tools/perception/LookAround.java) | `render/classify` 生成角色中心俯视字符图，`Terrain.standingSpot/fits` 判站位/净空；危险邻域另做 inflation | 借鉴紧凑近场地图和稀疏对象分工。Terraria 使用 XY 侧视完整截面，保留洞穴多层地面和头顶空间；不复制其一格上跳、三格落差或 17×17 默认参数，也不引用其研究收益作为本项目性能结论 |
| [Terrain.java](https://github.com/Dwinovo/minecraft-numen/blob/a9c30823682d81d931df58336d6a7ba4e8d0d930/numen/common/src/main/java/com/dwinovo/numen/nav/Terrain.java)、[pathing.md](https://github.com/Dwinovo/minecraft-numen/blob/a9c30823682d81d931df58336d6a7ba4e8d0d930/docs/pathing.md) | Terrain 是绑定真实身体的几何门面；感知、规划、执行共享底层。规划查询与执行任务分开；到达由目标自身定义 | 复用现有 IMotionWorld/形状与支撑函数，逐步让语义推导共用判据。Python 不另写一套“几格高就能跳”的规则；不导入完整独立 Gradle 模块 |
| [ADR 0016](https://github.com/Dwinovo/minecraft-numen/blob/a9c30823682d81d931df58336d6a7ba4e8d0d930/docs/adr/pathing/0016-model-says-where-only.md)、[MoveToCompanionTask.java](https://github.com/Dwinovo/minecraft-numen/blob/a9c30823682d81d931df58336d6a7ba4e8d0d930/numen/common/src/main/java/com/dwinovo/numen/task/move/MoveToCompanionTask.java) | 模型给目标；任务持有 destination，将行走交 Trip；按进展延长任务期限但有交还控制的上限 | LLM 不传路径、跳跃高度或逐帧按键。Numen 默认可挖可搭不适用本项目：导航初期只移动，采集/挖掘是未来显式动作。其 30 秒进展期限是任务层机制，不替代本项目 750ms 控制租约 |
| [ADR 0012](https://github.com/Dwinovo/minecraft-numen/blob/a9c30823682d81d931df58336d6a7ba4e8d0d930/docs/adr/pathing/0012-plan-subset-of-execution.md)、[修订 ADR 0020](https://github.com/Dwinovo/minecraft-numen/blob/a9c30823682d81d931df58336d6a7ba4e8d0d930/docs/adr/pathing/0020-segments-learning-closed-loop.md)、[ADR 0022](https://github.com/Dwinovo/minecraft-numen/blob/a9c30823682d81d931df58336d6a7ba4e8d0d930/docs/adr/pathing/0022-reliability-matrix.md) | 从“规划成立则执行成功”收敛为保守规划、执行闭环；矩阵与有限终止性测试发现不一致 | 不将静态可达预测当保证；每段验证、有限重规划。先保留本项目预算/无进展上限，不照搬直挖直垫、学习表或节点数 |
| [Navigation.java](https://github.com/Dwinovo/minecraft-numen/blob/a9c30823682d81d931df58336d6a7ba4e8d0d930/numen/pathing/src/main/java/com/dwinovo/numen/pathing/api/Navigation.java)、[Walked.java](https://github.com/Dwinovo/minecraft-numen/blob/a9c30823682d81d931df58336d6a7ba4e8d0d930/numen/common/src/main/java/com/dwinovo/numen/nav/Walked.java)、[MoveToTaskRecord.java](https://github.com/Dwinovo/minecraft-numen/blob/a9c30823682d81d931df58336d6a7ba4e8d0d930/numen/common/src/main/java/com/dwinovo/numen/task/move/MoveToTaskRecord.java) | tick/stop/report 分离；停止也交实际账；Walked 包含实际位置、剩余距离、挖放数量与裁决，失败仍可携带这些事实 | 统一结果把执行状态、世界差分、目标判定拆开。取消不抹掉已发生的效果；返回结构化事实，由 Agent 生成人话 |

Numen 是服务端 Minecraft 玩家、三维碰撞与独立 pathing 模块；这里控制 Terraria 客户端 LocalPlayer，含单向平台、持续按住跳跃、斜坡、半砖和装备/Buff/Mod 更新周期。两者坐标、tick 频率、碰撞 API、危险及能力恢复机制均不兼容。借鉴职责与证据规则，不移植机制常量或“保证可达”的前提。

## 3. 局部语义地图

### 3.1 三层数据，避免事实被路径结论覆盖

**[建议] F：空间事实层**沿用完整 observation 与 exploration 的有损投影，不把图上空白解释为可走。身份使用 `worldSessionId + observationId + gameUpdate + phase`，保留 bounds/tileSize、camera 来源与 completeness。世界像素 +X 向右、+Y 向下，Tile=16px；矩形边界右/下不含。角色精确位置继续用碰撞框左上角，地图可另有 feet 锚点但必须命名，不能隐式混用。

**[建议] G：身体相关几何层**由 F、实际 AABB、重力方向和共享碰撞判据推导 `clearance`、`supportSurfaces`、`standable`；每条结论携带来源与 unknown 原因。空净空表示身体放得下，不表示有支撑；可站立表示某姿态可被托住，不表示从当前位置能过去，更不表示无危险。

**[建议] N：导航层**再结合 Capability Profile 与任务风险策略推导 reachable/unknown/blocked、短段动作、危险余量和前沿。`safe` 只在明确能力、路径及策略上下文中成立，不永久写回 Tile 属性。派生缓存键至少含世界、地形采样、body/能力版本、策略版本；执行前仍读当前游戏状态复核。

| 对象 | F 保留的原始事实 | G/N 推导与限制 |
| --- | --- | --- |
| 实体方块（实心 Tile） | hasTile、solid、actuated、collisionKnowledge | 非 actuated 才参与当前碰撞；有 Tile 不等于实心。背景墙不默认阻挡；机关可能改变的风险另存 |
| 半砖 | halfBlock、原 Tile 范围 | 原生形状/共享区间求支撑像素高度与净空，不把整个 Tile 当 16px 实心；身体 swept AABB 也要检查 |
| 斜坡 | slope 原值，必要时 type/frame | 四种三角形含斜底，不能用一个“坡可走”布尔。与 TerrainClassifier 的方向定义对齐；落点/贴坡、上/下坡分别测 |
| 平台 | solidTop、solid、形状、必要 Frame | 单向支撑要看运动方向、脚底上一位置、重力及 controlDown/stairFall；不阻挡横穿/上穿不等于可随意下穿。初期不支持主动下穿 |
| 液体 | liquidType、liquidAmount；wet 等是角色状态 | 液体与方块可并存；分别保留水/熔岩/蜂蜜/微光，不合并为通行类别。液面、阻力、呼吸、伤害是能力相关推导；初期全部拒入 |
| 危险 | touchDamage、hot、bleeding、destroyOnTouch、boulder、实体/projectile 证据 | 原始危险不因抗性消失。暴露代价、膨胀余量、是否禁止由当前策略/能力决定；机关、Mod hooks、未来落石和敌人轨迹未被完整识别 |
| 可站立区域 | 不作为原始 Tile 事实 | 一列可以有多个像素高度的 surface interval；需要支撑足宽、身体净空及重力。共享 MotionSupport 起步，8px 是当前原型阈值，后续按身体与测试校准 |
| 角色通行空间 | boundsWorldPixels、velocity、movement | 身体配置空间与轨迹扫掠检查；空气可能没有落脚点，狭缝也可能放不下身体。不能只检查中心格 |
| 未知区域 | unloaded、out_of_world、unknown_mod_hooks、范围之外 | 未加载与未采样为未知；世界外作为已知边界禁止进入；Mod 已读事实但效果未知。unknown 不套 read 默认值，不按空气补洞 |

**已发现的投影缺口**：exploration 对普通 vanilla Tile 省略 type/frame，而 `TerrainClassifier.Classify` 对 `solidTop && !solid` 的家具帧用 `frameY == 0` 才认支撑。仅凭现有矩形无法无歧义重建这些精确平台/家具支撑；`supportColumns` 也未做该筛选。应由 C# 共用几何导出必要 `supportGeometryKnowledge`/支撑面，或通过完整观察取得 frame 精查。不得假装 Python 能从现有投影独立判全。地图的事实分类可以先做，准确导航派生延后接共享实现。

### 3.2 给 LLM 的表示与底层保留数据

**[建议]** 在现有探索结果上增加可选、带独立版本的 `localMap` 派生区；保持 v1 矩形解码语义。先不改 C# wire schema；新增能力/几何字段时另协商协议 feature，旧 Bridge 缺失则标 unknown。裁剪只影响展示，必须返回 crop bounds 与覆盖说明，不能静默丢失危险或实体后仍宣称完整。

LLM 默认看：较小 XY 侧视图（初始候选 33×21 格，大小待 token/理解测试）、精确世界原点、角色 AABB/feet、单位和图例；少量编号地标/候选、相对距离、可站面、多层通道、未知边界；当前受支持能力摘要；已计算路线的可行/阻塞/未知与原因，不看逐帧轨迹。该尺寸仅是展示预算，不替代已有 45×29 预测窗口，也不允许读取 observation 范围之外。

展示采用事实底图和派生叠层：底图 `.`=已读且无阻挡的空间（非可走）、`#`=实心、`h`=半砖、`1..4`=斜坡、`=`=单向平台、`?`=未知、`X`=世界外；液体和危险用独立稀疏列表或单独行层，防止一字符覆盖复合事实。`@` 只定位角色，仍保留其格子底层内容。派生叠层列出带 ID 的支撑面、候选站位、可行短段/不确定项，不借用 Numen 的 `^` 表示“必可跳”。

示意底图（仅说明结构，非真实观察）：

```text
       +X →                  originTile=(100,200), tileSize=16
....#####....????
.............????
....===......????
..@.....h12..????
######.......XXXX
```

图例必须说明空气、平台与未知的差别；站在平台下和平台上是不同候选节点。底层保存所有精确 Tile、斜坡/半砖几何、液体量、type/frame、swept AABB、动态身体参数、能力资源、预测帧与误差、搜索拒绝原因；普通 LLM 不必接收调色板/逐格索引、所有装备/Buff 或碰撞实现细节。需要材质、挖掘目标或特殊 Frame 时调用现有完整观察；未来局部精查接口应限定 bounds/fields，并携带同样 freshness/coverage，当前并不存在该工具。

矩形版不立即替换；先用同一真实 fixture 比较矩形与小图的字节/实际 tokenizer 成本、地形问答正确率与 unknown 误判率。不能用 JSON 压缩比承诺 token 或导航收益。

## 4. 动态角色能力

### 4.1 已有读取、候选 API 与可靠性

API 研究同时核对本机 `D:/steam/steamapps/common/tModLoader/tModLoader.xml`（如 `AnyExtraJumpUsable`、`ExtraJumpState`、`fireWalk`、`lavaTime` 的成员说明）与官方在线文档。在线 stable 页面本次显示 v2026.08；本仓库历史构建记录为 tModLoader 2026.08.3.0 / Terraria 1.4.4.9。文档中存在 API 不代表本轮编译或实测了新读取逻辑。

| 能力/状态 | 本仓库已确认读取 | 后续可靠来源及尚不能确认的部分 |
| --- | --- | --- |
| 身体与基础水平移动 | observation 的真实 AABB/速度、runAcceleration/maxRunSpeed/gravity/maxFallSpeed；GameNavigation 另读 accRunSpeed/runSlowdown | 使用执行阶段最终参数，而非装备名或单个 moveSpeed 倍率。不同表面、坐骑、Mod 会改变模型；当前预测器只接受 Basic。速度是瞬时状态，最大速度/加速度是模型参数，均有单位 |
| 基础跳跃 | observation 有 jump/controlJump；GameNavigation.State 有 jump/releaseJump 与 TerrainReader 推导 grounded；Parameters 读 `Player.jumpSpeed/jumpHeight` | 地面支撑 + releaseJump + 受限状态 + 真实跳跃参数共同决定能否起跳；jump 不是“剩余跳跃次数”。jumpSpeedBoost 等额外参数尚未进入现有预测模型，不可仅叠一个数就声称已支持 |
| 额外跳跃 | `Ordinary` 调用 AnyExtraJumpUsable，仅用于拒绝普通模型；未对外暴露每种跳跃状态 | 逐种 `Player.GetJumpState` 的 Enabled/Available/Active，并考虑 blockExtraJumps、CanStart 与执行顺序。AnyExtraJumpUsable=false 不能证明角色从无二段跳，也可能已消耗/被禁用。无统一“n 次跳”可描述所有 Mod 可复用跳跃 |
| 翅膀/火箭飞行 | Ordinary 读取 wingsLogic/rocketBoots 做排除；完整观察只能看到装备存在 | wingsLogic、wingTime/wingTimeMax，火箭另需 rocketTime/延迟/释放门控等状态及对应运动实现。装备或可视 wings 不证明可飞；有资源也不证明当前输入将进入飞行。无限飞行、坐骑和 Mod 行为无通用确认方式 |
| 液体运动、呼吸 | observation 有 wet/lavaWet/honeyWet/shimmerWet；当前导航拒绝液体 | 候选读取呼吸量/消耗阶段、液体相关有效标志及对应运动规则。氧气值不能直接当秒数；游泳、走液面和水下呼吸分开确认；微光的特殊效果不能当普通水 |
| 危险抗性/摔落 | 当前没有通用抗性 profile；有 life、Buff 原始槽位与时长、Tile 危险事实；导航掉血就失败 | 分危险核对有效标志与资源：例如 fireWalk 对热 Tile、lavaImmune/lavaTime 对熔岩、noFallDmg/extraFall 对摔落；抗热不等于免熔岩/尖刺，临时免伤不等于安全策略允许进入。Mod 改伤害 hooks 不能由这些字段穷尽 |
| 药水/饰品/Mod | 完整观察有 vanilla armor/misc/buff 数组、contentKey、buffTime；不含 Mod 饰品槽，customState 未解释 | 装备只作来源线索，效果以游戏更新后的有效状态为准。Buff 剩余时间不是该能力唯一来源的到期时间；多个来源叠加必须由专门适配器说明。未知 Mod 能力使用带来源的扩展，不根据名字猜 |

官方依据：[Player API](https://docs.tmodloader.net/docs/stable/class_player.html)、[ExtraJumpState](https://docs.tmodloader.net/docs/stable/struct_extra_jump_state.html)、[ExtraJump](https://docs.tmodloader.net/docs/stable/class_extra_jump.html)。其中 Enabled 会在 ResetEffects 清除，Available 与 Active 是不同状态；CanStart 还有额外条件。这是必须在正确阶段采样、逐次复核的直接理由。

### 4.2 采样阶段与信任边界

**[现有]** 观察 `pre_DoUpdate` 与控制 `PostUpdateRunSpeeds` 不同阶段；只给观察增加若干 Player 字段不能消除时序差异。**[建议]** C# 在现有执行钩子、有效参数已更新的位置生成 detached CapabilitySample，带自己的 `sampleGameUpdate/phase`；观察引用最近样本并明确 age，不能把旧样本重标为观察 tick。启动前由执行器再采当前状态，参数/身体不符则重规划或拒绝。Mod 钩子先后顺序尚需按安装版本核对；不承诺 PostUpdateRunSpeeds 后无其他 Mod 修改。

对静态模型支持、实时可用性和资源分别编号/更新：`profileRevision` 只在身体/有效模型或适配器配置变化时更新；动态资源每样本更新。规划基于引用版本与资源快照；执行逐 tick 检查消耗、受限状态和模型漂移，不能只验证 revision 而忽略资源减少。若将来扩展空中搜索，节点须含未来剩余跳跃/飞行资源，不能用搜索起点资源无限复用每条边。

### 4.3 最小 Capability Profile v1（建议契约，不是当前返回值）

只要求基础水平移动、地面跳跃和受支持模型判定。公共事实槽为 `{knowledge: known|unknown, value, source, reason}`；unknown 的 value 必须为 null，缺失不能默认 false/0。每个能力另有 `plannerSupport: supported|unsupported|unknown`，避免把“真实存在”混同为“导航会用”。

```json
{
  "schemaVersion": 1,
  "worldSessionId": "world",
  "playerId": 0,
  "profileRevision": 1,
  "sample": {"gameUpdate": 100, "phase": "PostUpdateRunSpeeds", "sampleId": "cap:100"},
  "units": {"position": "world_pixels", "speed": "pixels_per_tick", "acceleration": "pixels_per_tick_squared", "time": "game_ticks"},
  "body": {"width": 20, "height": 42, "gravDir": 1},
  "model": {"id": "ordinary_dry_v1", "eligibility": "confirmed", "reason": null},
  "state": {
    "alive": true, "velocity": {"x": 0, "y": 0},
    "groundContact": {"knowledge": "known", "value": true, "source": "shared_support_check", "reason": null},
    "releaseJump": true, "jumpCounter": 0, "restrictions": []
  },
  "abilities": {
    "walk": {
      "confirmed": true, "usableNow": true, "plannerSupport": "supported",
      "parameters": {"runAcceleration": 0.08, "maxRunSpeed": 3, "accRunSpeed": 3, "runSlowdown": 0.2},
      "resource": {"kind": "not_applicable"}, "duration": {"kind": "unknown", "ticks": null}
    },
    "groundJump": {
      "confirmed": true, "usableNow": true, "plannerSupport": "supported",
      "parameters": {"jumpSpeed": 5.01, "jumpHoldTicks": 15, "gravity": 0.4, "maxFallSpeed": 10.01},
      "resource": {"kind": "ground_contact_gate"}, "duration": {"kind": "unknown", "ticks": null}
    }
  },
  "extensions": {},
  "unknown": ["extra_jump_profile", "flight_profile", "liquid_resistance", "mod_movement"]
}
```

上例是通过 Basic 严格门禁的示意，数值源自当前 `PhysicsParameters.Basic`，不是所有玩家默认值，也不是新的协议常量。`confirmed/usableNow` 是 true/false/null 三态，null 表示未知并带 reason；整个 sample 提供来源阶段，参数非有效读取时应改用公共未知槽而非填 Basic。`groundContact` 是指定模型下可解释的推导，不声称通用 grounded API。

基础跳跃资源不是一个可自由消耗的计数；必须满足地面接触与松开后再按的门控。`jumpHoldTicks` 是模型的最大持续按键参数，`jumpCounter` 是当前持续跳跃状态；二者都不代表 Buff 时长。duration 表示能力有效期保证，默认未知；不得因为无 Buff 就填 infinite。拓展可按 contentKey 注册 extraJump/flight/resistance 事实、剩余量和到期上界；参数与机制未验证时 plannerSupport=unsupported，先拒绝依赖它的边。

**边的可行性**应为 `geometry × supported motion model × current state/resource × risk policy`，结果用 feasible/infeasible/unknown。当前 Basic 门禁是合理的暂时支持边界；后续逐个模型扩展，不直接删除 Ordinary 保护。跳高/距离由参数、起速、按住时间和碰撞模拟求出；速度不可从固定 tiles/ms 换算；危险区域依据具体抗性资源和策略处理。未支持液体时不因“装备有免疫”放行。

## 5. 导航与 Agent 分工及最小局部导航

```text
LLM：理解请求 → 选择地标/目标类型 → 决定继续、换策略或结束
  ↓ 目标 + 观察引用 + 有界预算（风险策略由宿主约束）
Python：现有对话/工具调度、观察授权、代次与预算、结果呈现
  ↓ 现有 Bridge 命令与续租
C#：能力采样 → 共享几何 → 局部规划 → 首段执行 → 真实反馈 → 再规划
  ↓ 输入只写 Main.LocalPlayer；租约、P、死亡等控制撤销始终优先
```

| 层 | 负责 | 不交给该层的决定 |
| --- | --- | --- |
| LLM | 去哪、为什么、目标不明时调查、阻塞后是否请求别的任务/结束 | 逐帧碰撞、跳跃高度/时序、资源消耗模型、凭装备猜能力、声称抵达 |
| Python Agent/监督者 | 将请求映射到明确目标；核对观察/控制身份；总预算、取消与用户新消息优先；更新历史 | 不另建与 C# 不一致的可达性规则，不用阻塞的模型调用续控制租约 |
| 通用局部导航 | 依能力参数生成候选边、检查净空/支撑/扫掠、有限搜索、执行与反馈、必要的受限重规划 | 不改变用户任务意图，不自动挖掘/放置，不扩大危险容忍度 |
| Bridge 控制层 | session/action/owner、generation、死手租约、绝对期限与输入释放 | 不由 LLM 文本覆盖终态，不把取消当撤销已发生世界变化 |

### 5.1 最小可行方案

**[建议]** 保留现有 PhysicsSimulator、IMotionWorld、StateSpacePlanner、NavigationTask、LocalMotionController 的职责。先支持普通干燥角色、普通重力、已知静态地形、无世界修改，目标限于局部窗口内可解释站位/水平穿越。每段从真实位置、速度、jump/releaseJump 与能力样本开始；搜索可回溯多边，只执行首段，校验后重搜。平地优先步行，跳跃是参数化模拟候选，不让 LLM 逐次发 jump 来“试墙”。

LLM 请求最终可收敛为 `navigate_local(goal, observationRef, budget)`；这是未来接口，当前不要将已有实验 CLI 宣称为该工具。goal 明确区分 `horizontal_crossing`、`position_xy`、未来 `near_object`；分别携带坐标约定、容差和是否要求落地停稳。沿用当前阶段 A 目标不可中途改写规则；未来动态跟随应显式 retarget 并更新任务版本，不悄悄迁就当前位置。

现有 45×29 预测窗口读的是角色附近当前游戏 Tile，未必完全落在探索 viewport 内。默认 Agent 新入口建议限制轨迹在已授权观察范围与执行窗口交集；否则需返回显式导航采样 bounds/身份供监督核验，不能宣称依据同一观察。原生碰撞需邻格，未知邻格也必须拒绝。无需先做全世界地图、D*、学习系统或独立线程池；若后续 CPU 预算不足，先量耗时，再在 detached 数据上考虑后台纯搜索，原生碰撞仍留游戏线程。

### 5.2 进展、预算和失败

现有 32 节点/25ms、50ms 保护不是严格实时调度；一个候选内部不抢占。后续保留展开数预算便于离线复现，并记录 wall-clock 超时作为保护与独立停因。`search_budget_exhausted`、`outside_known_scope`、`unsupported_capability` 与 `no_route_in_local_model` 分开；窗口内找不到路不证明全球不可达。

保留 Stage A 两次无至少 1px 进展的保护，直到替换为显式导航任务；不能照搬给允许绕路的通用导航。当前 NavigationTask 四段未刷新沿目标方向 4px、最多 256 段，Python CLI 还有 5 秒无前进保护；这些是原型边界，可能拒绝先远离目标的合法绕路。扩展时用任务内经过的站位/边、有效路径进展和总时间/动作预算防循环；只有确定局部阻塞时才记录带地形/能力版本的失败边，不把一次外部推挤永久写成障碍。

控制接管、unknown、死亡/世界变化终止后不自动恢复。地形变化或预测偏差只有在仍持有同一控制权、已停止旧段并取得当前证据时，才可在导航内部有限重规划；不能以“自动脱困”为由重试未知的挖掘或覆盖手动控制。取消松开注入输入，空中惯性/重力会继续，不能保证物理立即静止。

## 6. 统一动作结果契约

### 6.1 分开五个问题

**[建议] ActionResult v1** 是 Python/Bridge 的渐进适配目标，不在本轮改现有字段。动作 `move/jump/collect/mine/stop` 共用外壳，effects 与 goal 是动作特有的内容。仅一个 `success` 无法表达“按键完成但撞墙”“取消前已经挖掉一格”“物品消失但未知被谁拾取”。

| 维度 | 最小字段及语义 |
| --- | --- |
| 执行 | execution.status=`not_started/rejected/running/completed/cancelled/failed/unknown`；confirmed 表示执行端是否确认该状态；inputReleased=true/false/null。completed 仅表示该动作执行协议结束 |
| 世界变化 | effects.knowledge=`known/partial/unknown/not_applicable`；observedDelta 含实际位置/速度/生命、背包变化、Tile 变化等适用项，携带前后观察引用。observed change 与 attributedToAction=true/false/null 分开；多人变化不能全归因自己 |
| 目标完成 | goal.kind、不可变目标规格；status=`achieved/not_achieved/unknown/not_applicable`；evidenceRef、distanceRemaining（适用才有）。未知不填 0，采集数量与挖掉 Tile 是不同目标 |
| 停因/失败 | reason.code、phase、details、retryPolicy。区分拒绝、碰撞阻塞、能力不足、资源耗尽、预算、lease_expired、manual_override、world_changed、owner_changed、observation_stale、transport_unknown |
| 观察要求 | observation.required、purpose、afterGameUpdate、worldSessionId、expectedControlGeneration、requiredFields/scope；标明 post_action_verification 与 pre_next_action，不能用“有位置”代替所需证据 |

基础外壳还需 `schemaVersion/actionKind/requestId/actionId/chatId/worldSessionId/controlSessionId/ownership`；ownership 包含开始/结束 generation 和是否仍持有控制。requestId 是传输关联，actionId 是执行身份，worldSessionId 是观察世界身份，三者不可互换。时间含执行起止 gameUpdate/phase 以及墙钟耗时；当前回执缺失的字段标 unknown，不从时长估 tick。

示例：限时右移结束、已有粗位置变化，但尚未读取新的完整地形观察：

```json
{
  "schemaVersion": 1, "actionKind": "move", "requestId": 7, "actionId": 3,
  "worldSessionId": "world", "controlSessionId": 1,
  "ownership": {"generationBefore": 0, "generationAfter": 2, "heldNow": false},
  "execution": {"status": "completed", "confirmed": true, "inputReleased": true},
  "effects": {
    "knowledge": "partial", "source": "bridge_before_after_state",
    "observedDelta": {"positionWorldPixels": {"x": 20, "y": 0}},
    "attributedToAction": null
  },
  "goal": {"kind": "position_xy", "status": "unknown", "evidenceRef": null, "distanceRemaining": null},
  "reason": {"code": "duration_elapsed", "phase": "execution", "retryPolicy": "observe_then_decide"},
  "observation": {"required": true, "purpose": ["post_action_verification", "pre_next_action"], "afterGameUpdate": null, "expectedControlGeneration": 2, "requiredFields": ["player", "terrain"]}
}
```

示例中的 +2 仅对应当前 bounded movement 启动/释放各一次代次变化。不要把它定为所有动作的不变量：导航续租同 generation，停止/接管另变化。未来统一契约应由服务端明确返回拥有权转换记录；迁移前 MovementLoop 的 before+2 检查继续保留，不能通过放松校验“兼容”新任务。

### 6.2 迁移与异常规则

| 场景 | 结果与后续 |
| --- | --- |
| 现有 move completed | 适配 execution.completed，现有 before/after 只能作 partial effects；goal 等待 fresh observation 验证，不从 serverConfirmed 推导服务端权威世界结果 |
| stop_movement 无动作 | stop 请求本身可视为 completed/no_op；被停对象为 null，不伪造 move completed。若返回的是历史终态，要区分 stopOperation 与 targetAction；不能覆盖已确认 arrived/cancelled 的任务结论 |
| 取消 | 有 ACK 才确认 execution.cancelled/inputReleased；effects 保留取消前实测变化。停止不回滚跳跃、位移、掉落或挖掘。无 ACK 则 unknown，不能确认已取消 |
| 启动 ACK 丢失/连接超时 | execution.unknown；若 actionId 未知不猜一个，不盲重发；停止续租，由原有租约/绝对期限兜底。世界同步状态未知，重新观察也不能证明旧请求从未执行 |
| P/其他 owner 接管、generation/session 变化 | 明确撤销证据时 cancelled/owner_changed；无法确认旧动作状态则 unknown。旧控制者只取消其精确 action，不用无条件 stop 覆盖新控制。旧目标作废，新用户任务需重新观察 |
| 过期/复用观察 | 记录 observation_stale/not_newer，goal=unknown；在同一世界、同一拥有权下要求新观察。不能把旧结果改为成功，也不能用 get_state 为下一动作授权 |
| failed 但有变化 | execution.failed + effects.known/partial 合法；例如挖了第一格后第二格失败，返回实际已改格，不把计划数量记为产出 |
| 物品/实体目标消失 | 目标消失仅作观察事实；没有背包/数量或适当归因证据，collect 的 goal 仍 unknown/not_achieved，不能当拾取成功。instanceKey 当前仅 observation-scoped，不能跨观察凭 slot 当同一实体 |

**新鲜度规则**：现有闭环要求动作后观察比动作前更大 gameUpdate、不同 observationId、同世界、预期 generation，且 recent_render、ok/partial、有效 terrain。它未在动作回执中返回结束 tick，因此不能严格证明观察晚于完成时刻。建议统一结果增加 `endedAtGameUpdate`；验证观察应晚于执行结束 tick（相同 tick 只有明确且可比较的后置阶段才允许）。30 秒是现有授权超时上限，不是动态场景安全保证；执行器每 tick 复核，危险/敌人需要更短寿命，具体阈值实测决定。

partial 可用于已知局部，但未知格不能进入所需扫掠与落点范围；“地图里有 terrain”不足以证明整条路安全。跨世界、控制变化和不可用观察优先终止。若本次循环已终止，补观察用于解释，不自动恢复旧动作。现有查询后 `not_refreshed` 的严格授权策略保持，重新观察不重置原有接管/世界/年龄证据。

未来外部动作应有客户端生成、按 world/session 作用域去重的 request key 及有界终态查询缓存；传输 requestId 单调递增当前不是动作幂等保证。该扩展是未来挖掘/采集可靠恢复的依赖，当前 unknown 的默认仍是终止而非重试。对 unknown 可补充世界差分证据，但不伪造确定的执行终态或强因果归属。

## 7. 渐进实施路线与验收

以下是新增/补齐工作；已有原型不重写。各阶段通过离线门槛后才跑真实游戏；本轮不执行这些游戏动作。建议所有真实验收保存版本、角色状态、输入目标、前后 observation/profile 引用、控制身份、动作/导航结果与生命变化，区分真实记录与替身。

| 阶段 | 最小范围与依赖 | 离线测试门槛 | 真实游戏验收标准 |
| --- | --- | --- | --- |
| A：已有平地闭环，补齐证据契约 | 保持 move/stop 和预算；先做 ActionResult 适配与明确目标语义，不开放跳跃。依赖现有观察/代次机制 | 完成但撞墙→未抵达；unknown 无重试；stop 不覆盖终态；新消息/死亡/世界变化撤销；同 tick 新 ID/只读状态不授权。保留现有相关测试 | 安全干燥平地左右短移与接近目标；惯性/墙前不误报；P/新消息/死亡复活后旧任务不恢复；记录真实前后观察，不只看模型文字 |
| A1：语义地图与基础 profile | 从现有探索事实做可选小图；基础 profile 采样和 unknown/unsupported；不增加动作范围。依赖共享支撑/Frame 缺口处理 | 同 fixture 逐格事实不丢；多层洞/平台上下一图可区分；半砖/斜坡/未知不变空气；不同 phase 不冒充同步；缺字段不填 Basic；社交槽装备不授权能力 | 对照平地、低顶、半砖/坡/平台读取，验证地图与 AABB；记录 pre_DoUpdate 与执行阶段参数差异。装备/Buff 改变只能如实标 unsupported，不能仍报 Basic 可用 |
| B：简单越障 | 将已有局部预测/导航能力包成有界 Agent 工具；先普通干燥 Basic、局部已知、无改世界；明确 horizontal_crossing 与 XY 的不同，统一结果/取消。依赖 A1 和执行器拥有权适配 | 复用 MotionOfflineTests，增加工具入口→C# 反馈→新观察链路；左右两障碍、头顶碰撞、平台/斜坡、支撑边缘；窗口/搜索预算分开停因；失租/接管精确取消 | 左右各重复通过两处简单障碍并验证落地/最终停稳；从障碍顶下行继续；高墙、低顶、液体/未知拒绝；P、脚本断开、游戏退出均有限终止不恢复。历史右向一次成功不替代此矩阵 |
| C：能力感知导航 | 先扩展已验证的跑速/地面跳跃参数模型；再独立增加一种额外跳跃或飞行模型，不一次全开。依赖有效阶段采样、资源状态与边节点资源、模型偏差保护 | 同一地图不同参数得出不同可行边；Enabled/Available/Active、资源耗尽/补充、Buff 到期/多个来源、换装/重力/坐骑/Mod unknown；预测不能在每条边恢复资源 | 对无装备、速度/跳跃效果、额外跳跃消耗/落地刷新做可重复轨迹对照；执行中 Buff 到期/换装时停止或重规划。飞行和液体模型各自单独验收，资源不足不能进入不可恢复区域 |
| D：有限自主探索 | 在已验证导航上选局部未知前沿，记录同世界已访问区域/失败边，短程推进后重观察；不先做完整全球最短路。依赖 C 的已支持子集、任务与风险预算、跨观察地标身份方案 | 已知地形→前沿目标；未知不当空气；死胡同/循环有限结束；合法绕路不被距离单调性误杀；变世界清记忆；陈旧地图与实体身份不会授权行动 | 受控地表/小洞穴多个前沿连续推进；高墙/未知/危险前停下并说明；生命减少、资源过低、预算耗尽交还控制；重复路线不无限撞墙。探索成功定义为新增有效观测范围，不能声称已知屏外地形 |

每阶段运行最相关 Python 测试与 C# 对应套件；接控制与原生物理阶段再做本机版本构建及 IPC/死亡/租约回归。已有命令：`python -B -m unittest discover -s tests -p test_movement_loop.py`，其余用相应文件名；`pwsh -File tests/run_csharp_tests.ps1 -MotionOnly` 只测运动，完整命令去掉该参数会涉及 loopback。离线通过不替代游戏验收，不为文档改动启动游戏或反复跑全套。

## 8. 未解决问题与首个小里程碑

### 8.1 仍需证据的事项

1. **能力采样时序**：本机/Mod 更新顺序下，哪些值在指定钩子已最终生效？需要同 tick 分阶段记录和轨迹对比；pre_DoUpdate 的数值不能自动提升为执行资格。
2. **统一几何**：探索投影、旧字符 TerrainReader 和原生碰撞现在有不同信息损失；尤其家具 Frame、单向平台、贴坡接触。如何以最少必要事实共享判据，需要带真实场地的差异测试。
3. **Ordinary 门禁完备性**：当前检查很保守但不是覆盖所有 Mod 的证明；AnyExtraJumpUsable 只是瞬时查询，空装备/Buff 数组也不能证明所有运动 hooks 未生效。未知模型继续拒绝，不能称已安全支持任意角色。
4. **特殊能力寿命与资源**：额外跳跃顺序/可复用、翅膀/火箭、Mod 无限飞行、抗性叠加、呼吸实际耗时都缺完整本地适配与实测；不由物品名推断，也不按 60 tick/s 无条件承诺真实秒数。
5. **目标与终态持久性**：精确 XY、跨越 X、靠近物体有不同成功谓词；当前导航 Cancel 会清掉 Snapshot，查询可能失去旧任务终态。统一结果需有界保存终态及所属身份，取消应返回已发生效果。
6. **观测范围与多人权威性**：viewport 和预测窗口不一致；客户端同步年龄未知、实体身份仅单次观察有效。多人阻挡、地形突变、敌人动态轨迹仍缺充分验收。
7. **规划模型与性能**：现有速度 label 支配规则、局部 partial/progress 选择和软时间预算不是全局完备/最优或硬实时保证。能力扩展后需验证这些剪枝是否仍成立；先量化拒绝率/耗时/预测误差再调整。
8. **LLM 地图收益**：Numen 的侧重点和论文结果不足以证明本项目收益；需要同任务对比小图/矩形的正确率、工具次数、实际 token 和未知误判，不凭直觉删旧数据。

### 8.2 建议优先实现：A0“动作事实与抵达结论分离”

第一个小 PR 只在现有 Python move/stop/MovementLoop 边界增加统一结果适配，保留原字段与当前行为，增加 execution/effects/goal/observation；不先开放新动作或重构导航器。目标是让“completed 但没到”“取消前有位移”“stop 返回旧终态”“ACK 丢失结果未知”在同一结构中可检查，而非由模型自由解释。

最小完成标准：使用现有 mock 场景覆盖上述四种结果；阶段 A 已有代次、预算、未知不重试、动作后观察规则保持；缺失的 end tick/profile 不伪造。随后一次安全平地真实 observe→move→observe，保存新旧字段并核对目标判定。该小步先为地图、基础 Capability Profile 和已有越障原型提供共同反馈语言，再推进 A1 与 B。

本文所有 schema、localMap、Capability Profile、统一 ActionResult 和阶段里程碑均为建议。已有代码/测试与历史实测范围见第 1 节；本轮不新增运行时能力，不把 Numen 或 TerraBlind 的测试视为本项目验证。

本轮文档检查：本地相对链接存在性、Markdown 代码围栏配对及空白检查通过；已有 4 个修改文件的 diff 行数保持研究开始时状态。未运行代码测试、构建或真实游戏验收；前文测试结论为源码覆盖分析与已有记录，不是本轮运行结果。
