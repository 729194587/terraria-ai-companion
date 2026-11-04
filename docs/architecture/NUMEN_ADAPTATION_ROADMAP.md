# Numen 参考研究

<text>研究资料 · 非规范性</text>

参考项目：[Dwinovo/minecraft-numen](https://github.com/Dwinovo/minecraft-numen/tree/1.21.1)。

Numen 可作为 Agent、Lua 程序执行、原子 API、感知、导航、任务生命周期和可观测性设计的研究样本。它不是 Terraria AI Companion 已采用的目标架构；本项目现行模块职责以 [系统架构](../ARCHITECTURE.md) 和 [正式文档索引](../INDEX.md) 为准。

## 研究问题

阅读具体实现和历史提交时，回答：

- 旧方案遇到了什么实际问题？
- 新设计解决了什么，又引入了什么复杂度？
- 相关测试覆盖了什么，哪些场景仍未验证？
- 后续是否发生过撤销或再次重构？
- 哪些依赖 Minecraft 服务端、实体、方块或网络模型，不能直接移植到 Terraria？

## 建议阅读

- [Architecture Mind Model](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/docs/architecture-mind-model.md)
- [Agent Loop](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/docs/agent-loop.md)
- [Pathing](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/docs/pathing.md)
- [Shell](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/docs/shell.md)
- [Spatial Perception](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/docs/spatial-perception.md)
- [Permission Layer](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/docs/permission-layer.md)
- [Input Pipeline](https://github.com/Dwinovo/minecraft-numen/blob/1.21.1/docs/input-pipeline.md)

## 适配原则

1. 借鉴问题解决方法和职责边界，不照搬 API 或模块数量。
2. 先核对本项目已有实现，避免重复造轮子。
3. 把“代码存在”“能力对外开放”“测试通过”“真实游戏验收”视为不同结论。
4. 参考设计若影响控制权、身份绑定、原生执行或失败隔离，必须先评估 Terraria 侧风险。
5. 只有经过本项目证据验证的设计，才写入正式架构或专题契约。

具体审查问题和交付格式见 [架构审查计划](ARCHITECTURE_AUDIT_PLAN.md)。
