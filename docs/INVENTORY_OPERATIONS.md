# 背包原子能力第一批：CompanionBridge 0.1.39

## 异常诊断的基线可用性：0.1.72（2026-11-01）

`SourceBefore` / `DestinationBefore` 原本在 Token、参数和库存前置检查之后才赋值；之前的异常会把字段默认 null 当作“原槽位为空”，错误报告实例改变。现在分别记录 `SourceBaselineEstablished` / `DestinationBaselineEstablished`，在各引用赋值后置 true。`BasicSlot` 在对应标记为 false 时只返回 `{slot,available=false,reason='baseline_not_established'}`，不输出 `instanceChanged`；已建立的 null 基线或 air Item 基线仍返回 available=true，按引用比较判断是否改变。顶层 after.available 表示诊断可采集，各槽 availability 独立。

新增三项定向诊断：内部缺 sourceToken 使 Step 在基线建立前抛异常、已建立 null/air 基线、仅建立源槽基线。测试使用隔离夹具的内部参数/状态注入，未放宽公网协议校验；断言没有变更物品或取得输入，仍为 unknown。库存执行、回滚和重试行为不变。

**实验结果**：上述 3 项基线诊断、原有 36 项库存场景、7 次真实 Lua/IPC 动作和既有读取/选栏/导航/使用/挖掘/取消/进程清理通过。C# 定向 IPC/原子/读取/适配/聊天检查与 80 项正式包 IL 检查通过；构建 0 错误、0 警告。包 SHA-256 `a550d0495793752e066cc764e07cc9783ebb2ab9561787bbe2c7c3b695120049`；[命令与精选原始回执](evidence/INVENTORY_BASELINE_DIAGNOSTICS.json)。未运行全量测试、未部署现用 profile，未新增图形或远端客户端验收。

## Server AI Token 与异常证据修复：0.1.71（2026-11-01）

**实现**：修复 `305b9fe` 中只读 Token 调用 `ItemIO.Save` 的问题。`ActorInventory.Token` 及基础异常快照均不调用 `ItemIO`、`TagIO`、`SaveData` 或网络序列化钩子。Token 保留 World、Generation、Actor 对象身份、Actor 槽位、库存槽位、非空 Item 实例、type/stack/prefix/maxStack/favorited；空槽仍按值识别。摘要增加 `core_only_v2` 域标识，旧算法的 Token 不会被接受。共享 Schema、`server_ai_inventory_v1` 握手和客户端本地 `AtomicInventory` 不变。

**能力边界**：全部 ModItem/GlobalItem 自定义状态（包括可保存状态）都不进入 Token；仅自定义状态改变时不能检测过期，不宣称完整物品相等。执行时仍调用原生 CanStack、StackItems、TransferWithLimit 及原有钩子。移除为防 SaveData 副作用而设置的重复序列化检查；`mutationAttempted` 现在在原生许可钩子或变更入口前置 true，旧基础快照拒绝不再标记已尝试变更。

**异常证据**：CanStack、OnStack、SplitStack 或其他原生执行异常仍为 `unknown`。尽力填写 `after={available,kind='basic_fields_only',gameUpdate,selectedSlot,source,destination,customState='unknown'}`；两槽含原始 typeId/stack/prefix/maxStack/favorited、present、instanceId 和相对执行前引用的 instanceChanged。直接读字段，不调用 Item 属性/Mod 钩子，不把异常后的状态解释为成功或失败。绑定已失效时仅返回不可用原因，不读取替换 Actor；诊断自身失败也不覆盖原异常。保留不回滚、不自动重试及 Lua unknown 屏障。

**实验结果**：最终 `0.1.71` 包 SHA-256 `242bba96417152d8981317e1b17c0f96fd3a28e9a99783af0282f8861e514fa8`，正式构建 0 错误、0 警告。服务器库存 Python 6 项、本地库存 Python 7 项通过；C# IPC 45 / 原子 40 / 读取 63 / 原生适配替身 226 / 聊天 30 项及正式包 80 项 IL 检查通过。未运行全量测试。

**隔离原生**：36 个库存场景、7 次真实 Lua/IPC 库存动作及既有读取/选栏/导航/使用/挖掘/取消/进程清理通过。ModItem 与 GlobalItem 各自抛异常、修改状态的四种 SaveData 配置下，selected/hotbar/slots/full/item 及 get_player_state 查询的保存钩子调用均为零，物品状态保持不变。CanStack 修改目标数量/前缀后抛异常、OnStack 替换源实例后抛异常、SplitStack 修改源数量/前缀后抛异常均保留 unknown，并逐字段比对异常快照与真实槽位；实例替换标志也核对通过。移动、拆分、合并、交换、持用、旧绑定/旧基础字段及同值新实例 Token 拒绝通过；另有仅自定义状态改变仍接受 Token 的边界用例。

原始结果、失败日志、命令和逐文件 SHA-256 见 [修复证据](evidence/server-ai-inventory-token-fix/manifest.json)。两次最初的原生尝试未进入测试：新增 GlobalItem 夹具缺少与 SaveData 配对的 LoadData，被 tModLoader 禁用；先前对世界加载慢的猜测不成立。补齐测试夹具后，同一脚本/原超时的最终重建包验收通过，原失败证据保留。没有部署现用 profile 或付费模型请求，未验证图形 Host & Play、远端客户端收包和任意第三方 Mod。

下节 0.1.70 的实现描述和测试是历史记录：本节取代其中“保存状态进入 Token”“SaveData 开始即算变更尝试”等说明，原始证据不改写。

## Server AI 库存：0.1.70（2026-11-01）

以下为独立服务器 AI Player 的实现和验证；后文 0.1.39/0.1.40 为客户端历史记录，不将单人限制套用于服务器路径。

**实现**：新增 `ServerPlayerRuntime/ActorInventory.cs`，复用已有 Schema、`AtomicOperations.ReadInventory` 投影与 `AiAtomicActions` 生命周期。与 `AtomicInventory` 采用相同原生 `ItemLoader.TransferWithLimit` / `CanStack` / `StackItems` 和 `Utils.Swap`，没有改动本地 `AtomicInventory`。没有复用本地控制器、`Main.LocalPlayer`、鼠标物品或 `Recipe.FindRecipes`；`ActorCrafting` 每次查询/批次本就重建绑定 Actor 的材料和邻接缓存，无须刷新客户端配方 UI。

- 独立能力 `server_ai_inventory_v1`，同时要求原有动作能力。Python 根据实际握手开放三个操作；旧版本在 RPC 前拒绝。服务器协议继续使用绑定租约、动作互斥、取消、截止时间和原游戏线程结束证据。
- `transfer_inventory_item` 在 0..49 普通库存槽移动、拆分、合并精确数量；不同类型/前缀、容量不足、源数量不足或原生堆叠拒绝均不执行搬运。`swap_inventory_slots` 交换完整实例。`hold_inventory_item` 将完整物品与指定快捷栏交换并选中；游戏键 0 对应内部槽 9，同槽只选中，不自动使用。
- 用 `inspect_inventory(mode='slots', slots={source,destination})`（或 selected/hotbar）读取两槽当前 `inventoryToken`；full/item 不保证提供 token。令牌包含 World、Generation、Actor 对象随机身份、Actor 槽位、库存槽位、非空 Item 实例身份、核心字段及 `ItemIO.Save`/`TagIO` 保存状态。空槽使用值身份，避免正常 air 占位替换导致误拒绝。
- 游戏线程执行时重验绑定与两槽 token。检查整个库存的变更前后状态、选栏、非目标槽不变、数量/收藏/类型/前缀和交换引用；调用服务器原生完整同步，然后等 `GameUpdateCount` 严格增加再验证整个库存和选栏。`completed` 必须有 `inventoryVerified`、前后状态、后续更新及绑定输入释放证据；Python 也核对不可变 `targetInventory` 与这些效果证据。
- `failed` 表示已知前置拒绝；Mod 钩子异常、观察到额外改变、同步异常、变更后取消/断连但尚未确认均为 `unknown`。不回滚、不自动重试；Lua 保留 unknown 会话隔离和第二动作屏障。`mutationAttempted` 从可能调用 `SaveData` 开始置 true，表示已进入任意 Mod 代码，不承诺实际搬运已发生。执行前取消为 cancelled；已确认完成保留完成结果。

**实验结果**：最终正式包 SHA-256 `1fdfa14316b0f08acc3d441e4defd0aa66f1e1f4125344220d2782cd5f84bf8c`，构建 0 错误、0 警告。原始回执、日志、命令、逐文件哈希见 [证据 manifest](evidence/server-ai-inventory/manifest.json)。

| 定向验证 | 实际结果与范围 |
| --- | --- |
| Python | 新库存 6、原 Server AI 后端 36、原本地库存 7，共 49 项通过。真实 loopback/Worker；旧能力拒绝、回执缺字段/错误数量/旧目标、unknown 后不发起第二动作 |
| C# 离线 | IPC 45、原子动作 40、读取协议 63、原生适配替身 226、聊天 30 项通过；新增不可变库存请求、取消/断连保护。本地 `-InventoryOnly` 通过 |
| 正式包 IL | 79 项通过，包括原生库存调用、Mod 保存状态和无真人/UI 路径 |
| 隔离原生 | 33 个真实 Terraria 库存场景通过：移动/拆分/合并、交换/空槽、快捷键 0/1/9/同槽、旧源/目标/World/Generation/Actor槽/Actor对象/Mod保存状态、容量/前缀/忙碌、Mod原生拆分/合并/拒绝、CanStack/OnStack/SplitStack 修改后抛异常、执行前后取消、真实生命周期的断连令牌/Forget、SaveData异常/不稳定状态、下一更新前干扰 |
| 真实 Lua/IPC | 7 次新库存动作及原有读取、选栏、导航、使用、挖掘、取消与进程清理通过；下一更新才完成；真人库存、鼠标物品和 UI 选择状态未改变 |

首次原生尝试在旧读取脚本省略必填 `mode` 时失败，未进入库存变更验收；保留 [原失败回执](evidence/server-ai-inventory/initial-read-failure.json)。相关读取脚本和测试已改为显式 mode，并兼容旧/新库存 token 响应，没有放宽生产 Schema。最终同包完整复验通过。受沙箱 localhost 限制的初始测试尝试在获准的沙箱外重跑；没有运行全量测试。

**剩余限制**：Mod 未保存的私有数据无法进入 token；原生钩子决定自定义兼容性，不保证所有第三方 Mod 行为。验证用诊断 ModItem 仅在显式测试启动参数下注册，库存/场景注入只存在隔离夹具。断连精确时序用生产生命周期的 CancellationToken/Forget 配真实执行器验证，未声称实际 TCP 恰在堆叠与确认之间断开的确定性重演。已调用服务器原生同步，但未验证远端客户端收包、晚加入或图形 Host & Play；未部署用户现用 profile、未调用付费模型。

---


0.1.40已增加原生异常详情并更新预算，见 [可观测性与预算](LUA_OBSERVABILITY.md)。本页保留背包批次设计与验证记录。

基线 `4b6f491d219b94c6b2f4567974fb7386fe9723b4`（P0基础能力），开始时工作区干净。
本批先完成后续采集、制作、放置共同需要的背包持用与搬运。没有合入旧实验分支，
没有部署或操作真实游戏。目标型接近、砍树/拾取、配方合成和多格家具放置仍是后续批次。

## 调用与参数

所有背包动作走现有 `start_atomic`、独占控制、租约/绝对期限、取消、游戏线程结束证据链路，
也直接进入 Agent/Lua 白名单。握手能力 `inventory_operations_v1`；旧Mod在RPC前拒绝新动作和slots查询。

| 接口 | 语义 |
| --- | --- |
| `inspect_inventory(mode='slots',slots=[...])` | 按请求顺序读取1..16个不同内部索引（0..58），返回每槽typeId、stack、prefix、maxStack、favorited、empty、hotbarKey、工具属性与inventoryToken。空格明确表示；不是完整背包/装备快照 |
| `transfer_inventory_item(sourceSlot,destinationSlot,count,sourceToken,destinationToken)` | 在两个0..49普通背包槽移动精确1..9999件。目标为空时可全量搬运或拆分；目标非空时必须同类型/前缀、原生CanStack允许且有完整容量。不会自动换目标、部分装满、溢出丢弃或交换异类物品 |
| `swap_inventory_slots(sourceSlot,destinationSlot,sourceToken,destinationToken)` | 交换两端完整物品实例，不合并、不重新生成物品；可与空格交换。源/目标必须不同 |
| `hold_inventory_item(sourceSlot,hotbarKey,sourceToken,destinationToken)` | 将源物品与指定快捷栏槽整件交换，再选择该快捷键。目标原物品回源槽，即使同类也不堆叠；源本就在目标槽则只选择。不会自动使用物品 |

`sourceSlot/destinationSlot` 为内部数组0..49；`hotbarKey`为游戏键帽1..9、0（0映射内部9）。
钱币50..53、弹药54..57、鼠标关联58可查询，但本批不能搬运；箱子、银行、垃圾与装备槽也不在范围内。
三个新增动作目前只支持单人；多人返回 `inventory_requires_single_player`，不修改背包。
旧 `select_hotbar_slot` 保留原来的单人/多人选择语义。

显式selected及hotbar紧凑查询也补充上述元数据；0.1.69起mode必填，各模式用inventoryScope/inspectedSlots说明实际检查范围；item数量查询和full保持原有职责，
需要动作token时按需查询slots。`get_player_status`的selectedItem不保证带token。

## Terraria规则与状态保护

没有把普通背包索引直接设成selectedItem，也没有从内容模板生成替代物品。
空槽搬运/拆分调用 `ItemLoader.TransferWithLimit`；堆叠先验证类型、前缀、数量和容量，
再调用 `CanStack` 与 `StackItems`，正常执行GlobalItem/ModItem的拆分、堆叠钩子。
整件交换用与原生槽位实现一致的 `Utils.Swap` 原语，保留对象实例；它不是模拟鼠标点击。
完成后调用 `Recipe.FindRecipes` 刷新原生配方可用性，不执行合成。
参考[tModLoader ItemLoader接口](https://docs.tmodloader.net/docs/stable/class_item_loader.html)与
[官方实现](https://github.com/tModLoader/tModLoader/blob/stable/patches/tModLoader/Terraria/ModLoader/ItemLoader.cs)。

收藏标记遵循原生规则：部分拆到空槽时新堆不收藏、原堆保持；合并时可将收藏标记转移至目标堆。
不会为了表面一致而绕过这些规则。原生堆叠兼容性钩子仍可拒绝操作。

inventoryToken是64位十六进制不透明摘要，绑定世界、Bridge会话、玩家、槽位、物品实例及
已公开的类型/数量/前缀/最大堆叠/收藏标记。空格按值判断，避免原生替换空物品对象导致误报。
两端任一已变化或令牌来自另一世界/会话时拒绝；令牌不能由模型生成，也不应当作持久物品ID。
它不证明任意Mod自定义状态未变化，亦不宣称完整验证了Mod钩子的全部外部副作用。

鼠标持物、物品使用中、鼠标关联持用槽、数量不足、目标不兼容、容量不足和旧快照都先拒绝。
原生钩子与库存变更在 `ControlState.WithAtomicOwnership` 锁内执行，和取消、断线共享同一所有权门：
取消不能先回执“已停止”，随后这个已获准批次才在锁外继续改物品。
若某个Mod钩子阻塞游戏线程，无法从Lua中强行中断；宿主必须走超时/未知收尾，不伪造游戏线程释放证据。

## 完成、失败与未知

1. 开始前校验两端token、数量、容量、控制所有权及世界身份。
2. 调用任何可能产生副作用的原生钩子前，记录`mutationAttempted=true`及两端原状态。
3. 核对期望的类型/数量/前缀/收藏语义及其他背包槽未被同步钩子意外修改，记录立即读回状态。
4. 保持动作running，下一次游戏更新再核对两端token、所选槽、单人模式与所有权。
5. 核对成功才completed；后续真实玩家挂钩提供endEvidence。Python照旧等待释放证据，启动ACK不算成功。

明确的前置失败返回failed；异常发生在钩子/变更之后、读回不一致、刷新配方意外改库存或确认前状态变化，
返回unknown。可能已发生的变化保留在evidence，不自动回滚、重放或补齐。
消息/任务取消在已执行变更后发生时，cancelled同样不表示物品已还原。
Lua遇unknown继续沿用会话封锁/断开；失败后的行动禁令保留，未新增自动恢复例外。

回执只包含有界的槽位元数据、令牌和读回证据，不复制不受控的物品显示文本或Mod自定义数据。
数量/状态“已验证”限于本地背包契约，不证明物品已使用、已制作或世界任务完成。

## 固定Lua与集中验收

[固定持用程序](../examples/lua/hold_inventory_item.lua)按内容键查找物品，重新读取两端槽位，
校验源类型后持用，再读取当前选中物品。默认是铜镐→快捷键0，可修改顶部两项配置。
只在玩家授权该交换时执行；目标原物品会交换回源槽。正常路径4次内部API，加包装共5额度，
仍在现有Agent默认7个共享工具额度内；本批没有修改调用/动作预算。

离线验证包括参数、快照过期、原生钩子存储替身、部分变更/异常、下一更新确认、
取消/断线排序；真实Lua→dispatcher→TCP→生产C#查询/动作/玩家挂钩测试使用合成世界和存储替身，
并执行固定Lua文件（仅将物品内容键换为测试注册表中的名称）。
真实tModLoader构建只验证API兼容性；以上都不等同于游戏内验收。

以后集中游戏测试时依次核对：普通物品全量搬运；部分拆分/合并及收藏/前缀；
背包→十个快捷键的交换与持用；满槽/使用中/鼠标持物拒绝；动作期间新聊天、手动抢占、
暂停/切世界；最后再测试实际使用Mod的物品与堆叠钩子。多人库存同步是单独的后续实现与验收项。

聚焦测试：`pwsh -NoProfile -File tests/run_csharp_tests.ps1 -InventoryOnly`；
Python测试：`python -m unittest discover -s tests -p test_inventory_operations.py -v`；
跨语言测试：`python -m unittest discover -s tests -p test_lua_protocol.py -v`。

2026-10-23验证结果：完整Python回归387项通过，C#离线/协议回归通过，真实tModLoader构建
0错误、0警告。日志及构建产物仅留在 `.local/inventory-primitives-*`，不提交游戏产物。
回归发现的Windows短等待计时问题也一并修复：用高精度单调时钟重检截止时间，
避免事件循环时钟分辨率/早醒导致过早完成或返回0毫秒；包含确定性早醒、取消与超时测试。
