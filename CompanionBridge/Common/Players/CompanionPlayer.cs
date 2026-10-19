using Terraria;
using Terraria.GameInput;
using Terraria.ID;
using Terraria.ModLoader;

namespace CompanionBridge.Common.Players;

public sealed class CompanionPlayer : ModPlayer
{
    private bool CanControl => Player.whoAmI == Main.myPlayer
        && Main.netMode == NetmodeID.SinglePlayer
        && !Main.gameMenu && Player.active && !Player.dead && !Player.ghost;

    public override void OnEnterWorld() => CompanionBridge.AutoMoveEnabled = false;

    public override void PreUpdate()
    {
        if (Player.whoAmI == Main.myPlayer && !CanControl)
            CompanionBridge.AutoMoveEnabled = false;
    }

    public override void UpdateDead()
    {
        if (Player.whoAmI == Main.myPlayer)
            CompanionBridge.AutoMoveEnabled = false;
    }

    public override void ProcessTriggers(TriggersSet triggersSet)
    {
        if (!CanControl || CompanionBridge.ToggleAutoMoveKeybind?.JustPressed != true)
            return;

        CompanionBridge.AutoMoveEnabled = !CompanionBridge.AutoMoveEnabled;
        Mod.Logger.Info($"Auto move: {(CompanionBridge.AutoMoveEnabled ? "on" : "off")}");
        Main.NewText(CompanionBridge.AutoMoveEnabled
            ? "[CompanionBridge] 自动右移：开启"
            : "[CompanionBridge] 自动右移：关闭");
    }

    public override void SetControls()
    {
        if (!CanControl || !CompanionBridge.AutoMoveEnabled)
            return;

        // Override horizontal input only. Vanilla handles acceleration and collision.
        Player.controlLeft = false;
        Player.controlRight = true;
    }
}
