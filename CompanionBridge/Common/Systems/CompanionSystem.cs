using System;
using System.Diagnostics;
using Microsoft.Xna.Framework;
using MonoMod.Cil;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using CompanionBridge.Motion;

namespace CompanionBridge.Common.Systems;

public sealed class CompanionSystem : ModSystem
{
    private TimeSpan? _previousInactiveSleepTime;
    private ControlMode _lastMode;
    private bool _wasCanControl;
    private bool _wasInWorld;

    // Called only from DoUpdate on the game thread, never from the TCP tasks.
    private static PlayerSnapshot ReadSnapshot()
    {
        if (Main.dedServ || Main.gameMenu
            || Main.netMode is not (NetmodeID.SinglePlayer or NetmodeID.MultiplayerClient)
            || (Main.netMode == NetmodeID.MultiplayerClient && Netplay.Disconnect)
            || !Main.LocalPlayer.active)
            return PlayerSnapshot.NotInWorld;

        var player = Main.LocalPlayer;
        return new PlayerSnapshot(true, new StateVector(player.position.X, player.position.Y),
            new StateVector(player.velocity.X, player.velocity.Y), player.statLife, player.statLifeMax2);
    }

    private static bool CanControl => !Main.dedServ
        && Main.netMode is NetmodeID.SinglePlayer or NetmodeID.MultiplayerClient
        && (Main.netMode != NetmodeID.MultiplayerClient || !Netplay.Disconnect)
        && !Main.gameMenu
        && Main.LocalPlayer.active && !Main.LocalPlayer.dead && !Main.LocalPlayer.ghost;

    private static bool KeepSimulationRunning => CompanionBridge.AutoMoveEnabled && CanControl;

    public override void Load()
    {
        if (Main.dedServ)
            return;

        IL_Main.DoUpdate += BypassUnfocusedPause;
        On_Main.DoUpdate += UpdateExperiment;
    }

    private void BypassUnfocusedPause(ILContext il)
    {
        var cursor = new ILCursor(il);
        // Match only the early single-player focus pause, not the stored focus value.
        if (!cursor.TryGotoNext(MoveType.Before,
            i => i.MatchLdsfld(typeof(Main), nameof(Main.hasFocus)),
            i => i.MatchBrtrue(out _),
            i => i.MatchLdsfld(typeof(Main), nameof(Main.netMode)),
            i => i.MatchBrtrue(out _)))
            throw new InvalidOperationException("CompanionBridge: unsupported DoUpdate focus-pause branch.");

        cursor.Index++;
        cursor.EmitDelegate<Func<bool, bool>>(focused => focused
            || (Main.netMode == NetmodeID.SinglePlayer && KeepSimulationRunning));
    }

    private void UpdateExperiment(On_Main.orig_DoUpdate orig, Main self, ref GameTime gameTime)
    {
        // Runs before the vanilla focus early-return, including while unfocused.
        bool canControl = CanControl;
        var snapshot = ReadSnapshot();
        if (_wasInWorld && !snapshot.InWorld) CompanionBridge.StopControl();
        else if (_wasCanControl && !canControl) CompanionBridge.RevokeControl(snapshot);
        _wasInWorld = snapshot.InWorld;
        _wasCanControl = canControl;
        CompanionBridge.Controls.Update(canControl, Stopwatch.GetTimestamp(), snapshot,
            snapshot.InWorld ? TerrainReader.Read(Main.LocalPlayer) : null, GameNavigation.Snapshot, ChatBridgeSystem.Send,
            snapshot.InWorld ? EntityReader.Read(Main.LocalPlayer) : null, ObservationReader.Read);
        var mode = CompanionBridge.Controls.Mode;
        if (mode != _lastMode)
        {
            Mod.Logger.Info($"Control mode: {_lastMode} -> {mode}");
            _lastMode = mode;
        }

        UpdateSleepSetting(self);
        try
        {
            orig(self, ref gameTime);
        }
        finally
        {
            UpdateSleepSetting(self);
        }
    }

    private void UpdateSleepSetting(Main game)
    {
        if (KeepSimulationRunning)
        {
            _previousInactiveSleepTime ??= game.InactiveSleepTime;
            game.InactiveSleepTime = TimeSpan.Zero;
        }
        else if (_previousInactiveSleepTime is TimeSpan previous)
        {
            game.InactiveSleepTime = previous;
            _previousInactiveSleepTime = null;
        }
    }

    public override void OnWorldLoad() => ObservationReader.EnterWorld();

    public override void PostDrawTiles() => ObservationReader.CaptureCamera();

    public override void OnWorldUnload()
    {
        ObservationReader.LeaveWorld();
        CompanionBridge.StopControl();
        UpdateSleepSetting(Main.instance);
    }

    public override void Unload()
    {
        ObservationReader.LeaveWorld();
        CompanionBridge.StopControl();
        if (Main.dedServ)
            return;

        UpdateSleepSetting(Main.instance);
        On_Main.DoUpdate -= UpdateExperiment;
        IL_Main.DoUpdate -= BypassUnfocusedPause;
    }
}
