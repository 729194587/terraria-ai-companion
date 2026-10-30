using System;
using System.Diagnostics;
using System.Collections.Generic;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using CompanionBridge.Common.Systems;

namespace CompanionBridge.Motion;

internal sealed class NativeNavigation
{
    internal LocalMotionController Controller { get; } = new();
    internal NavigationTask Task { get; }
    private readonly NavigationDiagnostics Diagnostics;
    internal NavigationSnapshot Snapshot => _navigation ? Task.Snapshot : Controller.Snapshot;
    private bool _navigation;
    private int _session, _generation;
    private NativeMotionWorld _world;
    private int _life;
    private bool _paired;
    private readonly bool _captureContacts;
    internal NativeNavigation(bool captureContacts=false,Action<string> diagnosticWriter=null,bool enableSafeGroundWalk=false)
    {
        _captureContacts=captureContacts;
        Task=new(enableSafeGroundWalk);
        Diagnostics=new(diagnosticWriter??(line=>ModContent.GetInstance<CompanionBridge>().Logger.Info(line)));
    }

    private PhysicsState State(Player p) => new(p.position.X, p.position.Y,
        p.velocity.X, p.velocity.Y, TerrainReader.Read(p).Grounded, p.jump, p.releaseJump);

    internal bool Ordinary(Player p) => OrdinaryRejection(p) == null;

    private string OrdinaryRejection(Player p)
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

    private PhysicsParameters Parameters(Player p) => new(p.runAcceleration, p.maxRunSpeed,
        p.accRunSpeed, p.runSlowdown, p.gravity, p.maxFallSpeed, Player.jumpSpeed, Player.jumpHeight);

    internal PhysicsInput Before(Player p, int session, int direction, int generation, float? targetX = null,
        LandingRegion region = null)
    {
        _paired = false;
        _navigation = targetX.HasValue || region != null;
        if (!_navigation)
        {
            Task.Cancel(); Diagnostics.Observe(Task.Snapshot);
            Controller.Begin(session, direction, generation);
        }
        else
        {
            Controller.Cancel();
            if (region != null) Task.Begin(session, generation, region, State(p));
            else Task.Begin(session, generation, targetX.Value, State(p));
        }
        string rejection = OrdinaryRejection(p);
        if (rejection != null) return Fail("unsupported navigation state: " + rejection);
        if (_session != session || _generation != generation) _life = p.statLife;
        _session = session; _generation = generation;
        if (p.statLife < _life) return Fail("life decreased");
        _life = p.statLife;
        var state = State(p);
        _world = new NativeMotionWorld(state,_captureContacts);
        long started = Stopwatch.GetTimestamp();
        var input = _navigation
            ? region != null ? Task.Before(session, generation, region, state, Parameters(p), _world)
                : Task.Before(session, generation, targetX.Value, state, Parameters(p), _world)
            : Controller.Before(session, direction, state, Parameters(p), _world, generation);
        if (Stopwatch.GetElapsedTime(started).TotalMilliseconds > 50)
            return Fail("local prediction exceeded 50ms budget");
        if (_navigation) Diagnostics.Observe(Task.Snapshot);
        _paired = true;
        return input;
    }

    internal void After(Player p)
    {
        if (!_paired) return;
        _paired = false;
        if (p.statLife < _life || p.dead) { Fail("life decreased/dead"); return; }
        if (_navigation) { Task.After(State(p), _world); Diagnostics.Observe(Task.Snapshot); }
        else Controller.After(State(p), _world);
    }

    private PhysicsInput Fail(string reason)
    {
        var input = _navigation ? Task.Fail(reason) : Controller.Fail(reason);
        if (_navigation) Diagnostics.Observe(Task.Snapshot);
        return input;
    }

    internal void Cancel()
    {
        _paired = false; _world = null; _session = _generation = 0;
        Task.Cancel(); Diagnostics.Observe(Task.Snapshot); _navigation = false;
        Controller.Cancel();
    }
}

// Existing human callers retain their process-local instance and API.
internal static class GameNavigation
{
    private static readonly NativeNavigation instance = new();
    internal static LocalMotionController Controller => instance.Controller;
    internal static NavigationTask Task => instance.Task;
    internal static NavigationSnapshot Snapshot => instance.Snapshot;
    internal static bool Ordinary(Player p) => instance.Ordinary(p);
    internal static PhysicsInput Before(Player p, int session, int direction, int generation, float? targetX = null, LandingRegion region = null)
        => instance.Before(p, session, direction, generation, targetX, region);
    internal static void After(Player p) => instance.After(p);
    internal static void Cancel() => instance.Cancel();
}
