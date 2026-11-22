> **历史阶段记录**：本文保留对应阶段的设计、实现过程与当时验证结果。当前导航架构见 [NAVIGATION.md](NAVIGATION.md)。本文中的代码基线、待办和测试状态属于记录时点。

# Phase 4.8：装备管理实现说明 v0.1

**配套设计：** `PHASE4_8_EQUIPMENT_MANAGEMENT_DESIGN_v0.1.md`\
**代码基线：** `codex/unified-autonomous-navigation`，`6f97756`

## 1. 当前代码基础

-   `CompanionBridge/Common/Systems/ObservationReader.cs`
    -   `ReadPlayer()` 已输出
        `equipment`、`dyes`、`miscEquipment`、`miscDyes`。
    -   当前 `equipmentScope` 明确注明不包含 Mod accessory slots。
-   `CompanionBridge/ServerPlayerRuntime/ActorInventory.cs`
    -   当前只支持
        `transfer_inventory_item`、`swap_inventory_slots`、`hold_inventory_item`。
    -   已有 Actor/World/Generation 绑定、库存
        Token、变更后下一游戏更新确认及 `unknown` 语义。
-   `docs/INVENTORY_OPERATIONS.md`
    -   描述现有背包观察、Token 和操作契约。

## 2. 参考代码

-   TerraBot `BotEquipmentActions.Exchange()`：使用 `ItemSlot.EquipSwap`
    交换原版装备。
-   TerraBot `BotModEquipment.cs`：使用
    `ModAccessorySlotPlayer`、`AccessorySlotLoader` 处理标准 Mod
    饰品槽，并检查槽位启用、物品接收规则和饰品冲突。其 UI 模拟会临时改写
    `Main.mouseItem`、键盘和鼠标状态，不应直接移植到 Server AI Actor。
-   Numen `Wardrobe.java` /
    `GearApi.java`：集中装备槽操作；替换时保留旧装备，无空间时拒绝。

## 3. 实施步骤

### A. 完善装备观察

-   保留现有观察字段。
-   增加标准化功能装备槽列表，使用稳定 `slotId`。
-   区分原版盔甲、原版功能饰品与标准 `ModAccessorySlot` 功能饰品。
-   明确报告槽位启用状态和当前物品。
-   不把 Vanity、Dye、misc 或任意 Mod 自定义存储混入功能装备列表。

### B. 核实原生装备交换路径

在两个目标 tModLoader 版本检查：

-   `ItemSlot.EquipSwap` 的实际签名、调用副作用和对 `Main.CurrentPlayer`
    / `Main.LocalPlayer` 的依赖。
-   原版盔甲与功能饰品槽的兼容性检查路径。
-   标准 `ModAccessorySlot` 的槽位存储、`AccessorySlotLoader` 检查
    API、`CanAcceptItem`、冲突检查、启用状态和同步路径。
-   Mod hook 是否会读取 `Main.CurrentPlayer`，以及如何在 Actor
    上下文中安全执行。

优先采用原生装备规则；根据实际 API 选择 Actor 专用适配。不要模拟鼠标 UI
点击，不要改写 `Main.mouseItem` 或键盘/鼠标全局状态。

### C. 实现装备操作

在现有 Actor 库存操作架构中增加：

-   `equip_item`：来源库存槽 + 来源 Token + 目标装备 `slotId`。
-   `unequip_item`：目标装备 `slotId` + 目标 Token + 可选目的库存槽。

替换时优先以来源库存槽承接旧装备，形成原子交换。卸下时要求空库存槽；若自动选槽，必须先确定并记录实际目标槽。

使用原生检查验证物品适配、槽位启用和饰品冲突。执行前后核对 Actor
身份、来源/目标槽位、物品实例和数量。

### D. 同步与确认

-   使用当前项目适用于 Server AI Actor 的原生玩家/ModPlayer 同步路径。
-   不假设普通库存同步等同于 ModAccessorySlot 同步。
-   核验装备与库存真实变化，并在后续游戏更新确认。
-   原生 hook 异常、部分变化、Actor 失效或同步后状态不一致时返回
    `unknown`；不自动回滚或重试。

## 4. 定向测试

-   原版头盔、胸甲、护腿：空槽穿戴、占用槽替换、卸下。
-   原版功能饰品：穿戴、替换、冲突拒绝、锁定/未解锁槽拒绝。
-   标准 ModAccessorySlot：观察、穿戴、替换、卸下、自定义
    `CanAcceptItem` 拒绝、禁用状态。
-   背包满、指定目的槽被占用、来源/目标 Token 过期、装备槽在观察后变化。
-   Mod hook 异常或在检查中改变状态。
-   取消、Actor generation 失效、World 切换、死亡/不可操作状态。
-   验证其他玩家装备、`Main.myPlayer`、`Main.mouseItem`
    和鼠标/键盘状态不受影响。
-   双版本构建及既有背包、IPC、Task Runtime、Actor 生命周期回归。

## 5. 完成标准

-   观察覆盖原版功能装备与标准 Mod 功能饰品槽。
-   `equip_item`、`unequip_item` 可可靠完成并核验结果。
-   替换和卸下不会丢失或丢弃旧装备。
-   Mod 槽位规则通过原生机制验证。
-   两个目标 tModLoader 版本构建及相关回归通过。

## 6. 实现记录

Server AI 的 `inspect_inventory({mode="full"})` 与 `get_player_state` 新增 `functionalEquipmentSlots`。原有数组与库存查询模式保留。三个盔甲槽采用 `vanilla:head`、`vanilla:body`、`vanilla:legs`，七个原版功能饰品槽采用 `vanilla:accessory:1` 至 `vanilla:accessory:7`。标准 Mod 功能饰品用 `mod:<ModType.FullName>`，只枚举引擎已加载的槽位。槽位的可用状态使用原生解锁和 Mod 规则，禁用槽保留观察。当前装备配置、World、Actor、generation、槽位及物品实例和基础字段均纳入装备 Token。

### 原生机制与接入

- 两版固定引擎的 `ItemSlot.EquipSwap(Item, Item[], int, ref bool)`、饰品冲突检查、Mod 槽位访问和同步相关 IL 一致。原生 `EquipSwap` 会清除 incoming 的收藏标记、克隆双方物品、播放 UI 音效并调用 `Recipe.FindRecipes`。Server AI 使用原生规则校验后交换两个已有的 Item 引用，保留实例、收藏标记和 Mod 对象。
- 原版槽位使用 `Player.IsItemSlotUnlockedAndUsable`，盔甲依照原生装备槽标记检查，饰品使用接收显式 Player 的 `ItemSlot.AccCheck_ForPlayer`。检查的临时布局包括原版和 Mod 饰品，排除即将替换的目标物品。固定引擎的 `MaxVanillaSlotCount` 是七个饰品的数量，观察索引从盔甲之后开始。
- Mod 功能槽通过 `AccessorySlotLoader.Get(index, player)` 枚举，在 Actor 上下文中使用 `ModdedIsSpecificItemSlotUnlockedAndUsable` 判定可用性。`DrawFunctionalSlot` 只控制 UI 绘制。原生 `IsAccessoryInConflict` 会调用槽位的 `CanAcceptItem` 并进入带 Actor 的饰品检查，含 `ItemLoader.CanEquipAccessory` 与冲突钩子。公开 `FunctionalItem` 属性访问 Actor 的原生功能饰品存储。原生 `Main.CurrentPlayerOverride` 作用域绑定并恢复 `CurrentPlayer`。
- `equip_item` 在设计建议的参数上增加必填 `targetToken`，空槽也要传。只有同时确认来源和目标的观察仍然有效才能替换。`unequip_item` 接受可选空背包槽，未指定时先确定首个可用槽。两项操作以 `server_ai_equipment_v1` 握手，复用 Actor 动作租约、游戏线程、IPC 和 Lua 回执。
- 原生 `SyncOnePlayer` 同步库存与原版装备，并调用 `PlayerLoader.SyncPlayer`，其中标准 `ModAccessorySlotPlayer.SyncPlayer` 使用自己的槽位包。同步前后及后续游戏更新检查物品实例、基础字段、相关槽位、当前装备配置和 Actor 绑定。钩子异常或结果不一致返回 `unknown`，沿用会话栅栏，保留真实副作用。

装备 Token 继续只覆盖基础字段和实例身份。第三方自定义物品状态、非标准装备存储、图形客户端与多人接收侧显示属于未验证边界。

### 实际验证

| 检查 | 结果 |
| --- | --- |
| tModLoader `2026.09.3.1`、`2026.08.3.0` | 均零错误、零警告构建 |
| 双引擎 `verify_server_ai_direct_input_native.ps1 -Equipment` | 每版 34 个 IPC/Lua 场景及 6 个生命周期用例通过。包括原版三类盔甲、原版与标准 Mod 饰品、Mod 物品钩子、自定义拒绝、跨槽冲突、隐藏但启用的功能槽可穿戴和卸下、禁用槽拒绝两项操作、背包满、指定槽占用和失效 Token |
| 异常与隔离 | 双版夹具确认钩子抛错、钩子改变库存和原生同步期间改变库存均返回 `unknown`。钩子内核对 Actor 作用域，跨动作核对其他 Player 的装备及库存、`Main.myPlayer`、鼠标持物、鼠标和键盘状态。死亡、World、generation 的竞态用模拟绑定变化命中真实动作状态机 |
| 双包 `run_server_ai_ipc_tests.ps1` | IPC、Task Runtime、包级验证通过。每版 92 项 Task Runtime 与 103 项包级 IL 检查 |
| 参数与库存定向回归 | Python/C# 共享契约 209 条向量，3 项装备回执、6 项现有 Python Server AI 库存测试及 C# `-InventoryOnly` 通过 |
| 新版原生 `-Atomic -Actions -Inventory -Placement` | 39 个原生库存场景、7 个 Lua/IPC 库存动作及既有物品使用、放置与动作回归通过 |
| 旧版原生直接输入 | 16 个场景通过，覆盖 Actor 取消、退役、断开连接、死亡和受控输入释放 |
