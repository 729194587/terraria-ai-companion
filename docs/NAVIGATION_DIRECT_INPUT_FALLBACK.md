# 导航失败诊断与有界 Direct Input 降级（0.1.66）

## 问题与选择

导航执行器已有reason/reasonDetail、真实末位置/速度、grounded/safeSupport/settled和绑定释放回执，IPC和Dispatcher也保留failed；但Lua Runtime只允许明确“未开始”的预检拒绝续行，普通导航失败会终止程序。

本轮增加宿主确认的窄通道，不把导航失败改成成功，不重写导航器、物理或Agent Loop，不自动生成/重试移动。保留已有未开始预检续行规则。

- C# `AiActionReasons.NavigationRecoveryKind`只分类明确无局部路线、profile拒绝和已确认无进展。搜索/帧/动作预算耗尽、生命下降、预测/落点偏差、原生异常不放行。
- `ServerAiNativeActions.Poll`只有在原生failed、FinalObservation存在且安全着地停稳、输入已释放、角色存活、原因与原规划器终态一致时，才输出recoverableFailure=true和failureKind。既有x/y/vx/vy、grounded/safeSupport/settled、reasonDetail和采样更新号保留，新增inputReleased/alive/health便于诊断。接受请求或释放输入本身都不等于效果确定。
- Dispatcher继续原样传递终态和证据；`navigation_recovery.confirmed_failure`再核对failed、原生标记、有限数值、采样顺序、session/actionId/世界/Actor/代次/slot及known游戏线程释放。旧包缺字段即不放行。还要求当前后端同时开放observe_scene和move_input。
- Lua收到原始failed及宿主continuationAllowed=true、continuationScope=observe_scene_then_move_input。只能先读状态，再以同身份、终态之后的observe_scene作为下一变更的直接前置观察；跳过观察、直接重试导航或换成其他变更会在派发前停止。
- 观察须available/valid、sceneId和更新号一致，Actor存活且位置/速度为有限数。缺失/过期/身份不符的场景使本轮进入unknown隔离。宿主不把这次观察当成路径安全证明；Lua仍须按任务读取geometry/hazards和覆盖，决定方向、跳跃或停止。
- 观察后只放行一次move_input尝试，时长仍受原Schema的1..600游戏更新及jumpHoldUpdates范围约束；动作/调用/累计Lua时间预算不重置。降级失败不再获预检续行许可。成功回执只证明输入时长结束，程序须用真实末位置和新观察判断结果。
- 同一聊天轮跨程序保留待重观察约束，不能通过return后重开程序重试原导航；新程序必须重观察。新的明确用户轮次不继承旧降级许可。unknown、取消、超时、断连、身份变化、释放不确定仍停止，既有取消/清理/永久unknown栅栏不变。

## Lua 示例

```lua
local p=api('get_player_state',{})
local goal=p.player.position.x+32
local n=api('navigate_to_x',{targetX=goal})
if n.completed then return {navigation=n} end
-- 未获许可的失败会由宿主先终止；不依靠reason字符串决定安全。
if not n.continuationAllowed then return {navigation=n} end
local s=api('observe_scene',{layers={'geometry','hazards'}})
if s.data.actor.dead or not s.data.coverage.geometryComplete then
    return {navigation=n,stopped=true}
end
local x=s.data.actor.boundsWorldPixels.x
local m=api('move_input',{horizontal=x<goal and 'right' or 'left',
    vertical='none',jumpHoldUpdates=8,maxUpdates=40})
local after=api('get_player_state',{})
return {navigation=n,movement=m,finalX=after.player.position.x,
    moved=m.completed and after.player.position.x>=goal}
```

这是有界编排例子，不是固定游戏策略或安全路线保证。原导航failed仍在回执中，summarize.actionSuccess不会抹掉该失败；脚本正常返回也不证明任意用户目标完成。

## 验证与证据

正式包0.1.66，SHA-256：`29a6c53877acbc73220af8220a957c892d4c443924d271b49be4464cbe874179`；构建0错误0警告。

最终隔离原生场景在新profile、复制世界、双诊断旗标+marker内进行：仅初始化时给WellFed，稳定复现现有导航profile拒绝；之后没有位置/速度注入。真实Lua Worker→IPC→游戏线程导航failed→observe_scene→原生Direct Input40更新（前8更新跳跃）→读取实际位置。最终X从33616移至33697.473，超过目标33648；保留自然惯性，未声称精确到达或最终停稳。5次API、2次动作，没有逐Tick IPC或模型请求。

23个不同Python定向用例通过，包含真实Worker/loopback的成功分支、恢复分支、缺能力、缺/旧观察、导航直接重试阻断、降级再次尝试阻断、降级中取消、共享预算和unknown；原预检、取消、能力契约回归也通过。C#协议/原子/读取/适配/聊天组通过，适配器215项、包76项IL；NavigationOnly离线组通过。另在独立暂存快照重复运行8项新增测试通过，确认不依赖原有未提交的场景探索/Worker改动。未运行全量测试。

复现原生场景：

```powershell
pwsh -NoProfile -File scripts/build-companion.ps1 -OutputPath .local/agent-build
pwsh -NoProfile -File tests/verify_server_ai_direct_input_native.ps1 -TestScript native_navigation_fallback.py
```

[原始证据manifest](evidence/navigation-fallback/manifest.json)与同目录raw-evidence.zip保留最终/首轮原生结果、Lua/Worker轨迹、测试日志及初轮夹具终态重复改写失败。原始字节和ZIP entry逐项SHA-256核验；未修改失败记录。

限制：原生正向场景是已知profile拒绝，不代表任意障碍都能靠原始输入通过。no_route/no_progress分类及各种拒绝分支有定向离线覆盖，尚未逐一完成原生场景矩阵；没有图形Host & Play、真人同场、远端同步或真实LLM自主选择策略验收。未部署现用profile或启动模型请求，原有未提交的场景探索改动保留。
