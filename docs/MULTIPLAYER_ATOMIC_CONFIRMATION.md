# 多人原子操作确认（0.1.37）

`mine_wall`、`place_wall`、`hammer_tile` 和 `interact_tile` 的多人路径已实现服务器确认。保留单人原生路径；未扩展 Lua、组合执行器或其他系统。现有 `mine_tile/place_tile/attack_entity` 的完成语义不因此变成服务器确认。

## 调用链和完成条件

`LLM 工具 → Python 严格校验/能力检查 → 原 TCP/ControlState 动作 → 客户端游戏线程校验 → Mod Arm/Armed → 原生 ItemCheck 或 TileInteractionsCheck → 捕获原生请求 → Mod Execute → 服务器游戏线程复检 → 原生 MessageBuffer.GetData → 实际状态核验 → 原生状态同步 + Result → 客户端状态一致 → 原动作终态/输入释放证据 → Python 工具结果`。

工具参数保持 `{x,y}` 整数 Tile 格（16px/格）；原有80px保守距离、正常重力、工具和目标限制保留。Python 要求 `server_confirmed_interactions_v1`，缺少能力不发送动作。这个能力表示客户端具备协议实现；实际服务器支持仍由握手确认，不能从客户端能力推定服务器已执行。

服务器身份来自实际 Mod 包发送者，不接受载荷中的玩家身份。每次请求绑定客户端/服务器世界会话、原 session/generation/actionId 及随机 nonce。Arm 只允许开始输入，绝不作为完成证据。原生请求在受控输入作用域内被截获，匹配操作和目标后仅通过 Execute 发往服务器，避免再发送一次无身份的原生修改包。

| 操作 | 服务器原生路径 | 实际成功证据 |
| --- | --- | --- |
| mine_wall | 消息17/action2 → 原生 KillWall；客户端先走正常纯锤使用与挖墙进度 | 原有非零墙变0；客户端墙状态与回执一致 |
| place_wall | 消息17/action3 → 原生 PlaceWall；校验选中墙物品及邻接条件，执行 ConsumeItem 钩子并消费原选中栈 | 目标出现预期墙；服务器同一槽恰好消费一件（包括最后一件）；客户端墙/槽数量与类型一致 |
| hammer_tile | 消息17/action7或14 → 原生 PoundTile/SlopeTile | 同类型目标 HalfBlock/Slope 实际改变；客户端形状一致；仅 Frame 美术变化不算成功 |
| 门 | 消息19 → 原生 OpenDoor/CloseDoor | 对象范围内实际门类型转换（至少三格）；客户端门状态一致 |
| 宝箱 | 消息31开箱、33关闭 → 原生网络开关箱逻辑 | 服务器 player.chest 变为锚点箱索引或-1，客户端打开索引一致；原生箱内容同步保留，不实现搬物品 |
| 开关/拉杆 | 消息59 → SetCurrentUser/HitSwitch | 对象完整占用格仍是原类型且各格 Frame 切换；客户端 Frame 一致；不证明下游整条电路效果 |

服务器再次检查玩家生存/建造权限、选中槽和栈、目标快照、射程、已发送区块、原生物品条件和对象完整占用范围。多格锚点沿用 TileObjectData.TopLeft。宝箱检查记录、锁定和其他玩家占用。所有修改进入已安装游戏的正常网络处理器及其 Mod 钩子；没有直接赋值 Tile/Wall 来伪造效果。

Terraria 原生客户端通常负责同步背包。为避免预测消费与服务器消费重复，Arm 前先同步当前选槽和物品；放墙动作中暂缓该槽的预测同步，服务器在放置效果成立后对服务器已有选中栈消费一次，再同步最终槽。该设计不是对恶意修改客户端的通用反作弊保证。消费钩子拒绝或执行过程中物品被其他钩子替换时明确 failed/unknown，不能声称世界副作用已回滚。

## 截止、取消和不确定结果

沿用原动作10秒绝对截止、750ms租约、续租、精确取消和游戏线程输入释放。服务器另对每名玩家只保存一个有界请求槽，租约750ms、绝对10秒；重复 Arm 不延长租约。每玩家每秒最多64包，单包最多8192字节、快照最多16格，接收队列最多256包，每帧最多处理64包。

服务器在执行前消耗请求身份并缓存终态，重复 Execute/取消不会重复执行或再次扣物品；切世界、断线、旧代次/nonce 的消息不能完成当前动作。原生异常返回 unknown，不重试原生修改。

取消在服务器执行前生效时返回 cancelled；已经执行的原生效果不能靠迟到取消撤销。客户端收到回执前取消、断线或超时，会保留 `evidence.mutationSent` 和 pending_server 证据，不报告 completed，也不自动重试。先读取世界/背包再决定下一步，尤其避免重复拨动开关或拉杆。

已收到 completed 服务器回执但客户端状态一秒内未一致，返回 unknown（server_result_client_state_not_converged）。只有状态一致才有 `authority=server_native_handler`、`serverConfirmationStatus=known`、serverWorldId/serverUpdate、nonce、前后状态及数量、clientStateVerified=true；最终仍须原 endEvidence，缺少释放证据时 Python 降为 unknown。

## 兼容与运行

Mod 从 side=Client 改为 **side=Both**。服务器、Agent 控制的 B 客户端和其他参与客户端 A 都必须安装同版本0.1.37，再重启加载。此前只在 B 安装的部署方式不能直接沿用。专用服务器不开放 Agent TCP 端口；TCP 仍在受控图形客户端本机。专用服务器与主机开服的真实运行仍待验收；部署记录如下。

2026-10-22 经用户要求，已备份并部署到 `.local/m12-a/Mods` 和 `.local/m12-b/Mods`，A已加入启用清单，其他Mod保留。A通过“主机并开始”运行的服务器沿用A的Mod；没有另行部署独立专用服务器配置。记录为 `.local/deployment-backups/deploy-0.1.37-20261022-202905.json`，部署包SHA256为 `41CCE7056DEC6ACA03184C342470F4DE013F2E7EF7D5FDB60A701FDDC5DFA93D`。未启动游戏或Agent，未修改角色/世界。

同机双客户端必须分开本地TCP端口。新增启动参数 `-companionbridgeport`（整数1024..65535；省略仍为18777，非法值明确报错），A使用18778，B使用18777；Agent继续只连接B。端口解析及完整C#回归通过，重新构建0错误0警告，日志 `.local/deploy-0.1.37-csharp-tests.log`。在两个终端分别运行：

```powershell
# A：真人/主机
Set-Location 'D:/steam/steamapps/common/tModLoader'
.\start-tModLoader.bat -tmlsavedirectory 'D:/Agent Project/terraria-ai-companion/.local/m12-a' -companionbridgeport 18778
```

```powershell
# B：Agent控制客户端
Set-Location 'D:/steam/steamapps/common/tModLoader'
.\start-tModLoader.bat -tmlsavedirectory 'D:/Agent Project/terraria-ai-companion/.local/m12-b'
```

构建产物：`.local/multiplayer-authority-build/Mods/CompanionBridge.tmod`。部署前完全退出对应进程并备份原包；按用户指定的服务器/客户端配置分别更新 Mod，不覆盖 Players、Worlds、配置或本地测试数据。

常规多人聊天仍可触发 Agent。也可在仓库终端使用本地入口，仍走同一模型/工具/成本统计和 IPC 安全链：

```powershell
$env:TERRARIA_AGENT_TRACE='1'
python -u -B python/agent.py --port 18777 --timeout 2 --local-input
```

回复输出到终端，`/quit` 退出；游戏内动作仍通过 Mod。已有日志选项不变；token统计在 stderr 和 trace 的 llm_cost/chat_cost 中，runtime日志不含这两项。

仍不支持锁箱解锁、假宝箱/其他家具、ModTile交互、带前景挖墙、混合镐斧锤、自动涂漆/相邻墙自动补洞布局。只支持白名单原生消息；只发送 TileSquare 快照的修改路径会明确 unknown/unsupported，不转交任意世界快照。

## 验证和真实游戏验收

自动验证包括 Python 参数/能力/Agent dispatch/终态证据；实际 C# ControlState、服务器身份协议及生产客户端适配器，通过隔离的客户端/服务器存储与网络替身验证；已安装 tModLoader 的原生 IL 布局与分派契约；真实 Mod 编译。离线网络替身与 IL 检查不证明真实多人同步、网络时序或原生世界行为已验收。

2026-10-22 实际运行结果：

- `python -u -B -m unittest discover -s tests -v`：287项通过。
- `pwsh -NoProfile -File tests/run_csharp_tests.ps1`：完整回归通过；`-AuthorityNetworkOnly`：生产多人适配器专项通过，含重复请求、取消前后、迟到回执、状态一致和最后一件消费。
- `pwsh -NoProfile -File tests/run_native_item_aim_tests.ps1`：通过；执行原生输入转换，结构检查原生工具/交互和17/19/31/33/59消息布局与服务器分派，未运行原生世界修改。
- tModLoader 0.1.37构建：0错误、0警告；`git diff --check`通过。
- 日志：`.local/multiplayer-authority-{python-tests,csharp-tests,network-tests,native-tests,build}.log`。测试需要本机线程/回环IPC的部分在获准的本地测试环境运行。未启动真实游戏/服务器、未调用付费模型、未部署。

真实验收需在用户选择的测试世界完成：

1. 核对服务器、A、B均加载0.1.37，握手含新能力；先完成单人回归。
2. B逐一挖可拆墙、放墙（两件与最后一件）、普通方块/平台锤击；A与服务器观察结果及消费恰好一次。验证超距、工具错误、天然不可拆墙、目标变化均不假成功。
3. 从不同占用格开关门/宝箱/拉杆；A占用宝箱时B应失败；锁箱与受阻门应失败。开关和拉杆每次只切换一次，并由A观察下游效果（不作为工具已核验范围）。
4. 检查每次 completed 均含服务器身份、前后状态、clientStateVerified及已知 endEvidence。模拟延迟/断线、执行前后取消、新聊天/P/Ctrl+C/死亡/切世界，确认无迟到成功、无重复执行，unknown时不自动重试。
5. 分别验收专用服务器和主机开服；核对非默认缩放、选槽同步和真实 Mod 钩子拒绝行为。当前未进行这两种真实联机验收。
