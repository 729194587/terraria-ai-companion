# Server AI 能力认知与模型契约

日期：2026-10-31。范围为模型上下文与契约生成，不新增游戏API，不改Agent Loop、原子动作执行或Worker权限。

## 来源与生成规则

- `server_ai.operations_for_capabilities()` 从实际bind握手投影API白名单，ServerAiBridge与模型契约共用此函数。保留原有依赖：动作总能力之外，Direct Input、二维导航、放置、合成各自仍需对应能力旗标；仅合成旗标不开放合成动作。
- `server_ai_contract.exposed_operations()` 取握手投影、当前Bridge白名单与已描述服务器API的交集。未知旗标不扩大能力；测试适配器没有hello时仅信任其已声明且属于已知服务器API的白名单。
- Capability Manifest按角色/背包、感知、地图记忆、直接输入、导航、物品/战斗、采集、建造、制作、背包变更、交互、等待/取消和装备运动分类。Supported只表示有界接口开放；Limited必须结合具体API限制；Not exposed明确不可调用。替代项也只从已开放API选择，并注明非等价。
- API Contract由`atomic_operations.server_tools()`深拷贝原TOOLS/Schema；只保留可调用名字。服务器use_item继续使用既有的空参数Schema，其他Schema不改。服务器限制集中到该模块的Description：单位、前置状态、结果字段、部分支持范围和效果核验。
- `lua_agent.server_contract()`只委托生成四块内容：角色原则、Manifest、API Contract、Lua Runtime Reference。execute_lua描述不再重复全量API名字；示例按白名单生成，不建议调用缺失API。
- Runtime Reference依据`lua_worker.BOOTSTRAP`、JSON codec、LuaRuntime.Limits及实际Lua helper源码整理。标准库只列Worker真实白名单；helper虽注入但依赖不齐时明确勿调用；场景探索说明同时要求Worker协议支持scene_exploration参数，旧Worker只说明水平模式，不仅凭游戏握手推断辅助脚本升级。helper不是游戏原子API，局部table不跨程序持久化；query_map的AI地图记忆不等于脚本存储。
- 保留用户授权、绑定AI对象、预算共享、预检续行的宿主证据、unknown隔离/不重放，以及输入释放不等于效果确定的语义。没有靠模型提示代替执行器校验。

## Prompt 对照

口径：静态system字符串 + 紧凑JSON序列化的execute_lua工具定义；不含历史消息、runtimeInventory/runtimeLua动态数据和传输封套。字符为Python Unicode字符数。基线是修改前实际工作区的lua_agent.py（含原有未提交内容），SHA-256：
`76d292e912fea8f5da9cd69ec51607bfc508c85bfef5340a6412f8c1ba4a2f94`。

| 握手配置 | System字符：前 → 后 | 总字符：前 → 后 | Token估算：前 → 后 |
| --- | ---: | ---: | --- |
| 最小：选择/等待/取消 | 4,246 → 4,945 | 4,677 → 5,294 | 2,354–3,933 → 2,369–3,762 |
| 读取 + 基础感知 | 9,221 → 9,661 | 9,764 → 10,010 | 4,290–6,755 → 4,061–6,138 |
| 当前完整服务器能力 | 23,120 → 18,809 | 23,820 → 19,158 | 11,053–17,850 → 8,164–12,663 |

完整配置总字符减少19.6%。低能力配置新增明确缺失能力清单与运行时边界，字符数增加；不宣称所有配置都缩短。
表中“后”以独立暂存快照的旧Worker水平模式统计；工作区原有未提交的scene helper模式会得到19,195总字符，差异来自如实描述不同Runtime模式。
Token为可比较的启发式区间：U+3400..U+9FFF中文字符按1..2 token，其余字符按4字符/token，向上取整；不是指定模型的tokenizer结果、保证上下界或计费数字。

复算当前配置：

```powershell
& .local/lua-venv/Scripts/python.exe -B scripts/measure_server_ai_prompt.py
# 本地保留的修改前快照可同时复算前后：
& .local/lua-venv/Scripts/python.exe -B scripts/measure_server_ai_prompt.py --baseline .local/capability-awareness/lua_agent.before.py
```

## 定向验证与限制

新增测试穷举1024种可选旗标组合，核对Bridge投影、Manifest可调用集合、模型API集合、替代项与示例；同时检查未知旗标、过宽旧白名单、Schema不变及TOOLS不被修改。真实Worker测试逐项比较标准库成员，并验证JSON封套、非法结构及新程序没有旧变量。

共37个不同定向用例最终通过（包含上述矩阵测试、既有模型契约、权限/共享预算、失败/取消、unknown与pcall宿主预检保护）。初轮有契约断言词句迁移遗漏及一次测试类名选择错误，修正后相关项通过；另对独立暂存快照运行新增6项测试通过，确认不依赖原有未提交Worker/场景探索改动；未运行全量测试。

这里只验证生成契约与现有执行边界一致，未调用真实模型、未进行图形实机能力认知对比。Description是人工维护的后端投影；执行器能力变化时仍需同步更新Description、分类和定向测试。运行时可用不代表所有装备/原生场景已实机验证，Manifest明确保留Limited及观察的未知/时效。
