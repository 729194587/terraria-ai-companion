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
3. 仅当当前普通 20×42 玩家已停稳且局部网格能证明平层连续支撑、整个行走通道无实心阻挡、无危险/未知/孔洞时（水平单向平台不算身体侧向实心阻挡），计算留有距离余量的落脚区间，调用既有 navigate_to_landing_region。原有二维规划器、租约、代次、取消、超时和真实落地反馈均复用。
4. 导航完成且取得同世界释放证据后，再次 observe_terrain 和 inspect_tile，检查停稳、目标有效及中心至目标 <=80 像素。
5. 对完全相同坐标重试 mine_tile 一次。无安全路线、目标变化、仍不在范围内、导航失败/超时/取消或重试失败即停止。取消异常向上传播，不继续调用。

这是保守的安全接近子集，不承诺自动寻找任意复杂、跨高度或未观察路线。新聊天仍取消整个旧任务；其他失败、超时、取消、未知不触发恢复。恢复不更换工具、不改挖别的格子，也不绕过 C# 的执行时检查。

## 验证与构建

- Python：**251 项通过**。`python -B -m unittest discover -s tests -v`，含脚本模型驱动 Agent dispatch 的恢复顺序/上限、无路/目标失效/世界变化/距离复查、取消、不可恢复失败及召唤误报拦截。
- C#：**全套通过，原生坐标契约测试通过**。`pwsh -NoProfile -File tests/run_csharp_tests.ps1`；原生坐标契约另运行 `pwsh -NoProfile -File tests/run_native_item_aim_tests.ps1`，安装目录可用 `-TModLoaderPath` 指定。
- Mod 构建：**0 错误、0 警告**。构建目录：`.local/item-use-recovery-build/Mods/CompanionBridge.tmod`，版本 0.1.32。实际结果以本次交付报告和 .local/item-recovery-python-all.log、item-use-csharp-tests.log、native-item-aim-tests.log、item-use-build.log 为准。

构建产物 SHA256：`4F637DFF481396E5B416655860C05D12AFC52D37DE1926F3E5669940FF5061D3`。

本次不启动游戏或 Agent，不部署、提交或推送。未进行原生雀杖召唤、真实挖掘、其他 Mod 改写 ItemCheck/鼠标的现场验收；离线 IL 契约与适配器测试不能替代这些真实游戏验证。

## 默认 Agent 运行日志（后续诊断修复）

此前 `python -u -B python/agent.py --timeout 2` 只输出终端日志；完整 trace 默认关闭，所以手动启动时没有文件日志。现同一命令会默认创建 `.local/diagnostics/agent-runtime-时间戳.jsonl`，启动时打印 Runtime log 路径；可用 `--log-file 路径` 指定文件。单文件最多5MiB，保留一个轮转备份。日志路径不可写会明确提示，日志错误不影响动作安全流程。

默认记录工具名、参数、执行状态、原始失败/释放证据及恢复步骤中的玩家/局部碰撞/目标格信息；不记录聊天正文、提示词、HTTP请求或背包全量内容，复用现有已注册密钥脱敏。详细上下文 trace 仍须显式开启。

首次不可恢复失败会被保留。之后的动作若被 `previous_atomic_action_failed; no automatic retry` 拦截，结果携带 previousFailure，最终聊天报告首次真实失败原因，不再用拦截提示覆盖它。旧进程须由用户自行重启后才使用这些 Python 修改，无需替换 Mod。

## 两次真实挖掘恢复失败的回归修复

新日志记录玩家 (34722.652,3718)，脚底3760，地面为完整方块；头部所在行232有 Tile19 水平木平台，目标分别为 (2184,232) 和 (2179,232)。

- 第一次恢复停止于 approach_blocked_hazardous_or_unknown：Python 将身体区域内的 `=` 和实心块一样拒绝了。现与原有 MotionSupport 一致，允许已知的水平单向平台侧向/身体重叠，仍拒绝实心块、半砖、斜坡、液体/危险和未知。最终实际碰撞及落地仍由既有导航验证。
- 第二次已移动并执行3段行走，目标却是零宽 X=[34793,34793]；记录中的可达预测停点为34793.766，仍不满足该点目标，随后原规划器触发50ms保护。恢复现生成最多16px宽的落脚区间，两端及其间都在72px距离圈内，保留距80px上限的余量；区间少于4px拒绝，整段通道和整个落脚区间的支撑一并检查。两次目标对应 X=[34873,34889] 和 [34793,34809]，脚底Y均为3760。未放宽规划预算、到达条件或失败停止规则。

新增Python录制地形回归与C#生产规划器/执行器回放（普通地面/平台碰撞替身），覆盖两次坐标、左右接近、目标区间内距离、整区间支撑及危险/未知/实心拒绝。这里只修改Python恢复逻辑及测试，不改C#生产代码；无需重新部署Mod，用户重启Agent后可再次现场验收。真实新版本效果尚待验证。

本次回归结果：Python260项通过，C#全套通过（含上述两次场景回放）。日志为 .local/recovery-band-python-tests.log 和 .local/recovery-band-csharp-tests.log。未启动游戏或真实Agent、未部署、提交或推送。

## 0.1.33：接近成功后的挖掘缩放修复

15:40的新日志确认恢复实际完成：目标(2180,232)，调用二维目标X=[34809,34825]、surfaceY=3760，玩家停在(34814.367,3718)，速度为零、actual/predicted误差0；随后再次观察玩家和目标，确实发起了一次原挖掘，才被旧的 item_aim_requires_default_zoom 拦截。这证明此前平台通行/落脚区间修复已在游戏中通过接近阶段，失败点已转到游戏端挖掘适配器。

0.1.33 mine_tile复用ItemAim在PreItemCheck的已验证坐标空间，解除其默认缩放限制，仍要求正常重力、真实所选镐、已加载且80像素内有效目标；仅真实Tile消失才成功。攻击和放置的缩放限制保留。本机原生IL契约增加检查：ItemCheck_UseMiningTools直接以tileTargetX/Y调用ActuallyUseMiningTool，挖掘路径没有额外SetZoom/GameViewMatrix变换，也不覆盖这些目标字段。适配器回归在1/1.25/1.5/2倍缩放下验证实际玩家挂钩目标注入、PostItemCheck鼠标/Tile恢复，以及只有Tile消失才完成；原生物理/联网挖掘效果仍待实测。

另一轮旧合并错误 requires_existing_tile_and_selected_pickaxe 现在拆成 target_tile_missing 和 selected_item_not_pickaxe，后者附所选物品类型、键号、内部槽位和镐力证据。Agent提示明确要求挖掘前查询当前背包、确认镐力，必要时先选镐；其他失败仍不得事后自动重试或绕过停止规则。

新包目录 .local/mining-zoom-build/Mods/CompanionBridge.tmod。此次需要更新Mod才能解除挖掘缩放限制，单独重启Python不够；部署前须保存退出B。未自行启动游戏或真实Agent，未提交或推送。

0.1.33实际交付：Python260项通过，C#全套与原生挖掘坐标契约通过，Mod构建0错误0警告。用户保存退出B且核验进程结束后，已备份并部署到 .local/m12-b，源/目标SHA256均为 D7C5D65053A4F695526ADBDAE0717C5E67BD97706111A745C0BF1731985098AD。记录 .local/deployment-backups/deploy-0.1.33-20261022-154626.json。未启动游戏或Agent，新包真实挖掘效果待用户验收；源码尚未提交或推送。

## 脚下目标坐标提示

后续真实测试已成功清除右侧平台，日志也确认距离恢复后Tile19实际消失。再测试“挖脚下土块”时，Agent选镐成功，却请求(2183,233)，游戏端返回target_tile_missing。此前最近地形记录为玩家顶部Y=3718、身高42、脚底Y=3760，对应脚底行235；233行是身体旁的空气。原子挖掘没有失败地忽略土块，而是模型选中了空格。

Python探索投影现在增加同次采样的terrain.underfoot：feetWorldPixels、centerFootTile、碰撞框底部覆盖列的candidates，附真实已知/未知状态、hasTile和Tile类型。只从已有观察解码，不额外读取游戏、硬编码玩家尺寸或自动改挖目标。候选不等于安全支撑/地面接触；未知、视口外、倒重力、空格、斜坡与半砖不作安全推断。Agent提示明确要求先inspect_tile核验候选确有Tile，再执行mine_tile，不把身体中心坐标当脚下。默认日志同步保存这部分几何和inspect_tile结果，便于核对目标选择。

该修改只涉及Python感知、提示和日志，无需重新部署Mod，用户重启Agent后生效；实际模型的新目标选择仍需真实游戏验收。

0.1.34进一步核验并开放单格方块放置的当前缩放支持，原因、范围与验证见 [单格放置缩放修复](PLACEMENT_ZOOM_FIX.md)。
