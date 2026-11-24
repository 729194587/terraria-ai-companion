using System;
using System.Diagnostics;
using System.Linq;
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

    internal bool Ordinary(Player p,bool combatSwing=false) => OrdinaryRejection(p,NativeMovementReader.Capture(p),combatSwing) == null;

    private string OrdinaryRejection(Player p,MovementSnapshot movement,bool combatSwing=false)
    {
        var ph = Parameters(p);
        List<string> reasons = null;
        void Reject(bool condition, string reason)
        {
            if (condition) (reasons ??= new()).Add(reason);
        }
        Reject(p.mount.Active, "mount_active");
        Reject(p.gravDir != 1, "unsupported_gravity");
        Reject(p.wet&&!ph.CanWade&&!p.honeyWet&&!p.lavaWet&&!p.shimmerWet, "unsupported_water_profile");
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
        bool airRequested=AirMotionModel.Strategy(movement)!=AirKind.None;
        Reject(airRequested&&ph.Air==null&&!ph.GroundOnly,"unsupported_air_profile");
        Reject(airRequested&&(p.slowFall||p.empressBrooch||p.chilled),"unmodeled_air_effect");
        Reject(ph.Air!=null&&!ph.Air.Valid(AirMotionModel.Resources(movement)),"invalid_air_resources");
        Reject(ph.GroundOnly && (p.velocity.Y != 0 || p.wet), "unsupported_airborne_state");
        Reject(p.honeyWet,"honey_wet");
        Reject(p.lavaWet,"lava_wet");
        Reject(p.shimmerWet,"shimmer_wet");
        Reject(p.itemAnimation != 0 && !combatSwing, "item_animation");
        Reject(p.width != PhysicsSimulator.PlayerW || p.height != PhysicsSimulator.PlayerH, "unsupported_body_size");
        Reject(!float.IsFinite(p.velocity.X)||!float.IsFinite(p.velocity.Y), "invalid_velocity");
        Reject(!ph.IsSupportedGroundPhysics, $"unsupported_physics: {ph}");
        return reasons == null ? null : string.Join("; ", reasons);
    }

    internal static PhysicsParameters Parameters(Player p)
    {
        var m=NativeMovementReader.Capture(p);var air=AirMotionModel.Select(m);
        // Normalize only the airborne native WingAirLogicTweaks; Step reapplies them per frame.
        bool airborneWing=air?.Kind==AirKind.Wings && p.velocity.Y!=0 && !p.merman && !p.mount.Active;
        float speed=airborneWing && Math.Abs(p.accRunSpeed-air.AirRunSpeed)<.001f?PhysicsParameters.Basic.AccRunSpeed:p.accRunSpeed;
        float acceleration=airborneWing?p.runAcceleration/air.AirAcceleration:p.runAcceleration;
        var activeJump=ExtraJumpModel.Kind(Array.Find(m.ExtraJumps,j=>j.Active)?.FullName);
        var observed=new PhysicsParameters(acceleration/(activeJump?.Acceleration??1),p.maxRunSpeed/(activeJump?.Speed??1),speed,p.runSlowdown,p.gravity,p.maxFallSpeed,
            Player.jumpSpeed,Player.jumpHeight,ExtraJumpModel.Select(m),air,FallProfile(p));
        var basic=PhysicsParameters.Basic;
        bool stockDry=Math.Abs(observed.Gravity-basic.Gravity)<.001f&&Math.Abs(observed.MaxFall-basic.MaxFall)<.001f
            &&Math.Abs(observed.JumpSpeed-basic.JumpSpeed)<.001f&&observed.JumpHeight==basic.JumpHeight;
        bool wade=observed.Air==null&&observed.ExtraJump==null&&!p.ignoreWater&&!p.waterWalk&&!p.waterWalk2
            &&!p.trident&&!p.merman&&!p.accFlipper&&!p.canFloatInWater
            &&(stockDry||observed.UsesWaterProfile);
        // An unmodeled jump or wing can still leave ordinary supported walking intact.
        bool groundOnly = AirMotionModel.Strategy(m) != AirKind.None && air == null;
        if (groundOnly) return observed with {GroundOnly=true, ExtraJump=null, Air=null};
        observed=observed with{Impulse=NavigationAirActions.Capture(p).Action,
            NoAirRepress=!m.BlockExtraJumps && m.ExtraJumps.Any(j=>j.Enabled) && observed.ExtraJump==null};
        return wade?observed with{CanWade=true,Gravity=basic.Gravity,MaxFall=basic.MaxFall,
            JumpSpeed=basic.JumpSpeed,JumpHeight=basic.JumpHeight}:observed;
    }

    internal static FallSafety FallProfile(Player p)=>new(p.extraFall,p.noFallDmg||p.equippedWings!=null,p.statLife);

    // A travel order can spend up to one third of its starting life, with a
    // quarter of maximum life in reserve. Starting wounded still permits escape.
    internal static bool ExceedsTravelBudget(Player p,int startLife,int projectedDamage=0)
    {
        int remaining=p.statLife-projectedDamage;
        int reserve=Math.Max(p.statLifeMax2/4,startLife-startLife/3);
        return remaining<startLife && remaining<=reserve;
    }

    internal PhysicsInput Before(Player p, int session, int direction, int generation, float? targetX = null,
        LandingRegion region = null, bool combatSwing = false)
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
        string rejection = OrdinaryRejection(p,movement,combatSwing);
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
        if (!combatSwing && ExceedsTravelBudget(p,_life)) return Fail("travel damage budget exhausted");
        _life = p.statLife;
        var state = State(p);
        if(_planRevision!=_profile.Revision)
        {
            if(_navigation)Task.InvalidateMobility(state,_profile.Revision,ph);
            else Controller.Cancel();
        }
        _planRevision=_profile.Revision;
        _world = new NativeMotionWorld(state,_captureContacts,p.breath);
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

    internal void After(Player p, bool combatSwing = false)
    {
        if (!_paired) return;
        _paired = false;
        if ((!combatSwing && ExceedsTravelBudget(p,_life)) || p.dead) { Fail("life decreased/dead"); return; }
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
    internal static bool Ordinary(Player p,bool combatSwing=false) => instance.Ordinary(p,combatSwing);
    internal static FallSafety FallProfile(Player p)=>NativeNavigation.FallProfile(p);
    internal static PhysicsInput Before(Player p, int session, int direction, int generation, float? targetX = null, LandingRegion region = null)
        => instance.Before(p, session, direction, generation, targetX, region);
    internal static void After(Player p) => instance.After(p);
    internal static void Cancel() => instance.Cancel();
}
