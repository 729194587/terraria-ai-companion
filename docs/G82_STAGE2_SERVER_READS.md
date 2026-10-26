# G8.2 第二阶段：Lua 的服务器 AI 状态与背包读取

基线：PR #21 / `317afd4276b2bd57c0738154ece3f7fcccd2a61f`。独立分支
`codex/server-ai-read-adapters`，正式包 `0.1.43`。不修改 main、PR #20、PR #21，
不合并。第一阶段选栏、原生 G1–G7 执行实现和旧 profile 保留。

## 本阶段接口

| 原子操作 | 参数与结果 |
| --- | --- |
| `get_player_state` | 参数仍为 `{}`。保留 `valid/player/motion` 外形；player 保留 inWorld、position、velocity、life、lifeMax，并提供真实 mana、name、dead、ghost、selectedSlot、selectedHotbarKey 等字段；motion 保留 grounded、supported、playerWidth、playerHeight |
| `inspect_inventory` | 参数 Schema 不变，支持默认 selected、hotbar、slots、item、full 五种模式；保留 `valid/available/worldSessionId/gameUpdate/data` 外形，扩展 AI 身份元数据 |
| `select_hotbar_slot` | 第一阶段实现保留，可在同一 Lua 程序中先读取、选择，再读取核验 |

读结果附带连接 binding、actorGeneration、actorSlot、worldSessionId 和 gameUpdate。
每次查询是一个原生主线程处理点的快照；多次查询之间可能发生游戏变化，不承诺跨请求事务。
selectedSlot / inventory.slot 是内部数组索引，**游戏快捷键为 1–9、0**，由
selectedHotbarKey / hotbarKey 表达。inventory 仍为原生 59 格（0–58），包括钱币、
弹药和原生特殊栏；不读取图形客户端的 Main.mouseItem。

```lua
local state = api('get_player_state', {})
local selected = api('inspect_inventory', {})
local hotbar = api('inspect_inventory', {mode='hotbar'})
local slots = api('inspect_inventory', {mode='slots', slots={0,9,50,58}})
local count = api('inspect_inventory', {mode='item', item='Terraria/CopperPickaxe'})
local full = api('inspect_inventory', {mode='full'})
```

selected/hotbar/slots 沿用紧凑物品字段：slot、hotbarKey、typeId、contentKey、name、
stack、empty、prefix、maxStack、favorited 和 use.pick/axe/hammer/createTile/createWall。
slots 保留调用者给定顺序；0–58、最多 16 格，继续由原参数校验器检查。
item 复用已加载内容注册表的精确 contentKey / 本地名称解析与实际背包计数：未知名称
为 unknown_item，重名为 ambiguous_item；已解析但背包没有该物品才是 ok/count=0。
full 保留完整背包、原生装备/社交槽、染料、杂项装备及活跃 Buff 的原有投影与空槽压缩。
full 的返回结构与紧凑模式不同，沿用现有接口，不强加 status 或补造字段。

## 复用方式与边界

- `AtomicOperations.ReadInventory` 从原私有 Inventory 的主体提取；真人 wrapper 仍传入
  原 `AtomicInventory.Token` 与原 ReadPlayer。原两参数 CompactItem 入口保留。
- 服务器调用同一投影，提供独立的 snapshot/token provider；**不调用旧 Query 的
  Main.LocalPlayer 路径，也不生成依赖真人 Controls/世界/myPlayer 的 token**。
- `ObservationReader.ReadPlayer` 增加 source 参数，默认仍为 local_player；服务器为
  server_ai_player。复用其真实角色/物品/装备/Buff 字段和统一按键编号。
- get_player_state 增加原有简洁 position/velocity 别名；背包/装备/Buff 数组交给
  inspect_inventory。motion 复用现有 TerrainReader/TerrainClassifier：grounded 是
  既有地形接触分类结果，supported 是既有运动配置适用检查，不代表路径安全；玩家
  movement.contactStatus 仍明确无通用原生接触标志。没有伪造地形网格供 get_terrain。
- 新增窄能力 `server_ai_reads_v1`，没有为了两个读接口宣称全部 atomic_extensions 或
  inventory_operations 已实现。旧服务器无此能力时在 RPC 前拒绝新读操作。
- LuaRuntime、Lua Worker、Schema、预算、取消/unknown 机制、Agent Prompt 和默认
  真人 TCP 不变；只扩展显式 ServerAiBridge 后端及必要的分发路由。

以下内容明确不支持：AI 背包修改、可用于修改的 inventoryToken、银行/箱子、图形鼠标
持物、Mod 自定义 Player/Item/Buff 数据与 Mod accessory slots。紧凑物品的
inventoryToken 为 null，结果标记 `inventoryTokenStatus=unsupported_server_ai_inventory_mutation`；
customState 明确 unknown。不会以假 token、默认零值或空对象冒充已读取的未知状态。
移动、挖掘、使用物品、observe_terrain、get_player_status 等仍不在该后端白名单。

参考范围只复核 Numen 的 [ADR 0005](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/docs/adr/control/0005-atomic-commands-composed-by-scripts.md)
关于单一意图与脚本组合的职责约束；实际接口以本仓库已存在的原子操作为准，没有移植
Minecraft 的工具或另建 Lua 编排器。

## IPC 与身份/线程约束

沿用 127.0.0.1:18779、G8.1 bind/state 与 G8.2 第一阶段执行协议，增加只读 query：

```json
{"v":1,"id":"read-1","op":"query","binding":{"actor":"...","world":"...","generation":1,"slot":254},"operation":"inspect_inventory","arguments":{"mode":"slots","slots":[0,9,58]}}
```

query 只允许上述两种 operation，不允许夹带 actionId/期限等执行字段；未知操作在
capture 前拒绝。无 lease 或 binding 不匹配仍在 capture 前拒绝；当前对象、世界、
代次、槽位再次匹配后才调用查询。`ServerAiQueries` 在主线程/Gate 下用既有
NetworkStateSync.Require 于投影前后再核对身份，不允许退回真人或旧缓存。
原生受管理死亡角色可以如实读取 dead/life，但不因此允许动作。

成功响应的 data 沿用 available/worldSessionId/gameUpdate/data 包装，附 actorGeneration
和 actorSlot；外层 binding 回传连接身份。Python 再核对元数据，不可用或形状/身份不匹配
即拒绝，不填充 player/inventory 默认值。get_player_state 在 Python 保留 player/motion
顶层结构，inspect_inventory 继续走既有 query_atomic 读路径。
query 不创建 actionId，不发送 start_atomic，不消耗动作预算；同一 Lua 的调用次数、
时间、值大小等预算继续有效。队列过期、断线、卸载仍由原服务器 IPC 清理。

## 实际验证（2026-10-27）

没有运行全量测试，没有启动图形游戏，没有覆盖或删除旧 profile。

| 验证 | 实际结果 |
| --- | --- |
| 正式包 0.1.43 构建 | 0 errors / 0 warnings |
| 原 IPC / 第一阶段执行适配 | 45 / 40 项离线身份替身和真实 loopback 检查通过 |
| 新读协议 | 15 项：租约先验、能力、对象/世界/代次/槽位替换、不可用/死亡角色、禁夹带执行字段等 |
| 共享背包投影 | 10 项存储替身：非空工具属性、空栏、59 格索引、按键映射、顺序、无真人 token、计数/重名/未知、无修改；原 AtomicExtensions 组通过 |
| 正式包 IL | 25 项，包括前后身份检查、复用参数/投影/分类器、无 LocalPlayer/真人 token、无 Player 字段写入 |
| Python 后端 | 最终 18 项通过，含现有真实 Lua Worker、零动作预算读脚本、读—选栏—读、五模式、旧能力拒绝、未知不补值与身份检查 |
| 既有 Python 定向回归 | atomic_operations、inventory_operations、atomic_extensions、atomic_result_contract 共 43 项通过 |
| 真人 C# 路径 | BridgeOnly 与 InventoryOnly 均通过；原 TCP/输入/死亡及真人 token/背包动作的存储替身回归 |
| 真实原生服务器 | 第一阶段按键 0 将索引 0→9；同一组合 Lua 读取后按键 2 将 9→1，再读回；只读 Lua 8 次查询、0 动作，五模式均完成；原始绑定 state 与 query 的位置/生命/选栏匹配；错 binding 拒绝；本次进程正常退出并清理 Actor/预留槽位 |
| 本阶段 Host & Play / 远程多人 | **未执行** |

最终原生证据见 [G82_STAGE2_NATIVE_EVIDENCE.json](G82_STAGE2_NATIVE_EVIDENCE.json)。
原始结果、Lua 回执、stdout/stderr 和按本次进程/世界/起止时间筛选的日志保存在该文件
列出的独立 .local profile，Lua 证据仍由既有模块写入 .local/g82-lua-evidence。
空服原生 gameUpdate=0，证明实际 Player/背包字段和主线程执行链，不能当作真人在线帧
更新或图形显示验收。新临时 AI 背包为空；真实非空工具/前缀/Mod 物品未现场验证，
非空属性和计数目前有存储替身测试，明天需实机核对。

测试曾发现固定验收 Lua 使用了沙箱未开放的 table.insert：非空背包替身暴露该分支，
空背包原生跑不到。已改为普通数组赋值，未放宽沙箱；最终替身与原生脚本均重新通过。

定向重跑：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-companion.ps1
pwsh -NoProfile -File tests/run_server_ai_ipc_tests.ps1 -PackagePath .local/companion-g82-build/Mods/CompanionBridge.tmod
pwsh -NoProfile -File tests/run_csharp_tests.ps1 -ServerReadsOnly
pwsh -NoProfile -File tests/run_csharp_tests.ps1 -BridgeOnly
pwsh -NoProfile -File tests/run_csharp_tests.ps1 -InventoryOnly
$env:PYTHONPATH="$PWD/python;$PWD/tests"
& .local/lua-venv/Scripts/python.exe -m unittest test_server_ai_backend test_atomic_operations test_inventory_operations test_atomic_extensions test_atomic_result_contract -q
pwsh -NoProfile -File tests/verify_server_ai_atomic_native.ps1 -Reads
git diff --check
```

## 明天一起验收第一、第二阶段

正常退出旧游戏/服务器后，在仓库目录构建并创建新的测试 profile：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-companion.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/prepare-companion-host-play.ps1 -SeedProfile .local/companion-g81-host-20261026-233402-281
```

手动运行打印的新 start-host-play.ps1，选择 Host & Play；**等完全进入世界后再查询**。
启动器返回终端不表示服务器已就绪。两个终端均在仓库目录，使用已装 lupa 2.6 的原环境：

```powershell
# 第二阶段：只读，8 次查询，动作数应为 0；输出生命、选栏、59 格总数和非空物品。
& .local/lua-venv/Scripts/python.exe scripts/server_ai_verify.py

# 第一、第二阶段一起：同一 Lua 中先读取，再选游戏键 2，再读取；索引应为 1，动作数 1。
& .local/lua-venv/Scripts/python.exe scripts/server_ai_verify.py --key 2

# 再核对游戏键 0 对应索引 9，不使用或消耗物品。
& .local/lua-venv/Scripts/python.exe scripts/server_ai_verify.py --key 0
```

每次打印完整结果文件路径，便于一起核对；无需粘贴 59 个空栏。若希望核对真实非空物品，
可由真人在 AI 附近丢少量木头并移开，让原生拾取发生后再运行只读脚本，核对非空槽的
名称、数量、hotbarKey；不要把“扔出物品”直接视为 AI 已拾取。
真人移动/切换快捷栏后再读，AI 身份和选栏不应跟真人改变；真人仍应正常移动、聊天、
开背包和使用原 TCP 18777。AI IPC 为 18779。
最后正常退出世界，查询应断开；重开服务器后世界/Actor 身份更新。真实死亡重生期间
查询、持续连接旧 binding 的实机拒绝、远程显示与多人仍需另外验收，未标为通过。
