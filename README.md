# Terraria AI Companion

目标是让独立 tModLoader 客户端中的真实玩家由 AI 控制，未来再接入 Python Agent。**M1.2 实验版 `0.1.3` 的核心流程已由用户确认：两个真实客户端联机，B 失焦后持续自动右移，A 能观察移动和按时停止，A 自身操作不受影响。**

## 当前功能

- 在单人或多人客户端中按 **P** 切换本地玩家自动右移；可在“设置 → 控制 → Mod 控制”中重新绑定。
- 默认关闭。开启时覆盖水平输入，关闭时不再写入控制字段，由原生机制减速；手动右移仍会继续生效。
- 每次开启最多持续 **10 秒**。通过 `PostUpdateRunSpeeds()` 注入水平输入，仅控制安装并启用 Bridge 的客户端本地玩家；服务器和远端玩家不执行控制逻辑。
- 仅自动模式有效时临时关闭后台休眠，关闭后恢复原值。单人模式沿用 M1.1 的失焦暂停处理；多人模式保留原生更新和暂停判断。
- 进入世界、离开世界、死亡和卸载时清除自动模式。不保存开关状态。
- 不修改位置或速度，不创建 NPC 或额外 Player。

**验证状态：v0.1.3 收尾重新编译、打包成功，0 错误、0 警告。用户确认两个独立客户端进入同一世界，B 按 P 自动右移，切回 A 后 B 继续移动，A 可观察到移动和按时停止且操作不受影响。日志确认 B 以 player=1、netMode=1 加入，约 10 秒执行自动关闭，另有一轮手动关闭记录。同步平滑性、B 手动恢复及生命周期清理仍待单独确认。**

## 构建和测试

当前安装目录为 `D:\steam\steamapps\common\tModLoader`。在 PowerShell 中执行：

```powershell
$tml = 'D:\steam\steamapps\common\tModLoader'
$repo = 'D:\Agent Project\terraria-ai-companion'
Push-Location $tml
try {
    & "$tml\dotnet\dotnet.exe" "$tml\tModLoader.dll" -server -build "$repo\CompanionBridge" -tmlsavedirectory "$repo\.local\m12-b"
} finally {
    Pop-Location
}
```

产物为 `.local/m12-b/Mods/CompanionBridge.tmod`。本轮已准备两份隔离配置：A 为 `.local/m12-a`（无 Bridge，Mod 启用列表为空），B 为 `.local/m12-b`（仅启用 Bridge）。旧 `.local/tmodloader` 的角色和世界保持原样；新配置请创建独立角色，不使用云存档。

## M1.2 双客户端测试

保持 Steam 运行。用**两个 PowerShell 窗口**分别执行以下命令，不从 Steam 库连续点击“开始”。两窗口共享同一份游戏安装，只隔离客户端配置与存档，不需要复制游戏文件。

客户端 A（真人）：

```powershell
Set-Location 'D:\steam\steamapps\common\tModLoader'
.\start-tModLoader.bat -tmlsavedirectory 'D:\Agent Project\terraria-ai-companion\.local\m12-a'
```

客户端 B（Bridge）：

```powershell
Set-Location 'D:\steam\steamapps\common\tModLoader'
.\start-tModLoader.bat -tmlsavedirectory 'D:\Agent Project\terraria-ai-companion\.local\m12-b'
```

1. 两端窗口化。A 确认未加载 Bridge，创建 `HumanTest` 角色和测试世界，选择“多人游戏 → 主机并开始”。使用端口 `7777`，为直接 IP 测试关闭 Steam 邀请功能（若界面提供该选项）。
2. B 确认仅启用 CompanionBridge **0.1.3**，创建不同的 `BridgeTest` 角色，通过“多人游戏 → 通过 IP 加入”连接 **127.0.0.1:7777**，输入 A 设置的密码。
3. B 在“设置 → 控制 → Mod 控制”中确认“切换自动向右移动”已绑定 **P**；若为空，手动绑定。“模组配置”没有配置选项是正常情况。
4. 两角色站在彼此可见的安全普通平地。B 按 P，立即切回 A；从 A 观察 B 是否持续右移，同时操作 A 的 A/D 和空格，确认 A 不受影响。
5. 约 10 秒后观察 B 是否自然减速停止，有无回弹、瞬移或持续走动。多人游戏此时应继续运行，不会恢复单人失焦暂停。
6. 另开一轮，在 10 秒前切回 B 按 P 提前关闭，松开方向键并检查手动操作；再测试退出重连后默认关闭。

以上游戏步骤尚未由 Codex 执行。若第二窗口无法启动或加入，先保留报错和日志反馈；不要替换游戏 DLL、添加未经核实的 `-nosteam` 参数或改成伪造 Player。更多证据、结果表及排查方法见 [开发说明](docs/DEVELOPMENT.md#m12双客户端联机实验)。

## 范围和限制

自动模式只接管左右输入，其他手动输入保留。墙壁、地形和原生状态效果仍可能阻止移动。测试使用无坐骑、无异常状态的安全平地；聊天时不会通过快捷键切换模式，但本原型没有聊天暂停策略。A 必须不安装/启用 Bridge；若误在 A 启用并按 P，A 的本地角色也会被控制。

## M1.1 人工实验

M1.1 已测结果对应 **0.1.2**。当前 0.1.3 单人回归可使用 B 配置进入单人测试世界，窗口化或无边框显示，准备至少能安全右移 10 秒的普通平地。

1. 手动确认新配置中的“自动暂停”关闭；不要打开暂停菜单、聊天或背包，也先不要最小化窗口。
2. 前台按 P 开启，确认右移，立即 Alt+Tab 到其他应用。
3. 让游戏窗口保持可见，观察约 **3 秒**：是否继续右移、是否整个画面暂停。
4. 在 10 秒总时限前切回游戏，按 P 关闭，松开 A/D，确认停止，再验证手动操作。
5. 另开一轮失焦后等待超过 10 秒再切回，确认已自动关闭、没有恢复旧的移动指令；此时单人游戏可能再次按原生机制失焦暂停，属于实验结束后的预期行为。

请反馈“失焦继续移动 / 停止移动 / 整个游戏暂停”，以及切回后 P 关闭和手动操作是否正常。失焦前的短暂惯性不算持续移动；若返回时已有超时提示，不要马上按 P（那会重新开启）。当前修复不会伪造 `Main.hasFocus` 或修改“自动暂停”配置，只在自动模式有效期间绕过指定失焦暂停分支。若游戏线程完全停止调度，时限只能在下一次更新时执行。

尚未验证：同步平滑性、B 手动恢复、重连/死亡/卸载清理、新版单人回归，以及更长时间或最小化窗口下的失焦运行。尚未实现：Python/IPC、LLM、寻路、战斗、背包和装备操作。当前 IL 补丁针对上述 tModLoader 版本，升级后需重新核对。
