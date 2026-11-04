# M4.4 最终收尾核查

2026-11-03。基线 `619eac9684a26648ca11890eba6cb12f7cf2751d`，分支 `codex/g85-server-ai-perception`；核查开始和结束时 HEAD 一致，证据采集自提交前的工作区补丁。核查后按用户指示提交并推送至现有 PR #25，保持 Draft，不合并。开始时已有的 Python、原生验证脚本修改及未跟踪文件均保留，不纳入本次提交。未运行全量测试。

## 发现与处理

### 1. 快速移除、重新添加：补覆盖，没有确认产品缺陷

**实验结果**：在运行中的同一个导航动作第 51 个更新移除 Swiftness，第 52 个更新重新添加，发生在首次恢复尚未制动完成时。旧产品探针能够完成；最终包同样完成。实际采样版本依次 `2/Swift → 3/Basic → 4/Swift`，没有 ABA 版本复用；版本不匹配的 20 个更新全部只注入中性输入。最终计划版本为 4，仅发生一次恢复、一次重规划，没有重启外部动作，没有把旧能力的失败边当作新能力下的障碍。

离线新增相邻更新切换反例，覆盖单调版本、恢复合并、旧输入释放、最新计划版本及失败边记忆。本轮没有为这个场景修改 `NavigationTask`。两个采样边界之间完全移除又恢复、且有效运动状态不变的操作不能被采样器追溯识别；这不等于执行了不同物理参数的旧预测。

### 2. 自然到期临界帧：确认误拒绝，做一帧窄修复

**源码/IL 事实**（钉住的 tML 2026.8.3.0）：

- `Player.UpdateBuffs` 的 `IL_0055..0082` 只给 `whoAmI == Main.myPlayer` 的玩家递减计时；其后 `IL_01e7..0204` 仍应用 Swiftness 的 `moveSpeed += 0.25`。
- `Player.Update` 在 `UpdateBuffs/PostUpdateBuffs` 之后的 `IL_2418..2459` 删除零时长 Buff，再在 `IL_4649` 调用 `PostUpdateRunSpeeds`，紧接 `IL_464f` 执行水平运动。
- 因此，本地到期最后一帧存在“Buff 槽位已删除，但本帧有效参数仍为 Swift”。下一帧才成为 Basic。不能把 `HasBuff` 和本帧已应用的运动效果等同。

**反例**：保留 `619eac9` 的 `GameNavigation.cs`、`MobilityProfile.cs` 产品类，只加相同隔离诊断夹具，导航在第 52 个更新以 `unsupported_horizontal_velocity/unsupported_physics` 失败，最终 X=33755.508。不是旧输入不安全继续执行；这是合法状态被误判为不支持，提前终止动作。

**修复**：`MobilityTracker` 仅在以下证据全部成立时承认一帧 `SwiftnessExpiryTail`：上一帧受支持的 Swift、剩余时长恰为 1、相同 session/generation、相邻 SourceTick、当前 Buff 消失且所有实测参数仍匹配 Swift。尾效帧沿用当前有效 Swift 的版本；不能续期自己，不能跨采样缺口、身份或取消，也不能使陌生 Swift 参数成为受支持能力。`NativeNavigation` 的状态门禁使用同一尾效判定；下一帧 Basic 变化仍使旧计划失效，按已有释放、确认停稳和重规划机制推进原目标。

新增尾效诊断字段；没有改预测方程、碰撞/支撑、公差、恢复预算或动作终态规则。第一次接手恰好处于尾效帧、缺少上一帧证据时仍保守拒绝。

**原生验证范围必须区分**：独立服务器 Actor 的计时不递减，初次探针把计时设为 2 后一直保持 2，不能称为自然到期成功。最终 `expiry` 场景在现有 Action/Terrain/Mobility 隔离门禁内、只针对绑定 Actor，于原生 Buff 应用之后递减并删除槽位，重构上述已核对的本地到期帧序；后续参数生成、输入和碰撞由真实引擎执行。夹具不改变 Actor 身份、位置或速度，也不修改生产服务器计时行为。这是**到期相位重构 + 原生运动验证**，不是图形客户端自然倒计时实测。

### 3. 第三方 Mod：明确兼容性限制，没有自动能力归因

当前 `OrdinaryRejection` 对所有额外加载 Mod 返回 `unvalidated_loaded_mod: 名称`，故无关 UI/内容 Mod 也会被拒绝。这个全局拒绝确实存在；它表示组合未经验证，不能作为“检测出该 Mod 修改了运动”的证据。原测试向 Tracker 传入拒绝字符串也没有测试真实 Mod 识别，本轮修正该测试的说明。

本轮保留门禁并补源码说明：单次采样数值、装备/Buff 列表无法排除采样之后的 ModPlayer、On 或 IL 钩子改变运动。直接删除门禁会放行未验证组合，不符合本阶段的已验证能力边界；没有新增名称猜测、随意白名单或扩大引擎支持。独立服务器的 `EngineHooks` 在更外层原本就要求只有 ModLoader/CompanionBridge，完全未改。未加载任何第三方 Mod 做原生组合验证。因此，**第三方 Mod 自动能力识别仍未实现，客户端的全局兼容限制仍存在**，不能把此项写成通用 Mod 兼容验收通过。

## 最终包定向原生结果

隔离钉住引擎/源世界与前阶段一致。源世界 SHA-256 `9f4665e8c6989bc5a523349e1bd4a303895814152170ff6cf9ebc7e862a54482`。每场一个外部导航动作，无模型、无客户端，不部署现用 profile。版本仍为 0.1.79 的审查候选包（非新发布）；SHA-256 `e7f06b183ce6f05d9be62a932aad522c443b6c58d30a72b0615380d9353d3304`。

共同起点 X=33630，向右目标偏移 384px；沿用既有到达公差及真实支撑/停稳谓词，不承诺精确停在目标 X。

| 场景 | 结果 | 实际位移 px | 游戏更新数 | 恢复/重规划 |
|---|---|---:|---:|---:|
| Basic | completed | 391.652 | 161 | 0/0 |
| 移除后下一帧重加 | completed | 383.125 | 162 | 1/1 |
| 到期帧序重构 | completed | 375.832 | 173 | 1/1 |
| 未验证 WellFed(26) | 预期 failed | 0 | 1 | 0/0 |
| 未验证 Hermes Boots(54) | 预期 failed | 0 | 1 | 0/0 |

三场成功均核验 arrived、grounded、安全支撑、settled、释放前零速度；五场均为 known 输入释放证据，受控轨迹最大预测误差为 0。到期场景第 51 帧采到时长 1，第 52 帧唯一一次尾效（HasBuff=false、maxRunSpeed=3.75、计划版本=5），第 53 帧 Basic 版本=6，随后 19 个版本不一致的更新全部中性输入，最终计划版本为 6。未知 Buff/装备仍提供具体拒绝项，未产生位移。

## 定向测试与复现

- `pwsh -NoProfile -File tests/run_csharp_tests.ps1 -MobilityOnly`：70 项通过（原 56 项，新增 14 项）。
- `... -NavigationOnly`：M4.1 搜索 187 项、M4.2 恢复 111 项、M4.3 行程 23 项、导航感知 44 项，以及既有落地/玩家钩子和上述 Mobility 检查通过。离线碰撞替身不等于原生引擎。
- `tests/run_server_ai_ipc_tests.ps1`：IPC、原子动作协议、297 项适配替身、64 项 M3 任务、30 项聊天协议检查通过；不是实际网络 Terraria 客户端。
- 现有 `.local/lua-venv`，`PYTHONPATH=tests;python`，`python -B -m unittest test_navigation_recovery -v`：8 项通过，31.308 秒。使用并保留原有 Python 工作区修改。
- 最终包构建 0 错误、0 警告。未运行全量测试；未重新跑此前 11 场整个原生矩阵。

原生复现入口（在 PowerShell 中）：

```powershell
& ./tests/verify_navigation_recovery_native.ps1 `
  -TModLoaderPath 'D:/Agent Project/terraria-ai-companion/.local/m4-3-engine' `
  -WorldPath .local/m4-audit/source.wld `
  -PackagePath .local/m4-4-close-final-build/Mods/CompanionBridge.tmod `
  -Mobility -Scenes @('basic','rapid','expiry','unknown','equipment')
```

原反例使用 `.local/m4-4-close-baseline-build/Mods/CompanionBridge.tmod`，`-Probe -Scenes @('expiry')`，允许记录预期失败，不把 Probe 退出码当成功导航。夹具、脚本、回执、逐帧轨迹、只读 IL 和日志共 36 个条目保存在 [原始证据](closeout-raw.zip)，逐条字节哈希、两包哈希和源码快照见 [manifest](closeout-manifest.json)，派生结果见 [summary](closeout-summary.json)。原 M4.4 证据不改写。

## 收尾结论与剩余风险

确认并修复一项到期尾效误拒绝；快速切换为补覆盖，已有版本与安全恢复机制有效。没有确认过期预测在采样后继续驱动的缺陷。M1～M3 的身份、取消、unknown、终态、输入释放证据保护未改，相关定向回归通过。仅在本报告明确范围内通过收尾核查。

尚未覆盖图形客户端自然到期实机、空中恰好到期/切换、丢采样/连接中断与到期的原生竞态、第三方 Mod 钩子组合，以及独立服务器 Buff 自然计时生命周期。无前序证据的尾效仍会失败；这是保守限制。额外 Mod 全局拒绝仍是兼容风险，不以本次结果承诺普遍 Mod 支持。到期相位夹具仅为显式隔离诊断，不能当作生产计时修复。本轮没有扩展 M4.4 功能或进入后续阶段。
