# 0.7.3：草皮块的原生转化与挖掘失败诊断

基于用户0.7.2真实测试日志和累计 Draft PR #19，修复同一坐标草皮块被原生剥为泥土后错误中断的问题。main、旧 PR #11–#18 和用户存档不修改。

## 日志结论

本轮四次服务器挖掘请求均被处理，但没有 Completed：

| 时间（UTC+8） | 结果 | 可确认的范围 |
|---|---|---|
| 19:18:10 | InvalidTool | 没有选中符合范围的镐；19:17:30状态中AI栏1为空，但原失败日志未记录实际工具，不能断定失败时仍是空栏 |
| 19:19:59 | UnsupportedTarget | 目标超出支持范围；旧日志在拒绝后才填写类型，错误地留下默认0，无法据此识别实际方块 |
| 19:20:11 | NoPickPower，目标2，进度0 | 原日志无法区分原生保护/能力拒绝与转化分支抑制进度；不能把它直接称为铜镐无法挖草皮 |
| 19:20:33 | TargetChanged，目标2，进度35 | 普通草皮的正常剥除路径会触发该误判，已在无其他参与者的隔离服务器复现 |

状态记录证明AI在线，行走完成约65px；没有引擎 fault/quarantine。命令到达服务器不代表挖掘成功。原始日志快照保存在 `.local/live-mining-failure-20261026-192114/`，不提交个人日志。

## 机制与修复

重新核对固定1.4.4.9 / tML 2026.8.3.0 DLL：`DoesPickTargetTransformOnKill` 会按预测累计值判断转化，`PickTile` 某些分支把本次实际累计伤害置0；`WorldGen.KillTile(fail:true)` 在 `0x0650..0x06a0` 把普通草皮2改成泥土0并重整。第一次隔离回归明确重现 `Failed/TargetChanged`，世界中原块仍存在、类型已为0、进度35。

0.7.3仅在精确绑定的本AI原生 `PickTile` 作用域中，且原生保护判定后的伤害大于0、原目标为普通草皮2、形状/致动保持、没有外部变化记录时，接受实际 `KillTile(fail:true)` 返回的草皮2→泥土0。继续执行同一坐标的原生镐击，直到原生真正破坏并等待冷却结束。剥草皮不算 Completed，不写 TileType、不直接调用 KillTile、不挖邻格，不扩大为任意类型转化。

外部草皮剥除、外部直接改成泥土、同类型拆放和其他替换仍拒绝。原始类型、预期原生后续类型与实际观测类型分别记录，便于归因。严格IL核验增加了草皮转化判断和实际写入分支，结构未知继续拒绝安装。

零伤害判断改为读取原生 CanKillTile 后、转化抑制前的伤害，保留真正的保护/镐力拒绝，不把转化抑制产生的0当作没有能力。开始/停止按 HitTile 查询返回的真实缓存槽清除本AI的目标进度，避免继承旧动作或误清其他坐标。

拒绝日志现在包含真实目标类型、所选物品/镐力、原生伤害及观测类型；聊天中的空栏和不支持目标有更明确的中文提示。不会自动赠送物品或自动切换工具。

## 验证

- 构建0错误0警告，`verify.ps1 -MiningOnly` **266项PASS**，含新增转化结构变异拒绝；未运行仓库全量测试。
- 修改前已用新增草皮测试复现失败，证据 `.local/probe-grass-before-fix.txt`。
- 修改后 **37项原生挖掘检查通过**：石块行为保留；两次连续草皮测试均31次更新、1次挥动、2次镐击、进度105/100、一次草皮→泥土，最终真实破坏并生成 DirtBlock 世界掉落。中间泥土仍存在时保持 Running。
- 外部草皮剥除/替换拒绝、旧目标进度不继承、其他坐标缓存不被清除，以及已有距离/工具/保护/停止/死亡/代次等检查通过。
- 原生 PickTile 内部 CanKillTile 钩子异常注入验证输入释放、作用域退出和 quarantine；只终止脚本创建的隔离进程，不记为正常恢复。
- `git diff --check`通过。图形挖掘、选框对应、客户端接收和自然实时帧节奏仍待用户复测，不以原生API诊断替代。

包：`.local/virtual-player-single-window-build/Mods/VirtualPlayerEngineProbe.tmod`，SHA256 `AC2094DED9E94B52507EA8810A3A0A45DB91E0AF6A8E83396E92FA924B885B70`。证据 `.local/probe-073-offline-checks.txt`、`.local/probe-073-native-mining.txt`；正常原生profile为 `.local/mining-native-validation-9862a589f7684a38aa4b33e579610ef2/`。

## 更新与复测

当前运行中的0.7.2仍锁定旧包，准备脚本已安全拒绝更新。保存并完全退出游戏及服务器后，在仓库根目录执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File experiments/virtual-player-engine-hooks/prepare-host-play.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .local/virtual-player-single-window-host/start-host-play.ps1
```

确认0.7.3。先 `/vph status` 看AI当前是否真拿着铜镐；没有镐时 `/vph item-fixture`，按提示 `/vph slot <铜镐栏号>`。然后用 `/vptarget` 指向AI附近一格普通草皮或石块，打开聊天 `/vpmine`，再 `/vph mine-status`。应只有指定格最终消失并出现世界掉落；若原生保护拒绝，保留具体诊断，不绕过保护。
