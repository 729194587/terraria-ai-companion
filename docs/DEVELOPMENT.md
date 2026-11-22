# 开发指南

本文介绍本地开发环境、Mod 构建和常用验证入口。模块接口与行为见 [文档索引](INDEX.md) 中的专题文档。

## 环境

- Terraria 通过 tModLoader 运行。
- 默认构建目标为 Terraria 1.4.4.9、tModLoader 2026.09.3.1 Stable 和 .NET 8。
- 兼容配置与角色迁移方式见 [引擎配置与角色迁移](TML_ENGINE_MIGRATION.md)。
- Python 依赖见 `python/requirements.txt`。
- 模型配置参考仓库根目录的 `.env.example`。

## Python 环境

在仓库根目录创建环境并安装依赖：

```powershell
python -m venv .local/lua-venv
& .local/lua-venv/Scripts/python.exe -m pip install -r python/requirements.txt
```

## 构建 Mod

```powershell
pwsh -NoProfile -File scripts/build-companion.ps1
```

默认构建产物位于：

```text
.local/companion-build-2026.09.3.1/Mods/CompanionBridge.tmod
```

可通过 `-EngineProfile` 选择引擎配置，通过 `-TModLoaderPath` 指定本机安装路径。构建脚本使用对应 tModLoader 安装中的构建工具。

## 测试

项目包含 Python 测试、C# 离线测试、IPC 测试和隔离 Dedicated Server 原生验证。根据修改模块选择相应入口。

Python 示例：

```powershell
& .local/lua-venv/Scripts/python.exe -B -m unittest discover -s tests -p 'test_server_ai_backend.py' -v
```

C# 与 IPC 示例：

```powershell
pwsh -NoProfile -File tests/run_csharp_tests.ps1 -NavigationOnly
pwsh -NoProfile -File tests/run_server_ai_ipc_tests.ps1
```

服务器原生工具验证：

```powershell
pwsh -NoProfile -File tests/run_server_ai_tool_tests.ps1
```

## 游戏验证环境

隔离服务器验证脚本通常会使用复制的世界、角色或测试 profile。具体场景参数见对应的 `tests/verify_*`、`tests/native_*` 脚本。

图形 Host & Play 环境可由以下脚本准备：

```powershell
pwsh -NoProfile -File scripts/prepare-companion-host-play.ps1 -PackagePath .local/companion-build-2026.09.3.1/Mods/CompanionBridge.tmod
```

启动 Agent 的入口：

```powershell
pwsh -NoProfile -File scripts/start-server-ai-agent.ps1
```

## 验证结果

项目使用不同层级的验证方式：

- **源码与静态检查**：代码结构、协议和引擎调用分析。
- **离线测试**：测试替身、纯逻辑、序列化和 IPC。
- **隔离原生服务器**：在指定 tModLoader 版本中验证真实游戏逻辑。
- **图形 Host & Play**：验证客户端显示、输入和多人同步。
- **真实模型任务**：验证模型决策、工具组合和持续游玩表现。

记录验证结果时，注明引擎版本、运行环境、场景和实际执行的测试入口。

## 本地开发产物

构建包、测试世界、profile、Trace 和运行日志通常保存在 `.local/`。模型凭据保存在本地环境配置中。
