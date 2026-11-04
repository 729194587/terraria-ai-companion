# M4.6 环境状态与动态危险

基线为 Draft PR #25 的 `a4d11ed8568a7647912598a125f2b590ae36a763`。构建版本为 0.1.81。

专用服务器的 `observe_scene` 在 L0 和 L1 均返回 `lighting.available=false`，来源为 `dedicated_server`，原因为 `native_light_buffer_not_verified`。该字段表达当前运行环境的可用性，均值、最小值、量纲和阈值留空。旧服务器可能省略 `lighting`，调用方应按未知处理。

钉住的 tModLoader 2026.8.3.0 IL 显示，RGB `Lighting.AddLight(int,int,float,float,float)` 在 `netMode == 2` 时返回。`Lighting.Brightness` 读取活动光照引擎的颜色并乘以全局亮度。`LightTiles` 会设置渲染状态并调用 `ProcessArea`。生产查询未调用这些方法。隔离原生对照在 `dedServ=true`、`netMode=2`、`gameMenu=false` 时尝试读取 7×7 区域，原生 RGB 白光请求前后均在首格抛出 `NullReferenceException`。这验证了该环境与读取路径不可用。AI Map 的火把和遮挡近似仍只用于地图揭示。 [引擎 IL](lighting-il.txt)

`hazards.entities.items` 保留原有范围、列表上限和截断计数。NPC 和敌对弹幕条目带当前包围盒、原生速度、分类依据、`observedGameUpdate` 和 `observationRef`。`observationRef` 绑定场景与槽位，每次查询重新生成。NPC 的 `entityRef` 沿用实例标识。弹幕槽位仅用来定位当前观察条目。

`threat` 使用固定 12 游戏 Tick 的相对匀速包围盒计算。弹幕每 Tick 的推断位移按原生 `extraUpdates + 1` 次子步换算，原始速度单独保留。接触包含边界相切，当前接触为 0，时限内的首个接触可为小数。列表优先返回推断较早的接触，其次按距离排序。`unavailable` 表示输入无法计算，`no_projected_contact` 表示模型在时限内无接触结果。地形、AI 转向、输入、加速度、真实命中判定及伤害规则均不参与此推断。模型调用应核对新鲜度、列表截断与地形危险，再作自己的行动选择。

Tile 几何、静态危险和动态实体仍分别表达。L1 的可选地图叠加和精细几何保持独立。实现范围为结构化观察与只读推断，光照诊断和弹幕注入仅在复制世界的隔离夹具中启用。

定向验证覆盖相对速度、子步换算、恰好在第 12 Tick 接触、超时限、远离、另一个轴无交集、当前重叠和无效速度。实际读器测试还覆盖列表截断时的优先级、观察 ID 轮换、L0 兼容和 L1 的图层分离。C# 场景读器 16479 项断言与原几何 2018 项检查通过，Python 场景、Lua Worker 和接口定义共 18 项定向测试通过。构建零错误、零警告。Python 测试使用当时的工作区运行时文件。

隔离原生测试通过真实 IPC 读取 DeathLaser 的当前事实及接触推断，对照两次观察的标识与几何。弹幕在角色上方，夹具伤害设为 0，原生额外子步为 2。这验证了读链路和分层语义。接近时的预测只做离线验证。

最终包的 15 次区域查询中有 2 次按原软预算部分采样，最大游戏线程投影耗时为 37.27ms。先前每种尺寸仅查询两次，最大矩形两次均为部分采样，未达到旧脚本的完整样本要求。复验将每种尺寸增为五次，保留前次回执及截断证据。Lua Worker 和 AI Map 查询通过。原生导航 1/1 完成，X 从 33630 到 33810.27，用时 89 游戏更新，终态释放证据为 known。图形客户端光照、真人并发、第三方 Mod 及真实命中预测未纳入验收。

```powershell
pwsh -NoProfile -File tests/audit_dedicated_lighting.ps1 -TModLoaderPath .local/m4-3-engine
pwsh -NoProfile -File tests/run_scene_region_tests.ps1
& ./tests/verify_navigation_recovery_native.ps1 `
  -TModLoaderPath 'D:/Agent Project/terraria-ai-companion/.local/m4-3-engine' `
  -WorldPath .local/m4-audit/source.wld `
  -PackagePath .local/m4-6-final-build/Mods/CompanionBridge.tmod `
  -Region -RegionIterations 5 -Environment
```

原生清单、光照探针、查询回执和导航轨迹见 [原始证据](native.zip)。[验证摘要](summary.json) 记录最终包、源世界、观察结果和导航终态。
