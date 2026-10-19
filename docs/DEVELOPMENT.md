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
  CompanionBridge.cs              # 注册 P；维护唯一的自动模式开关
  Common/Players/CompanionPlayer.cs # 快捷键、本地玩家输入、进入世界/死亡清理
  Common/Systems/CompanionSystem.cs # 离开世界清理
  Localization/*.hjson             # 快捷键中英文名称
```

工程目录初始为空，没有覆盖已有文件。工作区没有 M0 报告文件，因此未从聊天重建 `docs/research/m0-feasibility.md`。

## API 与调用顺序

- `KeybindLoader.RegisterKeybind()` 注册快捷键；在 `ModPlayer.ProcessTriggers()` 中读取 `JustPressed`，避免按住时每帧反复切换。依据官方 [ExampleKeybindPlayer](https://github.com/tModLoader/tModLoader/blob/v2026.08.3.0/ExampleMod/Common/Players/ExampleKeybindPlayer.cs) 和 [KeybindSystem](https://github.com/tModLoader/tModLoader/blob/v2026.08.3.0/ExampleMod/Common/Systems/KeybindSystem.cs)。
- `ModPlayer.SetControls()` 是官方修改输入的钩子。本轮仅在自动模式开启且本地玩家存活、有效、处于单人世界时写入 `controlLeft=false`、`controlRight=true`。关闭时直接返回，不抹除硬件输入，不写位置、速度或其他动作输入。
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

`CopyInto` 位于普通游戏输入路径；聊天使用 `CopyIntoDuringChat`。上述检查确认 `SetControls` 在原生输入写入之后、水平运动之前，适合本轮前台实验。`PostUpdateRunSpeeds` 虽然紧邻 `HorizontalMovement`，本轮无需采用这个较晚的速度调整钩子；`PreUpdateMovement` 对注入本帧水平输入太晚。

这与 M0 建议的区别来自范围收缩：M0 考虑失焦运行，本轮明确不做。当前 DLL 中确实存在焦点分支，因此不声称 `SetControls` 能支持后台运行。IL 检查属于静态验证，不是游戏测试。

源码位置：[ModPlayer.cs](https://github.com/tModLoader/tModLoader/blob/v2026.08.3.0/patches/tModLoader/Terraria/ModLoader/ModPlayer.cs)、[Player.cs.patch](https://github.com/tModLoader/tModLoader/blob/v2026.08.3.0/patches/tModLoader/Terraria/Player.cs.patch)。本机同版本 `tModLoader.xml` 也描述了这些钩子的语义。

`side = Client` 在同版本 [BuildProperties.ReadBuildFile](https://github.com/tModLoader/tModLoader/blob/v2026.08.3.0/patches/tModLoader/Terraria/ModLoader/Core/BuildProperties.cs) 中按 `ModSide` 枚举解析；本机实际构建接受该配置。另按官方 TMOD 格式只读检查产物 `Info`：`side` 字节为 `1`，与本机 DLL 中的 `ModSide.Client = 1` 一致；打包版本为 `2026.8.3.0`。这验证了实际写入的配置，不代表已在游戏中加载，也不表示联机控制已经实现。

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

游戏调试时先只启用本 Mod。成功加载应有 `CompanionBridge M1.0 loaded; auto move is off.` 日志；每次手动切换有一条 `Auto move: on/off`，并在游戏中显示中文提示，不逐 tick 记日志。运行日志可通过游戏的“打开日志”入口查看；当前安装通常位于安装目录 `tModLoader-Logs/client.log`。

## 手动验收

本轮没有执行图形界面游戏操作。以下均为**待用户实测**，编译成功不代表这些行为已通过。

| 步骤 | 预期 | 本轮结果 |
| --- | --- | --- |
| 启用 Mod 并进入新单人世界 | 加载无异常，默认不移动 | 未执行 |
| 控制设置中检查快捷键 | 显示自动右移，默认 P；无冲突 | 未执行 |
| 安全普通平地松开方向键，按 P | 显示开启，持续向右，遇墙不穿过 | 未执行 |
| 再次按 P，保持方向键松开 | 显示关闭，原生减速至停止 | 未执行 |
| 关闭后按 A/D，跳跃 | 手动操作正常 | 未执行 |
| 按住 P 后松开 | 一次按下只切换一次 | 未执行 |
| 开启后保存退出、重新进入 | 默认关闭，不恢复旧模式 | 未执行 |
| 开启后死亡并复活 | 自动模式关闭 | 未执行 |
| 开启后退出并禁用/重载 Mod | 无继续注入或残留开关 | 未执行 |

测试停止时应松开 A/D，使用无坐骑、无外力、无异常状态的普通平地。手动按 D、冰面滑行、击退或下落不能直接作为停止失败的判据。若 P 无效，先检查 Mod 是否启用以及绑定冲突。

## 已知边界

- 没有新增第三方依赖，未修改 tModLoader 本体；Mono.Cecil 只用于本轮诊断，不是 Mod 依赖。
- 本原型只覆盖水平输入，开启时手动左移会被覆盖；关闭后不干预任何手动输入。
- 聊天期间不触发快捷键切换，但未增加自动移动的聊天暂停策略。测试前先关闭自动模式再输入文字。
- 原生限制、异常状态和其他 Mod 仍可能改变最终移动结果；没有进行兼容性验证。
- 第二客户端、联机、失焦、IPC 和 Agent 均未实现，也未测试。
