# Terraria AI Companion

**0.1.32 物品使用与挖掘恢复**：无目标 `use_item` 保留原生鼠标，有目标使用支持当前缩放；使用周期与效果确认分开报告。挖掘距离不足可在确认安全通道后导航接近、重新观察并最多重试一次。Python 251项、C#及原生坐标契约测试通过；构建0错误0警告。本版尚未部署或进行真实游戏验收，详见 [修复与验证记录](docs/ITEM_USE_AND_MINING_RECOVERY.md)。

**0.1.31 平台与快捷栏修复**：修正木平台已落地却报未完成；快捷栏按游戏按键 `1–9、0` 编号；新增 `drop_through_platform()` 原生 Down 下穿（先检查同X下方64px内安全落点，穿过当前层即松Down，实际落地停稳才完成）。最终包 `.local/platform-fixes-build/Mods/CompanionBridge.tmod`；不要使用早期单项修复目录的包。需加载后复测，详见 [平台与快捷栏测试](docs/PLATFORM_AND_HOTBAR_FIXES.md)。

**0.1.30 二维落脚导航入口**：新增 `navigate_to_landing_region(minPlayerLeftX,maxPlayerLeftX,surfaceY)`，Agent → TCP → 控制状态 → 实际玩家挂钩已接入现有二维规划/执行/真实状态反馈。新增目标一致性续租和跨语言协议测试。参数、测试包及真实游戏验收方式见 [二维落脚导航](docs/LANDING_REGION_NAVIGATION.md)。产物 `.local/landing-navigation-build/Mods/CompanionBridge.tmod`；未部署、未真实游戏验收。

**0.1.29 第一版原子操作**：Agent 已接入 6 个感知、8 个行动工具；复用现有 IPC、游戏线程、连续导航、控制代次/租约和输入释放证据。物品动作通过原生使用流程执行，结果按实际状态核验。交互、距离和兼容范围，以及测试与未实测边界见 [原子操作说明](docs/ATOMIC_OPERATIONS.md)。构建产物 `.local/atomic-build/Mods/CompanionBridge.tmod`；未部署、未真实游戏验收。

功能分支新增 **M1.9 / Mod 0.1.19 统一视口观察**：只读 `get_observation` 按需返回摄像机、完整视口 Tile、玩家背包／装备、NPC／玩家／掉落物／弹幕及环境，保留旧工具。离线测试和构建通过；产物 `.local/observation-build`，未部署、未真实游戏验收。接口、编码、测量及限制见 [统一观察说明](docs/OBSERVATION.md)。

**Mod 0.1.18 附近实体感知**：现有 `get_state` 在同次快照中返回其他玩家、NPC/生物，保留原始网格和 `spatial`；分类保守，显式标记未知和截断。历史包在 `.local/entity-build`，详见 [实体感知说明](docs/CHAT_AGENT.md#附近实体018)。

新增 **0.1.17 最小聊天 Agent**：远端真人聊天经 TCP 事件立即触发 Python LLM，模型可调用唯一的 `get_state` 工具读取真实状态和局部地形，再由游戏线程发送聊天回复。支持新消息取消旧请求与迟到结果拦截；未接入导航等动作工具。配置、协议、文件清单和双客户端验收步骤见 [聊天 Agent 说明](docs/CHAT_AGENT.md)。本轮仅构建到 `.local/agent-build`，未部署；以下 0.1.16 导航结论是历史实测，不代表新聊天功能已游戏验收。

**0.1.16 右向连续通行已通过本次游戏实测**：从障碍顶部跳下，连续跑跳越过后续障碍，最终到达并停稳。报告为 `arrived`，21 段动作、3 次跳跃；三次跳跃交接均保持非零水平速度，回执最大位置误差为零。该结论限于本次场地与右向任务，左向及中断专项仍待复测。

目标是让独立 tModLoader 客户端中的真实玩家由 AI 控制，未来再接入 Python Agent。**当前实验包为 `0.1.16`：移植 TerraBlind 物理状态搜索内核，在 C# 游戏线程中连续规划、执行和验证步行/跳跃，支持保速交接、障碍边缘支撑及受限下行。`navigate.py` 只提交目标、续租与收集反馈，原有实验入口保留，不含 LLM。**

本轮集成范围、上游调用链、限制和验收命令见 [TerraBlind 导航集成](docs/TERRABLIND_NAVIGATION.md)。构建输出使用 `.local/navigation-build`，不要把下方历史 `.local/m15-build` 包当作当前产物。完全退出 B、复制新包并重启后运行：

```powershell
python python/navigate.py --direction right --tiles 40 --seconds 40
```

工程原则、关键设计判断与验证边界见 [工程说明](docs/ENGINEERING.md)；环境、构建和实验记录见 [开发说明](docs/DEVELOPMENT.md)。

## 当前功能

- 在单人或多人客户端中按 **P** 切换本地玩家自动右移；可在“设置 → 控制 → Mod 控制”中重新绑定。
- 默认关闭。开启时覆盖水平输入，关闭时不再写入控制字段，由原生机制减速；手动右移仍会继续生效。
- 手动 P 模式最长 **10 秒**；外部模式使用 **750 毫秒租约**，Python 每 100 毫秒续发有效 `move_right`。`stop`、断线或停发后释放自动输入。
- P 优先：按下即断开外部控制；当前正在移动则停止，否则开启手动模式。外部 `move_right` 不抢占手动模式；`stop` 可停止任一模式。
- 通过 `PostUpdateRunSpeeds()` 注入水平输入，仅控制安装并启用 Bridge 的客户端本地玩家；服务器和远端玩家不执行控制逻辑。
- 仅自动模式有效时临时关闭后台休眠，关闭后恢复原值。单人模式沿用 M1.1 的失焦暂停处理；多人模式保留原生更新和暂停判断。
- 进入世界、离开世界、死亡和卸载时清除自动模式。不保存开关状态。
- `get_state` 在游戏线程读取本地玩家位置、速度和生命；菜单返回明确空状态，查询不激活自动移动。
- `get_terrain` 返回局部碰撞分类；`terrain_walk.py` 根据每次新观察前进、尝试低障碍跳跃或停止。
- `continuous_walk.py` 串联已有预测步行和跳跃，落地校验后重读状态/地形；运行时限、动作轮数和无进展检测共同限制任务。
- `navigate.py` 驱动 C# 连续导航任务：复用 TerraBlind 状态搜索与动作回溯，逐段校验落地并重新规划，支持左右水平目标和多障碍通行。
- 不修改位置或速度，不创建 NPC 或额外 Player。

**当前自动验证：0.1.16 编译打包 0 错误、0 警告；Python 104 项离线测试及 C# 地形/租约/IPC/预测运动/连续多障碍导航、保速步行、保速跑跳、障碍顶下行、实际坐标支撑检查、落地接触时序和浅坑通行回归通过。运动测试使用碰撞替身，支撑检查使用游戏同一份代码；游戏验收范围见本文开头。**

**M1.4 历史验证：0.1.5 编译打包 0 错误、0 警告；C# 实际 IPC 源码离线测试及 Python 11 项测试通过。用户已提供真实世界 get_state 成功输出；字段与游戏画面核对、单人失焦专项及移动回归仍待验收。**

**历史验证状态：v0.1.3 双客户端与 P 控制的核心流程已由用户实测通过。v0.1.4 编译打包为 0 错误、0 警告；实际 C# IPC 源码的租约/TCP 离线测试和 Python 的 4 项测试通过。M1.3 游戏实测仅确认 Python 触发 B 右移；5 秒后停止、断线/停发保护、P 键中断及手动恢复未明确实测，不标为通过。本轮人工测试已结束。**

## 构建和测试

当前安装目录为 `D:\steam\steamapps\common\tModLoader`。在 PowerShell 中执行：

```powershell
$tml = 'D:\steam\steamapps\common\tModLoader'
$repo = 'D:\Agent Project\terraria-ai-companion'
Push-Location $tml
try {
    & "$tml\dotnet\dotnet.exe" "$tml\tModLoader.dll" -server -build "$repo\CompanionBridge" -tmlsavedirectory "$repo\.local\m15-build"
} finally {
    Pop-Location
}
```

产物为 `.local/m15-build/Mods/CompanionBridge.tmod`。**完全退出 B 后**复制到原 B 配置，再重新启动（本轮未部署，不会自动覆盖正在使用的测试包）：

```powershell
Copy-Item "$repo\.local\m15-build\Mods\CompanionBridge.tmod" "$repo\.local\m12-b\Mods\CompanionBridge.tmod"
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
2. B 确认仅启用 CompanionBridge **0.1.7**，使用不同的 `BridgeTest` 角色，通过“多人游戏 → 通过 IP 加入”连接 **127.0.0.1:7777**，输入 A 设置的密码。
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
python -B -m unittest discover -s tests -v
```

C# 离线测试需 PowerShell 7.4+；只编译实际 IPC 源码，不启动 Terraria。测试会使用 localhost 临时端口。

## M1.4：读取本地玩家状态

只需一个启用 CompanionBridge **0.1.5** 的图形客户端，可沿用 B 配置进入单人测试世界；不需要 A 或联机服务器。Mod 加载后监听 `127.0.0.1:18777`，不需要按 P。

```powershell
python python/control.py get_state
python python/control.py get_state --timeout 5
```

成功时退出码为 0，stdout 仅输出 JSON（握手说明在 stderr），可用 `python python/control.py get_state > state.json` 保存。示例数值仅示意：

```json
{"v":1,"id":1,"ok":true,"status":"state","mode":"none","state":{"inWorld":true,"position":{"x":100.5,"y":200},"velocity":{"x":0,"y":0},"life":80,"lifeMax":100}}
```

请求为 `{"v":1,"id":1,"cmd":"get_state"}`，沿用同一连接和递增 ID。`inWorld` 表示已进入支持的客户端世界且本地玩家 active；死亡玩家仍可读取。`position` 是玩家左上角世界像素坐标（X 向右、Y 向下），`velocity` 是原生每模拟 tick 的像素速度，`life` 为 `statLife`，`lifeMax` 为包含加成的 `statLifeMax2`。快照在本次 `DoUpdate` 原生更新之前读取，不保证代表下一帧或远端状态。

菜单或尚未进入世界时成功返回 `inWorld:false`，`position`、`velocity`、`life`、`lifeMax` 全为 `null`，不返回上个世界的缓存。查询不发移动或 stop，不续期租约，也不修改 P 手动模式。进入/离开世界、死亡等生命周期切换可中断已有会话；重新运行命令查询。只支持一个连接，移动脚本占用期间另一个 CLI 可能被关闭；程序可在同一个 `Bridge` 连接中交替调用 `command("move_right")` 和 `get_state()`。

连接失败、忙碌/断开、协议错误或超时退出码为 1，错误在 stderr。默认套接字超时 2 秒，`--timeout` 必须有限且大于 0；超时后关闭连接，不自动重试或声称读到状态。

### 没有 Connected 就超时：先检查加载版本

CLI 会区分 `TCP connection timeout`（连接未建立）、`Handshake timeout`（连接后未收到 hello）和 `Request timeout`（握手完成后请求未收到回执）。只有第三种能进一步怀疑游戏更新暂停，不能把所有超时都归因于失焦。

注意启动时的 `-tmlsavedirectory`：`.local/tmodloader` 是旧 M1.1 配置，原包为 0.1.2；`.local/m12-b` 是后来的联机测试配置。构建 M1.4 不会自动更新任一配置。查看 `client.log` 的 `Saves Are Located At`、加载版本和 `CompanionBridge M1.4 loaded`，确认实际启动的是 0.1.5。

若沿用旧单人配置，**先保存并完全退出该游戏客户端**，再执行以下命令（仅更新 Mod 包，保留该配置的角色和世界）：

```powershell
Copy-Item '.local/tmodloader/Mods/CompanionBridge.tmod' '.local/tmodloader/Mods/CompanionBridge.pre-m14.tmod.bak'
Copy-Item '.local/m14-build/Mods/CompanionBridge.tmod' '.local/tmodloader/Mods/CompanionBridge.tmod'
Set-Location 'D:/steam/steamapps/common/tModLoader'
./start-tModLoader.bat -tmlsavedirectory 'D:/Agent Project/terraria-ai-companion/.local/tmodloader'
```

重启后先核对 0.1.5，再在另一个仓库终端运行 `python python/control.py get_state`。不要同时启动多个启用 Bridge 的客户端争用 18777。

### 单客户端人工验收（M1.4 基础读取已确认，其余待测）

2026-10-19，用户提供真实游戏终端输出：握手成功，`ok:true`、`status:"state"`、`mode:"none"`，`inWorld:true`，位置 `(33630, 3750)`，速度 `(0, 0)`，生命 `100/100`。这确认了一次真实世界状态请求成功；尚未提供字段与画面的核对、窗口焦点/暂停设置或最小化情况，不据此将相应专项标为通过。

随后用户在走动时提供第二份成功响应：`mode:"manual"`，位置 `(34494.395, 3766)`，速度 `(3.5904074, 0)`，生命 `100/100`。确认运动中能读到变化的位置及非零水平速度，且该回执中的模式仍为 manual；查询后的持续运动、完整手动期限和外部租约仍未单独确认。

1. 完全退出原 Bridge 客户端，按上面的构建/复制步骤部署 0.1.5，再只启动一个 Bridge 客户端。核对 Mod 版本，主菜单运行 `get_state`，预期 `inWorld:false` 和空字段。
2. 进入安全单人测试世界，不按 P，关闭自动暂停并关闭背包/设置/聊天；Alt+Tab 到终端运行 `get_state`。预期 `inWorld:true`、生命与游戏 HUD 一致；记录响应耗时及失焦时画面是否暂停。
3. 手动移动、跳跃或安全受伤后分别采样，核对位置/速度和生命变化。终端切换可能已经让角色减速，零速度本身不能判失败；需要运动中采样时可预先在终端启动带短延迟的查询，再切回游戏移动。
4. 保持自动移动关闭，分别测试普通失焦、自动暂停开启且背包打开、最小化窗口。记录即时返回还是超时。普通失焦的请求处理入口位于原生暂停提前返回前，但不绕过暂停、不关闭休眠；不能据此认定所有后台情形都会调度更新。若超时，切回游戏重试；不得把它当成 `inWorld:false`。
5. 在菜单、死亡/复活、退出重进后重新查询，确认无旧世界快照；切换过程中允许已有连接被关闭。退出游戏后运行查询，应报告连接失败。
6. 回归原移动命令、750ms 停发保护和 P 中断/10 秒上限；程序在同一会话移动期间查询应保留原租约期限，手动 P 模式下查询不应停止右移。

离线测试命令见上节。自动化测试使用纯 C# 快照和 localhost，不启动游戏；它们验证线程交接、字段序列化、无更新时等待、租约隔离和 CLI 错误处理，不能替代以上后台调度、真实 Player 字段及移动回归验收。

## M1.5：局部地形与规则运动（尚未游戏验收）

本轮从干净的 M1.4 提交 `2bc15f0` 开始；此前的 DeepSeek Agent 实验已撤回，本轮没有模型、API Key 或外部服务调用。只做单方向的普通步行/小障碍跳跃，不做寻路、跟随或战斗。

先按上面的步骤构建 0.1.7，并在**完全退出对应游戏客户端后**复制包。若继续使用你已验证的旧单人配置，在仓库终端执行：

```powershell
Copy-Item '.local/tmodloader/Mods/CompanionBridge.tmod' '.local/tmodloader/Mods/CompanionBridge.pre-m15.tmod.bak'
Copy-Item '.local/m15-build/Mods/CompanionBridge.tmod' '.local/tmodloader/Mods/CompanionBridge.tmod'
Set-Location 'D:/steam/steamapps/common/tModLoader'
./start-tModLoader.bat -tmlsavedirectory 'D:/Agent Project/terraria-ai-companion/.local/tmodloader'
```

若使用 B 配置则复制到 `.local/m12-b/Mods`，按前面的 B 命令启动。两者选一个，只运行一个启用 Bridge 的客户端，核对版本为 **0.1.7** 和日志 `CompanionBridge M1.5 loaded`。不需要重建角色或覆盖世界。

进入安全单人测试世界，在另一个仓库终端执行：

```powershell
python python/control.py get_terrain
python python/terrain_walk.py --direction right --seconds 20
# 或单独测试左行
python python/terrain_walk.py --direction left --seconds 10
```

运动脚本独占同一个 Bridge 会话；每轮先读最新地形，再发一次带方向/跳跃的输入，目标周期 100ms，默认运行最多 20 秒（CLI 上限 60 秒）。停止时打印原因，关闭连接，不自行重启。观察超过 250ms、两次 RPC 合计超过 250ms、失联或 P 中断均结束；失联类错误退出码为 1，正常期限和规则主动停止为 0。

局部窗口为 **45 列 × 29 行**，每格 16 世界像素，`originX/Y` 为左上角 Tile 坐标，`rows[y][x]` 是分类。快照同时包含原 `state`，以及玩家尺寸、脚下支持 `grounded` 和普通步行条件 `supported`。分类：

| 符号 | 含义及规则 |
| --- | --- |
| `.` | 空气、非实心装饰或已虚化方块，可通过 |
| `#` | 完整实心块，阻挡身体并可提供支撑 |
| `=` | 单向顶面（平台/家具顶帧；平台样式保留支撑），可支撑向下落地，不当作侧墙或头顶障碍 |
| `h` | 半砖，仅下 8 像素实心 |
| `1` / `2` | 两种地面坡，分别向右下降/向右上升；匹配相邻支撑面时可走 |
| `3` / `4` | 两种斜底方块，上表面平坦，底部三角形参与净空检查 |
| `a` / `b` | 两种倾斜平台楼梯，保留单向支撑，不视为侧墙/天花板 |
| `s` | 旧 0.1.6 的未分方向斜坡；拒绝猜测，提示更新到 0.1.7 |
| `!` | 液体或已标记的接触伤害/巨石类 Tile，停止 |
| `?` | 越界、联机未加载区或有自定义碰撞可能的 Mod Tile，停止 |

协议仍为 v1 JSON Lines。新请求示例：

```json
{"v":1,"id":1,"cmd":"get_terrain"}
{"v":1,"id":2,"cmd":"input","direction":1,"jump":true}
```

`get_terrain` 成功时 `status:"terrain"`，返回原 `state` 及上述 `terrain` 对象；未进入世界时拒绝为 `not_ready`，原 `get_state` 仍返回空状态。`input.direction` 必须是整数 -1/0/1，`jump` 必须为布尔值，两者都必填；成功回执与旧移动命令相同。新输入在租约内接管左右和跳跃，并禁止下落穿平台；`input(0,false)` 释放这些输入但保持租约会话，`stop` 则退出控制。查询不续租，旧 `move_right/stop/get_state` 格式和 P 手动模式不变。原 P/`move_right` 保留真人跳跃。

前方约 16 格用于预判；支撑面沿相邻坡面逐段更新参考高度，不再要求整段位于当前脚底高度附近。远处不可通行处记录为预警，进入停车范围后才停止。停车范围按当前速度、350ms 反应窗口（250ms 通信上限加 100ms 循环）、继续加速及减速余量计算，普通角色约 82–186px；加/减速度按 0.1px/tick² 估算，加速后速度不超过支持上限 4.1px/tick。此预算尚未经游戏制动距离验收，不适用于冰面、特殊装备或游戏线程停顿；750ms 租约仍是断发后的独立兜底，不是该反应窗口。

只允许相邻下降不超过 1 格；孤立障碍仍限制为高/宽不超过 2 格，并确认后方 4 格原高度平地；低台阶需确认 4 格同高顶面。连接平顺的地面坡/平台楼梯尝试直接走，陡侧低坡检查净空后尝试跳；接近到 40px 内起跳，按住 200ms。跳跃期间优先检查身体接触危险、当前动作走廊及已选落地区域，远处无关障碍不重新否定当前动作。每个障碍只尝试一次；400ms 未离地、1.8 秒未完成、提前落地或一秒未前进 4px 均停止。窄深坑跨越、不平整落点和连续台阶仍未实现。

停止原因区分 `terrain`（不可通行）、`immediate_danger`（危险/未知 Tile 或掉血）、`jump_failed` 和 `runtime`（手动中断、状态失效或通信问题）。异常/规则停止后，默认覆盖保存 `.local/terrain-walk-failure.json`，包含失败快照、决策前控制器状态和最近 8 次决策；先释放控制再写文件。可用 `--failure-log 路径` 指定文件。单次地形决策可离线复现：

```powershell
python python/terrain_walk.py --replay .local/terrain-walk-failure.json
```

复现不连接游戏；运行时中断只展示原因，不模拟 IPC、物理轨迹或游戏更新。

本轮离线验证：Python 68 项（控制 14、地形规则及循环 54）通过，C# 分类/租约/生命周期/真实 localhost TCP 回归通过，0.1.7 构建 0 错误、0 警告。修正仅涉及 Python，已有 0.1.7 游戏包可继续使用；连续坡面通行、实际跳跃及停车惯性尚未游戏验收。

### 简短单客户端人工验收

使用无坐骑、无钩爪、无速度/跳跃装备和异常状态的普通角色，关闭自动暂停和背包/聊天，先站稳；终端运行脚本即可，不按 P。每个场景单独运行，前后留至少 25 格普通平地，不把坑和障碍挤在同一测试区域。

1. **连续坡面**：锤出连续上升/下降至少 4 格的普通实心坡面，两端接平地，左右各跑 5 秒。观察累计升降超过 2 格后仍能行走；半砖过渡和平台碰撞仍需单独验收，不能由坡面通过推定。
2. **接近、跳跃与停止**：平地前方约 220px 放高墙，角色应先接近再停；另一次在近处加一格低障碍，确认先跳过，再在墙前停。用安全测试角色另将墙替换成近处深坑验证停车余量，先低速再普通跑速，随时用 P 中断；停止释放输入后仍有惯性。异常时保留终端输出及上述失败文件。

以上核心行为尚待逐项验收。首轮用户终端输出为 `walk` 后因无法确认落地区域而停止；停下后的只读快照显示前方平地接一段先升高 8 像素、再升高 16 像素的连续地面，旧规则将其误当作过宽障碍；现已区分有可站立顶面的低台阶，两次真实快照离线重放均允许继续走，尚不将其记为真实越障通过。Python 模拟地形、假时钟和 C# 分类/租约/TCP 测试已通过，但没有证明真实跳跃轨迹、边缘惯性、平台碰撞或多人同步。单客户端通过后，再按历史 A/B 流程让 Python 控制 B，观察 A 可见的跳跃和停止；这一步也未执行。

停止提示显示具体类别、Tile 坐标和水平距离。斜坡修复需要**重新构建并在退出游戏后部署 0.1.7**，不能只更新 Python；旧包仍会返回缺少朝向的 s。可沿原路线复测，观察 `walk slope` 或起跳后落地；低顶、深坑和不连续支撑仍应停止。四种坡形和一向平台分类按官方 [Tile 文档](https://docs.tmodloader.net/docs/stable/struct_tile.html) 与本机碰撞 IL 核对，游戏效果待测。

这是静态局部 Tile 实验，不识别敌人、机关触发、移动实体、弹射/传送等；坡向与支撑不连续、当前局部判定高度以外的连续长坡或未知形状仍会停止。当前地面支撑搜索相对脚底上方最多 32 像素、下方 16 像素，不是任意山坡的通行保证。断线/租约到期只释放输入，不能撤销已有速度或正在进行的跳跃；主线程卡住时恢复后才能处理租约。不要将离线安全分支当作游戏中永不落坑的保证。

## 预测运动实验（当前 0.1.9，基础双向越障已实测）

新增 `predicted_walk.py`：C# 使用改造自 [TerraBlind](https://github.com/Reisenbug/TerraBlind) 的干燥运动模拟核心和 Terraria 原生碰撞 API，先预测步行及制动；无法通过时枚举基础跳跃，确认带余量的落脚区，再逐帧执行并比较实际位置/速度。完成一次跳跃并稳定落地后结束；无安全动作、偏差过大、掉血或中断时停止，不自动重试。旧 `terrain_walk.py` 仍运行原 Python 规则，两种入口每次只运行一个。

构建使用上面的 tModLoader 命令，将 `-tmlsavedirectory` 改为 `$repo\.local\prediction-build`。**保存并完全退出测试客户端后**部署到当前单人配置（不覆盖角色/世界）：

```powershell
Set-Location 'D:\Agent Project\terraria-ai-companion'
Copy-Item '.local/tmodloader/Mods/CompanionBridge.tmod' ".local/tmodloader/Mods/CompanionBridge.pre-018-$(Get-Date -Format 'yyyyMMdd-HHmmss').tmod.bak"
Copy-Item '.local/prediction-build/Mods/CompanionBridge.tmod' '.local/tmodloader/Mods/CompanionBridge.tmod'
Set-Location 'D:\steam\steamapps\common\tModLoader'
.\start-tModLoader.bat -tmlsavedirectory 'D:\Agent Project\terraria-ai-companion\.local\tmodloader'
```

确认加载 **0.1.9**；进入普通单人世界，关闭自动暂停和自动跳跃，不按 P。初测角色需卸下功能装备、取消 Buff（包括向日葵附近的快乐），无坐骑/钩爪/额外跳跃，地面不用冰、蜂蜜块、沥青等特殊材料。另开终端：

```powershell
Set-Location 'D:\Agent Project\terraria-ai-companion'
python python/predicted_walk.py --direction right --seconds 10
```

Python 每约 100ms 用 `terrain_step` 续原 750ms 租约，用 `get_navigation` 读结果；不在 Python 中安排逐帧跳跃。P、Ctrl+C、断线及到期沿用撤销机制。运行后保存 `.local/predicted-walk.json`，含最近 20 次反馈、预计/实际稳定落点、逐帧累计最大位置误差及最后预计/实测位置和速度。这是有界诊断，不是完整物理回放。

动作按连接和控制代次隔离；租约到期、停止或切换控制源会使旧动作失效，即使同一游戏更新内又收到续租也不会恢复旧帧。Python 循环停顿超过 250ms 会结束，不自动恢复。

只需先验收两个场景：

1. 普通平地接一段低坡，左右各跑一次；检查持续行走和 P 中断。普通坡、半砖、平台通过原生碰撞函数处理，实际效果仍需验证。
2. 平地放 **1–2 格宽、2 格高**的土障碍，两侧留至少 20 格平地且上方净空至少 10 格。确认出现 `action:jump`，随后 `status:landed`，人物越过且停稳；一格障碍可能被原生跨步走过，不能用它证明跳跃。再将落脚区改为宽深坑或低顶，预期停止而非无限尝试。

基础参数为 60 更新/秒、20×42px 碰撞箱、起跳速度 5.01px/tick、重力 0.4px/tick²、持续跳跃计数 15、普通跑速参数 3、加速 0.08、地面减速 0.2。按住可维持起跳后 15 次更新；98.07px 是模型的无碰撞满按预测升高，**不是游戏实测或可越障高度保证**。具体兼容核对、MIT 来源及测试证据见 [DEVELOPMENT](docs/DEVELOPMENT.md#terrablind-局部预测运动018)。

本轮验证：Python **80 项通过**（IPC 15、旧规则 54、新入口 11）；C# 新旧协议、租约与控制代次、运动核心及左右低障碍闭环/偏差停止通过；0.1.8 构建 **0 错误、0 警告**，产物 `.local/prediction-build/Mods/CompanionBridge.tmod`。运动离线测试使用碰撞替身；原生碰撞行为、真实落点误差、P/失焦及多人同步仍待游戏验收。本轮未部署游戏包。

后续 0.1.9 已部署并收到真实游戏成功日志：先 walk，再以 holdFrames=12 起跳，58 次动作更新后报告 landed；预计与实际稳定位置均为 `(34823.582,4326)`，最大逐帧位置误差报告为 0，末速度两轴均为 0，CLI 正常结束。该日志确认这一场景的运动闭环；障碍尺寸与视觉越障未核对，其他地形、P/失焦和多人同步仍待专项验证。

随后用户按测试场景提供平地 5 秒到时退出、右跳和左跳日志，并明确确认两次均实际越过障碍且随后停止。右跳 hold=8、54 次更新后落在 `(33993.664,4342)`；左跳 hold=4、58 次更新后落在 `(33858.125,4342)`；两次预计/实际稳定落点一致、maxError=0、末速度为 0。此场景双向基础越障通过；一次跳跃后停稳退出为当前设计，尚未实现连续多障碍自主通过。

## 连续局部通行（0.1.10）

需先保存退出 Bridge 客户端，将 `.local/continuous-build/Mods/CompanionBridge.tmod` 复制到实际使用的配置目录 `Mods`，重启核对 **0.1.10**。本轮仅构建、未自动部署；原单次入口仍可独立运行。准备无功能装备、无 Buff 的普通角色（附近向日葵的快乐 Buff 也会被拒绝），关闭自动暂停、聊天和菜单，松开方向及跳跃键。

```powershell
python python/continuous_walk.py --direction right --seconds 20 --max-actions 8
```

默认每约 100ms 查询并续原 750ms 租约。C# 继续负责逐帧地形、预测和执行，Python 仅编排：开始一轮 → 步行/跳跃 → 实际停稳并报告 `landed` → 校验落点和速度 → 释放旧动作 → 重新读取状态、地形 → 开始下一轮。不重叠动作，不重试失败跳跃。平地仍使用同一轮持续步行，不每 100ms 停走。

`--seconds` 默认 20，最大 120；`--max-actions` 默认 8，最大 100，统计取得执行权的轮次（每轮持续步行，最多完成一次跳跃），不统计帧、预测候选或每次续租。`--max-actions 2` 会在第二轮跳跃停稳后结束；若没有第二个障碍则继续步行至时限。正常时限/轮数结束返回 `completed`，原生惯性仍由游戏消除。

默认 `--no-progress 3`：指定方向每取得至少 8px 的新最远进展才重置计时；反复左右晃动或换动作不会重置。危险/未知地形、无安全落点、预测偏差、落地校验失败、掉血、无进展、通信错误、P/Ctrl+C、租约/控制代次变化都会停止，不自动重连或恢复。查询与控制一轮超过 250ms 或循环间隔超过 250ms 也停止。

新握手能力 `guarded_control` 和可选 `expectedGeneration` 防止过期请求重启动作；连续入口拒绝旧包，旧脚本和协议继续兼容。报告保存为 `.local/continuous-walk.json`，含动作数、成功跳跃数、明确停止原因、最近 20 次状态/运动反馈及最后一份边界地形快照；终端记录每轮开始和动作状态变化。Ctrl+C 会尝试释放并关闭连接，输出中断信息。

人工验收只需两个场景（**本轮尚未游戏实测**）：

1. **平地**：至少 80 格长、2 格厚的连续土跑道，上方净空至少 10 格。从左侧起点执行 `python python/continuous_walk.py --direction right --seconds 5`，确认持续前进、无反复停走，最后以 `completed: runtime limit reached` 结束并减速停止。
2. **双障碍**：平地上放两个 **2 格宽、2 格高**的土障碍，中间至少 **20 格平地**，起点至第一处、第二处之后也各留至少 20 格平地，上方净空至少 10 格。执行 `python python/continuous_walk.py --direction right --seconds 20 --max-actions 2`。应两次出现 `landed`，第一次停稳后重新前进，越过第二处后正常停止，报告 `completedJumps: 2`、`action limit reached`。障碍需彼此分离，给当前执行器留下制动和重新起步空间。

## 范围和限制

原 P/`move_right` 模式只接管左右输入；M1.5 的 `input` 模式还接管跳跃并禁用向下穿平台，其他手动输入保留。墙壁、地形和原生状态效果仍可能阻止移动。测试使用无坐骑、无异常状态的安全平地；聊天时不会通过快捷键切换模式，但本原型没有聊天暂停策略。A 必须不安装/启用 Bridge；若误在 A 启用并按 P，A 的本地角色也会被控制。

## M1.1 人工实验

M1.1 已测结果对应 **0.1.2**。当前版本的单人/P 键回归可使用 B 配置进入单人测试世界，窗口化或无边框显示，准备至少能安全右移 10 秒的普通平地。

1. 手动确认新配置中的“自动暂停”关闭；不要打开暂停菜单、聊天或背包，也先不要最小化窗口。
2. 前台按 P 开启，确认右移，立即 Alt+Tab 到其他应用。
3. 让游戏窗口保持可见，观察约 **3 秒**：是否继续右移、是否整个画面暂停。
4. 在 10 秒总时限前切回游戏，按 P 关闭，松开 A/D，确认停止，再验证手动操作。
5. 另开一轮失焦后等待超过 10 秒再切回，确认已自动关闭、没有恢复旧的移动指令；此时单人游戏可能再次按原生机制失焦暂停，属于实验结束后的预期行为。

请反馈“失焦继续移动 / 停止移动 / 整个游戏暂停”，以及切回后 P 关闭和手动操作是否正常。失焦前的短暂惯性不算持续移动；若已超过 10 秒并停止，不要马上按 P（那会重新开启）。当前修复不会伪造 `Main.hasFocus` 或修改“自动暂停”配置，只在自动模式有效期间绕过指定失焦暂停分支。若游戏线程完全停止调度，时限只能在下一次更新时执行。

本轮未明确实测：M1.3 的 5 秒后停止、显式 stop 与 Ctrl+C 停止效果、断线/停发保护、P 中断与手动恢复、A 端观察与输入隔离、同步平滑性和生命周期清理。尚未实现：LLM、寻路、战斗、背包和装备操作。IPC 仅 loopback、无鉴权，应只在可信本机使用；端口被占用时 IPC 启动失败会记录日志，P 仍可使用。当前 IL 补丁依赖 tModLoader 版本，升级后需重新核对。
