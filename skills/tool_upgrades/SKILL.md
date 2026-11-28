---
name: tool_upgrades
description: 工具升级与备料：熔岩镐（Molten Pickaxe）合成、狱石锭、材料缺口与工作站。
game_version: Terraria 1.4.4.9
scope: 原版独立服务器 AI Player；本期目标为新制一把熔岩镐。
---

# 制作一把熔岩镐

从随正文返回的 knowledge.records 按名称或别名找到物品键。规则适用版本见 game_version，其他版本须复查来源。遇到歧义先澄清。背包中已有的镐只用于记录起点。

用 `inspect_inventory({mode="item", item=物品键})` 取得目标的 typeId，并合计槽 0–57 的当前件数。要额外新制一件时，调用 `terraria.material_plan.plan(typeId, 当前件数 + 1)`。要备齐指定总量时，直接传目标总数。读取 data.steps 的制作顺序和工作站、data.deficits 的原料缺口。查询不可用时向玩家说明原因。

要从基地取料，用 `api("get_world_state", {}).data.homeBase` 看自己是否在世界出生点周围 64 格内。在外时先回基地，优先前往已知的基地制作区落脚点。可按 spawnTile 的格坐标观察地形，选定落脚面后用世界像素调用 `navigate_to`。确认到达再用 `terraria.container.base(材料键)` 查各箱现货，按缺口取回后重算。仍缺原料时按需读取 mining_and_ores。只问箱里有多少时可以直接查。

玩家要求制作时，依计划顺序调用 `terraria.crafting.make`。野外原地能做就做。若返回 missing_crafting_station，回基地制作区后按 remaining 续做，需要取料时合并为同一趟。已经在基地边缘时可直接前往已知制作区。到制作区仍缺站时，按 stations 说明当前位置需要哪种站。只问做法时止于说明。
