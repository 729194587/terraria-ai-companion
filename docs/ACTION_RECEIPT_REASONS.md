# 动作回执原因：0.1.52

## 根因和修复

实机 0.1.51 的砍树及挖土回执成功时出现 `reason="None: "`。`ActorMining.Context.Reason` 是**失败原因枚举**；成功结束正确地保留 `Failure.None`、空 Detail。问题出在 `ServerAiNativeActions.Poll()` 无条件使用 `mine.Reason + ": " + mine.Detail` 构造公开原因。`AiAtomicActions`、JSON、Python 原子结果投影和 Lua 随后原样转发，序列化器并未生成该字符串。

适配层现在按动作状态将内部失败枚举映射为稳定公开原因码，成功使用目标结果本身。没有解析、replace 或隐藏旧字符串。物品原生执行器也在结果源头产生稳定代码；导航成功代码保持原样，导航失败/异常的原始诊断保留为详情。

| 状态/场景 | reason 示例 |
|---|---|
| 目标格原生破坏完成 | `native_tile_destroyed` |
| 原生物品周期完成 | `native_item_cycle_verified` |
| 导航成功 | `native_navigation_arrival_verified`（沿用） |
| 选栏成功 | `native_selection_verified_next_pump`（沿用） |
| 无效目标 / 错工具 / 原生保护拒绝 | `invalid_target` / `invalid_tool` / `native_tool_damage_refused` |
| 原生未启动 | `native_use_refused` |
| 取消 / 角色退休 | `explicit_cancel`（外层）或 `explicit_stop`（原生） / `actor_retired` |
| 超时 | `execution_deadline`（外层）或 `native_mining_deadline` / `native_item_timeout`（原生） |
| 释放或效果证据不足 | unknown + `native_inputs_not_released` / `native_effect_unconfirmed` |

`reason` 为稳定原因码；新增可空 `reasonDetail` 单独传递诊断详情，例如原生保护拒绝的坐标/工具力、导航异常消息。C# 终态 JSON 和 Python→Lua 投影均保留它。成功无详情时为空；不是将失败细节删掉或吞进成功原因。外层将结果降为 unknown 时，保留降级原因，原生原因及原有详情进入 reasonDetail，避免 unknown 携带误导性的成功原因。

另修正既有取消竞态：如果原动作已经以充分证据完成且释放，原有契约允许完成胜过取消；此时原因必须跟随已验证的完成结果，不能仍是 explicit_cancel。终态状态、effectVerified、inputsReleased、绑定/代次、actionId、预算、取消执行和 unknown 判定均沿用既有逻辑。物品内部超时的原有 failed 状态不改成另一种状态。

此改变不增加 API 或自动重试，也不把 `native_tile_destroyed` 扩大为木材已入包；回执原因不是新的效果证明。

## 验证和边界

- 定向 C# 回执测试新增成功/失败/取消/unknown 的代码和 JSON 详情验证、失败枚举映射、效果不足降级、输入未释放和取消完成竞态；现有相关协议组继续通过。
- 正式包 51 项检查通过，包含实际 Poll 使用映射和终态序列化保留 ReasonDetail。
- Python `test_atomic_result_contract` **15 项**通过，包含原因/Unicode 诊断详情的 JSON 往返以及原有终态、取消、反馈契约；使用离线模型/游戏替身。
- 0.1.52 构建零错误、零警告；隔离原生的铜斧/镐挖和物品周期检查通过，包括原生挖掘成功原因 `native_tile_destroyed` 和无成功详情。
- 最终包哈希及隔离原生结果见 [本轮验证记录](evidence/ACTION_RECEIPT_REASONS_NATIVE.json)；实机归档用目录级 `.gitattributes` 禁止换行转换，保证 Git 中也保留原始字节。
- 未运行全量测试，未调用外部模型分析服务。0.1.52 尚未部署或进行图形 Host & Play 验收；0.1.51 实机采集证据见 [R2/R3 文档](R2_R3_COPPER_AXE.md)，两者不能混作新版原因码实测。

重验时分别发起一次正常挖掘、错误工具/超距目标和取消，检查公开 reason 为稳定代码、失败诊断在 reasonDetail 中仍可读，且原 effect/release 证据一致。原始 `runs/20261027-chop-cycle/` 不修改；[归档 manifest](evidence/20261027-chop-cycle/manifest.json) 列出逐字节副本哈希。
