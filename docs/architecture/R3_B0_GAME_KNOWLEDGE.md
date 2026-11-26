# R3-B0 · Terraria Game Knowledge 架构研究

2026-11-22 · 架构决策建议 · 适用对象为驱动独立服务器 AI Player 的内置 Agent 与 External Brain。

## 1. 建议与研究边界

**建议沿用现有的 `read_skill` 和原生配方查询，优先补上两条联系：从玩家目标反查材料及获取条件，从获取条件找到本世界中值得复查的地点。** 例如，“做一把熔岩镐”应先引出数量、材料、工作站和工具门槛。只有确认背包和可取库存不足，才需要阅读狱石采集策略、调查地狱入口或规划返程。这样，每次加载的知识都能改变下一步决策。

| Agent 此时要回答的问题 | 建议由谁回答 | 交给模型的最小结果 |
| --- | --- | --- |
| 我该考虑哪些路线？ | 分阶段、分任务的 Markdown Skills | 相关小节、选择理由、适用条件，以及下一项可查的知识 |
| 为何能走这条路线，具体缺什么？ | 有来源和版本的结构化关系，加上原生配方注册表 | 少数候选、必要条件、替代项、净缺口和待确认项 |
| 在这名角色、这个世界里能否立刻做？ | 已绑定角色的只读 API、地图线索及现场复查 | 当前材料、工作站邻接、可达候选、采样范围和时效 |
| 做了以后算不算完成？ | 原生动作回执与相关状态复读 | 实际产量、入包或装备结果、未完成部分 |
| 玩家曾说过什么，本世界曾发现什么？ | 私人笔记与世界观察记录 | 有来源的偏好或地点线索，使用前复查易变事实 |

**证据口径。** 下文标为“源码事实”的结论来自生产调用链；“Wiki 规则”与攻略作者的推荐分别标明；“架构推断”解释取舍；第 4–7 节是待实现、待验收的建议。Numen 经远端查询确认默认分支 `1.21.1` 和 HEAD 同指 `ce047ffb08541798ae95757877233e1a4039ff8a`，以下源码链接固定于该提交。Terraria 项目代码以 `f8241da` 工作区为准，与已获取的 `origin/main@338dccb` 的 Git 树差异仅涉及发布文件。目标引擎为 Terraria **1.4.4.9**。官方 Wiki 部分直连请求返回 403，规则取自可检索到的官方页面正文、配方表和版本历史摘录；未取得固定 Wiki 修订号。当前 Wiki 同时描述较新桌面版，正式导入仍需逐项核对版本。[Numen 基线](https://github.com/Dwinovo/minecraft-numen/commit/ce047ffb08541798ae95757877233e1a4039ff8a) · [目标引擎](../../CompanionBridge/EngineProfiles.json)

## 2. Numen 的知识怎样进入一次决策

### 2.1 从资源目录到模型上下文〔源码事实〕

1. **目录表达主题，资源库决定谁能看到。** 核心资源放在 `numen/common/src/main/resources/numen/skills/numen/<主题>/SKILL.md`，插件可以携带自己的命名空间。一篇由 frontmatter 的 `name`、可选的 `description` 和 Markdown 正文组成；较大的主题可以有 `references/`。例如建筑设计按风格拆附件。启动时 `ServerBrains` 按同伴的主人注入技能库；库合并服务器条目和玩家条目，同命名空间、同名时玩家版本优先，房主的两层可合一。[建筑主题](https://github.com/Dwinovo/minecraft-numen/blob/ce047ffb08541798ae95757877233e1a4039ff8a/numen/common/src/main/resources/numen/skills/numen/building_design/SKILL.md) · [启动接线](https://github.com/Dwinovo/minecraft-numen/blob/ce047ffb08541798ae95757877233e1a4039ff8a/numen-api/common/src/main/java/com/dwinovo/numen/api/brain/ServerBrains.java#L64) · [Library](https://github.com/Dwinovo/minecraft-numen/blob/ce047ffb08541798ae95757877233e1a4039ff8a/numen-api/common/src/main/java/com/dwinovo/numen/api/library/Library.java)
2. **索引的可见性与读取正文分开。** `SkillRegistry.skills()` 在库版本变化时遍历 `effective()`，解析文件并缓存。缺失名称或解析失败的条目跳过。模型索引只列有描述、已启用并满足客户端插件提供条件的条目。`SystemPromptComposer` 插入 `formatXml()` 的名称与描述。这里延迟的是**正文进入模型上下文**；Registry 在建索引时已经读取并解析 Markdown。[SkillRegistry](https://github.com/Dwinovo/minecraft-numen/blob/ce047ffb08541798ae95757877233e1a4039ff8a/numen-api/common/src/main/java/com/dwinovo/numen/api/agent/skill/SkillRegistry.java#L101) · [系统提示组装](https://github.com/Dwinovo/minecraft-numen/blob/ce047ffb08541798ae95757877233e1a4039ff8a/numen-api/common/src/main/java/com/dwinovo/numen/api/agent/request/SystemPromptComposer.java)
3. **模型按任务披露正文、附件和页。** 模型调用 `skill({skill:"game_progression"})` 读正文，按正文提示传 `file` 读附件，传 `page` 续读长文。正文和主人通过 `/skill` 指定的内容经同一 `SkillInjection` 成型；外接大脑也使用同一工具。索引不会自动展开引用的其他主题。附件读取有目录边界和大小上限，输出另行分页。[SkillTool](https://github.com/Dwinovo/minecraft-numen/blob/ce047ffb08541798ae95757877233e1a4039ff8a/numen-api/common/src/main/java/com/dwinovo/numen/api/agent/tool/SkillTool.java) · [SkillInjection](https://github.com/Dwinovo/minecraft-numen/blob/ce047ffb08541798ae95757877233e1a4039ff8a/numen-api/common/src/main/java/com/dwinovo/numen/api/agent/skill/SkillInjection.java) · [工具测试](https://github.com/Dwinovo/minecraft-numen/blob/ce047ffb08541798ae95757877233e1a4039ff8a/numen-api/common/src/test/java/com/dwinovo/numen/api/agent/tool/SkillToolTest.java)

| 实际主题 | 正文怎样切知识 | Terraria 可借鉴的写法〔架构推断〕 |
| --- | --- | --- |
| [game_progression](https://github.com/Dwinovo/minecraft-numen/blob/ce047ffb08541798ae95757877233e1a4039ff8a/numen/common/src/main/resources/numen/skills/numen/game_progression/SKILL.md) | 用工具层级、里程碑、材料来源和成就串起主线；开篇指向采矿、制作、下界和末地专题。 | 一篇进程指南解释哪些变化值得关注，把矿物位置、配方数量和 Boss 细节交给各自的查询。 |
| [mining_and_ores](https://github.com/Dwinovo/minecraft-numen/blob/ce047ffb08541798ae95757877233e1a4039ff8a/numen/common/src/main/resources/numen/skills/numen/mining_and_ores/SKILL.md) | 把工具门槛、生成高度、掉落变化和地下风险放在一起，支持“去哪里、带什么、取得什么”的选择。 | 以探索决策组织矿物知识；把深度、镐力、危险、替代来源拆成可以过滤的字段。 |
| [crafting_and_smelting](https://github.com/Dwinovo/minecraft-numen/blob/ce047ffb08541798ae95757877233e1a4039ff8a/numen/common/src/main/resources/numen/skills/numen/crafting_and_smelting/SKILL.md) | 讲制作格、早期合成链、护甲成本、燃料与工作站，同时保留常见配方示例。 | 用正文解释材料组、工作站和批产量，保留少量例子；具体游戏会话按加载的配方计算。 |

### 2.2 KnowledgeSources 回答什么〔源码事实〕

Numen 把模型常问的三个问题做成 `numen.inv.recipes(item)`（如何制作）、`uses(item)`（可投入哪些配方）、`info(item)`（物品事实和说明）。Java 的 `KnowledgeSources` 登记命名空间、种类、隐藏函数、执行端、能提供的内容和缺失字段；插件卸载可用句柄撤回注册。实际查询与合并发生在 Lua 的 `inv.lua`。[注册表](https://github.com/Dwinovo/minecraft-numen/blob/ce047ffb08541798ae95757877233e1a4039ff8a/numen-api/common/src/main/java/com/dwinovo/numen/api/knowledge/KnowledgeSources.java) · [查询与合并](https://github.com/Dwinovo/minecraft-numen/blob/ce047ffb08541798ae95757877233e1a4039ff8a/numen/common/src/main/resources/numen/modules/numen/inv.lua#L237)

| 来源 | 查什么 | 如何解释缺失 |
| --- | --- | --- |
| 原生服务端 | 从运行中游戏的配方表查产物和用途；普通制作类型有详细定义，其他机器类型可提供通用骨架。物品事实来自实际物品定义，如堆叠、耐久、食物、标签。 | 来源声明流体、机器细节及部分加工参数缺失。注册表包含某项也不足以证明身体能执行该加工。 [VanillaKnowledge](https://github.com/Dwinovo/minecraft-numen/blob/ce047ffb08541798ae95757877233e1a4039ff8a/numen/common/src/main/java/com/dwinovo/numen/tools/inventory/VanillaKnowledge.java) |
| JEI 客户端插件 | 在线且有 JEI 的主人客户端补充机器、类别的本地化名称、流体数量和说明页，沿已有客户端通道应答。 | 离线、未提供或超时应可区分；界面画出的概率、热量、能量等不能从这个通道可靠读出。 [NumenJei](https://github.com/Dwinovo/minecraft-numen/blob/ce047ffb08541798ae95757877233e1a4039ff8a/plugins/jei/src/main/java/com/dwinovo/numen/plugins/jei/NumenJei.java) · [设计动机 ADR 0016](https://github.com/Dwinovo/minecraft-numen/blob/ce047ffb08541798ae95757877233e1a4039ff8a/docs/adr/control/0016-recipe-and-item-knowledge-sources.md) |
| 第三方或整合包 | Java 插件或 KubeJS 的 `knowledgeSource` 可声明同一组查询的额外来源。 | 调用方仍只按问题检索；每项能追溯贡献来源和缺项。 [KubeJS 接线](https://github.com/Dwinovo/minecraft-numen/blob/ce047ffb08541798ae95757877233e1a4039ff8a/plugins/kubejs/src/main/java/com/dwinovo/numen/plugins/kubejs/ScriptPlugin.java#L63) |

`gather` 逐个询问来源，保留 `answered/missing`、原因和 `not_provided`。配方按有值的 ID 合并，只补空字段，保留来源列表与省略计数；无 ID 的行分别保留。`info` 取首份可用事实并给说明标来源。这解决过一个真实设计问题：早期查询漏掉机器配方时，模型曾把可加工物品解释为必须开采。**对本项目的启示**是将“查询成功但没有匹配”“覆盖不足”“有配方但不能执行”编码为不同结果。对于有版本分歧的 Wiki 规则，仅按 ID 补字段仍不足以裁决冲突。[合并实现](https://github.com/Dwinovo/minecraft-numen/blob/ce047ffb08541798ae95757877233e1a4039ff8a/numen/common/src/main/resources/numen/modules/numen/inv.lua#L237) · [设计记录](https://github.com/Dwinovo/minecraft-numen/blob/ce047ffb08541798ae95757877233e1a4039ff8a/docs/adr/control/0016-recipe-and-item-knowledge-sources.md)

**Memory 的边界。** Numen 的 `MemoryTool` 管理同伴自己的跨会话札记，平时给一行索引，按需 remember、recall、forget。Skills 提供可复用玩法解释，Knowledge Sources 提供可查询定义。同一玩家说“备用药放左箱”可以成为笔记；“药的配方”应回到知识来源；“箱内今天有几瓶”要重新检查容器。这个区分同样适用于 Terraria。[MemoryTool](https://github.com/Dwinovo/minecraft-numen/blob/ce047ffb08541798ae95757877233e1a4039ff8a/numen-api/common/src/main/java/com/dwinovo/numen/api/agent/tool/MemoryTool.java)

## 3. Terraria Wiki 里应保留什么关系

以下为普通桌面世界的代表性规则与攻略。Wiki 的“建议装备”和规则页的“必须满足”应分别收录。表中最后一列是建模建议。

| 主题 | Wiki 事实或攻略及其关联 | 建议保留的区别 |
| --- | --- | --- |
| 世界阶段、Boss 链 | 肉山击败后世界进入困难模式。常规世界的三个机械 Boss 均击败后，地下丛林才会开始出现花苞；破坏花苞可触发世纪之花，击败后取得神庙钥匙。后期击败拜月教邪教徒会开始四柱事件。 [肉山](https://terraria.wiki.gg/wiki/Wall_of_Flesh) · [世纪之花](https://terraria.wiki.gg/wiki/Plantera) · [月亮事件](https://terraria.wiki.gg/wiki/Events#Lunar_Events) | 世界级里程碑用多项谓词表达。“已解锁生成”“现场已生成”“已观察到”“已取得奖励”是四种状态。 |
| 武器、护甲、饰品 | 职业攻略依阶段给候选，早期也容许混装。陨石套装与太空枪存在实际套装协同；赫尔墨斯靴改善机动，还可与火箭靴在工匠作坊合成幽灵靴。 [职业攻略](https://terraria.wiki.gg/wiki/Guide:Class_setups) · [陨石套装](https://terraria.wiki.gg/wiki/Meteor_armor) · [赫尔墨斯靴](https://terraria.wiki.gg/wiki/Hermes_Boots) | 武器比较要带职业、弹药/法力、套装效果和交战距离。升级关系可有分支；攻略偏好不能变成强制前置。 |
| 制作、工具升级、矿物 | 一把熔岩镐需要 20 个狱石锭，在铁砧或铅砧制作；一个狱石锭由 3 个狱石和 1 个黑曜石在地狱熔炉制成。狱石在地狱生成，有镐力门槛，在地狱开采会释放熔岩。 [熔岩镐](https://terraria.wiki.gg/wiki/Molten_Pickaxe) · [狱石锭](https://terraria.wiki.gg/wiki/Hellstone_Bar) · [狱石](https://terraria.wiki.gg/wiki/Hellstone) | 制作消耗材料；制作站和工具属于使用条件。矿区、局部熔岩与可用站位属于不同粒度。 |
| 草药、掉落与替代获取 | 洞穴探险药水的配方用到闪耀根、月光草，矿料可以是金或铂金；月光草长在丛林草上，夜间开花，采种和只取草药的时机不同。向导巫毒娃娃来源于地狱的巫毒恶魔。 [洞穴探险药水](https://terraria.wiki.gg/wiki/Spelunker_Potion) · [月光草](https://terraria.wiki.gg/wiki/Moonglow) · [巫毒娃娃](https://terraria.wiki.gg/wiki/Guide_Voodoo_Doll) | 分开记录生长基质、掉落主体、产物、时间条件及确定性。缺种子时选择等到开花，缺一株成熟药材时可选另一动作。 |
| 区域、结构、宝箱和奖励 | 地下小屋的主题与所在群系相关，宝箱可能含饰品、药水、武器或钱币。地下和洞穴层的适配宝箱是寻找赫尔墨斯靴的线索。地狱废屋又可能提供地狱熔炉。[地下小屋](https://terraria.wiki.gg/wiki/Underground_Cabin) · [赫尔墨斯靴](https://terraria.wiki.gg/wiki/Hermes_Boots) · [地狱熔炉](https://terraria.wiki.gg/wiki/Hellforge) | “群系 × 深度层 → 结构 → 容器种类 → 候选奖励”支持选区。地图上的箱子没有公开内容；已开且无目标物是该箱的一次观测。 |
| Boss 召唤、战斗和奖励 | 肉山的一条召唤路径要求向导存活，并在地狱熔岩中销毁娃娃；眼睛和嘴共享生命。世纪之花的钥匙能打开神庙，后续才涉及石巨人。 [肉山机制](https://terraria.wiki.gg/wiki/Wall_of_Flesh) · [困难模式流程](https://terraria.wiki.gg/wiki/Guide:Walkthrough/Hardmode) | “取得召唤材料”“满足召唤条件”“允许触发世界变化”“击败”“掉落入包”分别核验。策略正文描述作战取舍，结构记录条件和奖励。 |
| 基地、NPC、仓储与生存 | 住房要满足空间、墙、出入口、光源和家具等条件；合格空房与具体 NPC 入住条件共同影响发展。修复地下小屋有机会复用空间和家具。 [住房](https://terraria.wiki.gg/wiki/House) · [城镇 NPC](https://terraria.wiki.gg/wiki/Guide:Quickly_acquiring_town_NPCs) · [地下小屋](https://terraria.wiki.gg/wiki/Underground_Cabin) | 基地同时是返程点、制作站、库存和服务地点。规则可共享；哪间房合格、哪个商人已到、哪个箱子有材料要由本世界提供。 |

**一个能检验版本策略的反例。** 目前官方地狱熔炉页把“可从黑曜石匣、狱石匣获得”标为桌面 1.4.5.0 新增。面向本项目 1.4.4.9 的计划若推荐钓匣取**熔炉**，即使引用当前 Wiki，也会给出错误的探索目标。应保留适用的地狱废屋来源，并分别建模熔炉和狱石锭这两种物品的获取规则。[地狱熔炉版本历史](https://terraria.wiki.gg/wiki/Hellforge#History) · [项目版本](../../CompanionBridge/EngineProfiles.json)

## 4. 目标架构〔建议〕

### 4.1 内容与权威来源

| 载体 | 适合维护的内容 | 查询或更新时机 |
| --- | --- | --- |
| Markdown Skills | 进程导航；职业与装备取舍；资源采集与探索；制作与备料；基地、仓储与 NPC；Boss 准备、交战与战后处理。每篇给触发问题、选择依据、失败分支、验证方式和相关条目。 | 索引给标题与用途，用户目标涉及的主题才加载。常见示例可给数量并标来源；批量配方留给查询。可从四篇现有内容逐步增补。 |
| 版本化结构记录 | 里程碑条件，装备适用场景与协同，工具门槛，物品获取途径，敌人/箱型候选掉落，群系、深度、结构、时间与世界变体之间的关系。 | Python 宿主按规范 ID、中文/英文别名、主题、版本和条件过滤。首次可用受版本管理的数据文件和内存索引，无需独立数据库服务。 |
| 运行时只读提供者 | 引擎已加载的配方、材料组、产量、站点、当前可制作性；背包/装备、已打开容器、可信世界进度、当前局部对象及危险。 | 可执行性在行动前重查。Mod 或特殊条件标识不支持时可保留知识候选，同时报告执行边界。角色和会话令牌仅随当次执行流转。 |
| 本世界的地点与记忆 | 空间记录保存带世界身份的地点、结构、最近观察及其覆盖；私人笔记保存玩家习惯和个人经验。 | 把“地下可能有靴子”与“这里见过箱子”相交得到调查点。箱子内容和令牌不能从通用知识或私人笔记恢复。 |

**知识层只回答问题。** 持续目标和抢占由 R3-A1 的目标管理方案负责。知识检索可以返回“去已知地狱入口附近重新观察”的候选，计划器再按授权、身体控制、生存条件和预算决定能否前往。查询阶段可在仅问攻略的对话中独立使用。[R3-A1 审计](R3_A1_AGENT_AUTONOMY_AUDIT.md)

### 4.2 数据关系和不确定性

建议将物品、配方、动作、工具能力、工作站、Boss、里程碑、生物群系、深度层、结构、容器和敌人分别设为有类型的实体。对 Mod 保留命名空间，物品与同名 Tile 各有 ID。中文名、英文名和常用简称只做检索入口；多义别名应返回候选。跨会话保存内容键，任务内再解析当前 typeId。

- **制作关系。** 将一份配方作为独立节点，记录产物及每批数量、每个材料组的数量与备选成员、制作站和环境谓词。同一产品的不同配方分别保留。工作站不随批次消耗；已占用的一份材料不能重复抵扣另一个材料组。
- **进程与升级关系。** 区分 `requires`（硬前置）、`unlocks`（解锁）、`recommended_for`（攻略建议）和 `synergy_with`（搭配）。世界阶段用困难模式及各 Boss 标志的集合表达；另附角色装备、可用材料、世界邪恶类型、难度和特殊种子。每个条件可以为真、假或未知。多人带入的高级装备不能证明本世界打过相应 Boss。
- **获得关系。** 一条来源记录包含方法（挖掘、采摘、掉落、开箱、交易或制作）、地点约束、工具和时间条件、危险、数量/概率的适用范围。掉率未知时留空。搜索只产生待调查区域；具体坐标来自当前场景或有出处的历史观测。
- **溯源与冲突。** 每条规则保留来源、原页与小节、修订号或采集日期、适用平台/版本/Mod、核验状态。执行相关字段优先取本次引擎定义和绑定角色读数；资料之间冲突保留两项、指出冲突字段。Wiki 一般规律不会覆盖局部观察，玩家偏好可以改变排序。旧版适配不明的规则可用于解释，不能作为自动行动的唯一前提。

首批只需提供三种只读问题：**找主题或实体、解释阶段/装备依赖、查资源获取途径**。现有 `query_recipes` 继续承担配方查询。一个宿主级知识入口可按意图分派，返回匹配的实体、少量相关关系、未决条件、来源、覆盖和续页；各提供者共用结果形状。这些都是拟议语义，当前工具清单里没有此入口。

### 4.3 发现、检索与上下文预算

1. **发现。** 在现有 `available_skills` 旁提供只读知识入口和少量问题示例。每轮只补当前目标、已知阶段标志及其未知项、相关能力；目录扩大后按版本、授权后端和主题筛选，并保留按名检索全目录的途径。
2. **召回。** 先做名称/别名精确匹配，再按任务和邻接关系查询。约束为假时排除该路线；未知时保留并返回可回答它的观察。优先复用已持物资、已知站点与已调查地点；在成本相近时才用攻略偏好排序。建议第一版返回前三条并标省略数，阈值用离线题集调整。
3. **披露。** 需要权衡或解释时读 Skill 的相关小节；需要数量时取结构化记录和原生配方页；需要坐标时查世界。每条返回可续查的稳定 ID、来源和版本。热路径只保留当前结论、依赖、缺口和未决事实。压缩后凭 ID 重读正文，易变观测另行刷新。
4. **决策。** 内置 Agent 与 MCP 使用同一只读服务。执行时由 Lua 组合既有游戏 API，核验成功条件。记录召回条目、版本、缺失来源、读取字节、模型工具预算和最终动作回执，才能区分“没检索到”“读到了但选错”“规则正确但现场不可行”。

## 5. 熔岩镐案例：从知识到可核验行动〔设计演算〕

假设玩家委托制作一把；背包已确认有 8 个狱石锭、12 个狱石、3 个黑曜石，基地箱子上一次记录有 5 个黑曜石。以下数值是演算输入，现场没有被实际运行或观察。

| 步骤 | 交给 Agent 的知识及其使用 |
| --- | --- |
| 识别目标 | 索引将“熔岩镐 / Molten Pickaxe”解析到物品。若目标是“能开采第一档困难模式矿”，先查现有工具是否已满足；只有玩家明确要求制作，或确有工具缺口，才继续备料。 |
| 展开依赖 | 对目标和中间物品分别解析 `resultType`，分页调用 `query_recipes(availableOnly=false)`。按 Wiki 示例并假设本次原生定义一致，20 锭减 8 锭，缺 12 锭；需要 36 狱石、12 黑曜石。扣背包后缺 **24 狱石、9 黑曜石**。一般公式为批数 = 向上取整（净缺口 / 每批产量），每批乘材料量。递归必须限制深度、节点、备选数，记录已访问物品与配方，遇循环或缺数据返回未决项。 |
| 核实仓库 | 箱内 5 个是旧观察，只能先给条件估算。抵达后检查锁、范围和占用，打开、检查、获准取出并复读背包，随后关闭容器。若实取 5 个，缺口变为 **24 狱石、4 黑曜石**；若箱已空，仍缺 9 个黑曜石。相同物资在箱和包之间只记一次。 |
| 选择探索目标 | 查询狱石来源、工具门槛、液体危险及黑曜石形成条件；联合地点记录选已知地狱入口、地狱废屋或水与熔岩接触区。只有地图粗格时，目标应为“从已验证落点调查此区域”。重新 `observe_scene` 或 `inspect_tile` 证实矿、危险与站位；不从 Wiki 生成矿坐标。若镐力不足，转为查询工具升级分支。 [狱石](https://terraria.wiki.gg/wiki/Hellstone) · [黑曜石与矿块](https://terraria.wiki.gg/wiki/Ore_blocks) |
| 制作与验证 | 挖矿回执证明目标格的效果，拾取后按 `inspect_inventory` 再算缺口。到等效地狱熔炉、砧旁分别重查配方、产量、邻接和空间。核对 `completedCrafts`、`producedCount`、`inventoryVerified` 及目标物品净增。材料只够部分批次时保留实际产量并重算；结果未知时停止变更。 [原生合成](../../CompanionBridge/ServerPlayerRuntime/ActorCrafting.cs#L139) · [现有 Lua 模块](../../python/lua/modules/crafting.lua) |

同一检索过程也能服务不同目标。“提升机动性”会检索饰品的用途、替代品和宝箱来源；“挑战世纪之花”先检查世界标志、地下丛林线索、花苞和撤退条件。即使阶段数据暂缺，知识层也能返回条件化路线和最小调查问题。只有绑定世界的证据足够时，计划器才把其中一条转成行动。

## 6. 与当前项目的接线与缺口〔源码事实 → 建议〕

| 现有机制 | 能直接复用 | R3 还需补齐 |
| --- | --- | --- |
| Gameplay Skills | 当前有采矿、初期住房、NPC 住房、NPC 交互经济四篇。注册器缓存元数据，按注册 ID 读正文，最多 32 篇、单篇 16 KiB。原生 Agent 和外脑均有 `read_skill` 分支。版本和 scope 目前是文本字段。 [注册器](../../python/gameplay_skills.py) · [内置入口](../../python/agent.py#L752) · [外脑入口](../../python/external_brain.py#L188) | 增加进程与装备横向索引、按版本作用域过滤、稳定的小节引用和分页。现有采矿正文已含工具层级及矿层资料，可先从中抽取关系并回链。 [采矿正文](../../skills/mining_and_ores/SKILL.md) |
| API Help 与 Lua | Manifest 来自握手暴露集；`api("help")` 支持函数和部分参数范围的契约查询。内置与外脑的完整/紧凑提示投影有别。Lua 有能力筛选过的 crafting、building 和 npc 模块；`crafting.make` 按目标新增件数找可用配方、分批并核验。 [契约构造](../../python/server_ai_contract.py#L334) · [模块依赖](../../python/lua_modules.py) | API Help 继续描述参数和回执，游戏知识解释为何选它。知识层可返回可用查询名；递归备料、选择矿区、重查仓储由上层协调。 |
| 配方与仓库 | `ReadRecipes` 读 `Main.recipe`，返回产量、材料组、站点、支持/可用性、空间及分页。原生执行对特殊回调、Mod 配方等有明确限制；开箱或使用外部制作存储也受到限制。容器读取和取出有原生距离、身份及新鲜令牌约束。 [配方实现](../../CompanionBridge/ServerPlayerRuntime/ActorCrafting.cs#L88) · [容器实现](../../CompanionBridge/ServerPlayerRuntime/ActorContainer.cs#L114) | 建材料需求反查，单列“已持有”“近期看见可取”“尚未调查”。大批任务执行前确定同组材料分配并重验。长期索引禁存会话令牌。 |
| 场景、语义查询、阶段 | 场景给局部对象、危险和候选落点；区域可截断。`sense_world` 可把扫描、类型解析和核查组合起来。`discover_bosses` 检查附近 640 像素的活跃候选。客户端 schema 虽有 `get_world_state`，当前专服查询白名单没有相同入口。 [语义查询](../../python/lua/semantic.lua) · [场景](../../CompanionBridge/ServerIpc/ServerSceneReader.cs) · [Boss 查询](../../CompanionBridge/ServerIpc/BossMembers.cs#L39) · [专服查询白名单](../../CompanionBridge/ServerIpc/ServerAiQueries.cs#L44) | 增加绑定身份的只读阶段/群系/深度投影并声明覆盖；通过现有 schema、握手、分发和 Help 暴露。附近没 Boss 无法回答“本世界是否曾击败它”。 |
| 地图与 Memory | `query_map` 返回带 worldUid/characterId 的历史粗地形及已知比例；不记录逐格矿种和宝箱清单。笔记有世界/角色范围、索引和正文，可保存经验与偏好。 [地图编码](../../CompanionBridge/ServerPlayerRuntime/AiMap.cs#L42) · [私人笔记](../../python/agent_memory.py#L28) | 复用 A1 建议的地点观测记录，保存发现类别、范围、时间和复查结果。通用 Wiki 关系在知识包只维护一次；地图和笔记通过内容键引用它。 |

**扩展与维护。** 内置数据包、Mod 包和玩家自定义内容分别带命名空间、清单、来源及适用版本。导入时校验实体引用、材料组、数量、别名歧义和规则冲突；构建时生成查询索引。Wiki 摘录用自己的表述保留原页与修订信息，审核版本差异后发布。Mod 数据可补定义和来源，执行支持仍以握手为准。玩家可增添攻略、禁用主题或调整流派/开采偏好；例如“尽量保留矿区地形”会影响排序。引擎更新或启用包变化时按指纹失效索引，保留旧版本以重现历史决策。

## 7. 实施顺序与阶段验收〔待验证〕

| 顺序和依赖 | 第一份可交付的能力 | 通过条件 |
| --- | --- | --- |
| **P0 · 基线与知识样本** | 用现行四篇 Skills 和原生配方跑固定题集，覆盖制作、升级、寻宝、缺阶段观测和错误版本。固定引擎、库存、世界条件及模型。 | 同场记录检索命中、知识读取字节/模型工具次数、数量正确性、无依据的世界断言；“只问攻略”的样本产生零游戏变更。 |
| **P1 · 一个可反查的升级链**，依赖 P0 | 建最小内容键、别名、来源、条件及只读检索；用熔岩镐贯穿技能索引、两层配方和采集线索。复用现有合成模块。 | 对第 5 节两种箱况给出精确缺口；原生配方与 Wiki 不同时采用本次注册表；材料组不得重复抵扣，深度或页数耗尽要显式报告。1.4.4.9 查询不应返回 1.4.5 新增的熔炉匣来源。 |
| **P2 · 世界条件与探索决策**，依赖 P1，地点接口与 A1 协调 | 接最小进度/区域读取、地图与容器线索，支持草药、宝箱及一个带分支条件的 Boss 目标。 | 同一询问分别在机械 Boss 击败 2/3、3/3、未知时运行，输出不同的下一步；3/3 只证明具备生成条件。箱子被取空后撤回库存估算，未定位花苞或箱子时不得给伪坐标。 |
| **P3 · 扩展及真实 Agent 比较**，依赖 P1–P2 | 加入职业配装、基地/NPC 和自定义知识包。先用固定 Lua 隔离游戏执行，再把“仅现有 Skill”同“Skill 加结构检索”交给同一真实模型比较，多次重复。 | 逐组报告目标物入包、错误推荐、无依据动作、查阅开销、首次有效动作时间与停止原因。禁用插件、来源离线或更换版本后仍能给部分答案并标缺项；玩家偏好不改变配方数量。 |

**本次验证。** 对照远端 Numen HEAD、上述源码、官方 Wiki 规则和项目能力投影；在项目根以 `$env:PYTHONPATH='python;tests'` 运行 `.local/lua-venv/Scripts/python.exe -B -m unittest -v test_gameplay_skills`，3 项通过。实测现有注册器的四篇描述索引为 **530 UTF-8 字节**，四篇加载后的正文合计 **14,230 字节**；这两个数只衡量技能文本，不代表整个模型请求或 token 用量。本次交付为研究文档，未运行全量测试、真实模型或游戏服。实施阶段首先要验证的是：给定相同工具，Agent 能否用更少无关上下文选择正确来源、算对缺口，并用游戏证据确认实际收获。
