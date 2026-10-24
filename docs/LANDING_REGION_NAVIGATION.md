# 二维落脚导航入口（0.1.31）

新增 `navigate_to_landing_region`，从 LLM 接入已有二维落脚导航。保留 `navigate_to_x`、旧 TCP `navigate` 和 `navigate.py` 的水平目标行为；没有重写规划器、落脚判据或执行器。

## 参数和调用链

```json
{"name":"navigate_to_landing_region","arguments":{"minPlayerLeftX":1664,"maxPlayerLeftX":1744,"surfaceY":1552}}
```

三项都是有限的**世界像素**，Y 向下；协议范围 0..1000000，实际执行再检查当前世界边界。

- `minPlayerLeftX` / `maxPlayerLeftX`：玩家碰撞框**左边缘**的目标闭区间，要求前者不大于后者，可相等。不是玩家中心，也不是平台两端的 Tile 坐标。
- `surfaceY`：目标水平表面的 Y，即玩家**脚底**高度。普通模型身高 42，最终应 `position.y+42≈surfaceY`，容差 0.5px；不是玩家顶部 Y。
- 例：普通实心方块顶部在第 97 行，`surfaceY=97*16=1552`。X 区间须依据实际平台、角色尺寸和支撑余量选取。示例坐标只用于说明/离线场景，真实世界必须重新观察确认。

实际路径：`agent.TOOLS/dispatch → atomic_operations.dispatch → Bridge/TCP start_atomic → ControlState.TargetRegion → ControlInput.TargetRegion → CompanionPlayer.PostUpdateRunSpeeds → GameNavigation.Before(region: ...) → NavigationTask → StateSpacePlanner.Plan(start, LandingRegion, ...) → LocalMotionController`。

原生更新后，`CompanionPlayer.PostUpdate → GameNavigation.After → NavigationTask.After` 用实际玩家位置、速度、跳跃状态及原生碰撞/支撑反馈验证执行。最终 `LandingRegion.Arrived` 要求 X 区间和脚底高度均满足、着地、安全平顶支撑（完整方块或水平单向平台）、两轴速度绝对值 <=0.1、跳跃释放且计数归零。只经过目标 X 或预测 `reachedGoal=true` 都不等于完成。

## 协议与动作身份

新包握手增加 `landing_region_navigation_v1`；Python 拒绝向仅支持旧原子操作的包发送二维动作。启动沿用现有原子操作：

```json
{"v":1,"id":2,"cmd":"start_atomic","operation":"navigate_to_landing_region","arguments":{"minPlayerLeftX":1664,"maxPlayerLeftX":1744,"surfaceY":1552},"expectedGeneration":7,"chatId":"当前聊天事件ID"}
```

启动回执 `movement.action` 含 `actionId/session/generation/worldSessionId/targetRegion`。Python 检查固定身份和目标，并每约 100ms 读取反馈、续租。续租使用**启动回执中的实际浮点目标**：

```json
{"v":1,"id":3,"cmd":"renew_move","actionId":5,"expectedGeneration":8,"targetRegion":{"minPlayerLeftX":1664,"maxPlayerLeftX":1744,"surfaceY":1552}}
```

数字只是示例，实际使用当前回执。二维续租必须携带正确代次和目标；不匹配返回 `generation_required/generation_changed/target_changed/action_changed`，不延长租约。目标不可在动作中途替换，旧水平/限时移动续租格式保持不变。

复用唯一 actionId、session/generation、750ms 租约、30 秒绝对截止、精确 actionId 取消、新聊天/P/死亡/离开世界/断线撤销，以及游戏线程输入释放证据。续租不延长绝对截止；暂停时即使已有终态，也须等真正的释放挂钩执行，不能凭 ACK 确认完成。

| 返回 `status` | 含义 |
| --- | --- |
| `completed` | 实际状态通过二维抵达判据，且获得同世界的游戏线程 `endEvidence` |
| `failed` | 状态不支持、目标越界、无路、预测偏差/落地核验失败等，查看 `reason` |
| `timeout` | 30 秒截止；或 Python 监督截止且已确认释放 |
| `cancelled` | 动作被停止或控制被撤销，查看 `reason` |
| `unknown` | 通信、身份/目标变化或缺少可信释放证据；不声称成功，不自动重试 |

终态返回 `targetRegion/session/actionGeneration/actionId/controlGeneration/endEvidence`。导航的 `evidence` 保留 `targetRegion/planNumber/actualLanding/lastLanding` 和规划诊断。运行时可查询 `get_navigation`；结束后的证据保存在动作回执。`valid=true` 表示取得可信结果，仍须检查 `status/completed`。

## 真实游戏测试

构建包：`.local/platform-fixes-build/Mods/CompanionBridge.tmod`，版本 **0.1.31**。合并包已备份部署到B测试配置并确认加载；后续重新部署时须先保存退出客户端，重启核对版本及 `atomic_operations_v1`、`landing_region_navigation_v1` 能力。

使用无装备、无异常 Buff、无坐骑/钩爪、正常重力的普通角色，准备有净空的普通实心平顶平台。沿用原有普通角色/局部物理模型限制；水平单向平台可作为最终落点，斜平台、斜坡和半砖仍不作为此平顶目标，远目标或不可达高度允许明确失败。

可启动已有 `python python/agent.py`，让另一玩家在聊天中明确要求落到指定 X 左边缘区间和脚底 Y，检查实际工具调用参数及回执。也可在仓库根目录使用同一个原子入口直接测试（不要与 Agent 同时占用唯一 Bridge 连接），先替换示例坐标：

```powershell
@'
import asyncio, json, sys
sys.path.insert(0, 'python')
from control import Bridge
from atomic_operations import dispatch
async def main():
    with Bridge(timeout=2) as bridge:
        result = await dispatch(bridge, 'navigate_to_landing_region', {
            'minPlayerLeftX': 1664, 'maxPlayerLeftX': 1744, 'surfaceY': 1552})
        print(json.dumps(result, ensure_ascii=False, indent=2))
asyncio.run(main())
'@ | python -
```

分别验证左右上升 32/48px、同 X 错误高度、不可达/支撑不足/低顶、取消、停发/断线、P/新聊天/死亡和暂停。客户端 `Logs/client.log` 应出现 `goalKind=landing_region` 及正确 `targetRegion`，用 session/generation/planNumber 对齐规划、执行与终态；对照画面、脚底位置和停稳及 endEvidence。旧水平工具应继续报告 `goalKind=horizontal`。

## 自动验证与边界

Python 测试覆盖参数/能力、目标回显、固定目标与代次续租、取消和目标变化不得成功。跨语言测试走 **Python Agent（模型回复替身）→真实 Bridge/TCP/ControlState→生产 CompanionPlayer/AtomicOperations→生产 NavigationTask/StateSpacePlanner/LocalMotionController→落地与释放证据**。

C# 测试覆盖目标参数/世界边界、目标不可变、无效续租不延租、绝对超时、代次/互斥、挂钩传参、落脚/停稳、取消及错误高度。游戏世界、物理及原生 GameNavigation 适配层使用离线替身，不能证明真实 Terraria 原生碰撞、后台调度或多人同步已通过。

```powershell
python -B -m unittest discover -s tests -v
pwsh -NoProfile -File tests/run_csharp_tests.ps1
pwsh -NoProfile -File tests/run_csharp_tests.ps1 -LandingOnly
```

本轮验证：Python **242 项通过**（含上述跨语言端到端测试）；C# 全套回归通过；0.1.30 Mod 构建 **0 错误、0 警告**。日志位于 `.local/landing-python-tests.log`、`.local/landing-csharp-tests.log`。未进行真实游戏验证，未部署、提交或推送。

## 0.1.31 木平台抵达修复

真实测试中，目标为 X=[34864,35040]、surfaceY=3712，玩家跳跃后停在 (34870.684,3670)，脚底高度吻合、速度为零；但旧版最终判据只接受 `#`，拒绝脚下 Tile19 水平单向平台（`=`），继而重规划并报告失败。0.1.31 在共享 SafeFlatLanding 中接受 `#` 和 `=`；保留真实着地、净空、危险/未知排除、至少8px支撑、速度与跳跃状态要求。录制坐标回归验证已落地时直接 arrived、不再重规划，并继续拒绝错误高度、空中、非零速度、跳跃未释放和不足支撑。新包尚需加载后复测，不能将该离线回归当成新版实测通过。

0.1.31 合并包还包含快捷栏键号与原生 Down 下穿，详见 [平台与快捷栏修复](PLATFORM_AND_HOTBAR_FIXES.md)。下穿是独立原子动作，不把旧导航绕路当成直接下穿。
