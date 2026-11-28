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
| Container | `terraria.container.find(contentKey, radiusTiles?, offset?)`、`put(target, contentKey, count)`、`take(target, contentKey, count)` | 先查附近各箱库存，选定箱子后按数量存取 |
| Inventory | `terraria.inventory.give(teammateRef, contentKey, count)` | 到指定队友身边，按数量短抛物品 |
| Move | `terraria.move.follow(teammateRef, options?)` | 步行跟随移动中的同队玩家，靠近时停步待命 |
| NPC | `terraria.npc.talk_to` | 接近指定 NPC 并开启对话 |
| Crafting | `terraria.crafting.make` | 查询配方、逐批合成并汇总产出 |
| Building | `terraria.building.construct` | 调查 Blueprint、提交施工任务并复查结果 |

模块可用性由依赖 API 与对应能力旗标共同决定。

例如，`terraria.inventory.give(teammateRef, "Terraria/Torch", 12)` 会向该队友抛出最多 12 支火把。`teammateRef` 取自 `list_teammates`。有可用虫洞药水时，无论队友远近都先消耗一瓶传送，再朝队友短抛。没有可用药水且队友在投掷范围外时步行接近。如果送的正是虫洞药水，会先留足交付数量。出手时会再次核对队友和距离。

函数从普通背包中按槽短抛未收藏的原版可堆叠非钱币物品，抛够即停。优先用手上匹配的物品，再按槽号取相同前缀的散堆。例如两格各有 5 支和 9 支火把，交付 12 支时可以先抛 5 支，再从第二格抛 7 支。库存不足时返回已抛总数 `dropped`，抛出一部分时状态为 `partial`。`drops` 记录每次抛出的数量和实体引用，`entityRef` 指向最后一抛。队友的 `lastDrop.confirmedReceived` 只记录最后这次掉落的实收，可用 `entityRef` 核对。

### 跟随移动中的队友

从 `list_teammates` 选出目标玩家，将其本次连接的 `teammateRef` 传给专服。下面的调用返回任务回执；队友折返或自卫结束后，仍使用同一个 `taskId`。

```lua
return terraria.move.follow(teammateRef)
```

“跟着我”默认一直跟随。闲聊、查背包和询问进度时继续跟随。玩家叫停，或新指令开始执行另一项身体动作时，宿主先取消旧跟随并核实输入已经释放。玩家明确要求“跟我两分钟”时用 `terraria.move.follow(teammateRef, {durationSeconds=120})`。时长可设 1–600 个游戏秒，到期时仍在跟随范围内才算完成。

停步距离 `distanceTiles` 默认 3 格，可设 1–12 格。队友再次拉开约两格后继续追，在不同高度时还会检查落点。用 `api("query_task", {taskId=id})` 查看进度，或用 `api("cancel_task", {taskId=id})` 停止。队友掉线、死亡或离队时，任务以 `teammate_unavailable` 结束。

### 从指定箱子按数量存取

`terraria.container.find("Terraria/Torch")` 原地读取附近未锁的原版普通箱子。默认半径为 32 格，可传 1–64 格，按角色中心到箱子中心的直线距离计算。成功时 `chests` 按距离由近到远排列，每行的 `x`、`y` 是箱子锚点，`count` 是该箱所有槽位中目标物品的合计。以下示例选择一个够取 12 支的箱子。

```lua
local offset = 0
while true do
    local found = terraria.container.find("Terraria/Torch", 32, offset)
    if found.status ~= "ok" then return found end
    for _, chest in ipairs(found.chests) do
        if chest.count >= 12 then
            return terraria.container.take(chest, "Terraria/Torch", 12)
        end
    end
    if not found.truncated then return {singleChestFound=false, totalCount=found.totalCount} end
    offset = offset + #found.chests
end
```

`matchedChests` 和 `totalCount` 统计整个半径内的匹配箱数与件数，每页最多返回 32 箱。物品存在但附近没有库存时，结果为 `status="ok"`、`chests=[]`、`totalCount=0`。名称未知或有歧义时返回 `unavailable` 和原因。每次查询都读取当时的库存，实际取到的数量以 `take` 回执为准。库存分散时，可按各箱的 `count` 选择多个目标，每次从需求中扣除实际的 `moved`。

已知箱子坐标时可直接取物。

```lua
local chest = {x=2104, y=217}  -- 换成观察或记忆中的箱子 Tile 坐标
return terraria.container.take(chest, "Terraria/Torch", 12)
```

存入同样数量时使用 `terraria.container.put(chest, "Terraria/Torch", 12)`。`target` 接受原版普通箱子四格中的任一格，`count` 为正整数。模块检查普通背包 0–49 槽，存入时跳过收藏物品，优先补满同品种同前缀的堆栈，再使用空槽。距离较远时先导航。模块自行打开的箱子在正常返回时关闭；调用前已打开目标箱时沿用当前会话，另一只箱子已打开时返回 `different_container_open`。

查看 `status`、`moved` 和 `remaining`。例如请求 12 支、箱中只有 6 支时，返回 `partial`、`moved=6`、`remaining=6` 和 `reason="insufficient_items"`。目的地装不下时原因为 `destination_full`；预留关箱操作后预算不足时为 `insufficient_budget`。一次存取由服务端在游戏线程内跨槽搬运，下一次更新核验后返回总实移量；实移为零且无法继续时返回 `unavailable`。原生动作发生硬失败时查看程序终态、`lastAction` 和 `moduleResult`；效果未知时，`moved` 只包含此前已确认的量，先复查箱子与背包再作决定。按数量存取模块依赖服务端能力 `server_ai_container_amount_v1`。

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
