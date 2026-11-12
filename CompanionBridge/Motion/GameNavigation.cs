using System;
using System.Diagnostics;
using System.Collections.Generic;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace CompanionBridge.Motion;

internal sealed class NativeNavigation
{
    internal LocalMotionController Controller { get; } = new();
    internal NavigationTask Task { get; }
    private readonly NavigationDiagnostics Diagnostics;
    internal NavigationSnapshot Snapshot => CoreSnapshot with {Mobility=_profile};
    private bool _navigation;
    private int _session, _generation;
    private NativeMotionWorld _world;
    private int _life;
    private bool _paired;
    private readonly MobilityTracker _mobility=new();
    private MobilityProfile _profile;
    private long _planRevision;
    private NavigationSnapshot CoreSnapshot=>_navigation?Task.Snapshot:Controller.Snapshot;
    private readonly bool _captureContacts;
    internal NativeNavigation(bool captureContacts=false,Action<string> diagnosticWriter=null,bool enableSafeGroundWalk=false,Func<PhysicsState,int> historicalPreference=null)
    {
        _captureContacts=captureContacts;
        Task=new(enableSafeGroundWalk,historicalPreference);
        Diagnostics=new(diagnosticWriter??(line=>ModContent.GetInstance<CompanionBridge>().Logger.Info(line)));
    }

    private PhysicsState State(Player p) => NativeMotionWorld.Observe(p);

    internal bool Ordinary(Player p) => OrdinaryRejection(p,NativeMovementReader.Capture(p)) == null;

    private string OrdinaryRejection(Player p,MovementSnapshot movement)
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
        bool modeled=ExtraJumpModel.Select(movement)!=null;
        Reject(movement.ActiveExtraJump&&!modeled,"extra_jump_active");
        Reject(movement.PotentialExtraJump&&!modeled,"extra_jump_available");
        bool airRequested=AirMotionModel.Strategy(movement)!=AirKind.None;
        Reject(airRequested&&ph.Air==null,"unsupported_air_profile");
        Reject(airRequested&&(p.slowFall||p.empressBrooch||p.chilled),"unmodeled_air_effect");
        Reject(ph.Air!=null&&!ph.Air.Valid(AirMotionModel.Resources(movement)),"invalid_air_resources");
        Reject(ph.Air!=null&&!ph.Matches(PhysicsParameters.Basic)&&!ph.Matches(PhysicsParameters.Swiftness),"unsupported_air_physics");
        Reject(p.honeyWet&&!p.wet,"honey_wet");
        Reject(p.lavaWet&&!p.wet,"lava_wet");
        Reject(p.shimmerWet&&!p.wet,"shimmer_wet");
        Reject(p.itemAnimation != 0, "item_animation");
        Reject(p.width != PhysicsSimulator.PlayerW || p.height != PhysicsSimulator.PlayerH, "unsupported_body_size");
        Reject(!float.IsFinite(p.velocity.X)||!float.IsFinite(p.velocity.Y), "invalid_velocity");
        Reject(!ph.IsSupportedGroundPhysics, $"unsupported_physics: {ph}");
        return reasons == null ? null : string.Join("; ", reasons);
    }

    internal static PhysicsParameters Parameters(Player p)
    {
        var m=NativeMovementReader.Capture(p);var air=AirMotionModel.Select(m);
        // Angel's native air tweak changes accRunSpeed before this boundary.
        // Keep one planning profile; Step applies that verified air-only value.
        float speed=air?.Kind==AirKind.Wings&&Math.Abs(p.accRunSpeed-6.25f)<.001f?3:p.accRunSpeed;
        return new(p.runAcceleration,p.maxRunSpeed,speed,p.runSlowdown,p.gravity,p.maxFallSpeed,Player.jumpSpeed,Player.jumpHeight,
            AirMotionModel.Strategy(m)==AirKind.None?ExtraJumpModel.Select(m):null,air,FallProfile(p));
    }

    internal static FallSafety FallProfile(Player p)=>new(p.extraFall,p.noFallDmg||p.equippedWings!=null);

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
        var ph=Parameters(p);
        var movement=NativeMovementReader.Capture(p);
        string rejection = OrdinaryRejection(p,movement);
        int swiftnessIndex=p.FindBuffIndex(BuffID.Swiftness);
        _profile=_mobility.Sample(ph,p.width,p.height,p.HasBuff(BuffID.Swiftness),
            rejection==null?Array.Empty<string>():new[]{rejection},Main.GameUpdateCount,session,generation,
            swiftnessIndex>=0?p.buffTime[swiftnessIndex]:0,movement,
            ph.ExtraJump!=null&&ExtraJumpModel.ExpectedBoundary(_navigation?Task.ExpectedState:Controller.ExpectedState,State(p)),
            ph.Air!=null&&(_navigation?Task.ExpectedState:Controller.ExpectedState) is {} expected&&!AirMotionModel.Matches(expected,State(p),true),
            ph.Air==null?null:new(p.runAcceleration,p.maxRunSpeed,p.accRunSpeed,p.runSlowdown,p.gravity,p.maxFallSpeed,Player.jumpSpeed,Player.jumpHeight));
        if(!_profile.Supported)return Fail("unsupported navigation state: "+string.Join("; ",_profile.UnsupportedEffects));
        if (_session != session || _generation != generation) _life = p.statLife;
        _session = session; _generation = generation;
        if (p.statLife < _life) return Fail("life decreased");
        _life = p.statLife;
        var state = State(p);
        if(_planRevision!=_profile.Revision)
        {
            if(_navigation)Task.InvalidateMobility(state,_profile.Revision,ph);
            else Controller.Cancel();
        }
        _planRevision=_profile.Revision;
        _world = new NativeMotionWorld(state,_captureContacts);
        Task.LiveFallStart=Controller.LiveFallStart=p.fallStart;
        long started = Stopwatch.GetTimestamp();
        var input = _navigation
            ? region != null ? Task.Before(session, generation, region, state, ph, _world)
                : Task.Before(session, generation, targetX.Value, state, ph, _world)
            : Controller.Before(session, direction, state, ph, _world, generation);
        if (Stopwatch.GetElapsedTime(started).TotalMilliseconds > 50)
        {
            if(!_navigation||!Task.Continuous)return Fail("local prediction exceeded 50ms budget");
            Task.DeferPlanningOverrun(state);
            input=default;
        }
        if (_navigation) Diagnostics.Observe(Snapshot);
        _paired = true;
        return input;
    }

    internal void After(Player p)
    {
        if (!_paired) return;
        _paired = false;
        if (p.statLife < _life || p.dead) { Fail("life decreased/dead"); return; }
        if (_navigation) { Task.After(State(p), _world, inputsReleased: !p.controlLeft && !p.controlRight && !p.controlJump
            && !p.controlDown && !p.controlUp && !p.controlUseItem && !p.controlUseTile); Diagnostics.Observe(Snapshot); }
        else Controller.After(State(p), _world);
    }

    private PhysicsInput Fail(string reason)
    {
        var input = _navigation ? Task.Fail(reason) : Controller.Fail(reason);
        if (_navigation) Diagnostics.Observe(Snapshot);
        return input;
    }

    internal void Cancel()
    {
        _mobility.Reset();_profile=null;_planRevision=0;
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
    internal static FallSafety FallProfile(Player p)=>NativeNavigation.FallProfile(p);
    internal static PhysicsInput Before(Player p, int session, int direction, int generation, float? targetX = null, LandingRegion region = null)
        => instance.Before(p, session, direction, generation, targetX, region);
    internal static void After(Player p) => instance.After(p);
    internal static void Cancel() => instance.Cancel();
}
