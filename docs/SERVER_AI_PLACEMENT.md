# 独立 Server AI 原生基础方块放置（0.1.58）

## 支持范围与模型契约

`place_tile({x=...,y=...})` 使用**当前选中的快捷栏材料**，对实际观察到的空 Tile 发起一次原生放置。x/y 为 Tile 格，不是世界像素；每格 16px，Y 向下。需要 `server_ai_actions_v1` 与 `server_ai_place_tile_v1` 同时握手，旧后端不会因通用 Schema 已存在而开放该操作。没有扩大 `use_item` 的白名单。

首版支持原版 **DirtBlock、StoneBlock、Wood**，对应普通单格泥土、石块和木块，placeStyle=0、消耗品、无墙/工具/弹幕/channel/wand/ModItem 或自定义 TileObjectData。家具、平台、沙等下落块、火把、墙、种子、特殊材料及 ModItem 均不在本轮范围。只支持空格，不执行方块替换、移除障碍、自动走近、换材料或失败重试。

Agent 应先观察地形并 inspect_tile 确认目标为空、inspect_inventory 读取实际材料和 hotbarKey，必要时显式选栏，再生成 Lua 放置并重读世界和背包。方块位置、材料、是否继续及整体结构由 Agent 决定；本次没有新增建房或其他固定建造脚本。预检只排除显然不支持的情况，邻接、范围、保护、可放置和物品消费继续由 Terraria 判定。

## 实现选择

普通玩家的放置沿用 `Player.PlaceThing_Tiles`，其中范围检查和多个内部 helper 依赖共享 `Player.tileTargetX/Y`。既有服务器 G6 会跳过这段光标驱动区域。因此不能直接开放共享光标，也不能从执行器调用 WorldGen.PlaceTile、设置 HasTile 或手扣库存来模拟原生输入。

本次将放置上下文放入现有 `ActorItemUse.Context`，复用其 Capture/Binding、互斥、单次 controlUseItem、ItemCheck、计时、取消、死亡/卸载和原生异常隔离。新 `ActorPlacement.Dispatch` 只在该 AI 的 ItemCheck owner 工具阶段调用原 `PlaceThing_Tiles` 一次，随后继续原生物品周期，不重复按下或自动补放。放置不执行工具近战、割邻格或其他光标工具分支。

为固定目标，`PlacementPatches` 对 **17 个已核验原生方法**的坐标读取注入带 Player 参数的 getter：只有当前游戏线程、绑定 Actor/世界/代次/槽位、相同所选 Item 对象和活动物品执行作用域才读取私有目标；其他 Player 或非该作用域仍读取原光标。**不修改共享光标、Main.myPlayer 或真人 Player**。这是原方法的窄读取适配，未删原生校验/控制流；TileLoader.CanPlace、WorldGen 原生放置、ApplyItemTime、TileLoader.PlaceInWorld 和 ItemLoader.ConsumeItem 仍在原链内。

引擎指纹门禁保持原样；还核验方法签名（包括明确的 DMD Player 参数提升形式）、坐标读取数量、关键调用和原生异常处理结构，未知结构/重复补丁拒绝安装。绳索 helper 会写共享目标，保持原状；新材料白名单和非绳索约束不会进入该分支，不能据此扩展绳索支持。任意第三方 Mod 对原生方法、物品、Tile 或库存的直接改写未获兼容承诺。

## 效果、消费与终态

接受回执保存不可变 `targetTile={x,y}`，轮询/取消及 Python 身份核验包含该目标。原生 PlaceTile 返回后，只有本次放置作用域、正确 AI 槽位、原空目标、正确类型/style、非 forced 的真实成功才记 `placedByActor`；观察目标的外部放置/移除与同类型拆放，不能接管为成功。

材料消费计数来自同次真实 ItemCheck 前后的该 Item stack 变化，不用整段初末库存差代替原生消费（被动拾取可发生在其他原生阶段）。成功须：

- 目标由该原生放置作用域放成，最终仍为预期类型且无已观察目标替换；
- `consumedCount=1`，所选 Item 对象及剩余材料 metadata 仍符合原意图；
- 原生动画、itemTime、reuseDelay 自然结束，游戏线程确认输入释放；
- Python 再核对固定 x/y、放置/目标证据、消费计数和绑定结束证据。

成功 reason 为 `native_tile_placed_and_consumed`。原生规则未放成返回 failed（含 `native_placement_refused`）；动画或一次调用不证明完成。原生放成但消费 hook 没扣材料时保留实际方块，返回 `unknown/native_placement_consumption_unconfirmed`；不伪造消费、不回滚、不重试。取消或超时也可能已有方块和材料消耗，回执保留这些事实，不能描述为“什么都没发生”。

最后一件材料会在原生周期末替换为空 Item。开发中回归发现：若耗尽瞬间提前失去 G6 AI 作用域，原生消费尾段会触碰共享鼠标标志。现在在已确认本次放置、原 Item 耗尽到原生周期结束期间保持 UI 隔离资格；仍保留 Item 引用/选栏检查和最终消费证明。最后一件耗尽、真人背包/身份/光标及 blockMouse 均有原生回归。

沿用 10 秒 IPC 截止及最多 600 次物品更新；非放置 use_item 的 180 次更新限制不变。Lua 内存、指令、API、动作和墙钟预算未改。失败/取消/超时屏障、unknown 隔离、旧身份拒绝和异常 quarantine 均保留。

## 自动化验证

未运行全量测试、未调用 LLM、未启动/停止用户图形游戏或部署现用 profile。

| 层次 | 实际结果 |
| --- | --- |
| 放置原生诊断 | **24 项**通过；木材、石块、泥土每次 15 个物品更新、一次启动/原生入口、消费 1 件；最后一件材料正常耗尽，结束释放 |
| 原生拒绝与部分效果 | 占用/越界/保守 80px、悬空邻接、80px 内但越过原生放置范围、GlobalTile.CanPlace、CanUseItem 拒绝；无伪造世界或消费。消费 hook 拒绝时实际放成但 unknown。取消、超时、外部目标、同类型拆放、所选 Item 对象与消耗后 prefix 变化均正确终止 |
| 角色隔离 | 放置回调内对另一 Player 读取仍为其原光标（11 次）；真人对象/背包、Main.myPlayer、共享 Tile 光标及 blockMouse 保持不变 |
| 原生异常 | 独立复制世界的 CanPlace 异常触发输入释放、reservation 保留和 quarantine；只终止该诊断进程，不记作正常恢复 |
| 完整 Lua/IPC/原生世界帧 | **9 次 API、2 动作**：实际观察候选（一个占用、一个空位）、读取并选择真实材料、原生放置、重读 Tile 和背包确认木块及 Wood 4→3。夹具只注入初始材料/场景；执行阶段不直接改 Tile/库存。原读取、导航、物品、挖掘、取消及进程清理同轮通过 |
| 既有原生挖掘 | **43 项**外层检查及树木断言通过；铜斧仍 13 击正常砍树，持续挖掘时首更新拾取 7 木材，未回退拾取修复 |
| 定向离线 | Python 放置 5、既有后端 28、二维落脚 5、共享原子 12，共 **50 项不同用例**通过。C# IPC45/原子40/读取45/适配57/聊天30；正式包 **60 项 IL**检查通过 |
| 原生 IL 适配 | 新放置 **119 项**（17 方法），以及原物品 19、挖掘 30 项通过；覆盖保留规则、无全局身份/目标写入、未知结构与重复安装拒绝及真实 DMD/ILContext |

Lua/native 诊断候选来自明确隔离夹具；程序自行观察、筛空、按实际背包选材，并未调用预写建造流程。自动化只证明这些接口和原生链支持该编排，**不证明真实 DeepSeek 已能稳定设计建筑**。普通自然地图建造、图形 Host & Play、真人/敌怪同场、多玩家竞争和远程客户端同步仍待验收。

## 复现与证据

```powershell
pwsh -NoProfile -File scripts/build-companion.ps1 -OutputPath .local/server-place-build
pwsh -NoProfile -File tests/run_server_ai_tool_tests.ps1
pwsh -NoProfile -File tests/run_server_ai_ipc_tests.ps1 -PackagePath .local/server-place-build/Mods/CompanionBridge.tmod
pwsh -NoProfile -File tests/verify_server_ai_placement_native.ps1 -PackagePath .local/server-place-build/Mods/CompanionBridge.tmod -Port 27981
pwsh -NoProfile -File tests/verify_server_ai_placement_native.ps1 -PackagePath .local/server-place-build/Mods/CompanionBridge.tmod -Port 27983 -Failure
pwsh -NoProfile -File tests/verify_server_ai_native.ps1 -Atomic -Reads -Actions -Placement -PackagePath .local/server-place-build/Mods/CompanionBridge.tmod -Port 27985 -IpcPort 27986
pwsh -NoProfile -File experiments/virtual-player-engine-hooks/verify-native-mining.ps1 -PackagePath .local/server-place-build/Mods/CompanionBridge.tmod -ModName CompanionBridge -Port 27987
# Python 按文件：test_server_ai_placement.py、test_server_ai_backend.py、test_server_ai_landing.py、test_atomic_operations.py
```

包版本 **0.1.58**，路径 `.local/server-place-build/Mods/CompanionBridge.tmod`，SHA-256 `8b7959199e0d92185f5d9ab3e8da88671a5e20c4ece5718b70ceda2ce1363757`。保留前期原生隔离断言失败、被自然格占用的远距离测试目标等原始结果，不改写为通过；最终包各运行哈希与文件清单见[原始证据](evidence/server-placement/manifest.json)。PR 保持 Draft，不合并 main。

归档含 **94 个原始 payload**：原生回执、实际 Lua、逐次事件、精确引擎 IL 审计和离线日志；ZIP entry 全部逐字节核验。最终放置、异常、Lua/IPC 和旧挖掘四个独立原生运行均使用上述同一包。
