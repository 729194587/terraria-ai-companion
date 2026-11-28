# Agent 私人记忆

Server AI 内置 Python Agent 可以为当前世界中的 AI 角色维护私人笔记。笔记用于保存会影响后续行动的玩家信息、世界知识和过往经验。

## 记忆操作

模型通过 `memory` 工具管理笔记：

| 操作 | 用途 |
| --- | --- |
| `remember` | 创建或更新笔记 |
| `recall` | 列出笔记名称和摘要 |
| `recall(name=...)` | 读取指定笔记正文及来源 |
| `forget(name=...)` | 删除指定笔记 |

笔记名称是稳定更新键。同名写入会替换原笔记。正文与引语可以省略；引语用于保存玩家本轮实际说过的原话。

笔记内容由模型组织，来源记录由宿主提供。玩家原话可以证明某条信息曾被告知，模型观察或推测则作为角色自己的记录。后续行动仍可结合当前游戏观察更新判断。

## 身份与存储

笔记按 World UUID 和 AI Character ID 隔离，存储在：

```text
.local/agent-memory/<worldUid>/<characterId>/notes.sqlite3
```

身份来自已绑定 Server AI 角色的世界与角色信息。相同身份在同一宿主上复用笔记；复制世界并保留 UUID 的副本会共享该身份空间。迁移存档到另一台宿主时，可一并迁移对应的笔记目录。

## 容量与请求上下文

每个角色最多保存 32 条笔记。名称、摘要和正文长度上限分别为 48、120 和 1500 个字符。

内置 Agent 在首次看到非空目录及目录变化时，将名称与一行摘要写入对话历史。删除最后一条笔记会发出空目录。历史压缩时保留最新目录，需要正文时按名称调用 `recall`。

写入、替换和删除使用 SQLite 事务。外部 MCP 接管同一个服务器角色时复用同一笔记存储；客户端本地玩家控制路径目前不提供该功能。

## 验证

- `tests/test_agent_memory.py`：存储重开、世界/角色隔离、来源、容量、更新和删除。
- `tests/test_server_ai_agent.py`：通过聊天和工具入口写入玩家原话，再创建新 Agent 和连接验证召回。

真实模型是否会在长期游玩中主动记录有价值经验，仍需通过持续游戏任务观察。

实现入口：`python/agent_memory.py`、`python/agent.py`。

## 玩家体验参考（待实玩）

SillyTavern 的世界书可以由作者或玩家预写，分别绑定角色、玩家身份或单次聊天。普通条目可以等关键词出现再载入，条目共用上下文预算，也可设置冷却，避免同一段背景连续触发。它给本项目留下的设计问题是，同伴在什么场合想起某件事，玩家才会觉得自然。[World Info 官方文档](https://docs.sillytavern.app/usage/core-concepts/worldinfo/)

社区扩展 Memory Books 允许从手选或自动选中的一段对话生成可编辑的条目，保存共同经历、约定或未完的事。SillyTavern 自带的 Summarize 扩展则保留可修改、可回退的聊天摘要；官方指出模型概括时可能漏掉细节或写错。游戏中的物品转移、Boss 击败等结果可用专服记录核实，玩家给地点起的名字、为谁留了东西，更适合从当时的对话理解。[Memory Books 作者说明](https://github.com/aikohanasaki/SillyTavern-MemoryBooks) · [Summarize 官方文档](https://docs.sillytavern.app/extensions/summarize/)

Numen 在 `2cd1484` 用一份 Markdown 描述一种人设，同伴按 ID 绑定。经历由单独的笔记工具处理，条目可标为玩家习惯（`owner`）、世界见闻（`world`）或教训（`lesson`）。它的内置对话循环在开局、笔记修订或历史压缩后的下一次注入时附上索引；已有同版索引时跳过。这给将来处理重复上下文提供了一个可以独立对照的方案。[人设库](https://github.com/Dwinovo/minecraft-numen/blob/2cd1484530cc45a96efb52801461e131fa9623a3/numen-api/common/src/main/java/com/dwinovo/numen/api/persona/PersonaLibrary.java) · [笔记工具](https://github.com/Dwinovo/minecraft-numen/blob/2cd1484530cc45a96efb52801461e131fa9623a3/numen-api/common/src/main/java/com/dwinovo/numen/api/agent/tool/MemoryTool.java) · [索引注入](https://github.com/Dwinovo/minecraft-numen/blob/2cd1484530cc45a96efb52801461e131fa9623a3/numen-api/common/src/main/java/com/dwinovo/numen/api/agent/request/MemoryPreamble.java)

等同伴可以连续游玩后，留意三种具体时刻：玩家期待它记得，它却忘了；它在无关场合反复提起；它想起一件旧事，自然地改变了游戏里的行动。保留当时的玩家原话、现场和实际带入模型的笔记，再据此调整写入与召回。
