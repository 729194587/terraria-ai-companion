# Agent Gameplay Primitives 设计

**状态：** P0–P3 已实现并完成 Dedicated Server 定向验证；P4 设计定稿，待实现  
**目标后端：** Dedicated Server 上的独立 Server AI Player  
**代码基线：** `codex/unified-autonomous-navigation`，P3 提交 `12e1f1b1960e520bb96945fe00f0b71b030d5f88`

## 1. 目标与范围

为 Server AI 补齐玩家日常操作中的原生基础能力。P0–P3 已覆盖背包与容器；P4 聚焦明确的掉落物收集任务与通用放置。

设计原则：

- 优先复用 Terraria 原生交互、物品兼容性、堆叠、转移和排序规则。
- 操作对象始终是当前绑定的 AI Player，以及它实际打开且有权访问的容器。
- 保留 World Session、Actor Generation、游戏线程、Actor 身份和新鲜 Token 约束。
- 修改操作必须能核验服务端权威状态；部分效果和 unknown 必须如实表达。
- 不通过模拟真人鼠标输入或污染真人玩家全局状态来绕过 Dedicated Server 适配问题。

本设计定义底层 gameplay primitives，不定义基地经营策略、自动整理策略、长期目标或 Skill。

## 2. 当前实现基线

已确认的代码现状：

- `ActorInventory` 已支持 AI 普通背包槽 0..49 的指定数量搬运、交换和持用，并使用新鲜 Token、原生堆叠钩子及下一游戏更新确认。详见 [INVENTORY_OPERATIONS.md](INVENTORY_OPERATIONS.md)。
- Server AI 动作在游戏主线程的 Server AI Tick 中执行；Actor 操作由当前 World、Generation 和 Player 身份约束。
- `NativeSurvival.Run` 对 AI 生存更新建立隔离边界，并检查 `Main.myPlayer`、`Main.mouseItem`、`Main.ActivePlayerFileData` 和 `Main.player` 等全局状态未被改变。
- 共享客户端 `AtomicOperations.Interact` 包含门、箱子、开关和拉杆逻辑，但它使用 `Main.LocalPlayer`，且 Dedicated Server 分支明确返回 `unsupported_interaction_net_mode`。它不是 Server AI 可直接调用的服务端容器实现。
- `ServerAiNativeActions` 当前没有 `interact_tile` 执行分支；Python Server AI 契约将其列为未开放。
- 容器 P0–P3 已完成打开/观察/关闭、精确存取、五项批量操作和原生排序，并有 Dedicated Server 双客户端协议实测。
- `place_tile` 已支持一类普通原生方块，`place_object` 对外仍仅支持 Work Bench；底层 `ActorObjectPlacement.CanPlace` 已能读取 Item.createTile、placeStyle 和 TileObjectData，但箱子注册、墙壁和特殊放置尚未通用化。
- 现有制作系统不直接从箱子、银行或虚空保险库读取材料。

P4 不重写已经正常工作的容器、物品使用或原生自动拾取；在既有的 Server AI 行为和任务系统上补齐 Agent 可直接调用的意图。

## 3. 能力范围

### 3.1 玩家背包

继续沿用现有库存操作边界：

- 观察普通库存及已支持的装备槽。
- 在普通库存槽之间精确搬运、交换和持用。
- 新增排序时，排序前签发的相关槽位 Token 全部失效。
- 排序后重新读取库存；依赖槽位编号的缓存必须重新定位或清空，不得猜测。
- 排序操作必须验证物品数量与可核验物品集合没有非预期变化。

### 3.2 世界容器

第一阶段只支持已验证的原版箱子类型（Containers / Containers2）。Dresser、猪猪存钱罐、保险箱、护卫熔炉、虚空保险库及 Mod 容器均不自动纳入支持范围，需分别确认存储语义和权限后再开放。

能力分为：

1. 发现容器：复用场景观察定位容器；观察结果只用于定位，不构成操作许可。
2. 打开或关闭：由 AI Player 在服务端执行原生交互语义，并核验打开状态变化。
3. 查看内容：读取 AI 当前实际打开的容器，返回容器身份、槽位内容和快照信息。
4. 精确存取：按指定物品、数量和槽位执行存入或取出。
5. 批量操作：附近箱子快速堆叠、当前箱子快速堆叠、全部取出、全部存入、补充物品。
6. 排序：整理 AI 背包或当前箱子。

建议 API 方向如下，最终名称与参数以现有 Schema 和能力投影为准：

| 意图 | 建议接口方向 | 约束 |
| --- | --- | --- |
| 查看当前箱子 | `inspect_container` | 必须是 AI 当前实际打开的容器 |
| 存入指定物品 | `deposit_container_item` | 新鲜背包槽与容器槽 Token，指定数量 |
| 取出指定物品 | `withdraw_container_item` | 新鲜容器槽与背包槽 Token，指定数量 |
| 附近箱子快速堆叠 | `quick_stack_nearby` | 使用原生附近箱子范围和过滤规则 |
| 当前箱子快速堆叠 | `quick_stack_container` | 要求当前容器仍打开且身份匹配 |
| 全部取出 | `loot_all` | 当前容器，遵循原生容量与过滤规则 |
| 全部存入 | `deposit_all` | 当前容器，遵循原生过滤规则 |
| 补充物品 | `restock` | 当前容器，遵循原生补充规则 |
| 整理背包 | `sort_inventory` | 只整理 AI 背包 |
| 整理箱子 | `sort_container` | 只整理当前容器 |

批量操作表达一个原生游戏意图，不应由 Agent 模拟大量鼠标点击来拼接。

### 3.3 P4-A：主动收集掉落物 `collect_drops`

**现有事实：** `scan_entities` 可读取活动掉落物（类型、数量、位置等）；`NativeSurvival` 已接入 `Player.GrabItems`、物品归属及网络同步。此前挖铁矿真实测试已经捡到铁矿石 ×2。剩余一块铁矿因导航超时没有拾取，不能据此判定拾取机制缺失。

P4-A 提供**明确的收集任务**，把「观察 → 决定拾取站位 → 导航 → 原生拾取 → 再观察并确认」封装成 Agent 可直接使用的目标。不是新建自定义拾取物品、直接增加背包或重做导航器。

建议 API：任务 `collect_drops`，允许不带物品种类的调用；可选参数为扫描范围 `radiusPixels`、过滤的 `typeIds`，必要时允许从新鲜观察中指定单个目标。最终通过现有 `submit_task` / `query_task` / `await_task` / `cancel_task` 和能力握手暴露，参数按现有 Task Schema 风格实现。

行为契约：

1. 从当前玩家附近的**真实活动掉落物**中选择目标；默认收集所有当前可正常拾取的物品，不要求预先知道掉落种类。每次任务有明确的扫描范围和终止条件，不无限追逐新生成的物品。
2. 路线目标是**满足 Terraria 原生拾取条件的可达玩家站位或区域**，不强迫站到掉落物坐标，也不要求物品恰好与玩家中心重合。拾取范围、物品归属、背包容量、ModItem 拾取许可均以 Terraria 原生逻辑为准。
3. 使用现有 Server AI 导航任务推进，角色接近后让原生 `Player.GrabItems` 拾取；不调用直接写入背包或绕过引擎拾取规则的方法。
4. 观察目标时使用 World/Actor 身份和足以识别当前掉落物实例的快照；不能仅以可复用的 `Main.item` 槽位号当作永久身份。物品移动、合并、消失或槽位复用后重新观察，不按过时坐标继续追逐。
5. 任务记录已核实的收集效果、物品及数量、仍存在但未取得的目标及原因。硬币可按原生面额/价值核验；生命、魔力等非背包型拾取按其真实原生效果核验，不虚报为背包新增。
6. 超时、导航失败或目标不可达时报告已完成部分和未完成部分；不将「未能接近」误报为「无法拾取」。遇到一个不可达目标不应陷入无穷重试，也不必强迫任务丢弃此前已经获得的物品。
7. 与现有任务取消、输入释放、Actor 失效和 unknown 结果语义保持一致；中断后不自动重放可能已有副作用的步骤。

### 3.4 P4-B：通用物品放置 `place_item`

目标是**只要某件物品在当前 Terraria/tModLoader 原生规则下属于可放置物品，Agent 原则上也能使用它进行同类放置**。不对家具、箱子等物品维护逐项白名单，也不把药水、武器、装备等非放置用途强行归入此 API。

建议统一接口：`place_item({x, y})`。`x/y` 为玩家瞄准的世界 Tile 坐标；当前选中物品仍沿用现有热键栏与物品持用语义。游戏侧根据物品的原生属性、放置数据和实际调用链，决定是普通 Tile、平台、背景墙、多格物体、箱子、火把、绳索还是其他原生放置分支。多格物体的目标锚点和实际占地应在回执中明确，不能把旧 `place_object` 的工作台锚点语义无条件假定适用于所有对象。

优先扩展现有链路：

- `ActorItemUse`：原生 `Player.ItemCheck` 单次使用周期、物品及动画生命周期；
- `ActorPlacement`：`Player.PlaceThing_Tiles` 和受 Actor 绑定的坐标作用域；
- `ActorObjectPlacement`：`TileObjectData` 的占地、方向/样式、支撑与事件核验；
- 壁面放置等目前未接入的原生调用链：沿既有模式加入服务端适配，而不是绕开原生玩家放置过程。

支持原则：

1. **原生行为优先、类型驱动**：由 `Item.createTile`、`createWall`、`TileObjectData`、原生使用逻辑以及 tModLoader Hook 判定放置能力。普通方块、平台、墙壁、火把、工作站、家具、门、箱子及遵循常规机制的 Mod 物品均属于目标覆盖范围。
2. **不以硬编码白名单代替原生规则**：少数确实使用独立原生机制的物品只对其具体机制适配；未支持的调用链应给出准确原因，而不是把整个物品类别永久判为不可放置。
3. **原生世界效果**：保留真实交互范围、方向/样式、支撑、碰撞、替换、钩子与消耗规则。实际执行以 Actor 为所有者，不直接伪造世界状态或库存扣料。
4. **箱子是一项重点验收样例**：必须创建正确的 Chest 存储记录，关联 Tile 锚点，后续 `open_container` 和 `inspect_container` 能正常操作；不仅检查 Tile 图片存在。
5. **效果验证**：核验放置后的 Tile/墙壁/多格占地、必要的附属世界记录和库存消耗，同时同步到其他客户端。原生放置失败只报告失败；已可能产生世界效果但无法核实的情况返回 unknown。
6. **向后兼容**：现有 `place_tile`、`place_object` 和 `place_rope` 可以继续服务既有调用者，但 `place_item` 是面向 Agent 的统一放置入口。不要求 Agent 自己判断应该调用哪一类放置 API。
7. **边界明确但不人为缩小世界**：不要求一次实现所有不可归类的特殊道具、液体倒出或生成实体的行为；这些若由引擎定义为其他「使用」语义，属于后续物品使用扩展，而不是 `place_item` 的失败。

### 3.5 P4 任务组织

P4-A（收集）和 P4-B（放置）各自独立验收，不互相作为前置依赖。实现时先复用已有机制和能力契约，避免为两项任务重建独立的世界观察、移动或输入系统。

Numen 的 `task/move/ItemReach`、`Destination` 可借鉴「原生拾取可达区域」的**目标建模**；`task/build/BuildPlacer` 与 `tools/interact/UseApi` 可借鉴「优先走游戏原生使用/放置通路」的原则。Minecraft 的拾取距离、碰撞几何和方块放置协议不能搬到 Terraria。

TerraPlayerBot 的 `BotPickupPolicy` / `BotPickupRegression` 可参考物品归属、拾取许可与同步测试；`BotWorldActions` 可参考箱子注册的观察结果，但其中直接调用 `WorldGen.PlaceChest` 和手动消耗物品的方案不是本项目通用放置的默认实现。

## 4. 原生实现策略

以下方法是需要针对项目实际 tModLoader/Terraria 程序集核对的候选入口，不代表已确认能在 Dedicated Server 直接调用：

| 玩家操作 | 原生入口候选 |
| --- | --- |
| 附近箱子快速堆叠 | `Player.QuickStackAllChests` |
| 当前箱子快速堆叠 | `ChestUI.QuickStack` |
| 全部取出 | `ChestUI.LootAll` |
| 全部存入 | `ChestUI.DepositAll` |
| 补充物品 | `ChestUI.Restock` |
| 整理背包 | `ItemSorting.SortInventory` |
| 整理箱子 | `ItemSorting.SortChest` |
| 指定物品放入箱子 | `ChestUI.TryPlacingInChest` 或经验证的原生槽位转移路径 |
| 打开/关闭箱子 | AI Player 的原生交互路径；不能复用依赖 `Main.LocalPlayer` 的客户端封装 |

实现每个入口前，先以项目当前引用的程序集确认方法签名、可见性、前置状态、读取的全局字段、执行端限制和网络副作用。若入口只适用于客户端 UI，不得在 Dedicated Server 直接调用。

选择原则：

1. 优先使用服务端可调用、由服务端权威执行的原生逻辑。
2. 精确数量转移必须遵循 Terraria 的拆分、堆叠、CanStack、Transfer 和 Mod Hook 规则；不以直接改写 `Item.stack` 绕过原生规则。
3. 不为了一键功能而循环模拟大量鼠标点击。
4. 若原生入口依赖 UI 状态，先寻找服务端原生等价路径；只有不存在合适路径且必要性明确时，才设计专用适配。
5. 对每种批量操作独立验证服务端可用性，不能从某一个原生方法可用推断其他方法也可用。

### 4.1 Dedicated Server 交互边界与全局上下文

当前客户端 `AtomicOperations.Interact` 不能直接作为服务端实现：它以 `Main.LocalPlayer` 为操作对象，并明确拒绝 Dedicated Server 模式。服务端实现必须以已绑定的 AI Player 为对象，重新实现服务端执行与结果核验边界。

TerraPlayerBot 的上下文切换方式可作为研究参考，但不构成我们必须采用的实现方案。尤其不能未经验证就把 `Main.myPlayer` 临时切换为 AI 槽位。

- 默认不修改 `Main.myPlayer`、`Main.mouseItem`、`Main.ActivePlayerFileData` 或 `Main.player`。
- 先检查目标原生方法是否可直接对 AI Player / Chest 实例执行。
- 若某个必要原生方法确实依赖全局上下文，先提交该方法的调用链、字段读写清单和隔离方案，再实现 scoped adapter。
- 任何 scoped adapter 必须只在游戏主线程运行，限定当前 Actor 和 World/Generation，保存并在 `finally` 恢复全部被修改状态，验证恢复结果并拒绝重入。
- 不得削弱 `NativeSurvival.Run` 对全局玩家、鼠标和玩家文件上下文的隔离检查。若必须调整检查边界，应作为独立架构变更说明理由和测试。
- 上下文污染、恢复失败或 Actor 身份变化时停止后续操作，按 unknown/隔离处理。

## 5. 容器身份与 Token

不能只用 Chest 数组索引标识容器，因为索引可能变化或被复用。

容器 Token 至少绑定：

- World Session。
- Actor Generation 与 AI Player 身份。
- 容器锚点坐标和容器类型。
- 当前 Chest 记录索引，以及可验证的记录身份或对应关系。
- AI Player 当前打开状态及 `chest`、`chestX`、`chestY` 上下文。
- 本次观察的快照版本或观察时间。

容器槽位 Token 额外绑定：

- 容器 Token。
- 容器槽位索引。
- 物品实例身份及现有库存 Token 使用的基础字段快照。

每次修改前重新验证容器仍存在、未被替换、未锁定、处于原生交互范围内，且 AI Player 当前打开的是同一个容器。若打开状态丢失或 Chest 记录变化，旧 Token 立即失效。

现有 core-only Token 不能证明任意 ModItem 的全部自定义数据相等。继续保留此验证边界，并在实际转移时调用原生兼容性和物品转移钩子；不得把基础字段相同描述为完整物品相等。

## 6. 操作、同步与结果契约

所有修改操作必须：

1. 在游戏主线程执行，并绑定当前 World、Actor Generation 和 AI Player。
2. 执行前重新检查目标、范围、容器状态和新鲜 Token。
3. 记录操作前相关背包与容器槽位快照。
4. 调用经当前游戏版本验证的原生操作路径。
5. 通过服务端权威状态确认背包与容器的实际变化。
6. 使用正确的服务端网络同步路径，使其他客户端看到一致结果。
7. 确认操作结束，没有残留输入，也没有污染其他玩家或全局鼠标状态。

不要仅凭原生函数正常返回、输入释放或客户端 UI 更新就宣告完成。

### 6.1 结果语义

- **执行前拒绝：** 明确说明原因，并确认尚未开始物品变更。
- **completed：** 操作声明范围内的预期效果已由服务端状态确认。
- **部分效果：** 返回实际转移数量、剩余数量、受阻槽位和原因；不得伪装成全部成功。
- **failed：** 有明确证据表明目标未达成，同时说明是否确认没有副作用。
- **unknown：** 异常、取消、超时、身份变化或同步不确定，导致无法可靠判断最终效果。

取消、超时和异常可能发生在部分物品已经移动之后。不得假定回滚，不得自动重试或自动反向转移。先重新观察，再由 Agent 决定下一步。

### 6.2 排序后的失效处理

背包或箱子排序会改变槽位布局：

- 排序前签发的相关槽位 Token 全部失效。
- 排序后重新读取槽位。
- 依赖槽位编号的装备/快捷栏缓存必须重新定位或清空。
- 核验物品数量和可核验物品集合没有非预期增减；对无法完整比较的 ModItem 状态明确报告验证边界。

## 7. 权限和范围

- 只允许操作绑定 AI Player 当前可触及的原版容器。
- 不允许仅凭世界坐标远程打开或读写任意箱子。
- 不允许通过本能力读取或修改其他玩家的个人银行、虚空保险库或个人存储。
- 第一阶段不支持锁定箱子、未加载容器、未知容器类型和未验证的 Mod 容器。
- 不改变其他玩家的打开箱子状态，不抢占或重写其他玩家的 UI 上下文。
- 同一个 AI Player 的容器动作串行执行；每个动作都必须重新验证容器身份和打开状态。
- 若真人玩家正在打开同一个箱子，必须依据 Terraria 实际并发/锁定语义处理；不能假设允许共享，也不能擅自关闭真人玩家的容器。

## 8. 实施顺序

### P0：服务端打开、关闭与观察

- 先核对当前 tModLoader 程序集中 AI Player 的原生箱子交互调用链及服务端状态更新方式。
- 为 Dedicated Server 实现 AI Player 专属的箱子打开/关闭路径；不直接复用客户端 `interact_tile`。
- 增加当前打开容器的只读观察及容器/槽位 Token。
- 将新接口加入 Server AI Schema、能力投影、执行端和帮助契约。
- 验证锁定、超距、箱子被破坏/替换、Chest 记录缺失、关闭和 Actor 失效。
- 验证至少两个客户端在线时的服务端状态和网络同步。

### P1：精确存入与取出

- 实现指定数量的存入和取出，复用现有库存 Token、实例身份和原生物品转移规则。
- 对容器槽位和背包槽位做双向前置检查及操作后核验。
- 验证空槽、兼容堆叠、不可堆叠、数量不足、容量不足、收藏状态及 ModItem 转移钩子。
- 取消、异常或部分转移后返回实际证据或 unknown，不自动重放。

### P2：原生批量操作

- 逐项验证并实现附近箱子快速堆叠、当前箱子快速堆叠、全部取出、全部存入和补充物品。
- 每项先确认当前版本 API 签名、Dedicated Server 可调用性、全局上下文依赖及网络副作用。
- 如果某项只能通过客户端 UI 执行，寻找服务端等价原生路径；没有可靠路径时暂不开放该项。
- 验证满背包、满箱、收藏物品、快捷栏保留、原生过滤规则、多个同类堆叠、ModItem 和部分效果。

### P3：排序

- 实现 AI 背包和当前箱子的原生排序。
- 使旧 Token 失效，并刷新或清空槽位缓存。
- 验证排序不造成物品丢失、复制或错误装备选择。

### P4-A：`collect_drops` 主动收集

- 对接已有 `scan_entities`、Server AI 导航及原生 `GrabItems`；提供明确的收集任务入口。
- 以原生拾取可达区域作为移动目标，处理掉落物位置变化、合并、消失和槽位复用。
- 记录实际收集的物品/数量与失败原因；导航超时、背包满和拾取许可拒绝要可区分。
- 验证单个指定目标、附近全部目标、混合掉落、部分成功与正常取消。

### P4-B：`place_item` 通用放置

- 通用化现有 `ActorItemUse` / `ActorPlacement` / `ActorObjectPlacement`，对接原生墙壁及特殊放置分支。
- 不因旧版只验证工作台，就以物品白名单限制可放置范围；由原生 Item/TileObjectData/Mod Hook 规则判断。
- 优先覆盖并实测普通方块、平台、墙壁、火把、多格家具、原版箱子及至少一种遵循常规放置机制的 Mod 物品。
- 箱子放置必须验证 Chest 注册及随后正常开箱、查看、存取。
- 验证世界效果、物品消耗、结果同步及原生拒绝行为。保持既有放置 API 可用。

## 9. 验收标准

### P4-A：掉落物任务

- Dedicated Server 上可不指定物品类型收集附近掉落物；可选择指定类型。
- 真实掉落物的移动、原生拾取及库存/状态效果由服务端核实，多客户端观察一致。
- 目标位于玩家不能直接站入的位置时，仍能按原生拾取可达范围规划站位。
- 背包满、掉落物消失或合并、被其他玩家拾取、不可达及导航超时均返回正确的部分效果和原因。
- 已拾取的物品不因某个目标失败而回滚，停止后无残留移动输入。

### P4-B：通用放置

- Dedicated Server 上普通方块、平台、背景墙、火把、多格家具、箱子及一个普通 Mod 可放置物品分别实测。
- 放置由原生玩家物品使用路径触发；放置结果、物品消耗、Mod Hook 行为与 Terraria 一致。
- 箱子具有有效 Chest 注册，能被 P0–P2 容器 API 操作。
- 放置失败、支撑不满足、占地冲突、特殊物品不支持、取消和不确定副作用均准确回执。
- 两个协议客户端接收到正确的世界和库存同步；回归旧 `place_tile`、`place_object`、`place_rope`。

### P0–P3：容器回归

容器能力至少覆盖：

- Dedicated Server，至少两个客户端在线。
- 真人玩家与 AI 同时打开不同箱子，以及同时尝试打开同一箱子的情况。
- 空箱、满箱、空背包、满背包、部分可转移和多个同类堆叠。
- 收藏物品、快捷栏物品及各批量操作自身的原生过滤规则。
- 箱子锁定、超距、关闭、被破坏、被替换和 Chest 记录缺失。
- 原版物品与具有自定义堆叠/转移钩子的 ModItem。
- 操作取消、超时、异常和连接中断后的部分副作用。
- 所有客户端上的背包和箱子最终状态一致。
- 确认其他玩家的库存、容器打开状态及全局鼠标/玩家上下文未被改变。

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
- Numen 参考：Dwinovo/minecraft-numen，审计基线 commit `143ba409a62c99bf1e985b6047bcad0f00e9313e`；P4 重点参考 `ItemReach`、`Destination`、`BuildPlacer`、`UseApi` 的设计思路，游戏规则以 Terraria 为准。
- TerraPlayerBot 参考：重点检查 `BotPickupPolicy`、`BotPickupRegression`、`BotWorldActions`、`BotCompatibility`；仅研究行为与验收方式，不照搬未经验证的原生上下文切换或手动改世界方案。
