# 服务器 AI 世界角色持久化

基于 e377d6d，正式 Server AI 模式自动启用。本轮不修改 Agent/Lua/场景、动作终态、unknown、
租约或输入释放机制，不部署现用 profile。

## 审计与实现

已有 G3.1 `CharacterStore/CharacterIo/CharacterFiles` 使用隔离的原生 `Player.SavePlayer/LoadPlayer`，
保存 `.plr/.tplr`，检查 ModSaveErrors、loadStatus/customDataFail、读回指纹与文件哈希；不可变代次保护旧文件。
原实现只有显式 save/load，没有自动世界绑定，ServerAiSystem 每次启动直接 Spawn 新角色。
现在复用同一原生保存/读回流程，仅增加世界提交指针、排他文件租约和一次性生命周期状态机。
旧手动档案的 current.json 格式、生命/魔力校验和保存失败隔离仍保留，既有原生回归通过。

实现：`ServerPlayerRuntime/WorldCharacterFiles.cs`、`WorldCharacterPersistence.cs`，接线在
`ServerAiSystem.cs`、`ProbeSession.cs`、`EngineHooks.cs`；`CharacterStore/CharacterIo/CharacterFiles`
只抽取共享提交/按清单打开入口及持久状态指纹。原生诊断入口 `WorldCharacterSelfTest.cs`
仅在显式参数和独立 profile 标记同时存在时注入测试物品，不是正式玩法或模型 API。

世界身份使用本机已核对的 `Main.ActiveWorldFileData.UniqueId`（Guid），不使用名称、槽位、随机运行会话或文件名。
characterId 为 `SHA256("CompanionBridge/world-ai/v1/" + uuid.ToString("N"))` 的前 32 个小写十六进制字符；
该身份算法命名空间保持稳定，未来格式升级不得悄悄改变绑定。空 UUID 明确拒绝。
不同 UUID 即使名称相同也隔离；同一文件改名/移动但 UUID 和 Main.SavePath 不变仍使用原角色。
复制世界而保留相同 UUID 被视为同一个逻辑世界；同一保存根同时启动两份会因排他租约拒绝第二个，
不是自动创建另一个角色。需要独立世界时必须有独立 UUID。

## 格式与提交

沿用 `Main.SavePath/VirtualPlayerEngineProbe/Characters/<characterId>/`，保留历史根名称，避免另建物品存档系统：

```text
world.lock                         # 整个角色会话持有 FileShare.None 租约
world.json                         # 唯一的世界提交指针，UTF-8，Version=1
<generation GUID>/character.plr    # 原生角色文件
<generation GUID>/character.tplr   # 原生 ModPlayer/物品数据
```

清单字段示意（占位值不是实测哈希）：

```json
{
  "Version": 1,
  "WorldUid": "<32位原生世界UUID>",
  "CharacterId": "<32位派生ID>",
  "EngineHash": "<64位当前引擎SHA256>",
  "SelectedItem": 6,
  "Native": {
    "Generation": "<32位代次GUID>",
    "PlrHash": "<SHA256>",
    "TplrHash": "<SHA256>",
    "StateHash": "<原生持久状态读回指纹>"
  }
}
```

SelectedItem 是内部快捷栏索引 0..9，不是游戏按键编号。原生完整 Player 格式负责背包、数量、前缀、
收藏、装备、染料、loadout、银行及原生支持的其他持久数据，不以 JSON 手填物品字段。
ItemIO.Save 仅用于读回指纹，验证所有 inventory/armor/dye/misc/loadout/银行物品及外观、名称、永久成长等。
世界模式的指纹排除瞬时当前生命/魔力；数据仍走原生保存，死亡不作为待执行动作恢复。
当前装备 loadout 索引纳入持久指纹，快捷栏选择随同清单提交；身份 marker 也随原生 ModPlayer 保存和校验。

原生写入新代次 → 检查错误 → 原生加载读回并比较 → 校验并刷盘双文件 → 写入并刷盘临时清单 →
首次 File.Move / 更新 File.Replace 原子发布 world.json。不会先更新某个角色指针、再单独更新快捷栏。
旧代次不覆盖、不自动清理；失败的暂存代次可保留用于诊断。
加载要求所有字段存在（包括值为 0 的 SelectedItem），限制清单 16 KiB，拒绝重复键、未知字段、
不支持版本/引擎、错误世界/角色、非法 ID/槽位、文件哈希或读回身份/指纹不符、原生加载失败。
目录与文件沿用私有根/重解析点保护。只有完全没有已存数据、仅有租约文件时才能认定新世界。
已有目录丢失指针、缺少双文件或损坏不会回退 starter，也不会自动尝试旧代次或覆盖损坏内容。

## 生命周期与失败

- OnWorldLoad 仍只建立运行会话，重复同世界回调不换会话；不同世界未经卸载直接切换会拒绝。
- 原有模拟线程 StartServer/Pump、保留槽和 Gate 下完成恢复/创建。新角色先提交初始档案，
  然后进入 Ready；已有档案不重新发放初始物品。Capture 同时检查世界、运行会话和 Actor 引用，
  未完成恢复/提交时不能绑定到可执行角色。初始化失败只尝试一次。
- 原有 pre-clearWorld/模拟作用域 finally → Finish 的退役路径保存角色。先执行原 ActorMining/
  ActorItemUse/ActorControl.Retire，保留它们原有取消/unknown 判定及输入释放，再保存，最后离开/释放 Actor。
  输入、速度、动画、租约、动作、任务、ActorGeneration 和网络绑定不从档案恢复；新运行会话保持原隔离。
- Loading/Ready/Saving/Saved/Failed 状态阻止重复加载、生成和退出提交，完成退役后才释放文件租约。
  保存失败不标记 Saved，不自动重试，不安全退役；活 Player 和旧提交保留，新动作门禁关闭，日志明确失败。
  损坏加载没有已发布 Actor，可安全释放空保留槽退出，原文件保留。

## 已验证

固定引擎仍为 Terraria 1.4.4.9 / tModLoader 2026.8.3.0，DLL 指纹
`FCC6A9624B12191BE4A15D714A3675440911FEB8C1D374642AB1FD686F0DA0A5`；未放宽原引擎/Mod 隔离检查。

```powershell
pwsh -NoProfile -File tests/run_world_character_tests.ps1
pwsh -NoProfile -File scripts/build-companion.ps1 -OutputPath .local/world-character-final-build
pwsh -NoProfile -File tests/verify_world_character_native.ps1 -PackagePath .local/world-character-final-build/Mods/CompanionBridge.tmod
pwsh -NoProfile -File tests/run_server_ai_ipc_tests.ps1 -PackagePath .local/world-character-final-build/Mods/CompanionBridge.tmod
pwsh -NoProfile -File experiments/virtual-player-engine-hooks/verify-native-persistence.ps1 -ModName CompanionBridge -PackagePath .local/world-character-final-build/Mods/CompanionBridge.tmod -WorldPath .local/companion-g82-host-20261027-175538-078/Worlds/test.wld -Port 27971
# tests/ 中运行四项身份/unknown/取消回归
../.local/lua-venv/Scripts/python.exe -B -m unittest test_server_ai_backend.BackendTests.test_read_identity_mismatch_fails_without_fallback test_server_ai_backend.BackendTests.test_missing_selection_evidence_preserves_unknown test_server_ai_backend.BackendTests.test_server_unknown_evidence_poisoning_uses_existing_lua_runtime test_server_ai_backend.BackendTests.test_workbench_explicit_cancel_preserves_unknown_release -v
```

- 离线：39 项实际存储/状态机检查通过，包括 UUID 隔离、排他租约、坏格式/版本/缺字段、双文件损坏/缺失、
  发布失败保留前提交、重复 load/quit 和失败不重试。合成原生字节只用于存储协议测试，不冒充物品原生序列化。
- 原生隔离：最终包在全新 profile、复制世界中连续运行五个无客户端服务器进程。
  1 首次角色，注入原生 Wood×173、带 Light 前缀的 IronPickaxe、IronHelmet、RedDye、收藏和 slot=6，经正常退出保存；
  2 自动恢复全部字段，重复实际 OnWorldLoad/Start 不改变引用或代次，原生死亡后退出；
  3 再次恢复存货和身份，角色为存活状态；死亡和动作上下文不作为持久任务恢复；
  4 将指针置只读并修改活背包为 Wood×999，保存明确失败，旧指针哈希未变，活 Actor/释放状态保留；
  5 仅在该私有测试副本改 Version=999，加载被拒绝、没有 Actor、更未写回新角色。
- 既有 G3.1 两进程原生回归通过：手动档案重载、损坏拒绝、原生保存异常未请求游戏退出等保持原行为。
- IPC/包检查通过，含 69 项 IL 检查（新增保存相对退役/离开顺序与恢复门禁）；4 项 Python 身份/unknown/取消回归通过。
- 最终包编译 0 错误、0 警告，SHA-256：`740C413ABA124184472A985397564EEFA650BD9E64D09FD1A40522E48114E5D9`。

本轮的原始运行日志和回执未作为长期仓库资产保留；需要复验时从当前源码与隔离脚本重新生成。本轮不运行无关全量测试、
真实模型或图形 Host & Play，不修改源世界；所有原生状态注入只在专用标记的独立测试 profile。

## 限制与未验证

仍遵守原引擎保留槽契约：同进程重新 arm 服务器不支持，Host & Play 重进使用新服务器进程。
没有部署或实测图形 Host & Play 自动关服、远端多人、第三方 Mod、自定义物品、SSC、网络文件系统、
全部 loadout/银行交互、断电或进程强杀。新建 classic；硬核档案拒绝，其他难度未扩展验收。
动作进行中退出的所有原生组合时序尚未逐一实测；保持原退役实现，并以 IL 顺序和既有 unknown/取消回归验证接线。
没有周期性角色 autosave，崩溃只能保留最近已提交代次；文件原子提交不等于与 .wld 跨文件事务，
回滚世界/复制相同 UUID 的世界需同步考虑角色档案。本轮不实现迁移、回滚、坏档恢复 UI 或历史代次清理。
旧手动 G3.1 档案没有可靠世界绑定，不能猜测采用；正式自动绑定从第一次成功启用开始。
更换 Main.SavePath/新建测试 profile 不会跨保存根自动抓取旧角色，需要显式迁移；同一世界重进应复用同一保存根。
原生角色数据可保存外观/成长，但安全初始化仍按旧机制在世界出生点附近放置 Actor，床/瞬时位置、动作与输入不恢复。
