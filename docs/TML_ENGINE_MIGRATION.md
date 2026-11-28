# tModLoader 引擎配置与 AI 角色迁移

默认开发与验收目标为 **2026.09.3.1 Stable**。**2026.08.3.0** 保留为兼容配置。两者使用 Terraria 1.4.4.9 和 .NET 8，分别构建 CompanionBridge 包。[官方版本差异](https://github.com/tModLoader/tModLoader/compare/v2026.08.3.0...v2026.09.3.1)

## 选择安装与构建

从仓库根目录运行 PowerShell 7。

```powershell
pwsh -NoProfile -File scripts/build-companion.ps1
pwsh -NoProfile -File scripts/build-companion.ps1 -EngineProfile 2026.08.3.0 -TModLoaderPath 'D:/path/to/legacy-tModLoader'
pwsh -NoProfile -File scripts/prepare-companion-host-play.ps1 -EngineProfile 2026.09.3.1
```

[EngineProfiles.json](../CompanionBridge/EngineProfiles.json) 集中登记 Terraria 版本、规范化 tML 版本、官方提交和两份 DLL 的 SHA-256。默认安装路径可用 `-TModLoaderPath` 覆盖。IDE 直接加载项目时用 `COMPANION_TML_DIRECTORY` 指向同一个安装。包默认写入 `.local/companion-build-<版本>/Mods`。启动和原生验证脚本同时检查选定安装的 DLL 和包头版本。

专用服务器使用匹配的包和安装，启动参数包含 `-companionserverai -companionengineprofile 2026.09.3.1`。生成的 Host & Play 启动器通过 `COMPANION_ENGINE_PROFILE` 把相同选择传给子进程。已有启动器省略选择时可按实际 tML 版本匹配目录项，运行时仍检查 Terraria 版本、tML 版本、提交、引擎及挂钩 DLL 的完整 SHA-256。显式启用、专用服务器、单 Mod、主线程及首次监听前槽位预留门禁继续生效。

## 迁移和回滚

1. 正常关闭旧服，备份整个 profile，包含 `Worlds` 及 `VirtualPlayerEngineProbe/Characters`。若使用 Host & Play 的 `-SeedProfile`，准备完成后把已关闭旧 profile 的 `VirtualPlayerEngineProbe/Characters` 复制到新的 profile，保持原世界 UUID。
2. **新世界**直接用匹配的引擎和包启动。**已有 2026.08.3.0 角色的世界**用 2026.09.3.1 启动，并添加 `-companioncharactermigration upgrade`。生成的 Host & Play 启动器使用 `-CharacterMigration upgrade`。迁移仅接受已登记的旧引擎来源。
3. 服务持有世界角色排他锁，验证旧提交与文件哈希，在独立世代逐字节复制 `.plr`、`.tplr` 和已有的 `map.bin`。新版原生加载候选角色并核验状态指纹和 ModPlayer 标记，恢复清单里的快捷栏选择，同时解析地图并核验世界、角色及尺寸。候选和来源复核通过后，原子替换 `world.json`。迁移前指针保存在 `world-before-engine-upgrade.json`。没有地图的历史 V1 世代沿用原有的空地图初始化规则。
4. 验证失败或替换前中断时，旧指针和旧世代仍是有效来源。修复错误后可重试；未提交的候选目录不会被当作当前存档。替换后中断时，使用新版正常启动即可读取已发布世代。
5. **回滚**需关闭新版进程，在同一 profile 使用 2026.08.3.0 及本次迁移代码构建的旧版包，添加 `-companioncharactermigration rollback`。运行时在旧引擎复核升级前角色与地图，再原子恢复旧指针，另存被替换的新版指针。回滚返回最近一次升级前的角色和地图进度。若还需回滚地形、物品箱或世界其他变化，请同时恢复同一时点备份的 `.wld` 和 `.twld`。

迁移与回滚完成后，可移除相应启动参数。专用服务器日志中的 `world-character-engine-upgraded`、`world-character-engine-rollback` 和 `world-character-ready` 分别记录切换及就绪结果。

地图格式随角色正常保存自动升级。当前构建可读取 V1 至 V4 的 `map.bin`，保存时在新世代写入 V4，包含 Brotli 压缩的地图格子和实际访问位图。旧世代仍与原清单的哈希匹配。

## 定向验证入口

```powershell
pwsh -NoProfile -File tests/run_world_character_tests.ps1
pwsh -NoProfile -File tests/run_server_ai_tool_tests.ps1
pwsh -NoProfile -File tests/verify_world_character_native.ps1 -Migration -WorldPath '<固定世界.wld>'
pwsh -NoProfile -File tests/verify_server_ai_native.ps1 -TcpCapacity -WorldPath '<固定世界.wld>'
```

原生脚本使用复制世界和隔离 profile。`-LegacySeedPackagePath` 可指定迁移前的旧包来创建源角色，`-LegacyPackagePath` 指定支持回滚的旧引擎包。

## 2026.09.3.1 验证结果

- CompanionBridge 0.1.82 在两版引擎上构建均为 0 错误、0 警告。新版专用服务器通过精确指纹和实际挂钩安装，新 profile 可初始化角色和地图，IPC 可绑定真实 AI Player。新版 DLL 显式选择旧版配置时触发指纹门禁，AI IPC 未监听，原生游戏端口仍可监听。
- 迁移夹具用旧引擎和 0.1.81 生成源存档。故障注入损坏状态指纹后，新版原生读回拒绝启动，未发布角色且提交指针保持原样。还原夹具指针后依次通过升级、回滚和再次升级。夹具逐次检查物品堆叠、前缀、收藏标记、装备、ModPlayer 标记、快捷栏和地图。另用五次新版进程验证常规存取、死亡状态清理、写入失败保留提交及损坏清单拒绝。
- 两槽专用服务器在首次进入监听器前完成 AI 槽 1 预留。实测 TCP 分配顺序为 `0, -1, -1, 0`，并发的两个额外连接各收到原生断开包。首个连接断开后槽 0 复用，AI 绑定保持有效，关服释放游戏和 IPC 端口。保留 [tModLoader 的原生满员监听行为](https://github.com/tModLoader/tModLoader/commit/cd3ab192d024e24199f25c026255a53b33920399)。
- 新版原生夹具通过感知与场景语义、Tile/Collision 导航与落脚、物品使用、挖掘、背包、合成、放置及采集到合成的闭环。离线角色存储 45 项、地图 14 项及跨进程读回、IPC 和包内 90 项 IL 检查通过。
- TCP 容量夹具覆盖 Terraria 握手前的连接和槽位生命周期。图形 Host & Play、完成游戏握手的真人同步以及 Steam P2P 未验证。Host & Play 只验证了隔离启动器的生成。实际 Hook 安装失败后的回滚路径保留，本轮未在真实服务器注入安装失败。
