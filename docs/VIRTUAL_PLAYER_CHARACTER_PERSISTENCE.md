# G3.1：独立 AI Player 角色存档实验

版本：VirtualPlayerEngineProbe **0.3.0**；分支 `codex/virtual-player-character-persistence`。
从指定提交 `2a5dd5939afc16e0561f08d4e083ccfd882e4bc4` 分叉，未修改 main、G1 / PR #11 或 G2 / PR #12 分支。PR 保持 Draft。

## 本机原生 API 核对

核对对象是已安装的 tModLoader **2026.8.3.0** / Terraria **1.4.4.9**，不是根据其他源码分支推断：

- `tModLoader.dll` SHA256：`FCC6A9624B12191BE4A15D714A3675440911FEB8C1D374642AB1FD686F0DA0A5`。
- hooks DLL SHA256：`595D26FB3BBE27548D030943B5ED2437F690D1C002129BAD21FAFEAEB8118F7A`。
- `new PlayerFileData(path, false)`；设置独立 `Player` 和 `FileMetadata.FromCurrentSettings(FileType.Player)`，不激活档案。
- 保存签名：`void Player.SavePlayer(PlayerFileData, bool skipMapSave)`。
- 加载签名：`PlayerFileData Player.LoadPlayer(string path, bool cloudSave)`。实际 Player 是返回值的 `.Player`。
- 本机 `SavePlayer` 的 `skipMapSave` 参数在 IL 中没有被读取；即使传 true，仍调用 achievement 保存和 `InternalSaveMap`。异常 catch 会显示文件错误；`!Main.gameMenu` 时调用 `WorldGen.SaveAndQuit(null)`，最后重新抛出。这不能直接用于独立 AI 保存。
- `FileUtilities.ProtectedInvoke(Action)` 同步执行，不把保存派发到工作线程。
- `InternalSavePlayerFile` 按传入的 fileData.Player 序列化 `.plr`，并调用原生 PlayerIO 保存 `.tplr`。原生双文件写入不是一个原子事务。
- `Player.ModSaveErrors` 是内部属性；本实验在固定 DLL 指纹下通过反射检查 `Dictionary<string,string>`，不存在或有错误均拒绝提交。
- 原生加载可能不抛异常，而是返回 `loadStatus != 0` 的回退 Player，或设置 `customDataFail`。三者都必须检查；不能把回退角色当成恢复成功。
- 原生加载的 `Main.CurrentPlayerOverride` 只替换临时当前角色上下文，并通过 Dispose 恢复；没有写入 `Main.player` 数组。未调用 `PlayerFileData.SetAsActive()`。

定向 verifier 会再次检查安装 DLL 的 API/IL 及真实 DMD/ILContext 兼容性。完整 IL 审计输出保留在本机 `.local/character-api-audit/`，不将反编译代码纳入仓库。

## 保存隔离与失败语义

保存前必须在服务器游戏线程持有 G1 Gate，通过保留槽位、原生连接 idle 状态、Actor 与 `Main.player[slot]` 对象引用检查。
`CharacterIo` 的线程局部作用域仅认可本次精确的 `PlayerFileData` 对象；相同路径或相同 Player 的另一份 fileData 不获得保存例外。

`PersistencePatches` 对本机 SavePlayer 的签名、全局副作用入口、catch 范围、原生保存调用与退出分支进行结构检查，全部通过后才修改 IL：

1. 仅独立 AI 保存跳过 achievement/map 全局保存，仍调用 `Player.SavePlayer(fileData, skipMapSave: true)` 的原生角色保存段。
2. 仅该调用发生异常时直接 rethrow，跳过 UI / `SaveAndQuit` 分支。
3. 作用域外的真人保存走原始代码。另有 `On_WorldGen.SaveAndQuit` 熔断，阻止同步角色 IO 请求退出世界并计数。

不匹配时回滚已安装 hooks、Ready=false，不能 spawn。没有宽泛吞异常或全局禁用真人保存。
每次 IO 前后核对 Main.myPlayer、Main.ActivePlayerFileData 和全部已发布 Player 引用；改变则 quarantine，保留预留。

目录布局：

```text
Main.SavePath/VirtualPlayerEngineProbe/Characters/
  <32位小写GUID角色ID>/
    current.json
    <32位GUID代次>/character.plr
    <32位GUID代次>/character.tplr
```

首次 save 自动生成角色 ID；参数仅接受严格的小写 32 位 GUID。路径始终在独立根目录中，拒绝目录/文件 reparse point，禁止使用真人 Players 目录或任意路径。
每次保存先做目录实际写入/flush 探测，再新建代次。原生文件写完且无 ModSaveErrors 后，使用原生 LoadPlayer 回读验证，再记录两文件 SHA256 和状态摘要；flush 新 manifest 后替换 current.json。
读取只接受已提交且双文件 hash 正确的代次，不扫描/收养残缺文件。旧代次和失败代次留存，不自动删除。不声称可以抵抗磁盘硬件故障或提供双文件系统级事务。

保存失败不改变 current.json、不销毁 live Player；后续 despawn/世界清理也拒绝销毁该对象，必须先成功保存。强制结束进程仍可能丢失未保存状态，本实验没有自动恢复 UI。
debug-style 启用时拒绝保存，必须再执行一次恢复覆盖层后保存；不能把临时测试装备写成角色数据。显式 `fixture` 则是用于往返检查的真实 AI 测试数据，不是默认初始化。

## 加载与生命周期

`load <id>` 要求当前无 Actor。先验证原始占位对象/连接状态，再在私有对象中原生加载和检查摘要。进入原 G1 Spawn 流程，仅恢复 whoAmI、出生位置、速度、控制位、出生点、死亡/重生计时和 fallStart 等世界运行状态；不重设背包、装备、染料、外观或生命/魔力。
构造/加载后的候选对象在发布前再次核对原始预留和占位引用。未知对象、连接过渡态或同步错误继续使用既有 quarantine/回滚规则；不能覆盖未知 Player。
成功后调用 G2 原生全量同步和既有加入消息。同步实现本身没有改写角色数据。

正常 `/vph despawn` 同步离开消息并恢复已知 idle 占位对象，**保留最初预留**，因此同一个世界可反复 load；不会动态抢回已释放槽位。世界正常退出时才释放该预留。每一轮重新设置 G2 同步/消息计数，避免前一轮离开标记阻止后续发布。
显式 `/vph spawn` 新建实验 Player，清除默认保存目标 ID，避免把新空角色覆盖到上一次档案。已有档案使用 `load <id>` 优先恢复。本轮不实现正式原版新角色物品生成规则或角色选择 UI。

指定基点早于 G2 的 `63f44eb` 锁顺序修复。G3 独立带入其必要部分：未预留槽位提前返回；服务器 `UpdateInMainThread` 先取得 Gate，再进入原生消息处理，避免 MessageBuffer/Gate 锁反转。未修改 G2 分支或网络序列化机制。

## 验证范围

没有运行全量测试。执行：

```powershell
./experiments/virtual-player-engine-hooks/build.ps1
./experiments/virtual-player-engine-hooks/verify.ps1
./experiments/virtual-player-engine-hooks/verify-native-persistence.ps1
```

| 层级 | 结果与范围 |
| --- | --- |
| 编译 | 0 errors / 0 warnings，0.3.0 `.tmod` |
| 定向离线 | 239 个既有/生命周期/包审计检查 + 26 个 G3.1 文件/API/IL 检查；包括真人保存成功及失败路径逐项等价、G2 不重置角色数据、重复生命周期、失败保存禁止销毁、路径与配对文件拒绝、真实 DMD 及畸形 IL 拒绝 |
| 隔离原生服务器持久化 | 两个独立进程；每进程两次保存/正常销毁/加载/再保存；第二进程读取第一进程档案；原生写入失败保留状态且禁止销毁；调试覆盖拒绝保存；损坏 `.plr` 回退对象拒绝；SaveAndQuit 请求计数为 0 |
| 单窗口 Host & Play G3.1 | **未执行，待用户实测** |
| 远程多人 G3.1 | **未执行** |

原生测试运行在新建 workspace 隔离 profile，只复制测试世界，不加载或写入真人角色档案。测试报告明确标注 hostAndPlay / remoteMultiplayer 未执行。
往返摘要涵盖全部 inventory/armor/dye/miscEquips/miscDyes/loadouts 的原生 ItemIO 标签、外观色彩、发型、生命/魔力与永久升级计数、本 ModPlayer 的持久 marker。测试用 37 个火把、铁头盔、红染料、非默认外观和生命/魔力；ModPlayer marker 必须跨对象及进程保持一致。
当前实验强制仅启用探针与 ModLoader，未验证任意第三方 ModPlayer/ModItem 的兼容性；不能外推为所有 Mod 数据实测通过。位置、速度和控制是世界状态，恢复时有意重建。生命/魔力在实际世界运行后可能随再生等游戏规则变化。

本次交付包 SHA256：`FEAEA1301476CE2D76CB7FCFBCA86097408A6C0FBB4E085E1DA8B75C5202C26D`。
最终构建的原生证据位于本机 `.local/character-native-validation-510eac644eca4e6ca4f41c8179076a71/process-1-result.json` 和 `process-2-result.json`：均为 `native-persistence-selftest-passed`、`roundTrips=2`、`corruptLoadRejected=true`、`saveFailureRetained=true`、`exitRequests=0`；第二份 `restoredFromPriorProcess=true`。离线输出为 `.local/g31-verification.txt`。

## 单窗口真实验证步骤

先退出旧 tModLoader / Host & Play，再运行：

```powershell
./experiments/virtual-player-engine-hooks/prepare-host-play.ps1
./.local/virtual-player-character-host/start-host-play.ps1
```

该入口与 G2 分开，首次复制已有 G2 测试角色/地图，后续保留同一份；更新包前备份整个 profile。只启用此探针，检查 7777 未被旧服务器占用。不要再使用 G2 入口来测 G3。
进入 Multiplayer → Host & Play，选择已有测试角色和世界。逐条输入并等待回复：

```text
/vph status
/vph spawn
/vph fixture
/vph inspect
/vph save
```

save 成功回复含 `id=<32位ID>`，请记下。应看到 AI 和显式 fixture 的外观；默认 spawn 本身没有测试头盔或赠送物品。继续：

```text
/vph despawn
/vph load <刚才的ID>
/vph inspect
/vph save
/vph despawn
/vph load <同一ID>
/vph inspect
```

应看到离开/加入消息和同一外观。服务器每次 save 会立即回读比较上述数据，不一致就报错而不提交。inspect 可比对物品、marker 和生命/魔力；不要把游戏中的自然再生误判成存档修改。
再 save、despawn 后正常退出世界和游戏，重新使用同一入口开同一世界，**直接 `/vph load <ID>`，不要先 spawn**，检查跨进程恢复。

故障与调试覆盖检查（仅这个隔离测试 profile）：

```text
/vph debug-style
/vph save
/vph debug-style
/vph save
/vph test-save-failure
/vph despawn
/vph save
/vph despawn
```

第一次 save 应拒绝覆盖层，恢复后应成功。故障注入在新建且未提交的私有代次中制造不可写 `.plr` 路径；不会破坏旧档案。随后 despawn 应拒绝，AI 留在世界，游戏不退出；最后成功 save 后才能正常 despawn/退出。任何 fault/quarantine 都停止该世界中的后续测试，不重复覆盖未知槽位。

构建位于 `.local/virtual-player-character-build/Mods/VirtualPlayerEngineProbe.tmod`；AI 档案位于测试 profile 下 `VirtualPlayerEngineProbe/Characters`，不打入 `.tmod`。
`verify-native-persistence.ps1 -WorldPath <已有测试.wld>` 可在另一台装有同版本 tML 的机器重跑隔离测试，需调整 `-TModLoaderPath`；不能将其结果标成图形客户端验证。
