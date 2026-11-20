# Native Placement Survey & Building Parity

**状态：** Implementation Spec / 待 Codex 实现  
**代码基线：** codex/unified-autonomous-navigation，060e1a5cce7e1bb0b8421fc6a4882d4083c7b6f0  
**目标环境：** Terraria 1.4.4.9 + tModLoader 2026.09.3.1 Stable  
**参考设计：** Numen 1.21.1 的 BuildSurvey、BuildCellRules、BuildPlacer、BuildOutstanding

## 1. Objective

修复 Terraria 建筑系统中“自定义预检查比原生放置规则更严格、不同模块判断不一致”的问题。

让 build_diff、build_place、place_item 共用一致的放置调查和分类规则，并通过目标版本的原生行为验证。

重点解决：

- 原生放置可以自动切除的草丛/植物被误判为占用。
- 对象占地中的可替换 Tile 被误判为阻挡。
- TileObjectData.LiquidPlace 允许的液体放置被统一拒绝。
- 普通 Tile、Wall、多格 Object 的预检查、执行前检查和结果解释不一致。
- 支撑、锚点、Alternate、Origin、方向和占地的自定义启发式可能与原生行为不符。

目标不是保证所有目标都能在只读阶段被精确预测。只读 Survey 无法安全确定时，应返回明确的 unknown / native_attempt_required，而不是用自定义规则错误拒绝；实际放置仍由 Terraria 原生 Player.ItemCheck() 裁决。

本任务不增加新的建筑玩法，不重构整个施工任务，也不以性能优化为目标。

## 2. Current Implementation and Confirmed Findings

### 2.1 已确认的代码问题

当前 BuildDiff.Inspect：

- 对普通 Tile 目标使用 tile.HasTile 直接返回 occupied_requires_dig。
- 对多格 Object 的占地使用 cell.HasTile 直接返回 object_footprint_blocked。
- 对所有目标统一使用 LiquidAmount > 0 返回 liquid。

这些检查没有完整考虑目标版本原生的可切除/可替换 Tile 和物体液体放置属性。

当前 ActorPlacement.Prepare 的普通 Tile 路径也直接使用 Main.tile[x,y].HasTile 返回 target_occupied，可能在 Player.ItemCheck() 之前拦截原生可处理的植物。

当前 ActorObjectPlacement.Blocks(Tile) 已复用 WorkbenchFootprint.Blocks，并考虑：

- Main.tileCut[t.TileType]
- TileID.Sets.BreakableWhenPlacing[t.TileType]

但 BuildDiff 没有统一使用这一规则。

### 2.2 关键原生机制

目标版本的实际程序集/IL 必须作为最终依据。审计报告已记录：

- TileObject.CanPlace 的对象检查路径会考虑物体定义、锚点、Alternate、液体放置及可切除/可替换占位。
- Player.ItemCheck() 最终进入 Terraria 原生放置逻辑；普通 Tile 和多格 Object 的放置条件并不完全相同。
- Main.tileCut 与 TileID.Sets.BreakableWhenPlacing 是原生放置可能清除占位 Tile 的重要条件。
- TileObjectData.LiquidPlace 决定具体对象对液体的放置要求。

注意区分：
- 地表草皮是实体地形 Tile。
- 草丛/植物是可能可切除的独立 Tile。
- 南瓜植株是需要按其原生工具/武器规则采收的作物，不应仅因位于对象占地就当作可替换草丛。

相关审计：docs/NATIVE_MECHANISM_PARITY_AUDIT_REPORT.md。

## 3. Design

### 3.1 建立统一 Placement Survey / Rules

新增一个集中式放置调查/规则模块（名称由实现者按现有代码风格决定，例如 NativePlacementSurvey 或 PlacementRules）。

它至少要接收：

- 目标 Actor / Player。
- 当前目标 Item。
- 目标层：Tile、Wall 或 Object。
- 目标点击坐标。
- 当前 Tile / Wall / Liquid 快照。
- Object 的原生 TileObjectData 定义、Style、Origin、方向和 Alternate 候选。

输出结构化结果，建议至少区分：

- already_matches：目标已经符合蓝图。
- ready：当前 Survey 有足够原生依据认为可尝试放置。
- native_attempt_required：只读检查无法可靠模拟最终 ItemCheck；允许施工任务尝试一次原生放置，由原生结果裁决。
- native_replaceable：占位属于目标版本原生放置可切除/替换的类型，不应要求 Agent 先挖掘。
- requires_mining：目标被非原生可替换 Tile 占用，需 Agent 决定是否使用现有挖掘能力。
- missing_support_or_anchor：根据目标版本原生物体定义，确实缺少所需锚点/支撑。
- blocked_by_player：玩家碰撞阻挡。
- protected_or_occupied：箱子、家具或其他不可自动替换对象占用。
- unsupported_content：当前适配范围不支持。
- unknown：地形未加载、数据不完整或规则无法安全判断。

可以按现有 API 契约映射名称，但必须保留“可原生替换”“需要 Agent 先挖掘”“只读无法确定”之间的语义区别。

### 3.2 原生预检查边界

不要假设 Terraria 存在一个可以无副作用地完整模拟所有 Player.ItemCheck() 分支的通用 API。

实现前先检查目标 DLL / IL，确认每类物品是否存在安全的原生只读判定：

- 多格 Object：优先使用 TileObject.CanPlace 及目标物品对应的所有合法 Alternate/方向/Style 数据。
- 普通 Tile：根据目标版本 Player.ItemCheck / PlaceThing_Tiles 的实际逻辑，使用 Main.tileCut、BreakableWhenPlacing 和物品元数据做最小必要分类。
- Wall：按原生 Wall 放置规则单独检查，不能与前景 Tile 的占位规则混用。
- Liquid：根据物体的 LiquidPlace 和原生普通 Tile 放置分支决定；不得对所有目标统一拒绝液体。

若无法安全模拟最终放置结果，返回 native_attempt_required / unknown。不要为了得到一个看似精确的布尔值而重写 Terraria 全部放置规则。

### 3.3 可替换 Tile 规则

统一使用一个 helper 判断目标 Tile 是否可被当前原生放置操作处理。

- 对照目标版本 IL，确认 Main.tileCut 与 TileID.Sets.BreakableWhenPlacing 在普通 Tile 和 Object 放置路径中的确切语义。
- BuildDiff、ActorPlacement.Prepare、ActorObjectPlacement.Preflight 必须复用同一 helper，不能各自复制 HasTile 判断。
- 原生可替换的植物应标为可尝试原生放置；不要求 Agent 先调用 mine_target。
- 地表草皮等实体 Tile 不能因名字含“草”就被视为可切除植物。
- 南瓜等非原生放置可替换作物应返回具体的 requires_mining 目标；不在本任务中自动采收或自动破坏。
- 箱子、家具、玩家建筑等受保护对象不得被通用可替换规则误伤。必须验证目标版本对这些对象的实际标记。

实际施工时不应先手工 KillTile 清除植物。应让 Player.ItemCheck() 执行原生清除和放置，随后核验植物消失、目标 Tile/Object 完整出现及物品消耗。

### 3.4 Object / Furniture 规则

- 以物品对应的 TileObjectData 为基础解析 Width、Height、Origin、Style、CoordinateHeights、Anchor 和 Alternate。
- 不能只使用主定义的 Origin/尺寸来断言所有 Alternate 的占地。
- 对每个合法 Alternate/方向计算实际占地，并按原生 TileObject.CanPlace 结果确定候选；若无法唯一确定，返回多个候选或 unknown，不猜测。
- 对象占地中的 Tile 应逐格使用统一可替换规则，不得仅凭 HasTile 拒绝。
- 对象液体规则使用实际 Object Data 的 LiquidPlace。
- 支撑判断优先使用原生 TileObject.CanPlace / Anchor 数据；删除“任意相邻 Tile 就算支撑”这类通用启发式。
- 已有家具、箱子和玩家实体碰撞仍应作为独立阻挡原因。
- 完成后继续使用现有原生 Player.ItemCheck() 和完整 Frame/占地/消耗核验。

### 3.5 普通 Tile / Wall 规则

- 普通 Tile 与 Wall 分层判断，不能共享同一个 HasTile 语义。
- 普通 Tile 不能因为目标格 HasTile 就一律拒绝；必须区分已经匹配、原生可替换、需先挖掘和未知。
- 不要把普通 Tile 的相邻格存在任意 Tile 当作通用支撑规则。按目标版本原生放置机制确认普通 Tile 是否需要支撑，以及半砖、斜坡、致动状态的影响。
- Wall 的已有墙、前景 Tile、背景墙和液体必须按 Wall 原生行为分别处理。
- 本任务不扩大背景墙拆除能力；若某种 Wall 需要先拆除，报告为阻塞原因即可。

### 3.6 Shared Consumers

统一 Survey/Rules 必须成为以下路径的共同来源：

1. BuildDiff.Inspect：只读返回 Survey 分类。
2. BuildPlaceTask：按同一分类决定施工、延迟、需要 Agent 清理或停止。
3. ActorPlacement.Prepare：不再用更严格的 HasTile 规则拦截原生可处理的目标。
4. ActorObjectPlacement.Preflight：复用同一对象占地、可替换 Tile、液体和 Alternate 判定。
5. place_item 的最终执行仍由原生 Player.ItemCheck() 决定。

Survey 的 ready 只是预判，不是成功承诺。实际动作必须核验原生放置事件、目标状态、物品消耗和输入释放。

### 3.7 Failure Evidence and Recovery

- 对 requires_mining 返回准确世界坐标、Tile 类型和目标占地/锚点，方便 Agent 调用现有挖掘工具。
- 对 native_attempt_required，施工任务最多尝试一次原生放置；失败后读取新鲜世界状态并返回具体结果。
- 对 unknown 不盲目重放；先重新观察。
- 原生放置清除植物但未成功放置时，结果必须如实记录为部分副作用/unknown，并重新 Diff；不能假定原位置仍未变化。
- 保留当前 Actor/World/Generation/Slot 绑定、任务取消、输入释放和一次性动作语义。

## 4. Implementation Steps

1. 使用锁定的 Terraria 1.4.4.9 / tModLoader 2026.09.3.1 程序集和 IL，逐项确认普通 Tile、Object、Wall 的原生放置前置条件；记录方法和关键分支。
2. 实现统一 Placement Survey / Rules 和结构化结果类型。
3. 将可替换 Tile、液体、Object Alternate/Origin/Anchor 规则集中实现。
4. 修改 BuildDiff.Inspect 使用统一 Survey，移除已被原生规则取代的 blanket HasTile / blanket liquid rejection。
5. 修改 ActorPlacement.Prepare 和 ActorObjectPlacement.Preflight 使用同一规则，避免预检查提前拦截原生可处理的植物/液体。
6. 修改 BuildPlaceTask 对 ready、native_attempt_required、requires_mining、deferred 和 unknown 的处理；不要自动挖除非可替换障碍。
7. 增加专门的 Native Placement Parity fixtures 和测试。
8. 更新 docs/SERVER_AI_OBJECT_PLACEMENT.md、G2 实现文档和 docs/NATIVE_MECHANISM_PARITY_AUDIT_REPORT.md，记录确认支持与未验证边界。

预期主要修改范围：

- CompanionBridge/ServerIpc/BuildDiff.cs
- CompanionBridge/ServerIpc/BuildPlaceTask.cs
- CompanionBridge/ServerPlayerRuntime/ActorPlacement.cs
- CompanionBridge/ServerPlayerRuntime/ActorObjectPlacement.cs
- 新增统一 Placement Survey / Rules 文件
- tests/native_build_diff.py
- tests/native_build_place.py
- 新增或扩展 C# / Dedicated Server placement parity tests
- 对应专题文档与审计报告

不要预设必须修改所有这些文件；以实际调用链为准，保持最小改动。

## 5. Testing

### A. Pure / Contract Tests

- 相同目标输入到 BuildDiff 和执行前 Survey 时，得到一致的分类。
- BuildDiff 只读，不改变 Tile、Wall、液体、物品或 NPC 状态。
- 目标内容、坐标、Alternate 或世界代次变化后，旧 Survey 结果不能直接复用。
- unknown 不被转换成 blocked 或 ready。
- protected chest/furniture 不被误分类为可切除植物。

### B. Dedicated Server Native Placement Parity

每个场景使用隔离世界/复制 Fixture。先运行只读 Survey，再在独立等价 Fixture 上执行真实原生 Player.ItemCheck()，比较预测与实际结果。

至少覆盖：

1. 普通 Tile 目标格为空：原生放置成功。
2. 目标格有 Main.tileCut 植物：Survey 不应要求先挖掘；ItemCheck 应按原生行为切除并放置。
3. 目标格有 BreakableWhenPlacing Tile：按原生行为验证。
4. 地表草皮（实体地形 Tile）：不得误当作草丛；结果按原生规则分类。
5. 南瓜植株：不得被可替换规则误判；需要时返回 requires_mining，施工任务不得擅自破坏。
6. 椅子、门、床等多格 Object 的占地内有可切除植物：Survey 不应直接返回 object_footprint_blocked；实际 ItemCheck 与完整 Frame 核验通过。
7. 多格 Object 占地内有不可替换实心 Tile：按原生行为拒绝或要求挖掘。
8. Object Data 允许的液体类型/状态：不得被 blanket liquid 检查拒绝。
9. Object Data 不允许的液体类型/状态：按原生行为拒绝。
10. 不同 Alternate、方向、Origin 和占地尺寸。
11. AnchorBottom/Top/Left/Right、平台、绳索、半砖、斜坡和致动 Tile。
12. 玩家碰撞、已有箱子/家具和保护对象。
13. Wall 与前景 Tile 同格、已有 Wall、空 Wall 目标及液体环境。

每个可执行场景都核验：

- 原生放置事件。
- 目标 Tile/Object 的完整类型、Frame、Style 和占地。
- 植物/可替换 Tile 是否按原生机制被清除。
- 物品实际消耗数量。
- 输入释放。
- 未发生非预期的箱子、家具或其他 Tile 破坏。

### C. Regression

- 现有 Work Bench 放置测试继续通过。
- G2 BuildDiff、BuildPlace 的部分施工、取消、续建和重复提交测试继续通过。
- Housing/NPC Home 原生回归继续通过。
- git diff --check 通过。
- 目标版本构建无新增错误/警告。

### D. Host & Play / Real Model

在环境可用时，用真实模型重新执行一次此前 13–14 分钟的住宅建造任务，特别观察草丛、南瓜、门/椅占地和液体场景。

记录：
- BuildDiff 对各目标的分类。
- 实际 ItemCheck 的成功/失败。
- 返工和 mine_target 次数。
- MCP 往返和 build_place 耗时。
- Housing 最终结果。

本任务不以缩短总耗时作为代码验收门槛；先证明放置判断与原生行为一致。真实模型测试未运行时明确标记未执行。

## 6. Deliverables

1. 统一 Placement Survey / Rules。
2. BuildDiff、BuildPlaceTask、ActorPlacement、ActorObjectPlacement 使用一致规则。
3. Dedicated Server 原生放置差异测试及可复现命令。
4. 更新后的放置专题和 Native Mechanism Parity 审计报告。
5. 明确记录仍然 unknown / unsupported 的放置类型。
6. Git Commit，列出目标版本 IL 依据、测试命令、结果及未执行项。

## 7. Out of Scope

- 不新增建筑风格、自动基地设计或新家具种类。
- 不自动清理南瓜、普通实心方块、箱子或玩家建筑；这些由 Agent 决定是否调用现有挖掘能力。
- 不绕过 Player.ItemCheck()，不直接写 Tile，不以 WorldGen.PlaceTile 或 TileObject.Place 代替玩家放置。
- 不在本任务中重写导航、库存、任务运行时或 NPC 系统。
- 不以批量放置速度优化替代原生兼容性修复。
- 暂不创建新的 Gameplay Skills。
