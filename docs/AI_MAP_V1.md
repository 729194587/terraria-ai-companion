# AI 独立地图 V1（0.1.64）

M4.5 的实时 L1 区域观察复用 `observe_scene`，与本页 `query_map` 的 last-known 历史分开；没有第二套 map.bin 或存储格式升级，也不以历史格批准导航。当前分层用法与验证见 [M4.5](evidence/m4-5/README.md)。

实现入口：`ServerPlayerRuntime/AiMap.cs`（纯记忆/文件/聚合）、`AiMapRuntime.cs`（游戏线程揭示）、`WorldCharacterPersistence.cs`（角色提交）、`ServerIpc/ServerAiQueries.cs`（绑定查询）。只用于 Server AI。

## 身份与存储

地图身份为 WorldUid + CharacterId。当前正式运行时沿用一个世界一个 AI 角色的既有档案选择，不新增角色选择器。地图不读取或写入人类玩家 .map。

每个不可变角色世代增加独立 `map.bin`：BinaryWriter 字符串签名 CompanionBridge/AiMap、Int32版本1、世界UUID（N字符串）、角色ID字符串、Int32宽高、按y*width+x排列的逐Tile字节。0未知、1已知非阻挡、2实心、3平台、4水、5岩浆、6蜂蜜、7微光。探索状态由0与非0区分；墙暂不记忆（只参与光照近似），斜坡/半砖按实心记忆，非阻挡家具归入1，不是物体地图。

角色指针升级为版本2，增加 MapHash（SHA-256），仍由原机制一次发布 native pair + selectedItem + map fingerprint。地图先写入/Flush(true)，验证指纹后才替换指针。失败保留原世代；损坏、缺文件、身份/尺寸/版本不匹配拒绝恢复，不用空图兜底。版本1角色档案显式记录 ai-map-migration，以无历史记忆开始，下次提交升级；没有把坏版2档案迁移为空图。

## 揭示和时效

已检查本机精确引擎DLL的IL：常用 Lighting.AddLight(int,int,float,float,float) 在 netMode==2 返回；WorldMap.UpdateLighting 依赖调用方提供light字节并更新共享地图。未启动原生客户端绘制/光照系统，不伪造屏幕、LocalPlayer或摄像机。

V1使用服务器近似：角色中心120×68 Tile视口（1920×1080向Tile取整），每15个游戏更新重采样可见部分；目标前的实心非平台Tile阻挡整数射线，目标表面可见。地下近身半径4；可从角色看见的开启原版火把提供半径14、同样受遮挡的照明；白天surface以上且无墙、竖直到世界顶部无遮挡的格视为日光。原版火把沿既有 ItemCheck→PlaceThing_Tiles 原生输入与消费路径开放，不直接写Tile。

不是原生光照等价实现：不模拟亮度衰减、彩色光、液体吸收、装备/手持光、其他灯具、Mod发光钩子；半砖/斜坡作全格遮挡，平台不遮挡，整数射线有角落近似；地下近身视距是约定的可读性范围。夜间无日光，只有近身和已支持火把。服务器暂停更新时不按墙钟推进揭示。

只采样当前目的地区域，不沿移动线插值，所以传送不揭示沿途。死亡/重生不清空地图；普通移动逐步积累可见区域，重新观察覆盖旧值。绑定成功动作在宿主确认后立即重采样目标；物体放置重采样完整占地。未重新观察的人类修改保留历史值。定期观察也可以看到未完成动作的实际副作用，这不改变动作终态。

## query_map 契约

握手 `server_ai_map_v1`，经原绑定身份/游戏线程/IPC、Python atomic dispatcher、Lua动态白名单接入。客户端不开放。

```lua
local overview = api('query_map', {mode='overview'})
local region = api('query_map', {mode='region', centerX=2100, centerY=300,
                               width=120, height=68, cellSize=2})
```

overview只接受mode，自动选择统一方格cellSize，整个世界最多128列×48行，保持Tile长宽比例（字符显示字体可能影响视觉比例），仅用于战略范围。region中心默认AI中心Tile；width默认120、1..128；height默认68、1..96；cellSize默认2，只接受1/2/4。范围以Tile数计，裁剪世界边缘，不足整格时最后一格聚合实际覆盖部分；返回大小最多128×96字符。坐标0..100000，越世界中心钳位到边缘。

沿用 `{valid,available,worldSessionId,actorGeneration,actorSlot,gameUpdate,observedAtUnixMs,clock,binding,data}`。data含schemaVersion、mode、worldUid、characterId、originX、originY、cellSize、width/height（裁剪后的Tile范围）、rows（字符串数组）、exploredRatio（已知Tile数/请求覆盖Tile数）、legend、knowledge、visibility。外层时间是查询投影时间，不是每格最后观察时间，也不保证格子仍是实时状态。

全未知`?`，全空格为空格，全实心`#`，全平台`=`；不同状态或部分未知为`:`，不会比例平均抹平单格洞口。液体存在时优先保留L/S/H/~标记，表示该格中存在，不表示满格液体或全格已知，exploredRatio独立计算。进一步用cellSize=1查询结构。Query只遍历内存地图，不能读取Main.tile补全未知；不会隐式揭示或刷新。

## 验证结果与限制

- 纯地图14项：未知/2×2单格洞口/放大/液体、世界与角色隔离、输出上限、文件恢复、跨身份拒绝、截断损坏、世代提交与哈希损坏拒绝；额外独立PowerShell进程恢复通过。
- 既有角色文件/生命周期39项通过。地图与原生角色一起跨五个真实服务器进程恢复，包含死亡、只读指针保存失败与损坏版本拒绝；地图哨兵值随角色世代恢复。
- 最终隔离原生场景35项通过：实际方块/火把放置消费和释放、实际铜镐挖掘与记忆更新、地下近身、火把扩大、遮挡、重新采样、传送中途未知、远处修改保留历史、死亡不揭示与返回刷新。移动/死亡通过隔离fixture状态注入，非图形操控/真实死亡重生全过程；原生生命周期组另外验证死亡存档恢复。
- 新Python地图2项通过，真实Lua Worker→dispatcher→loopback IPC，只读0动作、能力拒绝及参数上限。IPC绑定/旧身份/断连和原子/适配/聊天定向回归通过，query_map加入绑定协议矩阵。
- 既有test_server_ai_backend.py：32项中31通过、1项基线失败（test_perception_contract_is_capability_gated要求无扫描能力时提示词不含scan_tiles，但HEAD原有pcall错误示例已含该词；HEAD模块直接复现）。未改动此范围外问题。
- 原生工具IL定向检查通过（ItemUse19、Mining30、Placement119、Crafting11）；Mod构建0错误0警告。未运行全量测试。

复现：`tests/run_ai_map_tests.ps1`、`tests/run_world_character_tests.ps1`、`tests/run_server_ai_ipc_tests.ps1`；Python按test_ai_map.py/test_server_ai_backend.py文件执行；`tests/verify_server_ai_placement_native.ps1 -PackagePath ...`、`tests/verify_world_character_native.ps1 -PackagePath ...`。原生脚本创建新profile、复制世界并只终止自己的诊断进程；需要tModLoader日志写权限和localhost套接字。首次沙箱权限限制已在允许日志写入及套接字的环境重跑；证据目录保存最终结果与原始IL检查。

未验证真实模型自主使用地图、图形Host & Play、人类摄像机实际移动、真人并发修改、所有光源/Mod/全部世界规模压力；地图实现不引用摄像机。当前全图1字节/Tile，最大支持4000万Tile，概览扫描整张记忆图；高频概览可能影响游戏线程，V1不声称性能压力验收完成。现用游戏profile未部署。
