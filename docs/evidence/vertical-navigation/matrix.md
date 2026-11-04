# 最终包逐场原生矩阵

世界像素，X 为玩家左边缘，Y 为脚底；方向 1=右、-1=左。每个交替高度场景连续执行四动作，中间不重置。
规划栏记录终段局部计划：完整表示已找到可行落脚路线，不承诺最优；原始中间计划见 summary.json 的 observedPlans。失败场景可先执行安全部分路线。

| 场景 / 方向 / 段 | 目标 X / 脚底 Y | 规划终段 | 实际 X / 脚底 Y | 终态 | 释放 | 原因 |
| --- | --- | --- | --- | --- | --- | --- |
| flat / 1 / 1 | [33712, 33780] / 3504 | 完整；7扩展；未耗尽时间预算 | 33723.754 / 3504.000 | completed | known / released | 区域、支撑与自然停稳已核验 |
| flat / -1 / 1 | [33440, 33524] / 3504 | 完整；1扩展；未耗尽时间预算 | 33520.690 / 3504.000 | completed | known / released | 区域、支撑与自然停稳已核验 |
| step_up / 1 / 1 | [33712, 33780] / 3488 | 完整；4扩展；未耗尽时间预算 | 33723.754 / 3488.000 | completed | known / released | 区域、支撑与自然停稳已核验 |
| step_up / -1 / 1 | [33440, 33524] / 3488 | 完整；4扩展；未耗尽时间预算 | 33504.246 / 3488.000 | completed | known / released | 区域、支撑与自然停稳已核验 |
| step_down / 1 / 1 | [33712, 33780] / 3520 | 完整；7扩展；未耗尽时间预算 | 33723.754 / 3520.000 | completed | known / released | 区域、支撑与自然停稳已核验 |
| step_down / -1 / 1 | [33440, 33524] / 3520 | 完整；1扩展；未耗尽时间预算 | 33520.690 / 3520.000 | completed | known / released | 区域、支撑与自然停稳已核验 |
| slope_up / 1 / 1 | [33744, 33780] / 3440 | 完整；6扩展；未耗尽时间预算 | 33748.746 / 3440.000 | completed | known / released | 区域、支撑与自然停稳已核验 |
| slope_up / -1 / 1 | [33440, 33476] / 3440 | 完整；1扩展；未耗尽时间预算 | 33470.973 / 3440.000 | completed | known / released | 区域、支撑与自然停稳已核验 |
| slope_down / 1 / 1 | [33744, 33780] / 3568 | 完整；1扩展；未耗尽时间预算 | 33761.508 / 3568.000 | completed | known / released | 区域、支撑与自然停稳已核验 |
| slope_down / -1 / 1 | [33440, 33476] / 3568 | 完整；1扩展；未耗尽时间预算 | 33466.492 / 3568.000 | completed | known / released | 区域、支撑与自然停稳已核验 |
| short_gap / 1 / 1 | [33728, 33780] / 3504 | 完整；14扩展；时间预算退出 | 33729.040 / 3504.000 | completed | known / released | 区域、支撑与自然停稳已核验 |
| short_gap / -1 / 1 | [33440, 33492] / 3504 | 完整；17扩展；未耗尽时间预算 | 33479.130 / 3504.000 | completed | known / released | 区域、支撑与自然停稳已核验 |
| low_obstacle / 1 / 1 | [33712, 33780] / 3504 | 完整；14扩展；时间预算退出 | 33760.156 / 3504.000 | completed | known / released | 区域、支撑与自然停稳已核验 |
| low_obstacle / -1 / 1 | [33440, 33508] / 3504 | 完整；20扩展；时间预算退出 | 33467.844 / 3504.000 | completed | known / released | 区域、支撑与自然停稳已核验 |
| platform_up / 1 / 1 | [33712, 33780] / 3440 | 完整；13扩展；未耗尽时间预算 | 33737.430 / 3440.000 | completed | known / released | 区域、支撑与自然停稳已核验 |
| platform_up / -1 / 1 | [33440, 33508] / 3440 | 完整；12扩展；未耗尽时间预算 | 33490.570 / 3440.000 | completed | known / released | 区域、支撑与自然停稳已核验 |
| platform_down / 1 / 1 | [33712, 33780] / 3504 | 完整；6扩展；未耗尽时间预算 | 33738.020 / 3504.000 | completed | known / released | 区域、支撑与自然停稳已核验 |
| platform_down / -1 / 1 | [33440, 33508] / 3504 | 完整；6扩展；未耗尽时间预算 | 33489.980 / 3504.000 | completed | known / released | 区域、支撑与自然停稳已核验 |
| high_platform / 1 / 1 | [33712, 33780] / 3312 | 未达目标；16扩展；时间预算退出 | 33658.918 / 3504.000 | failed | known / released | no safe local route: search time budget reached; jump descent exceeds 64px limit (59 candidates); no safe landing/progress (49 candidates); outside route span (16 candidates) |
| high_platform / -1 / 1 | [33440, 33508] / 3312 | 未达目标；13扩展；时间预算退出 | 33569.082 / 3504.000 | failed | known / released | no safe local route: search time budget reached; jump descent exceeds 64px limit (56 candidates); no safe landing/progress (34 candidates); outside route span (14 candidates) |
| obstacle_one / 1 / 1 | [33728, 33788] / 3504 | 完整；1扩展；未耗尽时间预算 | 33740.355 / 3504.000 | completed | known / released | 区域、支撑与自然停稳已核验 |
| obstacle_one / -1 / 1 | [33440, 33500] / 3504 | 完整；1扩展；未耗尽时间预算 | 33487.645 / 3504.000 | completed | known / released | 区域、支撑与自然停稳已核验 |
| solid_up_three / 1 / 1 | [33728, 33788] / 3456 | 完整；13扩展；时间预算退出 | 33732.680 / 3456.000 | completed | known / released | 区域、支撑与自然停稳已核验 |
| solid_up_three / -1 / 1 | [33440, 33500] / 3456 | 完整；18扩展；时间预算退出 | 33495.320 / 3456.000 | completed | known / released | 区域、支撑与自然停稳已核验 |
| solid_up_five / 1 / 1 | [33728, 33788] / 3424 | 完整；13扩展；时间预算退出 | 33732.844 / 3424.000 | completed | known / released | 区域、支撑与自然停稳已核验 |
| solid_up_five / -1 / 1 | [33440, 33500] / 3424 | 完整；1扩展；未耗尽时间预算 | 33491.016 / 3424.000 | completed | known / released | 区域、支撑与自然停稳已核验 |
| shaft_up / 1 / 1 | [33620, 33624] / 3440 | 完整；1扩展；未耗尽时间预算 | 33622.000 / 3440.000 | completed | known / released | 区域、支撑与自然停稳已核验 |
| shaft_up / -1 / 1 | [33604, 33608] / 3440 | 完整；1扩展；未耗尽时间预算 | 33606.000 / 3440.000 | completed | known / released | 区域、支撑与自然停稳已核验 |
| shaft_ceiling / 1 / 1 | [33620, 33624] / 3440 | 未达目标；1扩展；未耗尽时间预算 | 33622.000 / 3504.000 | failed | known / released | no safe local route: no safe landing/progress (26 candidates); walking blocked (2 candidates) |
| shaft_ceiling / -1 / 1 | [33604, 33608] / 3440 | 未达目标；1扩展；未耗尽时间预算 | 33606.000 / 3504.000 | failed | known / released | no safe local route: no safe landing/progress (26 candidates); walking blocked (2 candidates) |
| alternating / 1 / 1 | [33712, 33788] / 3440 | 完整；12扩展；时间预算退出 | 33737.430 / 3440.000 | completed | known / released | 区域、支撑与自然停稳已核验 |
| alternating / 1 / 2 | [33840, 33916] / 3472 | 完整；1扩展；未耗尽时间预算 | 33846.516 / 3472.000 | completed | known / released | 区域、支撑与自然停稳已核验 |
| alternating / 1 / 3 | [33712, 33788] / 3440 | 完整；17扩展；时间预算退出 | 33785.523 / 3440.000 | completed | known / released | 区域、支撑与自然停稳已核验 |
| alternating / 1 / 4 | [33584, 33644] / 3504 | 完整；2扩展；未耗尽时间预算 | 33641.305 / 3504.000 | completed | known / released | 区域、支撑与自然停稳已核验 |
| alternating / -1 / 1 | [33440, 33516] / 3440 | 完整；10扩展；时间预算退出 | 33490.570 / 3440.000 | completed | known / released | 区域、支撑与自然停稳已核验 |
| alternating / -1 / 2 | [33312, 33388] / 3472 | 完整；1扩展；未耗尽时间预算 | 33381.484 / 3472.000 | completed | known / released | 区域、支撑与自然停稳已核验 |
| alternating / -1 / 3 | [33440, 33516] / 3440 | 完整；14扩展；时间预算退出 | 33442.477 / 3440.000 | completed | known / released | 区域、支撑与自然停稳已核验 |
| alternating / -1 / 4 | [33584, 33644] / 3504 | 完整；2扩展；未耗尽时间预算 | 33586.695 / 3504.000 | completed | known / released | 区域、支撑与自然停稳已核验 |
| no_landing / 1 / 1 | [33728, 33788] / 3440 | 未达目标；12扩展；时间预算退出 | 33740.910 / 3504.000 | failed | known / released | no safe local route: search time budget reached; no safe landing/progress (75 candidates); outside route span (55 candidates); walking blocked (3 candidates) |
| no_landing / -1 / 1 | [33440, 33500] / 3440 | 未达目标；12扩展；时间预算退出 | 33487.090 / 3504.000 | failed | known / released | no safe local route: search time budget reached; no safe landing/progress (79 candidates); outside route span (55 candidates); walking blocked (4 candidates) |
| descent_five / 1 / 1 | [33728, 33788] / 3504 | 未达目标；23扩展；时间预算退出 | 33658.918 / 3424.000 | failed | known / released | no safe local route: search time budget reached; no safe landing/progress (81 candidates); jump descent exceeds 64px limit (67 candidates); outside route span (52 candidates) |
| descent_five / -1 / 1 | [33440, 33500] / 3504 | 未达目标；19扩展；时间预算退出 | 33569.082 / 3424.000 | failed | known / released | no safe local route: search time budget reached; jump descent exceeds 64px limit (67 candidates); no safe landing/progress (61 candidates); outside route span (27 candidates) |
| hazard_target / 1 / 1 | [33728, 33788] / 3504 | 未达目标；8扩展；时间预算退出 | 33674.875 / 3504.000 | failed | known / released | no safe local route: search time budget reached; no safe landing/progress (29 candidates); unknown/hazard terrain at (2107,218) (16 candidates); outside route span (11 candidates) |
| hazard_target / -1 / 1 | [33440, 33500] / 3504 | 未达目标；15扩展；时间预算退出 | 33553.125 / 3504.000 | failed | known / released | no safe local route: search time budget reached; no safe landing/progress (57 candidates); outside route span (52 candidates); unknown/hazard terrain at (2095,218) (15 candidates) |
