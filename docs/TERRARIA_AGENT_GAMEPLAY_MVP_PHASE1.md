# Terraria Agent Gameplay MVP 第一阶段实现记录

本阶段依据 [Gameplay MVP v0.1 设计](TERRARIA_AGENT_GAMEPLAY_MVP_DESIGN_v0.1.md) 实现。Server AI 系统提示先区分聊天、只读查询和行动委托。明确委托且能安全推进时，本轮使用现有 Lua 工具完成观察、行动和核验，再按实际回执回复。Manifest 和 API Contract 纳入握手开放的功能装备和原版绳索接口。系统提示复用现有动态上下文。

## 注册与调用

在 [内置玩法攻略目录](../skills/) 增加受版本管理的 `<ID>/SKILL.md`。文件使用 UTF-8，`---` 围起的 YAML 头包含 `name`、`description`、`game_version`、`scope` 四个单行文本字段，随后是 Markdown 正文。ID 与目录名一致，匹配 `[a-z][a-z0-9_]{0,63}`。每篇最多 16 KiB，描述最多 160 字符，最多注册 32 篇。新增 Skill 或修改元数据后重启 Python Agent 刷新索引。

系统提示的 `available_skills` 仅注入 ID 和描述。`read_skill({"name":"mining_and_ores"})` 按需读取正文，返回 `valid`、`name`、`description`、`game_version`、`scope` 和 `content`。未知 ID 返回 `unknown_skill`，非法参数由 Agent 返回工具错误。模型输入只用于查表。加载一次占一次模型工具调用，Lua 的查询、动作与运行时间预算保持原值。Markdown 与示例均作为文本回到模型，实际游戏操作仍由现有 `execute_lua`、Lua Runtime 和 IPC 链路执行。

[挖矿与矿石攻略](../skills/mining_and_ores/SKILL.md) 覆盖局部识别、安全站位、单格挖掘、按能力接近，以及矿格移除和库存增量的分别核验。参考来源列于攻略末尾。攻略适用于现有 Server AI 接口，具体可达性、开路许可和挖掘效果取决于当前游戏状态与宿主回执。

## 验证范围

两组定向 Python 回归通过，共 73 个不同用例，覆盖注册与非法路径拒绝、Server AI 能力握手、装备回执、聊天入口、结构化挖掘、场景层和任务协作。聊天集成使用替代模型、真实 Lua Worker 与本地回环 Bridge。它验证正文按需返回、加载后 Lua 预算保持原值，并运行攻略中的两段 Lua 示例，分别核验指定格动作回执和库存增量。

运行组为 `test_gameplay_skills`、`test_server_ai_contract`、`test_server_ai_equipment` 和 `test_mine_target.MineTaskAgentTests`（17 个用例），以及 `test_mine_target`、`test_server_ai_agent`、`test_scene_layers`、`test_server_ai_scene` 和 `test_m3_program_tasks`（57 个用例，含前组的一项集成测试）。系统提示测量脚本和 `git diff --check` 通过。

第一阶段提供仓库内置攻略。外部攻略、全局寻矿、连续开采与拾取归因仍受现有能力限制。原生挖掘实机和真实模型的行为仍待验证。
