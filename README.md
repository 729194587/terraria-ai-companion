# Terraria AI Companion

目标是让独立 tModLoader 客户端中的真实玩家由 AI 控制，未来再接入 Python Agent。**当前 M1.3 实验版为 `0.1.4`：Python 标准库脚本通过本地 TCP 控制移动。编译、离线测试已通过，用户已在双客户端联机中确认 Python 能触发 B 的真实玩家向右移动。**

工程原则、关键设计判断与验证边界见 [工程说明](docs/ENGINEERING.md)；环境、构建和实验记录见 [开发说明](docs/DEVELOPMENT.md)。

## 当前功能

- 在单人或多人客户端中按 **P** 切换本地玩家自动右移；可在“设置 → 控制 → Mod 控制”中重新绑定。
- 默认关闭。开启时覆盖水平输入，关闭时不再写入控制字段，由原生机制减速；手动右移仍会继续生效。
- 手动 P 模式最长 **10 秒**；外部模式使用 **750 毫秒租约**，Python 每 100 毫秒续发有效 `move_right`。`stop`、断线或停发后释放自动输入。
- P 优先：按下即断开外部控制；当前正在移动则停止，否则开启手动模式。外部 `move_right` 不抢占手动模式；`stop` 可停止任一模式。
- 通过 `PostUpdateRunSpeeds()` 注入水平输入，仅控制安装并启用 Bridge 的客户端本地玩家；服务器和远端玩家不执行控制逻辑。
- 仅自动模式有效时临时关闭后台休眠，关闭后恢复原值。单人模式沿用 M1.1 的失焦暂停处理；多人模式保留原生更新和暂停判断。
- 进入世界、离开世界、死亡和卸载时清除自动模式。不保存开关状态。
- 不修改位置或速度，不创建 NPC 或额外 Player。

**验证状态：v0.1.3 双客户端与 P 控制的核心流程已由用户实测通过。v0.1.4 编译打包为 0 错误、0 警告；实际 C# IPC 源码的租约/TCP 离线测试和 Python 的 4 项测试通过。M1.3 游戏实测仅确认 Python 触发 B 右移；5 秒后停止、断线/停发保护、P 键中断及手动恢复未明确实测，不标为通过。本轮人工测试已结束。**

## 构建和测试

当前安装目录为 `D:\steam\steamapps\common\tModLoader`。在 PowerShell 中执行：

```powershell
$tml = 'D:\steam\steamapps\common\tModLoader'
$repo = 'D:\Agent Project\terraria-ai-companion'
Push-Location $tml
try {
    & "$tml\dotnet\dotnet.exe" "$tml\tModLoader.dll" -server -build "$repo\CompanionBridge" -tmlsavedirectory "$repo\.local\m13-build"
} finally {
    Pop-Location
}
```

产物为 `.local/m13-build/Mods/CompanionBridge.tmod`。**完全退出 B 后**复制到原 B 配置，再重新启动（本轮已完成复制并核对哈希）：

```powershell
Copy-Item "$repo\.local\m13-build\Mods\CompanionBridge.tmod" "$repo\.local\m12-b\Mods\CompanionBridge.tmod"
```

A 为 `.local/m12-a`（无 Bridge），B 为 `.local/m12-b`（仅启用 Bridge）。沿用测试角色和世界，不覆盖存档、不使用云存档。若首次从 Git 克隆，请先创建 A 的 `Mods/enabled.json` 为 `[]`，B 的为 `["CompanionBridge"]`；这些本地配置不提交到 Git。

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
2. B 确认仅启用 CompanionBridge **0.1.4**，使用不同的 `BridgeTest` 角色，通过“多人游戏 → 通过 IP 加入”连接 **127.0.0.1:7777**，输入 A 设置的密码。
3. B 在“设置 → 控制 → Mod 控制”中确认“切换自动向右移动”已绑定 **P**；若为空，手动绑定。“模组配置”没有配置选项是正常情况。
4. 两角色站在彼此可见的安全普通平地。B 按 P，立即切回 A；从 A 观察 B 是否持续右移，同时操作 A 的 A/D 和空格，确认 A 不受影响。
5. 约 10 秒后观察 B 是否自然减速停止，有无回弹、瞬移或持续走动。多人游戏此时应继续运行，不会恢复单人失焦暂停。
6. 另开一轮，在 10 秒前切回 B 按 P 提前关闭，松开方向键并检查手动操作；再测试退出重连后默认关闭。

以上 P 操作可作为新版回归；测试 Python 时不要先按 P 开启手动模式。双客户端配置与历史结果见 [开发说明](docs/DEVELOPMENT.md#m12双客户端联机实验)。

## M1.3 Python 控制

仅 B 加载 Bridge；A 不加载。B 进入世界且手动模式关闭后，在项目根目录执行（Python 3.10+，本机为 3.11.8，无需 pip）：

```powershell
Set-Location 'D:\Agent Project\terraria-ai-companion'
python python/control.py move_right --seconds 5
```

脚本连接 `127.0.0.1:18777`，打印实例 ID，持续续期 5 秒后发送 `stop` 并断开。`applied` 表示游戏更新线程已处理指令，不等于主客户端已经观察到位移。可用 `--seconds 20` 做断线测试，单次脚本最长 60 秒。

```powershell
python python/control.py stop
```

`stop` 可停止手动模式；同时只允许一个 TCP 控制连接，第二连接会被关闭，不能用另一个脚本抢占正在运行的控制脚本。正常移动脚本会自行发送 stop，Ctrl+C 也会尝试 stop；异常退出由断线/租约保护。P 会断开外部连接，脚本报错退出且不自动重连。

协议为 UTF-8 JSON Lines，每条以换行结束，最多 1024 字节：

```json
{"v":1,"id":1,"cmd":"move_right"}
{"v":1,"id":2,"cmd":"stop"}
```

每次连接 ID 从正整数开始递增。握手包含协议版本、实例 ID 和 `lease_ms=750`；回执示例为 `{"v":1,"id":1,"ok":true,"status":"applied","mode":"external"}`。租约从完整消息的接收时间计算，积压、非法消息及重复 ID 不续期。离开世界、死亡或 Mod 卸载会停止控制并断开会话；重新连接从停止状态开始。

### 人工验收记录与复测方法

按用户收尾时明确的验收范围：在真实双客户端联机中运行 `python python/control.py move_right --seconds 5` 后，B 的真实玩家开始向右移动。终端还曾输出 `stop: applied, mode=none` 和 Ctrl+C 清理信息，但回执不等于角色已停止，5 秒后停止及 Ctrl+C 后的游戏表现均不标为通过。其余项目本轮不再执行，以下步骤仅供以后复测，不作为当前阻断项。

1. A/B 加入同一世界，B 不按 P，运行上面的 5 秒脚本；在 A 观察 B 移动并在 stop 后减速停止。
2. 运行 20 秒脚本，中途按 Ctrl+C，检查停止；再单独结束该 Python 进程，检查断线后停止。Ctrl+C 是正常清理，不能代替异常退出测试。
3. 停发但保持连接的租约测试：`python -c "import sys,time; sys.path.insert(0,'python'); from control import Bridge; b=Bridge(); print(b.command('move_right')); time.sleep(3); b.close()"`。应在 750 毫秒租约后释放输入，而非等 3 秒断线。
4. 外部移动时在 B 按 P，应停止并断开 Python；再次按 P 验证原手动右移、再次关闭及 10 秒上限。手动模式开启时运行移动脚本，应收到 `manual_active`，不抢占控制。
5. 外部控制中退出世界或死亡，确认脚本断开，重进/复活后不恢复旧指令。确认 A 操作不受影响。

计时到期需游戏更新线程执行，线程卡住时只能在恢复后的更新中清理；停止意味着释放自动输入，平地惯性或真人持续按 D 不属于租约失效。

离线测试：

```powershell
pwsh -NoProfile -File tests/run_csharp_tests.ps1
python -m unittest discover -s tests -p test_control.py -v
```

C# 离线测试需 PowerShell 7.4+；只编译实际 IPC 源码，不启动 Terraria。测试会使用 localhost 临时端口。

## 范围和限制

自动模式只接管左右输入，其他手动输入保留。墙壁、地形和原生状态效果仍可能阻止移动。测试使用无坐骑、无异常状态的安全平地；聊天时不会通过快捷键切换模式，但本原型没有聊天暂停策略。A 必须不安装/启用 Bridge；若误在 A 启用并按 P，A 的本地角色也会被控制。

## M1.1 人工实验

M1.1 已测结果对应 **0.1.2**。当前版本的单人/P 键回归可使用 B 配置进入单人测试世界，窗口化或无边框显示，准备至少能安全右移 10 秒的普通平地。

1. 手动确认新配置中的“自动暂停”关闭；不要打开暂停菜单、聊天或背包，也先不要最小化窗口。
2. 前台按 P 开启，确认右移，立即 Alt+Tab 到其他应用。
3. 让游戏窗口保持可见，观察约 **3 秒**：是否继续右移、是否整个画面暂停。
4. 在 10 秒总时限前切回游戏，按 P 关闭，松开 A/D，确认停止，再验证手动操作。
5. 另开一轮失焦后等待超过 10 秒再切回，确认已自动关闭、没有恢复旧的移动指令；此时单人游戏可能再次按原生机制失焦暂停，属于实验结束后的预期行为。

请反馈“失焦继续移动 / 停止移动 / 整个游戏暂停”，以及切回后 P 关闭和手动操作是否正常。失焦前的短暂惯性不算持续移动；若已超过 10 秒并停止，不要马上按 P（那会重新开启）。当前修复不会伪造 `Main.hasFocus` 或修改“自动暂停”配置，只在自动模式有效期间绕过指定失焦暂停分支。若游戏线程完全停止调度，时限只能在下一次更新时执行。

本轮未明确实测：M1.3 的 5 秒后停止、显式 stop 与 Ctrl+C 停止效果、断线/停发保护、P 中断与手动恢复、A 端观察与输入隔离、同步平滑性和生命周期清理。尚未实现：LLM、寻路、战斗、背包和装备操作。IPC 仅 loopback、无鉴权，应只在可信本机使用；端口被占用时 IPC 启动失败会记录日志，P 仍可使用。当前 IL 补丁依赖 tModLoader 版本，升级后需重新核对。
