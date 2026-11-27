# Numen 参考研究

**研究资料 · 非规范性**

参考项目：[Dwinovo/minecraft-numen](https://github.com/Dwinovo/minecraft-numen/tree/1.21.1)。

Numen 可作为 Agent、Lua 程序执行、原子 API、感知、导航、任务生命周期和可观测性设计的研究样本。它不是 Terraria AI Companion 已采用的目标架构；本项目现行模块职责以 [系统架构](../ARCHITECTURE.md) 和 [正式文档索引](../INDEX.md) 为准。

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
