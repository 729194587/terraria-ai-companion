---
name: tool_upgrades
description: 工具升级与备料：熔岩镐（Molten Pickaxe）合成、狱石锭、材料缺口与工作站。
game_version: Terraria 1.4.4.9
scope: 原版独立服务器 AI Player；本期目标为新制一把熔岩镐。
---

# 制作一把熔岩镐

从随正文返回的 knowledge.records 按名称或别名找到物品键。规则适用版本见 game_version，其他版本须复查来源。遇到歧义先澄清。背包中已有的镐只用于记录起点。

在当前角色执行只读 Lua：`return terraria.material_plan.molten_pickaxe()`。该模块按会话精确解析目标和锭，分页查询原生配方（包括当前不可制作的配方），再按物品键读取背包。status 为 planned 时，barDeficit 是尚需制作的锭数，rawDeficits 是两种原料仍需入包的数量。recipes 保留站点、条件与空间供判断。unknown 或 unsupported 时报告 reason 和相关内容键，先补齐观测或确认配方。

缺料时按正缺口查看相应获取规则，再按需读取 mining_and_ores。箱中旧数只给调查线索；实际取回物资后重新计算。缺站时定位对应制作站；背包材料齐备也要复查站点和空间。只问做法时止于说明。执行制作属于后续阶段，结案须有新制回执与同角色目标物背包净增一把。
