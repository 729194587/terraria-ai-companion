# R2-E　专服自主 Boss 战斗

设计基线为 `ea54e15`。本文记录源码审计、架构决定与待执行的验收。配装和自卫的原生实测见[战斗配装设计](COMBAT_WEAPON_LOADOUT_DESIGN.md)。[原生执行路线图](NATIVE_EXECUTION_REFACTOR.md)的 R2-d 是较早的武器支持记录。

## 决定：保留普通战斗，新增一个 Encounter 根任务

每个绑定 Actor 仍只有一个身体所有者。普通主动攻击和自卫继续使用 CombatController。显式挑战使用同级 BossEncounterTask，在一个 TaskSlot、一个 actionId 下运行至结束。它持有成员集合、当前瞄准对象、战术状态和整场证据。成员可复用 CombatTarget 的实例引用格式。专服每次游戏更新推进任务，提交后的决策与输入不需要模型调用。

选择这一边界有两个源码依据。第一，现有自卫已经可以在同一个控制器里改打下一个敌人，换目标无需重新获取身体。第二，现有执行器也不能原封不动地装进一个外层循环：BindTarget 清理命中和接近状态并递增 epoch，Shot.Pending 要求当前任务和 epoch 均匹配，普通主动攻击在该目标死亡时结束。如果依次启动多个 CombatController，部件切换会释放输入、关闭旧弹幕的命中窗口，并可能把击破一只手当成整场完成。若把全部 Encounter 语义塞进原控制器，则原有单目标成功条件、时限和自卫抢占都要随之改变。

**共享的位置在任务之下。** 从 NativeCombatWorld 分离绑定校验、选武器、原生攻击周期、移动意图执行和证据采样；目标资格、允许命中的实例、站位决策、胜负和汇总由各任务提供。保留普通任务现有契约与 Python 核验。Boss 任务始终只驱动一个攻击执行器；成员数量不产生额外的导航任务、ItemCheck 或身体租约。

## 当前调用链及需要改变的接缝

| 环节 | 当前仓库行为 | R2-E 接入点 |
|---|---|---|
| 战略请求 | Lua 的 inspect_inventory、configure/query_combat_loadout、scan_entities 和 submit_task(combat_attack, entityRef) 由 server_ai_contract 提示；atomic_operations 校验，server_ai 按 hello 能力分发。query_combat 返回当前或最近战斗。 | 增加独立能力、可发现的候选、异步提交、按 ID 查询与取消。一次 RPC 返回受理凭据；Lua 程序预算和 IPC 请求超时不能成为战斗时长。 |
| 受理与身体交接 | AiReadOnlyProtocol / AiAtomicActions 处理绑定与动作。CombatConflict 预检、停止旧动作、检查可交接性；释放未知会封锁身体。ServerAiNativeActions 创建 CombatController 和 TaskSlot。 | Boss 请求先检查种子实例、配装与身体状态；显式挑战可先要求身体空闲。拒绝时不打断原动作。运行期间自卫、补召、火把和普通导航不能取得第二份输入。 |
| 时钟与撤销 | ServerAiSystem.PostUpdatePlayers 在游戏线程 Pump；TaskSlot 按 GameUpdate 去重并核对 world/generation/slot/Actor。CombatController.Budget 为 1800 updates，AiAtomicActions 主动及自卫入口均设 35000 ms，任一先到即可停止。TaskSlot 的进展续期仅用于指定导航任务。 | 新操作独立配置总更新数、墙钟硬上限与分阶段无进展窗口。无查询也继续；连接取消、死亡、换代、世界卸载仍按既有规则收尾。不得自动重连重放或把长战斗隐藏在多次短任务中。 |
| 识别 | NativeCombatWorld.Enemy 要求有接触伤害、可追击，排除 boss、ShouldBeCountedAsBoss 和 realLife 非负实例。Resolve 在 AcquireRange（640 像素）内按 EntityRef 找一个实例。SameSpawn 核对对象、类型、GlobalNPC 实例与 token。 | 分开判断“属于战斗”“本帧可打”“可能伤人”。零接触伤害的弱点、无敌核心、远处根和敌对弹幕各有不同用途。普通 Enemy 保持原适用范围。 |
| 循环 | 主动攻击按单实例终结；reflex 通过 ContinueDefense、Retarget 在原生挥动排空后切换；CombatEncounter 只是自卫目标历史。Step 将近战接近、远程射位与攻击阶段耦合。任务外 TickMaintenance 在身体忙碌时退让。 | 部件消失触发成员重评；攻击执行器接受本帧站位或闪避意图。战前安全补召沿用现有路径；场内如允许补召，须由根任务在可完成施法的安全窗口发起。 |
| 原生执行与核验 | EngineHooks、NativeItemInput、ItemCheckWrapped 与原生射击/近战钩子执行。StrikeWithEvidence 也用 Enemy 和 SameSpawn 限制实际打击。CombatProjectile.Flight 持有 NativeCombatWorld.Shot；直接弹的 CanHitNPC 会拒绝不匹配目标。可信召唤本体可按当前任务重新分配，支持的子弹在出生时冻结父链和分配。CombatStrikeEvidence 要求同实例、正伤害及生命下降。Python verified_server_combat 再查任务和终态。 | 单改 Resolve 或结果 JSON 都不够。须同步改近战授权、直接弹实际碰撞许可、召唤分配、受击采样、终局分类、结果 Schema 与独立核验。保留现有武器支持范围。 |

源码入口：[动作受理](../CompanionBridge/ServerIpc/AiAtomicActions.cs)、[根任务调度](../CompanionBridge/ServerIpc/TaskRuntime.cs)、[专服更新](../CompanionBridge/ServerIpc/ServerAiSystem.cs)、[普通战斗任务](../CompanionBridge/ServerIpc/CombatController.cs)、[原生执行](../CompanionBridge/ServerIpc/NativeCombatWorld.cs)、[弹幕钩子](../CompanionBridge/ServerIpc/CombatBallistics.cs)、[Python 核验](../python/atomic_operations.py)。

## TerraBot 对照

研究材料为仓库上级目录的 terrabot.zip，SHA-256 为 `d217dc0ea14e361aa8b4c7d6902f92c93114e42359df9703cfae2bf7f55169fe`。对照解压树的 TerraPlayerBot 分支；该分支及 TerraPlayerBotClientCombat 分支中筛出的 Boss、Prediction、CombatAgent、CombatTargets 和 CombatMovement 文件分别有 16 和 14 个，均与归档对应条目逐字节一致。以下是反编译代码行为。

| 源码 | 行为与借鉴边界 |
|---|---|
| BotCombatAgent.Tick / BotCombatMovement | FindTarget 后经 Targeting.Select 确定当前目标；维护召唤及 UseWeapon 使用同一轮状态。移动路径调用 bossEvasion.Apply。值得保留每轮共享目标、射程和站位，以及闪避对移动的优先权。其 Controls、队友追踪、场地边界和直接写 Player.control 字段须换成本项目的绑定、运动 profile 与输入所有权。 |
| BotCombatTargets.EncounterRoot / IsBoss | 首先接受指向活跃 boss 的 realLife；骷髅王手和机械骷髅王手臂读 ai[1]，月总头手读 ai[3]，石巨人使用特定索引或唯一根。Boss 可返回自身。IsBoss 另显式识别 13–15 和 134–136。EncounterRoot 是一次根查询，不维护持久战斗身份。尤其世界吞噬者，识别所有段为 Boss 不足以证明所有段属于同一实例，也不能证明断裂后哪条链延续原战斗。 |
| BotBossTargeting | 毁灭者选可追击且射界较好的段；危险探针可短暂抢占（60 更新窗口、180 更新冷却）。双子锁定一只，优先已变身者，否则倾向低血者；扫描能发现两只眼，并未提供同场配对凭据。专家骷髅王锁同一头的可攻击手。机械骷髅王有射界时优先头；月总核心可打时优先核心，否则在同根暴露部件上评分，偏好较高剩余血量比例。借鉴有理由的目标承诺和优先级，归组须先于评分。 |
| BotBossThreatGeometry / BotBossEvasion | Observe 建立最长 60 更新的 NPC、弹幕轨迹；SetCandidate 把候选玩家路径反馈给追踪型预测。Score 模拟水平移动、起跳、延迟转向和下落，经 TileCollision 和扫掠/光束几何计算未来危险，再计入射程、边界与变向成本，Apply 输出一个移动选择。可借鉴短期滚动预测；60 帧和枚举规模须在本项目专服实测定标。 |
| 专用预测 | BotTwinsPrediction 预测眼球轨迹和可能射击；BotEmpressPrediction 预测本体；月总分为 MoonLord（观察、候选路径与未来射线）、MoonRay（发射源偏移）、MoonFreeEye（状态及本体路径）、MoonEye 和 MoonSphere（弹幕路径）。几何模块还直接实现骷髅旋转、Prime、Duke 的预测。它们读取特定类型、ai 状态、难度及装备条件；通用层只需要预测形状、有效时间和可信度，具体状态机由适配器解释。 |

## 身份、证据与终局

* EncounterId 在专服受理时产生，作用域含世界会话、Actor 代次与 actionId。它可有多个根；当前攻击成员、根槽位、Boss 类型都不能充当 EncounterId。首版由新鲜 seedEntityRef 发起对已生成 Boss 的挑战。召唤 Boss 道具是单独的原生操作；若以后合并“召唤并追踪出生”，须证明新实例与那次施放的因果关系。
* 成员索引以世界、slot、spawn token、类型和本地对象校验为基础；分开记录根、部件、附属敌人与共享生命的承载者。每条关系记录来源、首次及最近确认帧、失效条件。realLife 边要校验两端实例；boss 标记可发现根，不能证明两根同场。变身、分裂、同槽换种、失活后重生先关闭旧引用，只在能证明连续性时连接新成员。关系冲突、多个候选父级或环路时停止扩展。未确认的附近敌人仍可进入威胁图。
* 蠕虫优先验证原生关系，记录分裂前后链的出生、连接变化和死亡事件；只有经专用规则验证的链延续才能并入。双子需要可复核的共同生成上下文或唯一配对规则；两队同时存在且无法配对时返回 ambiguous。临近、同名、锁定同一玩家、某个全局 downed 标志均不能单独证明归属。月总核心无敌期间继续观察其成员，部件暂不可打进入等待窗口。
* 每次命中独立记录 Actor、Encounter、原始瞄准成员和选择 epoch、实际受击成员、武器/弹种、发射及命中更新、原生伤害、生命承载者及前后值。直接弹的权限在发射时冻结为已确认的允许实例集合（可共享不可变的成员版本）；碰撞时再验证当前绑定、战斗仍活动、实例仍属此场。旧箭可命中仍有效的旧部件；新加入部件、重用槽位、另一场的成员不得继承权限。近战每挥冻结同类许可，切换需尊重原生动画。召唤本体沿用 Actor 所有权，接触命中按该更新分配，子弹记录出生时的可信父链。取消后原生召唤物按原生命周期活动，关闭 Encounter 归因。
* 对共享血量须同时采样受击段和已验证的生命承载者，在原生调用边界成对读取；以原生打击事件一次入账，段和头的镜像变化只记一次。穿透弹可有多次合法打击，不能用“弹幕 ID + tick”吞掉所有后续打击。无法确认生命路由、同帧存在歧义或来源链断裂时保留观测并标记未核验。不得以玩家合计掉血或 Boss 消失反推 AI 伤害。
* 生命周期为受理、绑定、交战、等待暴露或转段、终局核验、释放。撤退走独立的限时导航状态，先撤攻击权限；只有安全落点和脱离条件实测成立才报告已撤退，路线不可达或超时分别报告。取消直接释放。把“Encounter 已结束”“Boss 已败”“AI 有贡献”“AI 完成致死击”分别记录：例如队友击杀可确认战斗胜利，AI 的致死贡献仍为空。终局要求与成员身份关联的原生死亡事件及该 Boss 完成规则；失踪、白天离场、玩家死亡、租约超时、部件死亡、世界切换各有独立原因。证据不足返回 unknown，终态后不再接收旧事件。

## 每更新的决策与长期运行

1. 游戏线程先处理撤销、绑定和身份变化；在固定更新点采样本地成员、敌对 NPC、弹幕及运动状态。记录 sampleUpdate。动画/碰撞钩子按实际发生帧采集，下一次决策消费已完成事件。不要为每个模拟候选重复扫描全世界。
2. 通用选择器先排除不可打、遮挡且无可行射位等目标，再按攻击机会、武器有效距离、威胁和切换代价评分。复用主副手资源、弹药、法力、切换滞回与原生周期。成员失效立即重评；分数接近保持当前目标。无暴露成员时仍持续闪避与观察。
3. 威胁图包含与 Encounter 无关但可伤及 Actor 的实体。通用近未来模型使用实测速度、碰撞盒与连续扫掠；适配器可补充蓄力冲刺、旋转光束、追踪或传送后的危险区。预测附时间窗、误差和来源；未识别的 Mod 弹幕按观察范围保守处理，无法可靠预测时公开降级。
4. 在 MotionSupport 允许的玩家 profile 内用现有 PhysicsSimulator、地形碰撞与液体风险模拟候选输入。先淘汰撞墙、危险落点和不支持的动作，再按预计伤害、可脱离性、攻击窗口和转向成本选择；只执行首步，下次用真实位置重算。接近、远程站位和紧急躲避共同产出一个移动意图，交给现有执行边界与物品输入合并，每 Actor 每更新最多一次受控 ItemCheck。飞行、冲刺等须逐种验证能力后启用。
5. 预算分三层：每更新限制扫描、候选数及预测步数；短期窗口监测有效命中、确认的阶段转换或恢复射位；整场使用独立的更新硬期限和墙钟期限。等待合法无敌阶段可延长短窗口，反复切目标不可续命。过载先缩短预测、保留最近及即将碰撞威胁并暴露截断计数。按阶段累计来源计数、去重伤害和最后一次证据，仅保留有界事件环与终局摘要。隔离专服先测规划时长分位数、最大耗时、漏帧、分配量与截断率，再确定预算。

## 战略 API 与可选 Mod 适配

建议握手公布独立版本能力；发现接口返回种子引用、根/关系来源、已知策略、歧义以及采样帧。沿用 submit_task 提交 boss_encounter，回执立即给出 taskId/encounterId。状态查询返回阶段、成员摘要、当前武器和目标、最近威胁与动作理由、贡献及证据等级、期限和退出原因。取消沿用已有所有权检查；撤退请求须带匹配的战斗 ID 和已验证目的地或已知安全区域。Python 白名单、Lua help、参数和结果 Schema、终态核验与 capability 同步发布。不轮询也能完成，实际 IPC 断开仍撤销。跨断线继续战斗涉及所有权变更，须另行设计。

内置通用规则只处理已验证的原生字段。可选兼容 Mod 在加载完成后按名称查找 Companion，通过版本化 Mod.Call 注册描述符或只读观察回调，传基础类型、内容全名、版本和有界快照，无需引用 Companion 程序集。注册项只提供成员边、阶段、目标偏好、威胁几何或终局判据；核心复核实例、原生伤害与许可，独占所有输入。按内容名在当前加载集解析类型，限定适配器版本、优先级和确定性冲突规则；冲突时降级到可证明的能力。卸载清注册和缓存；异常或超预算时隔离该扩展并记录原因。进程内回调不能硬中止或沙箱化，优先使用声明式描述符，对可信回调限频并在返回后计时。Calamity、Fargo's Souls 的识别、机制、运动能力与武器支持分别声明，不能因 Boss 可识别便宣称整包兼容。

## 分阶段实施与验收门槛

| 阶段 | 交付与定向验收 |
|---|---|
| E0　只读原生探针 | 在隔离专服记录出生、变身、死亡、realLife 与两端 token、段/根生命、原生伤害回调顺序、敌对弹幕样本。以单体、毁灭者、断裂的世界吞噬者、双子和月总绘制实际关系。额外生成同型并发实例测试误归组。无法证实的边和胜负规则列为未知。 |
| E1　单根闭环 | 新任务获得身体，使用基线武器对单体 Boss 原生攻击；使用真实装备和已验证地形移动。提交后保持连接且零后续请求，战斗跨越 1800 更新与 35 秒；另测死亡、主动取消、断连、换代及墙钟硬超时，核对输入已释放、无重放。回归普通主动攻击、挖矿遇敌和安全补召。 |
| E2　多成员与证据 | 毁灭者射段查根生命；发箭后切剑、换部件仍正确记账；穿透、召唤父子弹、槽位重用、他人补刀逐案比对原生事件。世界吞噬者断裂仍存活、双子一死、骷髅手被击破、月总无敌期均不提前胜利；同型第二场零串账。终局规则未验证的 Boss 限制为观察或明确拒绝。 |
| E3　移动与预测 | 同地形、装备、初态和攻击序列对比站位基线与启用躲避。至少覆盖 NPC 接触、冲刺、实际飞行弹、光束、平台边缘及无安全路；核对预测窗口、真实受伤、命中机会、输入冲突和最差帧耗时。专用阶段适配分别验收，不能以某次幸存代替轨迹误差和原生命中记录。 |
| E4　战略接口与兼容发布 | 使用运行时 help 完成配装、发现、提交、查询、取消及撤退；旧握手无新入口。用一个不引用核心的独立样例 Mod 验证标准关系自动识别，以及自定义关系/预测注册、未知版本和冲突降级。实际加载的 Calamity、Fargo 版本分别列出实测 Boss 与限制。最后对声明支持的 Boss 跑无人轮询的整场专服验收。 |

每阶段只运行本次路径所需的离线定向用例和隔离专服场景。已有 loadout-trip 等验收入口见[配装设计](COMBAT_WEAPON_LOADOUT_DESIGN.md)和[原生战斗场景](../tests/native_combat.py)。

## E0 隔离专服实测（2026-11-18）

Terraria 1.4.4.9，tModLoader 2026.9.3.1，包 SHA-256 为 `6C330357AD713E70AA683111C274D93300B03495A2C0482381BE36CBD6FBD540`。五个场景分别启动独立专服，使用复制世界、无客户端、无模型请求。只读观察器记录原生 OnSpawn（含来源和 NPC 父实例）、CheckDead、OnKill、成员快照及世界进度标志。场景驱动单独生成种子、设置夜间与测试角色免伤，在指定时间调用原生 StrikeNPC，并在调用两侧采样。场景沿用真实 NPC AI、变身、掉血和死亡逻辑。测试伤害来自 fixture，不能用作 AI 武器命中或致死归因的证据。E1 尚未实施。

| 场景 | 同一场运行中的证据 | 对后续设计的约束 |
|---|---|---|
| 世界吞噬者中段断裂 | 更新 12 的单链为 1 头、65 身、1 尾，全部 realLife=-1。击破槽 35 后仅该身段触发 OnKill。更新 18，沿 ai[0] 向后走、用 ai[1] 反查前驱，得到两条互斥的 33 段链，端点分别为 2→34、36→68。34 原身体变尾，36 原身体变头，两者原槽保留、token 更换；更新 32 两条链仍在，downedBoss2 仍为假。 | 初次生成可采样 EntitySource_Parent，分裂后须关联断裂前拓扑、受击事件和变身后的双向链。旧头引用覆盖不了新链，单段 OnKill 也不结束战斗。此次没有验证漏采断裂、反复分裂或两条不同来源的吞噬者交叉时的归组。 |
| 两条毁灭者同时存在 | 更新 12，根槽 2 和 3 的 realLife 各自指向自身；各有 80 身、1 尾指向对应根。击中槽 4 的身体，原生返回 123；在同一次调用内，受击段及其根槽 2 均从 80000 降到 79877。另一根采样仍为 80000。 | 此配置可按经实例校验的 realLife 区分两条毁灭者，根指向自身是终点。段和根的两个差值只入账一次。尚未证明穿透、多方同帧打击、根死亡或其他共享生命 Boss 的回调顺序。 |
| 一对双子 | 更新 12，Retinazer（125）触发 OnKill；更新 18，仅 Spazmatism（126）仍在，downedMechBoss2 为假。更新 24，126 触发 OnKill；更新 32，双子均已消失且标志为真。 | 已确认成员集合内应独立追踪两次死亡。世界进度标志只能旁证首次击败，不能识别哪一对、谁最后命中或重复击败。未验证一眼逃逸的终局。 |
| 两对双子同时存在 | 同一帧交错生成 2 只 125 和 2 只 126。四个实例 token 各异、realLife 均为 -1；四个种子来源均为 EntitySource_Misc，无 NPC 父引用。位置来自场景布置。 | 能区分四个成员；这些原生观测不能证明哪两只属于一对。此类请求应返回配对不明。自然召唤物品能否提供更强的共同出处仍需单独探测。 |
| 月亮领主 | 更新 12 只见核心；更新 61 双手和头出生，三个 OnSpawn 的父 token 均指向同一核心。更新 64 击中暴露部件，各产生自由眼，部件仍活跃并转入免伤；更新 72 命中可打核心后，核心仍活跃、生命 50000、转入免伤。更新 672 才出现同一核心的 CheckDead、OnKill，随后核心消失且 downedMoonlord 为真。整段记录只有一次 OnKill。 | “已进入死亡动画”和“已完成”必须分开。OnKill 回调内 active 仍可为真，需在更新结束后复核。正的 StrikeNPC 返回值本身也不能证明部件有可累计的生命损失。600 更新是本次观测间隔；终局应等待实例事件，不能用固定倒计时替代。 |

双对双子场景持续到更新 420。更新 61 和 82 分别观察到类型 96、83 的敌对弹幕，两者的 slot、owner、identity 均为 0、255、0，形状、速度和伤害不同。探针每类采首个样本，最多八类。这些数据可以作为本地威胁观察，不能用来认定是哪一只眼发射，也不足以验证躲避预测。

**复现与证据定位。** 在仓库根目录运行以下 PowerShell。每次均创建新 profile。对应 profile 的 `boss-e0.json` 含原始事件及实例 token，`manifest.json` 记录包指纹和进程退出码。脚本同时独立核验成员拓扑、扣血和终局。[观察器与场景驱动](../CompanionBridge/ServerIpc/BossProbe.cs) · [验收断言](../tests/native_boss_probe.py)

```powershell
pwsh -NoProfile -File scripts/build-companion.ps1 -OutputPath .local/boss-e0-build
foreach ($scenario in 'eater','destroyers','twins','two-twins','moon') {
    pwsh -NoProfile -File tests/verify_server_ai_direct_input_native.ps1 -BossProbe $scenario -PackagePath .local/boss-e0-build/Mods/CompanionBridge.tmod
    if ($LASTEXITCODE) { throw "Boss probe failed in $scenario" }
}
```

本次通过验收的目录均以 `.local/direct-input-native-` 开头，以下列出后缀以便核对原始记录。

| eater | destroyers | twins | two-twins | moon |
|---|---|---|---|---|
| d793a1a9215c4f73896a47cb6e43de4b | a12b36ef68714510a5b8d525453e8316 | 83df0f4f8b7c42629fead840e604728b | ed70551bfdbf4f12887b946fd0766869 | b955aa92b2d347c4952e9f28e7bbf060 |

## E1 单根 Encounter 实装与隔离专服验证（2026-11-18）

握手能力为 `server_ai_boss_encounter_v1`。E1 首个支持的根为克苏鲁之眼（原版类型 4），以 Actor 代次、槽位和出生 token 锁定同一个实例。在配置主武器或副武器后，从 `scan_entities` 的 `data.items` 选择目标行，通过 Lua 提交：

```lua
local roots = api('scan_entities', {category='npc', typeIds={4}, radius=640}).data.items
if #roots ~= 1 then return {candidateCount=#roots} end
return api('submit_task', {operation='boss_encounter', arguments={entityRef=roots[1].details.entityRef}})
```

受理后的 taskId 同时标识 Encounter。用 query_task、await_task、cancel_task 查看或结束任务。游戏端独占身体，复用 NativeCombatWorld 的配装、选栏、导航、ItemCheck 和命中核验。可选 durationSeconds 为 1–600 秒，默认 300 秒。另有 36000 更新总期限和 1800 更新未获原生命中的停滞期限。专服观察到同一根的 OnKill、失活、生命耗尽并确认输入释放后才报告 boss_root_defeated；lastHit 单独记录已核验的 Actor 贡献。槽位替换或失踪返回 unknown。换代后沿用旧绑定失效语义，不用新角色签发旧任务的释放回执。

**原生实测。** tModLoader 2026.9.3.1，包 SHA-256 为 `EB0F3134DB52571E976D637A8C48C428F292171EB0845290C7E4FA390B5C96F8`。每例独立复制世界，无客户端、无模型请求。fixture 提供剑、弓、箭、夜间和 Actor 免伤；克苏鲁之眼使用原生 AI。长战斗使用原始 2800 生命，击败专项把初始生命设为 1 以单独验证终局。移动及命中均由游戏端执行。

| 场景 | 专服结果 | 证据目录后缀 |
|---|---|---|
| long / 显式取消 | ACK 后没有 IPC 状态查询或续期。36.06 秒后观察仍在战斗；开始更新 137，核验到 2068 次 ItemCheck。首次和末次原生命中在 150 和 2118，间隔 1968 更新。40 次箭命中、9 次剑命中，Boss 余血 2660。取消收到 explicit_cancel 和 inputsReleased。 | 9a72e369b16d4da9a3b97ff8b9cb3958 |
| win | Lua 扫描根实例并提交，专服箭击后得到同一根的死亡事件、致死贡献及释放回执，await_task 通过 Python 终态核验和 JSON Schema。 | c420dbd8876e406bbfe34c6e8a969f40 |
| disconnect | 实际关闭发起连接，随后经 fixture 独立核对更新 131、162 的控制位、输入作用域、停止的攻击计数及 Boss 生命。 | 93e6c7ffb358474ea20aded6f72b22f7 |
| death | 原生 KillMe 后确认 Actor 死亡，再于更新 181、212 观察到身体和攻击输入已释放。 | 4588861b592e40d8ba35ced89bd6f342 |
| generation | 注入 Actor 代次 1→2；更新 176、208 观察到旧战斗已停、输入为空、无继续伤害。本例复用同一 Player 对象测试代次失效。 | f4700502f56747aa8df4e84cb7bfbec9 |
| timeout | 指定 5 秒墙钟期限，命中后由专服以 timeout / execution_deadline 结束；回执确认释放，更新 472、504 再次独立采样。 | 85bc5cade8f14c40917f32b2a123d429 |

以上目录位于 `.local/direct-input-native-<后缀>`，原始数据见 combat-results.json、manifest.json。复现时构建到 `.local/boss-e1-build`，设置环境变量 COMPANION_COMBAT_CASE 为 boss-long、boss-win、boss-disconnect、boss-death、boss-generation 或 boss-timeout，运行 `tests/verify_server_ai_direct_input_native.ps1 -Combat -PackagePath .local/boss-e1-build/Mods/CompanionBridge.tmod`。同包的 loadout-trip 定向回归通过。此阶段的证据覆盖单根自主执行和撤销；免伤场景未评估常规装备下的生存胜率。

## E2 多成员实装与隔离专服验证（2026-11-18）

握手增加 `server_ai_boss_members_v1`。提交仍用 `boss_encounter` 和根实例的 `details.entityRef`。进度中 `seed` 保留请求实例，`target` 随可攻击部位变化。可查询活成员数、吞噬者头数、累计核验伤害和最近四条命中。每条命中分开发射时瞄准部位、实际受击部位、生命承载者、命中时的当前部位与手持物。直接弹保留发射时允许的实例集合；当前绑定、任务、弹幕身份和受击实例在碰撞时再校验。每个原生伤害事件按承载者的生命变化入账一次。

**同型并发与终局。** 毁灭者按出生来源及指向确切头实例的 realLife 归组。骷髅王手和月总头手同时查出生来源及父槽。吞噬者入场需确认完整链；分裂时以断裂前邻接、已观察到的死亡和存活侧的双向链接确认新头尾。证据断裂时返回 `boss_worm_lineage_unconfirmed`。双子只接受同帧、同来源特征且唯一的一红一绿；多个候选或曾观察到同型重叠返回 `boss_twins_pair_ambiguous`，出生条件不匹配返回 `boss_twins_pair_unconfirmed`。已确认成员在同一身体租约下切换；暂时无攻击窗口继续观察，仍受任务期限约束。胜利回执的 `deathProof` 列出规则所需实例及其原生死亡更新号，外部击杀与 AI 自身伤害分开记录。

**本次实测。** Terraria 1.4.4.9 / tModLoader 2026.9.3.1，包 SHA-256 为 `141AF33455DEDC7C58526128B4C142F72915E8503502851EDAD997D4451A353A`。每个场景使用独立复制世界和免伤测试角色。毁灭者由引擎生成两条完整分段链，fixture 固定分段位置并在发射后改变近段位置以复现交叉命中；选栏、发射、飞行、碰撞与扣血走原生路径。断段、断手和双眼死亡由 fixture 的 StrikeNPC 触发。下列后缀对应 `.local/direct-input-native-<后缀>/combat-results.json`。

| 定向场景 | 观察 | 证据后缀 |
|---|---|---|
| destroyers | 第 633 更新弓向槽 4 发箭；664 剑命中近段槽 5；666 仍手持剑、瞄准槽 5，旧箭命中槽 4。根先后从 80000 降为 79999、79998，累计两击 2 点。箭击时槽 4 面板从 80000 直接更新到 79998，单次只计 1 点。另一根保持 80000。 | 899ec36c14f94354a58e04a3d61a565e |
| pierce / sentry | 同一红玉法杖弹在 1269、1289 分别打中两段，按根的两次变化计 4+6。另一次场景中持剑时，寒霜九头蛇子弹可追溯到父弹身份，93 点独立记入正确根。 | d95f36c1d7ce4e0685b3abf55f6a9ebb / 9c9b48be11b64972a21ebaad3b0c67b7 |
| reuse | 发箭后替换原部位；箭经过同槽同类型的新实例碰撞盒，新实例保持 80000，旧箭无命中回执。 | 64ebaca491174b0fa69df33ad3c7a220 |
| eater | 中段死亡后仍在运行，追踪 66 个存活成员、两个头。 | 10257c95c8314678becfc1b462eacdef |
| twins / ambiguous | 第一眼死亡后改打第二眼，第二眼死亡后才完成；终局列出更新 99、101 的两个不同实例。两对同帧共存时拒绝配对。 | 405243749a934478b2d438954e5772b6 / e70ae27a79154a3e8f158a43fa50b8ec |
| skeleton / moon | 骷髅王断手后从槽 3 切到另一只手槽 4，任务继续。月总核心仍免伤时追踪四个成员并选择暴露的手。 | 80a2169380e843c89d74d706caa62990 / c04ea46ea618439ca82528f14e989967 |

复现时构建到 `.local/boss-e2-build`，将 COMPANION_COMBAT_CASE 设置为 `boss-members-` 加表中的一个场景名，使用与 E1 相同的 `-Combat` 入口和新包路径。同包回归通过 boss-win 与 dual-weapons。结果适用于表内原版实例关系、武器及控制场景；未覆盖吞噬者任意多次同时断裂、Mod 自定义父子关系或整场生存胜率。E3 的实时威胁预测与躲避尚未实施。

## E3 实现与隔离专服实测（2026-11-18）

专服游戏线程在 Boss Encounter 内观察所有活跃、有接触伤害的敌对 NPC 和有伤害的敌对弹幕，包括场外来源。NPC 按实测速度外推，普通弹幕按每次位移乘原生 MaxUpdates 外推。以玩家与威胁的相对线段扫过扩张碰撞盒，覆盖高速物体在两次采样之间穿过玩家的情况。月总死亡光束对候选玩家姿态调用原生 Projectile.Colliding，使用当前束形。遇到加速、转向或改变束形时，下次更新以新观测修正。旋转预告、追踪制导、传送和自定义 Mod 光束的专用预测仍未验证。

每个更新最多保留 12 个附近威胁，按接近时间、预计擦过距离和光束类别保留候选，累计丢弃次数。预测风险窗口 48 更新。安全时沿用已有近战站位或远程射位；有危险时，由同一运动驱动在原生移动边界选择刹停、水平移动或不同持续时间的跳跃。每次最多九个候选，共用 PhysicsSimulator 的碰撞、地形风险、落地与刹停检查。垂直跳回同一高度仅在躲避候选中计为有效。缓存轨迹继续执行前会检查本步原生预测姿态、地形和余下轨迹的暴露风险。武器使用仍由原生 ItemCheck 驱动。暂时没有可攻击部位时，任务仍可躲避。

若已搜索的候选都有危险，任务公开报告 no_safe_route，选择经过地形核验且预测暴露较少的动作；没有通过地形检查的候选时交付中性输入。该原因只描述本次局部搜索。任务进度提供预测窗口、重新规划次数、无安全候选次数、截断次数及已执行预测步的最大位置偏差。

### 对照条件和读数

包 SHA-256 为 `3D02E1DC709C94C73851FF88944E21B423A6EBF0FB648A5E65528C3F8E91F92D`。每个场景一个隔离专服，两组在该服依次重建同一地形和初态，使用 300 生命、木剑、木弓和 999 支木箭，对比现有站位与加入躲避的站位。fixture 固定 NPC 的运动序列、弹幕生成时机和攻击起点，关闭测试眼球原有的强制免伤。两组均执行原生移动、使用物品、碰撞及 Hurt。每组记录 96 个更新，攻击窗口计已满足武器、冷却与几何条件并发出原生按下意图的更新；另计实际命中。冲突检测包括同帧多次移动注入、同时按左右及物品执行前后移动输入变化。表内成对读数均为关 → 开。

| 场景 | 实际损失 HP | 可攻击窗口 | 实际命中 | 最慢完整更新 ms | 开启时最慢候选规划及输入写入 ms |
| --- | ---: | ---: | ---: | ---: | ---: |
| NPC 接触 | 32 → 0 | 5 → 4 | 4 → 3 | 138.22 → 29.83 | 0.45 |
| NPC 冲刺 | 17 → 0 | 4 → 2 | 4 → 2 | 148.42 → 34.42 | 0.68 |
| 飞行 DeathLaser | 34 → 0 | 4 → 4 | 3 → 3 | 33.35 → 31.61 | 0.87 |
| 月总死亡光束 | 104 → 66 | 4 → 4 | 3 → 3 | 39.65 → 29.76 | 2.50 |
| 平台边缘冲刺 | 17 → 0 | 3 → 2 | 3 → 2 | 38.38 → 128.48 | 2.77 |
| 封闭空间 | 50 → 48 | 4 → 4 | 4 → 4 | 154.19 → 5.18 | 3.53 |

十二组输入冲突计数均为 0，取消后均核验了输入释放。平台基线被击退后低于起始站面 489.28 像素；开启躲避后跃起 98.07 像素并回到原站面高度。光束场景使用固定朝向和长度的原版 455 号弹幕及其真实伤害路径，两组分别被击中三次和两次，第一次均在光束生成当帧。封闭空间两组均受到三次接触伤害，开启组报告 no_safe_route。单次伤害有原生浮动，封闭场景的两点差值不能解释为成功躲过一次攻击。

完整更新时间计复制世界中的 Main.Update，包含原生执行和已启用的诊断钩子。候选计时仅覆盖移动驱动内的候选模拟、选择及输入应用。各服先跑关闭组，再跑开启组，运行含首次编译、诊断栈采集及并行专服的调度开销。实测有更新超过 60 Hz 的 16.67 ms 帧预算，这组数据不能给出线上最坏时延保证。

复现方式见下。将场景后缀分别改为 contact、charge、bolt、beam、edge、trapped，每次使用新隔离 profile。在 profile 中，evasion-场景-off/on.json 保存逐更新位置、生命、武器、按下机会、耗时、原生受伤来源及候选样本；combat-results.json 和 manifest.json 保存任务证据及包指纹。另用同包运行 boss-win 核验了原生击杀与终态契约。

```powershell
pwsh -NoProfile -File scripts/build-companion.ps1 -OutputPath .local/boss-e3-build
$env:COMPANION_COMBAT_CASE='boss-evasion-edge'
pwsh -NoProfile -File tests/verify_server_ai_direct_input_native.ps1 -Combat -PackagePath .local/boss-e3-build/Mods/CompanionBridge.tmod -Port 28270 -IpcPort 28271
```

## E4 战略入口、扩展与整场实测

运行时握手 `server_ai_boss_strategy_v1` 开放 `discover_bosses` 和 `cancel_task.retreatTo`。旧握手保留其原有操作，现代帮助、宿主分发和 IPC 均核对能力或任务所有权。普通配装只需主、副武器中至少一项；召唤槽可选。帮助中的配装示例从可用快捷栏候选读取类型、按键和新鲜物品 token。附近同类武器有多个候选时，示例返回待选择的角色。配置写入整份替换，读回 `query_combat_loadout` 可检查各角色及资源。

1. 调用 `discover_bosses({})`，从同一采样帧、角色周围 640 像素的候选中读取 `entityRef`、策略、可受理性和原因。配对不明或识别能力不足时保留原因。真正受理时重新核对世界、代次、距离、NPC 实例和部件关系。
2. 以选中的引用调用 `submit_task({operation="boss_encounter",arguments={entityRef=...}})`。返回的 taskId 可用于 `query_task`；当前手持、部位切换、累计核验伤害、死亡事件和躲避决策均在服务端更新。
3. `cancel_task({taskId=id})` 直接撤销。提供 `retreatTo={feetX=x,feetY=y}` 时，坐标为世界像素，应先观察落点。游戏端核对 1600 像素内的安全支撑，撤销攻击授权，并在原任务和连接所有权内驱动限时导航。最多 900 个更新，保留原任务墙钟期限。确认停稳、足底覆盖目标点、320 像素内没有威胁 NPC、160 像素内没有伤害弹幕后，返回 cancelled / boss_retreat_verified 和落点证据。受理不等于抵达；地点不安全、仍在交战、超时与原生运动失败均可分别识别。脱离半径为到达时的局部观察。

### 样例 Mod

[BossStrategySample](../experiments/BossStrategySample/Sample.cs) 在 PostSetupContent 按名称取 Mod，用 `Mod.Call("BossExtension",1,...)` 注册基础类型描述符。项目无 Companion 程序集引用和 modReferences。每个 root 声明其原生死亡是终局；来自已验证根实例的 realLife 成员自动纳入。非共享血条部件可以注册 member、根全名、部件全名及 ai 槽索引，仍要复核出生时的父实例。gravity、弹幕全名、垂直加速度声明按游戏更新预测的轨迹。版本不匹配返回 unsupported_version；同一键不同定义会禁用该键，自定义弹幕退回通用速度外推。注册表在使用后关闭，卸载时清空。

独立样例在启动时断言版本 2 被拒、版本 1 被接受、冲突返回 conflict。专服观察到三个成员，包括一个标准共享生命段和一个自定义关联段。满血样例战斗记录 41 次原生命中、3500 点归因伤害，应用自定义轨迹 445 次、观察到冲突回退 430 次。另两次分别验证直接取消、从地形观察选点并撤退。撤退回执的目标足底 Y 和实际足底 Y 均为 3504，实际水平足迹覆盖目标 X=33944，攻击已撤销且输入已释放。

样例通过复制世界测试标记、指定 Mod 目录、包名及显式包 SHA-256 检查进入专服。普通启动仍执行已有引擎指纹和 Mod 隔离条件。此次加载的外部包仅有 BossStrategySample，Calamity 与 Fargo's Souls 仍无实机兼容结论。

### 整场条件与复现

原版六场均使用独立复制世界、夜间平坦竞技场、500 生命、星旋套、三个伤害饰品、毁灭巨刃、手枪和普通火枪子弹。吞噬者用黑檀石竞技场并在 fixture 初始化时设置无客户端角色的腐化区域标志。六场均从自然满血和原生 AI 开始，未给角色持续免伤，未代打、缩减 Boss 生命或改写战斗轨迹。每场 start 后只等待文件中的游戏线程终态，再读一次权威行动回执；没有模型调用、IPC 状态轮询、续租或新增控制输入。独立样例另走常规 Lua 任务宿主，宿主的只读回执监视与 LLM 分离。

构建和复现命令如下。在仓库根目录运行，场景后缀可换为 eye、eater、skeleton、twins、destroyer、moon。样例额外构建包，使用 sample、cancel、retreat 后缀，给启动脚本传入 `-SamplePackage .local/boss-e4-build/Mods/BossStrategySample.tmod`。每次启动单独复制包和世界，profile 中的 combat-results.json、whole-terminal.json、逐时观察及复制包指纹用于复核。

```powershell
pwsh -NoProfile -File scripts/build-companion.ps1 -OutputPath .local/boss-e4-build
$sample = (Resolve-Path experiments/BossStrategySample).Path
$output = (Resolve-Path .local/boss-e4-build).Path
Push-Location D:/steam/steamapps/common/tModLoader
try { & ./dotnet/dotnet.exe tModLoader.dll -server -build $sample -tmlsavedirectory $output } finally { Pop-Location }
$env:COMPANION_COMBAT_CASE = 'boss-whole-eye'
pwsh -NoProfile -File tests/verify_server_ai_direct_input_native.ps1 -Combat -PackagePath .local/boss-e4-build/Mods/CompanionBridge.tmod -Port 28400 -IpcPort 28401
```

### 同包六场结果

CompanionBridge 包 SHA-256 为 `57C790127C24B2FD091A8D83542ABED8936A11E683C8309CDB30C05CAF2C941D`，样例包为 `B676C06FBEBC026191265366065DC29D8FB3D30A48D53537CF285ACC9C4F6A12`。下表按复制进各 profile 的实际包核对。

| Boss | 终局 | 执行更新 | 已核验命中 | 已归因伤害 | 成员峰值 | 本地 profile 后缀 |
|---|---|---:|---:|---:|---:|---|
| 克苏鲁之眼 | 原生击败 Boss | 590 | 40 | 2800 | 1 | e978037d76a840a5ac9a1989680422d3 |
| 世界吞噬者 | 原生击败 Boss | 3224 | 157 | 9750 | 67 | 6410c86e71d745669c401ec0e7502447 |
| 骷髅王 | 原生击败 Boss | 1301 | 77 | 5550 | 3 | 9127c8560650408bb996f3555fbc45a1 |
| 双子魔眼 | 角色死亡 | 3649 | 132 | 9966 | 2 | 47e4e4554da145f8966c5a9cf31f0518 |
| 毁灭者 | 角色死亡 | 2367 | 106 | 8512 | 82 | c151155bacc140fa8de7e651e86fb277 |
| 月亮领主 | 角色死亡 | 2340 | 84 | 3866 | 4 | a0627c9d29a74ea6ac265c5c8514225f |

三场胜利均有绑定到本场成员的原生死亡证据。另三场为 actor_not_alive，核对了角色死亡、Boss 仍活跃、任务终止及输入释放。当前装备与战术的整场胜绩限于表中前三场。

### 多层平台走位定向复测（2026-11-18）

专服在现有躲避决策中模拟沿平台移动、起跳和持续按下穿过当前平台的轨迹，检查沿途碰撞与落点。优先降低预测威胁，在风险相同的候选中按攻击目标的高度及远程交战距离选择。验证范围为干燥、水平的原版平台。

每个 Boss 使用复制世界跑三次。平地宽 221 格，多层组在同一地面上方 4 格和 8 格各增加一层平台。基础组均为 500 生命、星旋套、远程徽章、毁灭者徽章、复仇者徽章、毁灭刃、手枪及 9999 发火枪子弹。盾组只把复仇者徽章换为钴护盾。每场固定生成位置、夜间时刻和随机种子 9817，提交一次任务后运行 1200 次原生更新。各组角色起始位置、速度、生命和弹药一致。开始采样时 Boss 已生成 5 次更新，毁灭者头在平台组相对平地组偏移 1.35 像素。

| Boss | 地形和饰品 | 跳上 / 下穿落地 | 沿平台背离目标位移（像素） | 受伤次数 / 伤害 | 有效命中 / 造成伤害 | 本地 profile 后缀 |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| 双子 | 平地·徽章 | 0 / 0 | 0 | 10 / 180 | 48 / 4300 | d9097d2963f34950a5c58c5c4a8f44c4 |
| 双子 | 多层·徽章 | 6 / 5 | 27.0 | 10 / 192 | 54 / 4901 | ce0ab246f4794a8ca30219bb075e0ec2 |
| 双子 | 多层·盾 | 6 / 6 | 11.4 | 10 / 132 | 60 / 4323 | 3947c338a7a04b0090cba44f4325d1a4 |
| 毁灭者 | 平地·徽章 | 0 / 0 | 0 | 10 / 246 | 38 / 2979 | 97b4c6da71b748a1ad6102cef0ec19ff |
| 毁灭者 | 多层·徽章 | 5 / 5 | 94.7 | 12 / 223 | 66 / 3950 | 19b954cf0f6147f9a61ee888e32003b2 |
| 毁灭者 | 多层·盾 | 6 / 6 | 192.2 | 7 / 155 | 59 / 3122 | 7e9d99bfa8974f2b97a15deef718d3c9 |
| 月亮领主 | 平地·徽章 | 0 / 0 | 0 | 9 / 395 | 39 / 1502 | 6d335f01e1a240c681c82fbd3b5bf059 |
| 月亮领主 | 多层·徽章 | 4 / 2 | 133.9 | 9 / 365 | 34 / 1561 | e05b815a99604f7a891b65c388f31edb |
| 月亮领主 | 多层·盾 | 6 / 5 | 79.0 | 7 / 280 | 39 / 1646 | 201a4650ef1d40eca67ef7b34355c81b |

换层计数要求对应原生输入和另一层的实际支撑。六场多层战均记录到主动下穿后再次跳回原高度。水平位移只累计连续有平台支撑、与输入同向且背离当帧所选部位原位置的移动；这个数值无法衡量追击过程中的净间距。受伤统计来自原生受伤回调，有效命中取本场归因证据。每组仅一次，比较适用于本次采样窗口。

包 SHA256 为 CE5D2941881E673A3DC1BAF7891592B03F27A085E9E45161DA85C841A93FC002。复核时向 tests/compare_boss_platform.py 传入表内九个 .local/direct-input-native-<后缀> 目录，各目录的 arena-result.json 含逐次落脚记录。

### 云朵瓶与天使之翼的定向机动对照（2026-11-19）

BossEvasion 使用当前角色的额外跳跃可用状态和剩余翼时，在普通走跳及下穿之外，比较空中松键再跳和限时飞升的轨迹。每条轨迹检查碰撞、威胁暴露和落脚条件。预测暴露相同时，只有站位收益足以抵消消耗才优先花费机动资源。只读装备快照确认天使之翼（物品 493）的功能槽是 2，基础飞行时间 100；模型识别同时核对已验证的翼参数。

三次独立复制世界使用同一包 CD38A3A0AC828713B2363A708C7BD9F6837B4453B816F0F8B3C28AD6A009C336。221 格地面，上方 8 格一层平台。诊断用克苏鲁之眼具有高 216 像素的接触体，每帧 8 像素在固定端点间往返。三组均从同一坐标静止起步、500 生命、相同武器弹药、无护甲，唯一配装差异为机动饰品。每场观察 1200 次原生更新后取消，均核验输入释放。

| 配装 | 选中专用轨迹次数 | 原生能力表现 | 用能力后落地 | 受伤次数 / 伤害 | 有效命中 / 造成伤害 | 本地 profile 后缀 |
| --- | ---: | --- | ---: | ---: | ---: | --- |
| 无机动饰品 | 0 | 无额外跳跃或翼时消耗 | 0 | 20 / 312 | 50 / 5941 | 198228ff95284f189a5d38fbc3cbb3d8 |
| 云朵瓶 | 14 | 7 次空中上升时进入额外跳跃激活态 | 7 | 3 / 46 | 31 / 1739 | 989d591f8183400d9c1be8fc5f6c11b9 |
| 天使之翼 | 22 | 空中累计消耗 183 帧翼时 | 7 | 5 / 93 | 73 / 3481 | 64de704b53a445558ea4c5646b1080db |

落地按实际位置与原生脚下支撑判定。两组机动配装均实际登上高层，基础组全程在低层。重规划可能多次选择专用轨迹，所以选中数与实际触发数分别报告。每组单次，仅验证受控接触威胁和基础移动参数；原版 Boss 完整 AI、不同护甲和复杂弹幕中的收益仍待验证。以 COMPANION_COMBAT_CASE=boss-whole-arena-eye-{bare,cloud,wings} 分别运行 verify_server_ai_direct_input_native.ps1 -Combat；证据在各 profile 的 mobility-profile.json 和 arena-result.json。

### 多人复活归队准备（2026-11-19，尚未实现）

**引擎观察。** 在本项目固定的 tModLoader 2026.09.3.1 中，Main.DrawMap 的队友头像分支检查组队及 HasUnityPotion；点击后用队友当时的 position 调用 UnityTeleport，再调用 TakeUnityPotion。后者按原生顺序查找背包和可用的虚空袋，并经过 ConsumeItem 钩子。多人模式的 UnityTeleport 发送消息 65（模式 2、样式 3）；MessageBuffer.GetData 的对应分支调用 Player.Teleport 并执行网络同步。仅在专服上调用 UnityTeleport 不能作为服务端角色已传送的证明。当前 query_map 提供探索记录；它没有实时头像或队友会话身份。

**第一步验收。** 在复制世界用独立 AI 和一个真实联机客户端测试。先观察双方的实际队伍，包括 AI 初始是否未入队；队伍为零或不同、队友已死亡、离线或换人占用同槽时给出原因。提供可选择的队友引用，绑定世界和该次上线实例；执行时再解析位置。对绑定 AI 验证原生传送、虫洞药水数量变化、专服位置以及队友客户端看到的位置。部分效果不明时返回 unknown，禁止重放。无需打开图形地图或操作队友的角色。

**第二步验收。** 在已有 Boss 战中预先指定归队对象。死亡结束旧动作并核对输入释放；仅保留已授权的归队意图、队友引用和 Encounter 实例标识。观察 NativeSurvival 的自然复活，在新生命期至多发起一次归队。归队后重新发现并核对原场次，仍活跃才以新任务加入，记录一次真实命中。另测死亡期间取消、队友换队、Boss 已消失和无药；报告归队结果与参战结果。上述传送和续战属于待测设计，本轮仅复核了引擎 IL。

### 多人复活归队实现与验证（2026-11-19）

能力握手 `server_ai_regroup_v1` 增加三个独立入口。用 `list_teammates` 读取 AI 和在线玩家的队伍，以及绑定到世界、Player 对象和本次网络连接的 `teammateRef`。用 `set_team({team=3})` 显式设置 AI 自己的队伍，队号来自观察。用 `teleport_to_teammate({teammateRef=ref})` 在游戏线程重新核对队友，原生传送到其当前位置并消耗 AI 的虫洞药水。成功回执包含双方位置、实际队号和药水前后数量。

在 `submit_task` 的 `boss_encounter` 参数中加入 `regroupTeammateRef` 可以预授权复活归队。死亡时释放攻击和移动输入。原生复活后，同一世界、Actor 对象和槽位必须恰好进入下一代，并且自然复活计数增加一次。专服以新任务 ID 执行，继承剩余墙钟期限及原连接的断连信号，重验原 Boss 实例、成员、配装与队友。旧绑定失效。新绑定可通过 `query_combat` 找到新任务，再查询或取消。新任务的 `previousTaskId` 指向上一次生命的任务，成功传送的证据保存在 `regroupProof`，有效命中按新任务记录。旧根消失或成员关系无法确认时结束归队。

**隔离专服实测。** 构建包 SHA256 为 `264BE2CA228FFCD6D7D307B955AC7ADB476C6729BD06F51BCD7A9E813B42CDBE`。以下为复制世界的独立运行，均未调用模型。

| 场景 | 实测结果 | 本地 profile 后缀 |
| --- | --- | --- |
| 队伍和原生药水 | AI 初始队伍 0，Lua 设置为 3 后查询一致，失效引用被拒绝。用未接入网络的诊断 Player 单独验证药水执行路径，位置改变 128 像素，药水 2→1；异队、死亡、无药均返回对应原因。 | `85f1aac9da394580a8fe6ff92cf58179` |
| 死亡中取消 | 克苏鲁之眼交战中有原生命中，AI 死亡后进入等待，攻击停止并释放输入；显式取消取得已释放终态。 | `13768c33f8fc4e219ef9ddf3297bfba6` |
| 自然复活及原 Boss 消失 | 从第 1 代自然复活到第 2 代；旧连接查询被拒绝，新绑定观察到另一任务 ID，并通过 `previousTaskId` 对上旧任务。原根已消失，终态原因为 `boss_original_root_gone`。 | `0e3505cba7fd4aed8f333b0e13247fad` |

**待多人验收。** 独立 tModLoader 客户端在 Steam 登录检查处退出。仍需用已登录客户端确认实际队友发现、离线换人、远端所见位置，以及 Boss 仍活跃时传送后的首次有效命中。上述 128 像素结果来自专服药水执行路径。
