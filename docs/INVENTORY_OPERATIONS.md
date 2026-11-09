# 背包操作

Server AI 背包操作只修改绑定 AI Player 的普通库存槽。读写均在游戏线程执行，并遵循 World、Actor Generation、Slot 和物品实例身份约束。

## 观察

通过 `inspect_inventory` 明确指定模式：

- `selected`：当前选中槽。
- `hotbar`：快捷栏 0..9。
- `slots`：指定 `player.inventory` 内部槽位，最多 16 个。
- `item`：按精确 contentKey 或本地名称搜索。
- `full`：检查普通库存、原版装备/社交/染料/misc 槽和活跃 Buff。

回执中的 `inventoryScope` 和 `inspectedSlots` 说明实际检查范围。局部模式只报告对应库存槽。`full` 的库存范围排除银行、箱子、鼠标持物和任意 Mod 自定义存储。快捷栏按游戏显示键 1..9、0 表示，内部数组索引按 0 起算。

Server AI 的 `full` 和 `get_player_state` 额外返回 `functionalEquipmentSlots`，包含原版头、胸、腿、功能饰品，以及标准 tModLoader Mod 功能饰品槽。每项有稳定 `slotId`、启用和占用状态、基础物品信息及 `targetToken`。空槽的 `item` 为 null，仍提供 Token。

## 操作

| 操作 | 行为 |
| --- | --- |
| `transfer_inventory_item` | 在普通库存槽 0..49 间搬运指定数量；空槽可拆分，同类目标可堆叠，容量不足时整项拒绝 |
| `swap_inventory_slots` | 交换两个槽的完整物品实例，不合并、不丢弃 |
| `hold_inventory_item` | 将完整物品交换到指定快捷栏键，原目标物品回到来源槽，再选择该键；同槽时只选择 |

前三项只在普通背包槽间操作。装备操作通过独立能力 `server_ai_equipment_v1` 开放。

| 操作 | 行为 |
| --- | --- |
| `equip_item({sourceSlot, sourceToken, targetSlotId, targetToken})` | 用 `slots` 模式读来源背包 Token，用功能装备列表读目标 Token。兼容时穿戴，已有装备原样返回来源槽 |
| `unequip_item({targetSlotId, targetToken, destinationSlot?})` | 卸下并送到指定空背包槽，省略时在变更前选定首个空槽 |

装备操作遵循原生槽位及饰品冲突规则。背包限制为 0..49。当前装备配置索引纳入目标 Token，切换配置后须重新观察。装备和背包槽的物品实例、数量和基础字段必须在后续游戏更新核验成功。

## Token 与过期保护

变更前必须从当前观察取得来源槽和目标槽的 Token。Server AI Token 绑定 World、Actor Generation、Player/Slot 身份、库存槽位和物品基础实例信息（type、stack、prefix、maxStack、favorited 等），用于拒绝旧快照或身份变化。

Token 使用 core-only 表示，不调用 ItemIO/TagIO/SaveData 等可能触发 Mod 序列化钩子的路径。因此它不能检测仅由 Mod 自定义 Item 状态发生的变化；这不是完整物品相等证明。执行时仍调用 Terraria 原生 CanStack、StackItems、TransferWithLimit 和相关钩子。

## 结果语义

- 执行前拒绝：未取得输入或未开始变更，返回明确原因。
- 完成：必须核验目标槽/来源槽的实例与数量，并经过下一游戏更新确认。
- 部分变更、原生钩子异常、身份变化或无法确认实际结果：返回 unknown，保留实际副作用，不假设回滚。
- unknown 后不得自动重试；先重新观察并由 Agent 决定后续策略。
- 取消/断连时，先按原动作生命周期释放输入并读取结束证据。释放输入不证明库存目标达成。

## 代码入口

- Python Schema/分发：`python/atomic_operations.py`、`python/server_ai.py`
- Server AI 库存 Token 与操作：`CompanionBridge/ServerIpc/`
- 原生物品/库存执行：`CompanionBridge/ServerPlayerRuntime/`
