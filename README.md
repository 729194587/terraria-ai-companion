# Terraria AI Companion

目标是让独立 tModLoader 客户端中的真实玩家由 AI 控制，未来再接入 Python Agent。当前仅实现 **M1.0：本地玩家自动右移验证**。

## 当前功能

- 在单人世界中按 **P** 切换自动右移；可在“设置 → 控制 → Mod 控制”中重新绑定。
- 默认关闭。开启时覆盖水平输入，关闭时不再写入控制字段，由原生机制减速；手动右移仍会继续生效。
- 进入世界、离开世界、死亡和卸载时清除自动模式。不保存开关状态。
- 不修改位置或速度，不创建 NPC 或额外 Player。

**验证状态：已用本机 tModLoader 2026.08.3.0 编译，0 错误、0 警告。游戏内加载、快捷键、移动及停止行为尚待人工测试。**

## 构建和测试

当前安装目录为 `D:\steam\steamapps\common\tModLoader`。在 PowerShell 中执行：

```powershell
$tml = 'D:\steam\steamapps\common\tModLoader'
$repo = 'D:\Agent Project\terraria-ai-companion'
Push-Location $tml
try {
    & "$tml\dotnet\dotnet.exe" "$tml\tModLoader.dll" -server -build "$repo\CompanionBridge" -tmlsavedirectory "$repo\.local\tmodloader"
} finally {
    Pop-Location
}
```

产物为 `.local/tmodloader/Mods/CompanionBridge.tmod`。使用隔离的测试配置启动游戏，不使用原有角色或存档：

```powershell
Push-Location $tml
try {
    & "$tml\start-tModLoader.bat" -tmlsavedirectory "$repo\.local\tmodloader"
} finally {
    Pop-Location
}
```

确认 CompanionBridge 已启用，新建测试角色和单人测试世界，在安全平地按 P，观察向右移动；再次按 P，松开方向键，观察减速停止。再测试 A/D 手动移动、退出重进和死亡复活。完整验收表见 [开发说明](docs/DEVELOPMENT.md)。

## 范围和限制

仅支持前台单人验证；自动模式只接管左右输入，其他手动输入保留。墙壁、地形和原生状态效果仍可能阻止移动。自动模式开启时不要在危险地形测试；输入文字时不会通过快捷键切换模式，但本原型没有单独暂停自动移动的聊天逻辑。

尚未实现或验证：第二客户端、联机、失焦运行、Python/IPC、LLM、寻路、战斗、背包和装备操作。
