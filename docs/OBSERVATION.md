# M1.9 统一观察（当前 Mod 0.1.22）

状态：离线测试及本机 tModLoader 构建通过。2026-10-21 用户实测确认死亡期间仍能观察到自身所在位置的水体，掉落物观察正常。复活后无需重启 Agent、幽灵及移动中死亡的完整专项仍待确认；下文保留各版本实施时的验证记录。

0.1.22 另新增被动系统提示感知，击杀里程碑和死亡文本仅缓存供后续聊天使用，不自动发起模型请求；来源、协议和验收边界见 [游戏提示说明](GAME_MESSAGES.md)。该新增能力尚未部署或真实游戏验收。

## 0.1.20：死亡会话与实体诊断修复

死亡／幽灵状态仅调用 `RevokeControl`：取消导航、清除外部输入和租约、递增控制代次、拒绝排队动作及死亡期间新到达的动作；保留当前世界的 TCP、读取和聊天请求。复活后使用同一连接读取新快照，不恢复旧动作。失去行动权限不再直接断开会话；退出世界、卸载、实际断线以及原有手动接管仍按原设计关闭相关连接。死亡观察继续返回原因 `local_player_dead_or_ghost`，摄像机过期时仍诚实标记 stale。

Python 实体校验保留严格约束，并分别报告 `invalid_kind`、`missing_instance_key`、`invalid_instance_key_type`、`empty_instance_key`、`duplicate_instance_key`、`sample_tick_mismatch`、`invalid_in_viewport`（非对象条目另报 `invalid_entity`）。诊断包含观察 ID、更新号、实体数组索引／种类／实例键、出错字段、实际值和类型、预期条件；重复 ID 还包含首次出现的索引。诊断只选取这些字段，限制长字符串，不复制任意嵌套实体负载或完整对话。

诊断通过 `BridgeError.diagnostic` 进入 Agent 的失败工具结果，供模型回答错误原因；启用现有 Trace 时另有 `observation_validation_failed` 事件。没有启用新的默认日志，也不读取或记录 API Key。0.1.20 实施时尚未获取现场失败回执，因此保留校验并增加诊断；后续根因见 0.1.21 记录。

离线新增同一真实 loopback TCP 连接跨死亡／复活的读写、控制代次与晚到动作测试，以及诊断经过 Bridge、Trace、工具结果和 mock 聊天回复的测试。真实游戏仍需验证：死亡和幽灵期间聊天／观察持续可用、复活后不用重启 Agent、移动中的死亡停止且旧命令不恢复；若实体错误重现，保留字段级诊断以定位根因。修复构建输出隔离于 `.local/death-diagnostics-build`，不自动部署。

本次验证：155 项 Python 离线测试通过；C# 新增死亡生命周期及全部原有回归通过；compileall、git diff --check 通过；Mod 0.1.20 构建 0 错误、0 警告。未运行真实模型、未启动／重启游戏、未部署、未提交或推送。

## 0.1.21：真实回执确认掉落物实例键重复

2026-10-21 13:15（Asia/Shanghai）的 Trace 已确认：同一观察的实体数组索引 5 和 9，分别为类型 23 和 321 的不同位置掉落物，`slot` 和 `details.slot` 却都为 0，实例键都以 `item:0` 结尾，触发 `duplicate_instance_key`。此前 0.1.20 阶段根因未确认的结论由这份新回执补全。

生产代码原来从 `item.whoAmI` 生成实例键；本次实际读取到两个 0，因此不能将该字段作为可靠的客户端物品数组槽位。现在从实际客户端数组索引生成 slot、instanceKey 和物品 details.slot；玩家、NPC、弹幕同样采用实际数组索引。实例键仍限定于观察内，不承诺跨观察生命周期追踪，不改变弹幕 owner/identity。

保留所有实体及 Python 严格校验，不通过丢弃重复条目或放宽检查消除错误。C# 增加稀疏数组、多个同为 whoAmI=0 的物品和超过 12 条目的测试；Python 增加脱敏真实回执形状的失败／修复后校验回归。15 项观察测试及全部 C# 回归通过，compileall、git diff --check 通过，0.1.21 构建 0 错误、0 警告，输出 `.local/item-identity-build`。用户随后实测确认“观察掉落物正常”；不将该结果扩展为所有实体场景均已验收。

## 接口与文件

TCP JSON Lines 新增 `{"v":1,"id":1,"cmd":"get_observation"}`，回执 `status="observation"`，数据位于 `observation`。握手能力为 `viewport_observation_v1`。旧 `get_state`、`get_terrain` 和导航／聊天协议保留原行为。

- `CompanionBridge/ObservationSnapshot.cs`：独立 DTO、仿射摄像机逆变换、四边形与 AABB 相交、无损 Tile 编解码。
- `Common/Systems/ObservationReader.cs`：游戏线程读取、世界会话生命周期、最近渲染摄像机、玩家／Tile／实体／环境。
- `CompanionSystem.cs`：保留原更新入口，接入观察回调与 `PostDrawTiles`；`ControlState.cs` 仅在处理观察请求时调用回调，同一批请求共享一次快照，不获取或续租控制。
- `TcpControlServer.cs`：大回执边界、单次快照序列化、传输测量。
- `python/observation.py`：版本、行长度、字典索引、覆盖率及元数据校验；`control.py` 提供 Bridge 方法；`agent.py` 提供只读 LLM 工具。
- `tests/ObservationOfflineTests.cs`、`tests/test_observation.py`：新增测试；旧实体测试的消息边界随协议上限更新。

## 结构与时间

顶层包含 `schemaVersion=1`、`worldSessionId`、`observationId`、`gameUpdate`、UTC `sampledAt`、`phase="pre_DoUpdate"`、`status`、`completeness` 和 `samplingMilliseconds`。

所有游戏数据在现有 DoUpdate 原更新之前同步复制，TCP 线程只处理独立 DTO。不代表服务器同一时刻的权威状态；客户端网络同步年龄未知。世界切换和卸载清除摄像机并更换世界会话，不复用其他世界的快照。读取异常返回 `observation_failed`，不让异常逃入游戏更新。

摄像机在 `PostDrawTiles` 记录最终 `GameViewMatrix.TransformationMatrix`、`Zoom`、`screenPosition`、当前 GraphicsDevice viewport、绘制序号、来源更新号、UTC 和单调时钟。这个序号是观察器捕获次数，不是引擎原生帧编号。

使用视口局部四角 `(0,0),(W,0),(W,H),(0,H)` 逆变换，再加 `screenPosition`。viewport X/Y 是设备偏移，单独保存，不重复加进 SpriteBatch 局部坐标。不把 `offScreenRange` 离屏渲染余量加到最终视口。支持非奇异二维仿射变换，包括缩放、翻转和仿射旋转；不支持的投影矩阵明确报告摄像机不可用。

世界包围盒按 `floor(min/16)`、`ceil(max/16)` 得到左闭右开 Tile 范围，无额外边缘圈；与旋转四边形相比，这个矩形可能多覆盖角落 Tile，已在限制中说明。实体使用四边形与边界框的几何相交，而非中心或 480px 半径。

摄像机年龄大于 250ms、更新号落后超过 2 或来源更新号在未来，标为 `stale`。该阈值是初始策略，需游戏验证。暂停、失焦、死亡／幽灵、无当前世界摄像机、未加载或无法解释的 Tile 都给出原因。游戏线程完全停止时仍会触发现有请求超时，不承诺此时一定能产生新回执。

`ok` / `completeWithinScope=true` 仅指声明范围内的客户端数据扫描完整，不证明实际视觉可见、服务器最新或任意 Mod 语义完整。`partial`、`stale`、`unavailable` 均不能解释为“没有”。

## Tile 编码 v1

`terrain.bounds={x,y,width,height}` 为世界 Tile 坐标；每格 16px，Y 向下。行按 Y 升序，行内按 X 升序。

- `types` 为 Tile／Wall 的类型字典：种类、运行时 ID、稳定内容键 `Terraria/名称` 或 `Mod名/内容名`、是否 Mod 内容。
- `palette` 为完整格子描述字典；索引从 0 开始。
- 行 `{"encoding":"indices","data":[i0,i1,...]}`：每格一个 palette 索引。
- 行 `{"encoding":"rle","data":[count0,index0,count1,index1,...]}`：每对重复 count 格；count 必须为正整数。
- 同一快照可混用两种行。每行必须恰好解码为 width 格，共 height 行；索引必须有效。C# 测试逐格比较完整记录，Python 同样验证长度和索引。
- 扫描上限 131,072 格，超出时明确返回 partial 与原因，不裁剪为伪完整场景。

格子包含 `knowledge=read/out_of_world/unloaded`、Tile 存在／类型、Wall、FrameX/Y、半砖、Slope、solid/solidTop、Actuator／Actuated、导线、液体种类和数量、涂色、隐形／全亮涂层，以及接触伤害／热伤害／流血／触碰销毁／滚石标志。`wireMask` 位值：红 1、蓝 2、绿 4、黄 8。`solid/solidTop` 是引擎类型标志，解释碰撞时还必须结合 Frame、actuated、半砖和 slope，不能直接当作最终完整碰撞形状。

液体与危险独立于形状；空格、非碰撞物件、未加载和世界外不合并。多人先检查 `sectionManager.TileLoaded`。Mod Tile 的原始值保留，同时以 `collisionKnowledge=unknown_mod_hooks` 声明自定义行为未知。原版类型也可能受到 GlobalTile 等 Mod 钩子影响，普通标志不承诺重现任意 Mod 碰撞。

## 实体、玩家、环境与交互

遍历客户端活动玩家（包括本地、死亡和幽灵状态）、NPC、掉落物、弹幕，仅保留边界框与摄像机相交者。无 12 个上限，coverage 返回匹配、返回、遗漏数量。实例键绑定本次观察，不承诺跨快照跟踪槽位重用；弹幕另外保留 owner/identity。

统一字段为内容键、种类／槽位、世界边界框、速度、分类与依据、采样更新号；采样 UTC 继承顶层。玩家／NPC 有生命；物品／弹幕生命为 null，并有类型说明。NPC 复用旧保守分类，`friendly=false` 不等于敌对；弹幕独立返回 friendly/hostile、damage、owner、timeLeft 等证据，不套用 NPC 阵营模型。其他玩家的生命等为客户端已知值，非权威实时值。

本地玩家还包括魔力、方向、选中槽、0–9 快捷栏槽映射、完整 inventory/armor/dye/miscEquips/miscDyes 数组，物品类型／名称／数量／前缀、Buff 及剩余 tick、坐骑、重力、液体状态和常见运动参数。Mod 附加装备槽及自定义物品数据未实现，明确标为未知；不推断通用 grounded/collideX/Y。

环境包含昼夜、原生 time 单位、雨、风、云、血月、日食、霜月、南瓜月、沙尘暴、本地 invasionType 和本地玩家原版 Zone 属性；不声称服务端事件进度可靠，不生成整屏逐格生物群落，也不包含地图探索记忆。

Frame-important Tile 生成去重后的锚点候选，识别原版宝箱／梳妆台、门、标牌，其余为未分类候选。候选可能包含非交互装饰，Tile 锚点是推导值；能力标为 partial。没有打开宝箱、请求内容、读取未知容器缓存或执行任何动作。自定义 TileEntity、标牌文字、所有 Mod 交互语义尚待扩展。

引擎已知可能超出人眼能看到的信息，包括地下方块和隐藏导线。几何相交不等于经过光照、遮挡、隐形、UI 或 Mod 绘制后的实际可见性。

## 传输、LLM 与测量

回执最大 16 MiB（16,777,216 UTF-8 字节，含换行），请求最大 4,096 字节（不含换行），保留请求超时和 JSON Lines。超过回执上限返回 `ok=false,status=response_too_large,requiredBytes,maxResponseBytes`，不返回删减后的伪完整快照。Python 拒绝超限、缺少换行、无效 JSON、未知版本和不合法编码。

`samplingMilliseconds` 是游戏线程读取和编码时长。C# 在 TCP 线程将完整回执序列化一次，随后附加小型 `transportMetrics`；`measuredPayloadBytes` 和 `serializationMilliseconds` 测量附加 metrics 前的完整回执（字节数含换行），不含追加 metrics、最终缓冲区复制和网络写入。Python 的 `receivedBytes` 是实际收到的完整行长度。当前仍会分配 DTO、字典和输出缓冲区，没有分块／差分。

LLM 新增只读 `get_observation`，旧 get_state 不变；普通聊天不强制读取。后续聊天将旧观察工具消息替换为明确的过期占位，保留调用／回执配对，避免历史反复发送旧完整场景。没有添加截图、视觉模型、导航或战斗工具。

2026-10-21 离线生成样本（**不是游戏采样或性能基准**）：8,160 格，25 实体，891 个完整格子字典项；实际行长度 455,060 UTF-8 字节。一次冷运行编码 10.783ms，首次回执序列化 24.1757ms，metrics 前 454,876 字节。运行环境为 PowerShell/.NET 离线测试环境，包含 JIT 等影响，不能据此推导游戏 .NET 8 帧耗时。样本位于 `.local/observation-sample.json`，已用真实 Python Bridge 读取和校验，跨语言通过。

真实游戏采样时间、实际场景体积、分配量、IPC 端到端时延和 Token 数均未测量；未调用真实模型。Token 数需后续选定 tokenizer 离线测量。

## 验证与人工验收

自动验证：150 项 Python 离线测试通过；C# 新旧 IPC／租约／实体／地形／运动回归和新增观察测试通过；compileall、git diff --check 通过；本机 2026.08.3.0 stable 构建 0 错误、0 警告。构建产物 `.local/observation-build/Mods/CompanionBridge.tmod`，未部署。

复现：

```powershell
python -m unittest discover -s tests -v
pwsh -NoProfile -File tests/run_csharp_tests.ps1 -ObservationSamplePath "$PWD/.local/observation-sample.json"
python -m compileall -q python tests
git diff --check
```

人工验证应覆盖：

1. 多分辨率／缩放、窗口变化、瞄准镜头偏移、世界边缘、重力翻转，核对四角和 Tile 边缘；确认各光照／渲染模式下 PostDrawTiles 的 viewport 为最终世界视口。
2. 实心、平台及平台斜坡、四向斜坡、半砖、液体与方块共存、导线／制动器组合，核对原始值和逐格解码。
3. 屏幕边缘只有边界框相交的实体、超过 12 个实体、物品与弹幕、死亡／幽灵及多节 NPC；核对遗漏计数与实例键作用域。
4. 玩家背包、快捷栏选中、装备、Buff、坐骑及天气／群落变化；多人未加载区域、旧同步数据和未知宝箱内容。
5. 暂停、失焦、最小化、死亡、退出重进、换世界、断线；确认状态／年龄诚实且不复用旧世界数据、不获取或延长控制租约。
6. 记录真实 `samplingMilliseconds`、serializationMilliseconds、receivedBytes、格子数和实体数；验证普通聊天无需调用观察、需要场景时工具可被调用、旧局部工具仍工作。
