# 独立 Server AI 二维落脚（0.1.57）

## 设计与接口

复用已有 `LandingRegion → NativeNavigation → NavigationTask → StateSpacePlanner`。原规划器已支持二维目标、普通跳跃、平台顶面及实际落点确认；本次只将该目标类型贯通独立 AI 的能力协商、动作生命周期、原生 driver 和结果核验，没有重建导航器或增加探索决策脚本。

执行链：Agent 观察并生成 Lua → 既有 Worker/atomic dispatcher → `ServerAiBridge` → `AiAtomicActions` → `ServerAiNativeActions` → 每个 AI 自有 `NativeNavigation` → 原 `ActorControl` 游戏线程运动边界 → 原生 Player.Update。真人静态导航实例保持独立；没有用设置位置或垂直速度代替移动。

需同时握手 `server_ai_actions_v1` 和 `landing_region_navigation_v1` 才向服务器模型开放：

```lua
-- 数值必须由本轮观察确定；此处只示例坐标语义。
local action = api('navigate_to_landing_region', {
    minPlayerLeftX = 33728, maxPlayerLeftX = 33760, surfaceY = 3760
})
return {action=action, after=api('get_player_state',{})}
```

- 参数均为世界像素，1 Tile=16 像素，Y 向下。X 是玩家碰撞框**左边缘**闭区间；surfaceY 是脚底的目标表面，不是玩家顶部或身体中心。普通角色为 20×42 像素。
- 根据 `observe_terrain.data.terrain.originX/originY/rows`、覆盖和 legend 确认表面及角色净空。模型可先解释观察并生成参数，也可结合结构化 Tile 查询编排。Lua 沙箱仍不开放 string 库；不能在生成程序中假定 `string.sub` 或字符串方法存在。
- Agent 自主选择方向、目标、连续段与后续计划。目标不可靠或没有合适候选时停止并报告；`explore()` 仍只是可选参考，没有加入新固定探索流程。
- 接受时先按共享参数校验，再规范化到原生规划器的 float 坐标；回执 `targetRegion` 固定为该实际接受区域，执行与终态证据使用同一区域。目标不能被续约或轮询改变。小数舍入在原有开始回执容差内检查，不扩大到达公差。

## 可信反馈与保护

原规划器判定 arrived 后，服务器 driver 再以实际 Player 位置检查区域、平面支撑、grounded、Jump 释放和速度；最终效果回执再检查同一条件。Python 额外核对接受区域与证据一致、X 在闭区间、脚底高度误差 ≤0.5 像素、`|vx|/|vy|≤0.1`，以及绑定身份和 known 游戏线程输入释放证据。只经过目标 X、预测误差为零或脚本自报完成均不算成功。

保留原 30 秒 / 1800 原生更新限制、局部搜索/动作/进展预算、危险/未知拒绝、普通角色 profile、共享 Lua 预算和动作互斥。没有放宽距离、物理或落点阈值。世界/代次/槽位/Player 对象改变拒绝旧动作；死亡、断开、取消和超时沿用现有释放及隔离。续约与终态交接间目标改变也保持 unknown，不自动重试。

`native_navigation_failed` 的 `reasonDetail` 保留局部无安全路径、搜索时间/进展预算、落点不匹配或不支持状态等原因。**局部有界搜索失败不证明全局不可达**，也可能在部分移动后停止。失败时落地未确认仍返回 `unknown/native_navigation_landing_unconfirmed`；取消/超时维持对应状态，输入释放不等于已落地。

## 实际地形支持与边界

| 范围 | 本轮证据与限制 |
| --- | --- |
| 实心完整顶面 | 平地、上/下 1 格台阶，两方向都到达区域并安全停稳 |
| 斜坡路径 | 上/下 4 格的普通斜坡通行至水平完整顶面，两方向通过；斜坡自身不作为区域落脚面 |
| 缺口/障碍 | 48px 短缺口、32px 低障碍，两方向通过；不是任意宽坑或高墙承诺 |
| 水平单向平台 | 原生跳上高 64px 平台、绕边下降 64px 到下层平台，两方向通过；没有开放按 Down 直接穿落 |
| 过高目标 | 高 192px 平台两方向均失败，保留具体局部搜索原因并释放输入；不是通过传送或放宽阈值完成 |
| 真实生成世界 | 同一小型 Classic 世界 seed 20261028，6 个起点/方向组合，4 组有不同高度候选，共 6 段实际 16/32px 高差导航全部完成；其中两组连续两段。另两组没有观察到合适目标，未移动 |
| 窄区域 | 4px 窄目标可成功，也可能在左右逼近/制动时耗尽既有进展预算，不能保证任意精确 X。测试最终按观察将连续表面合并为区域；原窄目标失败保留，没有调整规划器或阈值 |
| 明确不支持 | 斜平台、半砖/斜坡作为最终区域面、游泳/飞行/坐骑/倒重力及其他拒绝的非普通 profile、危险/未知包络、超出原下降限制（64px）的边、任意远处全局寻路 |

上述构造场景与真实生成世界分开记录。生成世界的候选投影由确定性诊断宿主从真实观察解析，再交 Lua 排序/选择、执行和核验；没有 LLM，不能以此证明 DeepSeek 能稳定自主选点。每次动作后重新观察，失败后停止，不搜索替代路线刷通过率。窄目标和前期协议大小写错误的失败均保留。

## 自动化与图形验收

定向验证包括 Server AI 实际 loopback/Worker 的二维编排、旧后端能力门禁、错误高度/位置/支撑/速度证据拒绝、目标改变不重放及取消；原客户端二维协议/规划集成和 C# 落脚定向组保持通过。Server AI 协议/生命周期与最终包 IL 检查覆盖类型化区域接入和双重实际到达校验。未运行全量测试。

原生矩阵共 20 场（18 完成、2 明确失败）；另外验证逆序参数在执行前拒绝、开始后的小数区域取消及输入释放，并同轮执行原读取/导航/物品/挖掘/取消/进程清理。真实生成世界复用前轮字节归档的 source.wld，只初始化各测试起点，**不写 Tile**，随后通过原导航移动。

复现：

```powershell
pwsh -NoProfile -File scripts/build-companion.ps1 -OutputPath .local/server-landing-build
pwsh -NoProfile -File tests/run_server_ai_ipc_tests.ps1 -PackagePath .local/server-landing-build/Mods/CompanionBridge.tmod
pwsh -NoProfile -File tests/run_csharp_tests.ps1 -LandingOnly
& .local/lua-venv/Scripts/python.exe -B -m unittest discover -s tests -p 'test_server_ai_landing.py' -v
& .local/lua-venv/Scripts/python.exe -B -m unittest discover -s tests -p 'test_server_ai_backend.py' -v
& .local/lua-venv/Scripts/python.exe -B -m unittest discover -s tests -p 'test_landing_navigation.py' -v
pwsh -NoProfile -File tests/verify_server_ai_native.ps1 -Atomic -Actions -Reads -LandingRegions -PackagePath .local/server-landing-build/Mods/CompanionBridge.tmod -Port 27965 -IpcPort 27966
# 从 docs/evidence/natural-navigation/raw-runs.zip 解出 world/source.wld 到 .local 后：
& ./tests/verify_natural_navigation.ps1 -LandingRegions -WorldPath .local/world/source.wld -PackagePath .local/server-landing-build/Mods/CompanionBridge.tmod -Port 27971 -IpcPort 27972 -Offsets @(0,32,-32)
```

结果、源文件哈希、原始回执/逐帧轨迹和实际执行 Lua 见[证据清单](evidence/server-landing-region/manifest.json)。包位于 `.local/server-landing-build/Mods/CompanionBridge.tmod`，版本 **0.1.57**，包含 0.1.56 的持续挖掘原生拾取修复。本轮没有覆盖现用 profile 或启动/停止用户游戏，未调用付费模型。

最终包 SHA-256：`a27cb1a06c48fb15d16a883e6cd970381b59252d52d211cc9231694df5d27af3`。原生验证的两条最终运行均使用该包。39 项不同 Python 定向用例通过；既有后端只读用例曾在并行原生负载下耗尽 8 秒测试预算，未修改预算或断言，单独复验该文件 28 项通过，原失败日志保留。

439 个原始 payload 按字节压缩归档并校验 SHA-256。`protocol-failure/` 保留早期 targetRegion 大小写投影不一致造成的证据拒绝；`narrow-region-failure/` 保留窄目标预算失败；`matrix-final/`、`natural-final/` 为最终包结果。早期失败的包哈希单独记录，不与最终包混同。源世界与前轮原始归档的字节哈希相同，本轮不重复保存世界。

**图形 Host & Play 将与拾取修复合并验收**：核对实际加载版本/能力；观察模型从真实地形自主给出不同高度区域，原生跳跃/落地、失败/取消和实际位置反馈；同时验证持续砍树时附近掉落及时入包。真实 LLM、真人/敌怪同场、远程多人/晚加入同步尚未验收，PR 保持 Draft。
