using Terraria;
using Terraria.GameInput;
using Terraria.ID;
using Terraria.ModLoader;
using CompanionBridge.Motion;
using CompanionBridge.Common.Systems;

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
        CompanionBridge.StopControl();
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

        if (!CanControl && CompanionBridge.AutoMoveEnabled)
        {
            CompanionBridge.RevokeControl();
        }
    }

    public override void UpdateDead()
    {
        if (Player.whoAmI == Main.myPlayer && CompanionBridge.AutoMoveEnabled)
        {
            CompanionBridge.RevokeControl();
        }
        if (Player.whoAmI == Main.myPlayer)
            CompanionBridge.Controls.ConfirmMovementInputReleased(ObservationReader.WorldSessionId,
                Main.GameUpdateCount, "UpdateDead_after_revoke", Player.dead || Player.ghost);
    }

    public override void ProcessTriggers(TriggersSet triggersSet)
    {
        if (!CanControl || CompanionBridge.ToggleAutoMoveKeybind?.JustPressed != true)
            return;

        CompanionBridge.ToggleManual();
        Mod.Logger.Info($"Auto move: {(CompanionBridge.AutoMoveEnabled ? "on" : "off")}, player={Player.whoAmI}, netMode={Main.netMode}");
        Main.NewText(CompanionBridge.AutoMoveEnabled
            ? "[CompanionBridge] 自动右移：开启（最多 10 秒）"
            : "[CompanionBridge] 自动右移：关闭");
    }

    public override void PostUpdateRunSpeeds()
    {
        if (Player.whoAmI == Main.myPlayer)
            CompanionBridge.Controls.ConfirmMovementInputReleased(ObservationReader.WorldSessionId,
                Main.GameUpdateCount, "PostUpdateRunSpeeds_before_bridge_input",
                !Player.controlLeft && !Player.controlRight && !Player.controlJump);
        if (!CanControl || !CompanionBridge.AutoMoveEnabled)
        {
            GameNavigation.Cancel();
            return;
        }

        var input = CompanionBridge.Controls.Input;
        if (input.Predictive)
        {
            var predicted = GameNavigation.Before(Player, CompanionBridge.Controls.SessionId, input.Direction,
                CompanionBridge.Controls.Generation, input.TargetX);
            Player.controlLeft = predicted.Direction < 0;
            Player.controlRight = predicted.Direction > 0;
            Player.controlJump = predicted.Jump;
            Player.controlDown = false;
            Player.controlUp = false;
            return;
        }
        GameNavigation.Cancel();
        Player.controlLeft = input.Direction < 0;
        Player.controlRight = input.Direction > 0;
        // Verified against 2026.08.3.0 Player.Update: this hook precedes JumpMovement,
        // with no intervening controlJump write. PreUpdateMovement is too late.
        // Legacy move_right and manual P retain their original human jump behavior.
        if (input.OverrideJump)
        {
            Player.controlJump = input.Jump;
            Player.controlDown = false; // Do not drop through one-way platforms.
        }
    }

    public override void PostUpdate()
    {
        if (CanControl && CompanionBridge.Controls.Mode == ControlMode.External
            && CompanionBridge.Controls.Input.Predictive) GameNavigation.After(Player);
    }
}
