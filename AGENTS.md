# Terraria AI Companion：Agent 工作入口

## 目标与接手方式

让 AI 通过 Terraria 原生 Player 感知世界、交流并执行游戏操作。以可验证的游戏体验推动迭代。
本文件保存稳定边界和检索入口；接口细节、实验过程和验收状态留在对应源码与专题文档。

- 从当前任务相关文件、符号和测试开始，用 `rg` 定向检索；不要每次通读全部文档。
- 先看 `git status --short`，保留已有修改、未跟踪实验和原始证据；不顺手清理工作区。
- 用户给出的具体设计视为已决定，除非实现暴露实质冲突，不重复架构探索。
- 优先直接实现；仅在复杂度、风险或明确要求需要时使用额外技能、子 Agent 或独立评审。
- README、`docs/DEVELOPMENT.md`、`docs/ENGINEERING.md` 混有历史阶段描述；核对适用版本和后续专题。
- 当前实现看源码，包版本看 `CompanionBridge/build.txt`，运行能力看实际握手；实测结论看对应证据。
- 文档冲突时区分“实现了什么”和“验证过什么”，必要时用相关路径的 `git log` / `git show` 查原因。

## 架构地图

当前同时保留客户端控制与服务器独立 AI 两条路径，不能把任一路径的能力推广到另一条。

| 职责 | 实现入口与按需资料 |
| --- | --- |
| 模型、聊天轮次、取消与回复 | `python/agent.py`、`python/llm.py`；`docs/PLAIN_LOCAL_CHAT.md`、`docs/G84_SERVER_AI_AGENT.md` |
| Lua 编排、隔离 Worker、预算 | `python/lua_agent.py`、`python/lua_runtime.py`、`python/lua_worker.py`；`docs/LUA_RUNTIME.md`、`docs/LUA_OBSERVABILITY.md` |
| 原子 API Schema、校验、分发和结果核验 | `python/atomic_operations.py`；`docs/ATOMIC_OPERATIONS.md`、`docs/ATOMIC_FOUNDATIONS.md` |
| 客户端 TCP 与本地玩家控制 | `python/control.py`、`CompanionBridge/TcpControlServer.cs`、`CompanionBridge/ControlState.cs`、`CompanionBridge/Common/Players/CompanionPlayer.cs`、`CompanionBridge/Common/Systems/CompanionSystem.cs` |
| 服务器 AI 绑定、IPC 与动作适配 | `python/server_ai.py`、`CompanionBridge/ServerIpc/`；`docs/SERVER_AI_ACTIONS.md`、`docs/G85_SERVER_AI_PERCEPTION.md` |
| 无客户端的原生 Player、存档、同步与引擎挂钩 | `CompanionBridge/ServerPlayerRuntime/`；按问题读 `docs/VIRTUAL_PLAYER_*.md` |
| 游戏感知与共享查询 | `CompanionBridge/Common/Systems/` 下 `TerrainReader.cs`、`ObservationReader.cs`、`EntityReader.cs`、`AtomicQueries.cs`；`docs/OBSERVATION.md` |
| 客户端原子动作、背包及多人权威确认 | 同目录 `AtomicOperations.cs`、`AtomicInventory.cs`、`AtomicMultiplayer.cs`；`docs/INVENTORY_OPERATIONS.md`、`docs/MULTIPLAYER_ATOMIC_CONFIRMATION.md` |
| 物理预测与导航执行 | `CompanionBridge/Motion/`；`docs/TERRABLIND_NAVIGATION.md`、`docs/NAVIGATION_LANDING_CONFIRMATION.md` |
| 探索编排与模型状态投影 | `python/exploration.py`、`python/lua/explore.lua`、`python/observation.py`；`docs/AUTONOMOUS_EXPLORATION_MVP.md`、`docs/MODEL_STATE_PROJECTION.md` |

服务器模式的主链路是：本地聊天 → 客户端聊天 Bridge → 原 Agent / `execute_lua` → 原子分发器 → `ServerAiBridge` → 游戏线程 → 独立 AI Player。
聊天通道默认 `18777`，AI IPC 默认 `18779`，游戏联机通常 `7777`；多个实例须核对端口与同一世界的绑定。
服务器模式只向模型开放 `execute_lua`，Lua API 按当前后端能力白名单过滤；客户端完整 API 列表不是服务器能力承诺。
`experiments/virtual-player-engine-hooks/` 保留独立探针与验证来源；正式集成位于 `CompanionBridge/ServerPlayerRuntime/`，仍有历史命名空间 `VirtualPlayerEngineProbe`。

## 修改时必须保持的边界

- 网络线程处理协议、纯数据和队列；Terraria Player / Tile / Collision 操作留在游戏线程。模型推理和网络等待不得阻塞游戏更新。
- 服务器读写必须针对绑定 Actor，保持 world / generation / slot / 对象身份检查；不得回退到 `Main.LocalPlayer` 或操纵真人补偿失败。
- 保留控制租约、动作归属、取消与生命周期清理；旧连接、旧代次、旧聊天轮次不得恢复动作或发布迟到回复。
- 服务器连接异常或结果未知时不自动重连、重放动作；Lua 后续变更不得绕过既有失败与 unknown 防护。
- 接受请求、调用原生函数、播放动画均不等于动作完成。保留终态、游戏线程输入释放证据及各操作的效果核验。
- `use_item` 的使用周期完成不证明命中；导航零预测误差不证明已经落地；`unknown` 不能包装成成功。
- 原子操作表达单一意图，组合任务优先复用 Lua；不要在底层悄悄加入走近、选邻块或失败重试等策略。
- 游戏行为沿用已核验的原生输入、运动、使用与挖掘路径；不要用直接改位置、速度或世界格子掩盖执行问题。诊断 fixture 的状态注入不能成为正式执行路径。
- 服务器运行时依赖精确引擎版本、DLL 指纹和隔离条件，见 `ServerPlayerRuntime/EngineHooks.cs`；未知版本拒绝启用，不删除检查来“兼容”。
- 导航适用性、公差、危险判定和 IL 挂钩的变更须有针对性证据；不要仅放宽阈值让失败消失。保留 `CompanionBridge/ThirdParty/TerraBlind/NOTICE.md` 的来源和范围。

## 感知与接口设计

- 优先读取任务所需的结构化状态；模型投影可以缩减体积，但不能丢弃身份、时效、未知性和安全字段。
- 世界像素与 Tile 坐标分开：1 Tile = 16 像素，X 向右、Y 向下；明确使用左上角、中心还是脚底。快捷栏按游戏键 `1..9,0`，不要混同内部槽位。
- 局部地形是碰撞分类，不是材质地图；具体内容用 `scan_tiles` / `inspect_tile`。`resolve_content` 查询注册表，不证明附近存在该内容。
- `supported` 表示运动 profile 适用性，不是脚底支撑或路径安全；`complete`、`truncated` 和未知覆盖各自独立。
- 查询是有范围、有采样时间的观察；多次 API 调用不是同一快照，实体可见也不保证可达、可攻击或可拾取。
- 增加 API 时同时检查 Schema、后端白名单、C# 参数/协议、模型契约和结果核验；避免向模型宣称执行端未支持的能力。
- Numen 是原子化与脚本组合的参考，不是本项目已经实现的完整架构；外部思路须与现有执行链和实际任务匹配。

## 开发与定向验证

以下命令在仓库根目录的 PowerShell 执行。Python 3.10+；Lua 依赖见 `python/requirements.txt`（含固定版本 lupa）。
缺少环境时再创建 `.local/lua-venv` 并安装 requirements；已有环境可复用。模型配置参照 `.env.example`，不输出或提交 `.env` 密钥。

```powershell
# 仅在需要初始化环境时执行
python -m venv .local/lua-venv
& .local/lua-venv/Scripts/python.exe -m pip install -r python/requirements.txt
# 示例：按改动选择测试文件，不是每次必跑的清单
& .local/lua-venv/Scripts/python.exe -B -m unittest discover -s tests -p 'test_server_ai_backend.py' -v
pwsh -NoProfile -File tests/run_csharp_tests.ps1 -NavigationOnly
pwsh -NoProfile -File tests/run_server_ai_ipc_tests.ps1
# 游戏端改动需要构建；只生成包，不部署
pwsh -NoProfile -File scripts/build-companion.ps1 -OutputPath .local/agent-build
```

- **不要运行全量测试。** Python 按文件或具体测试选择；C# 脚本有 `-BridgeOnly`、`-InventoryOnly`、`-ServerReadsOnly`、`-LandingOnly`、`-NavigationOnly` 等入口，先检查参数与覆盖范围。
- C# 离线 harness 使用 PowerShell 7.4+ / .NET 8+，部分测试使用 localhost、进程 Worker 或碰撞替身；失败先区分环境问题与产品回归。
- 原生物品/IL 变更按需用 `tests/run_server_ai_tool_tests.ps1` 或 `tests/run_native_item_aim_tests.ps1`；它们依赖已安装的 tModLoader DLL。
- 构建脚本使用 tModLoader 自带 dotnet 和 `tModLoader.dll -server -build`；默认安装路径 `D:/steam/steamapps/common/tModLoader`，可用 `-TModLoaderPath` 覆盖。
- 上述构建产物是 `.local/agent-build/Mods/CompanionBridge.tmod`；构建成功不代表当前游戏加载了它。
- 游戏验证先阅读相关 `tests/verify_server_ai_*native.ps1` 的参数与副作用，再选择隔离原生场景；不是普通离线单元测试。
- Host & Play 用 `scripts/prepare-companion-host-play.ps1 -PackagePath .local/agent-build/Mods/CompanionBridge.tmod` 创建新 profile；需要旧存档时显式指定已确认的 `-SeedProfile`。
- 该脚本只准备环境；运行其打印的 launcher 后还需进入 Host & Play。服务器 AI 启用与端口由 launcher 设置，不能仅加载 Mod 就假设 AI 已运行。
- 进入世界后可用 `scripts/start-server-ai-agent.ps1` 启动 Agent；启动前核对包、日志、profile 和两个 Bridge 的实际能力，脚本提示里的历史版本号不代表当前包。
- 分别报告源码/IL 检查、离线测试、原生隔离验证、图形实机与远端观察的结果；只声明确实执行的场景。文档修改通常只需路径、命令和差异检查。
- 完成相关验证后总结改动、依据与剩余限制并停止，不反复跑未变化的测试或顺带重构。

## 从实际使用积累知识

- 工具问题先追踪用户任务、观察结果、Lua 程序、动作回执与停止原因，区分信息缺失、接口难用、反馈不足、原生适配缺失和策略问题。
- 先判断能否由现有 API 编排解决，再选择最小改进；新 API 或更长提示词都不是默认答案。复测尽量使用同一任务和可比较的场景。
- 值得保存的是反复踩坑的非显然约束、架构取舍、失败根因、可复现验证方法和已证实的工程经验；不逐条存储聊天、临时猜想或例行操作。
- 持久化时明确标记：**观察**（实际记录）、**假设**（待验证解释）、**实验结果**（版本/场景/步骤/证据）、**已验证经验**（适用范围与反例）。不要把单次结果升级成普遍保证。
- 重要决策简记问题、选择、理由、否决的替代方案、证据和剩余限制；改变决策时说明何处被新证据取代。
- 优先更新现有专题文档；跨模块且稳定的理由适合 `docs/ENGINEERING.md`，环境与操作经验适合 `docs/DEVELOPMENT.md`，接口行为留在对应专题。
- 运行诊断可从 `python/agent_trace.py`、`python/agent_diagnostics.py`、`python/evidence.py` 与 `docs/LUA_OBSERVABILITY.md` 查起；本地临时产物放 `.local/`。
- 需要共享的精选证据可沿用 `docs/evidence/` 的任务、Lua、原始回执、manifest 与结论组织方式；注明包版本或哈希、场景和来源，不改写原始证据来配合结论。
- 新发现的工作流改进先提出记录建议；在任务范围允许且有证据时沉淀。若用户限制改动文件，只在交付说明中建议，不额外写文件。
- 本文件只增加跨任务高频且稳定的规则或导航；细节增长时移入专题并保留链接，不为建立体系批量创建文档。

## 自主执行与讨论边界

- 可自主进行任务相关的检索、局部修复、定向测试、隔离构建和已有契约内的实现；已获授权的动作不重复索要确认。
- 改变控制对象/信任边界、绕过身份或撤销保护、破坏协议兼容、替换执行架构、扩展引擎支持范围等重大取舍，若不在已确认设计内，先带具体理由与方案讨论。
- 构建不隐含覆盖正在使用的包或存档；启动/停止用户游戏、部署到现用 profile、执行游戏动作或收费模型请求，应在任务授权范围内。优先新建隔离测试 profile，保留旧配置。
- 未跟踪草稿、旧实验的阶段禁令和聊天中的猜测，不自动成为长期规范；不确定的设计意图应明确提出，而非写成已确认规则。
