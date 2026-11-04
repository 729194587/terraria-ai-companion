# L2：二维地形通过（0.1.55）

后续自然世界验证见 [生成世界连续导航](NATURAL_WORLD_NAVIGATION.md)：本页的32场是构造矩阵，不作为自然世界通过证明。当前探索筛选沿行进方向滚动支撑高度，具体自然停点与剩余限制分别记录。

## 审查结论与本轮缺口

审查 `StateSpacePlanner`、`PhysicsSimulator`、`NavigationTask`、`NativeMotionWorld`、`NativeNavigation` 与 `ServerAiNavigation` 后，确认底层已具备二维地形通过能力：

| 环节 | 已有能力与保持的限制 |
| --- | --- |
| StateSpacePlanner | 左右输入、可变跳跃 hold、带速度状态的搜索、128px滚动目标、安全 partial route；32次展开/25ms，单候选不抢占 |
| PhysicsSimulator / NativeMotionWorld | 原生 WalkDownSlope、StepDown、StepUp、TileCollision、SlopeCollision；验证完整运动和制动尾段，下降最多64px，局部45×29窗口 |
| NavigationTask / LocalMotionController | 只执行首段、按真实状态重规划；逐帧偏差检查、交接位置/速度核验、落点双帧确认、60帧确认上限、进展/动作预算 |
| 原生控制 | 绑定AI的自然Player.Update输入钩子，Actor/world/generation/slot检查；原生终态与输入释放，未知落地保留unknown |

**实际未打通处是探索层的前置筛选。** 0.1.54的 `explore` 要求走廊每一列都有平面支撑，斜坡和短坑在发起导航之前就被拒绝。原生基线证明无需替换物理或新增动作。本轮使已有二维能力进入 Agent 的持续探索流程；不新增 `navigate_to_point`，不扩大引擎支持范围，不改变碰撞、公差、速度/Buff门禁或落点要求。

## 实现行为

`python/lua/explore.lua` 的候选过滤现在允许：

- 完整方块、平平台、普通地面斜坡 `1/2`、半砖 `h`，仍检查表面上方三格空间。
- 初列以当前脚底为参照；后续按行进方向以上一观察支撑高度为参照，在相同上下4格窗口找候选表面，相邻已观察表面差仍最多2格。
- 最多3格连续空缺，且两端都有观察到的支撑；目标不能停在缺口内。缺少表面但含实体块/不支持形状的列不能伪装成空坑。
- 候选走廊上方10格至下方5格内的未知/危险仍拒绝。此检查保守，可能拒绝实际可走路线；不会把它解释成全局不可达。

这只是方向/目标筛选。动作仍通过 `navigate_to_x` 的原生搜索、完整安全轨迹/制动证明、实际执行和落地核验决定能否完成。无法找到安全路径可在安全partial检查点停止；高墙也可能由既有四段无进展预算结束。不是发一个无规划的方向输入去试撞。

候选决策标为 `observed_bounded_2d_candidate_native_plan_required`。steps新增 `fromY/toY`，保留真实垂直变化；行程预算、访问桶和新区域指标仍按水平方向计，不改成二维路径长度。目标X仍是玩家左上角水平穿越语义，允许越过，不能用于精确XY站位。斜坡上安全停稳也可完成，不要求所有终点都是水平地面。

威胁快照、候选缩短、发动作前换向、共享调用/动作/墙钟预算、失败后不重试、取消与unknown保护保持不变。基础模型仍拒绝WellFed等改变物理的Buff、特殊装备/重力/液体/未知Mod碰撞。半砖只完成了候选层自动化覆盖，本轮没有额外把半砖列为原生验收结论。

## 可复用隔离场景

`ServerAiTerrainFixture` 仅在原 `-companionactionstest` + `-companionaiipctest` + 复制世界marker + 无客户端 + 游戏线程检查下，再加 `-companionterraintest` 生效。通过固定文件请求白名单场景，不接收任意路径或脚本。准备场景时先确认没有活动动作，再在 Reservation Gate 内构造地形、设定诊断初态，等待15个完整原生更新。之后不通过改位置/速度推动导航。

测试场景与左右镜像以出生点为原点，`q` 为沿前进方向的Tile列，起点中心在q=0，参考地面为 `spawnTileY+8`：

| 场景 | 几何 | 预期 |
| --- | --- | --- |
| flat | 连续平地 | 行走、制动、停稳 |
| step_up / step_down | q≥4整体升/降1格 | 原生上下台阶 |
| slope_up / slope_down | q=4..7连续普通斜坡，总高差4格，之后平地 | 上/下坡并安全停稳 |
| short_gap | q=4..6，宽48px、深160px | 跳过，不落入坑底 |
| low_obstacle | q=4..5，宽32px、高32px | 越障后落地 |
| wide_gap | q=4..20，宽272px、深160px | 在安全侧明确失败 |
| high_wall | q=4..6，高192px | 在安全侧明确失败 |

每个正例分别左右执行直接导航（192px）及一次 `explore`（128px）。负例左右执行直接导航；探索层拒绝另由自动化测试覆盖。各场景独立创建请求/动作，不是失败后的路线重试。fixture排除非城镇NPC并清除上一场景Buff，隔离战斗/击退和先前吃苹果带来的物理变化；因此本矩阵**不证明战斗中通行安全**。

每帧读取真实位置、速度、支撑、停稳和原导航预测反馈，单独保存 `terrain-motion-<scene>-<direction>-<mode>.json`。短坑和低障碍必须出现实际腾空；所有正例必须有完成回执、真实目标位移、末帧安全停稳和释放证据；负例必须因无安全路线或进展预算失败，不能把物理状态不支持等无关拒绝算作地形验证。

## 已执行的验证

没有运行全量测试。

```powershell
pwsh -NoProfile -File tests/run_csharp_tests.ps1 -NavigationOnly
$env:PYTHONPATH="$pwd/python;$pwd/tests"
& .local/lua-venv/Scripts/python.exe -B -m unittest test_autonomous_exploration -q
pwsh -NoProfile -File scripts/build-companion.ps1 -OutputPath .local/l2-build
pwsh -NoProfile -File tests/verify_server_ai_native.ps1 -Port 27961 -IpcPort 27962 -PackagePath .local/l2-build/Mods/CompanionBridge.tmod -Atomic -Reads -Actions -Terrain
```

- **确定性物理/离线**：已有定向导航组通过，覆盖真实生产规划器/模拟器/执行器的双向连续跳跃、障碍上下行、浅坑、近目标制动、危险/未知拒绝、预测偏差、预算、代次/取消、落点确认与玩家钩子隔离。其碰撞世界是明确的测试替身，不能代替原生碰撞结论。
- **Lua/协议自动化**：18项探索测试通过。新增斜坡、短坑、半砖候选转交导航，以及过宽缺口/缺少目标支撑拒绝；保留威胁、预算、取消、unknown不重试、访问记忆和能力门控测试。替身位移不算原生跳跃。
- **构建**：0.1.55，零错误、零警告。
- **隔离原生**：32/32通过，28个正例完成并停稳，4个负例安全失败并释放输入。所有保存帧的导航MaxError最大值为0；这是本矩阵内预测反馈，不是对所有地形的零误差保证。正例真实末端Y包含台阶±16px、完整斜坡±64px；短坑/低障碍均记录实际腾空再落地。直接下降斜坡到平地，128px探索也记录了安全停在坡面的情况。
- 同一次隔离进程中，既有选栏、读取、导航、物品使用、挖掘、取消、绑定代次错配拒绝和正常退出清理通过。请求generation+1的拒绝不等于已执行真实死亡/重生/世界重载时序。

首轮夹具继承吃苹果后的WellFed，基础物理门禁正确拒绝；修复夹具隔离后，第二轮下坡起步遭NPC击退（Vx=4.5、Vy=-3.5），保留unknown。最终矩阵排除NPC干扰后通过；未删除生产防护来掩盖这些结果。高墙原本由无进展预算拒绝，验收按真实原因记录，未将其改写成搜索无路。

[汇总结果](evidence/l2-terrain/results.json) · [原始回执与逐帧轨迹](evidence/l2-terrain/native-raw.zip) · [逐文件SHA-256](evidence/l2-terrain/manifest.json)

包：`.local/l2-build/Mods/CompanionBridge.tmod`；SHA-256 `39CB9A782B83280AB48FCEEA669B197C61301C08A6B4AB13E1F354639C151F81`。

## 尚未执行的图形集中验收

未部署现用profile，未启动图形Host & Play，未调用真实LLM。用新建隔离Host & Play profile与同包准备上表双向地形，先分别明确命令“向右/左移动192像素”，再“向右/左探索一段”。核对游戏画面与日志：台阶自然上下、斜坡连续通过、短坑/低障碍腾空后停稳、宽坑/高墙明确停止。另行验收移动中停止聊天、真人同场、敌怪出现、坡面起步、世界变化与连续多场景组合；这些不在本轮矩阵保证内。普通斜坡、短坑和低障碍之外的跳台、斜平台、天花板跳跃、特殊装备与任意深坑不自动开放。
