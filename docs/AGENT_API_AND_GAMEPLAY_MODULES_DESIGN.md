# API Help 与 Gameplay Modules

Server AI 为模型提供两种互补能力：API Help 解释当前连接可用的游戏接口；Gameplay Modules 将常见多步骤流程封装为可复用 Lua 函数。Gameplay Skills 则提供 Terraria 玩法知识。

## 三层分工

| 机制 | 解决的问题 | 内容 |
| --- | --- | --- |
| API Help | 如何调用游戏接口 | 参数、返回 Schema、状态语义、示例和关联 API |
| Gameplay Modules | 如何组合多项操作 | 可复用 Lua 流程、输入参数和结果汇总 |
| Gameplay Skills | 游戏规则与策略是什么 | Terraria 机制、玩法流程、条件与策略建议 |

底层 Atomic API 提供具体游戏能力。Module 和 Skill 均通过当前连接开放的 API 工作。

## API Help

模型通过 `api("help")` 查看当前 API 索引，通过 `api("help", {name="..."})` 获取单项详情。

单项帮助由当前后端 Schema、返回值契约和文档元数据组合生成，包括参数、返回结构、示例、限制与相关接口。Help 按连接能力过滤，避免模型看到当前服务器不可用的操作。

主要实现：

- `python/atomic_operations.py`：参数 Schema 和操作描述。
- `python/api_result_contracts.py`：返回 JSON Schema。
- `python/api_documentation.py`：示例和补充说明。
- `python/server_ai_contract.py`：Help 生成与能力过滤。

## Gameplay Modules

内置模块由 `python/lua_modules.py` 注册，Lua 源文件位于 `python/lua/modules/`。注册信息包含名称、摘要、依赖 API、导出函数和签名。

Worker 启动时根据当前 Bridge 开放的操作筛选模块，将模块源代码加载到 `terraria` 命名空间。模块仍通过宿主注入的 `api` 函数执行底层调用，因此共享相同的参数校验、预算和结果契约。

当前内置模块：

| 模块 | Lua 入口 | 用途 |
| --- | --- | --- |
| NPC | `terraria.npc.talk_to` | 接近指定 NPC 并开启对话 |
| Crafting | `terraria.crafting.make` | 查询配方、逐批合成并汇总产出 |
| Building | `terraria.building.construct` | 调查 Blueprint、提交施工任务并复查结果 |

模块可用性由依赖 API 与对应能力旗标共同决定。

## 模块与 Skills 的配合

以建造为例：

- Skill 介绍 Terraria 建筑规则、房屋要求和设计建议。
- Module 提供可复用的施工流程。
- API Help 解释 `build_diff`、`build_place` 和任务查询接口。
- 游戏侧任务负责 Blueprint Survey、站点选择、材料和原生放置。

三者可以独立演进：玩法知识可以更新而不改变 API；Module 可以改进流程而不修改游戏执行器；API 扩展也不要求每个 Skill 复制接口定义。

## 扩展方式

新增 Module 时，在注册表中声明依赖与导出入口，在 `python/lua/modules/` 实现 Lua 函数，并为输入、API 调用顺序、部分成功和失败结果添加测试。

新增 Skill 时，将游戏知识放入独立 Skill 文件并通过 Skill Registry 按需加载。Skill 目录见 `skills/`，注册与加载逻辑见 `python/gameplay_skills.py`。

## 相关实现

- `python/lua_modules.py`
- `python/lua/modules/npc.lua`
- `python/lua/modules/crafting.lua`
- `python/lua/modules/building.lua`
- `python/gameplay_skills.py`
- `python/server_ai_contract.py`
- [Lua Agent](LUA_AGENT.md)
- [API 契约](API_CONTRACT.md)
