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

### G2-A 基线检索（2026-11-19）

按 G2-A 标识、建筑基线名称及中英文建房目标检索了仓库文档和本机 `.local`（含忽略文件的路径、日志、JSON/JSONL 和文本）。命中的 G2-A 是架构 §9 的前测要求，未找到同时具有真实模型目标、施工轨迹和原生验房结果的运行记录。首个施工阻塞点仍未知；实施后 E2E 应与前测分别标记。

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
- Blueprint 单体统一按紧凑 JSON 的 UTF-8 字节数计量，上限 3072 字节（含）。坐标与条目数仍须独立校验。以相对坐标对象 `{"x":1,"y":2,"itemType":9}` 示意，8×8 外框扣除三格门洞有 25 个前景目标，另有 36 个内墙目标和 4 个门/家具/照明目标。用数字物品 ID、三至四位数原点及上述字段，样例小于 2 KB，可容纳于该预算；这是编码容量示例。
- Python 发送 `build_diff` 时须对**完整请求**使用 `json.dumps(request, ensure_ascii=False, separators=(",", ":"))`，按 UTF-8 编码后核对整包。既有 IPC 请求行上限保持 4096 字节（不含终止 LF）；Blueprint 和含身份字段、操作名等的完整请求分别校验，任一超限即在发送前拒绝。
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

**本机程序集核验（静态 IL，2026-11-19）。** 使用安装目录内 Mono.Cecil 0.11.6 读取 `D:/steam/steamapps/common/tModLoader/tModLoader.dll`，产品版本 1.4.4.9+2026.09.3.1 Stable，SHA-256 `BF2CA4D53512D064FAE664170A0D21ECC5E2ED51B5E917D871EC78CC449263E9`。

- 以 `WorldGen.MoveTownNPC(int x,int y,int n) -> bool` 的判定分支为依据。它调用 `StartRoomCheck(x,y) -> bool`，由该阶段产生边界和 `roomCheckFailureReason`；随后 `RoomNeeds(int npcType) -> bool` 写入光源、桌、椅、门标志。原方法传入的是静态 `prioritizedTownNPCType`，直到后续评分前才按非负槽位取目标 type。针对向导的查询须明确 NPC 类型来源。
- 原方法另有臭虫饰品和特殊 NPC 条件分支。`ScoreRoom(int ignoreNPC,int npcTypeAskingToScoreRoom) -> void` 在此以 `(-1, type)` 调用（IL_01e3），由 `hiScore > 0` 判断评分通过；占用、邪恶程度和候选住所格来自评分阶段。分别报告结构、家具、评分、占用及原生失败阶段；未执行阶段的静态字段标为未知，禁止沿用前次值。

关键约束：

- 房间检查在游戏主线程同步执行。
- \`WorldGen.StartRoomCheck\` 及后续阶段使用静态结果字段；同一游戏线程检查内按阶段立即复制结果，禁止跨帧/异步后读取，也禁止并发检查。
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
- 原生写入口是 `WorldGen.moveRoom(x,y,n)`（小写 m）。客户端发送消息 60；服务器以 n 为槽位，从 `Main.npc[n].type` 设置优先类型，将该 NPC 标为 homeless 后调用 `SpawnTownNPC(x,y)`。后者用 `FindAHomelessNPC()` 选择实例，在已有 NPC 分支写入 `bestX/Y`、清除 homeless。写前须确认该选择能唯一落在目标向导。
- `moveRoom` 丢弃 SpawnTownNPC 返回值，再将原槽位的 **NPC type** 和 homeTileX/Y 传给 `WorldGen.TownManager.SetRoom(type,x,y)`（IL_0046–0074）。直接写管理器会跳过 NPC 状态更新；原生调用返回也可能已有部分状态变化。
- 不允许对无效房屋、已占用房屋或不支持住房的 NPC 分配。
- 分配后重新读取 NPC 的 \`homeTileX/Y\`、\`homeless\` 和 TownRoomManager 状态；复核同一实体仍 active、homeless=false，住所等于该房间原生选出的 bestX/Y，且 `HasRoom(type,out Point)` 返回相同格，方可报告服务器侧 completed。室内输入格与住所格可不同。远端可见性仍须联机观察验证。
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

- \`server_ai_build_diff_v1\`：只读 build_diff。\`server_ai_build_v1\`：build_place。
- \`server_ai_housing_v1\`：inspect_housing 与 NPC 住所观察。
- \`server_ai_housing_assignment_v1\`：assign_npc_home。

只有对应实现和测试实际存在时才在握手中发布能力。

## 4. Implementation Steps

1. 静态调用链见 §3.4–3.5。以隔离服务器补验失败副作用、目标实例选择和客户端住所同步。
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

### Blueprint 与 build_diff 已验证范围（2026-11-19）

本阶段单独发布只读能力 `server_ai_build_diff_v1`。蓝图按绝对 Tile 原点、相对条目和运行时解析的物体占地计量，合计 1–256 项，完整占地每轴最多 64 格。同一前景层的占地重叠会拒绝整个请求，背景墙可与前景共格。每个差异条目对应一个逻辑目标；`plannedSupportCandidate` 表示蓝图包含可能提供支撑的目标，该项仍保留当前支撑不足状态。

定向验证使用 `tests/test_build_diff.py`、`ServerAiReadProtocolTests.Run()` 与 `tests/verify_server_ai_native.ps1 -BuildDiff`。在目标版本的隔离 Dedicated Server 中，经原始 IPC 和 Lua Worker 比较了 8×8 范围内的 28 个前景目标、36 个背景目标和 1 个工作台，返回 65 项。工作台占地 2×1，预算计 1 件；重复前景目标被拒绝。Lua 执行两次查询、零动作。该场景只验收差异查询；房屋施工、住宅有效性、向导入住及真实模型表现仍待后续验证。

### build_place 已验证范围（2026-11-19）

`server_ai_build_place_v1` 开放 `submit_task` 的 `build_place` 操作，参数为与差异查询相同的 `blueprint`。施工复用 Blueprint 解析、逐项目标观察、材料账本、带库存令牌的持用操作与 `place_item_native` 子动作。任务依次处理前景、背景、家具；暂缺支撑的目标可以在其他目标放置后重新观察。每个目标最多发起一次放置。施工接近仅使用原生移动，无法接近时记录未完成原因。提交相同蓝图会建立新任务，重新判断每个目标。

在目标版本隔离 Dedicated Server 上运行 `tests/verify_server_ai_native.ps1 -BuildPlace`：

- **部分施工**：两项目标中一格放置成功、消耗一块木材，另一格保留原有泥土。终态 `failed / build_partial`，证据 `outcome=partial`。
- **取消后续建**：四格石块施工中，实测一格完成后取消，终态确认输入释放。通过 `submit_task` 重新提交同一蓝图，`await_task` 核验一项 `matched`、三项 `placed`，两次施工合计消耗四块。
- **完工后重提**：四项均为 `matched`，材料余量保持不变。

以上实测使用前景方块，覆盖逐项原生效果、部分施工、取消和重提。住宅施工、各类家具及背景墙组合施工、复杂地形接近和真实模型建房仍待验证。

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
