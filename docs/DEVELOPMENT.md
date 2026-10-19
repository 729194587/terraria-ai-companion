# 开发说明

## 环境基线

本次检查日期：2026-10-19。

| 项目 | 实际结果 |
| --- | --- |
| tModLoader | `2026.08.3.0 stable`，Terraria `1.4.4.9` |
| 安装目录 | `D:\steam\steamapps\common\tModLoader` |
| 官方提交 | `c9545cf27d56c456321e1896274c02adf67d0140` |
| 自带运行时 | `dotnet/dotnet.exe`，.NET `8.0.0` x64 |
| SDK | 自带 dotnet 报告没有 SDK；PATH 中未找到 dotnet |
| 构建方式 | tModLoader 自带 Roslyn 编译器，实际编译成功 |

M0 时的安装缺失情况已过时。此次重新检查了安装 DLL、版本、运行时和官方 targets。

命令行内置构建只需完整的当前 tModLoader 安装。若使用 IDE 或 `dotnet build`，需另行安装 .NET 8 SDK；本轮未安装 SDK，也未验证 IDE 构建。可用编辑器为 VS Code 或支持 .NET 8 的 Visual Studio。

## 工程结构

```text
CompanionBridge/
  CompanionBridge.csproj           # 导入当前安装的官方 tMLMod.targets
  build.txt                       # 名称、版本、side = Client
  description.txt
  CompanionBridge.cs              # 注册 P；维护开关和单调时钟起点
  Common/Players/CompanionPlayer.cs # 晚期输入、焦点日志、进入世界/死亡清理
  Common/Systems/CompanionSystem.cs # 实验更新入口、失焦暂停补丁、休眠恢复、离开世界清理
  Localization/*.hjson             # 快捷键中英文名称
```

工程目录初始为空，没有覆盖已有文件。工作区没有 M0 报告文件，因此未从聊天重建 `docs/research/m0-feasibility.md`。

## API 与调用顺序

- `KeybindLoader.RegisterKeybind()` 注册快捷键；在 `ModPlayer.ProcessTriggers()` 中读取 `JustPressed`，避免按住时每帧反复切换。依据官方 [ExampleKeybindPlayer](https://github.com/tModLoader/tModLoader/blob/v2026.08.3.0/ExampleMod/Common/Players/ExampleKeybindPlayer.cs) 和 [KeybindSystem](https://github.com/tModLoader/tModLoader/blob/v2026.08.3.0/ExampleMod/Common/Systems/KeybindSystem.cs)。
- M1.0 与 M1.1 基线使用 `ModPlayer.SetControls()`；失焦暂停实测失败后，M1.1 修复版改为 `PostUpdateRunSpeeds()`。仅在自动模式开启且本地玩家存活、有效、处于单人世界时写入 `controlLeft=false`、`controlRight=true`。关闭时直接返回，不抹除硬件输入，不写位置、速度或其他动作输入。
- `OnEnterWorld()`、`OnWorldUnload()`、`UpdateDead()` 和 Mod 的 `Load()/Unload()` 清除开关；`PreUpdate()` 对本地玩家失效、幽灵或非单人状态再作清理。开关不写入角色存档。卸载后不再注入；控制字段由下一次原生输入更新重置，不强行清空可能来自真人的输入。

已使用安装自带的 Mono.Cecil **只读检查本机 DLL 的 IL**，没有修改程序集。确认调用顺序：

```text
Player.Update:
  ResetControls
  hasFocus 检查
  TriggersSet.CopyInto（内部写入左右输入并调用 ProcessTriggers）
  PlayerLoader.SetControls
  ...
  PlayerLoader.PostUpdateRunSpeeds
  HorizontalMovement
  ...
  PlayerLoader.PreUpdateMovement
```

`CopyInto` 位于普通游戏输入路径；聊天使用 `CopyIntoDuringChat`。`SetControls` 在原生输入写入之后、水平运动之前，适合 M1.0 前台实验，但受焦点分支限制。修复版使用紧邻 `HorizontalMovement` 的 `PostUpdateRunSpeeds` 注入输入；`PreUpdateMovement` 对注入本帧水平输入太晚。

M1.0 与 M0 建议的区别来自范围收缩：M0 考虑失焦运行，M1.0 只做前台。M1.1 先保留原实现测试，收到用户失焦暂停反馈后才修改。IL 检查属于静态验证，不是游戏测试。

源码位置：[ModPlayer.cs](https://github.com/tModLoader/tModLoader/blob/v2026.08.3.0/patches/tModLoader/Terraria/ModLoader/ModPlayer.cs)、[Player.cs.patch](https://github.com/tModLoader/tModLoader/blob/v2026.08.3.0/patches/tModLoader/Terraria/Player.cs.patch)。本机同版本 `tModLoader.xml` 也描述了这些钩子的语义。

`side = Client` 在同版本 [BuildProperties.ReadBuildFile](https://github.com/tModLoader/tModLoader/blob/v2026.08.3.0/patches/tModLoader/Terraria/ModLoader/Core/BuildProperties.cs) 中按 `ModSide` 枚举解析；本机实际构建接受该配置。另按官方 TMOD 格式只读检查产物 `Info`：`side` 字节为 `1`，与本机 DLL 中的 `ModSide.Client = 1` 一致；打包版本为 `2026.8.3.0`。元数据检查仅验证配置；游戏加载另有用户实测确认，联机控制仍未实现。

## 构建、加载与调试

复现命令见 [README](../README.md#构建和测试)。`-build` 指向本项目的 `CompanionBridge` 目录，内部编译器无需 SDK；`-tmlsavedirectory` 把构建输出、配置和测试存档放在项目 `.local/tmodloader` 中，不覆盖原有角色、世界或 Mod 列表。

本次实际命令退出码为 0，输出：

```text
Reading properties: CompanionBridge
Building: CompanionBridge
Compiling CompanionBridge.dll
Compilation finished with 0 errors and 0 warnings
Packaging: CompanionBridge
```

产物位于 `.local/tmodloader/Mods/CompanionBridge.tmod`。修改源码后先退出测试客户端，再执行同一命令并重新启动，避免旧程序集或文件占用。

若需从游戏“创意工坊 → 开发 Mod”构建，可将 `CompanionBridge` 源码目录放入当前配置目录的 `ModSources/CompanionBridge` 后执行“构建并重新加载”；本轮仅验证了上面的命令行构建，未执行此菜单流程。

`.csproj` 导入官方 `tMLMod.targets`，继承 `net8.0` / C# 12 和游戏引用。默认安装路径对应本机；其他机器可通过 `-p:TModLoaderDirectory=实际安装目录` 覆盖。需确保 SDK 的 dotnet 在 PATH 中，因为官方打包 target 调用 `dotnet`。此 IDE/SDK 路径尚未实测。

游戏调试时先只启用本 Mod。当前实验版加载日志为 `CompanionBridge M1.1 focus experiment loaded; PostUpdateRunSpeeds, 10-second limit; auto move is off.`；每次手动切换有一条 `Auto move: on/off`，并在游戏中显示中文提示，不逐 tick 记日志。运行日志可通过游戏的“打开日志”入口查看；当前安装通常位于安装目录 `tModLoader-Logs/client.log`。

## 手动验收

本次未由 Codex 操作游戏界面。2026-10-19 用户反馈：Mod 已加载，但首次按 P 无提示、未移动；在“设置 → 控制 → Mod 控制”中手动绑定 P 后，确认自动右移成功。随后确认再次切换可停止，关闭后恢复手动操作；开启时 A/D 不改变移动，空格仍可跳跃，符合只覆盖水平输入的实现。这里只确认已反馈的行为，其余保持待测。

**M1.0 核心流程已由用户确认游戏实测通过。**此结果对应原版 `0.1`；不能代替新增时限或失焦行为的测试。

| 步骤 | 预期 | 本轮结果 |
| --- | --- | --- |
| 启用 Mod 并进入新单人世界 | 加载无异常，默认不移动 | 用户确认已加载；进入后未自动移动 |
| 控制设置中检查快捷键 | 显示自动右移，绑定 P | 用户找到该项，手动绑定 P 后生效；代码默认值不保证现有配置已绑定 |
| 安全普通平地松开方向键，按 P | 显示开启，持续向右，遇墙不穿过 | 用户确认自动右移；文字提示及碰撞未单独确认 |
| 再次按 P，保持方向键松开 | 显示关闭，原生减速至停止 | 用户确认停止；未测量减速时间，文字提示未单独确认 |
| 开启时按 A/D、空格，关闭后手动操作 | 开启时覆盖水平输入、保留跳跃；关闭后恢复手动控制 | 用户确认符合预期 |
| 按住 P 后松开 | 一次按下只切换一次 | 未执行 |
| 开启后保存退出、重新进入 | 默认关闭，不恢复旧模式 | 未执行 |
| 开启后死亡并复活 | 自动模式关闭 | 未执行 |
| 开启后退出并禁用/重载 Mod | 无继续注入或残留开关 | 未执行 |

测试停止时应松开 A/D，使用无坐骑、无外力、无异常状态的普通平地。手动按 D、冰面滑行、击退或下落不能直接作为停止失败的判据。若 P 无效，先在“设置 → 控制 → Mod 控制”确认实际绑定；若为空，手动设置。“模组配置”提示没有配置选项不表示按键注册失败。M1.0 按键排查时未修改输入配置文件或移动代码，也未确定首次绑定为空的内部原因。

## M1.1：失焦基线与修复实验

**基线 0.1.1：用户实测失败。**用户反馈点击其他窗口后整个游戏画面停滞，重新点击游戏才恢复。日志记录 `13:45:21.379` 开启、`13:46:19.325` 才执行 10 秒到期关闭，间隔约 58 秒，期间没有玩家更新中的焦点切换日志；支持更新路径被暂停的判断，不能仅靠替换输入钩子解决。

2026-10-19 再次检查安装版本未变，并只读核对本机 `Player.Update` IL：

- `PreUpdate` 调用位于 `IL_05a2`；`ResetControls` 位于 `IL_09bf`。
- `IL_09c4` 读取 `Main.hasFocus`，随后失焦跳至 `IL_16ef`，绕过 `IL_1010` 的 `PlayerLoader.SetControls`。
- `PostUpdateRunSpeeds` 位于 `IL_4649`，随后 `IL_464f` 调用 `HorizontalMovement`。

另外核对 `Main.DoUpdate`：`IL_07e6` 写入真实 `hasFocus`；随后失焦且 `netMode==0` 的路径在 `IL_0853` 设置 `gamePaused=true` 并返回，早于玩家更新，也早于 `CanPauseGame()`。测试配置实际为 `AutoPause=false`，所以关闭自动暂停无法绕过这条独立的失焦暂停路径。

**修复实验版 0.1.2：**

- 通过 tModLoader 自带 `IL_Main.DoUpdate` 仅修改上述单人失焦暂停条件。精确匹配 `hasFocus / brtrue / netMode / brtrue` 序列，对这个条件计算 `focused || 自动实验有效`；不改写存储的 `Main.hasFocus`，保留原生失焦输入清空。匹配不到就明确报加载错误，避免新版游戏上静默失效。
- `PostUpdateRunSpeeds()` 负责重写水平输入，避开被跳过的 `SetControls()`；保持原生加速、碰撞和减速。
- `On_Main.DoUpdate` 在原更新入口之前检查单调时钟的 10 秒期限，避免安全关闭也被上述暂停分支阻挡；若游戏线程完全停止调度，只能等到下一次更新清理。
- 仅实验有效时暂存并设 `InactiveSleepTime=TimeSpan.Zero`；手动关闭、超时、死亡、离开世界及卸载后恢复暂存值。没有修改 `autoPause` 配置、菜单暂停规则或新增后台线程。暂时提高后台更新频率可能增加 CPU/GPU 占用。
- 仅自动模式开启且焦点变化时记录 `focused` 和只读 X 坐标；超时输出一次日志、位置和提示，不逐 tick 记录。

**编译结果：**0.1.2 使用原命令配合 `-tmlsavedirectory "$repo\.local\m11-build"` 编译和打包，退出码 0、0 错误、0 警告。游戏占用旧包时复制失败；用户确认保存退出后，已替换 `.local/tmodloader/Mods/CompanionBridge.tmod`，核对 SHA-256 一致及包版本为 `0.1.2`。未修改存档、角色或输入绑定。

**修复版运行结果：2026-10-19 用户确认失焦后持续右移，约 10 秒后结束控制，游戏恢复失焦暂停。**本轮未由 Codex 操作游戏界面。日志也记录两轮失焦期间 X 坐标增加，到期分别在开启后约 10.009 秒和 10.003 秒执行关闭；第一轮 `13:57:24.960` 失焦时 X 为 `33907.7`，`13:57:33.276` 关闭时为 `35381.0`。这些端点记录支持后台更新和位移，连续移动及恢复失焦暂停的结论来自用户观察，不能由端点记录单独推导。手动提前关闭和恢复等未单独反馈的项目保持待确认。

复测步骤见 [README](../README.md#m11-人工实验)。超时后单人游戏恢复原来的失焦暂停属于预期行为。

| 检查项 | 状态 |
| --- | --- |
| 原钩子下失焦后持续移动／停止／整个画面暂停 | 用户确认整个画面暂停，基线不通过 |
| 修复版失焦期间持续移动 | 用户实测确认成功；日志支持后台更新和位移 |
| 修复版切回后 P 提前关闭及手动恢复 | 待用户复测（M1.0 已通过） |
| 修复版约 10 秒执行自动关闭 | 两轮运行日志确认，分别约 10.009 秒、10.003 秒 |
| 自动结束后恢复原生失焦暂停 | 用户实测确认通过 |
| 超时切回后不恢复过期输入 | 待用户单独确认 |
| 新版本退出重进、死亡及卸载清理 | 待用户实测 |

返回时若已有超时提示，不要再按 P 试图关闭，那会开始新一轮。位置快照仅作诊断，不足以单独证明后台连续移动；不要把短暂滑行、撞墙或超时混同于失焦失败。

### 收尾 Review 与验证

2026-10-19 Review 未发现阻断问题，保留用户实测的 v0.1.2 代码：

- 暂停绕过和输入注入均受单人模式、有效本地玩家及自动开关约束；没有改写真实焦点或位置、速度。
- 10 秒期限在原更新入口之前检查；关闭后停止注入，由原生更新重置输入。
- 休眠值只在首次启用时暂存，关闭和失效后恢复；离开世界、卸载先关闭模式再恢复，卸载移除两个更新钩子。
- 开关和计时不持久化，进入世界及死亡路径清除开关。上述生命周期结论是代码 Review，未代替验收表中的实测。

收尾重新执行内置构建，输出到 `.local/m11-build`，退出码 0、0 错误、0 警告；`git diff --check` 通过。本轮没有新增运行测试，也没有为文档更新替换用户正在使用的测试包。

## 已知边界

- 没有新增第三方依赖，未修改磁盘上的 tModLoader 本体；运行时钩子使用 tModLoader 自带的 MonoMod.Cil。直接读取 Mono.Cecil 仅用于诊断。
- 本原型只覆盖水平输入，开启时手动左移会被覆盖；关闭后不干预任何手动输入。
- 聊天期间不触发快捷键切换，但未增加自动移动的聊天暂停策略。测试前先关闭自动模式再输入文字。
- 较晚注入输入可能影响混乱等原生状态语义；异常状态、坐骑和其他 Mod 没有进行兼容性验证。实验限定无异常状态的普通平地。
- IL 补丁针对当前稳定版的具体分支；升级后必须重新核对并运行测试。
- 第二客户端、联机、IPC 和 Agent 均未实现；失焦基线失败，0.1.2 的单客户端失焦移动已由用户实测通过，其他验证范围见上表。
