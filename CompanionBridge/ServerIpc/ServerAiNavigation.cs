using System;
using Terraria;
using CompanionBridge.Common.Systems;
using CompanionBridge.Motion;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

// G4's natural Update/PostUpdateRunSpeeds boundary, with the original planner.
internal sealed class ServerAiNavigation : ActorControl.IMovementDriver
{
    internal sealed record LandingObservation(PhysicsState State,bool SafeSupport,bool Settled);
    internal static LandingObservation Observe(Player p)
    {
        var s=new PhysicsState(p.position.X,p.position.Y,p.velocity.X,p.velocity.Y,TerrainReader.Read(p).Grounded,p.jump,p.releaseJump);
        bool safe=new NativeMotionWorld(s).SafeLanding(s);
        return new(s,safe,s.Grounded&&safe&&Math.Abs(s.Vy)<=0.1f&&Math.Abs(s.Vx)<=0.1f&&s.ReleaseJump&&s.JumpFramesLeft==0);
    }
    internal sealed class Run
    {
        internal AiReadOnlyProtocol.Identity Identity;
        internal float Target, StartX;
        internal LandingRegion Region;
        internal int Session, Generation, Updates;
        internal string Status="running", Reason="navigation_accepted";
        internal string ReasonDetail, ActionId;
        internal ulong? Tick;
        internal NavigationSnapshot Snapshot;
        internal LandingObservation FinalObservation;
        internal StateVector VelocityBeforeRelease;
        internal ulong FinalObservationTick;
    }
    private readonly NativeNavigation navigation;
    internal ServerAiNavigation()
    {
        navigation=new(captureContacts:true,enableSafeGroundWalk:true,diagnosticWriter:line=>
            ProbeSession.Log("atomic-navigation",new{actionId=Current?.ActionId,world=Current?.Identity.World,
                generation=Current?.Identity.Generation,slot=Current?.Identity.Slot,session=Current?.Session,
                gameUpdate=Main.GameUpdateCount,navigationEvent=System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(line.Substring("Navigation ".Length))}));
    }
    private int session;
    private bool executing, injected;
    internal Run Current;
    public bool Running => Current?.Status=="running";
    public bool Matches(Player p) => Current!=null && ReferenceEquals(Current.Identity.Actor,p);
    internal Run Start(AiReadOnlyProtocol.Identity identity,float target,string actionId=null)
        =>Start(identity,target,null,actionId);
    internal Run Start(AiReadOnlyProtocol.Identity identity,LandingRegion region,string actionId=null)
        =>Start(identity,region.MinPlayerLeftX,region,actionId);
    private Run Start(AiReadOnlyProtocol.Identity identity,float target,LandingRegion region,string actionId)
    {
        var p=(Player)identity.Actor;
        ServerAiNativeActions.Require(identity);
        ActorControl.Healthy(p);
        if(ActorControl.Running||ActorItemUse.Running||ActorMining.Running)throw new InvalidOperationException("Actor busy.");
        navigation.Cancel();
        Current=new Run{Identity=identity,ActionId=actionId,Target=target,Region=region,StartX=p.position.X,Session=checked(++session),Generation=checked((int)identity.Generation)};
        return Current;
    }
    public void BeforeMovement(Player p)
    {
        if(!executing||!Matches(p)||!NativeSurvival.Owns(p))throw new InvalidOperationException("No owned navigation update scope.");
        ServerAiNativeActions.Require(Current.Identity);
        var input=navigation.Before(p,Current.Session,Current.Target>=Current.StartX?1:-1,Current.Generation,Current.Region==null?Current.Target:null,Current.Region);
        p.controlLeft=input.Direction<0;p.controlRight=input.Direction>0;p.controlJump=input.Jump;
        p.controlDown=p.controlUp=p.controlUseItem=p.controlUseTile=false;
        ServerAiTerrainFixture.RecordInput(p);
        injected=true;
    }
    private bool Release(bool brake=false)
    {
        if(Current==null||!ServerAiNativeActions.Owned(Current.Identity))return false;
        var p=(Player)Current.Identity.Actor;
        p.controlLeft=p.controlRight=p.controlJump=p.controlDown=p.controlUp=p.controlUseItem=p.controlUseTile=false;
        if(brake)p.velocity.X=0; // Same explicit-stop convention as G4; never move position or vertical velocity.
        return true;
    }
    internal void Stop(string status,string reason)
    {
        if(Current==null)return;
        if(ServerAiNativeActions.Owned(Current.Identity))
        {
            var p=(Player)Current.Identity.Actor;
            Current.VelocityBeforeRelease=new(p.velocity.X,p.velocity.Y);
            try
            {
                var observed=Observe(p);
                if(status=="failed"&&(!observed.State.Grounded||!observed.SafeSupport||Math.Abs(observed.State.Vy)>0.1f))
                {Current.ReasonDetail=(Current.ReasonDetail??reason)+"; landing not confirmed";status="unknown";reason="native_navigation_landing_unconfirmed";}
            }
            catch(Exception e){Current.ReasonDetail=e.Message;status="unknown";reason="navigation_state_unavailable";}
        }
        Current.Status=status;Current.Reason=reason;Current.Snapshot=navigation.Snapshot;
        navigation.Cancel();Release(true);
        if(ServerAiNativeActions.Owned(Current.Identity))
        {
            try{Current.FinalObservation=Observe((Player)Current.Identity.Actor);Current.FinalObservationTick=Main.GameUpdateCount;}
            catch(Exception e){Current.Status="unknown";Current.Reason="navigation_state_unavailable";Current.ReasonDetail=e.Message;}
        }
    }
    public void Retire(Player p){if(Matches(p)&&Running)Stop("cancelled","actor_retired");}
    public void Update(On_Player.orig_Update orig,Player p,int slot)
    {
        var run=Current;
        if(!Running||!Matches(p)){orig(p,slot);return;}
        ProbeSession.Reservation.AssertOwner(Program.IsMainThread);
        lock(ProbeSession.Reservation.Gate)
        {
            if(!ServerAiNativeActions.Owned(run.Identity)){Stop("unknown","navigation_binding_changed");orig(p,slot);return;}
            try
            {
                if(executing||slot!=p.whoAmI||run.Tick.HasValue&&Main.GameUpdateCount<=run.Tick.Value)
                    throw new InvalidOperationException("Duplicate/reentrant navigation update.");
                ActorControl.Healthy(p);run.Tick=Main.GameUpdateCount;run.Updates++;
                executing=true;injected=false;
                orig(p,slot); // One original natural update, never a manual Player.Update.
                if(!ServerAiNativeActions.Owned(run.Identity)){Stop("unknown","navigation_binding_changed");return;}
                if(!Running)return; // Native death/retirement may have stopped the driver.
                if(!injected)throw new InvalidOperationException("Audited G4 movement boundary not reached.");
                navigation.After(p);run.Snapshot=navigation.Snapshot;
                if(run.Snapshot.Status=="unknown"){run.ReasonDetail=run.Snapshot.Reason;Stop("unknown","native_navigation_landing_unconfirmed");}
                else if(run.Snapshot.Status=="failed"){run.ReasonDetail=run.Snapshot.SearchFailureReason is {} detail && (run.Snapshot.Reason is "navigation_search_timeout" or "navigation_search_expansion_budget")?detail:run.Snapshot.Reason;Stop("failed",AiActionReasons.Navigation(run.Snapshot.Reason));}
                else if(run.Snapshot.Status=="arrived")
                {
                    var terrain=TerrainReader.Read(p);
                    var observed=Observe(p);
                    bool arrived=run.Region!=null
                        ? run.Region.Arrived(observed.State,new NativeMotionWorld(observed.State))
                        : terrain.Grounded&&Math.Abs(p.velocity.X)<=0.1f&&Math.Abs(p.velocity.Y)<=0.1f
                        &&(run.Target>=run.StartX?1:-1)*(run.Target-p.position.X)<=12;
                    Stop(arrived?"completed":"unknown",arrived?"native_navigation_arrival_verified":"arrival_not_settled");
                }
                else if(run.Updates>=1800)Stop("timeout","navigation_frame_budget");
                if(Running)NetworkStateSync.Tick(p);else NetworkStateSync.Full(p);
            }
            catch(Exception e){run.ReasonDetail=e.Message;Stop("failed","native_navigation_exception");}
            finally{Release();executing=injected=false;}
        }
    }
}
