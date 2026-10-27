# G8.5：服务器 AI 世界观察

基于 G8.4 `a6138e9`，独立分支 `codex/g85-server-ai-perception`，包版本 `0.1.48`。
不修改 main 或 PR #24，不合并；本阶段接通观察，不增加自主采集、战斗或寻路策略。

## 实际 API 与数据来源

模型仍只有 `execute_lua` 工具，通过现有 `api(name,args)` 调用。握手新增
`server_ai_perception_v1`，仅新服务器连接开放五项 API；旧包缺少能力时在原分发器拒绝。

| API | 参数与返回 data | 实际复用/来源 |
| --- | --- | --- |
| `observe_terrain {}` | `terrain={originX,originY,tileSize,rows,grounded,supported,playerWidth,playerHeight}`、同次 `state`、bounds、legend、scannedCount、unknownCount、complete | `TerrainReader.Read(绑定AI)`，45×29窗口，中心X格向左22，脚底Y格向上17；`Main.tile`及原碰撞分类器 |
| `scan_tiles` | 既有 minX/minY/maxX/maxY、tileTypes、可选maxResults；返回 items 的 x/y/typeId/contentKey/distancePixels、计数、截断、未知数和闭区间bounds | `AtomicQueries.ScanTiles` → `ObservationReader.ReadCell`，真实服务器 Tile；面积≤4096、类型≤32、最多返回256 |
| `scan_entities` | category=all/npc/item/player，radius为1..1600世界像素；可选maxResults/typeIds/npcFilter；返回中心世界像素、速度、类型、槽位、距离和details | `AtomicQueries.ScanEntities` → 服务器 `Main.npc/item/player` 活跃数组；原分类器；以绑定AI中心筛选排序，排除AI槽位自身 |
| `inspect_tile {x,y}` | 原 TileCell 的类型、Frame、半砖/坡、墙、液体、线、涂色/视觉标志、Mod未知性，以及内容键和局部preflight | `AtomicQueries.InspectTile(绑定AI)` → `ReadCell`；选中物品来自AI，显式注入无token投影 |
| `resolve_content` | kind=item/tile/wall、query，可选mode=exact/search、maxResults；返回候选typeId/contentKey/name、歧义与截断 | `AtomicQueries.Resolve` → 当前已加载 `ContentSamples` 与 Tile/Wall Loader 注册表；不是世界存在性检查 |

读取调用链：原 Agent → LuaRuntime/Worker → `python/atomic_operations.py` →
`ServerAiBridge.query_atomic` → 原 TCP 游戏线程队列 → `ServerAiQueries.Read`。
没有调用 `AtomicOperations.Query` 的真人入口，也不调用观察摄像机更新、
`Main.LocalPlayer`、`Main.myPlayer` 或客户端视口。仅复用 `ObservationReader` 的
显式坐标静态读格方法；专用服直接读取完整已加载世界，无客户端 section 依赖。

所有投影仍在原 simulation thread、reservation Gate 中完成。协议先验证连接lease、
Actor引用、world、generation、slot；适配器在投影前后再次 `Require` 原角色/网络身份。
NPC `entityRef` 包含服务器世界、AI代次、NPC槽位及原 `AtomicNpcIdentity` 实例token，
槽位复用不会成为同一NPC；它目前只用于观察，不开放 `attack_entity`。
掉落物/其他玩家的槽位仅是本次观察槽位，没有持久实例引用承诺。

## 覆盖与时间语义

五项返回同一外层：`{valid,available,data,binding,worldSessionId,actorGeneration,actorSlot,
gameUpdate,observedAtUnixMs,clock}`。`gameUpdate` 是服务器实际更新号，
`observedAtUnixMs` 是本次游戏线程投影完成时的UTC Unix毫秒，
`clock=server_utc_at_projection`；不是Python接收时间或模型请求时间。服务器暂停时更新号
可以不变，UTC墙钟也不是单调时钟。各API是独立采样，不宣称整份Lua程序具有同一快照。
Python检查绑定/代次/槽位/更新号及感知时间字段，缺失或错配即关闭异常连接，不重连或回退真人。

局部图是碰撞类别，`.`也可以是非阻挡装饰或致动方块，`!`同时涵盖液体与危险；它不携带
实际材质ID。`?`包括世界边界和不支持的Mod碰撞；具体材质使用scan/inspect读取。
`supported`是旧运动profile适用性，不是支撑或路径安全证明。

Tile扫描的scannedCount是请求格数，unknownCount是未读取格数。越界格保留未知，
不会静默裁剪；现有 `partial_unloaded` 状态泛指未知读格，包含越界，inspect用
`knowledge=out_of_world/unloaded/read`进一步区分。`complete`表示请求范围读完，
`truncated`独立表示匹配结果超过返回上限；complete=true且truncated=true仍可能有更多目标。
实体complete表示已枚举本次服务器活跃数组并筛选请求半径，不证明视线、可达性、当前威胁、
可拾取性或未生成区域没有实体。所有查询只读，不调用拾取/伤害/交互钩子。
content registry查询时间也保留采样信息，但不把注册表候选当作身边物品或Tile。

## 使用与启动

重建客户端与服务器，使用新的隔离 Host & Play profile，沿用 G8.4 的Agent启动参数：

```powershell
pwsh -NoProfile -File scripts/build-companion.ps1 -OutputPath .local/companion-g85-build
pwsh -NoProfile -File scripts/prepare-companion-host-play.ps1 -PackagePath .local/companion-g85-build/Mods/CompanionBridge.tmod -SeedProfile .local/companion-g82-host-20261027-135050-952
# 手动运行上一步打印的新profile的start-host-play.ps1，进入Host & Play。
& .local/lua-venv/Scripts/python.exe python/agent.py --server-ai --port 18777 --ai-port 18779 --timeout 5
```

游戏聊天可输入 `!ai 观察你周围的地形和附近掉落物，只观察不要行动`。
Lua示例（返回实际感知，不把脚本自述当任务完成证据）：

```lua
local view = api('observe_terrain', {})
assert(view.valid)
local dirt = api('resolve_content', {kind='tile',query='Terraria/Dirt'})
assert(dirt.valid and dirt.data.status == 'ok')
local b = view.data.bounds
local found = api('scan_tiles', {minX=b.minX,minY=b.minY,maxX=b.maxX,maxY=b.maxY,
    tileTypes={dirt.data.candidates[1].typeId},maxResults=8})
local drops = api('scan_entities', {category='item',radius=640,maxResults=8})
return {tiles=found,drops=drops}
```

模型契约按后端能力过滤既有Schema，更新了服务器实体/地形描述与结果层级。
仍保留G8.4显式挖掘授权边界，不因看见目标而自动选邻块、走近、挖掘或重试失败动作。
本阶段不迁入Numen的三维扫描、分片Pending、连通聚类或TerraPlayerBot的资源/战斗策略。

## 参考核对

已读取 Numen `1.21.1` 的 [LookAround.java](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/numen/common/src/main/java/com/dwinovo/numen/tools/perception/LookAround.java)、
[ScanApi.java](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/numen/common/src/main/java/com/dwinovo/numen/tools/perception/ScanApi.java)、
[ScanOps.java](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/numen/common/src/main/java/com/dwinovo/numen/tools/ScanOps.java)：
借鉴角色中心紧凑地图、只读有界发现、先距离排序，以及未知/覆盖/截断随结果交付。
Terraria保留XY侧视截面与原45×29碰撞编码，不复制Minecraft垂直地形折叠或危险膨胀算法。

TerraPlayerBot参考使用本机 `terrabot.zip` 已解压反编译树
`D:/Agent Project/terrabot/TerraPlayerBot/BotPerception.cs`、`BotTerrainCommand.cs`。
核对了显式Player中心Tile循环、原生NPC/掉落物数组和地形格型诊断。
只借鉴数据源及有界角色观察；不复制硬编码资源目标、最近敌人策略、CanPickup调用或修改世界路径。

## 验证记录

未运行全量测试。

```powershell
$env:PYTHONPATH="$pwd/python;$pwd/tests"
& .local/lua-venv/Scripts/python.exe -m unittest test_server_ai_backend test_server_ai_agent test_atomic_foundations test_atomic_extensions -q
& .local/lua-venv/Scripts/python.exe -m unittest test_server_ai_agent.ServerAiAgentTests.test_model_can_observe_through_existing_agent_lua -q
pwsh -NoProfile -File tests/run_server_ai_ipc_tests.ps1 -PackagePath .local/companion-g85-build/Mods/CompanionBridge.tmod
pwsh -NoProfile -File tests/verify_server_ai_native.ps1 -Port 27881 -IpcPort 27882 -PackagePath .local/companion-g85-build/Mods/CompanionBridge.tmod -Atomic -Reads -Actions -Perception
git diff --check
```

- Python四模块62项通过；随后新增完整Agent/HTTP模型替身/Lua/双TCP感知1项通过，共63个不同用例。
  三项新增后端测试检查五API只读路由、覆盖/时间保留、能力门控与缺失时间拒绝。
- 正式包构建0错误/0警告，42项IL检查通过，包括原查询复用与无真人/摄像机回退。
  服务器IPC45项、原子动作40项、读取协议45项、原生适配替身28项、聊天协议30项通过，
  逐API覆盖对象、世界、代次、槽位失效。
- [隔离原生证据](evidence/G85_NATIVE_PERCEPTION.json)：真实进程22152、27881/27882、
  复制世界，AI slot254、Main.myPlayer255；Lua感知10次API、0动作、actionState=idle。
  Tile扫描498个Dirt，仅返回最近1格、truncated=true；inspect材质一致、无inventoryToken；
  越界4格未知、inspect越界；其他玩家结果为空且排除AI；真实Blue Slime及3个Stone Block掉落
  保留类型/坐标/速度/距离/NPC实例。采样更新634→651，UTC时间随真实投影记录。
  同次原选栏/状态/背包、导航/使用/挖掘/取消及输入释放回归通过，退出码0、角色清理、reservation释放。
- 原生无客户端服务器仍使用既有诊断整帧Main.Update机制；新增NPC/掉落只在原有双诊断开关、
  复制世界marker及额外 `-companionperceptiontest` 下创建。正式Host & Play不创建测试实体。
- 验收脚本首次越界样例误用了超过参数上限的100001，被原验证器正确拒绝；改为99998..99999后通过。
  新Agent测试曾在Agent初始化后改变替身能力，导致缓存契约未更新；改为连接时协商后通过。
  包IL测试曾有局部变量重名编译错误，修正后通过。未把这些失败计作成功。

尚未验证：G8.5图形Host & Play、真实外部模型自主生成观察程序、远程真人实体同场、
持续旧连接跨真实死亡/重生/世界重载/角色对象替换、Mod内容及完整坡/液体/线/致动矩阵。
协议替身证明旧绑定拒绝，不能替代上述原生生命周期验收。保持Draft，不合并。
