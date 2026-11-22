# Building V2 实现与验证记录

本文记录 Building V2 施工调度器的实现结构、定向验证和 V1/V2 对照。当前施工架构见 [Building V2](BUILDING_V2_DESIGN.md)。

## 实现结构

Building V2 保留 `build_diff` 和 `build_place` 两个模型入口，施工调度在游戏侧完成。

| 组件 | 职责 |
| --- | --- |
| `BuildingBlueprint` | 解析 tiles、walls、objects 三层 Blueprint，处理多格物体占地 |
| `BuildDiff` | 调查当前世界与 Blueprint 的差异、材料和放置条件 |
| `NativePlacementSurvey` | 复用 Terraria 原生放置规则进行目标预检 |
| `BuildWorksiteSearch` | 搜索能够覆盖多个待施工目标的安全落脚站点 |
| `BuildPlaceTask` | 管理施工阶段、目标队列、材料持用、导航、放置和最终复查 |
| `ActorInventory` | 提供带观察 Token 的材料持用和库存操作 |
| `TaskRuntime` | 推进根任务及其串行子任务 |

施工过程包括初始调查、目标状态与支撑关系整理、站点选择、材料准备、原生放置和重新调查。站点候选根据安全落脚、可覆盖目标数量、材料种类和移动距离排序。

## 调度与恢复

- 已匹配目标在初始调查中标记完成。
- 放置预检返回的支撑信息用于识别 Blueprint 内部依赖。
- 缺少 Blueprint 内支撑的目标延后处理，支撑完成后重新调查。
- 当前站点可施工目标处理完后，再选择下一个工作站点。
- 每次放置后读取真实世界状态，更新目标状态和材料消耗。
- 施工期间发生外部变化时，最终结果按实际观察区分 matched、placed、changed、pending 和 unknown。
- 取消或部分完成后可以重新提交同一 Blueprint；新任务重新调查当前世界。

施工阶段耗时、导航次数、站点访问、材料切换、Survey 次数和放置结果都进入任务进度及终态回执。

## V1 / V2 对照实验

实验使用同一来源世界、Blueprint、初始位置、快捷栏和库存。输入为 4 个木块与 4 个泥土块交错排列。每个单元格是一趟独立 Dedicated Server 运行。

| 场景 | 版本 | 站点 | 导航调用 | 材料持用调用 | 目标调查 | 放置尝试/成功 | 根任务 Tick | 实耗秒 |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| open | V1 | 3 | 2 | 8 | 34 | 8/8 | 287 | 5.109 |
| open | V2 | 2 | 1 | 3 | 39 | 8/8 | 242 | 4.329 |
| crossing | V1 | 3 | 2 | 8 | 34 | 8/8 | 287 | 5.141 |
| crossing | V2 | 3 | 2 | 4 | 41 | 8/8 | 298 | 5.219 |

两个场景中 V2 均减少了材料切换。open 场景减少 45 个根任务 Tick；crossing 场景增加 11 个 Tick。V2 的 Survey 次数较多，反映调度器在站点与目标变化时进行重新调查。

计数口径来自各版本实际执行分支：V1 的目标调查数按准入、放置后核验、最终调查及子任务交接统计；V2 读取调度器自身计数。两种版本均排除原生执行器内部检查。

## 定向验证

以下记录来自当时的隔离 Dedicated Server、复制世界和指定测试包。它们是对应测试场景的历史结果。

| 验证入口 | 覆盖内容 |
| --- | --- |
| `verify_server_ai_native.ps1 -BuildPlace` | 依赖循环、外部缺支撑、缺料、阻挡、取消、重新提交、全匹配重复提交 |
| `verify_server_ai_native.ps1 -BuildDiff -Actions` | 65 项蓝图调查、多格家具计量、只读调查和非法重叠拒绝 |
| `verify_server_ai_native.ps1 -Housing -NpcHome` | 房屋尺寸、墙面、门、工作台、椅子、光源、原生 Housing 和 NPC Home |
| `verify_server_ai_placement_native.ps1 -Survey` | 植物、南瓜、占地、Alternate、墙支撑和部分清除 |
| `verify_server_ai_placement_native.ps1 -Object -Survey` | 多格对象原生放置预检、取消与期限 |
| `verify_navigation_recovery_native.ps1 -Region -RegionIterations 1` | 原生区域查询与落脚导航 |
| Python 定向 unittest | `test_build_place`、`test_build_diff`、住房、NPC Home、库存和放置相关测试共 34 项 |

### 世界状态恢复

一次施工在 1/4 进度时取消并保存世界。第二个 Dedicated Server 从该世界副本启动，调查识别出已完成的第一格，只施工剩余三格，最终达到 4/4。

另一个夹具在施工过程中移除已放置格并补齐未来目标。最终调查分别记录 changed 与 matched；再次提交时只补齐缺失目标。

## 历史问题与验证边界

原始实施阶段记录过以下问题：

- 一次并行运行中的住宅 fixture 返回 `native_placement_refused`，单独运行通过，未确定并行场景根因。
- 离线 `-LandingOnly` / `-SafeGroundWalkOnly` 公共源集合编译因缺少 ServerIpc 类型停止。
- 通用类型契约抽查中，`use_item` 样本曾校验失败，并在当时基线独立复现。

这些记录对应实施时的代码与测试环境，不作为当前分支的未解决问题判断。需要复现时，应使用当前分支重新运行相应入口。

本记录不包含真实模型 Host & Play 的完整建筑任务评测。Dedicated Server 原生测试验证的是指定场景中的游戏执行与结果核验。
