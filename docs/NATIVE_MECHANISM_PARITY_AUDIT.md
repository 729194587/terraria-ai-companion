# Native Mechanism Parity Audit & Currency Correction

**状态：** 定向修复与原生行为审计任务  
**代码基线：** codex/unified-autonomous-navigation，0920b6ce9eef7b170397240a969c605b0cb8a512  
**目标环境：** Terraria 1.4.4.9 + tModLoader 2026.09.3.1 Stable

## 1. Objective

修复最近 G3 NPC 服务中已确认的货币支付偏差，并对近期 Building、Housing、Merchant、Nurse、Reforge 实现进行原生机制一致性审计。

本任务分为两部分：

1. **必须修复：** 商人购买、护士治疗、哥布林重铸必须遵循目标版本 Terraria 原生货币支付规则，包括四个银行存储；同步修正报价、证据契约、测试和 Skill。
2. **必须审计并报告：** 对 G2/G3 新增实现逐项比较原生源码/IL、正常客户端行为和 Server AI 实现。除货币问题及其直接相关契约外，其他发现先报告，不顺手扩大修改范围。

核心原则：测试不能把实现自行添加的限制当作原生规则。每个限制必须有目标版本原生行为依据，或明确标记为产品策略。

## 2. Current Implementation and Confirmed Finding

### 2.1 原生货币支付偏差

当前 MerchantPayment.Coins(items, count) 只统计指定数组。G3 代码多处使用 p.inventory 的前 54 格作为可用资金：

- MerchantShop.Read/Catalog/Buy
- NurseService.Quote/Heal
- GoblinReforge.Quote/Execute

当前表现：

- 随身金币足够：允许服务。
- 随身金币不足、猪猪存钱罐/保险箱/防御者熔炉/虚空保险库有钱：错误拒绝。
- 重铸执行虽然调用 p.CanAfford(price, -1)，但前面仍有 beforeCopper < price 的随身余额门槛。
- 商人购买、护士治疗也调用 p.BuyItem()，但执行前的随身资金检查阻止原生支付逻辑使用银行。

目标版本原生 Player.BuyItem / CanAfford 的支付路径会考虑四个银行存储。应以锁定版本 2026.09.3.1 的实际程序集/IL 再次确认调用细节，禁止只凭当前注释推断。

### 2.2 当前测试和契约也固化了错误行为

需要同步修正：

- tests/native_g3_shop.py：目前把“随身 0 铜、Piggy Bank 有 100 银”作为 insufficient_carried_funds 拒绝用例。
- tests/native_nurse_lua.py：贫穷场景和成功证据按随身余额、银行不变编写。
- tests/native_reforge_lua.py：同样按随身余额拒绝。
- python/api_result_contracts.py：购买、治疗、重铸成功条件要求 banksUnchanged=true。
- python/atomic_operations.py：工具描述将支付限制描述为随身钱币。
- skills/npc_interaction_and_economy/SKILL.md：指导模型交易前只核对随身余额。

因此必须修复完整链路，不能只改一个 C# 判断。

## 3. Design: Native Currency Semantics

### 3.1 原生支付是权威

- 对金币支付，报价和执行前检查使用目标版本原生 Player.CanAfford(price, -1) 或经 IL 确认的等价原生查询。
- 实际扣款必须通过原生 Player.BuyItem(price, -1)。
- 不手工从背包或银行数组扣钱，不自行模拟原生支付优先级、换算和找零。
- 特殊货币仍属于当前未支持范围；不得把 -1 普通钱币规则套用到特殊货币。
- 如果原生支付 API 在绑定 Server AI Player 上不能安全使用，先报告具体阻塞及 IL 证据，不得回退到只检查随身金币并宣称符合原生行为。

### 3.2 报价必须表达真实可支付状态

商人商品列表、购买报价、护士报价和重铸报价应使用原生可支付判断。

返回字段应区分：

- 随身钱币余额。
- 四个银行各自的余额或可审计的资金摘要。
- 原生 CanAfford 结果。
- 商品/服务价格及货币类型。
- 最终拒绝原因。

carriedCopper 可以保留作为观察字段，但不得再作为普通金币服务的唯一资金门槛。拒绝原因应使用 insufficient_funds 等准确语义，不再把银行资金存在但未携带称为真正资金不足。

报价 Token 应绑定与本次报价有关的 Actor、NPC、World、Generation、物品/库存及资金状态。若报价后资金存储发生变化，应重新报价，或在执行前重新验证并按契约拒绝旧报价。

### 3.3 交易后核验必须允许原生银行扣款

购买、治疗和重铸成功时，不再要求 banksUnchanged=true。

应在扣款前后采集：

- 随身钱币总铜值。
- Piggy Bank、Safe、Defender's Forge、Void Vault 各自的钱币总铜值。
- 所有银行中非钱币物品的身份/数量快照。
- 所有支付来源合计的总铜值。

成功核验至少要求：

1. 原生动作确实执行。
2. 支付前后所有支付来源的总铜值净减少恰好等于报价金额。
3. 物品、治疗或重铸效果与报价相符。
4. 除原生支付实际涉及的钱币堆叠外，银行中的非钱币物品未发生意外变化。
5. Actor、NPC、World 和 Generation 身份仍有效。
6. 动作无重复执行，结果不是 unknown。

证据字段可以采用 beforeTotalCopper / afterTotalCopper、beforeCarriedCopper / afterCarriedCopper、beforeBankCopper / afterBankCopper 和 bankNonCurrencyUnchanged。最终命名可按现有结果契约调整，但语义必须清楚。

不要假设原生支付一定先扣随身、再扣某个指定银行；应记录实际变化，不自行规定扣款顺序。

### 3.4 出售必须与购买分开处理

出售是反向资金流：

- 当前 Player.SellItem 路径将所得钱币放入玩家随身库存槽位。
- 出售报价中的随身金币空间检查可能仍然合理，因为它检查的是原生出售所得能否放入随身库存。
- 不要为了统一代码而让出售自动向银行存钱，除非目标版本原生行为本来如此。
- 出售后继续核验物品减少数量和随身钱币净增加额；银行不应被无故修改。

Codex 必须用目标版本源码/IL 确认 SellItem 行为，并在审计报告中解释购买/服务支付与出售所得为何采用不同的资金范围。

## 4. Required Code Changes

重点文件：

- CompanionBridge/ServerIpc/MerchantPayment.cs
- CompanionBridge/ServerIpc/MerchantShop.cs
- CompanionBridge/ServerIpc/NurseService.cs
- CompanionBridge/ServerIpc/GoblinReforge.cs
- python/atomic_operations.py
- python/api_result_contracts.py
- tests/native_g3_shop.py
- tests/native_nurse_lua.py
- tests/native_reforge_lua.py
- tests/test_merchant_shop.py
- tests/test_goblin_reforge.py
- skills/npc_interaction_and_economy/SKILL.md

实现要求：

1. 建立统一的普通货币资金快照/统计辅助逻辑，覆盖随身可参与支付的钱币槽和四个银行。
2. 商人 Catalog/Quote/Buy 使用原生 CanAfford 判断；购买使用原生 BuyItem。
3. 护士 Quote/Heal 使用原生可支付判断和 BuyItem，保留现有治疗量、价格和 Buff 逻辑，除非 IL 审计另有发现。
4. 重铸 Quote/Execute 移除随身余额门槛，使用原生可支付判断和 BuyItem；保留原生前缀生命周期回调。
5. 统一成功证据与 Python verified_* 判定，使银行支付能够被验证，而不是被误判为 unknown。
6. 更新工具说明、结果 Schema、测试 Fixture 和 Skill，使其不再宣称必须携带金币。
7. 保持过期 Token、实体替换、库存变化、支付中断和 unknown 的防重放语义。

## 5. Currency Test Matrix

Dedicated Server 原生测试必须覆盖：

| 场景 | 预期 |
|---|---|
| 随身金币足够 | 成功 |
| 仅 Piggy Bank 有足够金币 | 成功 |
| 仅 Safe 有足够金币 | 成功 |
| 仅 Defender's Forge 有足够金币 | 成功 |
| 仅 Void Vault 有足够金币 | 成功 |
| 随身 + 一个银行合计足够 | 成功 |
| 四个银行与随身合计刚好等于价格 | 成功 |
| 所有支付来源合计不足 | 拒绝，且无任何变化 |
| 报价后银行钱币变化 | 旧报价失效或执行前重新验证 |
| 支付过程中发生异常 | 返回 unknown，不自动重放 |

上述资金场景至少对 Merchant Purchase、Nurse Heal、Goblin Reforge 分别覆盖银行支付；可共享 Fixture，但每个服务必须验证自身原生扣款和业务效果。

核验要求：

- 总支付金额与报价一致。
- 实际扣款来源记录真实。
- 银行非钱币物品不变。
- 不允许因银行支付而要求 banksUnchanged=true。
- 不足额拒绝时，背包和四个银行均无变化。
- 购买物品、治疗结果、重铸物品效果分别正确。

出售单独覆盖：随身钱币空间足够/不足、来源槽 Token 过期、交易后物品和所得钱币变化；不要把银行余额作为出售收入空间。

## 6. Native Mechanism Parity Audit

对以下新增实现逐项进行目标版本源码/IL 与正常客户端行为对照：

### 6.1 Merchant Purchase / Sale

- NPCShop 注册表、商店条件和动态商品。
- OnShopOpen、CanBuyItem、PostBuyItem、Item.OnCreated、Player.GetItem 的调用顺序。
- GetItemExpectedPrice、幸福度价格、折扣和特殊货币边界。
- Player.BuyItem 与 Player.SellItem 的支付/收款来源、堆叠和溢出行为。
- 售出回购列表、sellback credit、Mod Hook 限制是否与客户端语义一致。
- 当前只支持普通 Merchant、普通铜币和单件交易的边界是否准确声明。

### 6.2 Nurse

- 护士治疗量和价格计算与目标版本 GUIChatDrawInner / 原生 Nurse UI 的 IL 对照。
- 进度倍率、Expert 模式、幸福度价格、ModifyNurseHeal、ModifyNursePrice 的调用顺序和舍入方式。
- 原生扣款、生命恢复、可移除 Debuff 和 PostNurseHeal 的行为顺序。
- 银行支付后的效果与客户端一致。
- 不可清除 Debuff、Potion Sickness、满血和零价格治疗边界。

### 6.3 Goblin Reforge

- Prefix(-3) 的资格判断与 UI 一致性。
- ReforgePrice、折扣、幸福度价格和整数舍入顺序。
- BuyItem、PreReforge、ResetPrefix、Prefix(-2)、PostReforge 的顺序。
- 物品身份、前缀、堆叠、收藏状态和 Mod Hook 的行为。
- 银行支付和随机抽回原前缀的正确语义。

### 6.4 Building

- BuildingBlueprint 对 Tile、Wall、多格 Object 的内容解析和坐标语义。
- BuildDiff 对普通 Tile/Wall 的支撑、占位、液体、斜坡/半砖/致动状态判断是否与原生放置一致。
- TileObjectData 的 Origin、Anchor、Alternate、Style 和完整占地判断。
- BuildPlaceTask 的施工顺序、材料选择、导航范围、取消、续建和 unknown 处理。
- BuildingMaterials.available（槽 0–57）与 usable（槽 0–49）统计差异；检查 build_diff 的 materials.available/missing 是否可能把不能用于原生放置的槽位材料计为可用。该问题先在报告中给出复现和建议，不要求与货币修复捆绑重构。
- starter_house Skill 使用的字段是否与实际 build_place 回执一致，尤其核对 remainingByReason 是否存在。

### 6.5 Housing

- WorldGen.StartRoomCheck、RoomNeeds、ScoreRoom 的调用顺序及静态状态字段读取。
- 私有反射字段与锁定引擎版本的兼容保护。
- roomOccupied、roomEvil、Home Tile 和失败原因映射。
- WorldGen.moveRoom 的原生住所分配语义、NPC 类型/slot 参数及同步行为。
- TownRoomManager.HasRoom 和 NPC homeTileX/Y 的一致性。
- 普通 NPC 白名单、Truffle、Town Pets、ModNPC 特殊住宅规则的支持边界。

### 6.6 Audit report format

新增一份审计报告，按以下分类：

- Confirmed parity：已通过目标版本源码/IL 和测试证明一致。
- Confirmed mismatch：有明确原生行为对照，当前实现不同。
- Intentional limitation：有意限制，并且工具/Skill/文档准确声明。
- Unverified：缺少证据，必须补充测试或源码分析。
- Potential issue：存在风险线索，但尚不能断言错误。

每项至少记录：涉及文件/方法、原生行为依据（源码或 IL 位置）、当前实现、差异、影响、最小修复建议和测试证据。

本次只直接修复货币语义及其契约/测试；其他差异形成 follow-up 列表。若发现会导致数据损坏、重复扣费或错误报告成功的高危问题，应先停止相关能力发布并明确报告。

## 7. Validation

按顺序执行：

1. 目标 tModLoader 2026.09.3.1 Stable 源码/IL 核对。
2. Python 定向契约测试。
3. C# 构建和静态检查。
4. Dedicated Server 原生货币矩阵测试。
5. Merchant、Nurse、Reforge 各自的原生业务效果测试。
6. 现有 Building、Housing 原生回归测试。
7. git diff --check。

不要以当前“银行有钱仍拒绝”的测试作为成功标准。测试预期必须根据原生行为更新。

Host & Play 如环境可用则补测；否则明确列为未执行，不用 Dedicated Server 结果代替。

## 8. Deliverables

1. 修复后的货币支付实现与统一资金证据。
2. 更新后的 Python Schema、工具描述、验证函数、测试和 Economy Skill。
3. docs/NATIVE_MECHANISM_PARITY_AUDIT_REPORT.md：完整差异审计报告。
4. 对其他已确认问题的 follow-up 建议，不在本任务中扩大实现范围。
5. 测试命令、结果、未执行项目和 Git Commit。

本任务不扩展 NPC 服务种类，不重构建筑系统，也不创建新的 Gameplay Skills。
