# Numen 参考研究

**研究资料 · 非规范性**

参考项目：[Dwinovo/minecraft-numen](https://github.com/Dwinovo/minecraft-numen/tree/1.21.1)。

Numen 可作为 Agent、Lua 程序执行、原子 API、感知、导航、任务生命周期和可观测性设计的研究样本。它不是 Terraria AI Companion 已采用的目标架构；本项目现行模块职责以 [系统架构](../ARCHITECTURE.md) 和 [正式文档索引](../INDEX.md) 为准。

## 2026-11-28 模块与玩家操作覆盖审计

Numen 采用 `1.21.1` 的固定源码快照 `2cd1484`。Terraria 采用 `9f20af9` 加本次审计时的工作区，包含尚未提交的队友交付实现。下文以玩家委托独立服务器 AI 为主，另查普通客户端的帮助和准入。Numen 一侧核对实现，本次没有运行 Minecraft 或重新启动 Terraria 专服。[Numen 源码树](https://github.com/Dwinovo/minecraft-numen/tree/2cd1484530cc45a96efb52801461e131fa9623a3) · [Terraria 执行链](../ARCHITECTURE.md)

Numen 的源码分工是 `ai/agent` 分别处理模型调用与决策循环，`lua/script` 处理沙箱与脚本接口，`numen-api/plugins` 承载身体、扩展点和游戏动作，另有 `mcp`、`permission`、`store`、`ui`、`bench`。在本项目，对应的决策、Lua、外脑和记忆在 Python，执行和身份核验在 C# 的 `ServerIpc/ServerPlayerRuntime`，运动规划在 `Motion`，客户端另走 TCP。在“跟着我下矿”这种委托里，两边都有决策、角色和寻路三个模块，真正决定能力差异的是目标能否绑定一个持续移动的队友。[Numen 模块职责](https://github.com/Dwinovo/minecraft-numen/blob/2cd1484530cc45a96efb52801461e131fa9623a3/docs/architecture-mind-model.md) · [Terraria 模块职责](../ARCHITECTURE.md)

### 从 API 名称到玩家动作

将 C# 握手源码中的能力位合并，交给生产代码 `operations_for_capabilities` 和 `server_definitions`，得到下列**静态上限**。实际连接会根据执行器、查询端和引擎能力收窄。这里的分母是仓库定义的名字，不能据此计算 Terraria 玩家操作的覆盖率。[握手](../../CompanionBridge/ServerIpc/AiReadOnlyProtocol.cs) · [动作能力](../../CompanionBridge/ServerIpc/AiAtomicActions.cs) · [后端白名单](../../python/server_ai.py) · [模型契约](../../python/server_ai_contract.py)

| 核对层 | 结果 |
|---|---|
| 共享操作 Schema | 95 个名字，包含查询、动作、等待和任务操作。 |
| 独立服务器投影 | 最多 90 个。另 5 个是 `get_player_status`、`observe_exploration`、`drop_through_platform`、`attack_entity`、`place_wall`。其中攻击可走 `combat_attack` 任务，放墙可走蓝图，探索另有场景与地图查询。 |
| 模型直接可查的函数 | 84 个共享操作加 5 个任务管理函数，共 89 个定义，逐项均能取得返回 Schema。`combat_attack`、`boss_encounter`、`collect_drops`、`follow_teammate`、`place_item`、`build_place` 这 6 个操作通过 `submit_task` 调用。 |
| Lua 组合函数 | 审计时有 6 个导出，随后新增 `container.put` 和 `container.take`，共 8 个。Worker 另有 `explore`、`sense_world` 等辅助函数。 |

**普通客户端工具清单已修复。** 审计时，默认 Lua 说明列出了本地玩家无法调用的 `plan_materials`、`query_map`、`loot_all`。现在 TCP 握手决定直接工具和 Lua 各能调用哪些接口。定向测试用有效参数调用这三个接口，核对它们均在发出游戏请求前被拒，并通过同一连接成功读取玩家状态。[客户端握手](../../python/control.py) · [验收用例](../../tests/test_api_contract_mvp.py)

### 相同委托在两边怎样完成

| 玩家委托 | Numen 的调用 | Terraria 的实现和差距 |
|---|---|---|
| 给我 12 支火把 | `inv.give(target,item,count)` 接受实体或位置，走到两格内调用 `inv.drop`，返回丢出的数量。[inv.lua](https://github.com/Dwinovo/minecraft-numen/blob/2cd1484530cc45a96efb52801461e131fa9623a3/plugins/minecraft/common/src/main/resources/numen/modules/minecraft/inv.lua) | **已实现** `terraria.inventory.give(teammateRef,"Terraria/Torch",12)`。有可用虫洞药水就传送，否则按需走近。从原槽依次短抛，限普通背包未收藏的原版可堆叠非钱币物品。`dropped` 是已抛总数，`drops` 记录每一抛。指定队友的 `lastDrop.confirmedReceived` 对应最后一抛。固定 MCP 的两场实收均为 12，另有先收 5 后补齐、第三人拾走而指定人实收 0 的记录。自由模型能否据此正确答复仍待测。[Lua 实现](../../python/lua/modules/inventory.lua) · [专服与客户端验收](../../tests/native_torch_delivery.py) |
| 从那个箱子取 12 支，多余的放回去 | `container.take/put(target,item,count)` 处理接近、开关箱和槽位分配，返回实移数。[ContainerApi](https://github.com/Dwinovo/minecraft-numen/blob/2cd1484530cc45a96efb52801461e131fa9623a3/plugins/minecraft/common/src/main/java/com/dwinovo/numen/plugins/minecraft/tools/station/ContainerApi.java) | **审计后已补齐 Lua 入口。** 跨委托取绳脚本已通过 `terraria.container.put/take` 存入两堆共 20 根、远处返回取 10 根，以及原箱仅余 6 根时取 6 报差 4。[模块说明](../AGENT_API_AND_GAMEPLAY_MODULES_DESIGN.md#从指定箱子按数量存取) · [专服验收](../../tests/native_storage_memory.py) |
| 跟着我下矿，途中帮我清怪 | `move.follow` 接受持续跟随的对象；`fight.clear` 通过扫描和逐个攻击清理附近敌人。[MoveApi](https://github.com/Dwinovo/minecraft-numen/blob/2cd1484530cc45a96efb52801461e131fa9623a3/plugins/minecraft/common/src/main/java/com/dwinovo/numen/plugins/minecraft/tools/work/MoveApi.java) · [fight.lua](https://github.com/Dwinovo/minecraft-numen/blob/2cd1484530cc45a96efb52801461e131fa9623a3/plugins/minecraft/common/src/main/resources/numen/modules/minecraft/fight.lua) | **专服已支持移动队友跟随。** `terraria.move.follow` 绑定在线同队玩家，可在同一任务内转向、停步、自卫并继续跟随。限定半径主动清怪仍可组合扫描和战斗任务。[跟随任务](../../CompanionBridge/ServerIpc/FollowTeammateTask.cs) · [专服验收](../../tests/native_follow.py) · [战斗](../COMBAT_SYSTEM_DESIGN.md) |
| 看看现在适不适合出门，捡到的材料能做什么 | `status.world()` 返回时间、明暗和天气；`inv.uses(item)` 按输入材料反查配方。[StatusApi](https://github.com/Dwinovo/minecraft-numen/blob/2cd1484530cc45a96efb52801461e131fa9623a3/plugins/minecraft/common/src/main/java/com/dwinovo/numen/plugins/minecraft/tools/perception/StatusApi.java) · [InvApi](https://github.com/Dwinovo/minecraft-numen/blob/2cd1484530cc45a96efb52801461e131fa9623a3/plugins/minecraft/common/src/main/java/com/dwinovo/numen/plugins/minecraft/tools/inventory/InvApi.java) | **专服已支持世界状态查询和材料用途反查。** Agent 每轮收到游戏钟点，Lua 可用 `get_world_state` 读取天气、事件、角色群系和深度，以及难度和部分 Boss 进度。专服可用 `query_recipes({ingredientType=材料ID})` 查询材料参与的已启用配方，覆盖配方组替代材料；另有局部场景、地图、物品资料、NPC 掉落来源和递归材料规划。[客户端世界读取](../../CompanionBridge/Common/Systems/AtomicOperations.cs) · [服务端读取](../../CompanionBridge/ServerIpc/ServerAiQueries.cs) · [原生配方](../../CompanionBridge/ServerPlayerRuntime/ActorCrafting.cs) |
| 材料够了到工作站做一把工具 | `inv.make` 选配方，对需要工作台的配方找附近工作台或放下携带的工作台。[inv.lua](https://github.com/Dwinovo/minecraft-numen/blob/2cd1484530cc45a96efb52801461e131fa9623a3/plugins/minecraft/common/src/main/resources/numen/modules/minecraft/inv.lua) | **原地制作，野外缺站时回基地。** `crafting.make` 按角色当前的原生配方分批制作并核对数量，缺站时交回工作站要求和剩余数量。Agent 负责返程，到基地制作区后续做。放置新工作站可另行交给放置接口。已有工作台两种库存的固定验收，熔岩镐“查仓、按缺口实采、逐站制作”的完整验收暂缓。特殊条件、回调、Mod 配方也有明确执行限制。[crafting.lua](../../python/lua/modules/crafting.lua) · [合成边界](../SERVER_AI_CRAFTING.md) · [验收状态](../DEVELOPMENT_ROADMAP.md) |
| 钓一条鱼，或给渔夫交任务鱼 | `work.fish()` 从岸边抛一竿、等待咬钩、收竿并报告结果。[WorkApi](https://github.com/Dwinovo/minecraft-numen/blob/2cd1484530cc45a96efb52801461e131fa9623a3/plugins/minecraft/common/src/main/java/com/dwinovo/numen/plugins/minecraft/tools/work/WorkApi.java) | **缺原生钓鱼任务。** 现有水域观察和无参数 `use_item` 没有提供按角色识别浮标、等待咬钩与收竿的执行链。渔夫当日任务及交付又是独立的 NPC 服务。先用“一竿一次，取消后能释放输入”验证钓鱼。[物品执行](../../CompanionBridge/ServerIpc/ServerAiNativeActions.cs) · [玩法审计 P5](../PLAYER_ACTION_PARITY_ROADMAP.md) |

### 其他模块的成熟度

| 模块 | 本项目基础与仍需验证的边界 |
|---|---|
| 感知、导航与采矿 | 有照明地形、语义场景、持久地图、落点规划、矿簇接近与清障。两个自然世界已有地表和浅洞取物记录。深洞岔路折返后能否选中未走过的路仍待实测；这个用例应同时观察选路和实际抵达。[开荒实测](../archive/EARLY_GAME_FIELD_TESTS.md) · [地形挖掘](../GENERAL_TERRAIN_EXCAVATION.md) |
| 建筑、城镇与工程 | 蓝图支持前景、墙和多格物体，可检查住房、分配 NPC、与 NPC 对话；交易实现绑定普通商人，护士与哥布林有专门服务。拉杆可触发已有电路，逐格布线剪线、桶取倒液体、涂漆以及渔夫服务仍缺专用入口。扩大普通 NPC 商店可复用现有报价和核验。[蓝图施工](../BUILDING_V2_IMPLEMENTATION.md) · [商店实现](../../CompanionBridge/ServerIpc/MerchantShop.cs) · [物块交互](../../CompanionBridge/ServerIpc/ServerTileInteraction.cs) · [玩法审计 P4–P6](../PLAYER_ACTION_PARITY_ROADMAP.md) |
| Agent、Lua、任务与记忆 | 已有内置 Agent、外部 MCP、带预算的 Worker、可取消的游戏任务、主动模式、角色笔记和持久委托。内置有序委托可组合 2–8 步导航、挖格、拾取、放置和蓝图施工。固定 MCP 已验证新会话找回原箱；自由模型能否自行记、取、认出库存已变，再恢复多阶段委托，证据不足。Numen 的 `GoalSteward` 是可对照的轮末目标复核实现。[GoalSteward](https://github.com/Dwinovo/minecraft-numen/blob/2cd1484530cc45a96efb52801461e131fa9623a3/agent/src/main/java/com/dwinovo/numen/api/agent/goal/GoalSteward.java) · [持久委托](../../python/player_goals.py) · [主动模式](../../python/autonomous_mode.py) · [笔记与验收](../AGENT_MEMORY.md) |
| 多人控制与玩家界面 | Numen 有同伴名册和按动作类型设置的权限。Terraria 的聊天、MCP 驾驶租约、绑定角色和自主模式授权已各有入口。指定一名队友收件已有实证；多同伴名册、以及“允许跟随，但取这个箱子前先问我”等按对象或动作授权，是另外的产品范围。[Numen 名册](https://github.com/Dwinovo/minecraft-numen/blob/2cd1484530cc45a96efb52801461e131fa9623a3/numen-api/common/src/main/java/com/dwinovo/numen/api/entity/CompanionRoster.java) · [Numen 权限模型](https://github.com/Dwinovo/minecraft-numen/blob/2cd1484530cc45a96efb52801461e131fa9623a3/docs/permission-layer.md) · [MCP 控制权](../TERRARIA_EXTERNAL_BRAIN_MVP.md) · [主动模式授权](../../CompanionBridge/ServerIpc/AutonomyPolicy.cs) |
| 扩展与兼容 | Numen 分离公共 SDK、Minecraft 插件与 JEI 等联动。Terraria 有内容键解析、可加载的玩法攻略、内置 Lua 注册表以及锁定版本的原生执行器；普通第三方 Mod 的专服混装、配方执行和独立玩法注册应单独界定。做原版开荒优先时，第三方 SDK 可以后排。[Numen 插件树](https://github.com/Dwinovo/minecraft-numen/tree/2cd1484530cc45a96efb52801461e131fa9623a3/plugins) · [本项目注册表](../../python/lua_modules.py) · [引擎配置](../TML_ENGINE_MIGRATION.md) |
| 评测与诊断 | Numen 原版评测登记 8 个场景，支持标准解、空操作及真实模型重复运行。本项目统一场景清单有 3 个铜矿布局，runner 提供标准解与空操作；另有独立的专服玩法脚本、真实外脑探索记录。优先将交付、跨会话取仓接入同一裁判，再比较脚本与自由模型各自的成功率、失败环节和耗时。[Numen 场景注册](https://github.com/Dwinovo/minecraft-numen/blob/2cd1484530cc45a96efb52801461e131fa9623a3/plugins/minecraft/common/src/bench/java/com/dwinovo/numen/plugins/minecraft/scenario/VanillaBench.java) · [Numen 评测](https://github.com/Dwinovo/minecraft-numen/blob/2cd1484530cc45a96efb52801461e131fa9623a3/docs/bench.md) · [本项目清单](../../tests/benchmark/scenarios.json) · [本项目裁判](../../tests/benchmark/runner.py) |

按“像队友一样一起开荒”排序，存取接口补齐后可用已有火把委托测一次自由模型。跨站制作与基地取仓可结合通用材料规划验证。若要开启全新玩法，钓鱼是边界清楚、确实需要游戏侧实现的一项。

## 2026-11-28 从提交历史看 Numen 怎样推进

核对了 `1.21.1` 从 5 月 14 日到 11 月 28 日的历史。截至 `2cd1484`，该提交可达的历史有 3,416 条提交。以下沿角色执行、寻路、评测和容器四条线抽查提交、代码与决策记录。[分支历史快照](https://github.com/Dwinovo/minecraft-numen/commits/2cd1484530cc45a96efb52801461e131fa9623a3/)

| 问题 | 提交留下的做法 |
|---|---|
| 新的角色执行方式先做到哪一步？ | 6 月 13 日的 Phase 0 先把移动和采矿接上玩家身体。一次提交穿过工具分发、任务队列、导航和角色执行，使两种动作能从入口跑到底。[角色执行提交](https://github.com/Dwinovo/minecraft-numen/commit/2e838927530e70c5ff11d0c1a248b3edd918ce3f) |
| 寻路越来越复杂，怎么选下一项？ | 7 月 17 日的三期计划把深井等地形写进验收，远期能力按需立项。当天一期用生产预算重放，深井和双口袋都能一次搜完，于是删除了原本用于脱困的学习表。10 月的决策记录又明确了取舍顺序，先避免卡死，再看耗时，路线质量只报告；日常短中程优先。[三期计划](https://github.com/Dwinovo/minecraft-numen/blob/d26e9decd8e49ece3d3fecae6858c0c7648752ed/docs/pathing-stability-roadmap.md) · [一期结果](https://github.com/Dwinovo/minecraft-numen/commit/d6256efb9633901662786e1096cdd6cb985b351e) · [寻路取舍](https://github.com/Dwinovo/minecraft-numen/blob/2cd1484530cc45a96efb52801461e131fa9623a3/docs/adr/pathing/0023-reliability-performance-route.md) |
| 游戏任务怎样算做完？ | 10 月 1 日的挖铁题固定石镐和石中 12 格矿。玩家要求 10 个铁，裁判查背包粗铁，1 个和 5 个记中间进度。10 月 3 日把标准解写成 Lua，记录程序与失败类型。现行评测可单跑预期成功的标准解和预期失败的空操作，两者都无需模型 API；真模型另测成功率、轮数和成本。[挖铁场景](https://github.com/Dwinovo/minecraft-numen/blob/410ffcae043fcd1e958a0209d1ba8bdee9d4fd96/core/neoforge/src/bench/java/com/dwinovo/numen/core/bench/MineIron.java) · [Lua 标准解](https://github.com/Dwinovo/minecraft-numen/commit/648622b04604d8472aef259594928ddf0eae3a2e) · [评测说明](https://github.com/Dwinovo/minecraft-numen/blob/2cd1484530cc45a96efb52801461e131fa9623a3/docs/bench.md) |
| 已发布的接口还会改吗？ | 6 月 17 日曾让模型查看容器界面、点击槽位。11 月 23 日记录了模型操作不可见界面的问题，撤下窗口抽象，改成对指定容器做事，同时删除了上轮快照里的 `numen.inv.store/fetch`。11 月 28 日版本公开的是 `minecraft.container.put/take`，返回实际转移数量。[6 月的界面方案](https://github.com/Dwinovo/minecraft-numen/commit/3c6f54480a57dc58a95959b962fdc2f8c70b3c5c) · [11 月的决策](https://github.com/Dwinovo/minecraft-numen/blob/2cd1484530cc45a96efb52801461e131fa9623a3/docs/adr/control/0021-look-and-do-no-windows-for-the-model.md) · [现行容器实现](https://github.com/Dwinovo/minecraft-numen/blob/2cd1484530cc45a96efb52801461e131fa9623a3/plugins/minecraft/common/src/main/java/com/dwinovo/numen/plugins/minecraft/tools/station/ContainerApi.java) |

## 2026-11-27 对照：从一次外出到连续开荒

取仓库已有的 Numen 源码快照 `ce047ff`，对照本项目外脑通过 MCP 能调用的能力。下表覆盖近期里程碑用到的模块。Numen 一栏记录该版本实现，双方的游戏成绩没有做横向跑分。

| 玩家要做的事 | Numen 的实现 | Terraria 的现状和排期依据 |
|---|---|---|
| 边走边发现 | `move.explore` 分段调用 `walk`，每段用实际位移扣预算，再执行调用者给的 `until_`。 [move.lua](https://github.com/Dwinovo/minecraft-numen/blob/ce047ffb08541798ae95757877233e1a4039ff8a/numen/common/src/main/resources/numen/modules/numen/move.lua) | 两个自然世界已有地表取物、续行和 AiMap 新增证据。浅洞搜集已有两世界实测，深层岔路折返换路仍待验证。[定向探索](../../python/directional_explorer.py) |
| 走到够得着的位置采集 | `DigCompanionTask` 把接近、清除障碍和挖目标连在一次调用中。 [采矿任务](https://github.com/Dwinovo/minecraft-numen/blob/ce047ffb08541798ae95757877233e1a4039ff8a/numen/common/src/main/java/com/dwinovo/numen/task/dig/DigCompanionTask.java) | `mine_target` 已有站位和清障规划。早期铜矿样本暴露了接近站位问题；后续用原铜镐、原站位复验了从清障到 7 格锡矿入包的完整调用。[通用地形挖掘](../GENERAL_TERRAIN_EXCAVATION.md) |
| 遇险后继续原事 | `fight.clear` 组合目标扫描和逐个攻击。内置 `GoalSteward` 在一次运行结束后复核目标、决定是否续跑。 [战斗模块](https://github.com/Dwinovo/minecraft-numen/blob/ce047ffb08541798ae95757877233e1a4039ff8a/numen/common/src/main/resources/numen/modules/numen/fight.lua) · [GoalSteward](https://github.com/Dwinovo/minecraft-numen/blob/ce047ffb08541798ae95757877233e1a4039ff8a/agent/src/main/java/com/dwinovo/numen/api/agent/goal/GoalSteward.java) | 已有自卫、敌怪观察、任务回执和外脑决策。阶段 2A 在两个自然世界记录了遭遇史莱姆后的续行。[战斗系统](../COMBAT_SYSTEM_DESIGN.md) |
| 满包后腾空间再拿 | 地面拾取受阻时，`inventory_full` 指出物品与位置，提示腾空间后补捡。从容器取物时，`inv.fetch` 返回实际取出的数量。[满包消息](https://github.com/Dwinovo/minecraft-numen/blob/ce047ffb08541798ae95757877233e1a4039ff8a/numen-api/common/src/main/java/com/dwinovo/numen/api/event/NumenEvents.java#L74-L87) · [容器取物](https://github.com/Dwinovo/minecraft-numen/blob/ce047ffb08541798ae95757877233e1a4039ff8a/numen/common/src/main/resources/numen/modules/numen/inv.lua#L138-L147) | 2C 分别验证地面漏捡提醒与宝箱“全部拿取”的回执。两者都需让外脑知道物品留在何处，腾出空间后能从原处补拿。[原子接口](../../python/atomic_operations.py) |
| 想清楚该拿什么、能做什么 | 核心 Skill 包含早期生存、采矿、战斗和制作等主题，`inv.recipes` 可查询配方。 [Numen 核心攻略](https://github.com/Dwinovo/minecraft-numen/tree/ce047ffb08541798ae95757877233e1a4039ff8a/numen/common/src/main/resources/numen/skills/numen) · [配方查询](https://github.com/Dwinovo/minecraft-numen/blob/ce047ffb08541798ae95757877233e1a4039ff8a/numen/common/src/main/resources/numen/modules/numen/inv.lua) | 本项目已内置进度、采矿、工具升级、起步房屋、NPC 住房与经济六个主题，外脑可用 `read_skill`。原生配方和材料缺口计算也已存在。3A 用固定 MCP 脚本验证了 7 木材先补料和 10 木材直接制作两种初态。[本项目攻略](../../skills) · [合成](../SERVER_AI_CRAFTING.md) |

### 怎样判断一个模块有多完善

用玩家给的一句话固定目标，再逐次减少外部帮助。脚本给坐标能执行，说明原生动作可用；角色自己找到目标、选动作并核实结果，才有独立完成的证据。接着换初态或加入一次中断，记录能否换办法、回到原目标，以及花了多久。各层保留尝试次数，按当前最强证据排期。

地表和浅洞取物已有外脑实测；满包补拿和工作台制作由固定脚本核对专服库存。增加攻略内容前，应能指出外脑在什么决策点缺了什么信息。[阶段验收记录](../archive/EARLY_GAME_FIELD_TESTS.md)

每个差距对应的玩家委托、测试初态和结案证据见[开发路线图](../DEVELOPMENT_ROADMAP.md#第二和第三阶段的验收建议)。

## 研究问题

阅读具体实现和历史提交时，回答：

- 旧方案遇到了什么实际问题？
- 新设计解决了什么，又引入了什么复杂度？
- 相关测试覆盖了什么，哪些场景仍未验证？
- 后续是否发生过撤销或再次重构？
- 哪些依赖 Minecraft 服务端、实体、方块或网络模型，不能直接移植到 Terraria？

## 建议阅读

- [Architecture Mind Model](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/docs/architecture-mind-model.md)
- [Agent Loop](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/docs/agent-loop.md)
- [Pathing](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/docs/pathing.md)
- [Shell](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/docs/shell.md)
- [Spatial Perception](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/docs/spatial-perception.md)
- [Permission Layer](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/docs/permission-layer.md)
- [Input Pipeline](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/docs/input-pipeline.md)

## 适配原则

1. 借鉴问题解决方法和职责边界，不照搬 API 或模块数量。
2. 先核对本项目已有实现，避免重复造轮子。
3. 把“代码存在”“能力对外开放”“测试通过”“真实游戏验收”视为不同结论。
4. 参考设计若影响控制权、身份绑定、原生执行或失败隔离，必须先评估 Terraria 侧风险。
5. 只有经过本项目证据验证的设计，才写入正式架构或专题契约。

具体审查问题和交付格式见 [架构审查计划](ARCHITECTURE_AUDIT_PLAN.md)。
