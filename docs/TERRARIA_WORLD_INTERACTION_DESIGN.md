# Terraria 世界交互

世界交互让 Agent 能够改变和利用 Terraria 世界，包括建筑施工、住房管理、NPC 对话、商店交易和专用 NPC 服务。

模型负责选择目标、设计 Blueprint 和组织游戏流程。Skills 提供 Terraria 玩法知识；Server AI API 提供观察、查询和动作；游戏侧通过原生 Terraria/tModLoader 机制完成交互并核验结果。

## 当前能力

Server AI 已具备以下世界交互能力：

| 领域 | 能力 |
| --- | --- |
| 建筑 | Blueprint 差异调查、单格放置、多格物体放置、持续施工任务 |
| 住宅 | 房屋检查、NPC 住所分配与结果查询 |
| NPC 对话 | 发现 NPC、打开和关闭指定 NPC 对话会话 |
| 商店 | 原版城镇钱币商店的商品查询、报价、购买和出售 |
| 护士 | 治疗报价、付款、回血与减益清除 |
| 合成 | 配方查询和原生合成 |
| 玩法知识 | 按需加载 Terraria Gameplay Skills |

具体能力开放受当前 Server AI 握手与引擎适配影响。对应接口说明见 [文档索引](INDEX.md)。

## 建筑与施工

### Blueprint

Blueprint 使用世界 Tile 坐标原点，目标按 tiles、walls 和 objects 三层组织。单格目标描述 Tile 或 Wall；多格物体按原生 TileObjectData 的尺寸、Origin 和占地定义解析。

### Survey 与 BuildDiff

BuildDiff 对照 Blueprint 与当前世界，报告已经匹配、待施工、缺少材料、需要支撑或无法判断的目标。Survey 复用原生放置预检，识别占地、支撑、液体、碰撞和交互条件。

### BuildPlaceTask

`build_place` 是持续施工任务。任务组织目标依赖、工作站点和材料准备，调用导航、库存和原生放置执行器。每次施工后重新观察世界，再安排剩余目标。

完整调度结构见 [Building V2](BUILDING_V2_DESIGN.md)，阶段实现和验证见 [Building V2 实现记录](BUILDING_V2_IMPLEMENTATION.md)。

## 住宅管理

住房接口调用 Terraria 原生房屋检查和 NPC 住所分配机制。Agent 可调查房间状态、识别可用房间并为指定 NPC 分配住所。房间有效性、NPC 当前归属和分配结果由游戏侧查询。

住房管理与建筑施工相互配合：施工器负责创建结构，住房查询负责判断原生住宅规则是否满足。

## NPC 对话与服务

NPC 使用绑定世界和出生实例的 entityRef 识别。对话会话绑定指定 NPC 与 AI Player；会话打开后，商店和专用服务可以使用当前对话上下文。

### 城镇钱币商店

支持商人、爆破专家、军火商、哥布林工匠、机械师、巫师、树妖、染料商、蒸汽朋克人、巫医和海盗。先用 `open_npc_dialogue` 打开该 NPC 的对话，再用 `inspect_npc_shop` 查看该实例的商店。带未知条件或特殊货币的商品会给出不可购买的原因。

商店接口支持商品列表、单项报价、购买和出售。报价会绑定商人实例、Actor、库存和当前资金状态，并具有游戏更新及墙钟有效期。购买或出售时重新核对报价、库存、货币和 NPC 状态。

批量购买通过独立能力开放，单次数量范围由当前接口契约定义。

### 护士

护士服务先计算治疗报价，包括预计回血、可清除减益和诊金。Agent 根据报价决定是否治疗；执行时核对报价有效性、资金、角色生命状态和护士实例，随后通过原生服务路径完成治疗。

### 哥布林重铸

先用 `inspect_inventory` 取得主背包槽的令牌，再向附近的哥布林工匠调用 `quote_reforge`。报价的 `prefixes` 给出适用词条的 ID、内部名和显示名。用同一份报价调用一次 `reforge_item`，传入 `targetPrefix` 后由服务器连续重铸。已经命中时零次扣款，付不起下一次时返回 `insufficient_funds`。结果中的 `rolls`、`priceCopper` 和 `afterPrefix` 分别是已完成次数、总花费（铜币）和最终词条。省略目标则只重铸一次。

### 用桶与压力板

独立服务器 AI 手持普通空桶时可调用 `api("use_water_bucket", {x=tx, y=ty, mode="scoop"})`，手持水桶时用 `mode="pour"`。坐标单位是世界格。取水遵循原生桶对目标及相邻格的取水规则。

专服 AI 意外踩上压力板时会经过原生触发检查。定向导航会在前方步行路段遇到可接近的压力板时停下，用镐拆掉后重新规划。

### 其他 NPC 服务

对话入口与商店、护士服务分离。其他 NPC 服务应以各自原生调用链和结果契约扩展，不将普通对话能力视为所有 NPC 服务都已接入。

## 合成与资源准备

配方查询读取当前加载的 Terraria 配方注册表，返回产物、材料、制作站、环境条件及支持状态。合成执行器调用原生配方和入包机制，并核对实际产物与材料变化。

Lua `terraria.crafting.make` 将期望产物件数换算为配方执行次数，逐批重新查询和核验结果。

## Gameplay Skills

Gameplay Skills 保存 Terraria 规则和玩法知识，通过 `read_skill` 按需加载。技能内容帮助模型理解建筑、住房、NPC 服务和经济流程；实际操作仍由 API 执行。

## 关键实现入口

- Blueprint 与施工：`CompanionBridge/ServerIpc/BuildingBlueprint.cs`、`BuildPlaceTask.cs`、`BuildWorksiteSearch.cs`
- NPC 对话：`CompanionBridge/ServerIpc/NpcDialogue.cs`
- 商人交易：`CompanionBridge/ServerIpc/MerchantShop.cs`
- 护士服务：`CompanionBridge/ServerIpc/NurseService.cs`
- Server AI 能力与 API：`python/server_ai_contract.py`、`python/atomic_operations.py`
- Lua 模块：`python/lua_modules.py`、`python/lua/modules/`

住房与交易的专服验证、版本条件及已知范围保留在对应实现记录和测试脚本中。
