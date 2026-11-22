> **历史阶段记录**：本文保留对应阶段的设计、实现过程与当时验证结果。当前导航架构见 [NAVIGATION.md](NAVIGATION.md)。本文中的代码基线、待办和测试状态属于记录时点。

> **历史阶段记录**：本文保留对应阶段的设计、实现过程与当时验证结果。当前导航架构和能力说明见 [NAVIGATION.md](NAVIGATION.md)；本文中的分支、提交、待办和测试状态均属于记录时点。

# Phase 2 Surface Graph 实现与验证

2026-11-05。基线为 `81f3d2d6119123d1b374115186dd748f80fd2500`，实现前 HEAD 与设计文档指定提交一致。范围遵循 [Phase 2 规范](terraria_navigation_phase2_surface_graph.md)。本记录区分离线碰撞替身、隔离原生执行与未验证的图形场景。

## 源码审查与实现映射

实际调用链：`ServerAiNativeActions` → `AutonomousNavigationTask` / `MineTargetTask` → 共享 `NavigationKnowledge` 的内部 `ChildExecutor` → `ServerAiNavigation` → `NativeNavigation` → `NavigationTask` → 局部规划器、`LocalMotionController` → 原生 Player 输入与实际落脚核验。根任务仍严格串行调度移动、原生挖掘、原生放置。

| 职责 | 本次处理 |
| --- | --- |
| 大范围地形读取 | `NativeMotionWorld` 实现独立的 `ISurfaceTerrain` 接口；有界增量读取 Tile。`MotionAt` 为假设站姿创建局部碰撞世界，不改变 Player。原有 45×29 预测窗口未扩大。 |
| 落脚结构及候选图 | 新增 `Motion/SurfaceGraph.cs`。`SurfaceNode` 保存身体左边缘区间、脚部高度、支撑区间/宽度、实体/平台/半砖/斜坡类别、净空、地形签名和物理探测结果。 |
| 图路线与范围 | 同文件 `SurfaceNavigator` 在游戏线程以迭代器扫描、建立按需候选边、进行 A*、扩大覆盖并验证下一段。 |
| 局部运动 | 继续使用 `StateSpacePlanner.Search`、`PhysicsSimulator`、`NativeMotionWorld`；新增可选 `requireInitialJump`，只对已选 jump 连接的首动作生效。原有调用默认行为不变。 |
| 精确短程/改造接续 | 保留 `SafeGroundWalk`：64 像素内仍可直接交付已有完整模拟的地面短程路线，避免清空通道中的精确制动退化成昂贵通用搜索。 |
| 执行、学习、恢复 | `NavigationTask` 保留最终目标，使用真实完整 `PhysicsState` 验证图子目标。继续复用搜索学习、实际动作失败记忆、进度监测、恢复和输入释放。没有第二套 Task Runtime 或开路算法。 |
| 缓存生命周期 | 地形图由根行程的 `NavigationKnowledge` 持有，跨内部移动子任务复用；Actor 对象、world/generation/slot 或运动参数改变时沿用现有失效机制。 |

研究了用户提供的 TerraBot `BotNavigation`、`BotWideNavigation`、`BotRouteFailureMemory`、`BotGroundSupport`、`BotBodyGeometry`，以及辅助跟随/恢复源码。借鉴实际源码中的落脚采样、普通平地稀疏化、按需候选连接、优先队列、跨帧游标和扫描/搜索指标；没有复制其反编译实现、128/240/384 Tile 默认半径或特殊运动连接。

Numen 参考分支为 [`1.21.1`](https://github.com/Dwinovo/minecraft-numen/tree/1.21.1)。读取了 `numen/common/.../nav/Terrain.java`、`numen/pathing/.../drive/{Terrain,Trip,Driver,Continuation}.java`、`plan/{Stance,Maneuver}.java`、`search/{HandOver,TripMemory}.java` 和 [`docs/pathing.md`](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/docs/pathing.md)。采用统一几何入口、站姿与完整运动状态分层、真实身体接续、稳定交接、搜索学习与执行失败分离的原则。其文档明确已移除旧 `hier/`，本次没有将旧分层图当作当前参考，也没有移植 Minecraft 的方块、运动方式、编辑搜索或后台世界访问。

## 关键判定与预算

Surface 不等同于 Tile：先构造身体姿态，调用现有净空及原生支撑判定，再用当前物理参数模拟静止后续。普通形状保留连续有效区间；斜坡保留随 X 改变的高度采样。支撑宽度是几何信息，不是安全门槛；允许身体部分悬空和单格支撑。整数采样之外还在精确目标高度添加经过原生核验的候选，包括分数斜坡高度的逆解。

walk/jump/drop 仅是候选关系。图节点不吞并 `Vx/Vy/Grounded/JumpFramesLeft/ReleaseJump`；物理规划器始终从真实完整状态搜索。较低顶棚会使同一站立区间只有一部分可以起跳，因此用短段原生物理探测记录起跳区间，作为图代价与下一段位置约束；无法确认的起跳增加搜索代价，仍不直接判定物理不可行。局部连接只取得安全前缀时先给其他图分支机会，避免反复走向同一障碍。

图路线只交付现有模拟器已经验证的动作，最后一条执行完整制动尾段，保证身体不会带着移动惯性等待后续图搜索。到达仍由最终目标的真实位置、实际支撑、速度及原有落地确认判断；候选图到达和预测到达都不构成根任务成功。

| 配置/结果 | 当前行为 |
| --- | --- |
| 图半径 | 24 → 48 → 96 → 128 Tile；相对当前真实检查点。高度半径为至少 16、至多 64 Tile。边候选水平跨度至多 144 像素，最终受局部物理窗口和运动参数约束。 |
| 默认单帧图工作 | 512 个工作单元、4ms 检查预算；扫描、节点探测、候选连接和 A* 保留跨帧游标。单个不可抢占的原生碰撞调用可越过检查时限，原有 50ms 保护仍有效。 |
| 节点/缓存 | 至多 8192 节点的构建预算；缓存随角色滚动裁剪，不是永久世界数据库。普通平地在图搜索中每 4 Tile 保留代表，端点、窄支撑、起终点和特殊形状保留密集节点，原始区间缓存保留。 |
| 图诊断 | `NavigationSnapshot.SurfaceGraph`：exit/radius、Tile 读取、节点、候选边、已验证连接、展开量、单帧工作/耗时、变化 Tile、保留节点、扩展次数及当前子目标。物理扫描内部 Tile 访问不计作地形采集的 TileReads。 |
| complete / safe_partial | 分别表示最终局部物理路线或安全制动后的部分路线；完整图候选路线本身不证明整条已执行。 |
| surface_budget / surface_local_budget | 当前工作片或局部物理状态预算；保持行程运行，由根行程期限控制总预算，不解释成不可达。 |
| surface_node_budget | 图节点总量资源耗尽；搜索结束，`TimeBudgetReached=false`。导航以 `navigation_surface_node_budget` 失败并释放输入，不自动重建、不降级挖掘、不解释成不可达。详见下文基于 `3c3fa86` 的审查修复。 |
| no_route_in_coverage | 当前覆盖及当前普通地面运动模型内未取得可交付路线；不是全世界不可达证明。 |
| unknown_terrain / unsupported_model | 与无路线分开。耗尽覆盖后仍缺少地形时，生产适配返回 `unknown` / `native_navigation_terrain_unknown` 并释放输入，不自动降级开路、重连或重放。 |

## 地形更新与复用

每次新的真实检查点规划增量重读覆盖区域的签名；变更只移除支撑/净空依赖相交的节点，未变化节点保持对象/ID，也不重复原生探测。挖掘、放置后沿用根任务原有停稳、观察、再规划流程；共享图缓存会识别实际变化。

候选连接每次图搜索重新产生，不永久复用未经验证的弧。最近一条已验证连接保存完整起始状态、运动参数、实际模拟落点，以及覆盖所有预测帧和制动轨迹的地形签名；中途障碍变化使其变为 stale。执行前仍由现有控制器和路线接续逻辑重验，不能因为两端支撑没变就重播旧输入。

图搜索中的当前起点连接排除及不确定代价只是一次搜索的状态，不写入实际失败记忆。物理搜索继续使用 `NavigationSearchMemory`；实际执行偏差继续交给 `NavigationFailureMemory`，保留精确状态、动作、地形和 TTL 契约。

## 定向验证（2026-11-05）

没有运行仓库全量测试。以下离线测试使用碰撞替身，不能替代原生证据：

- `tests/run_csharp_tests.ps1 -NavigationOnly`：通过 Surface Graph、mobility、trip、recovery、planner correctness、perception、落地确认、motion 和 Player hook 定向回归。
- `-SurfaceGraphOnly`：通过几何、分数斜坡目标、单格/双格/悬空、顶棚、支撑及形状变更、弧中障碍失效、原区域保留、移动初速回放、实心/平台间跳跃和下降、绕行、连续长程、扩大范围后发现台阶入口、单工作单元接续、预算/未知/模型拒绝、动作失败记忆和取消/身份失效。检查数包含可变的分片断言，不作为固定性能指标。
- `-MiningSupportOnly`：30 项通过。
- `tests/run_server_ai_ipc_tests.ps1 -PackagePath ...`：IPC 45、读取协议 68、Task Runtime 81、包/IL 91 项，以及脚本内 atomic/native adapter/chat 定向检查通过。
- Python `test_server_ai_landing.py`：5 项通过，涵盖真实到达、取消、unknown 不重放及能力契约。
- `scripts/build-companion.ps1 -OutputPath .local/phase2-surface-build`：0 errors / 0 warnings；构建包未部署到用户现用 profile。

隔离原生执行使用 tModLoader `2026.09.3.1`。最终包 SHA256：`8E1DE940AE3F467A760668E4CD04954BBB0B8BBC899C0E23E5F3D82F02CCD78C`。

| 场景与证据（本地 `.local/`，不提交临时产物） | 结果 |
| --- | --- |
| `companion-g81-native-846a658779cd435987d529ac6a38c9fb/result.json`；`verify_server_ai_native.ps1 -Actions -Tasks -Placement` | 34 个 mine_target + 15 个自主导航场景通过，原生独立放置通过。导航长程位移约 952px，低顶棚绕行无工具改造完成；多轮改造分别 27 次挖掘、11 次平台放置。每趟只有一次外部任务调用，子动作串行且终态释放输入。 |
| `companion-g81-native-d61c35e6570243f79f6a80ca263ca15d/result.json`；额外 `-LandingRegions` | 双向 20 场地形矩阵符合预期（18 成功、2 超高平台安全失败），并通过同一批 mine_target / 导航 / 放置。此轮在最终 unknown 分支及恢复夹具调整前执行；后续最终包任务与恢复单独复测通过。 |
| `navigation-recovery-native-e48e398926734e5faa39844c8de79ce5/recovery-results.json` | offset / air：完成，均有 1 次实际恢复及重规划；cancel_air：取消；输入释放、真实停稳恢复、Actor 身份及失败记录核验通过。 |
| `natural-native-abc1a5a3139b4d2eb976494e067a6bb8/result.json`；Offsets 0/32/-32 | 自然地形 4 次真实目标到达，2 次因未观察到不同高度候选而跳过；未把跳过当成功。 |

两项测试契约随实证调整：原恢复夹具固定在第 8 个 update 扰动，新增图扫描使它落在尚未执行动作的等待期，现只在实际 walk/jump 至少 4 帧后注入，并核验注入凭据；未放宽生产恢复条件。原 `opening_unsafe` 只允许失败/超时，但图导航发现了空洞侧边有原生有效支撑的路线，连续三次 16px 下降后挖除目标；测试现在也接受该成功，额外要求侧向位置、真实停稳移动、逐级下降和最终 Actor 挖掘证据。危险地形、硬度、丢失效果观察等原负例仍保持对应终态。

## 阶段判断与限制

在本规范限定的普通地面、平台、半砖、斜坡模型及上述定向证据范围内，Phase 2 已完成。图用于组织空间，物理规划验证具体动作，实际 Player 状态确认结果。Phase 1 已验证的原子挖掘、放置、Task Runtime、身份、取消和 unknown 契约继续复用。

图 A* 的代价与采样用于发现路线，不保证全局完备、最短路线或最少跳跃；图覆盖、节点及行程期限仍然有限。候选图中的连接可由多条实际局部运动实现；已验证信息绑定精确状态，不能推广到该 Surface 上的任意速度。最大范围/高密度图预算压力主要由离线场景验证，未完成大规模复杂自然世界的性能基准；4ms 是协作式检查预算而非硬实时上限。

没有新增平台穿透输入、液体环境、特殊装备/绳索/钩爪/飞行/坐骑/Mod 运动能力，没有 Phase 3 多步改造成本搜索，没有后台原生碰撞访问或世界级持久图。未进行图形 Host & Play 或真人联机端到端验收；四阶段完整系统验收仍按规范留到 Phase 1–4 完成后。

## 基于 `3c3fa86` 的 Phase 2 审查修复

本轮仅修正 StepUp/StepDown 候选分类与图节点总量耗尽的控制流，不修改原生物理、运动控制器或 Task Runtime。

**台阶分类根因与变化。** 原 `SurfaceGraph.Candidate` 对每个中间 X 使用起点的固定脚部高度 `a.Rest(px)` 检查支撑和净空，台阶面因与该姿态相交而被改成 Jump。`SurfaceNavigator` 随后为 Jump 设置首动作必须跳跃，导致图候选比原生 StepUp 更严格。现在对小高度变化的 Walk 候选检查周边可能支撑高度，半砖、整格台阶以及同高度端点之间的短台阶不再要求中间全程平地；实际无支撑断面仍保留 Jump 分类。这只是候选层的高度包络，不是第二套 StepUp 算法；穿过过渡区的身体净空、惯性、制动及实际可行性仍由现有 `StateSpacePlanner` → `PhysicsSimulator` → `NativeMotionWorld` 的原生 StepUp/StepDown 验证。

**节点预算根因与策略。** 原构建在整 Tile 完成后才检查 `count>8192`，随后图搜索返回 `Finished + TimeBudgetReached`；`NavigationTask` 将其作为时间片耗尽丢弃并重建。现在在新增节点之前执行 8192 硬上限，标记最后的未完整 Tile，避免以后显式刷新误用部分缓存；单帧耗尽继续保留迭代器，节点总量耗尽则立即返回独立的终态资源结果，不再搜索不完整图或扩大范围。`NavigationTask` 在时间片分支之前处理该结果，释放输入并终止本次移动任务；`AiActionReasons` 保留明确原因，且不将它分类为可自动挖掘恢复的 no_route/no_progress。现有诊断中的节点数、范围、读取量、搜索退出原因仍保留。

新增定向证据：

- `SurfaceGraphTests.CheckStepCandidates`：半砖、整格高度变化、低顶棚、双向候选、同高度端点中间有台阶、真实缺口仍为 Jump。
- `SurfaceGraphTests.CheckNodeBudget`：真实扫描和探测重复斜坡，在初始半径内产生超过 8192 个有效采样需求；缓存保持恰好 8192 个节点，返回 `surface_node_budget`，`Finished=true / TimeBudgetReached=false`，不展开 A* 或再次扩大范围。以后显式刷新仍识别未完整覆盖。导航约 34–35 次更新后以资源原因终止，后续 100 次更新新增读取和探测均为 0，搜索计数不变。更新次数取决于时间片调度，不作为固定性能指标。
- `tests/run_csharp_tests.ps1 -NavigationOnly`：最终代码的 Surface Graph、PlannerCorrectness、Phase 1 trip/recovery/landing/motion/mobility 与 Player hook 定向回归通过，包含未完整 Tile 缓存覆盖断言；另在实现中使用 `-SurfaceGraphOnly` 验证局部变化。未运行仓库全量测试。
- `tests/run_server_ai_ipc_tests.ps1 -PackagePath .local/phase2-review-fixes-build/Mods/CompanionBridge.tmod`：IPC 45、atomic 48、读取 68、Task Runtime 81、包/IL 91 项及脚本内 native adapter/chat 检查通过；新增两项资源原因保留和不自动恢复断言通过。
- 原生隔离场景 `navigation_step_half_roof / navigation_step_up_roof / navigation_step_down_roof`：原生碰撞世界中的候选均为 Walk；零跳跃输入的完整运动预测安全到达，脚部高度分别变化 -8/-16/+16px；同一低顶棚将跳跃上升分别限制在约 13.99/5.99/5.99px。三个场景随后通过真实 Player 输入到达核验。逐项凭据保存为隔离 profile 下的 `navigation_step_*-proof.json`。夹具内状态注入、碰撞预测均受现有隔离测试开关保护，不是正式执行路径。
- `companion-g81-native-d3d8f65f169244d7824eebb9a8762efd/result.json`：上述三项及原 15 项自主导航全部通过，包括长距离持续导航、地形改造后接续、27 次挖掘和 11 次平台放置。
- 最终相关原生回归 `companion-g81-native-a008aa02444f4c4d962e28e2e099dcbd/result.json`（`verify_server_ai_native.ps1 -Actions -Tasks -Placement`）：34 个 `mine_target` 场景、18 个自主导航场景及独立放置检查均符合预期；包括危险拒绝、超时、unknown、取消和输入释放，以及新增三个台阶场景的持久物理凭据。
- 构建 `.local/phase2-review-fixes-build/Mods/CompanionBridge.tmod`：0 errors / 0 warnings。SHA256：`0CEFDEA0E840E6BF57B3D47801211CCCC18B92F904D3035EDA972D98F7613266`。未部署现用 profile。

剩余限制：Walk 分类仍是有界的乐观结构候选，不能用它证明过渡可走；原生物理验证有最终决定权。8192 仍是总量硬预算，本轮没有引入更高容量、自适应降密或截断图降级；高密度地形可能得到明确资源失败，调用方可以在新的明确请求中重新观察，但当前任务不会自动重启。超 8192 节点压力使用离线碰撞替身验证；本轮未进行高密度自然世界、图形 Host & Play 或特殊运动能力验收。

## 基于 `3fc959d` 的滚动缓存淘汰顺序修复

`Refresh` 原来在末尾淘汰旧缓存；移动搜索中心后，过期节点先计入 `RetainedNodes`，可能在末尾清理前触发节点上限并提前退出。现将节点和地形数据淘汰移到刷新开头，在新窗口读取、`RetainedNodes` 统计与节点预算计算之前完成；每次删除仍作为可跨帧的工作单元。原有保留范围（节点横向 ±132/纵向 ±72 Tile，地形横向 ±136/纵向 ±80 Tile）、8192 硬上限和资源失败语义不变。

新增 `SurfaceGraphTests.CheckRollingCache` 使用真实扫描和碰撞替身构建完整的 7992 节点旧区域；新区域独立构建需要 441 个节点，两者直接相加为 8433，足以复现旧顺序误触发预算的问题。测试先在旧实现复现失败，再验证修复：

| 新中心（原中心 100,100） | 计入预算的旧节点 | 新节点 | 最终节点数 |
| --- | ---: | ---: | ---: |
| 400,100 | 0 | 441 | 441 |
| 100,300 | 0 | 441 | 441 |
| 240,100 | 2376 | 441 | 2817 |
| 100,180 | 2664 | 441 | 3105 |

四种水平/垂直、完全/部分滚动均正常完成，没有 `Truncated`；过期节点和地形数据已移除，仍在保留范围内的未变化节点保持对象身份。既有密集斜坡测试仍产生超过 8192 个有效采样需求，按上限停止并返回 `surface_node_budget`，终止后 100 次更新没有重建；既有时间片续接、部分路线及持续导航测试继续通过。

本轮验证：`tests/run_csharp_tests.ps1 -SurfaceGraphOnly`、`-NavigationOnly` 均通过（后者包含现有物理规划、Phase 1 trip/recovery/landing/mobility/motion 与 Player hook 定向回归）；`git diff --check` 通过。未运行仓库全量测试。`scripts/build-companion.ps1 -OutputPath .local/phase2-cache-eviction-build` 构建为 0 errors / 0 warnings，包 SHA256 为 `2C217D03AD6C17B0242F0F7E971CBB48261C629F0040AFF4FD368C656019A97A`。

范围与限制：本轮只调整缓存淘汰顺序，未改变密集有效缓存的硬预算策略、物理模型或执行链；缓存保留范围内的节点仍按设计占用预算。滚动压力证据来自离线碰撞替身与实际生产缓存代码，本轮未重新运行原生游戏或图形场景，构建包未部署到现用 profile。
