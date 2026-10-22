using System;
using System.Diagnostics;
using System.Collections.Generic;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
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

    internal static bool Ordinary(Player p) => OrdinaryRejection(p) == null;

    private static string OrdinaryRejection(Player p)
    {
        var ph = Parameters(p);
        List<string> reasons = null;
        void Reject(bool condition, string reason)
        {
            if (condition) (reasons ??= new()).Add(reason);
        }
        Reject(p.mount.Active, "mount_active");
        Reject(p.gravDir != 1, "unsupported_gravity");
        Reject(p.wet, "wet");
        Reject(p.pulley, "pulley");
        Reject(p.grapCount != 0, "grappling");
        Reject(p.webbed, "webbed");
        Reject(p.stoned, "stoned");
        Reject(p.frozen, "frozen");
        Reject(p.sliding, "sliding");
        Reject(p.autoJump, "auto_jump");
        Reject(p.windPushed, "wind_pushed");
        Reject(p.slippy || p.slippy2, "slippery");
        Reject(p.sticky, "sticky");
        Reject(p.powerrun, "power_run");
        Reject(p.AnyExtraJumpUsable(), "extra_jump_usable");
        Reject(p.wingsLogic != 0, "functional_wings");
        Reject(p.rocketBoots != 0, "rocket_boots");
        Reject(p.itemAnimation != 0, "item_animation");
        Reject(p.width != 20 || p.height != 42, "unsupported_body_size");
        Reject(!(Math.Abs(p.velocity.X) <= 3.2f), "unsupported_horizontal_velocity");
        if (!ph.IsBasic) Reject(true, $"unsupported_physics: {ph}");
        for (int i = 0; i < 10; i++)
            if (!p.armor[i].IsAir) Reject(true, $"equipment: slot={i}, type={p.armor[i].type}, name={p.armor[i].Name}");
        for (int i = 0; i < p.buffTime.Length; i++)
        {
            int type = p.buffType[i];
            // Only resolve names for rejected effects; no per-tick string work for allowed buffs.
            if (NavigationBuffPolicy.Rejection(type, p.buffTime[i], i, null) == null) continue;
            string key = BuffLoader.GetBuff(type)?.FullName ?? "Terraria/" + BuffID.Search.GetName(type);
            Reject(true, NavigationBuffPolicy.Rejection(type, p.buffTime[i], i, key));
        }
        return reasons == null ? null : string.Join("; ", reasons);
    }

    private static PhysicsParameters Parameters(Player p) => new(p.runAcceleration, p.maxRunSpeed,
        p.accRunSpeed, p.runSlowdown, p.gravity, p.maxFallSpeed, Player.jumpSpeed, Player.jumpHeight);

    internal static PhysicsInput Before(Player p, int session, int direction, int generation, float? targetX = null)
    {
        _paired = false;
        _navigation = targetX.HasValue;
        if (!_navigation) { Task.Cancel(); Controller.Begin(session, direction, generation); }
        else { Controller.Cancel(); Task.Begin(session, generation, targetX.Value, State(p)); }
        string rejection = OrdinaryRejection(p);
        if (rejection != null) return Fail("unsupported navigation state: " + rejection);
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
