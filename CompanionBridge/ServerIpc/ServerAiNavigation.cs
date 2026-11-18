using System;
using Terraria;
using CompanionBridge.Motion;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

// G4's natural Update/PostUpdateRunSpeeds boundary, with the original planner.
internal sealed class ServerAiNavigation : ActorControl.IMovementDriver
{
    internal sealed record LandingObservation(PhysicsState State,bool SafeSupport,bool Settled);
    internal static LandingObservation Observe(Player p)
    {
        var s=NativeMotionWorld.Observe(p);
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
        internal NavigationKnowledge Knowledge;
        internal ServerGrappleClimb Climb;
        internal bool GrappleAttempted;
        internal BossEvasion Evasion;
        internal bool Combat;
        internal int CombatLife;
        internal ulong ProgressVersion;
    }
    private readonly NativeNavigation navigation;
    internal ServerAiNavigation()
    {
        navigation=new(captureContacts:true,enableSafeGroundWalk:true,historicalPreference:HistoricalPreference,diagnosticWriter:line=>
            ProbeSession.Log("atomic-navigation",new{actionId=Current?.ActionId,world=Current?.Identity.World,
                generation=Current?.Identity.Generation,slot=Current?.Identity.Slot,session=Current?.Session,
                gameUpdate=Main.GameUpdateCount,navigationEvent=System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(line.Substring("Navigation ".Length))}));
    }
    private int HistoricalPreference(PhysicsState state)
    {
        // Same last-known map as query_map, only on the owned game-thread path.
        // No RPC, terrain authorization, map writes or new persistent topology.
        var map=AiMapRuntime.Map;
        if(Current==null||!ServerAiNativeActions.Owned(Current.Identity)||!WorldCharacterPersistence.Ready
            ||map==null||map.World!=(Main.ActiveWorldFileData?.UniqueId??Guid.Empty)||map.Character!=WorldCharacterPersistence.ProfileId)return 0;
        int cx=(int)Math.Floor((state.Px+10)/16),feet=(int)Math.Floor((state.Py+42)/16),rank=0;
        for(int x=cx-1;x<=cx+1;x++)
            if(map.Get(x,feet) is 2 or 3&&map.Get(x,feet-1)==1)rank++;
        return rank;
    }
    private int session;
    private bool executing, injected, suppressStaleGrapple;
    internal Run Current;
    public bool Running => Current?.Status=="running";
    public bool Matches(Player p) => Current!=null && ReferenceEquals(Current.Identity.Actor,p);
    internal Run Start(AiReadOnlyProtocol.Identity identity,float target,string actionId=null)
        =>Start(identity,target,null,actionId);
    internal Run StartCombat(AiReadOnlyProtocol.Identity identity,float target,string actionId)
        =>Start(identity,target,null,actionId,combat:true);
    internal Run StartCombat(AiReadOnlyProtocol.Identity identity,LandingRegion region,string actionId)
        =>Start(identity,region.MinPlayerLeftX,region,actionId,combat:true);
    internal Run Start(AiReadOnlyProtocol.Identity identity,LandingRegion region,string actionId=null,bool continuous=false,bool preferFlatRegion=false,
        NavigationKnowledge knowledge=null)
        =>Start(identity,region.MinPlayerLeftX,region,actionId,continuous,preferFlatRegion,knowledge);
    private Run Start(AiReadOnlyProtocol.Identity identity,float target,LandingRegion region,string actionId,bool continuous=false,bool preferFlatRegion=false,
        NavigationKnowledge knowledge=null,bool combat=false)
    {
        var p=(Player)identity.Actor;
        ServerAiNativeActions.Require(identity);
        ActorControl.Healthy(p);
        if(ActorControl.Running||ActorItemUse.Running||ActorMining.Running)throw new InvalidOperationException("Actor busy.");
        navigation.Cancel();
        knowledge?.Bind(identity.Actor,identity.World,identity.Generation,identity.Slot);
        navigation.Task.SetKnowledge(knowledge);
        navigation.Task.Continuous=continuous;
        navigation.Task.PreferFlatRegion=preferFlatRegion;
        Current=new Run{Identity=identity,ActionId=actionId,Target=target,Region=region,StartX=p.position.X,
            Session=checked(++session),Generation=checked((int)identity.Generation),Knowledge=knowledge,Combat=combat,CombatLife=p.statLife};
        ActorControl.Driver=this;
        navigation.Task.TryGrapple=continuous&&region!=null?TryGrapple:null;
        return Current;
    }

    private bool TryGrapple(PhysicsState state,PhysicsParameters physics,IMotionWorld world)
    {
        var run=Current;
        if(run.GrappleAttempted||run.Knowledge?.GrappleAttempted==true||world is not ISurfaceTerrain terrain)return false;
        var p=(Player)run.Identity.Actor;
        if(ActorGrapple.Selected(p) is not {} item||ActorGrapple.HasHook(p)||p.noItems||p.CCed||p.tongued)return false;
        var plan=GrappleClimbPlan.Find(state,run.Region,physics,world,terrain);
        if(plan==null)return false;
        run.GrappleAttempted=true;
        if(run.Knowledge!=null)run.Knowledge.GrappleAttempted=true;
        run.Climb=new(plan,physics,item);
        return true;
    }

    public void BeforeMovement(Player p)
    {
        if(!executing||!Matches(p)||!NativeSurvival.Owns(p))throw new InvalidOperationException("No owned navigation update scope.");
        ServerAiNativeActions.Require(Current.Identity);
        navigation.Task.ObservationUpdate=checked((long)Main.GameUpdateCount);
        if(Current.Evasion is {} evasion)
        {
            long start=ServerCombatFixture.PlannerStart();
            Apply(p,evasion.Before(p));
            ServerCombatFixture.PlannerEnd(start);
            return;
        }
        var input=Current.Climb is {} climb
            ?climb.Before(p,navigation,Current.Session,Current.Generation,nativePull:false)
            :navigation.Before(p,Current.Session,Current.Target>=Current.StartX?1:-1,Current.Generation,
                Current.Region==null?Current.Target:null,Current.Region,combatSwing:Current.Combat);
        Apply(p,input);
    }
    internal bool BeforeGrappleMovement(Player p)
    {
        if(suppressStaleGrapple&&Matches(p))return false;
        if(!executing||!Matches(p)||Current.Climb is not {} climb)return true;
        ServerAiNativeActions.Require(Current.Identity);
        bool attached=climb.WantsNativePull(p);
        Apply(p,climb.Before(p,navigation,Current.Session,Current.Generation,nativePull:attached));
        return attached&&climb.Failure==null;
    }
    private void Apply(Player p,PhysicsInput input)
    {
        p.controlLeft=input.Direction<0;p.controlRight=input.Direction>0;p.controlJump=input.Jump;
        p.controlDown=input.Down;p.controlUp=false;
        if(Current.Combat) NativeCombatWorld.Moved(p,input);
        else p.controlUseItem=p.controlUseTile=false;
        ServerCombatFixture.EvasionInput(p);
        ServerAiTerrainFixture.RecordInput(p);
        injected=true;
    }
    private bool Release(bool brake=false)
    {
        if(Current==null||!ServerAiNativeActions.Owned(Current.Identity))return false;
        var p=(Player)Current.Identity.Actor;
        p.controlLeft=p.controlRight=p.controlJump=p.controlDown=p.controlUp=false;
        if(!Current.Combat) p.controlUseItem=p.controlUseTile=false;
        if(brake)p.velocity.X=0; // Same explicit-stop convention as G4; never move position or vertical velocity.
        return true;
    }
    internal void Stop(string status,string reason)
    {
        if(Current==null)return;
        var climb=Current.Climb;
        if(climb!=null)
        {
            Current.Climb=null;
            if(!climb.Release()){status="unknown";reason="grapple_cleanup_unconfirmed";}
        }
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
        navigation.Cancel();Release(!Current.Combat);
        if(ServerAiNativeActions.Owned(Current.Identity))
        {
            try
            {
                Current.FinalObservation=Observe((Player)Current.Identity.Actor);Current.FinalObservationTick=Main.GameUpdateCount;
                climb?.Log("stopped",Current.FinalObservation.State);
            }
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
            if(!ServerAiNativeActions.Owned(run.Identity))
            {
                // Retire the exact old projectile, then let the natural frame
                // clear its sole obsolete list entry without writing a stale Player.
                suppressStaleGrapple=run.Climb?.HasNativeEntry(p)==true;
                try{Stop("unknown","navigation_binding_changed");orig(p,slot);}
                finally{suppressStaleGrapple=false;}
                return;
            }
            try
            {
                if(executing||slot!=p.whoAmI||run.Tick.HasValue&&Main.GameUpdateCount<=run.Tick.Value)
                    throw new InvalidOperationException("Duplicate/reentrant navigation update.");
                // The AI's NPC collision runs after this driver returns. A hit
                // there invalidates the predicted pose before the next frame.
                if(run.Combat&&p.statLife<run.CombatLife)
                {
                    Stop("cancelled","combat_approach_hurt");
                    orig(p,slot);
                    return;
                }
                ActorControl.Healthy(p);run.Tick=Main.GameUpdateCount;run.Updates++;
                executing=true;injected=false;
                run.CombatLife=p.statLife;
                orig(p,slot); // One original natural update, never a manual Player.Update.
                if(!ServerAiNativeActions.Owned(run.Identity)){Stop("unknown","navigation_binding_changed");return;}
                if(!Running)return; // Native death/retirement may have stopped the driver.
                if(!injected)throw new InvalidOperationException("Audited G4 movement boundary not reached.");
                if(run.Combat&&p.statLife<run.CombatLife)
                {
                    Stop("cancelled","combat_approach_hurt");
                    NetworkStateSync.Full(p);
                    return;
                }
                if(run.Climb is {} climb)
                {
                    climb.After(run.Identity,run.Session,run.Generation);
                    if(climb.Failure!=null)
                    {
                        var state=NativeMotionWorld.Observe(p);
                        bool cleaned=climb.Release();
                        climb.Log("failed",state);
                        if(!cleaned){Stop("unknown","grapple_cleanup_unconfirmed");return;}
                        run.Climb=null;
                        navigation.Task.RecoverGrapple(state,climb.Failure);
                    }
                    else if(climb.Complete)run.Climb=null;
                    NetworkStateSync.Tick(p);
                    return;
                }
                if(run.Evasion!=null){run.Evasion.After(p);NetworkStateSync.Tick(p);return;}
                navigation.After(p,combatSwing:run.Combat);run.Snapshot=navigation.Snapshot;run.ProgressVersion=navigation.Task.ProgressVersion;
                if(run.Snapshot.Status=="unknown"){run.ReasonDetail=run.Snapshot.Reason;Stop("unknown",run.Snapshot.Reason=="navigation_unknown_terrain"
                    ?"native_navigation_terrain_unknown":"native_navigation_landing_unconfirmed");}
                else if(run.Snapshot.Status=="failed")
                {
                    bool budget=AiActionReasons.NavigationSearchIncomplete(run.Snapshot.Reason);
                    run.ReasonDetail=budget?run.Snapshot.SearchFailureReason??run.Snapshot.Reason:run.Snapshot.Reason;
                    Stop(budget?"timeout":"failed",AiActionReasons.Navigation(run.Snapshot.Reason));
                }
                else if(run.Snapshot.Status=="arrived")
                {
                    var observed=Observe(p);
                    bool arrived=run.Region!=null
                        ? run.Region.Arrived(observed.State,new NativeMotionWorld(observed.State))
                        : observed.Settled
                        &&(run.Target>=run.StartX?1:-1)*(run.Target-p.position.X)<=12;
                    Stop(arrived?"completed":"unknown",arrived?"native_navigation_arrival_verified":"arrival_not_settled");
                }
                else if(navigation.Task.Continuous&&run.Snapshot.ActualMovingUpdates>=1800&&run.ProgressVersion>0&&Observe(p).Settled)
                    Stop("cancelled","native_navigation_work_checkpoint");
                else if(!navigation.Task.Continuous&&run.Updates>=1800)Stop("timeout","navigation_frame_budget");
                if(Running)NetworkStateSync.Tick(p);else NetworkStateSync.Full(p);
            }
            catch(Exception e){run.ReasonDetail=e.Message;Stop("failed","native_navigation_exception");}
            finally{Release();executing=injected=false;}
        }
    }
}
