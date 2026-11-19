# G2：Building & Housing Implementation

**状态：** Implementation Spec / 待 Codex 实现  
**代码基线：** \`codex/unified-autonomous-navigation\`，代码审查基线 \`b00a9e4de25684f505c23382cd932e7c2d7f4719\`  
**目标环境：** Terraria 1.4.4.9 + tModLoader 2026.09.3.1 Stable  
**架构依据：** [Terraria World Interaction](TERRARIA_WORLD_INTERACTION_DESIGN.md)  
**本阶段不包含：** Skills、NPC 对话/商店/经济系统、钓鱼/农业、电路和涂漆

## 1. Objective

实现通用的 Terraria 建筑与住宅能力，让 Server AI Agent 能：

1. 提交二维建筑 Blueprint，描述前景 Tile、背景 Wall 和多格 Object。
2. 只读比较 Blueprint 与真实世界，得到差异、材料预算和阻塞原因。
3. 通过有界异步任务逐项施工，使用现有原生物品放置路径。
4. 对已施工的部分自动跳过；材料不足、阻挡、超时或取消后保留已完成成果。
5. 调用 Terraria 原生 Housing 检查，获取房屋范围、有效性和失败原因。
6. 查看 Town NPC 的住所状态，并将现有 Town NPC 分配到一个有效且空置的房间。
7. 使用原生状态核验施工、房屋有效性和住所分配结果。

目标端到端场景：

> 在一个合适的空地建造一间符合 Terraria 1.4.4.9 规则的住宅，通过原生 Housing 检查确认有效，然后把现有 Guide 分配到该住宅并验证其住所状态。

本阶段优先完成这个垂直切片，不扩展到 NPC 对话和商店交易。

## 2. Current Implementation

### 2.1 已有且应复用的能力

- \`place_item\` 已通过 \`ActorPlacement.PrepareUnified\`、\`ActorItemUse\` 和原生 \`Player.ItemCheck()\` 执行放置。
- \`ActorPlacement.UnifiedKind\` 已区分普通 Tile、Wall、Rope、TileObjectData Object 和 Storage Object。
- \`ActorObjectPlacement.Definition(Item)\` / \`CanPlace(Item)\` 已能根据当前物品的 \`TileObjectData\` 解析多格物体形状。
- 原生放置回执已核验放置事件、物品消耗、对象 Frame/完整占地、外部变化和输入释放。
- \`ActorInventory\` 已有受核验的背包转移、交换和快捷栏持用能力。
- Server AI 已有任务运行时、取消、身份绑定、子任务执行器和导航。
- 场景观察已能返回 NPC 实体引用、分类、树木/矿物/家具等对象；当前家具语义和放置候选仍不完整。
- \`ServerAiQueries\` / \`AiReadOnlyProtocol\` 是只读查询入口；\`ServerAiNativeActions\` 是原生动作入口。
- \`python/server_ai.py\` 根据 bind Capability Handshake 投影模型可用工具；共享 Schema 中存在的操作不等于 Server AI 已开放。

### 2.2 当前明确缺口

- 没有通用 Blueprint、\`build_diff\` 或批量施工 Task。
- 当前 \`objectPlacementCandidates\` 仍是 Work Bench 专用候选；不能把它当作通用家具候选。
- 当前统一 \`place_item\` 可以分类 Wall，但 Agent 缺少建筑级 Wall 规划/差异核验。
- Server AI 没有原生 Housing 检查 API。
- NPC 观察没有完整输出 \`homeTileX/Y\`、\`homeless\` 和可用住所状态。
- Server AI 没有住所分配 API。
- 当前 \`skills/\` 仅有 \`mining_and_ores\`；本阶段不新增 Skill。

### 2.3 主要代码入口

- \`CompanionBridge/ServerPlayerRuntime/ActorPlacement.cs\`
- \`CompanionBridge/ServerPlayerRuntime/ActorObjectPlacement.cs\`
- \`CompanionBridge/ServerPlayerRuntime/ActorInventory.cs\`
- \`CompanionBridge/ServerIpc/ServerAiNativeActions.cs\`
- \`CompanionBridge/ServerIpc/ServerAiQueries.cs\`
- \`CompanionBridge/ServerIpc/ServerSceneReader.cs\`
- \`CompanionBridge/ServerIpc/AiReadOnlyProtocol.cs\`
- \`CompanionBridge/ServerIpc/AiAtomicActions.cs\`
- \`CompanionBridge/SceneGeometry.cs\`
- \`python/atomic_operations.py\`
- \`python/server_ai.py\`
- \`python/server_ai_contract.py\`

## 3. Design

### 3.1 Building Blueprint

定义一个受大小限制的二维 Blueprint。建议结构：

- \`origin\`：世界 Tile 坐标原点。
- \`tiles[]\`：普通前景 Tile 目标，使用相对坐标和 placeable item type/content key。
- \`walls[]\`：背景墙目标，使用相对坐标和 wall item type/content key。
- \`objects[]\`：多格物体目标，以 TileObjectData Origin 锚点表达；占地、Origin、Anchor、Style/Alternate 由当前加载内容解析，不接受模型提供的尺寸作为权威。

约束：

- 使用当前运行时内容注册表和物品属性验证 item type、createTile/createWall、placeStyle 与可放置类别。
- 同一坐标允许同时有前景 Tile 和背景 Wall。
- 多格 Object 是单个逻辑目标，材料消耗按物体件数计算，不能按占地格数计算。
- 未知、未加载或超出检查范围的 Tile 必须保留为 unknown，不能当作空气。
- Blueprint 大小、坐标、条目数和序列化体积必须有明确上限。
- 不支持液体注入/排除、Wire、Paint、Actuator、复杂家具状态或任意 ModItem 的部分，明确返回 unsupported，不猜测。

### 3.2 Read-only build_diff

新增只读 \`build_diff\`：

- 比较 Blueprint 与当前世界实际 Tile/Wall/Object 状态。
- 已满足的目标标为 \`already_matches\`。
- 统计待施工数量、当前可施工项、距离/范围、材料需求与缺料。
- 对普通 Tile、Wall、多格 Object 分别给出目标状态。
- 阻塞原因至少区分：
  - \`occupied_requires_dig\`
  - \`wall_conflict\`
  - \`missing_material\`
  - \`material_not_available_for_native_placement\`
  - \`out_of_reach\`
  - \`missing_support_or_anchor\`
  - \`object_footprint_blocked\`
  - \`blocked_by_player\`
  - \`liquid\`
  - \`unknown_or_not_loaded\`
  - \`unsupported_content\`
- 材料预算和实际施工必须共用同一套计数逻辑。
- 查询不移动角色、不改变背包、不放置或破坏世界。
- 多格物体必须根据真实 \`TileObjectData\` 解析占地和锚点；不允许使用 Work Bench 固定 2×1 假设。

### 3.3 Asynchronous build_place

新增有界异步 \`build_place\`：

- 每个 Blueprint 目标在一次任务中最多尝试一次。
- 已匹配目标自动跳过；重新提交同一 Blueprint 可继续未完成部分。
- 施工按依赖排序：先完成支撑结构，再完成依附物体；背景墙和前景 Tile 分层处理。
- 优先复用现有导航和任务运行时；不另造运动控制器。
- 任务可在安全范围内接近可施工目标；若路线不安全、未知或超出预算，返回具体未完成项。
- 可通过现有 \`ActorInventory\` 能力将需要的材料持用到快捷栏；不得直接改写背包数组。若无法安全准备材料，返回准确的缺料/不可用原因。
- 每个实际放置仍调用现有原生 \`place_item\` 路径，保留 \`Player.ItemCheck()\`、TileLoader/Mod Hook、原生范围和消耗规则。
- 不直接写 Tile，不调用 \`WorldGen.PlaceTile\` / \`TileObject.Place\` 伪造玩家放置。
- 不自动挖除既有前景或背景墙，不自动拆除玩家建筑；遇到阻挡只报告 \`occupied_requires_dig\` 或 \`wall_conflict\`，由 Agent 决定是否改变设计/选择位置。
- 取消或超时保留已完成的放置；部分成功必须返回每类已完成数量和未完成原因。
- \`unknown\` 动作不自动重放。任务恢复必须先重新观察世界，再依 Blueprint 差异继续。
- 任务结果必须包含 World/Actor/Generation/Slot 绑定、逐项结果、原生放置证据和输入释放状态。

### 3.4 Housing Inspection

新增只读 \`inspect_housing\`：

输入为房屋内部的一个 Tile 坐标；可选目标 NPC type 用于附加 NPC 住宅要求检查（若目标版本原生 API 支持）。

使用 Terraria 原生 Housing 机制，优先验证：

- \`WorldGen.StartRoomCheck(x, y)\`
- \`WorldGen.roomCheckFailureReason\`
- \`WorldGen.roomX1/roomX2/roomY1/roomY2\`
- \`WorldGen.roomOccupied\`
- \`WorldGen.roomTorch/roomTable/roomChair/roomDoor/roomEvil\`
- \`WorldGen.RoomNeeds(npcType)\`（先核实目标版本的真实签名与语义）

关键约束：

- 房间检查在游戏主线程同步执行。
- \`WorldGen.StartRoomCheck\` 使用静态结果字段；同一次调用后立即复制所有结果，禁止跨帧/异步后读取，也禁止并发检查。
- 原生失败枚举转换为稳定字符串，并保留原始枚举名和检查版本。
- 返回 \`valid/invalid/unknown\`，范围边界、房间占用、家具要素和原生失败原因。
- 若原生检查未能给出明确结果，返回 unknown，不用模型推断替代。
- 不自己重写 Terraria 的住宅算法。

### 3.5 NPC Housing State and Assignment

扩展 NPC 观察或新增 \`inspect_npc_housing\`，至少返回：

- 当前 NPC 的新鲜 \`entityRef\`、type、slot、active、townNPC 分类。
- \`homeless\`、\`homeTileX\`、\`homeTileY\`。
- 能从原生 TownRoomManager 安全读取的房间/占用状态。

新增 \`assign_npc_home\`：

- 输入使用新鲜 NPC entityRef 和已通过 \`inspect_housing\` 的房间位置/检查 Token。
- 仅允许有效、存活、可分配住宅的 Town NPC。
- 重新验证 NPC 身份、房屋状态、房间占用和 Actor/World 绑定。
- 通过目标版本原生 \`Main.TownManager.SetRoom\` / \`HasRoom\` 等接口分配；Codex 必须先确认 SetRoom 的 npcID 参数究竟是 NPC type 还是 NPC slot，不能猜测。
- 不允许对无效房屋、已占用房屋或不支持住房的 NPC 分配。
- 分配后重新读取 NPC 的 \`homeTileX/Y\`、\`homeless\` 和 TownRoomManager 状态；只有结果与请求位置一致才报告 completed。
- 若原生分配 API 仅修改本地/未同步或状态不明确，返回 unknown 并记录证据，不伪造成功。

### 3.6 Capability and API Contract

所有新接口必须经过完整能力链：

1. C# bind Capability Manifest。
2. \`AiReadOnlyProtocol\` 查询白名单 / \`ServerAiQueries\` 查询分发。
3. \`AiAtomicActions\` / \`ServerAiNativeActions\` 动作和任务入口。
4. Python \`atomic_operations.py\` Schema。
5. \`server_ai.operations_for_capabilities()\` 能力投影。
6. \`server_ai_contract.py\` Manifest、Help 和结果语义。
7. Lua Runtime / Program Task 预算、取消和回执验证。
8. 对应 Python 与原生 Dedicated Server 测试。

建议能力旗标：

- \`server_ai_build_v1\`：build_diff 与 build_place。
- \`server_ai_housing_v1\`：inspect_housing 与 NPC 住所观察。
- \`server_ai_housing_assignment_v1\`：assign_npc_home。

只有对应实现和测试实际存在时才在握手中发布能力。

## 4. Implementation Steps

1. 对照目标 tModLoader 2026.09.3.1 Stable 的实际程序集/源码，核实 Housing API 的签名、静态字段、调用线程和同步行为。特别确认 \`TownRoomManager.SetRoom\` 的 npcID 语义。
2. 先补齐通用 Blueprint 数据模型和解析/验证，不修改原生放置器。
3. 实现 \`build_diff\`，并让它与后续施工任务共用目标解析、TileObjectData 和材料账本。
4. 实现 \`build_place\` 异步任务，使用现有 Task Runtime / ChildExecutor / Navigation / ActorInventory / place_item 原生执行路径。
5. 扩展场景或专用对象观察，使模型能读取常见家具的真实类别、锚点、占地和内容键；移除仅对 Work Bench 的通用接口假设，但保留旧 Work Bench API 的兼容性。
6. 实现 \`inspect_housing\`，在单次游戏线程调用中复制 WorldGen 的静态检查结果。
7. 实现 NPC 住所状态查询和 \`assign_npc_home\`，以新鲜 entityRef 和原生状态进行核验。
8. 补齐能力握手、Schema、Help、Manifest、测试和文档。
9. 运行定向测试和 Dedicated Server 原生测试；真实模型 E2E 若环境可用则执行，否则明确列为待用户验收。

若目标版本原生 API 与本设计中的候选名称/语义不同，以实际程序集为准，修订实现并记录差异。

## 5. Testing

### A. Static / Python contract

新增定向测试，至少覆盖：

- Blueprint Schema：缺字段、越界、重复目标、超大数组、非法内容类型。
- Tile / Wall / Object 三类目标的解析和材料计数。
- 多格对象按对象件数计费，而不是按占地格数。
- 同一 Blueprint 重复执行时已完成目标跳过。
- build_diff 只读，不触发 start/action。
- 能力缺失时工具不暴露，且 dispatch 在 RPC 前拒绝。
- 任务超时、取消、unknown 和部分成功的语义。
- Housing 的有效、无效、unknown 和原生失败枚举映射。
- NPC entityRef 过期、NPC 死亡/消失、Generation 不匹配和房屋检查 Token 过期。
- SetRoom 未生效或结果不匹配时不得 completed。

### B. C# offline / native tool checks

复用现有 \`tests/run_server_ai_tool_tests.ps1\` 和 C# patch 测试设施。新增覆盖：

- TileObjectData 不同宽高、Origin、AnchorBottom/Top/Left/Right、Alternate。
- 完整占地和 Frame 核验。
- Tile / Wall / Object 原生放置事件、消耗和输入释放。
- Housing 静态结果立即复制；连续两次检查不得串用上一次结果。
- NPC 住所分配前后状态和同步证据。

### C. Dedicated Server native E2E

建立隔离测试世界和专用 AI Player。允许 fixture 预置测试材料和 NPC，但不得直接写入最终房屋或伪造住宅结果。

至少覆盖：

1. 用真实原生动作建造一个 8×8 外框住宅（总面积 64 Tile），放置安全背景墙、门、火把、工作台和椅子。
2. 使用 \`inspect_housing\` 对房间内部坐标检查，确认目标版本原生 Housing 结果为 valid。
3. 对缺墙、缺光源、缺桌椅、尺寸不合格、占用、Home Tile 不合格和邪恶生物群系等场景，确认原生失败原因正确返回。
4. 将现有 Guide 分配到有效空房间，确认 \`homeless=false\` 且 homeTile 坐标与目标房间一致。
5. 对无效房间、已占用房间、过期 NPC entityRef 和不存在 NPC 的分配请求，确认拒绝且无副作用。
6. 施工过程中材料不足、对象被阻挡、任务取消/超时后，确认已完成部分保留、未完成部分有明确原因；重新提交同一 Blueprint 不重复消耗或放置。

### D. Real-model Host & Play

若本地模型和图形环境可用，使用真实模型，只发送一次：

> 在附近建造一间适合向导入住的房子，完成后检查房屋是否有效，并把向导安排进去。

不人工指定每一格坐标、材料顺序或家具放置步骤。记录完整模型工具调用、Blueprint、任务回执、原生 Housing 检查、NPC homeTile 状态和最终场景。

若真实模型测试未运行，报告“未执行”，不得用确定性模型代替。

### Pass criteria

- Python 契约测试和 C# 定向检查通过。
- Dedicated Server 原生住宅建造、Housing 检查和住所分配闭环通过。
- Host & Play / 真实模型结果单独报告；没有执行不得声称通过。
- 任一 unknown 不得报告成功。
- 任何新增 API 均有 capability gate、Schema、回执和定向测试。

## 6. Deliverables

1. 通用 Building Blueprint、\`build_diff\`、\`build_place\`。
2. 原生 \`inspect_housing\`、NPC 住宅状态观察和 \`assign_npc_home\`。
3. 必要的通用家具语义观察扩展。
4. Python/C# 定向测试与 Dedicated Server 原生测试。
5. 更新 \`SERVER_AI_OBJECT_PLACEMENT.md\`、相关 Housing 专题、\`docs/INDEX.md\` 和路线图。
6. Git Commit，包含实现范围、测试命令、实际结果和未完成项。

## 7. Explicitly Deferred

- NPC 对话、NPC Shop、购买/出售和特殊 NPC 服务（G3）。
- Gameplay Skills（按用户决定，后续单独编写）。
- 任意 Mod NPC 的客户端专属对话 Hook。
- 电线、涂漆、液体工程、农业、钓鱼。
- 自动拆除既有玩家建筑、自动清理墙/Tile 和危险地形。
- 未经验证的通用 ModItem / 特殊 TileObjectData 支持。
