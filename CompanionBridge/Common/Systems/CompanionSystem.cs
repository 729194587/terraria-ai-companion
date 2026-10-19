using System;
using Microsoft.Xna.Framework;
using MonoMod.Cil;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace CompanionBridge.Common.Systems;

public sealed class CompanionSystem : ModSystem
{
    private TimeSpan? _previousInactiveSleepTime;

    private static bool KeepSimulationRunning => CompanionBridge.AutoMoveEnabled
        && Main.netMode == NetmodeID.SinglePlayer && !Main.gameMenu
        && Main.LocalPlayer.active && !Main.LocalPlayer.dead && !Main.LocalPlayer.ghost;

    public override void Load()
    {
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
        cursor.EmitDelegate<Func<bool, bool>>(focused => focused || KeepSimulationRunning);
    }

    private void UpdateExperiment(On_Main.orig_DoUpdate orig, Main self, ref GameTime gameTime)
    {
        // Runs before the vanilla focus early-return, including while unfocused.
        if (CompanionBridge.ExpireAutoMove())
        {
            Mod.Logger.Info($"Auto move: off (10-second experiment limit), x={Main.LocalPlayer.position.X:F1}");
            Main.NewText("[CompanionBridge] 自动右移：关闭（10 秒实验时限）");
        }

        if (CompanionBridge.AutoMoveEnabled && !KeepSimulationRunning)
            CompanionBridge.AutoMoveEnabled = false;

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

    public override void OnWorldUnload()
    {
        CompanionBridge.AutoMoveEnabled = false;
        UpdateSleepSetting(Main.instance);
    }

    public override void Unload()
    {
        CompanionBridge.AutoMoveEnabled = false;
        UpdateSleepSetting(Main.instance);
        On_Main.DoUpdate -= UpdateExperiment;
        IL_Main.DoUpdate -= BypassUnfocusedPause;
    }
}
