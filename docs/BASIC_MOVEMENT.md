# 0.1.23 Agent 基础限时移动

本文记录 0.1.23 基础版本。0.1.24 阶段 A 已扩展为默认 7 次工具／8 次模型调用、6000ms 累计移动预算，并增加观察前置／后置验证及可选坐标目标；参见 [阶段 A](STAGE_A_MOVEMENT.md)。单次 50–2000ms、750ms 控制租约和按动作 ID 取消保持不变。

仅向 LLM 增加平地水平输入工具，复用 `ControlState` 的 External 模式、750ms 租约和 `CompanionPlayer.PostUpdateRunSpeeds` 输入执行器。没有接入自动寻路、跳跃、越障、攻击或无限行动循环。

## 工具调用

```json
{"name":"move","arguments":{"direction":"left","duration_ms":500}}
{"name":"move","arguments":{"direction":"right","duration_ms":1000}}
{"name":"stop_movement","arguments":{}}
```

`direction` 仅为 left/right，`duration_ms` 为 50–2000 的整数。工具描述和运行时均检查参数，C# 再次检查。推荐先调用 `get_observation`，执行一次 move，再调用 `get_observation` 核对位置；旧 get_state/get_terrain 和聊天、被动系统提示不变。默认每轮仍最多 4 次模型调用、3 次工具调用，不自动重试失败动作。

结果包含 `status`、`completed`、`cancelled`、`reason`、动作 ID、请求时长、实际经过时间，以及可靠时的 `beforePositionWorldPixels` / `afterPositionWorldPixels`。坐标为角色碰撞框左上角、世界像素、Y 向下。

- completed：游戏线程已到达动作截止时间并撤销输入。
- cancelled：游戏线程确认动作因停止、死亡／幽灵等失去控制权限、租约失效或手动接管而结束。
- rejected：服务端明确拒绝新动作，或开始前读取失败；没有发起新的可确认动作。
- unknown：启动或结束回执丢失、连接错误或动作所有权变化，不能确认最终状态；completed/cancelled 为 null，不伪造成功或取消。
- stop_movement 在没有限时动作时返回 idle；已有终态则返回该终态，不接管手动模式或停止其他导航任务。

completed 只代表水平按键注入结束，不保证移动了指定距离、已停稳、没有撞墙或没有因地形掉落。第一版需在安全普通平地验证；不自动判断通路、不避险。结束后恢复原有真人输入，速度仍由 Terraria 自身摩擦和物理处理。

## 安全机制与协议

握手新增 `bounded_movement_v1`。新增命令复用现有 v1 JSON Lines：

- `get_movement`：只读，返回当前控制 generation、该连接的最近动作和当前玩家状态，不续租。
- `move_for`：direction=-1/1、durationMs=50..2000、expectedGeneration 必填。Agent 另附当前 chatId，服务端拒绝已被较新聊天替代的迟到动作。已有手动／外部控制时不抢占。
- `renew_move`：只为同一个仍在运行的 actionId 续租，不改变动作绝对截止时间。
- `cancel_move`：只取消指定 actionId。旧 ID 不会停止新动作；已取消／完成时幂等，不重新取得控制。

C# 在游戏线程创建一次动作，记录开始状态及单调时钟截止时间；即使一直续租也最多 2 秒。Python 约每 100ms 查询并续租，暂停续租 750ms 后 C# 将动作标为 lease_expired 并撤销输入。过期心跳不能重新启动动作。旧 input/navigate 等不能在限时动作中修改输入并绕过截止时间，原 stop 仍可停止它。

死亡／幽灵、手动接管、断线、世界重置等沿用既有撤销／清理入口，同时终止限时动作；死亡保留只读连接，复活不恢复旧动作。动作 ID 不在重置或连接切换时复用，反馈不泄漏其他会话的旧动作。

新玩家聊天会取消旧 Agent 任务。Python 等待不可取消的启动线程返回其短回执，再停止确切的 actionId，避免旧启动请求在清理后迟到执行；较新 chatId 和 generation 也会阻止迟到启动。若清理通信失败，不再续租，依靠游戏线程的租约和绝对截止时间。工具运行期间不等待另一次模型响应，模型超时不能延长动作。

所有截止／撤销在游戏线程更新时执行。若游戏线程完全暂停，则期间没有物理更新；恢复时先处理过期状态，再执行新输入。位置是该更新入口复制的状态，不是渲染阶段原子快照。若死亡钩子没有可靠的结束位置，After 保持 null，不用随后复活的位置冒充动作终点。

## 文件与验证

`ControlState.cs`：协议参数、限时动作及结果、绝对截止时间与租约、取消、代次保护。`CompanionBridge.cs` / `CompanionSystem.cs`：可用时将撤销时的玩家快照传入。`TcpControlServer.cs`：新增能力声明。`python/movement.py`：异步工具封装、续租、取消与明确的不确定状态。`python/agent.py`：工具注册与原抢占链路接入。`python/control.py`：保留服务端拒绝状态以区别明确拒绝和回执丢失。

新增 C# 离线测试覆盖左右输入、不跳跃、独立截止时间、心跳不延长总时长、租约过期、主动取消、旧取消 ID、死亡／复活、手动接管、陈旧聊天／队列命令、序列化位置和终态。Python 使用 mock 验证前后位置、参数边界、主动停止、租约取消、完成与续租竞态、断连、启动回执丢失、所有权变化、启动中被抢占，以及 observe→move→observe 和旧回复丢弃。

构建输出 `.local/basic-movement-build/Mods/CompanionBridge.tmod`。不自动启动游戏、部署、提交或推送。

本次验证：全部 171 项 Python 离线测试通过（新增移动测试 10 项）；C# 限时移动及全部原有控制／观察／聊天／运动回归通过；compileall、git diff --check 通过。本机 tModLoader 2026.08.3.0 stable / Terraria 1.4.4.9 构建 0.1.23 完成，0 错误、0 警告。没有连接真实模型或游戏进行动作验证。

2026-10-21 用户真实游戏验证：短时左右移动正常，结合观察接近玩家成功；死亡期间不能移动，复活后无须重启 Agent 即可执行新的移动请求。向远处向导持续移动时触发每轮 3 次工具调用上限，当前不支持保证到达目标的连续移动任务。

真实游戏仍待验证：移动中发送“停止”或新消息、P 接管、移动中死亡、暂停／失焦及 Python 中断；确认旧动作不恢复，碰墙不被误报成“已到达”。这些取消路径的离线测试已通过，尚不能视为真实游戏验收通过。
