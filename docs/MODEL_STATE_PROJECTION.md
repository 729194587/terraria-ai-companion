# 第一阶段模型可见状态投影

共享 C# ReadPlayer 和原始观察协议保持完整。inspect_inventory 的 full 模式在序列化完整快照后生成独立投影：全部物品数组保留槽位，空槽仅含 slot/empty；非空项目全部字段不变。Buff 仅保留 typeId>0 且 timeTicks>0 的全部活跃项目。buffScope/scope/customState 声明覆盖、排除和未知 Mod 状态。selected/hotbar/item 不变。

Python project_model_observation 先执行 validate_observation，再复制原始结果，仅移除 player 的 inventory/equipment/dyes/miscEquipment/miscDyes/buffs，注明按需调用 inspect_inventory。其余字段全部保留，包括未来增加的安全字段。Bridge 原校验、探索投影、移动闭环和历史回执不变。

## 实际离线测量

Tokenizer: deepseek-ai/DeepSeek-V4.1-Flash，固定 revision
314113f8a7dadb388215396ba5fbee2c610a04d6；tokenizers==0.22.1。
tokenizer.json SHA-256:
c90dfa01249db1be4245780a052ede752e1361c612ac6d08e2bdada7d599476b。

| 完整结果 | UTF-8 字节（前 → 后） | Token（前 → 后） |
| --- | --- | --- |
| inspect_inventory(full) | 49,209 → 21,182 | 20,119 → 8,364 |
| get_observation | 309,604 → 290,333 | 115,966 → 108,474 |

库存来自已有本地真实完整工具消息 .local/token-audit-20261023/g10-inspect_inventory-full.txt；after.data 由生产 C# FullInventory 直接生成，其余外层原样保留。原消息不提交到仓库。观察来自 tests/fixtures/exploration-real-observation.json.gz 的已有游戏录制；前后使用相同的完整 read_observation 外层（固定 receivedAt，controlGeneration/transportMetrics 为 null）。录制样本代表该次数据规模，不保证其他场景的下降比例。

每个结果以 json.dumps(ensure_ascii=False，默认分隔符) 序列化，然后整串 encode(add_special_tokens=False)，没有按字段分别加总。不是聊天模板、服务端缓存或 API 账单实测。可复现计数脚本为 tests/measure_model_state.py，校验 tokenizer 版本和 SHA-256。

生成库存 after.data（在仓库 PowerShell 中，编译离线替身但执行生产投影）：

    & tests/run_csharp_tests.ps1
    $before = Get-Content .local/token-audit-20261023/g10-inspect_inventory-full.txt -Raw | ConvertFrom-Json
    $inputJson = $before.data | ConvertTo-Json -Depth 100 -Compress
    $afterJson = [AtomicExtensionsOfflineTests]::ProjectInventoryJson($inputJson)
    [System.IO.File]::WriteAllText((Join-Path (Get-Location) '.local/model-state-inventory-after.json'), $afterJson)
    python tests/measure_model_state.py --tokenizer .local/token-audit-20261023/tokenizer/tokenizer.json --inventory-before .local/token-audit-20261023/g10-inspect_inventory-full.txt --inventory-data-after .local/model-state-inventory-after.json

## 验证与限制

Python 全套 313 项通过；C# run_csharp_tests.ps1、原生物品 IL 离线回归通过；tModLoader 隔离构建零错误零警告。compileall 和 git diff --check 通过。构建包为 .local/model-state-build/Mods/CompanionBridge.tmod，未部署或启动游戏会话。

额外 AuthorityNetworkOnly 在“Server executes native handler and consumes exactly one selected item”断言失败；HEAD 原 AtomicOperations 和原 PlayerHookOfflineTests 在隔离脚本中复现相同失败，属于基线问题，本阶段未修改网络行为。完整 Python 首次失败来自新增测试桩使用错误枚举 unknown，修正为 unknown_mod_hooks 后全套通过。
