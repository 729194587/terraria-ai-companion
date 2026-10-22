# 0.1.32：原生物品使用与挖掘距离恢复

## use_item 调用与坐标

`agent.py → atomic_operations.dispatch → start_atomic → ControlState → CompanionPlayer.PostUpdateRunSpeeds / AtomicOperations.Before → PreItemCheck / Aim → 原生 ItemCheck → PostItemCheck / RestoreAim → PostUpdate / After → 真实使用周期 + 游戏线程输入释放证据`。

- `use_item({})`：不指定 target，完全保留当前鼠标和 Player.tileTargetX/Y。只注入原生物品使用输入，不虚构朝向 32 像素的目标，不执行该虚构目标的加载、距离、重力或缩放检查。空物品、忙碌、身份变化和原生使用条件仍有效。
- `use_item({"target":{"x":104,"y":96}})`：target 是世界像素，Y 向下；保留已加载目标、正常重力、80 像素保守距离检查，支持当前 GameViewMatrix 缩放。

本机 tModLoader 的 `Player.Update` 在 `ItemCheckWrapped` 前已调用 `PlayerInput.SetZoom_World()`；`ItemCheck` 通过 PlayerLoader 调用 PreItemCheck。此时 Main.mouseX/Y 已是缩放反变换后的世界相对坐标；正常重力的 Main.MouseWorld 只再加 screenPosition。正确注入值因此为 `world - screenPosition`，与渲染屏幕像素不同。不能在该挂钩里再次应用 GameViewMatrix，否则会重复缩放。`ItemAim` 明确封装这个阶段的转换、有限数值检查和整数截断。

`tests/run_native_item_aim_tests.ps1` 读取已安装 tModLoader.dll，用 Mono.Cecil 将实际 SpriteViewMatrix、SetZoom_World/SetZoom_MouseInWorld、MouseWorld 方法体放到隔离的存储替身中执行；不复制 Main 静态初始化，不启动游戏。覆盖实际渲染矩阵正变换、原生输入反变换、生产代码注入及世界坐标重建，缩放 1/1.25/1.5/2、不同摄像机位置与整数误差。它也检查真实 Update/ItemCheck 的调用顺序；升级引擎后不匹配则失败。

精确鼠标与 Tile 目标只在本地玩家 ItemCheck 范围内注入，PostItemCheck 立即恢复，原有 finally/After 恢复兜底保留。撤销与动作身份检查继续生效。挖掘/攻击/放置的默认缩放限制保留，本次不宣称验证这些适配器在其他缩放下的完整原生行为。

## 完成与效果

`completed / native_item_use_cycle_finished` 和 `evidence.execution=native_use_cycle_completed` 只证明观察到使用动画/计时并结束，不证明召唤、命中、治疗等效果。`evidence.effectStatus=unconfirmed` 明确标注实际效果未知。

当前没有专用的雀杖召唤物身份、所有者及新旧实例核验器；Agent 的最终回复会明确“实际效果未确认”，即使脚本模型错误声称召唤成功也会被纠正。本次不凭使用动画、耗蓝或已有随从推断召唤成功。未来只有加入充分的感知证据判据后才能报告具体效果成功。

## 挖掘恢复

C# mine_tile 仍只负责挖指定格子，不导航。距离失败附带原目标 Tile 坐标/类型证据。只有 valid=true、status=failed、reason=out_of_conservative_80_pixel_reach 且有释放/世界证据时，Python Agent 才调用 mining_recovery；本轮聊天最多一次，至多六个额外工具调用，统计入工具成本与预算。

1. observe_terrain 重新取得同次玩家位置、尺寸、速度和局部碰撞。
2. inspect_tile 核验原世界、更新号、原坐标、同类型、已加载、已知碰撞与无已知危险。
3. 仅当当前普通 20×42 玩家已停稳且局部网格能证明平层连续支撑、整个行走通道为空、无危险/未知/孔洞时，计算留有距离余量的落点，调用既有 navigate_to_landing_region。原有二维规划器、租约、代次、取消、超时和真实落地反馈均复用。
4. 导航完成且取得同世界释放证据后，再次 observe_terrain 和 inspect_tile，检查停稳、目标有效及中心至目标 <=80 像素。
5. 对完全相同坐标重试 mine_tile 一次。无安全路线、目标变化、仍不在范围内、导航失败/超时/取消或重试失败即停止。取消异常向上传播，不继续调用。

这是保守的安全接近子集，不承诺自动寻找任意复杂、跨高度或未观察路线。新聊天仍取消整个旧任务；其他失败、超时、取消、未知不触发恢复。恢复不更换工具、不改挖别的格子，也不绕过 C# 的执行时检查。

## 验证与构建

- Python：**251 项通过**。`python -B -m unittest discover -s tests -v`，含脚本模型驱动 Agent dispatch 的恢复顺序/上限、无路/目标失效/世界变化/距离复查、取消、不可恢复失败及召唤误报拦截。
- C#：**全套通过，原生坐标契约测试通过**。`pwsh -NoProfile -File tests/run_csharp_tests.ps1`；原生坐标契约另运行 `pwsh -NoProfile -File tests/run_native_item_aim_tests.ps1`，安装目录可用 `-TModLoaderPath` 指定。
- Mod 构建：**0 错误、0 警告**。构建目录：`.local/item-use-recovery-build/Mods/CompanionBridge.tmod`，版本 0.1.32。实际结果以本次交付报告和 .local/item-recovery-python-all.log、item-use-csharp-tests.log、native-item-aim-tests.log、item-use-build.log 为准。

构建产物 SHA256：`4F637DFF481396E5B416655860C05D12AFC52D37DE1926F3E5669940FF5061D3`。

本次不启动游戏或 Agent，不部署、提交或推送。未进行原生雀杖召唤、真实挖掘、其他 Mod 改写 ItemCheck/鼠标的现场验收；离线 IL 契约与适配器测试不能替代这些真实游戏验证。
