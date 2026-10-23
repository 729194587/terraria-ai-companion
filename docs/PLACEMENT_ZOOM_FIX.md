# 0.1.34：单格方块放置的缩放适配

## 实际失败与原因

真实日志 agent-runtime-20261022-162345-800345.jsonl 确认：脚下(2194,235)土块已被原生挖掘移除；随后快捷键7选择成功、同一格为空；place_tile(2194,235)返回 item_aim_requires_default_zoom，在发出原生使用输入前即被适配器拒绝。

首版为避免错误鼠标注入，对精确目标操作统一加了 Zoom==(1,1) 的保守检查。后续分步核验时仅开放use_item和mine_tile，place_tile仍保留检查。实际问题是这道前置限制过于保守，不是游戏不支持缩放放置，也不是LLM选错这次的格子。

坐标需按阶段区分：LLM传入Tile整数；适配器转换为世界像素的格子中心；游戏在ItemCheck之前通过SetZoom_World/SetZoom_MouseInWorld将物理屏幕鼠标反变换为未缩放的世界相对鼠标坐标。PreItemCheck此时应注入 world-screenPosition，并同步准确的Player.tileTargetX/Y。再乘缩放会重复变换。正常重力的Main.MouseWorld再加screenPosition即可还原世界位置。

## 修复范围

place_tile复用共享ItemAim及原有Before/Aim/RestoreAim/After链路，支持当前缩放。原生PlaceThing → PlaceThing_Tiles → PlaceThing_Tiles_PlaceIt中的普通WorldGen.PlaceTile调用直接读取注入的tileTargetX/Y，主路径没有再次切换缩放或重写目标字段。不会改用直接调用WorldGen.PlaceTile完成动作；这些原生方法仅供IL契约核验。

仍只支持80像素内、正常重力、选中的原版消耗型非frame-important单格方块、空目标。仍由原生ItemCheck决定资源、支撑和碰撞条件，只有预期Tile出现且物品减少才成功。多格家具、墙及ModItem自定义放置没有扩展。actionId/session/generation、续租、取消及鼠标/Tile状态恢复不变。攻击操作保留默认缩放限制，未把本次普通方块验证泛化到所有武器和弹道。

## 验证

- C#适配器：1/1.25/1.5/2倍缩放及非零摄像机位置；准确Tile及鼠标注入、PostItemCheck恢复和输入释放；只有发送输入不算成功；消耗最后一个方块并出现预期Tile才完成；无消耗或错误Tile均失败；撤销后恢复并阻止迟到输入。
- 原生坐标契约：执行本机tModLoader实际渲染/反变换/MouseWorld的隔离方法体，并检查实际WorldGen.PlaceTile调用的目标参数来源；覆盖本次目标(2194,235)、默认和非默认缩放、摄像机平移、整数舍入。没有启动游戏。
- 命令：pwsh -NoProfile -File tests/run_csharp_tests.ps1；pwsh -NoProfile -File tests/run_native_item_aim_tests.ps1；python -B -m unittest discover -s tests -v。
- 构建：tModLoader -server -build，独立输出 .local/placement-zoom-build/Mods/CompanionBridge.tmod，版本0.1.34。

原生放置效果仍需用户在测试世界验收。当前仅构建，不部署、不启动游戏或真实Agent、不提交推送。日志见.local/placement-zoom-csharp-tests.log、placement-zoom-native-tests.log、placement-zoom-python-tests.log和placement-zoom-build.log。

实际结果：Python264项通过；C#全套和原生坐标/放置目标契约通过；0.1.34构建0错误0警告。产物SHA256：FD2B88CADEB93A9379A6EC9EB377F69466984A86CC9E1B201D1C037A87230E4F。

后续部署：用户保存退出B且进程核验结束后，已备份0.1.33并部署0.1.34至.local/m12-b，源/目标哈希一致。记录.local/deployment-backups/deploy-0.1.34-20261022-163712.json。未启动游戏或Agent，真实放置结果待用户验收。
