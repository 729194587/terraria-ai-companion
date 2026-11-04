# 水平导航安全短程行走优化

## 开销核对与范围

原NavigationTask每个核验后的步行检查点重新调用StateSpacePlanner；该规划器枚举双向步行24/6帧及多种跳跃按住长度。直接路线即使是平地，也要建立标签/优先队列并搜索，而任务只执行路线首条边。现有行走预测已有完整制动尾段，执行器已有逐帧重算和真实反馈，因此可先用该预测生成单条边。

只在ServerAiNavigation构造的NativeNavigation显式开启；客户端和默认NavigationTask保持关闭，二维LandingRegion不走快路。使用启用/关闭同一个NavigationTask的成对离线对照，关闭模式就是原完整规划调用链；StateSpacePlanner.PlanCore算法和参数未变。不是把新碰撞算法与旧算法混作基线。

## 实现

SafeGroundWalk只生成Route/Prediction，不持有输入，不直接控制Player。普通profile、grounded、释放跳跃且起点可证明flat时，最多尝试现有24/6帧无跳跃预测。原PhysicsSimulator仍验证完整制动尾段；包括尾段在内的每帧必须保持同一Y(0.01px内)、通过原SupportedCheckpoint及世界SafeFlatLanding。靠近目标执行完整制动尾段，其余执行原预测的安全前缀。拒绝预计制动后超出目标前方12px的快路，回退原规划；没有放宽原到达或落地阈值。

无法证明、障碍、下降/边缘、斜坡导致的高度变化、未知、危险或非支持profile，均不将候选作为快路；按原StateSpacePlanner继续。首边照样交LocalMotionController逐帧复核、实际检查点/落地确认，原4次无进展、256动作、取消、释放、代次及unknown逻辑不变。未确认落地的快路也测试了60帧后unknown、无重规划/重放。

新增诊断PlanKind(state_space/safe_ground_walk)、SearchNumber(累计StateSpacePlanner调用)、FastWalkPlans(累计快路)。PlanNumber仍统计全部计划决策；快路Expansions=0、TimeBudgetReached=false不是在声称运行完A*。查询及原生日志均可看到来源和计数。

## 离线前后对照

正式PhysicsSimulator/NavigationTask/LocalMotionController，碰撞世界是离线替身。两个路径先预热，每方向/工作负载5组配对；下表取中位数。Move包含预测、运行前校验及模拟实际更新，不是游戏帧数或原生Collision调用耗时；时间只用于本机这个靶场，不外推FPS或真实游戏加速倍数。flat_without_proof只取1组，不能用其时间比较性能。

| 场景 | 方向 | 模式 | 计划次数 | 完整规划器调用 | Move调用 | 执行帧 | 总耗时中位数(ms) |
| --- | --- | --- | --- | --- | --- | --- | --- |
| continuous512 | 1 | 关闭 | 25 | 25 | 161912 | 202 | 97.438 |
| continuous512 | 1 | 开启 | 10 | 0 | 867 | 202 | 1.390 |
| continuous512 | -1 | 关闭 | 25 | 25 | 161912 | 202 | 127.066 |
| continuous512 | -1 | 开启 | 10 | 0 | 867 | 202 | 2.299 |
| five128 | 1 | 关闭 | 15 | 15 | 72145 | 350 | 46.829 |
| five128 | 1 | 开启 | 15 | 0 | 1385 | 350 | 2.940 |
| five128 | -1 | 关闭 | 15 | 15 | 72145 | 350 | 52.104 |
| five128 | -1 | 开启 | 15 | 0 | 1385 | 350 | 2.545 |
| flat_without_proof | 1 | 关闭 | 3 | 3 | 14429 | 70 | 5.828 |
| flat_without_proof | 1 | 开启 | 3 | 3 | 14429 | 70 | 8.298 |

连续512px两边最终X和执行帧一致；5×128px也是如此。后者计划次数15→15没有改善，只将15次完整规划器调用变成短程预测。低成本碰撞替身放大了纯搜索/枚举开销；实际原生Tile读取、日志和运行负载还会影响收益。

## 未改善和额外成本

二维目标、无平坦证明/profile不支持完全保留原路径。靠近障碍、边缘、斜坡和危险时快路失败不会改善绕路、全局搜索、64px下降限制或采矿后的窄支撑；可能额外付出最多两次有界行走预测的成本。本轮没有增加死路学习、缓存、后台规划、自动挖搭、走近/重试策略，也没有移植Terrabot的后退/恢复阈值。

## 验证与证据

- SafeGroundWalkOnly通过162个断言（含重复基准、确认期间中性输入断言，不是162个独立场景）：双向平地/连续5段，墙、斜坡、边缘、未知/危险回退，profile与世界证明能力、二维目标、不同时序下的危险变化、预测偏差、取消/新代次，以及未确认落地unknown。
- NavigationOnly、VerticalOnly保留原跳跃、连续障碍、不同高度/原地上升、平台/斜坡、落地与玩家挂钩定向回归；BridgeOnly与IPC组通过，含取消/手动接管/死亡代次与释放。首轮BridgeOnly因沙箱拒绝localhost套接字失败，开启套接字执行后通过，日志均保留。
- Python导航回执4项通过。Mod构建0错误0警告。
- 同包隔离原生旧读取/选栏/导航/物品/挖掘/取消/进程清理通过。水平导航actionId=9e696d78e5ef40f4b23bfb9677103213，3次快路、0次完整规划器调用、3个检查点MaxError=0并真实arrived；另一个动作取消，记录仍归属原actionId。native-events.jsonl逐行保存原日志，不改写回执。原生只验证开启后的链路，没有原生开关对照矩阵。
- 未运行全量测试、未部署现用profile；没有本轮图形Host & Play、真实模型自主行走、真人/敌怪干扰、大规模复杂地形压力验证。不能把离线保护用例称为原生碰撞全覆盖。

## 参考而非移植

Numen 61994fc的Trip将已规划路线交Navigator驱动、收实际Report；本轮仅借鉴产生计划与执行反馈分离，不引入其挖搭/权限/重规划架构。
Terrabot原压缩包未定位；读取了既有解压源码BotFollowGround、BotFollowProgress，路径和SHA在references.json。前者用便宜局部可走性判断优先地面跟随，后者记录实际进展和退避；本轮不复制源码，不采用其中“走过一段后只看身体不阻挡”的宽松分支、直接按键、地图访问或退避常量。新快路的物理、支撑与执行验证全部来自本仓库现有模块。
