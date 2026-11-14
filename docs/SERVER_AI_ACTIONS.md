# Server AI 动作接口

本文描述服务器独立 AI Player 可使用的移动、物品使用、挖掘和放置相关动作边界。实际可调用集合以 IPC 能力握手和 [Server AI 能力契约](SERVER_AI_CAPABILITIES.md) 为准。

## 执行原则

导航按游戏更新分片继续可恢复的搜索。时间、节点、局部覆盖和开路预算耗尽以
`timeout` 报告，并保留具体原因。未知地形单独返回 `unknown`。
当前覆盖内穷尽安全候选只说明该覆盖及运动模型内没有验证到路线。
碰撞、坠落、落地和输入释放仍由原有核验约束。

挖掘按目标选择镐力或斧力，镐斧合一工具可以走两种原生分支。
当前动作仍限制于可核验的镐挖目标和树干，锤击、持续施放和弹射工具不适用。
任务在扣血或接触岩浆时中断。附近 NPC 必须具备原生伤害资格，并已接触玩家，
或正在锁定玩家且满足接近条件。能穿墙的 NPC 不受视线条件限制。

- 所有动作针对绑定的服务器 AI Player；不操作客户端本地玩家或真人玩家。
- 请求经 Python 校验和 Server AI IPC 进入游戏线程，再复用 Terraria 原生输入、物品、导航、挖掘和放置路径。
- 原子动作只表达单一意图，不隐式增加寻路、换工具、邻格补偿或无限重试。需要持续推进的复合工作使用 [游戏侧任务运行时](M1_TASK_RUNTIME.md)。
- 动作受理、原生调用和输入释放都不等于效果完成。每种动作按自己的世界/物品/位置证据核验。
- 取消、超时、断连、身份变化和 unknown 保留不同语义；结果不明时不自动重放。
- 导航计划和观察快照有采样时间、revision 与适用 profile；预测可达不等于实际到达，必须确认真实落脚和停稳。
- 未知装备、Buff、Mod 行为或引擎版本不应被假设为受支持能力。

## 自主火把

Dedicated Server 默认在游戏更新中检查自主补光。每 120 个游戏更新，角色空闲、安全且中心亮度低于 0.45 时，可以在原生触及范围内用普通火把补光。选址要求空格、无液体、合法支撑及视线。已有可见点亮火把按 8 Tile 间距筛选，近期尝试位置有短期预留。

维护行为等待已受理的 Agent 根任务结束。外部动作可在维护期间受理，等待当前原生使用周期和库存清理后开始。暂停空闲脉冲不会发起补光。普通背包火把只会借用空闲快捷栏，结束时恢复原选中槽，并核对能安全恢复的库存布局。服务端日志记录原生放置事件、实际消耗与输入释放。

## 任务级挖掘

`mine_target` 是游戏侧高层任务，不是普通单格原子挖掘。优先传入 `observe_scene` 返回的结构化目标对象；任务运行时负责选镐、站位、导航、重验和挖掘。生命周期、令牌、截止、效果证据及限制见 [游戏侧任务运行时](M1_TASK_RUNTIME.md)。

运行端声明 `server_ai_excavation_v1` 时，`mine_target({target=target, cluster=true})` 或 `mine_target({x=120, y=80, cluster=true})` 从种子快照同种四连通矿簇。上限为 18 格，超过上限会在动手前拒绝。任务先在当前安全站位连续挖掘可触及的目标，必要时按站位覆盖数和移动、挖掘预计更新数选择下一个站位。

批次在 `task.result.evidence.targets` 给出每格的状态和原生破坏回执。任务结果里的 `outcome` 区分完成、部分完成、中断与失败。外部变化的方块记为 `changed`。连续挖掘子任务 `excavate_tiles` 报告预计与实际更新数，换镐前先释放旧工具的使用周期。掉落入包需另行观察。

`server_ai_navigation_v1` 开放 `navigate_to({x,y})`。坐标单位为世界像素，Y 向下，点位于预期落脚面。游戏侧选择移动、挖掘或放置，并在同一个任务内串行执行。成功回执要求真实脚底覆盖目标点、落脚安全、停稳和输入释放。局部搜索受节点、时间和动作预算约束。

导航在当前站位比较物理搜索得到的移动路线与有界地形改造候选。改造候选沿用原生地形分类、工具与放置前置检查，模拟局部净空和支撑；每次改造后重新读取实际世界。自动开路限普通运动角色、干燥安全地形和局部平面，搭路使用快捷栏内合适的单格材料。`mine_target` 复用同一个导航控制器，保留原有挖掘站位与目标核验规则。

内部地形单元保留 Tile 类型和致动状态，碰撞形状仍单独判定。藤蔓等可穿过的格不会提供落脚支撑，MCP 地图继续使用紧凑的形状网格。挖矿开路可用原生碰撞预测侧移后的较低落点，也可在虚拟移除支撑后预测多格垂直下落。预估落点使用当前玩家的落伤阈值和可检查的地形范围。每次实际清障后核对落脚并重新搜索站位。装饰物的邻接关系本身不阻止开路，物体锚点、上方坠落方块和危险地形仍受保护。

`navigate_to` 会在同一次请求内执行安全的部分路线。每次交接根据真实位置、速度和当前碰撞验证下一条边。搜索切片耗尽且没有安全前缀时，移动保持松开；下一个游戏更新核对完整运动状态、物理参数及地形签名后，接续已有搜索前沿与候选游标。状态变化会废弃旧搜索。搜索数据和原生碰撞访问都在游戏线程内。

搜索拒绝缓存与执行失败记忆分别维护。缓存使用完整位置、速度、着地和跳跃状态；失败记录含运动类型、输入、预计落点、实际落点、原因及沿预测轨迹采样的地形签名。每个根任务独立持有记忆，移动子动作共享；地形、物理参数变化或到期后重新评估。候选拒绝缓存最多 256 项，实际失败最多 64 项，有效期为 120 游戏更新。搜索自身保留精确状态与开放集，局部状态存储达到容量时可交出部分路线或释放该前沿，继续受根任务预算约束。局部前沿穷尽表示当前物理窗口内没有安全接续，不能据此声称全局不可达。

同一站位的连续必要障碍共用原生挖掘周期，每格独立核验。需要落脚、移动或放置支撑时，结束当前挖掘周期并重新观察。18 格限制用于审核当前局部开路方案，已验证的累计改造次数不再截断行程。反复失效的未执行计划、重复出现的改造后状态、连续运动却没有有效进展，以及整趟截止时间各有独立终止原因。搜索等待不计作运动停滞。`timeout` 表示整趟截止或连续局部计划失效预算耗尽。`unknown` 停止后续动作。

任务回执保留最近 16 个子动作的完整记录，`childrenTruncated` 和 `omittedChildren` 明示历史覆盖。累计完成数、挖掘数、放置数和最终子动作效果仍独立保留，避免长行程回执随子动作数量无限增长。既有堵墙和不安全下挖负例允许以整趟 `timeout` 收场，并继续核验输入释放和零未授权改造。

落脚支撑由 Terraria 原生碰撞确认，允许单格及部分脚底悬空。可保速交接的检查点仍需验证后续制动，终点另外要求实际脚底命中目标、角色停稳且跳跃已释放。平面判定用于水平候选和局部地形改造预览。

## 接口维护

新增或修改动作时，同时核对：

1. Python Schema 与 Server AI 白名单；
2. IPC 协议、能力旗标和 C# 参数验证；
3. 游戏线程执行及 Actor 身份绑定；
4. 操作专属完成证据、输入释放和 unknown 处理；
5. 对应定向测试及真实原生验证边界。

## 实现入口

- `python/server_ai.py`、`python/atomic_operations.py`
- `CompanionBridge/ServerIpc/`
- `CompanionBridge/ServerPlayerRuntime/`
- [原子操作契约](ATOMIC_OPERATIONS.md)
- [导航](NAVIGATION.md)
- [多人权威确认](MULTIPLAYER_ATOMIC_CONFIRMATION.md)

### Dedicated Server 容器 P0

`server_ai_containers_v1` 提供 `open_container({x,y})`、`inspect_container({})` 和
`close_container({containerToken})`。坐标可以是 Containers（21）或 Containers2（467）的任一占地格。
打开前检查完整的 2×2 框架、锁定状态、原生多格触及范围、Chest 记录及占用情况。
观察返回锚点、类型、索引、名称和全部 40 个槽位。每次成功观察签发新 Token，旧 Token 随即失效。
关闭需要最近一次观察的 Token，有效期为 30 秒。离开范围、箱子失效或 Actor 失效时，服务端清理开箱状态。
容器替换按 Chest 对象身份检测，索引和坐标相同也不能沿用旧 Token。

在 tModLoader 2026.09.3.1（引擎 DLL SHA-256
`BF2CA4D53512D064FAE664170A0D21ECC5E2ED51B5E917D871EC78CC449263E9`）中，
`Player.TileInteractionsUse` 归一化箱子锚点，发送 `RequestChestOpen`（31）。
`MessageBuffer.GetData` 通过 `Chest.FindChest` 和 `Chest.UsingChest` 检查占用，向请求者发送
`SyncChestItem`（32）及 `SyncPlayerChest`（33），再写入服务端玩家的 `chest`，
向其他客户端发 `SyncPlayerChestIndex`（80）。关闭时客户端用 33 提交 `chest=-1`。
`Player.OpenChest` 会写本地 UI 全局状态。AI 适配层复用原生占用和多格触及判定，
在游戏线程设置绑定 Player 的箱子上下文，并用 80 同步开关。原生 `SyncOnePlayer` 也包含 80。
带机关箱沿用原生开箱布线行为。动作回执在后续游戏更新核对 Player 上下文。

定向复现使用复制的世界和两个完成入服握手的协议客户端。脚本检查两端收到的 80、
真人客户端的服务器箱子状态、两个箱子类型、锁定、超距、记录缺失、同索引替换、
销毁和 Actor 失效，并用实际帮助契约校验 Python 动作和观察结果。

```powershell
& .local/lua-venv/Scripts/python.exe -B tests/verify_server_ai_container_native.py --world <test.wld> --package <CompanionBridge.tmod>
```

### Dedicated Server 容器 P1

`server_ai_container_transfer_v1` 提供 `deposit_container_item` 和 `withdraw_container_item`。
两个操作都接收 `inventorySlot`（0–49）、`containerSlot`（0–39）、`count`（1–9999）
及两个槽和当前容器的 Token。存取只涉及指定的两个槽。源数量不足或目标没有容量时返回
`failed`。目标只有部分容量时，回执为 `failed`，并以 `evidence.effectStatus=partial`
和 `movedCount`、`remainingCount`、`blockedSlot` 表达已核验的部分效果。
异常、身份变化或核验失败可返回 `unknown`。再次变更前重新观察两侧。

存取与 ActorInventory 共用 tModLoader 的 `ItemLoader.TransferWithLimit` 和
`ItemLoader.StackItems`，并在堆叠前执行 `ItemLoader.CanStack`。服务端先核验数量守恒和
槽位状态，再用原生 `SyncEquipment`（5）及 `SyncChestItem`（32）同步，并在后续游戏更新复核。
Token 只覆盖原生基础字段及实例身份，ModItem 的自定义状态由原生钩子处理。

Actor 退役在释放保留槽前清除开箱状态，原生重生在增加 Generation 前执行同样的清理。
同一对象出现新绑定时，旧箱子状态会关闭。身份无法确认时旧 Token 立即失效，保留上下文供
合法退役路径处理。世界卸载调用 Actor 退役，运行时重置清除残留会话引用。

使用上面的双客户端脚本，加上 `--transfers` 运行 P1。

### Dedicated Server 容器 P2

`server_ai_container_batch_v1` 提供 `quick_stack_nearby`、`quick_stack_container`、`loot_all`、
`deposit_all` 和 `restock`。当前箱子的四项操作需要新鲜 `containerToken`。附近快堆使用
原生的箱子中心距离（小于 600 像素）、未锁定、无人占用及已有同类物品的规则，并限定
Containers / Containers2。快堆跳过收藏、快捷栏和钱币。批量存入保护快捷栏及收藏，
只扫描普通背包 10–49；钱币按原面额堆叠。取出适用原生的拾取延时、唯一物品及弹药槽过滤。
补充优先填已有同类堆叠，符合条件的弹药也可填空槽。

tModLoader 2026.09.3.1 的四个 `ChestUI` 入口使用 `Main.myPlayer`。
`Player.QuickStackAllChests` 的服务端分支调用 `Chest.PutItemInNearbyChest`，直接遍历并写入
世界箱子，还会处理虚空袋。服务器适配层复用 `ItemLoader.CanStack`、`StackItems` 和
`TransferWithLimit`，按绑定 Actor 和原版箱子范围执行。专用钱币栏和虚空袋不参与存入。

单次至多 128 次槽位搬运。服务端核对槽位前后状态、原生转移数量及绑定身份，
发送 `SyncEquipment` 和 `SyncChestItem`，再于后续游戏更新确认。变更槽的背包 Token
及相关容器 Token 失效。回执 `moves` 列出箱子和槽位的实际转移量，`blocked` 列出剩余
源槽和原因。已核验的部分效果使用 `failed` 和 `effectStatus=partial`。没有符合条件的
物品时使用 `completed` 和 `effectStatus=none`。原生异常或未确认状态返回 `unknown`。

使用上面的双客户端脚本，加上 `--batches` 运行 P2。

### Dedicated Server 背包和箱子排序 P3

`server_ai_sorting_v1` 提供 `sort_inventory({})` 和 `sort_container({containerToken})`。
当前箱子需要新鲜 Token。排序后重新观察槽位，旧快照中的槽位 Token 已失效。

tModLoader 2026.09.3.1 中，`ItemSorting.SortInventory` 先执行原生钱币和弹药整理，
再排序普通槽位。底层 `ItemSorting.Sort(Item[], int[])` 根据玩家伤害加成设置物品分类优先级，
使用 `ItemLoader.TryStackItems` 合并堆叠，并调用 UI 高亮。原生 `SortChest` 从
`Main.myPlayer` 选取箱子后调用同一个底层方法，箱子槽位同步只在客户端分支执行。

服务端按已核验 DLL 的 IL 调用点注入绑定 Player，并在 AI 排序期间略过 UI 高亮。
背包调用原生排序入口，箱子将已核验的原版箱子物品数组传给底层排序。服务端同步变更槽，
并在下一次游戏更新复核实例、基础字段和数量。钱币按面额总值核验；可能触发原生溢出掉落的输入会拒绝。
功能只在已核验的 2026.09.3.1 引擎配置下公布。

使用双客户端验证脚本的 `--sorting` 模式复现。

### Dedicated Server 主动收集掉落物 P4-A

通过 `submit_task` 提交 `collect_drops`，使用 `query_task`、`await_task` 和
`cancel_task` 查看、等待和取消。默认半径 320 像素，可设为 16–960；可传
`typeIds` 或 `scan_entities` 掉落物的 `details.entityRef`。任务最多处理启动时
最近的 16 个目标。引用绑定世界、Actor 代次及掉落实例；移位会重新规划，
同一存活实例合并后读取其最新数量。

当前 tModLoader 的 `Player.Update` 调用 `GrabItems`。收集任务按
`GetItemGrabRange`、玩家碰撞框和安全落脚面计算候选区域，复用落脚区域导航。
站位搜索以完整的原生拾取矩形为边界，再裁剪到世界边界。脚底高度按 8 像素对齐，
横向逐像素检查并合并连续的安全落脚点。每次接近最多探测 6144 个站位。
优先选择能与掉落物直接接触的站位，其次考虑原生吸附范围内的站位。
用完预算且未找到候选时，剩余目标的原因是 `pickup_stance_search_budget_exhausted`。
原生拾取仍检查归属、延时、吸附、库存空间和 Mod 钩子。
任务仅在原生 `PickupItem` 返回的地面数量变化与绑定 AI 的背包或虚空袋增量
一致时计数。服务器沿用原有掉落物和玩家同步路径。

`entityRef` 包含 Actor 绑定、掉落槽位和当前 `Item` 实例标识。Clone 使用不同的
对象键，`SetDefaults` 和 `OnSpawn` 会使原地复用的对象获得新标识。槽位更换对象
或同一对象转移到其他槽位后，旧引用无法选中该掉落物。

回执包含 `collected`、`remaining`、`unavailable` 和 `outcome`。
未确认的消失只记作不可用。部分成功保留已核验数量；取消会释放导航输入。
任务使用现有安全导航能力，可尝试其他落脚区域；候选站位和每次导航均有界。
若本批 16 个目标全部收集且初始扫描还有候补，回执为 `completed / drop_batch_collected`，
总体 `outcome=partial` 且 `canContinue=true`。可以重新扫描，或用仍有效的 `nextTarget.entityRef` 开始下一批。
本批未拾取的存活目标仍列在 `remaining`，失效目标列在 `unavailable`。
可靠性回归在 tModLoader 2026.09.3.1 的双协议客户端场景中验证了 16+1 分批拾取、
Picksaw 原生镐击（镐力 210、斧力 25）和危险判定。墙后与未锁定玩家的 NPC 未中断，
可见且锁定玩家的近距离 NPC、身体接触和扣血均触发保护。
掉落物移动超过 24 像素或失效时，任务取消旧导航并等待原生物理更新。
只有确认输入已释放、脚底支撑安全、角色停稳且跳跃已释放后，才会按最新掉落物状态重新规划。
最多等待 120 个游戏更新，未确认落地时返回 `unknown`。
在现有双客户端脚本中使用 `--collect` 复现。

P4-A-R 双客户端验证使用 tModLoader 2026.09.3.1。基线 `15872bf` 在第 1302 帧取消旧导航时，
玩家左上角为 (33692.184, 3422.9094)，速度为 (0.72, -3.41) px/tick。
第 1305 帧启动新导航时玩家仍在上升，纵向速度为 -2.21。第 1306 帧的新导航因缺少落地支撑拒绝规划，
最终回执为 `unknown / native_navigation_landing_unconfirmed`。
修复后，测试在第 1201 帧记录到玩家仍在上升（纵向速度 -4.61）且输入已释放，
第 1226 帧记录到安全支撑、双轴速度归零和输入释放，第 1229 帧启动新导航并随后收集 7 个木材。
同一轮验证还通过了台阶混合收集及双客户端同步、类型过滤、指定目标、原地重初始化前后引用、
目标消失、掉落合并、满背包、部分容量、导航超时和显式取消。
### Dedicated Server 通用物品放置 P4-B

`server_ai_place_item_v1` 开放 `submit_task({operation="place_item", arguments={x=2100,y=180}})`。
坐标是绝对 Tile 坐标。任务使用当前选中的快捷栏物品，沿用原生物品使用周期和放置钩子。
可以通过 `query_task`、`await_task` 和 `cancel_task` 管理该任务。

原生放置分支按物品属性选择。多格物体使用实际选中的 `TileObjectData` 变体，墙壁限定为一个空背景格，
绳索可向下延伸，原版箱子由原生放置后钩子创建存储记录。任务核验箱子的占地和实际 Chest 记录，
并广播放置数据。标准 Mod 放置属性沿用相同机制，受服务器现有引擎与 Mod 隔离约束。
使用光标、弹射物或材料魔杖的放置行为超出此接口范围。

终态证据位于 `task.result.evidence`，包含 `kind`、请求坐标、实际锚点、占地、样式、
适用时的变体与方向、物品消耗和箱子核验。已发生的世界修改或消耗无法核验时返回 `unknown`。
`observe_scene` 的 `placementMaterial.placeItem` 提供当前物品的放置类型和默认占地、原点，
实际占地和方向以原生放置回执为准。
