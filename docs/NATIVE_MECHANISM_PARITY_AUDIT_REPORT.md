# Terraria 原生机制一致性审计

2026-11-20。实现基线为 `0920b6c`，任务说明为 `6abef6a`。本次修改普通钱币支付及直接关联的契约、夹具和说明。下文的方法偏移来自本机目标程序集，由 Mono.Cecil 读取。Terraria 为 1.4.4.9，tModLoader Stable 为 2026.09.3.1，`tModLoader.dll` SHA-256 为 `BF2CA4D53512D064FAE664170A0D21ECC5E2ED51B5E917D871EC78CC449263E9`。构建和专服入口均校验 EngineProfiles.json 中的引擎和 hooks 指纹。

“客户端”栏描述正常 UI 的 IL 调用路径。运行证据来自绑定独立 Player 的无客户端隔离专服。未启动图形客户端、Host & Play 或远端观察者。

## 已验证一致（Confirmed parity）

| 环节 | 目标版本依据 | Server AI 与实测范围 |
| --- | --- | --- |
| 普通币购买和服务支付 | `Player.CanAfford` IL_0000–00b7 在货币号为 -1 时统计随身及 bank、bank2、bank3、bank4，再比较合计与价格。初始化数据解码为 58、57、56、55、54 五个排除槽。其他货币走 CustomCurrencyManager。`BuyItem` IL_0003/000c 依次调用 CanAfford、PayCurrency。后者 IL_0043–00e2 建立五个存储和排除槽，IL_0341 调用 TryPurchasing。 | MerchantShop、NurseService、GoblinReforge 在报价和执行时重查原生支付能力，原生处理扣款与找零。MerchantPayment.Balance 只作观察和核验。报价绑定各来源铜值，执行消费一次性令牌。成交读回 beforeFunds、afterFunds 的各来源和总额，比较银行非钱币所在槽、实例和核心属性，并验证服务效果。Python 独立复算两边合计及净差。 |
| 普通商人一件火把 | `NPCShop.FillShop` IL_0037/0068 检查条件并在克隆上运行 OnShopOpen。`ItemSlot.HandleShopSlot` IL_0093/00ca/00da/017e/0217 分别检查 CanBuyItem、取价、付款、OnCreated、PostBuyItem。`GetItemExpectedPrice` 读 discountAvailable 与 currentShoppingSettings.PriceAdjustment。 | 对无开店委托的普通条目，Catalog 从 NPCShopDatabase 解析物品，临时应用绑定角色与商人的购物设置并恢复。Deliver 调用实际付款与创建钩子，用 GetItem 入包。专服核对单件增量与报价净额。游标交付、动态条目和回购的区别见下。 |
| 普通出售 | `ItemSlot.SellOrTrash` IL_007b/0097/00a1/00d6 收款、加入回购、清空源槽、PostSellItem。`Player.SellItem` IL_0052–0090 按调整价整除 5、最低 1 铜，再处理同次访问的退款。各面额从槽 53 向前寻找随身空间，调用 DoCoins；放不下时 IL_0475–049d 从副本恢复槽 0–57 并返回 false。 | MerchantSale.Sell 对单件调用 SellItem，核对源堆叠减一、同类型总数减一、随身铜值增加、银行逐槽不变。专服检查实际收入和移动源槽后的拒绝。CanReceive 是执行前的空间估计，完整兑零边界另列。 |
| 护士的常规治疗 | `Main.GUIChatDrawInner` IL_06fa–0837 取缺失生命，可移除且剩余超过 60 tick 的减益每项加 100，进度倍率依次为 200/150/100/60/25/10/3/1，专家再乘 2，乘幸福度后转整数。IL_1247/1269 执行 ModifyNurseHeal、ModifyNursePrice；IL_20a5–20cd 要求正价且允许治疗，BuyItem 成功后加生命、DelBuff，最后 IL_2274 PostNurseHeal。清除阈值为剩余大于零。 | NurseService.Inspect/Heal 在支持的原版配置上按该顺序计算并执行。本次专服仅第一银行有钱，付 132 铜，生命 60 → 100，移除流血 30，保留药水病 21；满血仅有药水病时无付费报价。进度、折扣、短时减益各分支只核对 IL。 |
| 哥布林常规重铸 | `ItemSlot.PickItemMovementAction` IL_014b–0152 用 Prefix(-3) 检查资格。`Main.DrawInventory` IL_198d–19ef 按当前价值乘数量，调用 ReforgePrice，通过时打折乘 0.8 并截断，乘幸福度再截断，最后整数除 3。IL_1db8–1e98 检查资金、CanReforge，随后 BuyItem、PreReforge、ResetPrefix、Prefix(-2)、PostReforge。 | GoblinReforge 使用同一原生随机调用及折算顺序。专服第一银行支付 31 铜，木剑 24 的前缀 0 → 10，源槽新令牌生效；保留同一实例、类型、数量和收藏状态。定向 Python 测试接受付费后抽回相同前缀。随机抽回原值和折扣组合未在本次专服采样。 |
| 建造和住宅的覆盖样本 | `TileObject.CanPlace` 按摆放点、style、direction 和 TileObjectData 选择对象；`WorldGen.SpawnTownNPC` IL_002f/003d/0098 顺序为 StartRoomCheck、RoomNeeds、ScoreRoom(-1,type)。ScoreRoom IL_0001–0022 先判占用，IL_007b 标记邪恶，IL_03df 起记录最佳分与坐标。 | BuildingBlueprint 用物品注册表及 createTile/createWall 分层，把蓝图偏移加到世界 Tile 原点；多格对象以点击点减 Origin 求左上。BuildPlaceTask 调原生放置后读回；BuildPlace 专服覆盖部分完成、取消、续建和重复提交。HousingInspection 在每阶段立即采集静态字段，结构失败不读取旧家具/评分，已占用时邪恶值为空。NpcHome 专服实建房屋，核对墙缺口、缺椅、实心查询点、完整房屋和家具占地。 |
| 普通居民分房 | `WorldGen.moveRoom` IL_0000–001f 客户端发送消息 60；服务端 IL_0020–0074 以 NPC 槽取 type、设 homeless、SpawnTownNPC，最后以类型登记 SetRoom。SpawnTownNPC IL_00dc–00ff 将最佳位置写回 NPC。 | NpcHousing.Assign 传 whoAmI，拒绝同型多个实例，成功要求仍是原引用、homeless=false、NPC 与 TownManager.HasRoom(type) 的坐标均等于候选位置。NpcHome 回归覆盖向导及同槽替换的陈旧引用。远端同步另列。 |

## 确认偏差（Confirmed mismatch）

| 位置 | 对照与影响 | 处理或最小后续工作 |
| --- | --- | --- |
| 基线 MerchantShop、NurseService、GoblinReforge | 随身铜值门槛及 banksUnchanged 成功条件使银行足额者被拒，或把合法扣款误判为未确认。与上述 CanAfford/PayCurrency 的五来源直接冲突。 | **本次修复**。保留出售的随身收入证据。四个银行均能参与购买；三项服务都验证各自效果。 |
| BuildingMaterials.Summary / ReserveOne、BuildPlaceTask.Tick | 代码级复现：目标需一块木料，槽 0–49 无木料，仅槽 54 有一块。Summary 计槽 0–57，给出 available=1、missing=0；ReserveOne 返回 material_not_available_for_native_placement，实际选材只搜前 50 槽。两处对同一份材料的可用性含义不同。 | 后续分别返回持有量及可施工量，按后者给施工缺口；加入特殊槽 fixture。离线直接编译该账本，以 Player/Item 替身注入槽 54 和对照槽 49，实测前者拒绝、后者接受，两者摘要相同。特殊槽注入仅用于诊断。 |
| starter_house Skill / BuildPlaceTask.Finish | 攻略要求读 remainingByReason。实际回执包含 entries、counts、completedByLayer；原因在每个 entry.reason。部分完成时攻略指向缺失字段。 | 后续只改攻略为按 entries 的 status/reason 选择剩余目标；用实际部分完成回执校对示例。 |
| BuildDiff.Inspect 对对象的预判 | 原生 TileObject.CanPlace IL_0574 调用按物件定义的 LiquidPlace，IL_05c0/05ee 区分可切割和可替换物。BuildDiff 在调用前直接拒绝任何液体和已占用足迹。部分原生合法位置会显示阻塞。 | 如需覆盖，先以允许液下放置的具体物品和可切割植物做客户端/专服成对验证，再缩小前置拒绝。此次只记录。 |

## 有意限制（Intentional limitation）

| 位置 | 客户端与适配边界 | 处理建议与证据 |
| --- | --- | --- |
| MerchantShop.Inspect / ResolveService、MerchantPayment.Deliver | 当前仅普通 Merchant、单件、普通钱币，限距 80 像素，拒绝动态 OnShopOpen、buyOnce、未知谓词和受检查的外部钩子；仅解释日间及绑定玩家雪地条件。UI 可评估完整店铺、用 mouseItem 接货并通过 sellbackHelper.Add 记录退款。服务端要求可直接入包，拒绝虚空袋交付，PostBuyItem 看到的是单条 offer 数组。 | 保留工具的条件/不支持原因。扩展前需逐项解决 LocalPlayer、整店回调上下文和访问期。现有火把用例仅覆盖无条件静态商品。 |
| MerchantSale.SalePrice / Sell | UI 在一次店铺访问里保留出售物品供回购；当前单次访问使用独立 Chest，只卖一件，拒绝已有 sellback credit 和未审计的出售 hook。 | 保留独立报价和正常卖价。若扩展回购须建立同次访问身份与测试。 |
| NurseService.Supported / GoblinReforge.Inspect | 只在锁定版本且仅加载原版服务配置时开放。重铸只接受主背包的单个不可堆叠物品。资格/CanReforge 会在报价时提前检查。客户端重铸还设置物品的特效位置，护士调用成就、音效；服务端验证玩法效果。 | 继续以能力握手限定适配。未经扩展验证的 Mod 回调、视听和成就效果不作等价承诺。 |
| BuildingBlueprint.Parse / BuildPlaceTask | 蓝图限制范围、体积和层内重叠；施工按前景、墙、对象及自下而上顺序，暂缓缺支撑条目。仅一次有限水平接近（64 格内），用可选背包槽，取消等待释放；子动作结果不明立即结束。客户端交互无此批次、导航和排序规则。 | 保留策略与原生单次放置的区别。专服验证支持样本的部分完成与续建。 |
| HousingInspection / NpcHousing.OrdinaryResident | 普通查询用 Guide，分配用白名单居民实际类型。原生有 Town Pet 共享检查（SpawnTownNPC IL_0069–0095）、Truffle 生物群系、ModNPC 特殊条件。当前拒绝这些分配。 | 保留白名单；新增居民须分别验证登记、特殊条件及共住房间。 |

## 潜在问题（Potential issue）与未经验证（Unverified）

| 分类及位置 | 风险、依据及已有证据 | 最小后续验证 |
| --- | --- | --- |
| 潜在问题 · BuildDiff.Inspect、ActorObjectPlacement.Preflight | 预检使用主定义的 Origin、占地比较候选左上；执行可枚举 alternate。TileObject.CanPlace IL_0087 起枚举替代定义和各方向 anchor。方向导致 Origin/尺寸变化时可能漏报候选。 | 选一个两方向占地或 Origin 不同的实际物品，分别记录 candidate、最终 alternate 和全部帧。 |
| 潜在问题 · 普通 Tile/Wall | BuildDiff 用同类且未致动判前景已匹配，用墙或四邻接判支撑；不会表达所需半砖或斜坡形状。WorldGen.PlaceTile IL_0492/04aa 等处有半砖、斜坡及物品种类分支。当前蓝图只指定材质，无证据可把普通邻接启发式推广到所有种类。 | 逐项测边邻、致动块、半砖、斜坡、液体、墙替换及玩家碰撞，分别比较 diff 与原生执行。 |
| 潜在问题 · MerchantSale.CanReceive | 预估按各面额的 maxStack 和空槽计算，原生 SellItem 涉及 DoCoins、拆分和失败恢复。普通有空间/满库存用例不能证明临界找零的所有分支。 | 构造 99 银等进位边界、仅剩一空槽和跨面额，再对照实际 SellItem。 |
| 潜在问题 · HousingInspection.Flag | 运行时有 DLL 指纹门槛，私有字段缺失会抛出。握手在有查询适配器时即公布住宅能力，缺少逐字段类型/布局探测。目标版本的此次回归实际读取了字段。 | 新版移植时在公布能力前检验字段名及类型。邪恶住宅、特殊气味和每一结构失败枚举分别加样本。 |
| 未验证 · 网络、客户端与扩展 | 客户端 UI 的价格、回调顺序依据目标 IL。没有图形 Host & Play 同场双角色对照；分房发包、远端客户端的家旗和同步延迟也未观测。特殊货币、Mod 回调和所有幸福度组合均未跑实机。 | 按服务记录同世界/装备/价格系数的双端观察，再决定是否扩大支持。 |

## 定向验证与复现

使用 `.local/lua-venv/Scripts/python.exe -B -m unittest discover -s tests -p <文件名> -v`。test_merchant_shop.py 的 4 项、test_goblin_reforge.py 的 2 项通过，包括伪造总额、银行非钱币变更、业务效果不符和相同前缀。未运行全量测试。

运行 `./scripts/build-companion.ps1 -OutputPath .local/parity-fix`，目标版本编译 0 错误、0 警告。各专服场景单独运行 `./tests/verify_server_ai_native.ps1 -PackagePath <本次构建的包> -PythonExecutable .local/lua-venv/Scripts/python.exe -KeepProfile <场景开关>`。所有运行使用复制的世界、独立角色和本机 IPC，未部署到现用 profile。

| 场景 | 验证内容 |
| --- | --- |
| -MerchantLua | 五个单来源、随身与一个银行、五来源合计恰等于价格。每笔分别对照真实库存快照、报价及收到的一支火把；拒绝合计不足、银行间移款后的旧报价及到期/已用令牌。另测实际扣费后中断和出售空间。 |
| -NurseLua / -ReforgeLua | 各自验证全来源不足、仅银行有钱时成交、原生效果、报价/实体/物品身份与重复提交。四家银行内放置非钱币哨兵物品。 |
| -G3Shop | 独立游戏线程探针读取注册商店的条件与火把价格，通过 IPC 读回场景、银行付款后的入包结果及同槽换 NPC 拒绝。 |
| -BuildDiff / -BuildPlace / -NpcHome | 分别通过蓝图查询、部分建造及取消续建、原生建房验房和居民登记回归。NpcHome 同时执行 Housing 场景。 |

本地证据保存在对应隔离 profile 的 merchant-lua.jsonl、nurse-results.jsonl、reforge-results.jsonl、g3-shop-result.json 以及建造/分房 trace 中。全量测试、其他引擎版本及 Host & Play 均未执行。

### 此次专服读回

商人矩阵的固定报价为 44 铜。按随身、猪猪存钱罐、保险箱、防御者熔炉、虚空保险库的顺序，混合资金样本分别为 (1,43,0,0,0) 和 (1,1,1,1,40)，两笔成交后五处均为零。另一次使用银行内的一枚铂金币，银行价值从 1,000,000 降至 999,956，随身仍为零。出售一支火把实收 11 铜；随身槽填满时返回 insufficient_coin_space。

故障夹具在原生 PayCurrency 成功返回后抛出异常。保险箱从 44 降至 0，火把数量不变，回执为 unknown。随后向同一 Lua runtime 提交原报价，返回 blocked，调用次数为 0，独立快照无第二次变化。故障注入只在显式标记的复制世界中启用。本次未对护士及重铸单独注入故障。

可按以下 profile 后缀定位本机 .local/companion-g81-native-* 证据。商人 Lua 为 95e6620793294ec09da68ade7df0cd5f，独立商人探针为 318e07162f824f84aabb985081bf7b92，护士为 9660f581c40042cca4f90e11801bde6b，重铸为 19385659ba5742a2b39c1976d7b45e3e。BuildDiff、BuildPlace、NpcHome 分别为 f761f598f16246ebb5cb624913f740d1、0835950bc9ac48f098cba1a6d1412023、e62f7023858f4739976cfbe0a57cdb92。首次商人夹具碰到 Windows 短暂文件共享冲突；最终夹具在读取命令阶段等待下一帧。独立商人探针首次因安装日志轮换未能启动，顺序复测通过。
