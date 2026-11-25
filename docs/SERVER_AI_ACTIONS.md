# Server AI 动作

Server AI 动作接口面向独立服务器 AI Player，覆盖身体输入、导航、物品使用、挖掘、放置和持续游戏任务。操作名称与参数通过当前连接的 API Help 提供。

## 执行路径

Lua Agent 调用 Python 宿主，宿主根据能力与 Schema 分发到 Server AI IPC。C# 在绑定 Actor 的游戏线程中调用对应执行器，复用 Terraria 原生输入、碰撞、物品和世界交互机制。

短动作由单项 Action 执行；需要跨多个游戏更新持续推进的工作由 TaskRuntime 管理。

## 自主模式

连接独立服务器 AI Agent 后，在游戏聊天栏输入 `/agent autonomy on`。Agent 会立即读取角色、局部场景和历史地图，选择一个目标，执行一次有界动作，再根据回执重新观察。`/agent autonomy status` 显示调度状态和输入释放状态。`/agent autonomy off` 撤销当前授权，取消自主任务，并在宿主确认释放后显示关闭完成。授权只对当前世界、角色和控制连接有效，进入世界时默认关闭。

普通玩家指令会暂停自主调度。宿主先撤销旧版本并释放自主动作，随后执行玩家任务，待玩家任务结束并确认释放后重新观察。角色死亡时保留目标和进展，复活后由模型依据新一代角色的状态重新决策。如果队友仍与 Boss 交战，观察中会包含活动目标和队友引用。自主模式可以参与活动战斗，宿主会拒绝召唤 Boss 等进度触发操作。

`observe_scene` 的可选 `resources` 层和 Lua 的 `sense_world({resources=true})` 提供局部资源扫描。普通宝箱和生命水晶使用左上角 Tile 锚点及 2×2 占用范围，梳妆台为 3×2。矿脉返回四邻接的同种矿石、范围和最多 64 个具体 Tile。边界截断、缺格或帧不匹配的对象标记为 `incomplete`。掉落物携带实体引用。自主观察另附历史记录、最近观测时间和新发现或状态变化，扫描本身不会写入探索地图。

自主导航期间，Agent 每隔至少 0.5 秒读取一次局部资源层。首次发现、对象补全、完整矿脉形状变化和已知资源消失可以触发模型评估。忽略发现会让当前导航继续。选择调查时，宿主按动作 ID 取消导航，取得终态和输入释放回执，再重新观察并决定调查动作。调查期间模型可以看到原探索目标和目标坐标。

可重试的模型请求失败后，Agent 分别等待 1 秒、3 秒重试。重试耗尽会显示 `model_blocked` 和错误原因。反复尝试同一失败目标或交替执行多个无进展目标会进入 `no_progress`。场景变化或再次输入 `/agent autonomy on` 可触发新决策。动作结果或输入释放不确定时，宿主暂停自主调度。

## 移动与导航

- `move_input`：执行有界方向和跳跃输入。
- `navigate_to_x`：沿水平方向到达指定世界像素 X。
- `navigate_to_landing_region`：到达二维落脚区域。
- `navigate_to`：持续导航任务，组合移动、挖掘或放置等局部动作。
- `move_on_rope`：沿原版绳柱攀爬或下降。
- `drop_through_platform`：执行原生平台下穿。

导航架构、运动能力和规划模型见 [导航系统](NAVIGATION.md)。

## 挖掘与资源采集

单项挖掘操作用于明确的 Tile 或 Wall 目标。任务级 `mine_target` 围绕观察得到的矿物目标组织工具选择、站位、导航、挖掘和结果汇总。掉落物收集使用独立的实体观察和拾取流程。

资源采集与通路清障共享部分原生挖掘能力，具体规划见 [通用地形挖掘](GENERAL_TERRAIN_EXCAVATION.md)。

## 放置与施工

单格 Tile、Wall 和已适配的多格物体使用对应放置操作。Blueprint 施工由 `build_place` 任务组织 Survey、依赖、工作站点、材料和原生放置。

- [Server AI 多格物体放置](SERVER_AI_OBJECT_PLACEMENT.md)
- [Building V2](BUILDING_V2_DESIGN.md)
- [Building V2 实现记录](BUILDING_V2_IMPLEMENTATION.md)

## 物品、装备与战斗

物品使用、库存转移和装备管理通过 Actor 专属库存接口完成。战斗入口包括主动攻击、自卫 Reflex 和 Boss Encounter。

- [背包操作](INVENTORY_OPERATIONS.md)
- [战斗系统](COMBAT_SYSTEM_DESIGN.md)
- [战斗配装](COMBAT_WEAPON_LOADOUT_DESIGN.md)

## NPC 与世界服务

Server AI 还提供住房、NPC 对话、商店、护士、重铸、合成和队伍操作。相关架构见 [世界交互](TERRARIA_WORLD_INTERACTION_DESIGN.md)。

## 实现入口

- Python Schema 与分发：`python/atomic_operations.py`、`python/server_ai.py`
- C# 动作适配：`CompanionBridge/ServerIpc/ServerAiNativeActions.cs`
- 任务运行时：`CompanionBridge/ServerIpc/TaskRuntime.cs`
- Actor 与原生操作：`CompanionBridge/ServerPlayerRuntime/`
