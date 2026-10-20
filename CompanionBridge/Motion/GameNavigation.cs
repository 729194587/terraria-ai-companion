using System;
using System.Diagnostics;
using Terraria;
using CompanionBridge.Common.Systems;

namespace CompanionBridge.Motion;

internal static class GameNavigation
{
    internal static LocalMotionController Controller { get; } = new();
    internal static NavigationTask Task { get; } = new();
    internal static NavigationSnapshot Snapshot => _navigation ? Task.Snapshot : Controller.Snapshot;
    private static bool _navigation;
    private static int _session, _generation;
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

    internal static PhysicsInput Before(Player p, int session, int direction, int generation, float? targetX = null)
    {
        _paired = false;
        _navigation = targetX.HasValue;
        if (!_navigation) { Task.Cancel(); Controller.Begin(session, direction, generation); }
        else { Controller.Cancel(); Task.Begin(session, generation, targetX.Value, State(p)); }
        if (!Ordinary(p)) return Fail($"unsupported player/equipment/buff/physics parameters: {Parameters(p)}");
        if (_session != session || _generation != generation) _life = p.statLife;
        _session = session; _generation = generation;
        if (p.statLife < _life) return Fail("life decreased");
        _life = p.statLife;
        var state = State(p);
        _world = new NativeMotionWorld(state);
        long started = Stopwatch.GetTimestamp();
        var input = _navigation
            ? Task.Before(session, generation, targetX.Value, state, Parameters(p), _world)
            : Controller.Before(session, direction, state, Parameters(p), _world, generation);
        if (Stopwatch.GetElapsedTime(started).TotalMilliseconds > 50)
            return Fail("local prediction exceeded 50ms budget");
        _paired = true;
        return input;
    }

    internal static void After(Player p)
    {
        if (!_paired) return;
        _paired = false;
        if (p.statLife < _life || p.dead) { Fail("life decreased/dead"); return; }
        if (_navigation) Task.After(State(p), _world);
        else Controller.After(State(p), _world);
    }

    private static PhysicsInput Fail(string reason) => _navigation ? Task.Fail(reason) : Controller.Fail(reason);

    internal static void Cancel()
    {
        _paired = false; _world = null; _session = _generation = 0;
        Task.Cancel(); _navigation = false;
        Controller.Cancel();
    }
}
