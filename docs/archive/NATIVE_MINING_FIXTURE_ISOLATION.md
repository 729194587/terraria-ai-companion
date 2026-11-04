# 串联原生挖掘 out_of_range：非战斗夹具的 NPC 污染

## 结论与证据范围

**观察（e2d6c35 / CompanionBridge 0.1.55）**：原失败的直接原因是角色受到伤害并被击退，在一次真实工具击之后离开原生物品范围。
这是复制世界中未隔离战斗的测试夹具/测试顺序污染；没有证据指向生产挖掘回归。生产逐帧拒绝继续挖掘与释放输入是正确行为。
中心距离小于80像素只通过适配器的保守前置检查，不保证随后满足原生矩形范围，亦不保证角色始终留在范围内。

原始失败回执 [combined-actions-failure.json](evidence/autonomous-lua/combined-actions-failure.json) 及其既有清单均未修改。
本轮从后续测试 profile 的 `previous-installation-logs/server.log` 找回同进程/世界的原日志，按字节归档，见 [新证据清单](evidence/mining-fixture-isolation/manifest.json)。
原日志没有伤害来源的NPC槽位，不能严谨指认某一只NPC；但 native-hurt、真实位移/速度和范围失效的因果链明确。
夹具创建了活跃、能正常行动的 BlueSlime，复制世界也存在环境NPC，而这组感知/语义/工具验收没有战斗前提或隔离。

## 原失败时间线

绑定世界 `456658b41c924094909ef33adad04572`、进程35540、Actor槽254。目标为 `(2109,217)`，中心 `(33752,3480)`；角色碰撞框20×42。
下表位置/速度是原生主线程日志实测，距离/范围由这些日志重建（显示值有浮点舍入）。

| gameUpdate / 事件 | 角色左上角 (px) | 速度 (px/tick) | 目标中心距离 | 原生右侧Tile上界 |
| --- | --- | --- | --- | --- |
| 1606 / 选中铜镐 | (33688.83,3462) | (0,0) | 约53.25 | 2109.8019 |
| 1613 / native-hurt | (33688.83,3462) | (-4.5,-3.5) | 约53.25 | 2109.8019 |
| 1614 / mine_tile启动 | (33684.43,3458.9) | (-4.4,-3.1) | 约57.57 | 2109.5269 |
| 1616 / mine-result失败 | (33675.93,3453.9) | (-4.2,-2.3) | 约66.27 | 2108.9956 |

1613帧生命91→84、伤害7、immuneTime=40。启动时80像素前置检查和原生范围均通过；之后真实运动继续，边界被越过。
`ActorMining.Update` 在递增Ticks之前重查 `InRange`，所以第二次更新拒绝后仍为 Ticks=1 / Swings=1 / Strikes=1 / PeakDamage=35；
35/100只是原生部分进度，目标仍是Stone且没有被该Actor破坏，不能记为完成。失败终态有known输入释放，未自动重试。

`MiningPatches.RangeClone` 只把引擎 `IsTargetTileInItemRange` 的光标坐标读取换成绑定目标；没有修改原生范围公式。
本轮读取实际DLL的77条IL并归档：右侧上界是 `(position.X+width)/16 + tileRangeX + item.tileBoost - 1`。
铜镐的tileBoost=-1，基本profile的tileRangeX=5、tileRangeY=4；右侧条件要求上界≥2109，即X至少33676。
原失败X=33675.93，约越界0.07像素，而中心距离仍小于80。**不得把80像素检查当作原生射程承诺，也不应扩大任何范围来修复此例。**

## 工具、场景与连续状态

- 读取阶段真实库存：内部槽1 / 游戏键2为无前缀CopperPickaxe（type3509、pick35）；语义阶段选键4铜斧。
  后续旧动作明确依次选键1木剑、键3苹果、键2铜镐，选栏回执确认selectedSlot=1。失败原生结果仍记录type3509、pick35、axe0；没有沿用铜斧。
  `Held` 的对象/slot/type/prefix/pick/axe检查仍在；本轮未变更装备或背包执行路径。原归档没有失败帧的完整装备/Buff快照，不声称重演了全部状态。
- 语义夹具的树在spawnX-4，矿在spawnX-10..-8；旧Stone目标在spawnX+8、floor-2。树木倒下和矿格注入不覆盖目标。
  原失败TileType/ExpectedType/observedType都为1（Stone），targetChanged=false。语义阶段无导航或位置注入，仅显式选斧和砍树。
- 后续取消导航会产生合法的微小初始位移，成功导航也保留既有12像素公差；原到达X=33688.83与独立成功X=33689.066接近，两者都在原生范围内。
  真正越界发生在其后的native-hurt击退。苹果使用周期结束与Buff并未被当作范围成功证明；没有清Buff、强制停稳或传送来掩盖失败。
- e2d6c35改变了查询数量与测试耗时，生产C#与所用0.1.55包未变。延长/缩短只读阶段会改变活跃NPC的运动/接触时机；
  独立旧动作通过不能证明串联无残留，复制世界本身也不等于已隔离NPC。

## 最小修复与确定性回归

修改仅限测试夹具、原生自测和验收脚本，没有修改 ActorMining、ServerAiNativeActions、MiningPatches 或生产范围/身份/取消契约。

1. `ServerAiActionFixture` 从开始就排除环境中的非城镇NPC；读取期间仅保留指定的真实BlueSlime实例并将其固定在诊断姿态，
   保留原生type/damage等感知字段。脚本必须实际观察到该fixture槽位，不能由另一只同类NPC偶然满足测试。
2. NPC感知完成后，脚本写入仅隔离profile使用的阶段marker；主线程在没有运行中动作时移除该NPC，并持续排除后续非城镇NPC。
   ACK绑定world/generation/slot，保存角色原有位置、速度、生命、所选物品/范围参数和Buff；不重置或修改Player运动/生命/装备，不赋予无敌。
   所有行为仍受原有双诊断开关、复制世界marker、无客户端、主线程与Actor身份门禁约束。整轮日志若出现native-hurt则验收失败。
3. `MiningSelfTest` 用原日志的相对位置注入两个诊断状态，执行真实ItemCheckWrapped和原生范围：
   起初允许一次35伤害工具击；下一次范围检查前注入已记录的击退后位置，必须稳定OutOfRange、保留目标、释放输入，且中心距离仍<80。
   另一独立显式场景使用相同目标/铜镐但保持静止，必须三击达到105并原生破坏。位置注入仅存在于既有opt-in自测，
   不模拟NPC攻击来源或完整物理运动，也不是正式动作路径或失败后自动重试。

首次修复尝试把隔离边界放到语义阶段之后，复验反而在语义阶段遭遇多次native-hurt并触发80像素前置拒绝（该轮日志共三次伤害）；
这证明清理边界过晚。该失败的原始回执/日志也保留，不记为通过。最终规则从夹具开始控制NPC，并在感知后进入无NPC阶段。

## 验证与复现

最终包沿用版本0.1.55（只改诊断代码），包SHA-256和各结果/日志逐字节哈希见新证据清单。未部署现用profile，未运行全量测试，未调用付费模型。

```powershell
pwsh -NoProfile -File scripts/build-companion.ps1 -OutputPath .local/mining-isolation-build
# 与原失败相同的阶段顺序：读取→感知→语义→取消导航→旧动作→取消/清理
pwsh -NoProfile -File tests/verify_server_ai_native.ps1 -Atomic -Reads -Actions -Perception -Semantic -PackagePath .local/mining-isolation-build/Mods/CompanionBridge.tmod -Port 27931 -IpcPort 27932
# 另一新复制世界的独立旧动作回归
pwsh -NoProfile -File tests/verify_server_ai_native.ps1 -Atomic -Reads -Actions -Perception -PackagePath .local/mining-isolation-build/Mods/CompanionBridge.tmod -Port 27931 -IpcPort 27932
# 定向原生挖掘组，含确定性范围回归；无图形/模型
pwsh -NoProfile -File experiments/virtual-player-engine-hooks/verify-native-mining.ps1 -PackagePath .local/mining-isolation-build/Mods/CompanionBridge.tmod -ModName CompanionBridge -Port 27961
```

构建零错误/警告；最终串联和独立旧动作均在新profile通过，包含目标真实破坏、掉落、输入释放、取消和进程收尾。
原生挖掘自测的43项断言通过，包含确定性OutOfRange与静止成功对照。不是图形Host & Play、真实模型自主任务或所有战斗时序验收。
