# 历史阶段资料

本目录只保留有助于理解架构演进的阶段报告。它们记录特定时间点的设计、实现和验证范围，不代表当前能力或当前版本状态。旧报告中指向原始日志、回执、manifest 或 ZIP 的链接可能已失效：历史运行产物已从仓库清理，不再提供下载。

## 阶段报告

- [G81：Server AI IPC](G81_SERVER_AI_IPC.md)
- [G82：Server AI 原子操作](G82_SERVER_AI_ATOMIC.md)
- [G82：Server AI 读取接口](G82_STAGE2_SERVER_READS.md)
- [G84：Server AI Agent](G84_SERVER_AI_AGENT.md)
- [G85：Server AI 感知](G85_SERVER_AI_PERCEPTION.md)
- [G86：实机能力缺口](G86_PLAY_GAPS.md)
- [本地普通聊天入口阶段记录](PLAIN_LOCAL_CHAT.md)
- [场景响应精简阶段记录](SERVER_AI_SCENE_COMPACTION.md)
- [M1.1：基础身体控制设计](M1.1-基础身体控制设计.md)
- [M2：API Contract 与 Tool System 实施记录](API_TOOL_SYSTEM_MVP.md)
- [M2：API Contract 与 Tool System 设计](API_TOOL_SYSTEM_MVP_DESIGN.md)
- [早期原子能力基础](ATOMIC_FOUNDATIONS.md)
- [导航架构设计研究](NAVIGATION_DESIGN.md)
- [TerraBlind 导航原型与历史记录](TERRABLIND_NAVIGATION.md)
- [客户端观察接口历史说明](OBSERVATION.md)
- [observe_scene 数据层阶段说明](SERVER_AI_SCENE_LAYERS.md)
- [二维地形通过阶段报告](L2_TERRAIN_TRAVERSAL.md)
- [自然世界导航实验](NATURAL_WORLD_NAVIGATION.md)
- [导航落脚确认修复记录](NAVIGATION_LANDING_CONFIRMATION.md)
- [Server AI 二维落脚阶段报告](SERVER_AI_LANDING_REGION.md)
- [M4：Agent 能力](M4_AGENT_CAPABILITIES.md)
- [M4：最终验收记录](M4_FINAL_ACCEPTANCE.md)
- [R1：新角色初始化](R1_NEW_CHARACTER_INITIALIZATION.md)
- [R2/R3：铜斧实验](R2_R3_COPPER_AXE.md)

## 其他已归档的阶段与修复记录

- [动作回执原因与导航诊断](ACTION_RECEIPT_REASONS.md)
- [Agent Runtime MVP](AGENT_RUNTIME_MVP.md)
- [自主探索 MVP](AUTONOMOUS_EXPLORATION_MVP.md)
- [基础移动](BASIC_MOVEMENT.md)、[阶段 A 移动](STAGE_A_MOVEMENT.md)
- [聊天 Agent](CHAT_AGENT.md)、[探索观察](EXPLORATION_OBSERVATION.md)
- [物品使用与挖掘恢复](ITEM_USE_AND_MINING_RECOVERY.md)
- [二维落脚导航入口](LANDING_REGION_NAVIGATION.md)
- [原生挖掘夹具隔离](NATIVE_MINING_FIXTURE_ISOLATION.md)
- [导航诊断](NAVIGATION_DIAGNOSTICS.md)、[Direct Input 降级](NAVIGATION_DIRECT_INPUT_FALLBACK.md)
- [平台与快捷栏修复](PLATFORM_AND_HOTBAR_FIXES.md)、[放置缩放修复](PLACEMENT_ZOOM_FIX.md)
- [单格方块放置](SERVER_AI_PLACEMENT.md)
- [Virtual Player 历史阶段资料](VIRTUAL_PLAYER_CHARACTER_PERSISTENCE.md)、[引擎钩子](VIRTUAL_PLAYER_ENGINE_HOOKS.md)、[草地挖掘修复](VIRTUAL_PLAYER_GRASS_MINING_FIX.md)、[原生物品使用](VIRTUAL_PLAYER_NATIVE_ITEM_USE.md)、[原生挖掘](VIRTUAL_PLAYER_NATIVE_MINING.md)、[原生生存](VIRTUAL_PLAYER_NATIVE_SURVIVAL.md)、[网络同步](VIRTUAL_PLAYER_NETWORK_SYNC.md)、[拾取归属修复](VIRTUAL_PLAYER_PICKUP_OWNERSHIP_FIX.md)、[服务器控制](VIRTUAL_PLAYER_SERVER_CONTROL.md)、[单窗口集成](VIRTUAL_PLAYER_SINGLE_WINDOW.md)、[可用性](VIRTUAL_PLAYER_USABILITY.md)

## 使用规则

- 当前行为以源码、运行时能力握手和 [正式文档索引](../INDEX.md) 为准。
- 报告中的版本号、测试数量、命令、路径和验收结论只对原记录时点有效。
- 原始运行产物不长期保存在仓库；需要复测时从当前源码和测试脚本重新生成。
- 仍有效的 API 或架构契约应维护在正式专题文档中，不在历史报告中复制更新。
