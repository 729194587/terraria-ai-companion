# 战斗系统

Server AI 战斗由游戏侧持续任务驱动。Agent 负责选择目标、配置武器并发起战斗；C# 运行时负责目标身份、身体控制权、原生攻击、接近、命中归因和终态判断。

当前有两种战斗入口：

- **主动战斗**：Agent 通过 `combat_attack(entityRef)` 指定一个目标。
- **自卫 Reflex**：服务器运行时根据角色受到的威胁主动寻找目标，并在当前防御任务中继续处理其他威胁。

两种入口共用 CombatController 和 NativeCombatWorld，避免分别维护攻击、输入和命中规则。

## 任务生命周期

CombatController 在游戏更新中推进目标观察、接近、攻击和结果确认。单次主动战斗的预算为 1800 个游戏更新。

启动时，NativeCombatWorld 核对目标引用、NPC 实例、角色状态、武器和执行条件，然后取得身体控制权。任务执行期间持续检查目标是否仍为同一出生实例、是否存活以及是否仍可攻击。

主动战斗在目标被确认击败时完成。目标被替换、失效或不再符合条件时，任务以对应原因结束。自卫任务可以在当前目标失效或接近失败后重新选择威胁；主动任务则保持调用者指定的单一目标。

## 身体控制与动作协调

同一个 Server AI Player 同一时刻由一个身体动作所有者控制。战斗通过统一的原生执行入口取得控制权，与导航、挖掘、物品使用等动作协调。

CombatInputCycle 记录移动与攻击输入的更新周期，并确保每个游戏更新至多执行一次原生 ItemCheck。移动和攻击可以在同一更新中组合，但各自的输入通道由战斗运行时协调。

攻击执行沿 Terraria 原生物品使用路径进行。近战攻击使用原生 ItemCheck；远程攻击核对弹药、弹道与投射物；召唤攻击记录召唤物来源和实例。战斗结束后释放由任务持有的输入。

## 目标与命中归因

目标引用绑定世界会话、Actor 代次、NPC Slot 和出生 Token。NPC 槽位复用时，旧引用不会指向新实例。

命中证据记录目标实例、生命值变化、伤害、游戏更新和攻击来源。远程投射物保存发射时的武器、弹药、所有者、实例和目标 epoch；召唤物保存来源武器、Projectile 身份及其目标归属。命中时重新检查来源与目标关系，避免把旧任务、其他角色或已替换目标的攻击计入当前战斗。

完成条件以原生目标状态和本次战斗可归因的效果为依据。攻击输入次数、动画播放或投射物发射本身不构成命中证据。

## 武器配置与选择

Agent 可通过 `configure_combat_loadout` 配置主武器、副武器和召唤武器。配置引用快捷栏中的物品槽及观察 Token，并记录物品类型和配置版本。

战斗运行时根据距离、武器类别、弹药、法力和可用攻击方式选择武器。近距离优先使用可生效的近战武器；远距离在弹道与资源允许时使用弓、枪或已适配法器。召唤武器用于生成仆从，运行时会核对原生施放结果和仆从实例。

配置与执行分离：Agent 决定允许使用哪些武器，游戏侧根据当前交战条件选择和切换。完整配置行为见 [战斗配装与自卫](COMBAT_WEAPON_LOADOUT_DESIGN.md)。

## 自卫与威胁躲避

自卫 Reflex 在服务器更新中观察附近敌对实体，并根据目标关系和接近情况选择威胁。Boss Encounter 使用独立的短时威胁预测器，处理 NPC 接触、弹幕和已适配的特殊弹道。

BossEvasion 使用约 48 个游戏更新的局部预测窗口，限制候选威胁数量，并复用导航的物理模拟与落脚检查评估刹停、水平移动、跳跃和平台穿越。预测结果在执行过程中持续与实际位置比较。

当局部候选均无法避开威胁时，系统保留本次无安全路线结果，并选择通过地形检查且预测暴露较低的动作。该判断服务于当前观察窗口，不是全局安全保证。

## Boss Encounter

Boss 战斗使用独立的 BossEncounterTask。它在整场战斗期间持有一个身体控制权，维护 Encounter ID、Boss 根实例、成员集合、当前目标、武器状态和战斗证据。

成员追踪区分 Boss 根、部件、附属敌人和共享生命承载者。系统根据原生关系、出生实例和专用规则更新成员集合。每次攻击与命中都关联当前 Encounter 和成员版本。

Boss Encounter 支持：

- 多成员 Boss 的发现与成员追踪。
- 持续攻击、武器选择和威胁躲避。
- 战斗进度与已核验伤害记录。
- 显式取消和有界撤退。
- 可选的死亡后自然复活等待与队友归队流程。

当前原版 Boss 隔离专服记录中，克苏鲁之眼、世界吞噬者和骷髅王完成过原生击败；双子魔眼、毁灭者和月亮领主的记录以角色死亡结束。详细场景、配装和数据见 [Boss Encounter 验证记录](R2_E_BOSS_ENCOUNTERS.md)。

## 主要实现入口

| 模块 | 文件 |
| --- | --- |
| 普通战斗任务 | `CompanionBridge/ServerIpc/CombatController.cs` |
| 原生战斗适配与命中归因 | `CompanionBridge/ServerIpc/NativeCombatWorld.cs` |
| 武器与弹道 | `CompanionBridge/ServerIpc/CombatBallistics.cs` |
| Boss 任务 | `CompanionBridge/ServerIpc/BossEncounterTask.cs` |
| 威胁预测与躲避 | `CompanionBridge/ServerIpc/BossEvasion.cs` |
| 战斗入口与能力 | `CompanionBridge/ServerIpc/ServerAiNativeActions.cs`、`python/server_ai_contract.py` |
| 任务生命周期 | `CompanionBridge/ServerIpc/TaskRuntime.cs` |
| 原生验证 | `tests/native_combat.py`、`tests/native_boss_encounter.py`、`tests/native_boss_strategy.py` |
