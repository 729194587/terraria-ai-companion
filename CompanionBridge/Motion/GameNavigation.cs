using System;
using System.Diagnostics;
using Terraria;
using CompanionBridge.Common.Systems;

namespace CompanionBridge.Motion;

internal static class GameNavigation
{
    internal static LocalMotionController Controller { get; } = new();
    private static NativeMotionWorld _world;
    private static int _life;
    private static bool _paired;

    private static PhysicsState State(Player p) => new(p.position.X, p.position.Y,
        p.velocity.X, p.velocity.Y, TerrainReader.Read(p).Grounded, p.jump, p.releaseJump);

    internal static bool Ordinary(Player p)
    {
        var ph = Parameters(p);
        bool ordinary = !p.mount.Active && p.gravDir == 1 && !p.wet && !p.pulley && p.grapCount == 0
            && !p.webbed && !p.stoned && !p.frozen && !p.sliding && !p.autoJump && !p.windPushed
            && !p.slippy && !p.slippy2 && !p.sticky && !p.powerrun && !p.AnyExtraJumpUsable()
            && p.wingsLogic == 0 && p.rocketBoots == 0 && p.itemAnimation == 0
            && p.width == 20 && p.height == 42 && Math.Abs(p.velocity.X) <= 3.2f
            && ph.IsBasic;
        for (int i = 0; i < 10; i++) ordinary &= p.armor[i].IsAir;
        for (int i = 0; i < p.buffTime.Length; i++) ordinary &= p.buffTime[i] == 0;
        return ordinary;
    }

    private static PhysicsParameters Parameters(Player p) => new(p.runAcceleration, p.maxRunSpeed,
        p.accRunSpeed, p.runSlowdown, p.gravity, p.maxFallSpeed, Player.jumpSpeed, Player.jumpHeight);

    internal static PhysicsInput Before(Player p, int session, int direction, int generation)
    {
        _paired = false;
        Controller.Begin(session, direction, generation);
        if (!Ordinary(p)) return Controller.Fail($"unsupported player/equipment/buff/physics parameters: {Parameters(p)}");
        if (Controller.Snapshot.Session != session || Controller.Snapshot.Status == "idle") _life = p.statLife;
        if (p.statLife < _life) return Controller.Fail("life decreased");
        _life = p.statLife;
        var state = State(p);
        _world = new NativeMotionWorld(state);
        long started = Stopwatch.GetTimestamp();
        var input = Controller.Before(session, direction, state, Parameters(p), _world, generation);
        if (Stopwatch.GetElapsedTime(started).TotalMilliseconds > 50)
            return Controller.Fail("local prediction exceeded 50ms budget");
        _paired = true;
        return input;
    }

    internal static void After(Player p)
    {
        if (!_paired) return;
        _paired = false;
        if (p.statLife < _life || p.dead) { Controller.Fail("life decreased/dead"); return; }
        Controller.After(State(p), _world);
    }

    internal static void Cancel()
    {
        _paired = false; _world = null;
        Controller.Cancel();
    }
}
