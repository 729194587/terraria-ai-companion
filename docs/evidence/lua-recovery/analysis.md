# 2026-10-29：Lua 失败恢复与放置反馈

## 原实机证据核对

先读取 `runs/20261029-scene/verdict.md`、全部 25 份 JSON、10 份 Lua，以及同一世界
`63c7c06e65634f54bac71c32f289f719` 对应的 27 个原始 evidence bundle。
报告与原始源码不一致的部分以实际提交源码、API trace 和动作回执为准，原文件未改写。
`c11` 对应 bundle `680b4742be80419a8fc2390bc63b5df4`，实际源码 SHA-256：
`e779354cc37516169127cc1ef447ce15b0ac95abba57a7b39cbe6639823d7b6a`。
源码明确写 `local cands=scene.data.placementCandidates.items`。

| c11 次序 / API callId | target | 工作台 footprint / support（派生 Tile 矩形 x,y,w,h） | 实际回执 |
|---|---|---|---|
| 1 / 4 | (2102,210) | (2102,210,2,1) / (2102,211,2,1) | failed，object_footprint_occupied；mutationAttempted=false、inputsAcquired=false、known 释放 |
| 2 / 5 | (2097,211) | (2097,211,2,1) / (2097,212,2,1) | blocked，previous_action_failed；未发送原生动作 |
| 3 / 6 | (2097,212) | (2097,212,2,1) / (2097,213,2,1) | blocked，previous_action_failed；未发送原生动作 |

这些是单格候选，不自带家具 footprint/support。表中矩形来自已验证 Work Bench 定义
Width=2、Height=1、Origin=(0,0)，**不是原观察返回的对象候选字段**。
不能把后两个宿主阻断写成原生拒绝或三个对象候选误报。
第一项回执的 reasonDetail 为 null，未记录当时究竟哪一格、哪种 Tile 阻挡。
稍后的 geo3/geo5 显示 (2103,210) 有 Plants2、(2104,210) 有 Torch，符合右侧占地阻挡的解释，
但这些并非 gameUpdate=36875 的同帧证据，不能反填成该回执的精确诊断。

其他实际程序也应区分来源：

- `c6.lua` 读取 **objectPlacementCandidates**，返回 candidateCount=0，没有发 place_object。
- `c12` 用 inspect_tile 筛手写位置，(2110,208) 被距离预检拒绝。
- `c13` 手写 (2097,210)/(2099,210)/(2101,210)，仅第一项占地拒绝，其余宿主阻断。
- `c14` 手写 (2092,210)/(2093,210)/(2091,210)，仅第一项距离拒绝，其余宿主阻断。
- `c15` 导航后使用同一手写列表，第一项玩家碰撞拒绝，其余宿主阻断。
- `c16` 手写 (2091,210) 成功，完整两格核验、消费 1 件、输入释放均确认。

**结论：** 本轮证据支持修正模型字段指引、保留失败前观察，以及有证明的安全拒绝续行；
不支持认定 ObjectPlacements 产生了错误候选。本轮未新增候选 API，未改 SceneGeometry.ObjectPlacements。

## 实际版本与包

原 server.log 启动记录为 2026-10-29 13:37，Local CompanionBridge **0.1.61**，
同一日志的世界身份、slot 254 与 API trace 一致；启动 modpath 为既有 Host & Play profile。
该 profile 包、启动器固定哈希、部署回执和先前取消回归证据的 SHA-256 一致：
`79396ee80594a77cb6f35158667792ee80e15097ea8f5cf836a0b81ff3b90071`。
部署发生在该次启动之前，包文件时间也早于启动；原日志没有独立打印运行时包 SHA。
因此这里区分“日志确认加载版本/路径”和“部署文件及启动器哈希佐证”，不伪造运行时哈希字段。
原日志的无修改选行、部署回执、启动器与源码/trace 都归档并记录来源哈希。

本轮 **0.1.62** 的新包 SHA-256：
`a1872dab28ca89429df10f73ba12588024e7486ffabdd052bcd7b9e8d2b127b0`。
原实机报告不属于此包的验收；本轮未覆盖正在使用的包、启动/停止用户游戏或调用真实模型。

## 实现与验收

- `lastObservation` 保存独立、有界、经过 JSON 校验的最后成功只读结果，不把异常变成 completed。
  64 KiB 超限显式省略。运行时异常后仍可从回执提取数据给下一程序；保留身份/时效/覆盖，
  不新增隐式缓存重放。编译期语法错误不可能执行同一 chunk 前面的 API，测试明确不伪造观察。
- 仅有绑定真实终态证明 failed、未尝试变更、从未取得输入且 known 释放、没有未知副作用时，
  宿主设置 continuationAllowed。Runtime 与 Agent 沿用同一判定，预算不重置；不按 reason 放行。
  已开始失败、取消、超时、断连、未知所有权/释放和 unknown 仍硬隔离。
- 预检 reasonDetail 实测核对占用格/Tile 类型、玩家/完整占地碰撞框、实际距离/80px/锚点；
  没有把预检拒绝标成原生执行。geometry/map 的原点一致，纯字符串只开放 sub/len，metatable 仍移除。

定向验证（未运行全量测试）：新增恢复/沙箱 6 用例，Lua Runtime/场景/合成 47 用例，
Agent 跨程序恢复及跨入口/共享预算 4 用例，服务器 unknown/取消/身份 3 用例。
其中 2 个核心场景另运行一次以保留完整原始源码/API trace/终态，不计为新增不同用例。
同包隔离原生工作台 45 项，旧 IPC/Lua/语义场景 8 次调用、4 动作，实际普通放置与上下落脚通过；
工作台矩阵内同时验证真实配方站点邻接与合成。IPC/原子/读取/适配/聊天为 45/40/57/97/30 项，
包 IL 66 项，构建 0 错误 0 警告。

这区分了原用户 Host & Play 的问题观察、离线替身测试和本轮隔离原生验证。
没有进行本轮真实图形 Host & Play 或真实模型复测，不能据此宣称模型自主选点效果已验收。
原始 payload 和逐文件哈希见 [manifest.json](manifest.json)。

消费端注意：未跟踪的 `runs/tools/bridge.py` 用 keep 白名单裁剪 Runtime 回执，当前不转发
lastObservation（也不转发完整 trace）。本轮保留该用户文件未改；正式 Agent 的 summarize
会保留新字段，定向测试验证的是完整 Runtime/Agent 回执。若继续用该外部测试桥验收新字段，
需要在其 keep 白名单加入 `lastObservation` 并重启 Python 进程；不能据被裁剪的回执判断宿主未保存观察。
