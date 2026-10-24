# Lua 编排第二阶段：Agent 接入

0.1.40更新了分层预算、源码行号、阶段日志与证据包，见 [可观测性与预算](LUA_OBSERVABILITY.md)。

基线为第一阶段 `10788fe8c8201e4390dec4dca6ece60909b8d908`，开始时工作区干净。
本阶段直接在现有 Chat Completions Agent 中注册 `execute_lua`，不新增模型客户端、游戏协议或 Mod 版本。
第一阶段的 Worker、Schema、dispatcher、结束证据与未知状态保护继续生效，见 [运行时说明](LUA_RUNTIME.md)。

## 使用

安装 `python/requirements.txt`，沿用现有 `.env` 的模型配置和已部署的 0.1.37 游戏端。
正常聊天模式与本地终端模式均默认开放 Lua 工具：

```powershell
.local/lua-venv/Scripts/python.exe python/agent.py --local-input
# 或多人聊天触发：
.local/lua-venv/Scripts/python.exe python/agent.py --port 18777
```

可以用自然语言要求“检查快捷栏，选择快捷键2，再读取核对”作为较小验证任务。
模型可在一次工具调用中提交完整程序：

```json
{"source":"local before=api('inspect_inventory',{mode='selected'}); local r=api('select_hotbar_slot',{slot=2}); if not r.valid or not r.completed then return {action=r} end; return {before=before,action=r,after=api('inspect_inventory',{mode='selected'})}"}
```

`source` 是唯一参数，最多 16 KiB UTF-8。模型不能改墙钟、调用数或存档路径。
现有直接工具保留兼容；提示词要求多步骤任务优先使用 Lua。每次模型响应仍只允许一个工具，
内部 `api` 可以顺序多次执行。Lua 不支持调用外部 `get_observation/get_exploration_observation`
或旧 `move`；0.1.38起，程序内可用 `observe_exploration` 读取有界视口探索，
用 `get_player_status` 获取紧凑状态、`resolve_content` 解析名称、`wait` 可取消等待。
完整说明见 [基础能力扩展](ATOMIC_FOUNDATIONS.md)。0.1.39新增token保护的单人背包持用与搬运，见 [背包操作](INVENTORY_OPERATIONS.md)。

模型通过同名直接工具获得权威的参数 Schema 与能力限制；Lua 工具描述从当前 SCHEMAS
自动列出白名单。提示词补充结果层级、数组索引、json.null、可用函数、失败停止和效果确认语义。
HTTP 输出预算从 512 增为默认 2048 tokens，给完整程序留空间；宿主构造 LLMClient 时可配 1–8192。
返回被 finish_reason=length 截断的程序仍按不完整模型响应拒绝，不执行半段代码。

## 预算与结果

- 默认每轮7次模型工具、64次Lua内部API、8次变更尝试分别限制，多段程序共享剩余预算。
  一次execute_lua加三次api消耗1次模型工具和3次内部API，动作另计；成本仍记录4个工具结果。
  失败参数/blocked内部请求也记API数，直接挖掘恢复的固定查询allowance保留，动作仍计预算。
- Lua 总墙钟预算默认整轮60秒，包含每次 Worker 启动、API 等待及实际收尾耗时；
  多次程序共享剩余时间，不因模型重新提交程序重置。模型请求时间不计入这项预算。
  单次清理仍有第一阶段的独立期限，未知状态时断开并禁止复用 Bridge。
- 每次模型请求末尾的临时 `runtimeLua` 给出真实剩余额度、可执行内部 API 数、剩余秒数及源码上限，
  与 runtimeInventory 一样不写入永久用户历史。每轮至少为工具之后的模型反馈预留一次请求。
- `programCompleted` 只表示程序正常返回。`actionSuccess` 汇总所有内部行动（包括停止收尾），
  没有行动则为 null；不会只看最后一个成功动作来覆盖之前的失败。
  `taskOutcome` 为 unknown 或 not_completed，不用脚本自报 success 证明玩家目标完成。
- `value` 是模型编写的程序返回数据，与宿主的顶层状态分开。当前轮模型会收到完整有界返回；
  跨轮历史省略 value 并标记 valueExpired，保留执行回执、源码和关联信息，避免旧观察当成新状态。
- 每次内部结果进入现有动作失败、use_item 效果未确认、MovementLoop 观察失效与诊断链路。
  Lua/直接工具之间不能通过切换入口绕过失败后的行动禁令。
  纯语法错误且整轮尚未行动时，可在剩余预算内纠正；行动后脚本失败则停止本轮进一步行动。

`TaskCosts` 继续统计真实 provider usage，工具成本记录包含 execute_lua 与已返回的内部操作，
新增 lua_api_calls 记录所有已接收的内部尝试（包括取消时尚未返回的操作）。
这与模型调用次数、已提交工具结果字节分开，不将工具次数/字节冒充 token 或货币成本。

## 新消息、关闭与动作收尾

新聊天立即取消旧轮次；新轮次等旧 Lua 轮次完成必要收尾后才刷新背包或请求模型。
本地终端的新指令也先等待收尾，再调用 begin_local_turn，防止该请求撞上 Lua 会话所有权保护。
只等待旧 Lua，保持原有普通 HTTP 请求抢占语义；不会因为不响应取消的旧普通模型请求阻塞新聊天。
多个连续新消息不会把取消传播进已受保护的旧动作收尾。

退出/Agent.close 等待 Lua 清理。旧程序未完成的 assistant/tool 批次不提交到历史，
源码仍可在本地找到。未知状态永久禁用该 Bridge，后续程序和直接工具均无法启动新动作；
断线时可能无法再向游戏聊天发送失败消息，应查看本地运行日志。

跨语言验证还暴露一个现有竞态：动作在 running 查询之后、renew_move 到达之前已经结束，
游戏端会拒绝续租为 action_changed。dispatcher 现在重新读取并只接受**同身份的终态动作**，
再走原有 terminal/endEvidence 核验；不重试动作，不吞掉其他错误。替换所有者、仍在运行或
缺少释放证据依旧不能报告完成。

## 源码与日志

执行前将源码保存到 `.local/lua-programs/<SHA256>.lua`，默认最多64份、每份最多16KiB，
重复内容去重，淘汰旧文件。保存失败不执行程序。已注册凭据会被脱敏，并在记录中标记 redacted；
只有未脱敏的文件才承诺与实际执行源码完全一致。生成文件和日志不进入 Git。

沿用默认有界运行日志与可选完整 trace：

- lua_program_saved 关联源码哈希和本地路径，lua_program_end 关联 runId、调用数和收尾。
- 普通诊断记录 Lua 摘要和源码哈希，不复制脚本正文或返回的完整观察。
- 完整 trace 中 lua_tool_result 与原有 Lua/API/IPC 记录按 generation/chat_event_id/tool_call_id/
  lua_run_id 关联。生成源码可包含游戏数据，应按本地诊断资料管理。

## 验证与边界

```powershell
.local/lua-venv/Scripts/python.exe -m unittest discover -s tests -p 'test_lua*.py' -v
.local/lua-venv/Scripts/python.exe -m unittest discover -s tests -v
# 显式调用真实配置的模型，会产生少量 provider 请求/费用，游戏后端仍为模拟：
.local/lua-venv/Scripts/python.exe tests/smoke_lua_model.py --live
```

新增测试验证 HTTP 工具注册/完整源码/结果回传、共享预算、源码保存与脱敏、历史过期、
失败不能被脚本自报成功或后续工具隐藏、物品效果提醒、聊天/本地指令抢占、关闭与未知状态。
新增模型 HTTP 测试替身 → Agent → 真实 Lua Worker → Bridge/TCP → 当前 C# 控制状态、
玩家挂钩与二维导航的集成测试，C# 世界/物理仍为既有替身。

最终完整 Python 回归 **367 项全部通过**，包含上述跨语言集成与第一阶段回归；
续租/结束竞态的确定性用例同时覆盖终态成功/失败、缺释放证据、所有者替换和仍在运行。
日志位于本机 `.local/lua-stage2-python-tests.log`。

2026-10-23 已使用配置的真实 deepseek-flash 完成模拟后端联通：模型提交一份完整 Lua，
三次 API 执行“读选中项→选择快捷键2→重读核对”，再由第二次模型请求解释结果。
最终冒烟为2次模型请求、3次内部 API、含包装共4个工具额度，仅启动1次 select_hotbar_slot。
日志位于 `.local/lua-stage2-live-model-smoke.log`。没有连接或修改真实游戏。
源码与 provider usage 保留在本机，不上传密钥或生成日志。

第三阶段尚需真实游戏验证：单人/多人代表性任务、P/断线/暂停、世界切换、原生物品效果和
长任务中的感知充分性。当前 Worker 仍不是 OS 级敌对代码沙箱；本阶段没有扩充原子能力、
引入 Skill 系统或承诺任意地形/Mod 任务可完成。
