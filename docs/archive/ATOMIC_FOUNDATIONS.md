# 原子能力基础扩展：CompanionBridge 0.1.38

0.1.40已将历史共享工具额度调整为模型/API/动作三层，新增日志与证据包，见 [当前说明](LUA_OBSERVABILITY.md)。本页保留P0实现与验证记录。

基线为 Lua 第二阶段 `cf28c9790bb4703132fd84e547a24ed73398f19d`，开始时工作区干净。
本批补足状态、内容解析、目标检查、扫描和程序内观察/等待，继续使用现有 Schema、dispatcher、
游戏线程查询、动作管理和日志。没有新增修改世界的动作，也没有部署或启动游戏。

## 接口

| 接口 | 参数与结果 |
| --- | --- |
| `get_player_status()` | 原有 ReadPlayer 的紧凑投影：生命/魔力、名称、防御、呼吸、免疫、位置/速度、运动/液体、坐骑、所选完整物品；非空原版装备/社交/特殊装备和全部活跃 Buff。其他背包物品、染料不返回，背包只给占用/空槽摘要。Mod 自定义状态/饰品槽未知 |
| `get_world_state()` | 保留环境/天气/事件/生物群系和深度；新增客户端已知 hardMode、expertMode、masterMode、前三个 Boss 标志，明确不是完整进度账本 |
| `resolve_content(kind,query,mode?,maxResults?)` | kind=item/tile/wall；默认 exact，search 为忽略大小写的包含搜索。默认16、最多32候选；完整 matchedCount 与 truncated。精确多匹配为 ambiguous，不选择第一项。物品支持内容键和当前语言名称，Tile/Wall 支持内容键/内部符号，暂不支持本地化地图名称。物品候选附 creates.tileType/wallType，可供扫描使用；-1 等原生非放置值不是有效目标 |
| `inspect_tile(x,y)` | 保留原始 cell；新增 content/wallContent 与 preflight：距离、80px范围、正常重力、所选工具及局部前提。未加载/越界时 preflight=null。没有 canExecute/canReach 承诺，不检查路线、安全、镐力、完整家具占位、原生钩子/放置邻接或服务器许可，动作仍重新校验 |
| `scan_tiles(...)` | 原有类型筛选与4096格面积限制；匹配后按玩家中心到格中心的距离、Y、X排序，再截断。附内容键和 distancePixels；未加载计数、完整性和截断语义保留 |
| `scan_entities(category,radius,typeIds?,npcFilter?,maxResults?)` | 原有 all/npc/item/player；typeIds 仅用于 npc/item，1..32个不同ID；npcFilter 仅用于 npc，可取 all/hostile_creature/friendly_npc/critter/unknown。复用既有 EntityCollector 分类。先筛选，再按距离、kind、slot排序，最后截断；默认128、最多128。新增类型、速度和分类依据；NPC保留稳定 entityRef。敌对分类不是当前威胁或可攻击保证 |
| `observe_exploration(radiusTiles?,maxEntities?)` | Python复用完整观察验证与探索投影。默认玩家中心 Tile 半径8（最大8，最多17×17格），实体默认16（最大16），按距离返回；调查候选限裁剪范围内32项。保留采样身份、camera年龄、未知格、危险与覆盖；明确遗漏数。不是全世界、路径规划或自动探索行动 |
| `wait(milliseconds)` | 1..10000毫秒宿主单调时钟等待；asyncio.sleep可取消。Lua写法 `api('wait',{milliseconds=50})`。不持有输入、不推进世界时间、不证明游戏更新或效果 |

新工具均可直接调用，也可用 `api('name',args)` 在 Lua 内调用。
旧 `get_player_state` 继续返回原来的 `{valid,player,motion}`；优先用新 status 获取详细人物状态，
保留旧接口以免破坏运动状态消费者。需要完整背包时继续 `inspect_inventory(mode='full')`。

## 返回与兼容

原生查询继续返回 `{valid,available,worldSessionId,gameUpdate,data}`，字段在 data 下。
有界探索返回 `{valid,exploration,controlGeneration}`；wait 返回
`{valid,status,elapsedMilliseconds,clock='host_monotonic',effectStatus='not_applicable'}`。

握手新增 `atomic_foundations_v1`。新 status/内容解析和带新参数的实体扫描，在旧 Mod 上于 RPC 前明确拒绝。
旧参数的查询继续兼容旧包；旧包不具备新增返回字段和排序保证，应检查能力/返回中的 sort，不能假定旧包也已升级。
0.1.38 与现有 side=Both 的多人确认机制配套使用；本地编译成功不代表已装入客户端/服务器。

有界探索裁剪不会更改共享快照，矩形只覆盖返回的 terrain.bounds；范围外和省略实体均未知。
`projectionCoverage` 和 `completeness` 明确范围限制。单值仍受256KiB、20,000节点、24层限制；
异常大的数据返回 `valid=false,reason=exploration_result_limit`，不会通过丢弃危险/未知信息来伪装完整观察。
直接观察和 Lua 返回值沿用跨轮历史过期机制。

## 动作与资源语义

`READS`、`UTILITIES`、`ACTIONS` 明确区分感知、宿主工具和游戏动作。wait 不覆盖 lastAction，
不制造动作结束证据或 actionSuccess，也不解除先前的失败禁令；观察/等待之后再行动仍走已有动作校验。
脚本超时、消息/任务取消或 Worker 退出时，等待结束，后续代码不执行。
若之前有游戏动作，已有租约、精确动作身份、取消收尾、endEvidence 和未知状态封锁机制继续适用。

每个内部 API（包括等待/观察）仍占调用数预算，等待也占整段 Lua 的墙钟预算。
Agent 默认7个共享工具额度、execute_lua占1的限制保留；本批没有扩大自动行动权限或放松失败重试。
后续持续采集需要另行设计内部调用/动作预算和明确可恢复错误，不能仅去掉上限。

## 固定程序示例

下列只读程序最多6次内部API，符合默认Agent剩余额度；只证明查询链路，不证明采集成功。

```lua
local content = api('resolve_content', {kind='tile', query='Terraria/Dirt'})
if not content.valid or content.data.status ~= 'ok' then return content end
local view = api('observe_exploration', {radiusTiles=4, maxEntities=4})
if not view.valid or view.exploration.terrain == json.null then return view end
local b = view.exploration.terrain.bounds
if b.x < 0 or b.y < 0 then return {reason='bounds_outside_scan_domain'} end
local args = {minX=b.x, minY=b.y, maxX=b.x+b.width-1, maxY=b.y+b.height-1,
    tileTypes={content.data.candidates[1].typeId}, maxResults=4}
local scan = api('scan_tiles', args)
if scan.valid and #scan.data.items > 0 then
    local t = scan.data.items[1]
    local target = api('inspect_tile', {x=t.x,y=t.y})
    api('wait', {milliseconds=50})
    return {scan=scan, target=target, after=api('scan_tiles', args)}
end
return scan
```

## 验证边界与下一批

Python测试使用录制观察、合成边界数据及真实Lua子进程；C#测试编译生产参数/查询代码，
用替代存储验证协议队列和投影；TCP集成覆盖真实 Lua→Python dispatcher→C#查询→导航→结束证据。
导航物理和世界仍为合成替代；tModLoader构建只验证真实API兼容性。
未进行真实游戏、真实模型或多人游戏验收，也没有自动部署。

2026-10-23验证：完整Python回归378项通过；`pwsh -NoProfile -File tests/run_csharp_tests.ps1`
通过；真实tModLoader构建0错误、0警告。新增投影覆盖计数调整后另跑相关测试确认。
本地详细日志在 `.local/atomic-foundations-full-python.log`、`.local/atomic-foundations-csharp.log`；
构建产物位于 `.local/atomic-foundations-build`，均不提交仓库。

游戏内还需核对原版/Mod内容与本地化名称、装备/Buff/呼吸状态、扫描距离和未加载区，
以及消息抢占/暂停/切世界下的观察与等待。后续P1才连通持用任意背包物品、斧类砍树、
目标型接近/拾取、火把/门/工作台放置和原生配方合成；本批不宣称已经具备这些能力。
