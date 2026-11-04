# Server AI 世界角色持久化

正式 Server AI 模式为每个逻辑世界绑定一个独立 AI 角色。角色档案由世界 UUID 派生身份，不依赖世界名称、文件名或随机运行会话；同一保存根中的并发会话通过排他租约防止同时使用同一角色。

## 身份与存储

- 世界身份来自 Terraria 世界的稳定 UUID；UUID 缺失时拒绝启用，不回退到名称匹配。
- Character ID 由固定命名空间前缀与世界 UUID 的 SHA-256 派生，身份算法保持稳定。
- 原生角色数据使用 Terraria 的 Player 保存/加载机制，保留原生角色和 ModPlayer 数据；项目不自行重建一份物品 JSON 存档。
- 每次提交生成新的不可变角色代次，校验原生文件、读回状态指纹和文件哈希后，才原子替换世界提交指针。
- 世界指针同时绑定原生角色文件、快捷栏选择、引擎指纹和地图哈希；不能分别发布而产生混合代次。
- AI 地图作为角色档案的一部分持久化；地图缺失/损坏或哈希不符时不以空地图静默替代。

当前实现沿用历史目录命名空间 `VirtualPlayerEngineProbe/Characters/<characterId>/`。目录名称不代表它是独立实验运行时。

## 生命周期

1. 世界加载时获取排他租约并验证现有提交。
2. 有效档案通过原生 Player 加载/读回校验恢复；全新档案才创建新角色并提交初始状态。
3. 只有角色、世界、运行会话和 Actor 引用全部匹配后，Server AI 才进入可执行状态。
4. 角色退役时先按原有动作生命周期取消/收尾并释放输入，再提交角色与地图状态，最后释放租约。

初始化失败、已有档案损坏、提交指针缺失、身份/引擎不匹配或保存失败时均失败关闭。不会发放 starter 替代损坏档案、覆盖无法验证的旧提交或自动重试不确定的保存。

## 持久化边界

保存的是受支持的原生角色持久状态和已提交地图。瞬时位置、速度、输入、动画、控制租约、Action/Task 生命周期和待执行任务不会恢复；重启后不会续跑上次任务。地图与角色提交不构成跨 `.wld` 文件事务，也不提供世界回滚或复制 UUID 世界的自动迁移。

## 实现入口

- `CompanionBridge/ServerPlayerRuntime/WorldCharacterFiles.cs`
- `CompanionBridge/ServerPlayerRuntime/WorldCharacterPersistence.cs`
- `CompanionBridge/ServerPlayerRuntime/CharacterStore/`
- `CompanionBridge/ServerPlayerRuntime/AiMap.cs`、`AiMapRuntime.cs`
