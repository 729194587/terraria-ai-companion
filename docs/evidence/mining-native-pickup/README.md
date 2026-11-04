# 持续挖掘期间的原生拾取（0.1.56）

## 根因与修复

**观察 / 历史依据**：`3f51c6b` 的 G7 原生挖掘探针在 `ActorMining` 安装 `On_Player.GrabItems` 拦截器，运行期间及最后一次挖掘更新的同帧直接返回。原目的为单独验证目标破坏和世界掉落、确保实验背包不变；`2903551` 整合时保留了该实验限制。它不是绑定身份、拾取所有权或挖掘归属判定所需的保护。旧实验和既有实机原始证据均未改写。

**修改**：删除挖掘层拾取钩子及 `suppressPickupTick` 状态，恢复普通 `Player.Update → GrabItems`。继续通过既有 `NativeSurvival` 的线程、绑定角色、reservation、原生拾取 IL、世界物品变化与网络同步路径执行；没有另建拾取循环、直接改背包或开放新的动作。挖掘的固定目标、原生范围、工具对象与选栏校验、破坏归属、自然计时结束、取消/超时/异常隔离均保留。捡到物品不会完成 `mine_tile`。

旧测试只在结束后运行完整世界帧，挖掘阶段仅调用 `ItemCheckWrapped`，所以无法揭示这一行为差异。现在 `TreeMiningSelfTest` 在真实 `WorldGen.GrowTree` 生成的树旁，准备原生木材掉落后，使用完整 `Main.Update` 帧持续砍树。夹具仅在开始前设定位置、清除 NPC 干扰；动作中不纠正位置或物品计时，不手调背包完成拾取。已有物品专用帧测试只证明该调用没有自行修改背包，不再将“不能拾取”表述为挖掘契约。

## 实验结果

基于 `055da80`，同一回归先在旧生产逻辑上运行，再验证修复包。两者均在全新隔离 profile 中复制原世界，未连接真人或调用 LLM。

| 场景 | 实际结果 |
| --- | --- |
| 旧逻辑 + 新回归（0.1.55） | 稳定触发拾取断言失败：10 个完整世界帧内未拾取身边 7 木材；动作仍 Running，树干存在，1 次击打，进度 8/100。前置 40 项检查通过，不能算整组通过。 |
| 修复后（0.1.56） | **第 1 次更新拾取 7 木材**，世界种子掉落已消费，目标仍存在、Running、1 次击打、进度 8/100，铜斧对象和选栏未变。 |
| 随后正常砍树 | 274 次更新、9 次挥动、13 次击打、峰值 104；22 个树格归零，API `completed/native_tile_destroyed`，效果与输入释放核验通过。完成时木材入包共 23（包含预置 7），世界尚有 12，未把目标破坏等同于收齐掉落。 |
| 原生挖掘组 | 43 项外层检查及树木内部断言通过；覆盖普通石块、草地转化、原生保护/工具拒绝、目标/工具替换、取消、超时、死亡和旧代次。原生范围边界失败仍正确拒绝。 |
| 独立异常注入 | PickTile 内异常仍释放输入、保留 reservation 并 quarantine；只终止该诊断进程，不将强制退出计作正常恢复。 |
| 独立 IPC/Lua 原动作回归 | 读取、导航、选栏、使用物品、挖掘、取消、输入释放及进程清理通过；石块 3 击、进度 105、真实目标破坏，退出码 0。 |
| 定向工具 IL | G6 19 项、G7 30 项通过，包括精确引擎结构、未知结构拒绝、私有目标范围克隆与禁止全局身份改写。 |

构建零错误/零警告，未运行全量测试。图形 Host & Play、客户端接收/显示、真实 LLM 和多人竞争拾取未在本轮验证；本修复包未覆盖现用游戏 profile，也未停止用户游戏。

## 复现与原始证据

```powershell
pwsh -NoProfile -File scripts/build-companion.ps1 -OutputPath .local/mining-pickup-build
pwsh -NoProfile -File experiments/virtual-player-engine-hooks/verify-native-mining.ps1 -PackagePath .local/mining-pickup-build/Mods/CompanionBridge.tmod -ModName CompanionBridge -Port 27961
pwsh -NoProfile -File experiments/virtual-player-engine-hooks/verify-native-mining.ps1 -PackagePath .local/mining-pickup-build/Mods/CompanionBridge.tmod -ModName CompanionBridge -Port 27963 -Failure
pwsh -NoProfile -File tests/run_server_ai_tool_tests.ps1
pwsh -NoProfile -File tests/verify_server_ai_native.ps1 -Atomic -Actions -Reads -PackagePath .local/mining-pickup-build/Mods/CompanionBridge.tmod -Port 27965 -IpcPort 27966
```

旧逻辑复现：在 `055da80` 仅应用本次两个诊断测试文件的变更，保留原 `ActorMining.cs` 和 0.1.55 版本，构建至 `.local/mining-pickup-baseline` 后执行相同原生挖掘命令。

修复包 SHA-256：`d037a379a24867fd0be9430b437f734c63bb29d30fe089cf651371d6f8a674d9`。
旧逻辑诊断包 SHA-256：`fd5500f2d6683ae74666f949f85f258225628604b956cc391394e3ab5e9a3eb4`。

[manifest.json](manifest.json) 记录 23 个原始 JSON/日志的来源、字节数和 SHA-256；`baseline/` 保留原失败，`fixed/` 为同回归修复后结果，`exception/` 为异常隔离，`ipc/` 为独立原动作验证。文件禁用换行转换；原始 stderr 为空也按字节保留。日志中的历史 G7/G6 命名不表示另一套生产执行器。
