# Server AI 多格物体放置

单件放置接口 `place_object` 只开放经过适配的原版 Work Bench 默认样式。蓝图通过 `build_diff` 比较世界，通过 `build_place` 组合调用统一原生放置路径。

## 接口与能力

需要服务端能力握手 `server_ai_actions_v1` 和 `server_ai_place_object_workbench_v1`。模型通过 Lua 调用：

```lua
api('place_object', {x = anchorX, y = anchorY})
```

`x/y` 是 TileObjectData 的 Origin 锚点 Tile 坐标，不是世界像素或角色中心。当前验证的 Work Bench 定义为 Width=2、Height=1、Origin=(0,0)，占地为 `(x,y)..(x+1,y)`，支撑为下方两格。

## 预检范围

- 当前选中物品必须是原版 Work Bench（ItemID 36），默认样式；不会自动选栏、走近或重试。
- 完整占地和支撑必须在世界边界内，锚点距离不超过 80 世界像素。
- 占地必须为空，或属于当前固定引擎原生放置逻辑允许清除的植物/装饰物；不能把“非实心”泛化为可覆盖。
- 支撑必须满足当前实现的双格水平实心支撑要求，不接受平台、液体、驱动、斜坡或半砖。
- 完整占地不能与 AI 或其他活跃存活玩家碰撞。
- 未知 Tile、未知 Mod 类型或不匹配的 TileObjectData 定义保守拒绝。

场景中的 `objectPlacementCandidates` 是工作台锚点候选，包含完整占地/支撑信息。它与单格 `placementCandidates` 不同；后者只对应 `place_tile`，不能传给 `place_object`。

## 原生执行

执行器在绑定 AI Player 的游戏线程中，通过既有原生物品使用/ItemCheck 路径进入 `Player.PlaceThing_Tiles` 和 `TileObject.Place`。保留 Terraria 原生范围、CanUseItem、TileLoader.CanPlace、占地/支撑、碰撞和物品消费规则。

- 不直接调用 WorldGen.PlaceTile/TileObject.Place 来伪造玩家动作。
- 不直接写 Tile、共享鼠标目标或 Main.myPlayer。
- 原生清除只在本动作拥有的放置调用作用域内归属；其他时间发生的 Tile 变化视为外部变化。
- 引擎 IL/方法签名/调用结构必须匹配已核验指纹；未知结构拒绝启用。

## 完成与失败

成功需要同时确认：

1. 本动作触发恰好一个原生物体放置事件；
2. 完整两格占地存在，类型、默认样式和 Frame 正确；
3. 支撑未发生不允许的变化；
4. 本次消耗恰好一件对应物品，库存变化一致；
5. 原生使用周期结束，绑定输入已释放；
6. 没有观察到目标被外部移除或替换。

前置条件不满足时明确拒绝且不启动动作。取消或超时可能发生在放置/消耗之后；已确认的副作用保留，不回滚。部分放置、消费不确定、原生异常或无法确认输入释放时返回 unknown，隔离后不重放。

## 当前边界

仅支持上述 Work Bench 形状与默认样式。其他家具、多格物体、ModItem、墙/平台、自动挖除阻挡物、自动放置材料、自动走近和自动重试均不在支持范围。图形多人环境和任意第三方 Mod 兼容性需单独验收。

## 实现入口

- 候选与观察：`CompanionBridge/ServerIpc/ServerSceneReader.cs`
- 原生放置：`CompanionBridge/ServerIpc/ServerAiNativeActions.cs`、`CompanionBridge/ServerPlayerRuntime/`
- Python 契约：`python/atomic_operations.py`、`python/server_ai.py`

## Server AI 蓝图施工

读取绑定能力中的 `server_ai_build_diff_v1` 后，可用 `api("build_diff", {blueprint=plan})` 检查材料与现场条件。施工另需 `server_ai_build_place_v1`。两者共用蓝图格式，原点是世界 Tile 坐标，条目使用相对坐标，多格物体使用其原生 Origin 锚点，材料按件数计算。

```lua
local task = api("submit_task", {operation="build_place", arguments={blueprint=plan}})
if task.taskId then
    return api("await_task", {taskId=task.taskId, milliseconds=5000})
end
return task
```

同一蓝图可在取消或部分施工后再次提交。新任务重新读取世界，跳过仍与蓝图匹配的目标；重新观察只能证明当前状态，不能将先前结果未知的放置追认为成功。任务的操作记录和最终世界差异分别用于核对实际执行与剩余目标。完整契约和验证范围见 [G2 实现规范](G2_BUILDING_HOUSING_IMPLEMENTATION.md)。
