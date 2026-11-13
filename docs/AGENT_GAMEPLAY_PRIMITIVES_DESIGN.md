# Agent Gameplay Primitives 设计

**状态：** 架构设计，待实现与原生验证  
**目标后端：** Dedicated Server 上的独立 Server AI Player  
**代码基线：** \`codex/unified-autonomous-navigation\`，\`466c1fe46816302a5c19fd534a81298b68793396\`

## 1. 目标与范围

为 Server AI 补齐玩家日常操作中已经由 Terraria 原生实现的基础能力，重点是背包与世界容器交互。

设计目标：

- 优先复用 Terraria 原生物品转移、堆叠、排序和交互规则。
- 所有操作只作用于当前绑定的 AI Player，以及它在游戏内实际打开、可触及的容器。
- 保留现有 Server AI 的 World、Actor Generation、游戏线程和新鲜 Token 约束。
- 每项修改操作都有操作前检查、操作后效果证据和明确的 unknown 语义。
- 不因适配原生 UI 操作而引入对真人玩家全局状态的副作用。

本设计定义底层 gameplay primitives，不定义基地经营、自动整理策略、长期目标或 Skill。

## 2. 当前实现基线

已有能力：

- 普通背包槽 0..49 的指定数量搬运、交换和持用，见 [INVENTORY_OPERATIONS.md](INVENTORY_OPERATIONS.md)。
- 普通原生掉落物拾取路径及 Dedicated Server 的 AI 物品所有权适配，见 NativeSurvival 和 NativeItemOwnership。
- 场景观察已能识别部分世界容器；掉落物可通过 scan_entities 观察。
- 共享 Atomic Operations 中已有 interact_tile 的门、箱子、开关和拉杆处理，但当前 Server AI 尚未完整暴露这条路径。
- place_object 当前仅支持经过验证的原版 Work Bench，不是通用多格家具放置。
- 制作系统当前不直接读取箱子、银行或虚空保险库中的材料。

因此，本阶段不是重写背包或拾取系统，而是补齐容器观察、交互和批量操作，并把现有能力正确接入 Server AI。

## 3. 能力分层

### 3.1 玩家背包

继续沿用现有库存操作边界：

- 观察普通库存和受支持的装备槽。
- 在普通库存槽之间精确搬运、交换和持用。
- 新增背包排序时，使用原生排序逻辑，并使排序前签发的槽位 Token 全部失效。
- 排序后刷新快捷栏、装备选择和其他依赖槽位编号的缓存；无法唯一重新定位的缓存应清空，不得猜测。

### 3.2 世界容器

第一阶段只支持已验证的原版箱子类型（Containers / Containers2）。其他容器类型，例如 Dresser，单独验证后再开放。

能力分为：

1. 发现容器：复用 observe_scene 的对象观察；观察结果只用于定位，不构成操作许可。
2. 打开或关闭：复用原生 TileInteractionsCheck 路径，优先接入现有 interact_tile。
3. 查看内容：新增只读容器观察，返回容器身份、槽位内容、范围和快照信息。
4. 精确存取：按指定物品、数量和目标槽执行存入或取出。
5. 原生批量操作：快速堆叠、全部取出、全部存入、补充物品和整理当前箱子。

建议的 API 方向如下，最终名称与参数以现有 Schema 和能力投影为准：

| 意图 | 建议接口方向 | 约束 |
| --- | --- | --- |
| 查看当前箱子 | inspect_container | 必须是 AI 当前实际打开的容器 |
| 存入指定物品 | deposit_container_item | 新鲜背包槽与容器槽 Token，指定数量 |
| 取出指定物品 | withdraw_container_item | 新鲜容器槽与背包槽 Token，指定数量 |
| 快速堆叠到附近箱子 | quick_stack_nearby | 采用 Terraria 原生附近箱子范围和过滤规则 |
| 快速堆叠到当前箱子 | quick_stack_container | 要求当前容器仍打开且身份匹配 |
| 全部取出 | loot_all | 当前容器；按原生容量和过滤规则处理 |
| 全部存入 | deposit_all | 当前容器；按原生过滤规则处理 |
| 补充背包物品 | restock | 当前容器；按原生补充规则处理 |
| 整理背包 | sort_inventory | 玩家背包排序，不涉及容器 |
| 整理当前箱子 | sort_container | 当前容器排序，不涉及背包 |

批量操作应表达一个原生游戏意图，不应由 Agent 模拟大量鼠标点击来拼接。

### 3.3 世界掉落物

继续复用现有 Player.GrabItems 和 NativeItemOwnership，不另写一套直接向背包加物品的拾取实现。

后续补齐：

- 为掉落物观察增加可失效的实例身份，不能仅依赖可复用的 Main.item 数组槽位。
- Agent 根据位置接近目标，让原生拾取流程完成吸引和拾取。
- 通过背包变化及掉落物实例变化核验结果。
- 掉落物合并、消失、被其他玩家拾取或槽位复用时，旧目标身份失效并要求重新观察。

### 3.4 多格物体放置

通用箱子放置不属于普通 place_tile，也不能通过简单放开当前 Work Bench 白名单完成。

箱子放置必须同时满足：

- TileObjectData 定义的完整占地、样式和支撑规则。
- Terraria 原生物品放置及 Mod Hook 规则。
- 正确创建或注册 Chest 存储记录。
- 正确处理物品消耗、网络同步和放置结果核验。

建议在容器读写稳定后单独扩展 place_object；第一阶段不把通用家具放置混入箱子操作实现。

## 4. 原生实现策略

审计发现的原生入口可作为适配候选：

| 玩家操作 | 原生入口候选 |
| --- | --- |
| 附近箱子快速堆叠 | Player.QuickStackAllChests |
| 当前箱子快速堆叠 | ChestUI.QuickStack |
| 全部取出 | ChestUI.LootAll |
| 全部存入 | ChestUI.DepositAll |
| 补充物品 | ChestUI.Restock |
| 整理背包 | ItemSorting.SortInventory |
| 整理箱子 | ItemSorting.SortChest |
| 指定物品放入箱子 | ChestUI.TryPlacingInChest 或经验证的原生槽位路径 |

这些是目标游戏版本中的待验证入口，不代表它们都能直接在 Dedicated Server 上调用。实现前必须确认当前 tModLoader / Terraria 版本的签名、调用前置条件、执行端和网络副作用。

选择原则：

1. 优先调用服务端可用的原生批量方法。
2. 如果某个 UI helper 只适用于客户端，不能在服务器上盲目调用；应继续寻找服务端权威的原生路径。
3. 精确数量转移应复用 Terraria 的堆叠、拆分、CanStack 和 Mod Hook 规则，不直接改写 Item.stack 来绕过规则。
4. 不为了一键功能而循环模拟大量鼠标点击。
5. 只有原生路径确实依赖鼠标格子语义时，才考虑隔离调用 ItemSlot 操作。

### 4.1 全局上下文隔离

TerraPlayerBot 的实现显示，部分原生批量方法可能读取 Main.myPlayer、当前容器或鼠标上下文。不能因此默认把这些全局字段改成 AI 状态。

- 默认不改变 Main.myPlayer、Main.mouseItem 或真人玩家的 Main.player 状态。
- 若经实际反编译和 Dedicated Server 测试确认某个原生入口必须依赖全局上下文，才允许设计独立、短生命周期的 scoped adapter。
- 该作用域必须在游戏主线程执行，保存并在 finally 中恢复所有改动的全局状态；验证恢复结果；拒绝重入，并确保操作期间不会与其他玩家操作交错。
- 不应直接削弱 NativeSurvival 当前对 Main.myPlayer、Main.mouseItem、Main.player 和玩家文件上下文的隔离检查。需要例外时，应单独设计和评审边界。
- 任何上下文污染、恢复失败或 Actor 身份变化都应停止后续操作并进入隔离/unknown 处理。

## 5. 容器身份与 Token

容器不能只用 Chest 数组索引标识，因为索引可能变化或被复用。

容器观察 Token 至少绑定：

- World Session。
- Actor Generation 与 AI Player 身份。
- 容器锚点坐标和容器类型。
- 当前 Chest 记录索引及可验证的记录/实例身份。
- 当前打开状态及对应的 Player chest、chestX、chestY 上下文。
- 本次观察的版本或快照标识。

容器槽位 Token 额外绑定：

- 容器 Token。
- 容器槽位索引。
- 物品实例身份及现有库存 Token 使用的基础字段快照。

操作前重新验证容器仍存在、未被替换、未锁定、仍在原生交互范围内，且 AI Player 当前打开的是同一个容器。

现有 core-only Token 无法证明任意 ModItem 的全部自定义数据相等。继续保留这一限制，并在实际转移中调用原生兼容性和物品转移钩子；不能把基础字段相同描述为完整物品相等。

## 6. 操作与同步契约

所有修改操作必须：

1. 在游戏线程中执行，并绑定当前 World、Actor Generation 和 AI Player。
2. 在执行前重新检查目标、范围、容器状态和新鲜 Token。
3. 记录操作前的背包与容器相关槽位快照。
4. 调用经当前游戏版本验证的原生操作路径。
5. 通过服务端权威状态确认背包与容器的实际变化。
6. 使用正确的原生网络同步路径，使其他客户端看到一致结果。
7. 确认操作结束、输入已释放，且没有污染其他玩家或全局鼠标状态。

不要仅凭原生函数正常返回、输入释放或客户端 UI 更新就宣告完成。

### 6.1 结果语义

- 执行前拒绝：明确说明原因，确认尚未开始物品变更。
- completed：操作声明范围内的预期效果已由服务端状态确认。
- 部分效果：返回实际转移数量、剩余数量、受阻槽位和原因；不能把部分转移伪装成全部成功。
- failed：有明确证据表明目标未达成；同时说明是否确认没有副作用。
- unknown：发生异常、取消、超时、身份变化或同步不确定，无法可靠判断最终效果。

取消、超时和异常可能发生在部分物品已经移动之后。不得假定回滚，不得自动重试或自动反向转移。先重新观察，再由 Agent 决定下一步。

### 6.2 排序后的失效处理

背包或箱子排序会改变槽位布局：

- 排序前签发的相关槽位 Token 全部失效。
- 排序后重新读取槽位。
- 依赖槽位编号的装备/快捷栏缓存必须重新定位或清空。
- 排序结果应核验物品集合和数量没有非预期增减；对无法完整比较的 ModItem 状态明确报告验证边界。

## 7. 权限和范围

- 只允许操作绑定 AI Player 当前可触及的原版容器。
- 不允许仅凭世界坐标远程打开或读写任意箱子。
- 不允许通过本能力读取或修改其他玩家的个人银行、虚空保险库或个人存储。
- 第一阶段不支持锁定箱子、未加载容器、未知容器类型和未验证的 Mod 容器。
- 不改变其他玩家的打开箱子状态，不抢占或重写其他玩家的 UI 上下文。
- 同一个 AI Player 的容器动作串行执行；每个动作都必须重新验证容器身份和打开状态。

## 8. 实施顺序

### P0：打开和观察容器

- 将现有 interact_tile 的箱子路径正确接入 Server AI 能力投影和执行端。
- 增加当前打开容器的只读观察及容器/槽位 Token。
- 验证箱子打开、关闭、锁定、被破坏、被替换、超距和记录缺失等情况。
- 完成 Dedicated Server 与多客户端同步验证。

### P1：精确存取与原生批量操作

- 实现指定数量的存入和取出。
- 验证快速堆叠、Loot All、Deposit All 和 Restock 的服务端原生路径。
- 对每种批量操作分别验证满背包、满箱、收藏物品、过滤规则、ModItem 堆叠钩子和部分效果。
- 所有操作均提供实际数量和槽位变化证据。

### P2：排序与缓存刷新

- 实现背包和当前箱子的原生排序。
- 排序后使旧 Token 失效，并刷新或清空槽位缓存。
- 验证排序不会造成物品丢失、复制或错误装备选择。

### P3：掉落物任务与通用箱子放置

- 为掉落物建立实例级观察身份，复用原生拾取并核验进入背包的结果。
- 独立扩展经过验证的箱子多格放置和 Chest 记录注册。
- 不把这两项作为容器读写功能的前置条件。

## 9. 验收标准

每个操作至少完成以下测试：

- 单人世界和 Dedicated Server。
- 至少两个客户端同时在线；真人玩家与 AI 同时打开不同箱子，以及同时打开同一箱子的情况。
- 空箱、满箱、空背包、满背包、部分可转移和多个同类堆叠。
- 收藏物品、快捷栏物品及各批量操作自己的原生过滤规则。
- 箱子锁定、超距、关闭、被破坏、被替换和 Chest 记录缺失。
- 原版物品与具有自定义堆叠/转移钩子的 ModItem。
- 操作取消、超时、异常和连接中断后的部分副作用。
- 背包和箱子在所有客户端上的最终状态一致。

核心不变量：

1. 不复制物品。
2. 不丢失物品。
3. 不改变非目标玩家的库存或容器状态。
4. 不因旧 Token 或复用的 Chest/Item 数组索引操作错误对象。
5. 结果不确定时停止，不自动重放。

## 10. 参考与现有文档

- [背包操作](INVENTORY_OPERATIONS.md)
- [原子操作契约](ATOMIC_OPERATIONS.md)
- [Server AI 动作接口](SERVER_AI_ACTIONS.md)
- [Server AI 能力契约](SERVER_AI_CAPABILITIES.md)
- [Server AI 多格物体放置](SERVER_AI_OBJECT_PLACEMENT.md)
- Numen 容器交互参考：Dwinovo/minecraft-numen，审计基线 commit 143ba409a62c99bf1e985b6047bcad0f00e9313e。
- TerraPlayerBot 参考：资料库中的 terrabot.zip，重点检查 BotCompanionActions、BotInventoryInteraction、BotCompatibility、BotWorldActions 和 BotCompanionRegression。
