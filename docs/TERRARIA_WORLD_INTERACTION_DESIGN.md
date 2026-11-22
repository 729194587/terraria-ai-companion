# Terraria World Interaction：建筑、住宅与 NPC 交互架构设计

**文档状态：** 架构基线 / 待按真实测试拆分实现  
**代码审查基线：** \`codex/unified-autonomous-navigation\`，\`b00a9e4de25684f505c23382cd932e7c2d7f4719\`  
**目标游戏版本：** Terraria 1.4.4.9  
**目标 tModLoader：** 2026.09.3.1 Stable（见 \`docs/DEVELOPMENT.md\`）  
**参考项目：** Numen Minecraft 1.21.1

本文确定 Building、Housing、NPC Interaction 与 Economy 的架构方向。它不是一次要求 Codex 立即实现全部功能的任务书。新增能力必须先核对当前运行时能力，并通过定向原生测试证明确有缺口。

---

## 1. Objective

让 Terraria Agent 从“能够操作角色和放置物品”进一步具备改变、组织和利用世界的能力：

- 设计并施工由 Tile、Wall 和多格家具组成的建筑。
- 使用 Terraria 原生住宅规则检查房屋，并管理 NPC 住所。
- 识别 NPC 可提供的服务，使用可验证的原生服务适配器。
- 查询 NPC 商店、购买和出售物品，并核验金币与库存变化。
- 通过按需加载的 Gameplay Skills 掌握 Terraria 建筑、住宅、NPC 和经济规则。

核心职责划分：

1. **模型负责目标、设计、策略和工具组合。**
2. **Skills 提供 Terraria 规则和游戏知识，不代替 API。**
3. **Server AI 提供受能力握手约束的观察、查询和动作入口。**
4. **Terraria/tModLoader 原生机制负责规则判定和实际效果。**
5. **结果层核验世界、NPC、背包和货币状态。**

## 2. Current Implementation

以下是代码审查基线中可确认的事实；“存在代码路径”不等于“所有原版/Mod 场景已验证”。

### 2.1 可复用的现有能力

- Server AI 可观察局部场景、Tile 几何、对象、危险、NPC、掉落物和玩家。
- NPC 场景记录含世界/Actor 代次绑定的 \`entityRef\`、类型和分类信息；当前记录没有完整的住宅、商店或服务状态。
- 背包、快捷栏、物品转移、合成、容器存取、导航、挖掘和掉落拾取已有实现。
- \`place_item\` 的统一执行路径在 \`ActorPlacement.PrepareUnified\` 中已能分类普通 Tile、Wall、Rope 和具备 \`TileObjectData\` 定义的对象；执行使用现有原生物品使用 / \`Player.ItemCheck()\` 路径。
- \`ActorObjectPlacement.Definition(item)\` / \`CanPlace(item)\` 已有按物品读取 \`TileObjectData\` 的代码基础。
- 当前公开文档 \`SERVER_AI_OBJECT_PLACEMENT.md\` 仍将专用 \`place_object\` 限定为原版 Work Bench；场景 \`objectPlacementCandidates\` 也仍是工作台专用候选。因此不能把统一执行路径推断成“任意家具已完整支持”。
- \`place_item\` 的实际可用范围受引擎探针和能力握手约束，必须以运行时 Capability Manifest 为准。

### 2.2 当前明确缺口

- 没有通用建筑 Blueprint、建筑差异查询和批量施工任务。
- 没有专门的原生 Housing 检查、房间列表、NPC 住所分配和入住核验接口。
- 当前 NPC 观察不能完整提供 \`homeTileX/Y\`、\`homeless\`、商店/服务能力。
- Server AI 当前没有 \`interact_npc\`、\`inspect_npc_shop\`、\`buy_npc_item\`、\`sell_npc_item\` 或通用 NPC 服务操作。
- \`interact_tile\` 是门、宝箱、开关和拉杆等 Tile 交互，不是 NPC 对话。
- \`atomic_operations.py\` 中虽有共享的 \`mine_wall\`、\`place_wall\`、\`hammer_tile\` 定义，但当前 Server AI 能力投影没有开放这些操作。不能只凭共享 Schema 判断服务端可调用。
- 当前 \`skills/\` 只有 \`mining_and_ores\`，尚无建筑、住宅和 NPC/经济 Skills。

主要实现入口：

- \`python/atomic_operations.py\`
- \`python/server_ai.py\`
- \`python/server_ai_contract.py\`
- \`python/gameplay_skills.py\`
- \`CompanionBridge/ServerIpc/ServerSceneReader.cs\`
- \`CompanionBridge/ServerIpc/ServerAiNativeActions.cs\`
- \`CompanionBridge/ServerPlayerRuntime/ActorPlacement.cs\`
- \`CompanionBridge/ServerPlayerRuntime/ActorObjectPlacement.cs\`
- \`docs/LOCAL_SEMANTIC_SCENE.md\`
- \`docs/SERVER_AI_OBJECT_PLACEMENT.md\`

## 3. Numen Design Patterns to Reuse

Numen 的参考价值是职责划分和执行模型，不是照搬 Minecraft 的世界操作实现。

### 3.1 Building

参考：

- \`BuildApi\` / \`BuildOps\`：设计输入、只读 diff、施工入口分离。
- \`BuildSurvey\`：预检查与实际施工使用同一套格子判据。
- \`BuildLedger\`：材料预算、缺料和实际消耗共用一套计价逻辑。
- \`BuildOutstanding\`：按具体原因汇总未完成项目，并提供下一步线索。
- \`BuildCompanionTask\`：将长施工作为有界异步任务执行。
- \`BuildGameTests\`：覆盖材料不足、续建、支撑、阻挡、方向、邻居更新和取消。

关键思想：

- 模型提交设计，执行器不决定建筑风格。
- \`diff\` 只读，\`place\` 执行。
- 同一设计可重复提交；已符合目标的部分自动跳过，世界状态就是施工进度。
- 部分完成必须返回结构化原因，而不是笼统失败。
- 模型决定是否采集材料、清除障碍或修改设计。

**不照搬 Numen 的直接世界写入路径。** Terraria 建筑施工继续使用现有原生 \`Player.ItemCheck()\` / 物品放置机制；不通过直接写 Tile 或调用 WorldGen 放置来伪造玩家操作。

### 3.2 NPC Interaction / GUI

参考：

- \`UseApi\` / \`InteractEntityCompanionTask\`：实体身份、接近、范围检查和一次原生交互分离。
- \`GuiApi\`：通用界面观察和槽位操作。
- \`MenuKinds\`：按实际界面类型注册适配器。
- \`TraderKind\`：交易内容结构化读取，选择交易与取出结果分开核验。

Terraria 与 Minecraft 的重要差异：Terraria 的 NPC 对话钩子并非都能在 Dedicated Server 执行。因此借鉴“统一入口 + 服务适配器”，但不能假设存在一个可在服务器端读取的通用 NPC GUI。

---

## 4. Target Architecture

建议采用四个领域模块，建立在现有 Server AI 原子操作和任务运行时之上：

| 模块 | 职责 |
|---|---|
| **Building** | Blueprint、差异检查、材料预算、施工任务和未完成原因 |
| **Housing** | 原生房屋有效性、空置房间、NPC 住所分配和入住状态 |
| **NPC Interaction** | NPC 身份、交互资格、会话和可用服务发现 |
| **Economy** | 商店商品、动态价格、购买/出售和交易核验 |

共同执行链：

\`Agent + Skills → Domain API → Task Runtime → Terraria Native API → Evidence Verification\`

### 4.1 共同约束

- 所有动作继续绑定 World Session、Actor Generation、Actor Slot 和目标实体/物品身份。
- 世界修改在游戏线程执行。
- 读操作与写操作分开；查询商店、检查住宅不得隐式改变世界或库存。
- 动作使用新鲜观察或报价 Token；NPC 消失、商店刷新、库存变化或 Actor 代次改变后，旧 Token 失效。
- 异步任务必须有明确状态、超时、取消和输入释放语义。
- \`unknown\` 不得当成普通失败后自动重放；先重新观察并确认副作用。
- 能力是否开放由运行时握手决定，Prompt、Skill 和文档不能扩大权限。
- 第三方 Mod 未提供可验证的服务端接口时，明确报告 unsupported，不猜测或伪造效果。

## 5. Building：Blueprint and Construction

### 5.1 Blueprint 表达

采用 Terraria 二维 Tile 坐标。Blueprint 应区分三种逻辑对象：

1. **Tile cells**：普通前景 Tile，每个目标格一条记录。
2. **Wall cells**：背景墙，与前景 Tile 分层，允许同一坐标同时存在。
3. **Objects**：家具和多格物体，以一个逻辑锚点表示；占地由原生 \`TileObjectData\` 解析，不把每个占用 Tile 当成独立物品。

建议记录内容键、相对坐标、样式/方向等必要状态。优先使用内容键而非硬编码数字 ID；运行时由内容注册表解析。多格物体的实际尺寸、Origin、Anchor、Alternate 和 Frame 以当前游戏注册数据为准。

Blueprint 不应预先假定世界已知：未观察、未加载和截断区域必须标为 unknown。

### 5.2 建议的 API 形态

接口名称是设计候选，不是已存在 API。

| 接口 | 类型 | 职责 |
|---|---|---|
| \`build_diff\` | Read | 比较 Blueprint 与当前世界，计算待施工项、材料和阻塞原因 |
| \`build_place\` | Task | 对当前可执行项进行有界施工，返回逐项证据和未完成原因 |
| \`inspect_build_area\` | Read | 获取建筑范围内的 Tile、Wall、对象占地、支撑和未知覆盖 |

\`build_diff\` 与 \`build_place\` 必须复用同一套判据和材料计价逻辑。至少区分：

- already_matches
- ready
- missing_material
- occupied / dig_first
- out_of_reach
- unsupported_item_or_object
- missing_support
- blocked_by_entity
- liquid
- unknown_or_not_loaded
- native_placement_failed / unknown

施工默认不擅自拆除既有方块、家具或玩家建筑。遇到障碍时报告位置和类型，由 Agent 决定是否调用挖掘操作；只有用户目标明确要求拆除时才执行破坏。

### 5.3 执行策略

- 先做真实模型建筑基线，判断当前 \`place_item\`、\`mine_tile\`、导航、库存和合成能否完成简单住宅。
- 只有真实测试证明逐项 Lua 调用过于脆弱或成本过高，才增加批量施工任务。
- 执行器按可达范围和依赖关系分批施工；支撑结构优先于依附物体。
- 多格物体每个对象只消耗对应物品数量一次；不能按占地 Tile 数重复计费。
- 每项施工使用原生物品放置路径，并核验完整占地、样式/Frame、支撑、消耗和输入释放。
- 重新执行同一 Blueprint 时，已符合目标的部分跳过；材料不足、取消或超时后保留已完成部分。
- \`build_diff\` 的材料清单必须与实际施工扣料使用同一计算规则。

### 5.4 与现有放置系统的关系

优先扩展和复用 \`ActorPlacement.PrepareUnified\`、\`ActorObjectPlacement.Definition(item)\`、现有 \`place_item\` 和原生放置核验。

需要先补齐验证的事项：

- 常见原版家具的 TileObjectData、Alternate、Origin、支撑和方向。
- 对象观察/放置候选从当前 Work Bench 专用扩展为按当前持有物品生成的通用候选。
- 背景墙放置与拆除的 Server AI 能力开放情况。
- 墙、平台、家具和特殊物品在 Dedicated Server 上的原生 ItemCheck 行为。
- Modded TileObjectData 与 ModItem 的实际兼容范围。

不要因为某个物品通过 \`CanPlace\` 预检，就宣称该物品的所有样式和 Mod Hook 均已验证。

---

## 6. Housing：Terraria Native Housing

### 6.1 原生规则与版本

项目目标是 Terraria 1.4.4.9。官方 Wiki 当前页面包含 1.4.5.0 的历史变更，Skills 和实现必须区分版本。

1.4.4.9 住宅设计至少需要考虑：

- 房屋总面积至少 60、少于 750 Tile，包含框架。
- 房屋框架必须封闭，内部区域按 Terraria 规则连通。
- 墙体必须符合原生安全墙检查；自然生成的不安全墙不能简单视为有效墙。
- 住宅需要原生认可的四类要素：光源、桌类、椅类和门/入口类。普通发光 Tile、平台或墙不等于合格光源。
- 需要有效 Home Tile 和 NPC 站立空间；1.4.4.9 的 Home Tile 规则不能按 1.4.5.0 的平台放宽规则处理。
- 房屋还可能因已被占用、邪恶生物群系评分或 NPC 特殊要求而无效。
- 每个 Town NPC 需要独立住宅；住宅有效不代表 NPC 已满足招募条件。

**不要将 1.4.5.0 的变更套用到目标版本：** Home Tile 可使用平台、房屋坐标计算调整、住宅查询视觉反馈/无站立位置提示，以及房屋宽高 100 Tile 上限，均应按 Wiki 历史标记为 1.4.5.0 变更；在 1.4.4.9 的实现与 Skill 中不得默认采用。

### 6.2 原生 API 方向

优先验证以下 tModLoader / Terraria 原生入口：

- \`WorldGen.StartRoomCheck(x, y)\`：从房屋内部 Tile 启动原生房间检查。
- \`WorldGen.roomCheckFailureReason\`：读取原生检查原因。
- \`WorldGen.roomX1/roomX2/roomY1/roomY2\` 与相关房间数据：若目标版本可安全读取，可用于结构化范围信息。
- \`Main.TownManager.HasRoom / HasRoomQuick\`：查询 NPC 是否有房间。
- \`Main.TownManager.SetRoom\`：分配住所；调用前必须确认目标版本的方法语义和服务端同步行为。
- \`NPC.homeTileX\`、\`NPC.homeTileY\`、\`NPC.homeless\`：核验 NPC 的实际住所状态。

\`WorldGen.StartRoomCheck\` 使用静态房间检查状态。调用应在游戏主线程串行执行，并在同一次调用中立即读取结果；不得并发调用或在异步等待后读取可能已被下一次检查覆盖的全局结果。

### 6.3 建议 API

| 接口 | 类型 | 职责 |
|---|---|---|
| \`inspect_housing\` | Read | 对指定房间位置执行原生检查，返回 valid/invalid/unknown 和原生原因 |
| \`list_housing\` | Read | 查询已知房间及空置/占用状态；仅在原生数据支持时提供 |
| \`inspect_npc\` | Read | 返回 NPC 身份、类型、存活、homeless、homeTile 和可验证的住宅状态 |
| \`assign_npc_home\` | Task/Action | 对已存在 NPC 分配一个通过原生检查的空房间，并核验结果 |

不要自己重新实现 Terraria 房屋算法。Agent 可用结构化失败原因修正建筑；原生 Housing 检查是最终裁判。

### 6.4 住所分配与 NPC 招募是两件事

- 住所分配只针对已经存在于世界中的 NPC。
- 房间有效不保证 NPC 已满足首次生成/招募条件。
- Guide 在普通新世界开局已存在。普通 Merchant 首次招募需要空置有效住宅，目标版本的钱币检查在所有活跃玩家随身钱币合计达到 50 银时通过（含等号）。箱子及便携存储不计入。观察与判断方法见 [NPC 住宅攻略](../skills/npc_housing/SKILL.md)。
- 其他 NPC 有各自的物品、生命、事件、Boss 进度或探索条件；Traveling Merchant 等也不遵循普通永久住宅流程。
- NPC 出现受时间、事件和生成间隔影响。Agent 应观察实际生成状态，不把“条件已满足”报告成“NPC 已入住”。

---

## 7. NPC Interaction & Economy

### 7.1 当前架构限制：NPC 对话钩子有客户端边界

tModLoader API 文档将以下钩子标注为 Local Client Only：

- \`GlobalNPC.GetChat\`
- \`GlobalNPC.CanChat\`
- \`GlobalNPC.SetChatButtons\`
- \`GlobalNPC.OnChatButtonClicked\`
- \`GlobalNPC.ModifyActiveShop\`

因此不能假定 Server AI Dedicated Server Player 可以通过通用 NPC 右键动作读取所有 NPC 对话、按钮和 Mod 自定义服务。

目标架构应采用：

**NPC 身份与服务发现 + 服务端可执行的原生服务适配器**

而不是把所有 NPC 都模拟成一个普通容器 GUI。

对于必须依赖客户端专属钩子的 Mod NPC，只有在未来提供受控客户端代理或 Mod 服务端适配器后，才扩大支持范围。没有适配器时返回明确 unsupported。

### 7.2 NPC 身份与会话

建议新增统一 NPC 交互上下文，绑定：

- 当前 World Session / Actor Generation。
- 新鲜 \`entityRef\` 与 NPC slot/token。
- NPC 类型、位置和存活状态。
- 当前交互距离/范围检查。
- 可用服务类型及其支持状态。

NPC 移动、消失、重生、World/Actor 代次变化或会话失效后，必须重新观察。不能只使用过期的 NPC slot。

建议的只读入口：

- \`inspect_npc\`：读取身份、类型、位置、townNPC 分类、住所字段和已知服务能力。
- \`inspect_npc_services\`：列出当前 NPC 可用且当前后端真正支持的服务。

### 7.3 Shop Service

tModLoader 提供可复用的商店注册机制：

- \`NPCShopDatabase.TryGetNPCShop\` / \`AllShops\` / \`GetShopName\`。
- \`AbstractNPCShop.FillShop\`。
- \`NPCShop\` 与 \`TravellingMerchantShop\`。
- \`NPCShop.Entry\` 的条件、预留槽和开店回调。

但存在一个 Dedicated Server 关键问题：

- \`NPCShop.Entry.ConditionsMet()\` 会执行 \`Condition.Predicate\`。
- tModLoader 内置的许多 \`Condition\` 直接读取 \`Main.LocalPlayer\`，例如生物群系、玩家携带物品、购物价格调整等。
- \`GlobalNPC.ModifyActiveShop\` 是 Local Client Only；部分 Mod 动态商店内容不会由服务器端静态商店定义完整表达。

因此不能未经验证就直接在 Dedicated Server 调用 \`FillShop\`，并宣称得到的是绑定 AI Player 的准确商店。也不要把临时修改全局 \`Main.myPlayer\` 当作默认解决方案。必须先验证安全的 Actor 上下文评估方式；无法安全评估的条件或 Mod 动态商店应标为 unsupported。

建议接口：

| 接口 | 类型 | 职责 |
|---|---|---|
| \`inspect_npc_shop\` | Read | 返回当前 NPC 对应商店的实际商品、数量/库存状态、价格和货币类型 |
| \`quote_npc_purchase\` | Read | 根据当前商店快照、购买数量和玩家状态生成短时有效报价 Token |
| \`buy_npc_item\` | Action/Task | 按新鲜报价执行购买，核验支付与物品入包 |
| \`quote_npc_sale\` | Read | 读取指定物品可出售数量和预计收入 |
| \`sell_npc_item\` | Action/Task | 执行出售，核验物品减少和货币增加 |

报价 Token 绑定 NPC、商店标识、商品内容、数量、价格/货币、Actor/World 身份和库存版本。商店刷新、NPC 状态变化或背包变化后必须重新报价。

### 7.4 原生交易机制

可优先验证并复用：

- \`Player.GetItemExpectedPrice\`：计算当前玩家上下文中的买卖预期价格。
- \`Player.BuyItem(price, customCurrency)\`：原生购买货币扣除。
- \`Player.SellItem(item, stack)\`：原生出售。
- \`Player.GetItem\`：原生将购买物品放入玩家库存，并处理返回的剩余物品。

交易实现不得自行直接减金币堆叠或直接改写库存数量。

注意：\`Player.BuyItem\` 的 API 说明指出会从玩家的 inventories 和 banks 中取货币。普通购买、治疗与重铸通过绑定玩家的原生支付路径使用随身钱币及四个银行，资金证据见[原生机制审计报告](NATIVE_MECHANISM_PARITY_AUDIT_REPORT.md)。自定义货币、特殊 NPC 交易和 Mod 自定义购买回调应单独验证。

购买完成必须核验：

- 真实支付金额/货币类型。
- 玩家库存中目标物品净增。
- NPC 商店状态或库存变化（若该商店有库存限制）。
- Actor 与 NPC 身份仍匹配。

出售完成必须核验：

- 指定物品实际减少数量。
- 玩家货币净增。
- 不可出售物品、库存锁定或金币空间不足时正确拒绝。

### 7.5 Special NPC Services

将特殊服务设计为可注册适配器，而不是在通用交互层中堆叠 NPC 类型分支。

首批候选：

- Nurse：治疗。
- Guide：物品配方提示。
- Goblin Tinkerer：重铸。
- Angler：任务与任务奖励状态。

每个适配器声明：

- 支持的 NPC 类型/Mod。
- 只读状态查询。
- 可执行服务和参数。
- 前置条件。
- 原生执行入口。
- 结果证据与失败原因。

先完成 Vanilla Merchant 的查询/购买/出售闭环，再逐项增加特殊服务。客户端专属对话钩子不自动纳入服务端支持范围。

#### 护士治疗（2026-11-20）

在 tModLoader 2026.09.3.1、只加载 CompanionBridge 的服务器上，能力握手提供 `server_ai_nurse_v1`。用附近护士的 `entityRef` 调用 `quote_nurse_heal` 查看当前生命、治疗量、将清除的减益、诊金（铜）和原生支付能力，再用同一引用及 `quoteToken` 调用 `heal_at_nurse`。报价与普通商人交易共用租约的一次性令牌位置，有效期为 600 个游戏更新或 10 秒，以先到者为准。提交时重查生命、治疗内容、费用、支付能力、身份和距离；已开始支付但读回无法确认时返回 `unknown`。

目标 DLL 的 `Main.GUIChatDrawInner` 在 IL_06fa–0837 按缺失生命及剩余时间超过 60 tick 的可治疗减益计价（每项加 100 铜基础费用），再应用世界进度、专家倍率与购物价格系数。IL_1247/1269 调用护士治疗与价格钩子，IL_20c8 通过 `Player.BuyItem` 收费，随后恢复生命、用 `DelBuff` 清除剩余时间大于零的可治疗减益，最后调用 `PostNurseHeal`。服务端在绑定 AI 上执行这些步骤，按原生规则保留护士不能清除的减益。成交核对全部支付来源的总铜值差及银行非钱币物品。

修复前的隔离专服使用公开 Lua 接口，实测支付 132 铜，生命 60 → 100，移除流血（30），保留药水病（21），四个银行逐槽一致。余额不足（银行有钱）、报价后再次受伤、同槽更换护士、成交后重用旧令牌均被拒绝；拒绝前后独立读取的生命、Buff 类型、背包与银行相同。满血且只剩药水病时无付费治疗报价。诊金随世界与角色状态变化，此处金额仅对应该场景。

复现入口为 `tests/verify_server_ai_native.ps1 -NurseLua -PackagePath .local/nurse-service/Mods/CompanionBridge.tmod`。隔离 profile 中的 `nurse-results.jsonl` 保存回执和快照。真实模型、Host & Play 与远端显示留待集中验收。

---

#### 哥布林工匠重铸（2026-11-20）

目标 DLL 的 `Main.DrawInventory` 从 IL_1981 起取物品当前 `value × stack`，调用 `ItemLoader.ReforgePrice`。原版分支先按折扣资格乘 0.8 取整，再乘工匠购物系数取整，最后整除 3。界面显示金额的最小值与传给支付的变量分开；接口返回实际支付变量。槽位准入沿用 `Item.Prefix(-3)` 检查。点击时依次检查 `CanAfford`、`CanReforge`，调用 `BuyItem`、`PreReforge`、`ResetPrefix`、一次 `Prefix(-2)` 和 `PostReforge`。前缀可以和上次相同。

能力 `server_ai_reforge_v1` 适用于目标版本原版服务配置。用附近工匠的 `entityRef`、普通背包 `sourceSlot` 和最新 `sourceToken` 调用 `quote_reforge`，再带同一组参数及 `quoteToken` 调用 `reforge_item`。当前支持不可堆叠的单件原版物品，支付范围包括随身钱币及四个银行。报价与买卖、治疗共用租约的一次性报价位置。扣费后若无法确认抽取、源物品身份、数量、收藏状态、钱币或银行状态，返回 `unknown`。见[经济攻略](../skills/npc_interaction_and_economy/SKILL.md)。

修复前的隔离专服公开 Lua 实测：木剑（24）报价 31 铜，随身钱币 1,000,000 → 999,969 铜，前缀 0 → 10，四个银行逐槽相同。用新的槽位令牌再次询价为 22 铜。不可重铸物品未获得报价；随身余额不足、同槽替换工匠、重用成交前的源槽令牌均被拒绝，拒绝前后的背包和银行逐槽相同。相同前缀的成功回执由定向测试覆盖。复现命令为 `tests/verify_server_ai_native.ps1 -ReforgeLua -PackagePath .local/goblin-reforge/Mods/CompanionBridge.tmod`，证据保存在隔离 profile 的 `reforge-results.jsonl`。

## 8. Gameplay Skills：Numen 风格 + 官方 Wiki 知识

Skill 负责游戏知识和策略；不复制 API 文档，不把固定 Lua 工作流写死。

沿用当前 \`gameplay_skills.py\` 注册约定：每个 Skill 独立目录、\`SKILL.md\`、YAML 元数据、简短 description、按需 \`read_skill\`。所有 Terraria 规则明确版本。

建议第一批：

### 8.1 \`building_design\`

内容：

- 住宅、基地、仓库、制作区和防御区的二维布局。
- 地形适配、材料选择、建筑比例、入口和通道。
- Tile、Wall、平台、门、家具和多格物体的使用原则。
- 家具方向、锚点和支撑注意事项。
- 施工依赖、材料预算和分阶段扩建。
- 建筑安全、照明和敌怪防护。

主要参考：官方 Wiki Building、Guide: Bases、Furniture。

### 8.2 \`npc_housing\`

内容：

- 1.4.4.9 房屋框架、尺寸、墙壁和四类住宅要素。
- Home Tile 与站立空间。
- 原生住宅检查状态及常见无效原因。
- NPC 入住、住所分配和空置房间。
- Guide、Merchant 等早期 NPC 招募条件。
- NPC 特殊生物群系要求和邪恶生物群系影响。
- 1.4.5.0 与 1.4.4.9 的差异提醒。

主要参考：官方 Wiki House、Housing menu、NPCs、Guide: Bases。

### 8.3 \`npc_interaction_and_economy\`

内容：

- Town NPC、临时 NPC 和特殊 NPC 的区别。
- NPC 服务与招募条件。
- 商店商品、购买/出售、金币与特殊货币。
- NPC 幸福度、邻居和生物群系对价格的影响。
- 生物群系专属商品、Traveling Merchant 等特殊商店。
- 购买前核对价格、库存、支付能力和背包空间。
- 服务未开放时识别限制，不编造 NPC 能力。

主要参考：官方 Wiki NPCs、Merchant、Shop、NPC preferences。

Skill 应说明规则与策略，实际价格、商品、住所和 NPC 状态始终以游戏运行时查询为准。

---

## 9. Development and Validation Phases

本设计不要求一次性实现全部领域。

### G2-A：真实模型建筑基线

先用当前 Agent、当前模型和实际开放工具尝试建造一间符合目标版本规则的简单住宅。只发送一次自然语言目标，不预先增加 \`build.*\` API。

记录：

- 模型是否能规划布局和材料。
- 当前 Tile/Wall/家具操作是否足够。
- 多格家具是否能可靠放置。
- 施工中遇到的首个真实阻塞点。
- 最终房屋由原生 Housing Query / 原生检查判定的结果。

若流程成功，保存回归证据，不为已能完成的操作重复开发。

### G2-B：Building & Housing

仅针对 G2-A 证实的缺口实施：

- 通用对象候选与家具放置验证。
- 必要时新增 Blueprint、\`build_diff\` 和有界 \`build_place\`。
- 背景墙拆除等必要 Server AI 能力。
- 原生 Housing 检查与结构化原因。
- 住所分配及 NPC 状态核验。

### G3-A：NPC / Shop 原生路径验证

先在目标 Dedicated Server 版本验证：

- NPC 身份和交互资格。
- Vanilla NPC 的服务端交互路径。
- NPCShop 条件对绑定 AI Player 的正确求值。
- 动态商品、幸福度、价格和商店回调。
- 交易的原生货币和库存变化。

特别验证 Local Client Only 钩子与 Dedicated Server 的边界。

#### G3-A 第一项实验：普通商人出售一支火把（2026-11-19）

**历史验证环境（银行支付修复前）。** Terraria 1.4.4.9 / tModLoader 2026.09.3.1 Stable，使用仓库引擎 profile 校验 DLL 指纹。隔离 Dedicated Server 加载 CompanionBridge；fixture 在复制世界中预置商人、绑定 AI Player 和一枚存于第一银行的金币。测试程序通过实际 IPC 的 `observe_scene` 取得已在场商人的槽位和 `entityRef`，由仅在该 fixture 开启的游戏线程探针执行交易。探针同时检查 Actor、NPC 实例身份、原生 `CanTalk` 和 80 像素内的实验距离。

**目标程序集 IL 与运行时注册表。**

| 环节 | 核实结果 |
| --- | --- |
| 商店与商品 | `NPCShopDatabase.GetShopName`、`AllShops` 和 `TryGetNPCShop` 定位到 `Terraria/Merchant/Shop`。本次加载了 37 条注册项；记录中逐项保存物品 ID、禁用标记和条件键。普通火把（ItemID 8）已启用、条件数为零，与 `RegisterMerchant` IL_0092–0098 一致。注册项数量不能当作当前可售数量。 |
| 条件与动态内容 | `NPCShop.FillShop` 会执行 `Entry.ConditionsMet()`、克隆商品并调用 `Entry.OnShopOpen`，随后 `Chest.SetupShop(string,NPC)` 调用 `NPCLoader.ModifyActiveShop`。本版 `TimeDay` 的谓词读取 `Main.dayTime`，`InSnow` 的谓词读取 `Main.LocalPlayer.ZoneSnow`。探针对后者使用绑定 AI 的 `ZoneSnow`。本次昼间、AI 不在雪地，两个条件分别为 true、false；其余未审计条件记为 null。不能从单个昼间条件推断具有多个条件的商品可售。 |
| 定价与付款 | `ShopHelper.GetShoppingSettings(p,merchant)` 通过 `ProcessMood` 取得购物调整系数；在绑定 AI 上临时应用后调用 `p.GetItemExpectedPrice`，并在 finally 恢复原购物设置。原生 UI 在 `ItemSlot.HandleShopSlot` 的 IL_00ca/00da 计算买价并付款。`BuyItem` 调用 `CanAfford`、`PayCurrency`，原生支付范围可达四个银行。探针先用随身可支付槽 0–53 核对余额。 |
| 购买与交付 | 客户端入口读取 `Main.LocalPlayer` 和 `Main.mouseItem`，并维护买回记录。服务端探针只取已核实的火把条目，在该商品克隆上运行开店回调，以绑定 AI 调用 `PlayerLoader.CanBuyItem`、`Player.BuyItem`、购买创建回调和 `PlayerLoader.PostBuyItem`，再以 `Player.GetItem` 入包。购买钩子收到单商品商店上下文。完整客户端列表、动态 Mod 商店钩子、买回、自定义货币和出售仍待单独验证。 |

**隔离专服结果。** 火把基础价值 50 铜，本次购物系数约 0.89，原生报价 **44 铜币**。以下三项均在游戏线程分别采集操作前后快照，覆盖全部随身槽和四个银行槽的类型、数量、前缀、收藏标记。

| 场景 | 实测读回 |
| --- | --- |
| 随身余额不足 | 随身 0 铜，第一银行有 1 金币。按 44 铜报价返回 `insufficient_carried_funds`，背包和四个银行逐槽相同。 |
| 成功购买一件 | 独立的 fixture 准备步骤提供 2 银币。购买前后随身价值 **200 → 156 铜**，火把 **0 → 1**，其他物品总量及银行槽均不变。再次通过绑定 IPC 的 `state` 独立读回 1 银、56 铜及 1 支火把。 |
| 商人引用过期 | 同一世界和代次内替换商人实例，确认复用同一 NPC 槽。携带仍足以再买一支火把的余额，使用先前实际观察到的引用返回 `stale_entity_ref`，未生成报价，背包和四个银行逐槽相同。 |

复现时先构建至 `.local/g3-a-shop`，再运行 `tests/verify_server_ai_native.ps1 -G3Shop -PackagePath .local/g3-a-shop/Mods/CompanionBridge.tmod`。新 profile 内的 `g3-shop-result.json` 保存商店注册项、报价及逐槽快照，`g3-shop-ipc-readback.json` 保存独立读回。G3-B 可复用已验证的原生支付和交付路径；生产报价令牌、完整商品条件适配、结果未知后的恢复及模型可调用的购买 API 仍属后续工作。

### G3-B：NPC Interaction & Economy

先交付 Vanilla Merchant：

1. 发现商人并验证身份/范围。
2. 读取当前实际商店。
3. 读取价格和货币。
4. 检查支付能力与库存空间。
5. 购买指定商品。
6. 核验货币和库存。
7. 出售指定物品并核验结果。

之后再增加其他商店和特殊服务适配器。

#### 普通商人购买的 Lua 专服验收（2026-11-19）

从 `observe_scene` 取得普通商人的 `entityRef`，在交互范围内调用 `inspect_npc_shop({entityRef=ref})`。条目按 `itemType` 返回条件是否满足、实际单价（铜）、货币、库存类型和当前能否购买。当前支持无条件、白天及绑定玩家的雪地条件；其他未适配条件返回未知，不能据此报价。此阶段支持普通钱币、无限库存、每次一件。

`quote_npc_purchase({entityRef=ref,itemType=8,quantity=1})` 返回火把的实际单价和一次性 `quoteToken`。同一 IPC 租约的新报价替换旧报价，600 个游戏更新或 10 秒单调时钟时间先到即失效。`buy_npc_item({entityRef=ref,quoteToken=token})` 重新核对绑定玩家、商人实例、距离、背包、商品条件与价格，沿用 G3-A 的原生支付和交付路径。支付开始后若无法确认效果，返回 `unknown`。

以下为银行支付修复前的 tModLoader 2026.09.3.1 隔离专服记录。测试场景提供商人、随身测试钱币及银行内的 1 金，交易和拒绝均走公开 Lua 接口。

| 场景 | 动作回执与独立逐槽读回 |
| --- | --- |
| 查价后购买火把 | 单价 44 铜，随身钱币 200 → 156 铜，火把 0 → 1，四个银行逐槽相同 |
| 随身余额为零、银行有钱 | `insufficient_carried_funds`，背包与四个银行逐槽相同 |
| 报价到期，或重复使用已成交的令牌 | 均为 `stale_quote`，背包与四个银行逐槽相同 |
| 同一 NPC 槽换成新商人后使用旧引用 | `stale_entity_ref`，背包与四个银行逐槽相同 |

新 profile 中的 `merchant-lua.jsonl` 保存 Lua 调用、回执及独立快照。购买和出售的联合复现入口见下节。

#### 普通商人出售一件物品

**目标版本 IL。** tModLoader 2026.09.3.1 的 `ItemSlot.SellOrTrash` 检查收藏标记、钱币类型和 `PlayerLoader.CanSellItem`，随后调用 `Player.SellItem`（IL_007b）。成功分支调用 `Chest.AddItemToShop`（IL_0097）、清空源槽（IL_00a1），再调用 `PlayerLoader.PostSellItem`（IL_00d6）。`SellItem` 根据绑定玩家的购物设置调用 `GetItemExpectedPrice`。普通卖价取第一个输出整除 5，正价值商品最低 1 铜，钱币发到随身槽 0–53。原生客户端同次开店内还可能按 `shopSellbackHelper` 记录退款。

**Lua 接口。** 握手能力为 `server_ai_merchant_sale_v1`。从 `inspect_inventory({mode="slots",slots={slot}})` 取得当前 `inventoryToken`，传给 `quote_npc_sale({entityRef=ref,sourceSlot=slot,sourceToken=inventoryToken})`。可出售时返回一件的预计实收铜币 `unitPrice` 和 `quoteToken`。在相同参数上加该报价令牌调用 `sell_npc_item`。买卖共用每个 IPC 租约的一份一次性报价，刷新任一报价都会替换旧报价。执行时重验商人、背包、槽位实例、价格与收款空间。收款开始后，若源槽、总数量、随身钱币或银行证据不符，返回 `unknown`。

服务器将每次交易视作一次独立访问。出售只处理主背包 0–49 内的一件原版物品；沿用原生收款、单次访问的商店容器和出售钩子，并通过 `ItemLoader.TransferWithLimit` 移走指定槽的一件。收藏品、钱币、无售价物品、已有客户端退款额度或未适配的出售钩子均不开放交易。卖回物不会作为可再次购买的库存出现。

**隔离专服读回。** 同一普通商人卖出火把时收取 44 铜。AI 将这支火把换到另一背包槽，拿旧槽位令牌提交原出售报价，收到 `stale_source_token`。用新槽位重新报价并出售后，火把从 1 支变为 0 支，随身 156 铜变为 167 铜，实收与报价同为 11 铜。四个银行逐槽相同，银行中的 1 金保留。另一次用有效出售报价替换同槽商人后，旧引用收到 `stale_entity_ref`。两种拒绝均在变更前返回，背包与四个银行的独立逐槽快照一致。

复现时先运行 `scripts/build-companion.ps1 -OutputPath .local/g3b-sale`，再运行 `tests/verify_server_ai_native.ps1 -MerchantLua -PackagePath .local/g3b-sale/Mods/CompanionBridge.tmod`。验收使用确定性 Lua Worker；真实模型 Host & Play 另行验收。

### G4：Town Development

在前述能力稳定后，再考虑 NPC 幸福度优化、Pylon 网络、城镇布局、储物/生产规划和长期基地维护。

---

## 10. Testing and Acceptance

### Building

- Tile、Wall、家具、多格物体分别测试。
- 测试 TileObjectData 的 Origin、占地、支撑、Style、Alternate 和方向。
- 材料不足时只完成可支付部分；同一 Blueprint 重试不得重复放置已完成项。
- 测试实体阻挡、未知区域、未加载区块、液体、支撑缺失、占地冲突、取消和 unknown。
- 核验原生放置事件、完整 Tile 帧、材料消耗和输入释放。

### Housing

- 使用目标版本的原生房屋检查作为最终判据。
- 覆盖有效房间、缺少墙、缺少家具、尺寸不足/过大、占用、邪恶生物群系、Home Tile 不合格和 NPC 特殊要求。
- 测试空置房间、重复分配、NPC 死亡/重生和房间失效。
- 分别验证 \`StartRoomCheck\`、\`TownRoomManager\` 和 \`NPC.homeTileX/Y\` 的服务端行为。

### NPC / Economy

- NPC 消失、移动、重生、身份过期和超出范围。
- 商店条件、动态商品、幸福度、价格、售罄/刷新和特殊货币。
- 金币不足、背包空间不足、物品不可出售、库存变化和交易中断。
- 购买/出售必须同时核验货币和物品变化。
- 对 Local Client Only 的 Mod Hook 明确验证 Dedicated Server 不支持的情况。

### Real-model E2E

第一条综合验收目标：

> 在附近建立一间有效住宅，让现有向导入住；满足商人招募条件后，让商人入住；然后从商人处购买指定物品并放入基地箱子。

分别报告：

- 建筑是否完成。
- Terraria 原生 Housing 是否判定有效。
- NPC 招募条件是否满足。
- NPC 是否真实生成/入住。
- 商店商品和价格是否读取正确。
- 交易和箱子存储是否真实发生。

不得把房屋建成等同于 NPC 已入住，也不得把模型自述当作交易证据。

Dedicated Server、Host & Play 和真实模型测试分别记录，不互相替代。

---

## 11. Open Technical Questions

以下事项在进入具体实现任务前必须通过目标版本源码或测试确认：

1. \`WorldGen.StartRoomCheck\` 在目标 tModLoader 版本的调用前置、静态状态和失败原因读取语义。
2. \`TownRoomManager.SetRoom\` 在 Dedicated Server 的调用方式、NPC 类型参数语义和网络同步。
3. 1.4.4.9 的 Home Tile 计算与 1.4.5.0 变更的准确边界。
4. 通用 \`place_item\` 对不同原版家具、Wall 和 Modded TileObjectData 的实际支持范围。
5. \`NPCShopDatabase\` / \`AbstractNPCShop.FillShop\` 如何在不污染全局玩家上下文的情况下评估绑定 AI Player 的商店条件。
6. 对 Local Client Only NPC 对话/商店钩子，是否需要受控客户端代理；第一版可否仅支持服务器可验证的 Vanilla 服务适配器。
7. 交易 API 对银行货币、特殊货币、Mod 自定义购买回调和物品溢出的实际副作用。

未解决的开放问题不能在实现文档中写成已确认能力。

---

## 12. References

### Project

- [Server AI Capability Contract](SERVER_AI_CAPABILITIES.md)
- [Local Semantic Scene](LOCAL_SEMANTIC_SCENE.md)
- [Server AI Object Placement](SERVER_AI_OBJECT_PLACEMENT.md)
- [Inventory Operations](INVENTORY_OPERATIONS.md)
- [Gameplay E2E Baseline](G1_GAMEPLAY_INTEGRATION_MVP.md)

### Numen source

- [BuildApi](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/numen/common/src/main/java/com/dwinovo/numen/tools/work/BuildApi.java)
- [BuildOps](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/numen/common/src/main/java/com/dwinovo/numen/tools/BuildOps.java)
- [BuildSurvey](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/numen/common/src/main/java/com/dwinovo/numen/task/build/BuildSurvey.java)
- [BuildLedger](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/numen/common/src/main/java/com/dwinovo/numen/task/build/BuildLedger.java)
- [BuildOutstanding](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/numen/common/src/main/java/com/dwinovo/numen/task/build/BuildOutstanding.java)
- [UseApi](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/numen/common/src/main/java/com/dwinovo/numen/tools/interact/UseApi.java)
- [GuiApi](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/numen/common/src/main/java/com/dwinovo/numen/tools/interact/GuiApi.java)
- [MenuKinds](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/numen/common/src/main/java/com/dwinovo/numen/tools/menu/MenuKinds.java)
- [TraderKind](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/numen/common/src/main/java/com/dwinovo/numen/tools/menu/TraderKind.java)
- [building_design Skill](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/numen/common/src/main/resources/numen/skills/numen/building_design/SKILL.md)
- [villagers_and_trading Skill](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/numen/common/src/main/resources/numen/skills/numen/villagers_and_trading/SKILL.md)

### Official Terraria Wiki

- [House](https://terraria.wiki.gg/wiki/House)
- [Housing menu](https://terraria.wiki.gg/wiki/Housing_menu)
- [Guide: Bases](https://terraria.wiki.gg/wiki/Guide:Bases)
- [Building](https://terraria.wiki.gg/wiki/Building)
- [NPCs](https://terraria.wiki.gg/wiki/NPCs)
- [Merchant](https://terraria.wiki.gg/wiki/Merchant)
- [Shop](https://terraria.wiki.gg/wiki/Shop)
- [NPC preferences](https://terraria.wiki.gg/wiki/Npc_preferences)

### Official tModLoader API / source

- [WorldGen](https://docs.tmodloader.net/docs/stable/class_world_gen.html)
- [TownRoomManager](https://docs.tmodloader.net/docs/stable/class_town_room_manager.html)
- [NPC](https://docs.tmodloader.net/docs/stable/class_n_p_c.html)
- [GlobalNPC](https://docs.tmodloader.net/docs/stable/class_global_n_p_c.html)
- [NPCShopDatabase](https://docs.tmodloader.net/docs/stable/class_n_p_c_shop_database.html)
- [AbstractNPCShop](https://docs.tmodloader.net/docs/stable/class_abstract_n_p_c_shop.html)
- [NPCShop](https://docs.tmodloader.net/docs/stable/class_n_p_c_shop.html)
- [Player](https://docs.tmodloader.net/docs/stable/class_player.html)
- [Condition.cs](https://github.com/tModLoader/tModLoader/blob/stable/patches/tModLoader/Terraria/Condition.cs)
- [NPCShop.Entry.cs](https://github.com/tModLoader/tModLoader/blob/stable/patches/tModLoader/Terraria/ModLoader/NPCShop.Entry.cs)
- [NPCShop.cs](https://github.com/tModLoader/tModLoader/blob/stable/patches/tModLoader/Terraria/ModLoader/NPCShop.cs)
- [NPCShopDatabase.cs](https://github.com/tModLoader/tModLoader/blob/stable/patches/tModLoader/Terraria/ModLoader/NPCShopDatabase.cs)

本文中的 API 名称是研究和设计入口；最终实现必须以项目锁定的 tModLoader 2026.09.3.1 Stable 实际编译结果和 Dedicated Server 测试为准。
