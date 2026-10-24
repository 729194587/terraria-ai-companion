# 原子操作（Mod 0.1.40）

0.1.40更新运行时预算、任务日志和原生异常证据，见 [可观测性](LUA_OBSERVABILITY.md)；没有新增游戏动作。

0.1.39 新增单人背包持用、整件交换、指定数量搬运/拆分/堆叠及槽位token校验，见 [背包操作](INVENTORY_OPERATIONS.md)。

0.1.38 新增紧凑状态、内容解析、目标前置检查、扫描筛选/距离排序，以及 Lua 内有界探索和可取消等待。接口及验证边界见 [基础能力扩展](ATOMIC_FOUNDATIONS.md)。

0.1.37 已接入墙、锤击与白名单交互的多人服务器确认，服务器和所有客户端必须使用同版本 CompanionBridge（side=Both）。实现与联机验收方法见 [多人原子确认](MULTIPLAYER_ATOMIC_CONFIRMATION.md)。下方 0.1.35 验证记录保留为历史结果。

参考 [Numen 仓库](https://github.com/Dwinovo/minecraft-numen/tree/1.21.1) 中“可组合工具、原生玩家规则、真实回执”的设计思路。本轮对照了 Numen 当前 `1.21.1` 分支的 [ScanApi](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/numen/common/src/main/java/com/dwinovo/numen/tools/perception/ScanApi.java)、[InvApi](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/numen/common/src/main/java/com/dwinovo/numen/tools/inventory/InvApi.java)、[UseApi](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/numen/common/src/main/java/com/dwinovo/numen/tools/interact/UseApi.java) 及 [原子职责 ADR](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/docs/adr/control/0005-atomic-commands-composed-by-scripts.md)。仅借鉴 scan.blocks 的有界已加载目标发现、inv.items/count 的紧凑查询与 use.block/hit 的真实交互回执职责；未复制 Minecraft 的三维坐标、移动/换工具策略、Lua 或 API 实现。

## 调用链与坐标

`agent.py TOOLS / dispatch → atomic_operations.py 校验与异步编排 → Bridge.command → TCP → ControlState.Update → 游戏线程适配器 / 原有导航 → MovementSnapshot + endEvidence → Python 工具结果 → LLM`。

保留原有工具、旧协议和实验脚本。新包握手声明 `atomic_operations_v1`、`atomic_extensions_v1` 和 `server_confirmed_interactions_v1`，二维导航另需 `landing_region_navigation_v1`；新操作不向旧包降级发送未经保护的输入。Agent 仍每次模型决策只执行一个工具，沿用工具/模型调用次数预算。新聊天取消旧任务，迟到聊天结果继续由原有 chatId/代次机制拦截。动作失败、超时、取消或未知后，本轮禁止继续发起动作，可继续读取状态。唯一例外为明确的挖掘距离不足，Agent 可执行一次有界恢复，详见 [物品使用与挖掘恢复](ITEM_USE_AND_MINING_RECOVERY.md)。

世界位置、目标、实体半径：像素，Y 向下。Tile：整数格，每格 16 像素。快捷栏：按游戏键帽1..9、0；0是第十格，内部背包索引另行标注。

| LLM 操作 | 实际路径和完成语义 |
| --- | --- |
| `get_player_state()` | 复用 `Bridge.get_terrain()` 的同次 `state` 与 `grounded/supported/尺寸`；后者表示运动模型支持状态，不是可达性 |
| `observe_terrain()` | 直接复用 `get_terrain`，45×29 碰撞分类及同次玩家状态 |
| `inspect_tile(x,y)` | `query_atomic → AtomicOperations.Query → ObservationReader.ReadCell`；Tile 类型、Frame、半砖/坡、液体、墙、线、颜色及危险属性，越界/未加载知识状态明确 |
| `scan_entities(category,radius)` | `query_atomic → AtomicOperations.Query`；扫描客户端已知 NPC / item / 其他 player；category 为 all/npc/item/player，radius 为 1..1600 像素，最多 128 项，明确 matchedCount/truncated |
| `inspect_inventory(mode?,item?)` | 默认 selected，仅选中槽；hotbar 返回十格；item 精确解析标识/本地名称，返回数量与槽位；full 才复用完整 `ObservationReader.ReadPlayer`。详见下文兼容说明 |
| `scan_tiles(minX,minY,maxX,maxY,tileTypes,maxResults?)` | 游戏线程复用 `ObservationReader.ReadCell`，Tile 闭区间矩形最多 4096 格，返回目标坐标/类型，数量/截断/未知格状态 |
| `get_world_state()` | 复用提取后的 `ReadEnvironment`，附生物群系深度标志、地表/岩层高度和世界尺寸 |
| `navigate_to_x(targetX)` | `start_atomic → ControlInput.Predictive → 原有 GameNavigation/NavigationTask/物理模拟与落点验证`；原有 settled/crossed 水平目标判据报告 arrived 才完成，不承诺精确 XY；30 秒上限 |
| `navigate_to_landing_region(minPlayerLeftX,maxPlayerLeftX,surfaceY)` | Agent/IPC 固定二维目标 → 实际玩家挂钩传入 `region` → 已有二维规划/动作执行/反馈；按 X 区间、脚底高度、安全支撑与停稳核验。30 秒上限，详见 [二维落脚导航](LANDING_REGION_NAVIGATION.md) |
| `select_hotbar_slot(slot)` | slot为游戏键号1..9、0，1映射首格索引0，0映射第十格索引9；核对 selectedHotbarKey。需hotbar_keys_v1 |
| `drop_through_platform()` | 原生Down下穿当前水平单向平台，检查同X下方64px内首个安全落点，离开当前层即松Down，实际落地停稳后完成；3秒截止，需platform_drop_v1 |
| `use_item(target?)` | `start_atomic → PostUpdateRunSpeeds → PreItemCheck → 原生 ItemCheck → PostUpdate`；确认实际出现并结束一次物品动画周期。target 为世界像素 `{x,y}`；省略时完全保留原生鼠标与 Tile 目标，不执行精确瞄准检查。有 target 时按 ItemCheck 已反缩放的坐标空间注入，支持当前缩放。成功只表示使用周期，不承诺治疗/命中/其他效果 |
| `mine_tile(x,y)` | 原生使用选中镐，Tile 消失才完成；不自动选工具/接近，不直接 KillTile、不绕过镐力/保护 |
| `attack_entity(entityRef)` | NPC 实例引用校验、原生选中物品使用、生命下降才完成；没有伤害证据的失活失败，不把消失当击杀；结果不声称伤害归因 |
| `interact_tile(x,y)` | 单人门走 `WorldGen.OpenDoor/CloseDoor`，其他白名单走原生玩家交互；多人截获原生请求并在服务器正常网络处理器执行，回执与客户端同步一致后完成。按门类型、宝箱索引、开关/拉杆 Frame 分别核验 |
| `mine_wall(x,y)` | 选中纯锤 → 原生 ItemCheck 挖墙分支 → `CanPlayerSmashWall/PickWall`；初始非空墙实际归零才完成 |
| `place_wall(x,y)` | 选中墙物品 → 原生 ItemCheck/PlaceThing_Walls → `WorldGen.PlaceWall`；目标墙类型正确且选中槽恰好消费一件才完成 |
| `hammer_tile(x,y)` | 纯锤 → 原生 ItemCheck/TryPoundingTile → PoundTile/SlopeTile；同类型方块 HalfBlock/Slope 实际变化才完成 |
| `place_tile(x,y)` | 当前缩放下通过共享ItemAim注入目标，原生选中物品使用；空目标格变为预期类型，且选中槽位物品减少，二者同时成立才完成；不直接 PlaceTile、不免费造物 |
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

- 无目标 `use_item` 保留原生鼠标，不要求默认缩放或正常重力，也不执行虚构目标的距离检查。有目标 `use_item` 支持当前缩放，仍要求正常重力、目标已加载且距玩家中心不超过 80 像素。挖掘和首版单格方块放置支持当前缩放，仍要求正常重力和80像素限制；攻击继续要求默认 `GameViewMatrix.Zoom == (1,1)`。0.1.34已核验普通放置的原生目标传递；特殊武器/弹道的完整缩放适配未在本次覆盖。原生游戏仍会检查实际工具射程、使用条件、遮挡、镐力、冷却和资源。
- `mine_tile` 仍需要选中的镐，墙用独立的 `mine_wall`，形状用 `hammer_tile`；不自动选工具。坚硬/受保护 Tile 若原生拒绝，截止时间后 timeout；不伪造具体原生拒绝原因。
- `place_tile` 仍只支持原有单格方块；墙走 `place_wall`。多格家具、ModItem 自定义放置未实现。
- 交互支持原版门、未锁宝箱、开关和拉杆；多人要求同版本服务器确认。锁箱解锁、假宝箱、告示牌、梳妆台、其他机关与 ModTile 未接入，明确失败。开关与拉杆只证明自身 Frame 切换，不证明下游电路效果。
- 攻击是有界的当前物品攻击，验证一次目标生命下降；不自动追击/换武器，也不承诺击杀。多人环境使用客户端已知状态，不能证明服务器最终结果或排除其他伤害来源。
- 导航继续受原有普通角色/装备/Buff/地形物理支持范围约束。其他物品动作上限 10 秒，均可取消；超时不表示原生物品效果已经撤销。
- Mod 装备槽/自定义物品内部状态、服务器事件进度等沿用既有 unknown 标注；不声称全量 Mod 兼容。

## 第一轮扩展参数、回执与支持范围

四个新工具及修改后的 `inspect_inventory`、`interact_tile` 要求能力 `atomic_extensions_v1`。墙、锤击与 interact_tile 还要求 `server_confirmed_interactions_v1`（包括单人调用，以免旧包语义混用）。旧 Mod 缺少所需能力时 Python 在发送前拒绝，不将新语义发送给旧适配器。旧 `mine_tile/place_tile/use_item` 参数及语义不变。这些原子工具共用现有 Agent dispatch；没有新执行器或控制源。

### scan_tiles

```json
{"minX":100,"minY":200,"maxX":131,"maxY":215,"tileTypes":[0,1],"maxResults":64}
```

所有坐标是非负整数 Tile 格，闭区间，X 向右/Y 向下，每格 16 像素；不是玩家/屏幕像素。必须提供明确边界和 1..32 个不重复的类型 ID（0 是土块，不是空气），类型来自观察字典或 inspect_tile；游戏线程按当前 TileLoader.TileCount 再校验类型。拒绝反向边界、世界越界和面积超过 4096 格的请求，不自动裁剪。maxResults 默认 128，范围 1..256。

扫描整个有界区域；不因返回达到上限而提前结束，因此 matchedCount 是可读格的精确匹配数。0.1.38起按玩家中心距离、Y、X排序后截断，items 包含 `{x,y,typeId,contentKey,distancePixels}`；旧包保持原先的行优先输出。附 returnedCount、matchedCount、truncated、scannedCount、unknownCount、complete 和请求 bounds。无匹配且全部可读时 status=not_found；有未加载格时 status=partial_unloaded（即使 items 为空），不能声称那些格没有目标。truncated 仅表示匹配结果被数量上限截断，独立于未知格。查询异常由 IPC 返回 query_failed；未进世界 available=false；越界/无效类型有独立 reason。查询不获取控制权或附带完整观察。

### inspect_inventory

- `{}` 或 `{"mode":"selected"}`：只返回 selectedSlot、selectedHotbarKey 和 inventory 中的选中槽。保留物品 typeId、contentKey、当前语言 name、stack、slot、hotbarKey，以及后续选工具所需的 pick/axe/hammer/createTile/createWall。
- `{"mode":"hotbar"}`：返回十格快捷栏及选中信息。数组 slot 0..9 与键帽 1..9、0 不同，仍使用 hotbarKey 映射。
- `{"mode":"item","item":"Terraria/StoneBlock"}` 或传当前语言精确名称：在已加载 ContentSamples 物品注册表中做不区分大小写的精确匹配；同时支持 ModItem.FullName。不给模型硬编码 ID 的责任。不作模糊匹配或静默选歧义项。未知名称 status=unknown_item；同名多类型 status=ambiguous_item，最多返回 32 个候选及截断标记。已知物品未持有为 status=ok/count=0/slots=[]。
- item 成功返回 typeId/contentKey/name、所有匹配栈总 count 与 `{slot,hotbarKey,stack}` 列表，不附完整背包。范围是 player.inventory（包括快捷栏、硬币和弹药槽）；不含装备、银行、箱子、鼠标物品。
- `{"mode":"full"}`：原有完整 ReadPlayer 输出，包括背包、装备和玩家附带状态。Mod 自定义装备槽仍未知。item 字段仅允许且必须与 mode=item 一起出现，所有未知字段拒绝。

**兼容影响：** 无参 inspect_inventory 从完整输出改为选中槽紧凑输出，这是本轮明确要求的默认值调整。依赖完整 inventory/equipment 数组的调用方必须改传 mode=full；依赖选中槽镐力的 Agent 仍可读取 inventory[].use.pick。握手能力阻止旧包返回不同默认语义。其他工具不受此变更影响。

### 墙与锤击

三项动作均使用当前选中物品、正常重力、距玩家中心到目标格中心 <=80px、目标已加载；支持现有 ItemCheck 坐标空间和当前缩放。Native ItemCheck 继续检查实际射程、noBuilding、物品使用条件、冷却、工具力与 ModLoader 钩子。最大 10 秒，复用 actionId/session/generation/worldSessionId、750ms 租约、续租、绝对截止、精确取消和游戏线程输入释放证据。新动作每个玩家更新后清除 controlUseItem，鼠标和 tileTarget 在 PostItemCheck/finally 恢复；世界会话变化会取消，不能取得旧世界释放证据时 Python 返回 unknown。

| 操作 | 前置条件与成功证据 | 明确拒绝或失败 |
| --- | --- | --- |
| mine_wall | 选中非 ModItem 纯锤（hammer>0、pick=axe=0，无放置效果）；目标有墙且无前景。3×3 邻域已加载并通过原生 CanPlayerSmashWall 才注入输入；防止原生改选邻墙。实际 wallType 从初始非零变为 0 才完成 | target_wall_missing、selected_item_requires_pure_hammer、unsupported_wall_behind_foreground_tile、wall_not_smashable_by_native_rules、邻域未知、超距。墙被替换或出现前景会失败；其他原生拒绝无法判因时截止后 timeout |
| place_wall | 原版消耗型 createWall>0 墙物品，createTile<0，stack>0，目标墙为空。预期墙出现且同一选中槽恰好消费一件，包含最后一件变空气 | 目标已有墙/错误物品、物品不足、超距、原生放置拒绝。目标出现错误墙或未消费为 wall_placement_not_verified；资源先耗尽但效果未出现为 item_exhausted_before_effect_verified。拒绝 autoPaint；有多件物品时检查5×5已加载邻域并拒绝原生会自动补相邻孔洞的布局，防止额外消费与修改 |
| hammer_tile | 同样的纯锤，原版普通实心方块或平台、未虚化、无背景墙。原生锤击后同一类型的 HalfBlock 或 Slope 改变即完成，证据含前后形状和 Frame | 空格、ModTile、家具、tileHammer 破坏型目标、带背景墙或混合镐/斧工具为 unsupported/具体失败。目标消失、变类型、虚化或出现墙为 hammer_target_invalidated。普通方块仅 Frame 美术变化不当作形状成功；无效果截止后 timeout |

不直接修改世界 Tile/Wall 数据；不会用 KillTile 模拟挖墙或免费 PlaceWall 模拟放置。测试中的状态修改仅在离线替身中用于验证结果判据。原生 Mod/装备效果可能影响实际消费和使用行为，不承诺全部兼容；即使动作失败/取消，也不能声称原生副作用被回滚。

### interact_tile

参数仍是 `{x,y}` Tile 格，选择对象任意已占用格。游戏线程先验证原版白名单和非虚化，使用 TileObjectData.TopLeft 求锚点并检查占用范围全部已加载、同类型、非虚化：关门1×3、开门2×3、宝箱2×2、开关1×1、拉杆2×2。检查点击格中心的80px限制及 `Player.InInteractionRange(...,TileReachCheckSettings.Simple)` 原生范围，不把宝箱专用 hitbox 范围检查误用于其他对象。

- 门：保留原有 WorldGen.OpenDoor/CloseDoor；原生返回变化且点击格原类型实际变化才 completed。受阻/未变化为 door_blocked_or_unchanged。没有自动改开门方向或挖障碍。
- 宝箱：仅 Containers/Containers2 真宝箱；锁箱明确 locked_chest_unsupported，缺 Chest.FindChest 记录明确 chest_record_missing。通过 Player.TileInteractionsCheck → 原生 TileInteractionsUse 完成开/关，包含原生 UI 及交互行为；核对 player.chest 是锚点记录的索引，或原已打开同一箱时变为 -1，并核对 chestX/chestY。证据包含前后索引、open 和 footprint。不开包查询箱内容、不转移物品、不自动解锁或消费钥匙。
- 开关（TileID.Switches=136）：原生交互走 Wiring.HitSwitch，核对自身 Frame 切换。它与压力板/瞬时按钮不同，不泛化其他机关。
- 拉杆（TileID.Lever=132）：从任意占用格进入原生 HitSwitch，核对2×2四格仍是原类型且每格 Frame 发生切换，证据包含全部前后 Frame 和点击/锚点位置。开关/拉杆的下游 TripWire 仍由原生负责；本接口仅证明对象切换，不核验整条电路。

原生交互所需 releaseUseTile/tileInteractAttempted 临时设定后在 finally 立即恢复。原生钩子/菜单拒绝而未产生对应状态变化时失败，调用原生函数本身不构成成功。残缺占用范围为 invalid_interaction_footprint，未知类型为 unsupported_interaction_type。多人通过相应原生消息 19（门）、31/33（宝箱开/关）、59（开关/拉杆）进入服务器游戏线程；独立核验服务器效果和客户端同步，详见 [多人确认](MULTIPLAYER_ATOMIC_CONFIRMATION.md)。其他玩家占用箱子、目标变化、未发送区块均明确失败。

### 0.1.35 历史验证（2026-10-22）

- Python：`python -B -m unittest discover -s tests -v`，272 项通过。新增扫描/背包严格参数、能力门槛、Agent dispatch、各终态及每项新动作取消测试。
- C#：`pwsh -NoProfile -File tests/run_csharp_tests.ps1`，完整项目脚本通过；新增真实协议解析、查询失败、各新动作截止/续租/取消/释放及真实 AtomicOperations 适配器替身测试。
- 原生 IL：`pwsh -NoProfile -File tests/run_native_item_aim_tests.ps1`，执行已安装游戏的输入/缩放转换 IL，另外检查原生工具范围/权限/冷却、PickWall、锤击形状/释放、PlaceWall、宝箱及开关/拉杆调用契约。**未执行原生世界修改、ItemCheck时序或游戏内交互**。
- Mod：0.1.35，编译 0 错误、0 警告，包为 `.local/atomic-extensions-build/Mods/CompanionBridge.tmod`。只在独立构建目录生成，未部署、未运行客户端、未覆盖存档/配置/测试世界。
- 日志：`.local/atomic-extensions-{python-tests,csharp-tests,native-tests,build}.log`。受限环境首次 C# 回环套接字受阻、Python 异步用例停滞，切换获准的本地测试运行后通过；第一次 Mod 命令因启动目录错误缺少 ReLogic，改用 tModLoader 工作目录后构建通过。

真实游戏验收待办：在用户指定的测试世界加载包后核对0.1.35和atomic_extensions_v1；验证扫描类型/截断/未加载、中文物品名与contentKey/零数量、十格快捷键；选纯锤挖可拆墙/拒绝封闭天然墙，持墙物品验证原生邻接条件和最后一件消费，验证自动补墙布局拒绝；普通方块/平台锤击形状与时序；从门、宝箱、拉杆不同占用格交互，检查受阻门/锁箱/开关Frame/下游电路（后者只人工观察）；分别测试超距/工具错误、P/新聊天/Ctrl+C/死亡/断线/切世界/暂停后的取消与释放。非默认缩放也需真实验收。上述是 0.1.35 的单人验收范围；0.1.37 的多人验收清单以 [多人确认](MULTIPLAYER_ATOMIC_CONFIRMATION.md) 为准，尚未完成真实游戏验收。

## 验证与产物

以下为前序版本的记录；0.1.32 本次修复的验证和独立构建目录见 [物品使用与挖掘恢复](ITEM_USE_AND_MINING_RECOVERY.md)。

- Python：**242 项通过**，含新增跨语言二维调用链回归。`python -B -m unittest discover -s tests -v`，新增参数/能力、感知复用、终态/证据、启动取消竞态、精确取消和 Agent dispatch/失败禁止重试测试。
- C#：**全部通过**。`pwsh -NoProfile -File tests/run_csharp_tests.ps1`，原有 IPC/导航/生命周期回归；新增真实 ControlState 协议/互斥/租约/绝对超时/释放边界测试，以及真实 AtomicOperations 适配器对游戏 API 替身的选择/范围/目标/放置消耗/伤害/门/光标恢复测试。替身不模拟真实 Terraria ItemCheck、物理或联机同步。
- Mod 构建：**0 错误、0 警告**。tModLoader 自带 .NET，`-server -build CompanionBridge -tmlsavedirectory .local/platform-fixes-build`；输出 `.local/platform-fixes-build/Mods/CompanionBridge.tmod`。
- 本轮未复制包到任何客户端配置、未部署、未启动新包、未进行真实游戏验收；离线 PASS 不等于原生物品时序/光标或多人同步已实测。

真实游戏验收仍需在用户选择的测试世界加载新包：核对 0.1.31 和能力握手；依次读取感知、选槽、使用物品、挖近处土块、放回一格、攻击近处敌对 NPC、开关单人门、水平导航；每项核对画面与 effect/endEvidence；另测远目标/空物品/过期 entityRef、Ctrl+C/新聊天/P/断线/死亡、暂停和多人观察。该步骤需要部署新包，本任务按“不部署”要求保留为待验收。
