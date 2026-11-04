# M1.1-A Direct Input 验证记录

日期：2026-10-31。实现包 CompanionBridge 0.1.65；最终包 SHA-256：
`2747fd3ac331dc4607a412175b0618112d082bdf9e34c801eeff0b0d9326ea48`。

接口与实现见 [SERVER_AI_ACTIONS.md](../../SERVER_AI_ACTIONS.md) 的 M1.1-A 节。
原始文件无损归档于 raw-evidence.zip；manifest.json 记录每个源路径、ZIP entry、字节数、SHA-256，
并分别保留各次实际运行包哈希。较早构建和宽松断言的结果不能作为最终包严格验收证据。

## 实验结果：隔离原生

最终运行 final-native：真实 Lua Worker → Server AI IPC → 游戏线程自然 Player.Update，
复制世界、新 profile、无客户端、无模型请求；16 组结果全部通过，另有 7 个无效输入在原生协议侧被拒绝。
运行使用原生 Terraria 1.4.4.9 / 本机 tModLoader 安装，继续沿用既有精确引擎指纹门禁，没有扩展支持版本。

- 左右各 30 更新，净位移约 ±37.195px；末 Vx 约 ±2.4，没有人为制动。
- 原地/向右各跳跃 16 更新，均只在前 8 更新按住 Space；末顶部 Y 从3462降至3402.6392。
- 同一个 Lua 程序先左12更新再右18更新；两次开始顶部 Y 分别为3164.4和3254.4，地面站立顶部Y为3462。
  两段开始确实在空中；末水平速度分别为负/正。两段之间没有重置位置或速度。
- 平台边缘向右50更新，末 Y=3516.4、Vy≈6.4，completed且仍在自然下落。
- 墙前90更新正常 completed；继续贴墙20更新得到 noProgress=true，没有 action_unknown。
- Down25更新穿过平台，末Y=3592；Down+Jump（前8更新Jump，总60更新）最低顶部Y约3400.809，
  最后Y=3563.2：**原生组合先向上跳，再在下降时下穿**。不是“按下组合立即下穿”。
- Up2更新按键确实注入；站在普通地面时不产生垂直飞行。
- 中途取消：终态 cancelled、绑定游戏线程输入释放确认，保留非零 Vx。
- ActorControl.Retire：实际 Driver 终止且释放；这是退役入口测试，不是真人在线接管验收。
- IPC 连接关闭：游戏线程取消原动作并释放，保存原 Driver 终态；新连接仅为测试后续场景显式创建。
- NativeSurvival.Run → 原生 KillMe：动作取消、已释放，回执补采 dead=true 和实际生命终态。
- 每段执行数等于请求数，firstInput/lastInput 更新差为 N-1，逐更新方向与跳跃序列核对通过；
  Lua 每段只有一次动作调用，C#内部连续注入，没有逐Tick IPC/模型请求。

legacy-regression：**同最终包**的既有选择、真实Lua导航/使用/挖掘、取消、原生世界效果和进程清理回归通过。
正式包未覆盖现用 profile 或存档；测试服务器都已退出。

## 定向离线及源码/IL检查

- Mod 构建0错误0警告；正式包76项IL检查通过，包含Driver接入、无导航预测/Healthy门禁、无位置/速度赋值。
- Server AI IPC、原子动作、读取、原生适配器替身、聊天协议定向组通过。
  Direct Input覆盖一次start、多次poll、完成/取消/死亡/断连/forget/超时，以及对象/世界/代次/槽位变化不调用旧执行句柄。
  这些身份变化测试使用离线执行器替身，不冒充引擎身份变更实机验收。
- NavigationOnly回归通过（离线碰撞替身），包含原导航落点确认、进展预算、双向运动和玩家hook边界。
- 新增2个Python用例最终通过：真实Worker/dispatcher/loopback、一次start、请求与证据篡改、无释放证明、能力门控、参数边界。
- test_server_ai_backend.py共34项首轮32项通过、2项失败，**不宣称整文件通过**：
  test_perception_contract_is_capability_gated 因当前原有未提交的lua_agent提示词出现scan_tiles而失败；
  test_transport_timeout_after_start_is_unknown在设置socket 0.1秒超时后未发出start，单独复跑仍失败。
  本轮保留原测试和相关未提交文件，没有为使其通过修改提示词、放宽超时或改变产品逻辑。
- 包检查的既有“禁止任何名为Query的方法”断言误拦截服务器AiExplorationMap.Query；
  已限定到真正应禁止的AtomicQueries.Query，LocalPlayer/摄像机/全局玩家检查保持。
- 没有运行全量测试。

## 观察与测试修正

归档保留初次取消脚本对null evidence调用get的错误、Windows夹具信号文件短暂共享冲突，及修正后结果。
早期空中换向只检查末水平速度，后加“第二段开始也须在空中”发现第二次Worker启动期间已自然落地。
最终用同一Worker中的两段Lua，并在Worker启动完成、首次start前才准备一次空中初态，严格断言通过。
执行两段期间不再注入位置/速度，也不暂停原生更新。这些是测试时序修正，不是运动阈值放宽。

## 尚未覆盖

真实图形Host & Play、真人手动接管/在线槽位竞争、远端角色显示与输入释放同步、非致命原生伤害/击退下的完整矩阵、
真实世界切换/角色替换/代次变化、游戏线程完全停止推进的墙钟看门实机、LLM自主选择Direct Input尚未验收。
死亡和断连已做隔离原生测试；身份变化/超时和未知释放保护目前为离线契约及现有引擎边界检查。
坐骑、反重力、失能状态明确拒绝；没有加入Ground Follow、装备/翅膀/钩爪适配、自动越障或恢复策略。
