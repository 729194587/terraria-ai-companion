# Server AI 动作接口

本文描述服务器独立 AI Player 可使用的移动、物品使用、挖掘和放置相关动作边界。实际可调用集合以 IPC 能力握手和 [Server AI 能力契约](SERVER_AI_CAPABILITIES.md) 为准。

## 执行原则

- 所有动作针对绑定的服务器 AI Player；不操作客户端本地玩家或真人玩家。
- 请求经 Python 校验和 Server AI IPC 进入游戏线程，再复用 Terraria 原生输入、物品、导航、挖掘和放置路径。
- 原子动作只表达单一意图，不隐式增加寻路、换工具、邻格补偿或无限重试。需要持续推进的复合工作使用 [游戏侧任务运行时](M1_TASK_RUNTIME.md)。
- 动作受理、原生调用和输入释放都不等于效果完成。每种动作按自己的世界/物品/位置证据核验。
- 取消、超时、断连、身份变化和 unknown 保留不同语义；结果不明时不自动重放。
- 导航计划和观察快照有采样时间、revision 与适用 profile；预测可达不等于实际到达，必须确认真实落脚和停稳。
- 未知装备、Buff、Mod 行为或引擎版本不应被假设为受支持能力。

## 任务级挖掘

`mine_target` 是游戏侧高层任务，不是普通单格原子挖掘。优先传入 `observe_scene` 返回的结构化目标对象；任务运行时负责选镐、站位、导航、重验和挖掘。生命周期、令牌、截止、效果证据及限制见 [游戏侧任务运行时](M1_TASK_RUNTIME.md)。

`server_ai_navigation_v1` 开放 `navigate_to({x,y})`。坐标单位为世界像素，Y 向下，点位于预期落脚面。游戏侧选择移动、挖掘或放置，并在同一个任务内串行执行。成功回执要求真实脚底覆盖目标点、落脚安全、停稳和输入释放。局部搜索受节点、时间和动作预算约束。

导航在当前站位比较物理搜索得到的移动路线与有界地形改造候选。改造候选沿用原生地形分类、工具与放置前置检查，模拟局部净空和支撑；每次改造后重新读取实际世界。自动开路限普通运动角色、干燥安全地形和局部平面，搭路使用快捷栏内合适的单格材料。`mine_target` 复用同一个导航控制器，保留原有挖掘站位与目标核验规则。

`navigate_to` 会在同一次请求内执行安全的部分路线。每次交接根据真实位置、速度和当前碰撞验证下一条边。搜索切片耗尽且没有安全前缀时，移动保持松开，后续游戏更新从实际状态换一种搜索顺序。连续切片仍无前缀时返回 `timeout` 和 `navigation_search_budget_exhausted`。行程继续受根任务截止时间约束。

## 接口维护

新增或修改动作时，同时核对：

1. Python Schema 与 Server AI 白名单；
2. IPC 协议、能力旗标和 C# 参数验证；
3. 游戏线程执行及 Actor 身份绑定；
4. 操作专属完成证据、输入释放和 unknown 处理；
5. 对应定向测试及真实原生验证边界。

## 实现入口

- `python/server_ai.py`、`python/atomic_operations.py`
- `CompanionBridge/ServerIpc/`
- `CompanionBridge/ServerPlayerRuntime/`
- [原子操作契约](ATOMIC_OPERATIONS.md)
- [导航](NAVIGATION.md)
- [多人权威确认](MULTIPLAYER_ATOMIC_CONFIRMATION.md)
