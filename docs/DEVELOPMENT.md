# 开发指南

本文只记录可复用的环境准备、构建和验证方式。具体接口行为见 [文档索引](INDEX.md) 中对应专题；历史阶段记录不放在本页。

## 环境

- Terraria 通过 tModLoader 运行；当前项目构建配置见 `CompanionBridge/CompanionBridge.csproj` 与 `CompanionBridge/build.txt`。
- 默认目标为 tModLoader 2026.09.3.1 Stable、Terraria 1.4.4.9 和 .NET 8。2026.08.3.0 保留独立配置，用于旧版构建、复现和角色回滚。详见 [引擎配置与角色迁移](TML_ENGINE_MIGRATION.md)。
- Python 3.10+；依赖见 `python/requirements.txt`。Lua Runtime 的 Python 依赖应按锁定文件安装。
- 模型配置参考 `.env.example`。不要提交或输出 `.env`、API Key 或其他凭据。

## 初始化 Python 环境

仅在尚无可用环境时执行：

```powershell
python -m venv .local/lua-venv
& .local/lua-venv/Scripts/python.exe -m pip install -r python/requirements.txt
```

已有环境可直接复用。不要每个任务重复创建虚拟环境或重装依赖。

## 构建 Mod

仓库提供构建脚本：

```powershell
pwsh -NoProfile -File scripts/build-companion.ps1
```

构建产物位于：

```text
.local/companion-build-2026.09.3.1/Mods/CompanionBridge.tmod
```

脚本使用选定的本机 tModLoader 安装及其构建工具。`-EngineProfile` 选择版本，`-TModLoaderPath` 指定该版本的安装位置。构建产物按版本分开保存。

构建后先核对包版本、哈希和输出路径。不要覆盖正在运行的游戏包或用户存档；部署与启动游戏必须属于明确授权的任务。

## 定向验证

默认选择与改动直接相关的测试，不运行全量测试。先阅读目标测试脚本的参数和副作用。

常见入口：

```powershell
# Python：按改动选择测试文件
& .local/lua-venv/Scripts/python.exe -B -m unittest discover -s tests -p 'test_server_ai_backend.py' -v

# C#：选择相关子集
pwsh -NoProfile -File tests/run_csharp_tests.ps1 -NavigationOnly
pwsh -NoProfile -File tests/run_server_ai_ipc_tests.ps1

# 服务器原生工具或 IL 相关改动
pwsh -NoProfile -File tests/run_server_ai_tool_tests.ps1
```

以上是入口示例，不是每次都要执行的固定清单。根据本次改动选择最小充分验证；不重复运行代码未变化且已通过的测试。

## 验证等级

结果报告应区分：

1. **静态检查/源码检查**：确认结构、契约或调用关系。
2. **定向离线测试**：使用测试替身、IPC 或独立 harness 验证局部逻辑。
3. **隔离原生服务器测试**：使用真实 Terraria/tModLoader 代码和独立 profile 验证原生行为。
4. **图形 Host & Play**：验证图形客户端、多人场景和同步。
5. **真实模型端到端任务**：验证模型决策、工具使用和长期任务表现。

较低等级通过不能自动推出较高等级通过。记录实际执行的场景、版本和结果，不把未执行的验证写成通过。

## 本地临时文件

构建产物、测试 profile、临时日志、模型 Trace 和原始回执放在 `.local/` 等本地临时目录。默认不提交这些运行产物；不要把本机绝对路径、临时 profile 状态或一次性命令记录写入长期文档。
