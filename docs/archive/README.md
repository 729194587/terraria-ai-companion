# 历史阶段资料

本目录只保留少量有助于理解架构演进的阶段报告。它们记录特定时间点的设计、实现和验证范围，不代表当前能力或当前版本状态。

## 阶段报告

- [G81：Server AI IPC](G81_SERVER_AI_IPC.md)
- [G82：Server AI 原子操作](G82_SERVER_AI_ATOMIC.md)
- [G82：Server AI 读取接口](G82_STAGE2_SERVER_READS.md)
- [G84：Server AI Agent](G84_SERVER_AI_AGENT.md)
- [G85：Server AI 感知](G85_SERVER_AI_PERCEPTION.md)
- [G86：实机能力缺口](G86_PLAY_GAPS.md)
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

## 使用规则

- 当前行为以源码、运行时能力握手和 [正式文档索引](../INDEX.md) 为准。
- 报告中的版本号、测试数量、命令、路径和验收结论只对原记录时点有效。
- 原始运行产物不长期保存在仓库；需要复测时从当前源码和测试脚本重新生成。
- 仍有效的 API 或架构契约应维护在正式专题文档中，不在历史报告中复制更新。
