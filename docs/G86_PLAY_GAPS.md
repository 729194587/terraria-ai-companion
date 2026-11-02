# G8.6：AI 陪玩能力缺口（实机体检）

2026-10-27 在真实 Host & Play 会话（CompanionBridge 0.1.49，AI slot 254，generation 1）上做的一次只读体检。目的不是验收，而是把"要一起探索、挖矿、建造、打 boss"缺什么，按代码位置列清楚。

## 开发循环（2026-10-27 起）

每个真实任务结束后按固定顺序推进，**禁止“看到一个缺口就加一个 API”**：

1. **实机任务（DSH）**：在真实 Host & Play 里执行，保留决策轨迹、每次工具回执、失败原因。
2. **根因分析（人主导，对照 Numen 的成熟设计）**：先判断是“看不到”、“接口不好用”、“反馈不够”，还是“能力真缺”。
3. **最小改进（Codex）**：优先改现有接口/反馈；只有确认现有能力无法表达时，才新增 API。
4. **再次实机验收（DSH）**：用同一任务复测，对比前后证据。

判定一个改动值不值得做，先问三个问题：

- 能不能用现有 API 编排出来？
- 如果不能，是“看不到”还是“做不到”？
- 如果做不到，是原生适配缺失，还是白名单/门控太窄？

证据目录约定（建议，`.local` 被 sandbox 拒绝时放工作区外的 `runs/`）：

```
runs/<yyyymmdd-hhmmss>-<task>/
  task.md        用户原话，不改写
  trace.jsonl    DSH 的决策与工具调用
  lua/           每轮提交的 Lua 源码
  receipts/      每次 submit 的完整 JSON 回执
  verdict.md     结论、失败原因、下一轮假设
```

## 结论

- **能用**：读玩家状态/背包/地形/实体/格子/内容注册表；按水平坐标走到指定位置；选快捷栏。
- **不能用**：世界时间与事件、拾取、给物品、穿装备、合成、说话、攻击、放置、挖墙、锤击、开门/箱子、下平台、背包整理、精确落点、垂直移动。
- 27 个客户端原子 API 里，服务器 AI 只适配了 3 个动作（`navigate_to_x`、`use_item`、`mine_tile`）和 7 个只读查询。

## 实测事实

```
capabilities: atomic_operations_v1, hotbar_keys_v1, server_ai_actions_v1,
              server_ai_atomic_v1, server_ai_chat_v2, server_ai_perception_v1,
              server_ai_reads_v1
supported:    get_player_state, inspect_inventory, inspect_tile, mine_tile,
              navigate_to_x, observe_terrain, resolve_content, scan_entities,
              scan_tiles, select_hotbar_slot, stop_action, use_item, wait
```

`navigate_to_x` 实测：起点 `x=33680`，目标 `33508.168`，实际停在 `33516.254`（差 8.09px），`updates=84`，`MaxError=0`，`reason=horizontal goal crossed on safe settled ground`。它保证"跨过目标"，不保证"停在目标"。

`observe_terrain` 实测：`origin(2073,194)`，`bounds 2073..2117 × 194..222`，45×29，`unknownCount=0`，`grounded=true`。地形永远是"以 AI 为中心、中心向左 22 格、脚底向上 17 格"的固定窗口。

`scan_entities` 实测：`radius=1600`，`matchedCount=8`，`truncated=true`（`maxResults=5`）。能同时看到 70px 的向导和 1600px 内的敌怪（实测出现 `hostile_creature`，type 320，60 血，`damage=15`）。

`inspect_inventory(mode='full')`：59 格背包 + 19 格装备 + 10 格染料 + 5 格杂项全空，`buffs=[]`，`inventoryTokenStatus=unsupported_server_ai_inventory_mutation`，所有 `inventoryToken` 为 `null`。

## 第二轮：DSH 实机反馈（找树并靠近）

任务：在出生点附近找一棵树，判断如何安全接近。执行结果：找到 tile (2098, 203–210) 的一棵树，逐格确认地面连续、无液体、无危险、800px 内无敌对生物，用 `navigate_to_x` 走到 `x=33548.535`（`updates=36`，`MaxError=0`，`CompletedJumps=0`）。

过程本身暴露的不是“能力缺失”，而是信息粒度、操作粒度和反馈质量的问题。

### 信息缺口

| 缺什么 | 实例 | 影响 |
| --- | --- | --- |
| 树不是实体，只是 133 个 tile 格 | 需按 x 列手工聚类，才猜出树干 2098、树根 210 | 不知道树高、是否成熟、能否砍；分不清树干/树枝/树根/树苗 |
| 没有可拾取物聚合 | y=212 的 `Terraria/AbigailsFlower` 要逐格 `inspect_tile` 才发现 | 探索时不会意识到“这里有东西可捡” |
| 地形图没有背景墙 | `inspect_tile` 有 `wallType`，45×29 的 `rows` 里没有 | 判断不了房屋、怪物生成区、洞穴封闭性 |
| 没有生物群系/亮度 | 靠“墓碑 + 阿比盖尔之花”反推墓地 | 不知道昼夜、洞穴明暗、危险群系 |
| 没有“这段路能不能走”的查询 | 手工 `inspect_tile` 45 格，占掉 64 次预算的七成 | 目标再远一点预算就不够 |

### 操作缺口

- 砍不了树：`use_item` 只认木剑/铜镐/苹果，没有斧头
- 捡不了东西：阿比盖尔之花、地上的铜币都没有拾取入口
- 站不准：`navigate_to_x` 目标 33540，实际停在 33548.535，差 **8.5px**
- 不能垂直：目标在坡下/坑对面时水平导航到不了
- 不能标记：放不了火把，也不能“记住这棵树”

### 执行反馈缺口（最影响自我纠错）

1. **`worker_error` 是空消息**。第一次跑 Lua 返回 `{"kind":"worker_error","message":"worker_error","call":null,"line":null}`，没有行号、异常类型、stderr；实际原因是 sandbox 拦了 `multiprocessing`，却花了一整轮才定位。
2. **没有动作中间状态**。`navigate_to_x` 只给结束 evidence，全程 `CompletedActions=1`、`Expansions=1`；中途是否重规划、是否跳过障碍、是否差点卡住都看不到。真出 `jump_failed` 时无法定位帧。
3. **没有真实耗时**。`updates=36` 是游戏 tick，`PlanMilliseconds=5.17` 只是规划耗时；判断不了这次动作花了 3 秒还是 30 秒，而这决定一轮还能做几件事。
4. **没有预算回执**。Lua 程序结束后看不到剩余 calls/actions/秒；`runtimeLua` 只注入给 `agent.py` 的模型层，外部驱动层拿不到。
5. **`reason` 是内部代号**。`native_navigation_arrival_verified`、`horizontal goal crossed on safe settled ground` 没有人话解释，也没有“下一步建议”。
6. **输入释放证据在 `endEvidence` 里**，顶层只有 `actionState=released`。
7. **没有跨程序的动作历史**。actionId 在下次提交时随连接/worker 一起失效，无法回溯；`stop_action` 只对当前程序有效。
8. **`log()` 要等程序结束才返回**，长动作中间静默；程序超时时连日志都拿不到。

### 外部驱动层（DSH 桥）自身的账

- 每次提交冷启动（连接 + bind + worker）约 1–2 秒
- 无会话状态，上次的 actionId 直接丢
- 回执超长时需额外落盘再读
- 桥在系统临时目录，会话结束即失

## 一、感知缺口

### P0 没有世界状态

`get_world_state` 客户端有（[atomic_operations.py:69](../python/atomic_operations.py)），服务器 AI 的查询白名单里没有（[ServerAiQueries.cs:28](../CompanionBridge/ServerIpc/ServerAiQueries.cs)）。

结果是 AI 不知道：白天/黑夜、血月/日食、哥布林入侵/史莱姆雨、天气、生物群系、深度、世界难度。**打 boss 和"天黑了回家"这两件事都做不了**，因为连时间都不知道。

### P0 `!` 把液体和危险揉在一起

地形图例：`! liquid or hazard`（[ServerAiQueries.cs:59](../CompanionBridge/ServerIpc/ServerAiQueries.cs)）。水、岩浆、蜂蜜、微光、尖刺、爆炸物全是同一个字符，AI 无法判断"前面这格能不能踩"。只能对每一格 `inspect_tile` 逐个确认，而一次程序只有 64 次 API 预算。

建议拆成独立符号（例如 `~` 水、`^` 岩浆、`!` 其他危险），或在 `observe_terrain` 返回里附一份"危险格坐标列表"。

### P1 地形窗口固定居中，和 1600px 实体半径不对等

地形只有 45×29 格（720×464px），实体扫描 1600px。探索时经常"看见怪但看不见怪脚下的地形"，必须先 `observe_terrain` 拿 bounds 再 `scan_tiles`，多花两次 API。

建议 `observe_terrain` 接受可选 `originX/originY`（或 `direction` 偏移），或者直接给服务器侧一个 `observe_exploration`（客户端已有）。

### P1 没有光照、可见性、未探索区分

`scan_tiles` 的 `unknownCount` 只覆盖越界/未加载/未知碰撞。夜晚、洞穴、视野外没有区别。探索到洞穴口时，AI 无法知道"里面是黑的、可能有怪"。

### P1 掉落物没有"可拾取性"

`scan_entities` 明确写了 `not proof of pickup eligibility`。AI 知道地上有铜币，但不知道能不能捡、会不会被归属锁定。而拾取本身也没有 API（见下文）。

### P1 队友信息太少

其他玩家只有 `{dead, statLife, team}`（[AtomicQueries.cs:150](../CompanionBridge/Common/Systems/AtomicQueries.cs) 的投影）。没有装备、buff、是否在战斗、血量变化趋势。陪玩时无法判断"队友快死了"。

### P1 没有"当前威胁"

`hostility is inference, not current threat`。`damage`、`chaseable` 是静态属性，不说明这只怪现在是否在追 AI、是否已经仇恨、有没有视线。战斗决策只能靠猜。

### P2 字段冗余与分散

- `position` 与 `positionWorldPixels`、`velocity` 与 `velocityPixelsPerTick` 同时存在。
- `player` 对象里嵌了 `id`、`source`（[ServerAiQueries.cs:36-41](../CompanionBridge/ServerIpc/ServerAiQueries.cs)）。
- `buffs`、`equipment` 只在 `inspect_inventory(full)` 里，`get_player_state` 没有。判断"能不能打"要两次调用。

建议统一成一份字段命名，并把 `buffs`（含剩余时间）放进 `get_player_state` 的摘要。

### P2 没有脚下支撑格坐标

`grounded`/`supported` 是布尔，`rows` 要自己数。放置、精确落地、判断"脚下这块会不会塌"都得自己算，容易错一格。

## 二、动作缺口

### P0 只适配了 3 个动作

客户端 27 个工具里，服务器只实现了 `navigate_to_x`、`use_item`、`mine_tile`（[ServerAiNativeActions.cs:52-53](../CompanionBridge/ServerIpc/ServerAiNativeActions.cs)、[AiAtomicActions.cs:75](../CompanionBridge/ServerIpc/AiAtomicActions.cs)）。

未适配：`attack_entity`、`place_tile`、`place_wall`、`mine_wall`、`hammer_tile`、`interact_tile`、`drop_through_platform`、`navigate_to_landing_region`、`transfer_inventory_item`、`swap_inventory_slots`、`hold_inventory_item`。

对应体验：**打不了怪、建不了房、开不了门和箱子、下不了平台、整不了背包。**

### P0 `use_item` 硬编码三种物品

[ActorItemUse.cs:40](../CompanionBridge/ServerPlayerRuntime/ActorItemUse.cs)：`Supported(type) => type==WoodenSword || type==CopperPickaxe || type==Apple`。

火把、药水、弓、方块、钓竿全部不能用。陪玩时连"点个火把"都做不到。

### P0 没有拾取，也没有给物品

没有 `pickup`，没有 `give_item`。AI 只能走到掉落物上靠原生拾取，且拿不到"拾取成功"的证据（`scan_entities` 只证明物品存在）。新角色开局没镐没剑，这个缺口会直接卡死整条链。

### P0 没有装备穿戴

`inspect_inventory(full)` 能读 19 个装备槽和染料槽，但没有 `equip`/`unequip`。新角色裸装，打 boss 不可能。

### P0 没有合成

没有配方查询，没有工作台/熔炉/铁砧交互。从木头到工作台到镐的整条链断了，"挖矿建造"只能靠玩家预先给成品。

### P0 Lua 里不能说话

`say` 是桥外的 `command('say')`（[server_ai.py:122-147](../python/server_ai.py)），不在 Lua 的 API 列表里。我写的程序无法在游戏聊天里回话。"言出法随"的对话闭环少了一半。

建议加 `say(text)` 作为 Lua 宿主函数（不消耗动作预算，但要受回复节流和 superseded 保护）。

### P1 `navigate_to_x` 的精度与维度

- 只到"跨过目标"，实测差 8px；需要"停在区间内"的语义（可参考客户端的 `navigate_to_landing_region`）。
- 只有水平。挖竖井、上平台、爬绳子全做不了。
- 不能跟随移动目标（队友在走，它追不上）。
- 30 秒上限，一次调用可能吃掉半轮预算。

### P1 背包不可变

`inventoryTokenStatus=unsupported_server_ai_inventory_mutation`，所有 token 为 `null`。`transfer_inventory_item`、`swap_inventory_slots`、`hold_inventory_item` 在服务器侧全部不可用。

不只是"整理"：捡到矿石后无法放进箱子，无法把镐从背包深处换到快捷栏，无法给队友东西。

### P1 动作预算偏小

一轮 8 个变更动作、64 次 API、60 秒（[lua_agent.py:38](../python/lua_agent.py)）。`navigate_to_x` 一次就可能 30 秒，所以"走过去 + 挖 + 走回"要分多轮，每轮都要模型往返。

### P2 没有丢弃、喝药、吃食物、钓鱼、坐骑

`use_item` 泛化后能顺带解决一部分，但"丢东西"需要独立的 `drop_item`。

## 三、扩展点（给 codex 的代码地图）

新增一个服务器 API 需要动五处，少一处就会被静默拒绝：

| 层 | 文件 | 位置 |
| --- | --- | --- |
| C# 能力声明 | `CompanionBridge/ServerIpc/AiAtomicActions.cs` | `basicCapabilities` / `Capabilities`（12-13 行） |
| C# 查询白名单 | `CompanionBridge/ServerIpc/ServerAiQueries.cs` | `Read()` 开头（28 行） |
| C# 动作白名单 | `CompanionBridge/ServerIpc/AiAtomicActions.cs` | `Handle()` 的 `operation is not (...)`（75 行） |
| C# 原生执行 | `CompanionBridge/ServerIpc/ServerAiNativeActions.cs` | `Validate()` / `Start()`（32-53 行） |
| Python 门控 | `python/server_ai.py` | `PERCEPTION_OPERATIONS`、`supported_atomic_operations`（17-35 行）、`query_atomic` 白名单（150 行） |
| Python 契约 | `python/lua_agent.py` | `server_contract()` 的描述覆盖（48-71 行） |

另外 `python/atomic_operations.py` 的 `TOOLS` 是唯一 schema 来源，新 API 应先加在那里，再在各层放行。

## 初始背包与基础资源采集的根因（M0）

这一节专门回答“为什么 AI 出生背包是空的”，并且**不新增 API**。

### 现象

`inspect_inventory(full)`：59 格背包 + 19 格装备 + 染料全部为空，`buffs=[]`。AI 无法挖矿、砍树、战斗，整条链从第一步断开。

### R1（主因）角色创建绕过了原版新角色规则

[ProbeSession.cs:143-145](../CompanionBridge/ServerPlayerRuntime/ProbeSession.cs)：

```csharp
var candidate = CharacterStore.AcquireCandidate() ?? new Player { whoAmI = view.Slot,
    name = "G31_NoClient_Probe", width = 20, height = 42,
    statLife = 100, statLifeMax = 100, statLifeMax2 = 100, ... };
```

`new Player { ... }` 只是对象初始化器，**没有调用 `Player.SetDefaults()`**。所以原版新角色的初始三件套（铜短剑、铜镐、铜斧）、初始属性、ModPlayer 初始化全部缺失。

代码里第 140-142 行的注释已经写明这一点：

> This probe only allocates an engine Player. Future character creation must restore an existing profile first, otherwise use vanilla new-character rules.

也就是说，这不是设计意图，而是已知的未完成项。

**最小改法**（二选一，都不加 API）：

- 在无档案分支里，构造后执行一次原版新角色初始化（`SetDefaults` 及其配套 Hook），再覆盖 `whoAmI`/`position`/`active` 等运行时字段；
- 或者在没有档案时先生成一个“原版新角色”档案，再走现有 `CharacterStore.Load()` 路径。

需要小心的点：`SetDefaults` 会重置生命/位置/名字，并可能触发 `ModPlayer` 与 Hook；代码第 138-139 行已经警告过构造可能抛异常或重入，所以顺序和隔离要按现有 `Reservation`/`Quarantine` 模式处理。

### R2 `use_item` 白名单挡掉斧头

[ActorItemUse.cs:40](../CompanionBridge/ServerPlayerRuntime/ActorItemUse.cs)：

```csharp
internal static bool Supported(int type) => type == ItemID.WoodenSword
    || type == ItemID.CopperPickaxe || type == ItemID.Apple;
```

即使 R1 修好、出生自带铜斧，`use_item` 仍会拒绝。**最小改法**：按选中物品的原生 `useStyle`/`useAnimation` 放行，或先把铜斧加进白名单验证闭环。

### R3 `mine_tile` 只认镐，树需要斧

[ActorMining.cs](../CompanionBridge/ServerPlayerRuntime/ActorMining.cs) 的 `Allowed` 只接受镐。Terraria 的树（tile 5）只吃斧类工具，所以“砍树取木头”需要给 `mine_tile` 增加斧分支，或复用同一套原生 tile 破坏流程。

### R4 合成暂不需要

开局有铜短剑/铜镐/铜斧就足够“砍树→得木头→挖土石”。合成（工作台、木剑、火把）留到 M3，不要在这里先加配方 API。

### 闭环验收脚本（DSH 实机）

R1、R2、R3 修好之后，用同一套 Lua 验收，不需要新 API：

```lua
-- 1. 出生自带三件套
local inv = api('inspect_inventory', {mode = 'full'})
-- 断言出现 Terraria/CopperShortsword、Terraria/CopperPickaxe、Terraria/CopperAxe

-- 2. 斧头砍树（目标来自 scan_tiles 的 Trees 格，实测 tile 2098,210）
local axe = api('select_hotbar_slot', {slot = 3})
local chop = api('use_item', {})           -- 或扩展后的 mine_tile
-- 断言 Trees 格消失、背包出现 Terraria/Wood

-- 3. 镐挖土（实测 tile 2094,211 是 Grass）
local pick = api('select_hotbar_slot', {slot = 2})
local dig = api('mine_tile', {x = 2094, y = 211})
-- 断言 Grass 格消失、背包出现 Terraria/Dirt
```

## 四、建议的最小可用里程碑

**M1 感知补齐（只读，风险低）**
1. `get_world_state` 适配到服务器 AI（时间/事件/天气/群系/深度）。
2. `observe_terrain` 支持窗口偏移或方向参数。
3. 危险格拆分：水/岩浆/其他危险分开，并附危险格列表。

**M0 初始背包闭环（先做，见上一节）**
- 修 R1（角色创建走原版新角色规则）、R2（`use_item` 放开斧头）、R3（`mine_tile` 支持斧/树）。
- 不新增 API，先让 AI 出生自带铜短剑/铜镐/铜斧，能砍树、能挖土。

**M2 生存闭环（能活下去、能对话）**
4. 拾取：先确认原生拾取结果能否从现有 `scan_entities`/`inspect_inventory` 观察到，再决定是否需要 `pickup_item`。
5. `equip_item` / `unequip_item`（同样先看现有 `inspect_inventory(full)` 能否配合原生流程）。
6. `say(text)` 进 Lua（桥外已有完整实现，只差暴露）。
7. 给物品：优先做调试/验收用的最小入口，不等同于常规 API。

**M3 建造与战斗（真正陪玩）**
8. `attack_entity`（复用 `entityRef`，需要实体实例校验）。
9. `place_tile`（客户端已实现，服务器侧按 G8.2 的模式适配）。
10. `interact_tile`（门/宝箱/开关）。

**M4 背包与容器**
11. 服务器侧 inventory token 语义（当前直接标 `unsupported`）。
12. `transfer` / `swap` / `hold`。
13. 箱子存取。

## 五、验收方式

沿用现有模式：每个 API 一个 Lua 只读/动作脚本 + 真实证据断言，参考 [scripts/server_ai_perception.py](../scripts/server_ai_perception.py) 和 [scripts/server_ai_actions.py](../scripts/server_ai_actions.py)。

建议每个新 API 至少断言：

- 返回带 `binding`/`worldSessionId`/`actorGeneration`/`actorSlot`/`gameUpdate`；
- 感知类带 `observedAtUnixMs` 与 `clock=server_utc_at_projection`；
- 动作类带 `completed` + `endEvidence`，失败有明确 `reason`；
- 旧能力缺失时明确 `unsupported_operation`，不静默降级。

## 六、附：本次实测的原始观察

- `observe_terrain` 的 15–19 行（AI 右侧有一段半砖+斜坡+实心块）：

```
15: ..............................h#########1....
16: ...........................#.################
17: .........==...........#######################
18: ###########################.#################
19: ########........#############################
```

- `inspect_tile` 的 `preflight` 已包含 `tilePresent`、`pickSelected`、`consumableTileSelected`、`distancePixels`、`withinConservativeReach`、`reachPixels=80`，但明确标注 `partial_checks_only`。
- 所有响应都带 `customState: unknown; custom Mod player/item/buff data ...`，即 Mod 物品状态不可见。
- 每次查询 `gameUpdate` 递增（本次 22535→22541），时间语义可信。
