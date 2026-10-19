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
| Python | `3.11.8`，脚本要求 3.10+，仅标准库 |
| 离线 C# 测试 | PowerShell `7.6.5` / .NET `10.0.11`；不替代游戏的 .NET 8 构建 |

M0 时的安装缺失情况已过时。此次重新检查了安装 DLL、版本、运行时和官方 targets。

命令行内置构建只需完整的当前 tModLoader 安装。若使用 IDE 或 `dotnet build`，需另行安装 .NET 8 SDK；本轮未安装 SDK，也未验证 IDE 构建。可用编辑器为 VS Code 或支持 .NET 8 的 Visual Studio。

## 工程结构

```text
CompanionBridge/
  CompanionBridge.csproj           # 导入当前安装的官方 tMLMod.targets
  build.txt                       # 名称、版本、side = Client
  description.txt
  CompanionBridge.cs              # 注册 P、启动/停止 TCP 服务
  ControlState.cs                 # 控制模式、请求队列和租约，无 Terraria 引用
  TcpControlServer.cs             # loopback TCP、JSON Lines 和异步回执
  Common/Players/CompanionPlayer.cs # 晚期输入、焦点日志、进入世界/死亡清理
  Common/Systems/CompanionSystem.cs # 实验更新入口、失焦暂停补丁、休眠恢复、离开世界清理
  Localization/*.hjson             # 快捷键中英文名称
python/control.py                 # 标准库命令行客户端
tests/                            # 实际 IPC 源码及 Python 的离线测试
```

工程目录初始为空，没有覆盖已有文件。工作区没有 M0 报告文件，因此未从聊天重建 `docs/research/m0-feasibility.md`。

## API 与调用顺序

- `KeybindLoader.RegisterKeybind()` 注册快捷键；在 `ModPlayer.ProcessTriggers()` 中读取 `JustPressed`，避免按住时每帧反复切换。依据官方 [ExampleKeybindPlayer](https://github.com/tModLoader/tModLoader/blob/v2026.08.3.0/ExampleMod/Common/Players/ExampleKeybindPlayer.cs) 和 [KeybindSystem](https://github.com/tModLoader/tModLoader/blob/v2026.08.3.0/ExampleMod/Common/Systems/KeybindSystem.cs)。
- M1.0 与 M1.1 基线使用 `ModPlayer.SetControls()`；失焦暂停实测失败后改为 `PostUpdateRunSpeeds()`。M1.3 沿用 M1.2 的注入路径，仅在自动模式开启且本地玩家存活、有效、处于单人或多人客户端时写入 `controlLeft=false`、`controlRight=true`。排除 dedicated server、服务器模式和正在断开的连接；关闭时直接返回，不抹除硬件输入，不写位置、速度或其他动作输入。
- `OnEnterWorld()`、`OnWorldUnload()`、`UpdateDead()` 和 Mod 的 `Load()/Unload()` 清除控制状态；`PreUpdate()` 对本地玩家失效、幽灵或不支持的游戏模式再作清理。M1.3 同时断开控制会话、使排队请求失效。开关不写入角色存档。卸载后不再注入；控制字段由下一次原生输入更新重置，不强行清空可能来自真人的输入。

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

`side = Client` 在同版本 [BuildProperties.ReadBuildFile](https://github.com/tModLoader/tModLoader/blob/v2026.08.3.0/patches/tModLoader/Terraria/ModLoader/Core/BuildProperties.cs) 中按 `ModSide` 枚举解析；本机实际构建接受该配置。另按官方 TMOD 格式只读检查产物 `Info`：`side` 字节为 `1`，与本机 DLL 中的 `ModSide.Client = 1` 一致；打包版本为 `2026.8.3.0`。元数据检查仅验证配置；游戏加载及 M1.2 联机控制另有用户实测确认，M1.3 仅确认 Python 触发 B 右移，停止及其他场景未明确实测。

## 构建、加载与调试

复现命令见 [README](../README.md#构建和测试)。`-build` 指向本项目的 `CompanionBridge` 目录，内部编译器无需 SDK；当前 M1.4 构建输出隔离到 `.local/m14-build`，退出 B 后再复制包到 `.local/m12-b/Mods`。不覆盖角色、世界或 Mod 列表。M1.0/M1.1 旧测试目录为 `.local/tmodloader`。

本次实际命令退出码为 0，输出：

```text
Reading properties: CompanionBridge
Building: CompanionBridge
Compiling CompanionBridge.dll
Compilation finished with 0 errors and 0 warnings
Packaging: CompanionBridge
```

当前构建产物位于 `.local/m14-build/Mods/CompanionBridge.tmod`（0.1.5）。修改源码后重新构建，退出 Bridge 客户端后复制并重新启动，避免旧程序集或文件占用；M1.4 尚未复制到 B，不覆盖正在使用的包和存档。

若需从游戏“创意工坊 → 开发 Mod”构建，可将 `CompanionBridge` 源码目录放入当前配置目录的 `ModSources/CompanionBridge` 后执行“构建并重新加载”；本轮仅验证了上面的命令行构建，未执行此菜单流程。

`.csproj` 导入官方 `tMLMod.targets`，继承 `net8.0` / C# 12 和游戏引用。默认安装路径对应本机；其他机器可通过 `-p:TModLoaderDirectory=实际安装目录` 覆盖。需确保 SDK 的 dotnet 在 PATH 中，因为官方打包 target 调用 `dotnet`。此 IDE/SDK 路径尚未实测。

游戏调试时 B 先只启用本 Mod，A 不加载。当前加载日志为 `CompanionBridge M1.4 loaded; TCP 127.0.0.1:18777; auto move is off.`；进入世界记录本地 player 索引和 netMode，手动切换和控制模式变化各有日志，不逐 tick 记录。运行日志可通过游戏的“打开日志”入口查看。共享安装下日志通常为 `tModLoader-Logs/client.log`、`client2.log` 等，不能假定 A/B 文件名固定；按 `Saves Are Located At` 和 Bridge 加载行区分实例。

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

## M1.2：双客户端联机实验

M1.2 的历史测试包版本为 **0.1.3**，沿用 v0.1.2 的输入钩子、单调时钟和 10 秒保护。该阶段新增行为仅限允许多人客户端控制及显式服务器/断线隔离，没有新增网络协议或通用控制框架。M1.3 历史包为 0.1.4，当前 M1.4 包为 0.1.5，详见后文。

### 当前版本依据

- 本机仍为 `2026.08.3.0 stable`。`Main.DoUpdate` 的原生失焦提前返回要求 `netMode==0`，多人客户端 `netMode==1` 不走这条路径；`CanPauseGame()` 也只对单人暂停。因此单人失焦暂停的 IL 绕过现在明确限定 `SinglePlayer`，不改变多人分支；自动模式下的临时休眠调整同时用于单人和多人客户端。
- 玩家输入的焦点限制仍存在，继续使用 `PostUpdateRunSpeeds()` 在原生输入清空后注入。`Player.whoAmI==Main.myPlayer` 限定本地玩家；`!Main.dedServ`、允许的 netMode 和 `!Netplay.Disconnect` 限定可控实例与连接状态。系统层使用相同条件，失效时撤销开关并恢复休眠值。
- `side = Client` 保持不变。[ModSide.cs](https://github.com/tModLoader/tModLoader/blob/v2026.08.3.0/patches/tModLoader/Terraria/ModLoader/ModSide.cs) 定义客户端 Mod；[ModNet.SyncMods / SyncClientMods](https://github.com/tModLoader/tModLoader/blob/v2026.08.3.0/patches/tModLoader/Terraria/ModLoader/ModNet.cs) 显示强制同步列表针对 `Both`（及必要的 NoSync 依赖），客户端额外的 `Client` Mod 不因服务器缺少它而禁用。本 Mod 没有依赖 Mod，也不发送 `ModPacket`。
- 本机 DLL 的 `Main.TrySyncingMyPlayer()` 使用原生 `NetMessage.SendData`，包括消息 13（玩家控制）。M1.2 依赖这条既有同步路径，不手动发送移动包、替换 `Main.myPlayer` 或模拟额外 Player。A 已观察到 B 移动和停止；平滑性与详细同步时序未单独验证。

以上是源码/IL 依据；实际握手和移动结果另列在下方验收表中，不以静态检查替代运行验证。

### 隔离配置和启动

| 实例 | 配置根目录 | Mod | 角色/世界 |
| --- | --- | --- | --- |
| A：真人、Host & Play | `.local/m12-a` | `Mods/enabled.json` 为 `[]`，无 Bridge 包 | 新建 HumanTest 和测试世界 |
| B：Bridge 测试玩家 | `.local/m12-b` | 仅 CompanionBridge 0.1.3 | 新建 BridgeTest，通过 IP 加入 A |

两者使用同一安装目录，不复制游戏商业文件，不使用云存档。当前已经创建 A 的空 Mod 列表，B 的包及启用列表由构建生成；没有复制或覆盖旧 `.local/tmodloader` 中的角色、世界、设置和按键绑定。`-tmlsavedirectory` 指定最终目录，不再追加 tModLoader 子目录。以后重新准备配置时，不要在已有用户设置上盲目写入空列表。

构建 B 和两份完整启动命令见 [README](../README.md#m12-双客户端测试)。先在一个 PowerShell 中启动 A，加载完成后在第二个 PowerShell 中运行 B 命令。不要通过 Steam 库重复点击“开始”；不使用 `-skipselect`，以免进入单人世界。

A 选择“多人游戏 → 主机并开始”，如有 Steam 联机开关则关闭，使用默认端口 7777。B 选择“通过 IP 加入”连接 `127.0.0.1`，端口和密码与 A 一致。两端都必须是相同版本 tModLoader（不是原版 Terraria）。仅 B 在 Mod 控制中绑定 P 并启用自动模式；安装和启用 Bridge 本身不会自动接管玩家。

官方 [Main.cs.patch 的本地服务器启动代码](https://github.com/tModLoader/tModLoader/blob/v2026.08.3.0/patches/tModLoader/Terraria/Main.cs.patch) 会把当前 `-tmlsavedirectory` 和 `-modpath` 传给 Host & Play 子进程。收尾已核对 `server.log`：使用 `.local/m12-a`，未见 Bridge 加载记录，与 A 配置一致。

本机双开已由用户确认；若其他环境使用官方脚本无法双开，记录错误后反馈，不能用编译成功替代双开验证。不要编辑游戏 DLL、使用伪造 Player 或照搬发行版未核实的 `-nosteam` 参数。

日志仍位于共享安装的 `tModLoader-Logs`；官方 [Logging.InitLogPaths / GetFreeLogFileName](https://github.com/tModLoader/tModLoader/blob/v2026.08.3.0/patches/tModLoader/Terraria/ModLoader/Logging.cs) 会在文件被占用时分配编号。检查日志内的存档根目录来识别 A/B；B 加入后预期记录 `netMode=1` 和自己的 player 索引。A 及 Host & Play 的服务器日志不应出现 Bridge 加载记录。启动脚本的 launch.log 可能共用，不用它单独判定角色身份。

### 本轮验证状态

内置构建输出到 `.local/m12-b`，退出码 0、**0 错误、0 警告**。只读检查包头和 Info 确认版本为 `0.1.3`、构建版本 `2026.8.3.0`、side 字节为 `1`（Client）；确认 A 无 Bridge 包且启用列表为空，B 仅启用 Bridge；`git diff --check` 通过。

2026-10-19 用户确认已出现两个角色，按 P 自动右移成功。游戏由用户操作，Codex 只读核对运行日志：A 的 `client.log` 使用 `.local/m12-a`，未见 Bridge 加载记录；B 的 `client2.log` 使用 `.local/m12-b`，列出 `Client Side mods: CompanionBridge`，在 `14:16:35.732` 以 `player=1, netMode=1` 进入世界。

B 第一轮于 `14:17:14.012` 开启，`14:17:15.594` 失焦时 X=`33887.0`，`14:17:24.027`（约 10.015 秒后）执行到期关闭时 X=`35376.6`；第二轮于 `14:19:13.179` 开启、`14:19:14.646` 手动关闭。日志证明开关变化及失焦后的端点位移，不单独证明 A 画面中的连续移动、停止或同步平滑性，也不证明 A 的按键未受影响。

用户随后确认从 A 看到 B 按时停止，且真人操作不受影响。因此远端停止与 A 输入隔离已有用户实测依据，不再仅依赖 B 的本地日志；同步平滑性和未单独反馈的清理项目仍待确认。

收尾时用户进一步明确确认：两个独立客户端成功加入同一世界，B 按 P 自动右移，切换回 A 后 B 仍继续移动，A 能正常观察到 B 的移动。这补足了连续失焦移动与远端可见性的人工验收依据。

| 验收项 | 当前结果 |
| --- | --- |
| A 无 Bridge，B 包为 0.1.3、side=Client，配置隔离 | 本地文件及两客户端日志已检查，服务器日志也使用 A 配置且未见 Bridge 加载 |
| 两个真实客户端进入 A 主持的同一世界 | 用户确认两个角色，B 日志确认 netMode=1 加入 |
| B 保留额外 Client Mod，A 无 Bridge | A 本地无包、日志未加载；B 日志列出 Client Side Mod 并成功加入 |
| B 按 P 自动右移 | 用户实测确认成功 |
| A 能观察 B 失焦后持续右移 | 用户明确确认通过 |
| A 的操作不受影响 | 用户实测确认通过 |
| B 约 10 秒后在 A 画面中停止 | 用户确认按时停止；B 日志记录约 10.015 秒关闭开关 |
| B 提前按 P 关闭后手动操作恢复 | B 日志有手动关闭记录；手动恢复待用户确认 |
| 无明显瞬移、回弹、不同步或断线 | 待用户实测 |
| B 退出重连、死亡/复活时不恢复旧指令 | 待用户实测 |
| 0.1.3 单人回归 | 待用户实测 |

先只开两个窗口，使用足够长的安全普通平地，保持双方互相可见；初测不要最小化 B，不使用坐骑、异常状态或额外 Mod。第一轮 B 按 P 后切回 A，观察完整 10 秒；第二轮提前切回 B 按 P，确认关闭，再测手动操作。多人超时后世界继续运行，不会恢复单人失焦暂停；停止的判据是释放输入后的原生减速，不要求瞬间速度归零。

### 排查边界

- 连接失败：先确认 A 已完成 Host & Play、端口/密码一致，再检查同版本与各自日志。无需把客户端 TCP 源端口设成不同值，只有服务器监听 7777。
- A 要求 Bridge：检查是否误启用旧包/其他 Both Mod；本轮 A 不放置 Bridge 文件，不在全局 Steam 启动选项写入同一个存档路径。
- B 仅自己画面移动而 A 看不到、或超时后 A 仍看到持续移动：记录两端观察、时间和日志，不以本地输入日志判为同步成功。
- 同时运行两个图形客户端与 Host & Play 服务器会增加资源占用，可降低 B 的分辨率和画质，但本轮没有测性能或降低模拟更新频率。

### M1.2 收尾 Review

2026-10-19 Review 未发现阻断问题，保留用户实测的 0.1.3 代码，不扩展功能：

- 玩家层和更新层均排除服务器、无效/死亡玩家及正在断开的连接；玩家层额外要求 `whoAmI == Main.myPlayer`，没有修改远端玩家输入。A 不加载 Bridge，B 必须按 P 才开启控制。
- 10 秒计时和失效关闭仍在更新入口检查。单人 IL 暂停绕过明确限定 `SinglePlayer`，多人保留原生更新路径；关闭后不再注入输入。
- 休眠原值首次启用时暂存，关闭/失效后恢复；退出世界和卸载先关闭开关再恢复，卸载移除两个钩子。生命周期结论来自代码 Review，相关游戏测试仍按上表保留待测。

收尾重新构建到 `.local/m12-build`，退出码 0、0 错误、0 警告；`git diff --check` 通过。未覆盖正在使用的 B 测试包、角色或世界。游戏实测由用户执行，本轮未新增自动化图形界面测试。

## M1.3：Python Control Bridge

本节记录 M1.3 历史版本 **0.1.4**；M1.4 的菜单连接规则和新接口见后文。沿用输入注入、失焦更新及客户端身份限制，只新增本机控制通道。完整启动和人工验收命令见 [README](../README.md#m13-python-控制)。B 进入世界后运行 `python python/control.py move_right --seconds 5`，无需按 P。脚本每次连接打印实例 ID，正常结束发送 stop，出错退出而不重连。

### 协议和控制优先级

- 服务只监听 `127.0.0.1:18777`，与联机端口 7777 无关。一次只接收一个控制连接；第二连接被关闭。固定端口被占用时记录错误，保留 P 功能；不自动选择其他实例或端口。
- UTF-8 JSON Lines；每行内容最多 1024 字节，每条命令必须有 `v:1`、递增的正整数 `id`、`cmd:"move_right"` 或 `cmd:"stop"`。握手为 `{"v":1,"type":"hello","instance":"实例ID","lease_ms":750}`。
- 成功回执为 `{"v":1,"id":1,"ok":true,"status":"applied","mode":"external"}`；拒绝状态包括 `manual_active`、`expired`、`invalid_id`，生命周期清理也会使排队请求失败或连接关闭。非法 JSON、未知命令、过长消息或队列溢出关闭连接，不续期。
- 外部移动租约为 750 毫秒，Python 每次收到回执后约 100 毫秒续发。用 `Stopwatch` 单调时钟，从完整命令接收时刻计算；主线程积压不会获得新的完整租约。输入和回执队列分别最多 32 项。
- P 优先：先断开外部会话，正在移动则停止，否则开启最长 10 秒的手动模式。外部 move_right 不抢占手动模式；Python 收到拒绝后直接退出，不追加 stop。明确发送的 stop 可以停止任一模式。
- 外部模式靠租约约束，不沿用手动模式的 10 秒总上限；持续有效续期可以持续移动。随附 CLI 将每次操作限制为 60 秒以内，默认 5 秒。断线立即撤销外部状态，停发但不断线则租约到期撤销；释放输入后的实际减速由原生机制处理。

### 线程和生命周期

`TcpControlServer` 使用异步读写和后台任务，只处理协议、纯 C# 控制状态及完成通知，不读取或操作 Terraria 对象。接收端将命令与时间戳放入有界队列；`On_Main.DoUpdate` 在原生失焦暂停分支前检查玩家有效性、应用请求并检查期限。回执由后台写任务发送；`applied` 表示更新线程已处理请求，不代表远端已经看见位移。

`ControlState` 通过短临界区保护模式与队列，锁内不进行网络 I/O。每个会话有独立编号，旧连接的清理不能撤销新连接。网络 EOF 只撤销纯控制状态；输入字段和休眠值仍由游戏线程处理。进入/离开世界、死亡、失效或卸载时清除状态并取消会话；卸载停止监听并最多等待后台任务 1 秒，正常游戏更新不等待任务。原有单人暂停绕过只在有效控制时启用，控制结束后恢复后台休眠。

游戏线程完全卡住时，任何清理都无法立即推进模拟；租约将在恢复更新时检查，过期请求不会重新激活移动。连接必须在 B 加入世界后建立，菜单或死亡时会被主动断开。P 中断后也需显式重新运行脚本。

### 本轮验证

- tModLoader 内置编译、打包成功，**0 错误、0 警告**；产物在 `.local/m13-build`，已复制到 B 配置且 SHA-256 一致。未改动存档、角色或按键配置。
- C# 离线测试直接编译 `ControlState.cs` 和 `TcpControlServer.cs`：检查主线程处理前不启用、租约与续期、积压过期、重复 ID、手动优先级、无效玩家、旧会话隔离，以及实际 TCP 分包/粘包、断线与监听端口释放。测试中的更新由测试程序调用，不包含 Terraria。
- Python 4 项标准库测试覆盖分段握手、续期与 stop、手动模式拒绝后不发送 stop、CLI stop、非法时长。运行命令见 README。测试仅绑定 localhost 临时端口；受沙箱网络限制时需允许本地套接字测试。
- Review 修正了 Python 请求被手动模式拒绝后仍发送 stop 的清理问题，避免误关手动控制。

| 游戏验收项（0.1.4） | 状态 |
| --- | --- |
| Python 连接 B 并收到握手/回执 | 用户终端输出确认连接、750ms 握手信息及 applied 回执 |
| Python 触发 B 向右移动 | 用户确认在真实双客户端联机中运行 move_right --seconds 5 后，B 的真实玩家开始右移；通过 |
| 5 秒后停止、显式 stop 后停止 | 未明确实测，不标为通过 |
| Ctrl+C 后停止 | 终端输出含 stop: applied, mode=none 及中断清理信息；仅确认回执，不标记游戏内停止通过 |
| A 观察 B 移动、停止，A 操作正常 | 本轮未单独验证；M1.2 的 P 控制已有实测依据 |
| 强制结束 Python 进程后停止 | 本轮未执行；Ctrl+C 不能代替异常退出测试 |
| 保持连接但停发指令，750 毫秒后停止 | 本轮未执行；离线租约检查通过 |
| P 中断外部控制、手动恢复及 10 秒上限 | 本轮未明确实测；历史 0.1.3 P 核心流程已通过，不能代替新版回归 |
| 死亡、退出重进、禁用/重载清理 | 本轮未执行；仅代码 Review 和离线状态检查 |

游戏由用户操作，Codex 未操作游戏界面。收尾以用户最新明确的范围为准：只确认 Python 触发 B 右移，不将此前笼统的停止反馈计为专项验收通过。本轮人工测试结束，其余项目保留为未验证，不作为本轮收尾阻断项。

### 收尾 Review

复核 IPC、租约、线程交接与清理逻辑，未发现明显阻断问题，未改动已编译的 C# 实现：

- 监听限定 loopback、单连接，消息及队列有界；网络任务不操作游戏对象，游戏更新不等待网络回执。
- 租约按接收时间计算，旧指令不因积压获得新租约；断线撤销纯控制状态，旧会话不能清理新会话。
- 玩家失效和世界/Mod 生命周期入口取消会话、清除状态；游戏线程恢复休眠值，卸载移除更新钩子并停止监听。相关游戏场景仍按上表保留未验证。
- P 先撤销外部会话；Python 在手动模式拒绝移动时不追加 stop，避免误关手动模式。

保留此前内置构建 0 错误、0 警告及测试包哈希一致的证据。本次收尾重新运行 C# 实际 IPC 源码测试及 Python 4 项测试，均通过；未重复构建未改动的 C#，未新增游戏测试。离线套接字测试在沙箱外获准运行，仅使用 localhost 临时端口。上述回归不替代游戏内停止、失联保护或手动恢复的验证。

## M1.4：游戏状态读取（0.1.5）

2026-10-19 实现；用户已确认提交 M1.4。只新增底层读取，没有接入 LLM 或 Agent 工具系统。构建和单客户端人工验收命令见 [README](../README.md#m14读取本地玩家状态)。

### 请求、响应与数据含义

复用 `TcpControlServer` 的 loopback TCP、JSON Lines、有界队列、单会话及异步回执，网络层无需新增 Terraria 依赖。握手、版本和递增 ID 规则不变，增加请求：

```json
{"v":1,"id":1,"cmd":"get_state"}
```

响应保留 `v/id/ok/status/mode`，成功时 `status:"state"`，增加 `state` 对象：

```json
{"v":1,"id":1,"ok":true,"status":"state","mode":"none","state":{"inWorld":true,"position":{"x":100.5,"y":200},"velocity":{"x":0,"y":0},"life":80,"lifeMax":100}}
```

示例数值仅示意。`position` 取本地 Player 的左上角世界像素坐标，X 向右、Y 向下；`velocity` 是原生每模拟 tick 的像素速度；`life` 取 `statLife`，`lifeMax` 取含装备/增益的 `statLifeMax2`。`inWorld` 要求客户端已进入世界且本地玩家 active，但不要求玩家存活或允许移动。菜单、断开的多人连接等返回 `inWorld:false`，其余四项为 JSON null，而不是旧世界缓存或假造零值。原移动响应不新增 `state` 字段。

### 线程、生命周期及失焦暂停

`CompanionSystem.ReadSnapshot()` 只在 `On_Main.DoUpdate` 的游戏线程执行，在原生更新之前复制为不含 Terraria 引用的不可变记录。`ControlState.Update()` 用当前快照完成排队请求，后台任务只序列化记录；不从网络线程读取 Player，不等待游戏/网络任务，也不读上次响应缓存。

查询不设置输入、控制模式或租约期限。原 750ms 外部租约、P 优先级、10 秒手动上限及暂停绕过条件不变。玩家从可控转为不可控时仍断开旧会话；进入/离开世界和卸载仍使用原清理入口。为允许菜单和死亡后的新查询，系统不再每帧断开新会话，玩家钩子也不再对已停止模式重复断线。移动请求在不可控时仍返回 `not_ready`；生命周期边界处的查询可能被中断，需显式重连。

已检查现有钩子顺序：队列处理位于 `orig DoUpdate` 调用之前，先于原有 IL 匹配的单人失焦暂停提前返回。查询不启用 `KeepSimulationRunning`，不调整 `InactiveSleepTime`，不改自动暂停设置；暂停时返回的快照可能与上次相同。此代码检查只说明游戏进入 DoUpdate 时能处理请求，不能证明最小化、失焦节流、自动暂停或主线程卡顿下总能及时调度。CLI 默认套接字超时 2 秒，可用 `--timeout 5` 调整；超时输出错误并退出 1，不返回空状态冒充成功，也不自动重试。

`python python/control.py get_state` 成功退出 0，stdout 是完整格式化 JSON，握手诊断写 stderr。连接失败、忙碌/断开、拒绝、非法响应、超时均退出 1。接口一次只允许一个连接；需要移动同时采样时必须复用同一个 `Bridge`，查询本身不发送 move 或 stop。连续采样占用的时间也计入移动租约。

### 本轮自动验证与待验收项

- tModLoader 内置 .NET 8 编译打包成功，0 错误、0 警告，产物 `.local/m14-build/Mods/CompanionBridge.tmod`。没有部署到 B 或更改角色、世界及配置。
- C# 离线测试编译实际 `ControlState.cs` / `TcpControlServer.cs`，通过原租约、会话和 TCP 回归，并新增：查询必须等待更新、菜单空状态、死亡快照、不续期移动、不打断手动模式、断线清理、真实 TCP 世界/菜单字段序列化、原移动响应格式兼容。
- Python 标准库 11 项测试通过，涵盖原移动流程和新增世界/菜单 JSON 输出、缺失快照、连接失败、请求超时、非法超时参数。假服务使用 localhost 临时端口。首次沙箱测试受套接字限制；获准在沙箱外运行后通过。C# 审批曾因超时未完成，按工具允许重试一次后通过。
- `git diff --check` 通过。离线更新由测试程序模拟，不包含 Terraria Player 或真实暂停行为。
- M1.4 基础状态读取已有用户真实游戏终端成功输出，详见后面的记录。菜单空状态、字段与画面核对、生命变化、死亡/重进、单人失焦、自动暂停、最小化及原移动/P/租约专项仍待验收。详细操作见 README；历史 M1.3 联机移动成功不能作为状态读取验收。

### 首轮用户超时反馈的排查

用户连续两次运行 get_state，仅出现通用超时，没有 Connected 行，尚未证明完成握手。读取最近 client.log：启动参数及 Saves Are Located At 为 `.local/tmodloader`，加载 CompanionBridge 0.1.2，记录 M1.1 focus experiment；未见 M1.4 TCP 加载行。`.local/m14-build` 已有新包，但此前未部署到该旧配置。这表明最近游戏启动使用了旧版；不能据此认定 M1.4 单人失焦处理失败，也不能仅凭旧版日志断定历史两次超时的确切网络原因。

已修正 Python 错误诊断，分别报告连接、握手和请求超时，并增加连接/握手超时测试；11 项 Python 测试覆盖三种阶段。README 补充沿用旧单人配置时的备份、复制及启动命令。未自动关闭游戏、覆盖运行中的包或操作存档，用户需保存退出后部署，再核对实际加载 0.1.5 并复测。当时真实游戏状态读取仍未验收通过；后续成功响应见下节。

### 用户确认真实世界状态读取成功

2026-10-19，用户在确认此前使用旧启动命令后，提供新的 CLI 完整成功输出。握手实例为 `d9b194534f5343a7aa2bcae0cacf2e9a`，`lease_ms=750`；响应为 `v:1,id:1,ok:true,status:"state",mode:"none"`，状态为 `inWorld:true`、位置 `(33630, 3750)`、速度 `(0, 0)`、生命 `100/100`。

该输出确认 Python → 实际 Bridge → 游戏线程快照 → JSON 回执的基础链路成功运行。它不单独证明字段与游戏画面一致、运动/生命变化正确，也未明确窗口焦点、自动暂停或最小化条件，因此不将这些专项标为通过。未新增 Codex 游戏界面操作或自动化游戏测试；本次仅更新验收记录，无需重复运行未改动代码的测试。用户随后确认提交 M1.4。

用户随后明确说明“走动时是这样的”，并提供第二份成功状态响应：`mode:"manual"`、`inWorld:true`，位置 `(34494.395, 3766)`，速度 `(3.5904074, 0)`，生命 `100/100`。相较第一份静止快照，位置已有变化、水平速度非零，确认真实运动期间的状态采样成功；回执中的控制模式为 manual。该证据不单独证明查询后的持续移动、完整 10 秒手动期限、750ms 外部租约或失焦/最小化条件，相关专项继续保留待验收。

### M1.4 提交前验证

用户确认提交后，重新运行 Python 11 项离线测试及 C# 实际 IPC 源码测试，均通过；`git diff --check` 通过。提交范围仅含 M1.4，DeepSeek/Agent 接入已撤回。沿用已有 tModLoader 构建 0 错误、0 警告的记录；本次未重复构建未改动的 C#，未新增游戏实测，专项待验收状态保持不变。

## 已知边界

- 没有新增第三方依赖，未修改磁盘上的 tModLoader 本体；运行时钩子使用 tModLoader 自带的 MonoMod.Cil。直接读取 Mono.Cecil 仅用于诊断。
- 本原型只覆盖水平输入，开启时手动左移会被覆盖；关闭后不干预任何手动输入。
- 聊天期间不触发快捷键切换，但未增加自动移动的聊天暂停策略。测试前先关闭自动模式再输入文字。
- 较晚注入输入可能影响混乱等原生状态语义；异常状态、坐骑和其他 Mod 没有进行兼容性验证。实验限定无异常状态的普通平地。
- IL 补丁针对当前稳定版的具体分支；升级后必须重新核对并运行测试。
- M1.2 双客户端加入、B 自动右移并在 A 画面中按时停止、A 操作不受影响已由用户确认；同步平滑性及清理仍未单独验证。M1.3 仅确认 Python 触发 B 右移，停止、失联保护和手动恢复等场景未明确实测，Agent 未实现；历史单客户端失焦移动不能代替新版单人回归结果。
- TCP 无鉴权，只用于可信本机。固定端口只支持一个 Bridge 实例，不验证角色名称；仅在 B 启用，连接前核对配置和日志。没有为其他本机进程设计访问权限管理。
