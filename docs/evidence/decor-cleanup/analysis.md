# SmallPiles 清理与工作台放置（2026-10-31）

实现基线：64aaef1；仅 Server AI 原生 mine_tile 的 SmallPiles（185）准入及装饰帧一致性检查有变化。place_object、原子生命周期和执行器不变；无自动选工具、走近、清邻块或重试。

## 来源与最小范围

真实 play2/play9 actionId `067cdcda30a144f79cffc61004725e58`，目标 `(2207,216)`，回执 `unsupported_target`（原始 JSON 附件）。该范围可解释并解除这个具体拒绝，尚未在原世界重放。

对当前安装的 tModLoader.dll 作 Cecil IL 只读核对（player-il.txt，DLL 哈希见 manifest）：ActuallyUseMiningTool 对纯镐且非 tileAxe/tileHammer 的目标调用 PickTile；PickTile 经 GetPickaxeDamage、WorldGen.CanKillTile、DoesPickTargetTransformOnKill、HitTile.AddDamage，再调用 KillTile。GetPickaxeDamage 对 tileNoFail 先给 100 伤害。原生测试确认 185 为 solid=false、cut=false、noFail=true、CanKillTile=true；样式 0/1 均经真实 Player 铜镐链销毁，非直接删格。

只新增精确 TileID.SmallPiles 白名单，不按非实心、tileCut 或 tileNoFail 泛化。Plants2(73)、VineFlowers(382) 在真实记录出现，但本轮不开放；箱柜、家具、虚化和其他原有拒绝保留。185 不同样式可能触发原版连带清理、掉落或诱饵行为，不提供物品归属或无副作用保证；本轮只验证两个单格样式。执行中帧改变按目标变化处理，避免采纳不同装饰状态。

## 验证

- native-object.json：61 项检查通过。两个样式：原生创建隔离 fixture → place_object 拒绝占地 → 取消一次采矿且目标仍在 → 实际铜镐破坏、目标消失和输入释放 → 新鲜 ServerSceneReader 观察 → 正常工作台放置、完整对象核验并且 stack 5→4。所有 Tile 注入仅在隔离诊断 Setup 中，不在执行链。
- 同矩阵验证箱子、工作台、73、382 拒绝且零工具击，并沿用支撑、完整占地、玩家碰撞、物品变化、取消和消费不明 unknown 检查。
- native-unknown.json：2 项放置后原生异常检查通过，保留实际世界效果、unknown、隔离和输入释放；仅终止创建的诊断进程。
- native-regression.json：既有 Actions/Atomic/Reads 隔离原生采矿、导航及取消回归通过。
- tool-tests.log：现有工具 IL 定向检查通过（含 30 项 G7 和 119 项放置检查）；build.log：0 errors / 0 warnings。

## 边界

没有图形 Host & Play 验收；没有真实模型决策或 Lua 闭环实机回放。闭环是相同原子后端和真实 Player 的诊断调用，证明执行能力，不证明模型会主动采用该策略。没有部署现用 profile。没有运行全量测试。装饰不同帧样式、连带邻格变化和其他装饰类别未全部覆盖。原生保护规则仍具有最终决定权。

Python 结果契约 15 测试通过（python-contract.log）。test_atomic_operations.py 的 12 测试中 11 通过；唯一失败是旧工具数量断言 31 与当前实际 32 不符。本次仅改工具描述，未增删工具；保留原始失败日志 python-verified.log，不顺带修复旧断言。
