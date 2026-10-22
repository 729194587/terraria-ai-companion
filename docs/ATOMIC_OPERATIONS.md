# 第一版原子操作（Mod 0.1.30）

参考 [Numen 仓库](https://github.com/Dwinovo/minecraft-numen/tree/1.21.1) 中“可组合工具、原生玩家规则、真实回执”的设计思路。开发站点 https://numen.dwinovo.cn/zh/develop/ 本次访问失败；没有声称核验其全部接口，也没有复制 Minecraft API 或源码。

## 调用链与坐标

`agent.py TOOLS / dispatch → atomic_operations.py 校验与异步编排 → Bridge.command → TCP → ControlState.Update → 游戏线程适配器 / 原有导航 → MovementSnapshot + endEvidence → Python 工具结果 → LLM`。

保留原有工具、旧协议和实验脚本。新包握手声明 `atomic_operations_v1`，二维导航另需 `landing_region_navigation_v1`；新操作不向旧包降级发送未经保护的输入。Agent 仍每次模型决策只执行一个工具，沿用工具/模型调用次数预算。新聊天取消旧任务，迟到聊天结果继续由原有 chatId/代次机制拦截。动作失败、超时、取消或未知后，本轮禁止继续发起动作，可继续读取状态。

世界位置、目标、实体半径：像素，Y 向下。Tile：整数格，每格 16 像素。快捷栏：0..9。

| LLM 操作 | 实际路径和完成语义 |
| --- | --- |
| `get_player_state()` | 复用 `Bridge.get_terrain()` 的同次 `state` 与 `grounded/supported/尺寸`；后者表示运动模型支持状态，不是可达性 |
| `observe_terrain()` | 直接复用 `get_terrain`，45×29 碰撞分类及同次玩家状态 |
| `inspect_tile(x,y)` | `query_atomic → AtomicOperations.Query → ObservationReader.ReadCell`；Tile 类型、Frame、半砖/坡、液体、墙、线、颜色及危险属性，越界/未加载知识状态明确 |
| `scan_entities(category,radius)` | `query_atomic → AtomicOperations.Query`；扫描客户端已知 NPC / item / 其他 player；category 为 all/npc/item/player，radius 为 1..1600 像素，最多 128 项，明确 matchedCount/truncated |
| `inspect_inventory()` | 复用 `ObservationReader.ReadPlayer` 背包/快捷栏/选中槽位/装备及附带玩家状态 |
| `get_world_state()` | 复用提取后的 `ReadEnvironment`，附生物群系深度标志、地表/岩层高度和世界尺寸 |
| `navigate_to_x(targetX)` | `start_atomic → ControlInput.Predictive → 原有 GameNavigation/NavigationTask/物理模拟与落点验证`；原有 settled/crossed 水平目标判据报告 arrived 才完成，不承诺精确 XY；30 秒上限 |
| `navigate_to_landing_region(minPlayerLeftX,maxPlayerLeftX,surfaceY)` | Agent/IPC 固定二维目标 → 实际玩家挂钩传入 `region` → 已有二维规划/动作执行/反馈；按 X 区间、脚底高度、安全支撑与停稳核验。30 秒上限，详见 [二维落脚导航](LANDING_REGION_NAVIGATION.md) |
| `select_hotbar_slot(slot)` | 游戏线程赋值并核对 `selectedItem`；物品忙碌时拒绝 |
| `use_item(target?)` | `start_atomic → PostUpdateRunSpeeds → PreItemCheck → 原生 ItemCheck → PostUpdate`；确认实际出现并结束一次物品动画周期。target 为世界像素 `{x,y}`；省略时朝当前方向 32 像素使用。成功只表示使用周期，不承诺治疗/命中/其他效果 |
| `mine_tile(x,y)` | 原生使用选中镐，Tile 消失才完成；不自动选工具/接近，不直接 KillTile、不绕过镐力/保护 |
| `attack_entity(entityRef)` | NPC 实例引用校验、原生选中物品使用、生命下降才完成；没有伤害证据的失活失败，不把消失当击杀；结果不声称伤害归因 |
| `interact_tile(x,y)` | 首版单人原版门 `WorldGen.OpenDoor/CloseDoor`，核对实际 Tile 类型变化；受阻/不支持显式失败 |
| `place_tile(x,y)` | 原生选中物品使用；空目标格变为预期类型，且选中槽位物品减少，二者同时成立才完成；不直接 PlaceTile、不免费造物 |
| `stop_action()` | `get_movement → cancel_move(actionId) → 等待游戏线程 endEvidence`，只取消本会话 Agent 动作，不接管手动 P 控制；无动作时显式 noOp |

`get_state/get_terrain` 保持原 IPC 名称；上表两个新感知工具是 Python 的统一入口，避免重复实现状态采样。其余感知不依赖摄像机和完整视口编码。实体客户端已知、未同步、不可见和没有实体不能等同；半径扫描也不是视觉/遮挡检测。

NPC 引用由世界会话、NPC 槽位和 `InstancePerEntity GlobalNPC` 实例 Token 组成；每次使用重新确认槽位对象、Token、active/可攻击状态。不能把旧观察的 instanceKey 或数字槽位作为 entityRef。引用在世界切换、NPC 重建/槽位复用后失效。

## 控制与结果

新 IPC：

```json
{"v":1,"id":1,"cmd":"query_atomic","operation":"inspect_tile","arguments":{"x":100,"y":200}}
{"v":1,"id":2,"cmd":"start_atomic","operation":"mine_tile","arguments":{"x":100,"y":200},"expectedGeneration":0,"chatId":"当前聊天事件ID"}
```

省略 chatId 时仍要求正确 expectedGeneration；Agent 总是传 chatId。双方校验有限数、整数、槽位、对象结构/未知字段和实体引用长度，游戏线程再校验当前世界边界、加载状态、距离、当前物品和目标。

复用 `MovementSnapshot`：唯一 actionId、session、generation、世界 ID、原有 750ms 租约与 100ms 心跳；绝对截止时间不随续租延长。新动作与旧限时移动/导航互斥，旧动作的完成/取消/断线/P/死亡均撤销同一控制源。等待原动作输入释放证据时不允许另一个原子动作接管。

终态 `completed / failed / timeout / cancelled`；IPC 丢失、所有权变化或缺少可信游戏线程释放证据返回 `unknown`。接收/启动 ACK 从不代表完成。游戏线程确认效果后仍需 `PostUpdateRunSpeeds_before_bridge_input` 或 `UpdateDead_after_revoke` 释放证据；Python 检查世界、更新号、阶段，不把暂停期间的终态 ACK 当成释放完成。取消不撤销已生成弹幕、已经发生的修改或尚未结束的原生动画。

物品目标只在本地玩家的 ItemCheck 范围内注入 mouse/tileTarget，PostItemCheck 和 DoUpdate finally 恢复。断线/撤销发生在注入与 ItemCheck 之间时再次检查动作状态，阻止迟到物品输入。远端玩家不执行这些逻辑。

## 首版明确限制

- 物品使用、挖掘、攻击、放置目前要求正常重力和默认 `GameViewMatrix.Zoom == (1,1)`，目标距玩家中心不超过 80 像素。当前适配器没有实现其他缩放/镜像重力的完整光标变换，返回明确失败；不会猜测瞄准。原生游戏仍会检查实际工具射程、使用条件、遮挡、镐力、冷却和资源。
- 挖掘需要选中的镐；斧/锤、墙壁挖掘、自动工具选择未实现。坚硬/受保护 Tile 若原生拒绝，截止时间后 timeout；不伪造具体原生拒绝原因。
- 放置只接入消耗型、非 ModItem、非 frame-important 的单格方块。多格家具、墙、ModItem 自定义放置尚缺放置锚点与专门成功判据。
- 交互只支持单人原版门。多人门尚缺服务器请求与确认状态机；宝箱/告示牌/机关/ModTile 尚缺各类交互和状态确认适配。明确返回 unsupported，不泛化为成功。
- 攻击是有界的当前物品攻击，验证一次目标生命下降；不自动追击/换武器，也不承诺击杀。多人环境使用客户端已知状态，不能证明服务器最终结果或排除其他伤害来源。
- 导航继续受原有普通角色/装备/Buff/地形物理支持范围约束。其他物品动作上限 10 秒，均可取消；超时不表示原生物品效果已经撤销。
- Mod 装备槽/自定义物品内部状态、服务器事件进度等沿用既有 unknown 标注；不声称全量 Mod 兼容。

## 验证与产物

- Python：**242 项通过**，含新增跨语言二维调用链回归。`python -B -m unittest discover -s tests -v`，新增参数/能力、感知复用、终态/证据、启动取消竞态、精确取消和 Agent dispatch/失败禁止重试测试。
- C#：**全部通过**。`pwsh -NoProfile -File tests/run_csharp_tests.ps1`，原有 IPC/导航/生命周期回归；新增真实 ControlState 协议/互斥/租约/绝对超时/释放边界测试，以及真实 AtomicOperations 适配器对游戏 API 替身的选择/范围/目标/放置消耗/伤害/门/光标恢复测试。替身不模拟真实 Terraria ItemCheck、物理或联机同步。
- Mod 构建：**0 错误、0 警告**。tModLoader 自带 .NET，`-server -build CompanionBridge -tmlsavedirectory .local/landing-navigation-build`；输出 `.local/landing-navigation-build/Mods/CompanionBridge.tmod`。
- 本轮未复制包到任何客户端配置、未部署、未启动新包、未进行真实游戏验收；离线 PASS 不等于原生物品时序/光标或多人同步已实测。

真实游戏验收仍需在用户选择的测试世界加载新包：核对 0.1.30 和能力握手；依次读取感知、选槽、使用物品、挖近处土块、放回一格、攻击近处敌对 NPC、开关单人门、水平导航；每项核对画面与 effect/endEvidence；另测远目标/空物品/过期 entityRef、Ctrl+C/新聊天/P/断线/死亡、暂停和多人观察。该步骤需要部署新包，本任务按“不部署”要求保留为待验收。
