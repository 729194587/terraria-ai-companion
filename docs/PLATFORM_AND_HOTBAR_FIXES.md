# 0.1.31 平台与快捷栏修复

合并测试包 `.local/platform-fixes-build/Mods/CompanionBridge.tmod`，不要使用早期 `.local/platform-landing-build` 单项修复包。没有重写二维规划器。

## 木平台抵达

0.1.30 实测目标 X=[34864,35040]、surfaceY=3712。角色完成一次跳跃，实际停在 (34870.684,3670)，脚底正好3712、速度两轴为零、位置误差0；但脚下 Tile19 木平台在碰撞网格中为 `=`，旧最终判据仅接受 `#`，因而继续重规划后失败。另一轮落在下方泥土上已报告 arrived。

共享 `SafeFlatLanding` 现在接受完整实心顶面 `#` 和水平单向顶面 `=`，保留着地、净空、危险/未知、至少8px支撑、目标X/高度、速度、跳跃释放/计数的检查。斜平台与半砖仍不作为该水平表面目标。回归使用上述实测坐标，验证已停稳时直接 arrived、不再重规划。

## 快捷栏按键编号

`select_hotbar_slot(slot)` 的参数为游戏按键 **1..9、0**：1是第一格，0是第十格。不再要求用户使用内部数组0..9索引。新能力 `hotbar_keys_v1` 防止新版Python把按键语义发给旧Mod。

观察新增 `selectedHotbarKey`、`hotbarKeyBindings` 和背包物品的 `hotbarKey`；`selectedSlot/inventory.slot` 保留为内部数组索引，装备数组不添加快捷键。选取结果回传 `selectedHotbarKey` 和明确标为内部索引的 `inventorySlot`。Agent 对用户说明应优先用键号，不能把背包0号说成游戏快捷键0。

## 原生向下穿平台

新增 `drop_through_platform()`，参数为空对象。需能力 `platform_drop_v1`。

调用链沿用 `Agent → atomic dispatch → TCP start_atomic → 现有actionId/session/generation/租约 → CompanionPlayer.PostUpdateRunSpeeds → PlatformDropAction → 原生玩家更新 → 实际位置/速度/着地反馈 → 输入释放证据`。

该动作输入 `controlDown=true`，不改位置/速度，也不按跳跃键。核对本机 tModLoader 2026.8.3.0 的 Player.Update IL：PostUpdateRunSpeeds 位于 IL_4649，稍后 IL_7e44/7e49 把 controlDown 存入局部14，作为第一个布尔参数传给 DryCollision/SlopingCollision；DryCollision 继续传给原生 TileCollision 的 fallThrough 参数。不是把已有水平/二维绕路导航改名成下穿。

执行范围与结果：

- 须在支持的20×42正常重力、干燥角色状态下停稳，脚下为水平单向平台；完整实心块不能直接穿下。
- 检查同X下方最多64世界像素的首个安全水平表面及整个下穿通道。目标可为完整方块或水平平台；未知、液体/危险、阻挡和不足支撑拒绝。没有确认到落点即失败，不盲目下落。
- 离开原平台超过4px后松Down，防止连续穿过下一层。观察到预选表面上的真实着地、速度为零、跳跃释放/计数归零，连续两次更新确认后才 completed。
- 地形变化、掉血、水平偏移超过1px、越过目标高度等失败；3秒绝对截止。沿用750ms租约、renew_move(actionId)、精确取消和输入释放证据。终态仍为 completed/failed/timeout/cancelled/unknown。
- `evidence` 包含 startFeetY、targetSurfaceY、actualFeetY、实际位置/速度和Grounded。只发送Down不算完成。
- 用户要求“直接按下下平台”时，Agent先观察并优先用该工具；若之后需水平移动，再依据新观察决定。不用先绕到边缘再返回来替代直接下穿。高于64px或不支持的场景明确说明限制。

## 验证与真实测试

自动测试包括快捷键1/0与内部首末槽转换、实际选中字段；单层Down注入/释放、16/48/64px下穿、下一层平台捕获、通道/落点变化、未知/危险、健康、动作截止、取消和实际玩家挂钩释放证据。游戏碰撞使用替身，不宣称原生下穿或新版木平台抵达已实测通过。

真实测试需用户保存并退出B后加载合并包，再启动Agent：先问当前选中键号，分别要求选择1和0；在木平台上要求“直接按Down下平台”，确认不左右绕路、不连续穿过下一层且回执实际落地；再要求跳上原木平台，核对 arrived/completed。测试无安全下方落点、取消、断线及新消息中断。日志现在额外记录工具名、status和reason，便于区分执行结果与聊天措辞。

本轮验证：Python244项通过，C#全套回归通过，Mod构建0错误0警告。合并包已备份部署到B配置 `.local/m12-b`，客户端日志确认加载0.1.31，握手确认hotbar_keys_v1/platform_drop_v1，B已进入世界、Agent已就绪。新版三项行为仍待用户实测，本轮源码修改尚未提交或推送。
