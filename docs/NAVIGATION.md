# 导航系统

## 职责划分

Agent 选择目的地和策略；Lua 组织观察与动作；导航运行时负责在当前运动能力和观察范围内规划、执行、监测实际状态并核验落点。导航器不负责全局探索、采矿、建造或战斗策略。

## 主要操作

- `navigate_to_x`：沿水平方向导航到玩家碰撞框左边缘的目标世界像素 X。目标语义是水平穿越/到达，不是精确 XY 站位。
- `navigate_to_landing_region`：导航到观察结果提供的二维落脚区域。目标描述玩家左边缘 X 区间和脚底表面 Y；只接受当前执行器支持的落脚面。
- `move_input`：服务器 AI 的有界原生方向输入。它不做路径搜索或安全落点证明，撞墙、走下边缘或受伤都是可能的真实结果。需要受控导航时优先使用导航操作。

## 执行模型

规划基于当前 Player 物理状态、已验证的 Mobility Profile 和局部碰撞世界。搜索器输出有限的安全动作路线；执行器逐帧根据当前世界和实际 Player 状态重新核验，不盲目播放旧输入序列。

导航任务保留目标、Actor/World/Generation 身份、截止时间和进展预算。局部路线结束后可从真实状态继续规划；计划检查点必须经过实际位置、速度、支撑和跳跃释放确认。预测到达不等于真实到达。

## 安全检查

- Player、Tile、Collision 和输入操作都在正确的游戏线程执行，并绑定独立 Server AI Actor。
- 未知地形、危险格、未加载区域和超出采样范围不能当作空地。
- 落脚确认要求真实支撑、可接受速度和跳跃释放；未确认的落地保持 unknown。
- 运动能力变化会使旧计划失效；未知装备、Buff、坐骑、额外跳跃、翅膀或第三方 Mod 行为不自动视为受支持。
- 路线偏差可按当前恢复策略停止、释放输入并从真实状态有限重规划。只有宿主为特定已知失败明确授予 `continuationAllowed=true` 和允许的 continuation scope，Agent 才能按该范围重新观察并尝试有界 `move_input`；不能根据 reason 文本自行降级。恢复预算耗尽、危险或身份失效时终止。
- 取消、超时和断连必须释放输入。释放输入不代表角色已经停稳或到达目标。

## 已知范围

导航是局部、有界的，不保证全局可达、最短路径或对任意自然世界路线成功。深落差、液体、复杂机关、动态敌人干扰、特殊装备和任意 Mod 运动行为只有在明确支持并验证后才能使用。局部搜索预算耗尽不能解释为整个世界不可达。

`mine_target` 会把导航作为游戏侧任务的内部子任务；其生命周期和挖掘目标重验见 [任务运行时](M1_TASK_RUNTIME.md)。

## 实现入口

- 规划与执行：`CompanionBridge/Motion/`
- Server AI 导航适配：`CompanionBridge/ServerIpc/ServerAiNavigation.cs`
- Server Player 原生输入/钩子：`CompanionBridge/ServerPlayerRuntime/`
- Python API 与结果适配：`python/atomic_operations.py`、`python/server_ai.py`
- 观察与候选： [Server AI 语义场景](LOCAL_SEMANTIC_SCENE.md)
