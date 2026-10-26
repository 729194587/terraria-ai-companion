# 修复多人世界中 AI 物品归属导致的真人拾取延迟

独立分支 `codex/fix-human-pickup-delay`，以已验收 G6 / PR #16 的 `ee6c349d67e841bb8984a055a6bb6264e7c77c95` 为基础，版本 **0.6.1**。在独立 worktree 开发；不切换或修改另一个对话的 G7 分支，不覆盖其构建/测试 profile。

## 现象与核实

用户反馈：原版多人没有问题，使用本模组的较早版本就偶尔发生，AI 已进入世界；真人走到怪物掉落物上有时不能立即拾取，需要来回走一次。大多数拾取正常。这不是 G5/G6 新增背包容量或消耗规则的特有现象。

本机固定 tML 2026.8.3.0 / Terraria 1.4.4.9 原生 IL 证实一个与无客户端 Player 有关的归属缺口：

1. `Item.FindOwner(index)` 通过原生 ItemSpace、CanPickup、CanPullItem、距离和拾取范围，选择物品归属玩家。AI 是 active 的原生 Player，也会被选择；这发生在实际接触拾取之前。
2. `Main.UpdateClient()` 只为当前客户端所拥有的物品调用 FindOwner。AI 没有自己的客户端来承担这一步。
3. `Main.UpdateServer()` 的 `0x0121..0x0141` 通常只为未归属（255）或原玩家 inactive 的物品重新选择归属。AI 仍 active，因此服务器也跳过。
4. 即使主动调用原生 FindOwner 选出了新的真人玩家，其 `0x017c..0x01b0` 的同步条件仍假定原持有者的客户端负责通知；原持有者是 active AI 时，服务器不发原生 **21 SyncItem / 22 ItemOwner**。

这使“原先归 AI，之后真人更适合拾取”的物品可能留在过时归属，或转交后客户端未及时收到消息。原先就归真人或未归属的路径通常正常，因此现象可以是偶发的。

隔离原生测试已分别复现以上两处缺口并验证修复。尚未在图形客户端重现用户现场，不能声称已经证明所有可能的拾取延迟都只有这一原因。

## 最小修复

`NativeItemOwnership` 安装两个严格校验的 IL 例外，继续使用原生完整选择与发包代码：

- `Main.UpdateServer`：仅对仍由本实验准确拥有的 AI 所持物品进入现有 FindOwner 分支。不是每帧强制改归真人，不绕过 keepTime、物品容量、CanPickup、距离等原生规则。
- `Item.FindOwner`：只在本次调用的**原持有者**是经过验证的 AI 时，允许原生归属改变分支发送 21/22；比较的仍是原生保存的旧归属，归属没变就不发包。

真人持有者、无 AI、客户端和未知 Player 对象仍执行原来的判断。没有直接扣除世界物品、直接给真人背包物品、清零拾取延迟、取消原生保留时间或伪造网络连接。

AI FindOwner 调用在游戏线程、生命周期 Gate 内，捕获 Player 引用、WorldSession、ActorGeneration、世界 Item 引用及索引。额外发包门槛在发送前再次验证这些引用、代次、当前槽位与完整 idle 连接状态；未知对象不获得例外。异常不回滚或重试部分原生转交，沿用 quarantine 并保留预留。

安装前校验两处完整计划的关键操作码、分支目标、原生字段/调用、局部变量入口、谓词签名与异常区；结构不符或重复安装时，在修改之前报错。沿用 G1 引擎/Hook 指纹和安装 rollback。没有复制第三方反编译实现。

## 已执行验证

- 构建 **0 errors / 0 warnings**。
- **23 项新增归属 IL 定向检查**：真实 DMD/ILContext、真人原操作码保留、无全局身份写入、重复安装拒绝、关键操作码变异安全拒绝。
- 复用 **30 项 G6 定向检查 + 180 项实验包/生命周期/G2 检查**，总计 **233 项定向检查通过**。没有运行仓库全量测试。
- 新 profile、复制地图、独立端口 27836 的原生服务器对照通过并正常退出：

| 场景 | 观测 |
|---|---|
| 未修补的原生 UpdateServer，物品旧归属为 active AI | 真人候选更近，旧归属仍为 AI，0 个转交包 |
| 未修补的原生 FindOwner 直接执行 | 选中真人候选，但 21/22 均为 0 |
| 修补后的原生 UpdateServer → FindOwner | 选中真人候选，恰好 1 个 21 和 1 个 22 |
| 原持有者是真人 | 保持原生客户端负责的路径，没有额外转交包 |
| 归属仍为 AI，多次调用 | 不重复发转交包 |
| 原生 keepTime 为 30 | 保留时间不被绕过 |
| AI 原生 GrabItems | 7 个石块原生入包，世界物品清空 |

对照测试从固定 DLL 生成未注入补丁的私有 DMD，以相同物品/玩家候选状态比较。诊断只在明确的 `-pickupownershipselftest` 参数下运行。无客户端的空服务器不会推进正常世界帧，因此由独立启动观察器在原生服务器启动后执行真实 API；它不修改 G7 共用的启动诊断分发代码。

“真人候选”是隔离 profile 中原本 inactive 的既有 Player，临时 active 用于原生归属算法，最终恢复；不是实际联网客户端，没有创建或激活 RemoteClient、没有修改 Main.myPlayer。原生 CheckSection 会清理这个无连接候选，所以每次观测前只恢复测试夹具的 active 状态；没有禁用原生 Main.UpdateServer 或其清理逻辑。通知计数来自实际 NetMessage.SendData 调用，背包结果来自原生 GrabItems。

**图形 Host & Play 复测：未执行。远程多人复测：未执行。** 用户此前确认的 G5/G6 基础功能通过记录，不等于本修复已通过真人拾取复测。

证据保存在本修复 worktree 的 `.local/ownership-offline-checks.txt`、`.local/ownership-native-checks.txt` 以及其中列出的原生诊断 profile/result JSON。

## 构建与复测

包：`.local/virtual-player-pickup-fix-build/Mods/VirtualPlayerEngineProbe.tmod`（相对于本修复 worktree）。

SHA256：`2B9E1982F3C20FEA6109588BE6F8B0C2749968781F78F4F8AAE0E3CA75579E02`。

`prepare-pickup-fix.ps1` 只创建新的独立 profile，复制指定来源的角色、地图、AI 档案和配置；目标已存在则拒绝，不覆盖 G5/G6/G7。修复构建以 G6 为基础，**不包含 G7 挖掘功能**。

已准备的固定入口位于主工作区 `.local/virtual-player-pickup-fix-host/start-host-play.ps1`。完全退出此前游戏和服务器后：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\Agent Project\terraria-ai-companion\.local\virtual-player-pickup-fix-host\start-host-play.ps1"
```

用已有角色、地图进入 Host & Play，执行 `/vph load`。让 AI 留在附近，击杀附近怪物，或让掉落物起初更靠近 AI，然后走近并穿过掉落物。重复几次，从 AI 更近到真人更近的场景尤其重要；物品应转交并正常拾取，不需要特意走回去。另检查真人附近普通掉落、AI 自身拾取仍正常。不需要新增调试命令或重新创建地图。

供 G7 集成：该修复只新增 ItemOwnershipPatches、NativeItemOwnership 及安装调用，不依赖 G7 控制代码。可将本修复提交 cherry-pick 到 G7 后重新编译和定向验证；本对话没有修改或给另一个对话发送指令。

重现命令（在本修复 worktree 中执行）：

```powershell
powershell -File experiments/virtual-player-engine-hooks/build.ps1
pwsh -File experiments/virtual-player-engine-hooks/verify.ps1 -ItemUseOnly
pwsh -File experiments/virtual-player-engine-hooks/verify-native-pickup-ownership.ps1 -WorldPath "D:/Agent Project/terraria-ai-companion/.local/virtual-player-control-host/Worlds/test.wld"
```
