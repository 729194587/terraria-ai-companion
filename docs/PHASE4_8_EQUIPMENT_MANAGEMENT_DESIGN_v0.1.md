# Phase 4.8：装备管理设计 v0.1

**状态：** MVP 设计基线\
**代码基线：** `codex/unified-autonomous-navigation`，`6f97756`\
**目标引擎：** tModLoader `2026.09.3.1`、`2026.08.3.0`

## 1. 目标

为 Server AI Actor 提供基础装备管理能力，使 Agent
能观察当前功能装备栏，并从自己的背包穿戴、替换和卸下装备。

本阶段支持：

-   原版头盔、胸甲、护腿。
-   原版功能饰品栏。
-   tModLoader 标准 `ModAccessorySlot` 添加的功能饰品栏。
-   原版或 Mod 提供、且遵循标准饰品装备机制的装备物品。

本阶段不负责自动挑选最优装备。由 Agent 根据观察结果决定穿戴什么。

## 2. 参考实现与借鉴原则

### TerraBot

-   `BotEquipmentActions.Exchange()` 使用 `ItemSlot.EquipSwap`
    交换原版装备，并在 Bot authority 上下文中执行。
-   `BotModEquipment.cs` 通过 `ModAccessorySlotPlayer` 和
    `AccessorySlotLoader` 处理 Mod
    饰品栏，并调用槽位可用性、物品接受规则及饰品冲突检查。
-   TerraBot 的 Mod 装备 UI 路径临时修改
    `Main.mouseItem`、键盘和鼠标状态。Server AI Actor 不应直接照搬这条
    UI 模拟路径。

### Numen

-   `GearApi` 提供独立的 wear/remove 操作。
-   `Wardrobe` 集中处理槽位选择、兼容性和物品交换。
-   替换时旧装备返回背包；无法安全存放时拒绝操作，不丢弃旧装备。

### 本项目采用的原则

复用当前 Actor 背包 Token、World/Generation/Actor 身份校验和 `unknown`
结果语义。使用原生装备规则验证兼容性，但执行路径必须保持 Server AI Actor
隔离，不依赖鼠标 UI 全局状态。

## 3. 装备观察

当前 `ObservationReader.ReadPlayer()` 已输出：

-   `equipment`：`Player.armor`。
-   `dyes`：`Player.dye`。
-   `miscEquipment`：`Player.miscEquips`。
-   `miscDyes`：`Player.miscDyes`。

当前观察明确标注 Mod accessory slots 未包含在内。

本阶段扩展装备观察，新增一个明确区分功能槽位的结构化列表，例如
`functionalEquipmentSlots`。保留既有字段以避免破坏现有消费者。

每个槽位至少提供：

  字段            含义
  --------------- ---------------------------------------
  `slotId`        稳定槽位标识，作为装备操作目标
  `kind`          `head`、`body`、`legs` 或 `accessory`
  `source`        `vanilla` 或 `mod_accessory_slot`
  `displayName`   可读槽位名称
  `enabled`       当前是否可用
  `occupied`      是否已装备物品
  `item`          当前物品的基础 ItemData；空槽为 null

要求：

-   原版功能装备与社交/时装槽明确区分。
-   Mod 槽位使用 Mod 内容全名等稳定标识，不把运行时数组索引作为唯一 ID。
-   Mod 功能饰品槽通过 tModLoader 标准 `ModAccessorySlot` 系统发现。
-   只纳入功能饰品部分；Mod Vanity 和 Dye 部分不属于本 MVP。
-   对未加载或当前不可用的槽位，不得伪装成可装备槽。
-   保留物品 `contentKey`、type、stack、prefix
    等现有基础信息，不尝试解析所有 Mod 的自定义属性。

## 4. 操作接口

### `equip_item`

从 Agent 自己的普通背包穿戴物品到指定功能装备槽。

建议参数：

-   `sourceSlot`：背包内部槽位。
-   `sourceToken`：最近一次观察得到的库存 Token。
-   `targetSlotId`：最近一次装备观察得到的稳定槽位 ID。

行为：

-   执行前重新验证 Actor 身份、库存 Token、目标槽位和当前槽位内容。
-   使用原生装备/饰品兼容性规则检查物品是否适合目标槽。
-   目标槽为空时，将物品放入目标槽。
-   目标槽已有装备时，执行原子交换：旧装备返回原来源背包槽，新装备进入目标槽。
-   不丢弃物品；不支持安全交换时拒绝。
-   成功必须核验来源背包槽和目标装备槽的真实变化，并在后续游戏更新确认。

### `unequip_item`

将指定功能装备槽的物品卸下并放回 Agent 自己的普通背包。

建议参数：

-   `targetSlotId`：装备观察中的稳定槽位 ID。
-   `targetToken`：最近一次观察得到的装备物品 Token。
-   `destinationSlot`：可选的指定空背包槽；未指定时由服务端选择普通背包中的空槽。

行为：

-   目标槽必须仍包含观察时的同一物品实例。
-   目标背包槽必须为空；自动选择时必须在变更前确定空槽。
-   背包没有可用空间时拒绝操作。
-   不丢弃物品，不使用鼠标持物。
-   成功必须核验装备槽清空、物品进入目标背包槽，并在后续游戏更新确认。

## 5. Mod 饰品栏边界

本 MVP 所称的 Mod 额外饰品栏，特指 tModLoader 标准 `ModAccessorySlot`。

这类槽位由 `AccessorySlotLoader` 管理，槽位可以提供功能饰品、Vanity 和
Dye 部分，并通过 `IsEnabled`、`CanAcceptItem`
等机制定义可用性和接收规则。

本阶段支持标准 `ModAccessorySlot`
的功能饰品部分，包括槽位自定义接收规则和饰品冲突检查。

不承诺支持 Mod 自行实现、未接入标准 `ModAccessorySlot`
的自定义装备界面或任意额外存储系统。

实现时需核实目标 tModLoader 固定版本中公开 API、内部存储和 Actor
`Main.CurrentPlayer` 上下文要求。不得因某个槽位 API
依赖当前玩家而错误地对 `Main.myPlayer` 或其他玩家执行操作。

## 6. 安全与结果语义

-   所有读写在游戏线程、当前 Server AI Actor 绑定上下文中执行。
-   校验 World、Actor slot、Generation、Actor 实例及物品 Token。
-   不修改 `Main.myPlayer`、`Main.mouseItem`、鼠标/键盘状态或共享 UI
    目标状态。
-   Mod hook 或原生装备检查执行前后都要检查 Actor
    绑定及相关槽位是否被意外修改。
-   原生钩子抛错、部分变化、同步期间状态变化或无法确认结果时返回
    `unknown`。
-   `unknown` 不自动回滚、不自动重试；Agent 先重新观察。
-   不允许把旧装备或卸下装备丢弃到世界中。

## 7. 明确不做

-   自动比较装备评分或自动配装。
-   自动装备升级或游戏进度策略。
-   Vanity/社交装备、染料、宠物、坐骑、钩爪等 `miscEquips` 槽。
-   Mod 自定义非标准装备栏或任意自定义存储。
-   装备 Loadout 自动切换。
-   读取和推断所有 Mod 装备的特殊效果。

## 8. 验收标准

### 观察

-   Agent 能区分头盔、胸甲、护腿、原版功能饰品槽和标准 Mod 功能饰品槽。
-   能准确报告槽位稳定 ID、是否启用、当前装备和空槽状态。
-   既有 `inspect_inventory` 字段保持兼容。

### 穿戴与卸下

-   能穿戴有效头盔、胸甲、护腿和功能饰品。
-   能替换已有装备，旧装备准确返回来源背包槽。
-   能卸下装备并放入指定或自动选择的空背包槽。
-   错误物品、饰品冲突、禁用槽、过期 Token、背包满等情况安全拒绝。
-   不发生物品丢失、复制或意外掉落。

### Mod 与隔离

-   测试夹具提供标准
    `ModAccessorySlot`，验证观察、穿戴、替换、卸下及自定义拒绝规则。
-   Mod 槽位启用/禁用状态变化能被正确识别。
-   Actor 操作不修改其他玩家装备、`Main.myPlayer` 或鼠标/键盘共享状态。

### 工程回归

-   tModLoader `2026.09.3.1` 和 `2026.08.3.0` 构建通过。
-   现有背包交换、放置、物品使用、Actor 生命周期、IPC 和 Task Runtime
    回归通过。

## 9. 完成定义

Agent 能准确观察自己的原版功能装备和标准 tModLoader Mod
功能饰品槽，并可靠地穿戴、替换和卸下装备。装备选择策略仍由 Agent
自主决定。
