# 探索观察 get_exploration_observation

调用方式：`get_exploration_observation({})`。这是 Python Agent 工具，不新增 C# TCP 命令。一次工具调用只请求一次现有 `Bridge.get_observation()`；复用 M1.9 游戏线程采样和严格校验，在 Python 投影后将探索数据交给模型。没有额外 `get_state/get_terrain` 读取，不缓存旧世界数据，不续控制租约。游戏端完整采样与 IPC 体积不因此减少；减少的是交给 LLM 的信息。

旧 `get_state`、`get_observation` 及移动工具、工具预算保持兼容。需要背包、装备、材质或 Frame 细节时仍用完整观察。没有新增自动寻路、跳跃或持续移动循环。

## 返回结构

工具外壳是 `{valid, exploration, receivedAt}`。`valid` 表示源观察不是 unavailable，不代表新鲜、完整或通路安全。探索对象 schemaVersion=1、sourceSchemaVersion=1，包括：

|部分|内容|
|---|---|
|元数据|世界／观察 ID、gameUpdate、采样时间、phase、status、坐标和观察范围、主动排除的能力|
|camera|保留原摄像机几何、渲染序号、来源更新号、年龄与过期状态|
|player|位置、碰撞框、速度、生命、死亡／幽灵、坐骑及重力／水中／运动受限等物理状态|
|terrain|世界 Tile 边界、语义矩形、未加载／世界外及 Mod 不可解释数量；潜在支撑／落差列段|
|entities|全部源实体的身份、类型、位置、速度、生命（适用时）、保守分类及依据，以及种类相关字段和原 coverage|
|investigate|调查候选按类型和可信度分组，重复锚点位置横向合并；不是确认能操作的对象|
|completeness|完整保留源 completeWithinScope、reasons 和 limitations；表示源范围覆盖，不表示主动排除的背包等也被返回|

不返回完整背包／装备／饰品／Buff、详细时间天气和生物群落、完整 palette/types/逐格索引。普通 Tile 材质、墙、涂色及外观 Frame 被主动排除；不可解释的 Mod 碰撞保留原 typeId/frame 作为进一步查询线索，仍标为 unknown_mod_hooks。

## 地形解读

`terrain.encoding="semantic_rectangles_v1"`。`regions` 每项为 `[tileX,tileY,width,height,stateOverrides]`，整数世界 Tile 坐标、范围右／下边界不包含；像素位置为 Tile × 16。按移动语义相同合并横向连续格，再合并相同横段的相邻行。所有矩形无重叠地覆盖原 bounds，不再给模型原始 palette 和逐格编码。

每个 state 都有 `knowledge`。**只有 knowledge=read 才能使用 readCellDefaults 补齐省略字段**；unloaded/out_of_world 不是空气，不允许套默认值。read 状态保留 collisionKnowledge，并分别保留：

- hasTile、solid、solidTop、halfBlock、slope；平台是单向，不是普通实心块。
- actuated、actuator、wireMask；当前是否阻挡和可能改变状态的机关信息不混淆。
- liquidType、liquidAmount；液体独立于危险字段。
- touchDamage、hot、bleeding、destroyOnTouch、boulder。

非碰撞 Tile 仍有 hasTile=true，与空 Tile 区分。合并仅忽略排除清单中的非探索属性；不是完整观察的无损编码，但上述碰撞语义逐格保真。不能推导出 Mod hooks、装备免疫或一定可通行。

`supportColumns` 每项为 `[tileX,width,supportTileY,status]`，从当前角色碰撞框底部向下找第一个潜在 solid/solidTop 支撑 Tile，actuated 不当支撑。可用于发现左右支撑高度变化和潜在落差，**不是可达路径、精确地表像素或安全跳落距离**；斜坡／半砖应结合 regions。缺少支撑只表示到视口下边界未观察到，不表示世界没有地面。未知在支撑前出现会标为 unknown_before_support；重力翻转或重力状态未知时返回 unsupported_or_unknown_gravity，不错误给出正常落地判断。

## 实体和候选

实体没有新增数量上限，原 coverage 的 matched/returned/omitted/reason 原样保留。集合说明所有实体的 sampleGameUpdate 等于观察 gameUpdate、inViewport=true；投影前运行原严格校验。相交仍只是几何范围，不保证实际可见。身份还是观察内作用域，不能将 friendly=false 当敌对。

掉落物保留 stack/prefix 等，去掉 details 与外层重复的身份名称；NPC evidence、玩家死亡／队伍、弹幕 owner/identity/friendly/hostile/damage 等种类字段仍在，弹幕不套用生命模型。没有将未返回实体当作不存在。

调查候选使用原 category/type/contentKey/interactionKnowledge/contents 分组；每组 anchorRuns 为 `[tileX,tileY,horizontalCount]`。保留原始候选锚点集合与计数，不把树木植物候选改称已确认交互对象，不读宝箱内容。当前未额外截断候选，omittedCount=0 表示本次投影没有删源候选，不保证引擎已发现所有交互对象。源 interactables 为 null 时新字段也为 null。

新聊天到来时，历史中的探索结果沿用完整观察的过期替换策略，不把旧 viewport 长期当当前场景。成本统计可识别新工具名称，工具字节和重复提交走原通用统计；`full_observation` 指标仍专指完整 get_observation，探索投影不是完整观察。

## 实际离线测量

统一按 `json.dumps(..., ensure_ascii=False)` 默认分隔符测 UTF-8，不采用去空白来放大收益。以下是**已保存真实游戏正文的离线投影**，不是新版实时游戏回执：

|真实工具结果|旧工具结果 B|新工具结果 B|
|---|---:|---:|
|Trace 68df… sequence 9|295,250|44,934|
|Trace 68df… sequence 52|303,032|51,199|
|Trace 7b39… sequence 9|308,827|56,133|
|Trace 7b39… sequence 41|310,614|55,927|

最后一份的探索对象本体为 55,845 B，分项如下。metadata 是其余顶层字段组成的独立对象，framing remainder 是整体与独立分组之和的净差，避免重复计数：

|部分|B|
|---|---:|
|metadata|638|
|camera|727|
|player|730|
|terrain|23,696|
|entities|22,801|
|investigate|6,139|
|completeness|1,028|
|framing remainder|86|
|合计|55,845|

该样本保留 8,470 个采样格的移动语义，合并成 266 个矩形；保留全部 46 个实体、329 个候选锚点，合并为 9 组／240 个横段。地形高度碎片化时矩形数可能明显上升，不承诺固定体积或比例，也没有为了控制体积静默截断。

仓库内脱敏真实 fixture 的世界 ID 和玩家显示名变短，旧观察对象 309,476 B，新探索对象 55,004 B；其分项可运行 `python tests/measure_exploration.py` 重现。脱敏和工具外壳差异解释了它与上表的数值差异。341,366 B 那次观察的正文未保存，不能声称已测到它的探索投影。**字节变化不等于 Token 节省比例**，本轮不调用真实模型或冒用其他 Tokenizer。

## 验证与真实游戏验收

`tests/test_exploration.py` 使用仓库中的脱敏真实 fixture，逐格检查矩形覆盖无重叠及关键语义、锚点还原、实体身份、坐标和源完整性。没有真实数据覆盖的斜坡／半砖／多液体／机关／未知／世界外／过期／死亡／截断／重力翻转是明确标注的合成场景。mock Agent 验证单次读取、参数错误、历史过期替换及 exploration→move→exploration 不增加工具预算。

真实验收只需重启 Python Agent，无需部署 Mod：

1. “调用 get_exploration_observation，告诉我左右有什么障碍、落差和水。”核对位置与地形；不应把未知或候选当已确认事实。
2. 安全平地：“先用探索观察，向右移动 500 毫秒，再用探索观察确认位置。”保持原限时移动约束。
3. “调用 get_observation 查看背包。”确认完整工具仍可用。
4. 正常死亡后读取探索、复活后再次读取；无需在两秒动作窗口内制造死亡。

查看 Cost 日志的 tool_cost 和 model_context_cost，确认实际调用了新工具及结果字节。仍需实测复杂地形、液体、未知区域与模型对矩形的理解；本轮未启动游戏、部署或提交代码。
