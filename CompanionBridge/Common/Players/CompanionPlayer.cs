using Terraria;
using Terraria.GameInput;
using Terraria.ID;
using Terraria.ModLoader;

namespace CompanionBridge.Common.Players;

public sealed class CompanionPlayer : ModPlayer
{
    private bool _lastHasFocus;

    private bool CanControl => Player.whoAmI == Main.myPlayer
        && !Main.dedServ
        && Main.netMode is NetmodeID.SinglePlayer or NetmodeID.MultiplayerClient
        && (Main.netMode != NetmodeID.MultiplayerClient || !Netplay.Disconnect)
        && !Main.gameMenu && Player.active && !Player.dead && !Player.ghost;

    public override void OnEnterWorld()
    {
        CompanionBridge.AutoMoveEnabled = false;
        _lastHasFocus = Main.hasFocus;
        Mod.Logger.Info($"Bridge local player entered: player={Player.whoAmI}, netMode={Main.netMode}, autoMove=false");
    }

    public override void PreUpdate()
    {
        if (Player.whoAmI != Main.myPlayer)
            return;

        if (Main.hasFocus != _lastHasFocus)
        {
            if (CompanionBridge.AutoMoveEnabled)
                Mod.Logger.Info($"Focus changed: focused={Main.hasFocus}, autoMove=true, x={Player.position.X:F1}");
            _lastHasFocus = Main.hasFocus;
        }

        if (!CanControl)
        {
            CompanionBridge.AutoMoveEnabled = false;
        }
    }

    public override void UpdateDead()
    {
        if (Player.whoAmI == Main.myPlayer)
        {
            CompanionBridge.AutoMoveEnabled = false;
        }
    }

    public override void ProcessTriggers(TriggersSet triggersSet)
    {
        if (!CanControl || CompanionBridge.ToggleAutoMoveKeybind?.JustPressed != true)
            return;

        CompanionBridge.AutoMoveEnabled = !CompanionBridge.AutoMoveEnabled;
        Mod.Logger.Info($"Auto move: {(CompanionBridge.AutoMoveEnabled ? "on" : "off")}, player={Player.whoAmI}, netMode={Main.netMode}");
        Main.NewText(CompanionBridge.AutoMoveEnabled
            ? "[CompanionBridge] 自动右移：开启（最多 10 秒）"
            : "[CompanionBridge] 自动右移：关闭");
    }

    public override void PostUpdateRunSpeeds()
    {
        if (!CanControl || !CompanionBridge.AutoMoveEnabled)
            return;

        // Override horizontal input only. Vanilla handles acceleration and collision.
        Player.controlLeft = false;
        Player.controlRight = true;
    }
}
