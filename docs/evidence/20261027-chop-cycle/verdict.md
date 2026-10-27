# 结论：20261027-chop-cycle

## 结果：基础采集闭环全部通过

| 步骤 | 结果 | 关键回执 |
| --- | --- | --- |
| 出生三件套 | ✅ | 槽0 铜短剑、槽1 铜镐(pick 35)、槽2 铜斧(axe 7) |
| 铜斧砍树 `(2103,210)` | ✅ | `toolKind=axe`、`axe=7`、`Ticks=274`、`Swings=9`、`Strikes=13`、`PeakDamage=104`、`nativeDestroyed=true`、`destroyedByActor=true` |
| 树格消失 | ✅ | 同格 `hasTile=false`、`content=null` |
| 木头掉落 | ✅ | `scan_entities` 找到 `Terraria/Wood ×2`，距 AI 66px |
| 走过去自动拾取 | ✅ | 走到 `x=33675.117` 后背包出现 `Wood ×2`，附近掉落物清空 |
| 铜斧 `use_item` | ✅ | `itemType=3506`、`Ticks=30`、`Starts=1`、`cycleVerified=true`、`effectStatus=unconfirmed`（空挥不证明砍到东西） |
| 铜镐挖土 `(2106,210)` | ✅ | `toolKind=pickaxe`、`pick=35`、`Ticks=31`、`Swings=1`、`Strikes=2`、`PeakDamage=105`；背包 +`DirtBlock` |

## 卡点

1. **第一次 `OutOfRange`**：目标树 `(2103,210)` 距离超过 80px 原生 reach，`mine_tile` 直接拒绝、不自动走近。走近 30px 后成功。
2. 成功动作的 `reason` 是 `"None: "`，没有可读的成功原因。
3. 斧砍树 `Ticks=274 / Swings=9`，镐挖土 `Ticks=31 / Swings=1`；砍树明显更慢，但都能在一次程序内完成。

## 小观察

- `mine_tile` 现在回执里带 `toolKind`/`pick`/`axe`，能直接看出用了什么工具。
- 拾取是走过去后自动发生的，没有独立回执；只能从背包变化和掉落物消失推断。
- 感知结构（`inventory` 字段、空槽、外层信封）较上一版没有明显变化。

## 证据

- `lua/r6.lua`–`r12.lua`
- `receipts/r6.json`–`r12.json`
