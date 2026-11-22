# Terraria 原生机制一致性审计：货币支付修正记录

本文记录 2026-11-20 对普通商人购买、护士治疗和哥布林重铸的原生货币支付修正。目标版本为 Terraria 1.4.4.9、tModLoader 2026.09.3.1 Stable。

## 发现的问题

早期实现将随身背包中的铜币作为可支付余额。目标版本 Terraria 的普通货币路径会通过 `Player.CanAfford(price, -1)` 和 `Player.BuyItem(price, -1)` 处理随身及四个银行存储。

因此，随身余额不足但银行余额足够的角色会被错误拒绝；交易后银行发生合法扣款也可能被旧的 `banksUnchanged` 条件误判。

## 修正内容

- MerchantShop、NurseService 和 GoblinReforge 使用原生支付能力判断与扣款。
- 报价记录各来源余额，执行时核对实际扣款和服务效果。
- Python 结果契约改为核对支付前后余额变化。
- 更新相关测试、API 描述和 NPC 经济 Skill。

## 验证依据

目标版本 `tModLoader.dll` SHA-256：

`BF2CA4D53512D064FAE664170A0D21ECC5E2ED51B5E917D871EC78CC449263E9`

原生 IL 对照、隔离 Dedicated Server 实测、Building/Housing/NPC 服务审计及后续发现详见 [原生机制一致性审计报告](NATIVE_MECHANISM_PARITY_AUDIT_REPORT.md)。

## 当前实现入口

- `CompanionBridge/ServerIpc/MerchantShop.cs`
- `CompanionBridge/ServerIpc/NurseService.cs`
- `CompanionBridge/ServerIpc/GoblinReforge.cs`
- `python/api_result_contracts.py`
- `python/atomic_operations.py`

当前支付行为以这些实现及 [世界交互](TERRARIA_WORLD_INTERACTION_DESIGN.md) 为准。本文件保留修正背景，不再作为待执行审计任务。
