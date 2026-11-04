# Server AI 语义场景

`observe_scene` 是服务器独立 AI Player 的只读场景接口。它在游戏线程采样当前世界，返回带坐标原点、身份、覆盖和截断语义的结构化数据。它不代表摄像机画面，也不直接授予导航或世界修改许可。

## L0：局部场景

默认观察范围为角色附近 45×29 Tile，schemaVersion=2。默认层包括：

- `map`：面向模型阅读的字符地图与语义标记。
- `geometry`：逐 Tile 几何与碰撞形状。
- `objects`：识别出的资源、树木组件、家具、掉落物及其他对象。
- `hazards`：液体、接触危险及动态实体观察。
- `landingRegions`：导航可用的落脚候选。
- `placementCandidates`：单格方块放置候选。
- `objectPlacementCandidates`：多格原版物体（当前实现包括工作台）的锚点、占地与支撑候选。

层可通过 `layers` 按需请求。未请求字段会省略，不表示该层为空；`includedLayers` 表示实际返回的层。各集合有自己的 `complete`、计数和 `truncated` 语义，不能只检查总响应是否成功。

## L1：区域场景

具备 `server_ai_scene_region_v1` 能力时，可请求：

```lua
local region = api('observe_scene', {
    scale = 'region',
    width = 120,
    height = 68,
    layers = {'geometry', 'objects', 'hazards'}
})
```

- 宽度 45..128、高度 29..96，面积最多 8192 Tile；以 AI Player 为中心。
- schemaVersion=3；默认提供 `geometry`、`objects`、`hazards`，可选 `map`。
- L1 不提供 `landingRegions`、`placementCandidates`、`objectPlacementCandidates` 或可直接执行的目标令牌。
- L1 用于区域知识与资源定位；实际移动、挖掘或放置仍须经过 L0 新鲜观察及原生执行器校验。

旧服务器未协商该能力时，不应发送 L1 参数或假设宿主会静默降级。

## 编码与未知状态

- `map` 与 `geometry` 共用 Tile 原点 `originX/originY`。行/列索引从 1 开始；Tile 坐标为 `originX + column - 1`、`originY + row - 1`。
- L1 的几何、对象和危险网格使用 `encodingVersion=2` 字典编码，行可采用 RLE 或定长十六进制索引；按响应中的 palette 与 indexDigits 解码，不要把编码字符串当作普通字符地图。
- `coverage.knowledge` 区分已知、未知、未加载和未采样。未知格不能视为空气。
- `hazards.tileMask` 保留逐格危险来源；危险详情列表被截断不意味着掩码中的危险消失。掩码为 0 也不能证明未知格安全。
- `objects.kindGrid` 逐格表达已识别类别和内容键。空类别仍须结合 knowledge 判断，不能据此推断不存在资源。
- L1 采样受软时间预算限制，未采样格会明确标记；对象聚类/列表截断与逐格网格覆盖是不同概念。

## 对象与候选

- 对象记录包含类别、内容键、观察范围、完整性及必要的锚点/Tile 详情。树木等多 Tile 组件不等于一个可直接挖掘的单格目标。
- 只有 L0 返回的矿物对象在具备任务能力时才可能携带 `target` 令牌；优先原样传给 `mine_target`，不要重建或修改令牌。
- `landingRegions.items[].target` 用于二维落脚导航。
- `placementCandidates` 对应单格 `place_tile`；`objectPlacementCandidates` 对应多格 `place_object`。两种候选不可互换。
- 候选只表示观察时的有界预检结果。执行器会重新读取当前 Tile、材料、Actor 身份、原生范围与 Terraria 放置/碰撞规则。

## 动态危险

`hazards.entities.items` 可包含当前 NPC/弹幕的包围盒、原生速度、观察 Tick 和短时威胁推断。当前实现的威胁推断是在最多 12 个游戏 Tick 内按相对匀速包围盒计算接触；它不模拟地形、敌人 AI 转向、输入、加速度或真实伤害判定。没有预测接触、字段缺失或列表截断均不能证明安全。

专用服务器的光照数据可能返回 `available=false`，原因例如原生光照缓冲未验证。调用方必须把它视为不可用，而不是零亮度或黑暗事实。

## 地图记忆

`query_map` 读取既有 AI Map，是持久化探索记忆，不是实时 Tile 快照。历史知识可能过期，揭示近似不证明当前通路、可达性或物体仍存在。需要行动时重新观察并由原生执行路径验证。

## 实现入口

- Schema 与文本契约：`python/atomic_operations.py`（`SCENE_TEXT_CONTRACT`、`SCENE_REGION_CONTRACT`）
- Server AI 能力协商：`python/server_ai.py`、`python/server_ai_contract.py`
- 场景采样：`CompanionBridge/ServerIpc/ServerSceneReader.cs` 及相关场景/几何实现
