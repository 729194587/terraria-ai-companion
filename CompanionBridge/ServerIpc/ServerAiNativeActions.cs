using System;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using CompanionBridge.Motion;
using VirtualPlayerEngineProbe;
using Result = CompanionBridge.ServerIpc.AiAtomicActions.NativeResult;

namespace CompanionBridge.ServerIpc;

// Native execution bridge only: existing G4 navigation boundary, G6 use, G7 mining.
internal sealed class ServerAiNativeActions : AiAtomicActions.IExecutor
{
    private sealed record Handle(AiReadOnlyProtocol.Identity Identity,string Operation,object Context,Result Rejection=null);
    private readonly ServerAiNavigation navigation = new();
    private readonly ServerAiDirectInput directInput = new();
    private Handle active;
    private static bool IsNavigation(string op)=>op is "navigate_to_x" or "navigate_to_landing_region";
    internal ServerAiNativeActions(){ActorControl.Driver=navigation;}
    internal static bool Owned(AiReadOnlyProtocol.Identity expected)
    {
        if(expected==null||!Program.IsMainThread||!ReferenceEquals(expected.Actor,ProbeSession.Actor)
            ||expected.World!=ProbeSession.WorldSession||expected.Generation!=ProbeSession.ActorGeneration
            ||expected.Slot!=ProbeSession.Actor?.whoAmI)return false;
        try{NetworkStateSync.Require((Player)expected.Actor,true);return true;}catch{return false;}
    }
    internal static void Require(AiReadOnlyProtocol.Identity expected)
    {
        ProbeSession.Reservation.AssertOwner(Program.IsMainThread);
        if(!Owned(expected))throw new InvalidOperationException("Native action binding unavailable.");
    }
    private static bool Released(Player p)=>!p.controlLeft&&!p.controlRight&&!p.controlUp&&!p.controlDown
        &&!p.controlJump&&!p.controlUseItem&&!p.controlUseTile&&!p.channel;
    public string Validate(string operation,JsonElement args)
    {
        try
        {
            AtomicParameters.Validate(operation,args,false);
            return operation=="use_item"&&args.TryGetProperty("target",out _) ? "targeted_use_not_supported" : null;
        }
        catch{return "invalid_arguments";}
    }
    public object Start(AiReadOnlyProtocol.Identity identity,string operation,JsonElement args,string actionId)
    {
        Require(identity);var p=(Player)identity.Actor;
        Handle Reject(string reason,string detail=null)=>new(identity,operation,null,new("failed",reason,
            new{operation,mutationAttempted=false,effectStatus="not_started"},true,InputsAcquired:false,ReasonDetail:detail));
        if(ActorControl.Running||ActorItemUse.Running||ActorMining.Running||p.itemAnimation!=0||p.itemTime!=0||p.reuseDelay!=0||p.channel)
            return Reject("actor_busy");
        if(p.dead||p.ghost||p.gravDir!=1||p.mount.Active)return Reject("unsupported_actor_state");
        try
        {
            object context;
            if(operation=="move_input"){context=directInput.Start(identity,args,actionId);ActorControl.Driver=directInput;}
            else if(operation=="craft_recipe")context=ActorCrafting.Begin(identity,args);
            else if(operation=="navigate_to_x"){context=navigation.Start(identity,args.GetProperty("targetX").GetSingle(),actionId);ActorControl.Driver=navigation;}
            else if(operation=="navigate_to_landing_region"){context=navigation.Start(identity,AtomicParameters.ReadLandingRegion(args),actionId);ActorControl.Driver=navigation;}
            else if(operation is "place_tile" or "place_object")
            {
                int x=args.GetProperty("x").GetInt32(),y=args.GetProperty("y").GetInt32();
                string error=ActorPlacement.Preflight(p,x,y,operation=="place_object");
                if(error!=null)return Reject(error,operation=="place_object"?ActorObjectPlacement.PreflightDetail(p,x,y,error):null);
                ActorItemUse.StartPlacement(p,identity.World,identity.Generation,x,y,operation=="place_object");context=ActorItemUse.Current;
            }
            else if(operation=="use_item")
            {
                if(p.selectedItem<0||p.selectedItem>9||p.HeldItem.IsAir||!ActorItemUse.Supported(p.HeldItem.type))return Reject("unsupported_selected_item");
                ActorItemUse.Start(p,identity.World,identity.Generation);context=ActorItemUse.Current;
            }
            else
            {
                int x=args.GetProperty("x").GetInt32(),y=args.GetProperty("y").GetInt32();
                if(!WorldGen.InWorld(x,y,2))return Reject("invalid_target",JsonSerializer.Serialize(ActorMining.TargetDiagnostic(x,y)));
                if(Vector2.Distance(p.Center,new Vector2(x*16+8,y*16+8))>80)return Reject("out_of_conservative_80_pixel_reach");
                ActorMining.Start(p,identity.World,identity.Generation,x,y);context=ActorMining.Current;
            }
            active=new(identity,operation,context);
            ProbeSession.Log("atomic-native-start",new{actionId,operation,identity.World,identity.Generation,identity.Slot});
            return active;
        }
        catch
        {
            // Start preflight excludes other running actions. Release only this known actor.
            if(Owned(identity)){ActorMining.Retire(p);ActorItemUse.Retire(p);navigation.Retire(p);directInput.Retire(p);}
            throw;
        }
    }
    public Result Poll(AiReadOnlyProtocol.Identity identity,object value)
    {
        var h=(Handle)value;Require(h.Identity);
        if(h.Rejection!=null)return h.Rejection;
        var p=(Player)h.Identity.Actor;
        if(h.Operation=="craft_recipe")
        {
            var c=(ActorCrafting.Context)h.Context;ActorCrafting.Step(c);
            return new(c.Status,c.Reason,ActorCrafting.Evidence(c),Released(p),c.Status=="completed",InputsAcquired:false);
        }
        if(h.Operation=="move_input")
        {
            if(!ReferenceEquals(directInput.Current,h.Context))return new("unknown","native_context_changed",null,false);
            directInput.ObserveDeath(p);
            var c=(ServerAiDirectInput.Run)h.Context;
            return new(c.Status,c.Reason,directInput.Evidence(),c.InputReleased&&Released(p),
                c.Status=="completed"&&c.Updates==c.MaxUpdates,InputsAcquired:c.Updates>0,ReasonDetail:c.Detail);
        }
        if(IsNavigation(h.Operation))
        {
            if(!ReferenceEquals(navigation.Current,h.Context))return new("unknown","native_context_changed",null,false);
            var c=(ServerAiNavigation.Run)h.Context;
            var physical=c.FinalObservation??ServerAiNavigation.Observe(p);
            bool released=Released(p);
            string recoveryKind=AiActionReasons.NavigationRecoveryKind(c.Snapshot?.Reason);
            bool recoverable=c.Status=="failed"&&c.FinalObservation!=null&&physical.Settled&&released&&!p.dead&&!p.ghost
                &&recoveryKind!=null&&c.Reason==AiActionReasons.Navigation(c.Snapshot.Reason);
            return new(c.Status,c.Reason,new{targetX=c.Region==null?(float?)c.Target:null,targetRegion=c.Region==null?null:new{minPlayerLeftX=c.Region.MinPlayerLeftX,maxPlayerLeftX=c.Region.MaxPlayerLeftX,surfaceY=c.Region.SurfaceY},startX=c.StartX,x=physical.State.Px,y=physical.State.Py,vx=physical.State.Vx,vy=physical.State.Vy,
                updates=c.Updates,navigation=c.Snapshot,arrived=c.Status=="completed",grounded=physical.State.Grounded,
                safeSupport=physical.SafeSupport,settled=physical.Settled,velocityBeforeRelease=c.VelocityBeforeRelease,
                inputReleased=released,alive=!p.dead&&!p.ghost,health=p.statLife,
                recoverableFailure=recoverable,failureKind=recoverable?recoveryKind:null,
                observedGameUpdate=c.FinalObservation!=null?c.FinalObservationTick:Main.GameUpdateCount,
                effectStatus="native_navigation_observed"},Released(p),c.Status=="completed"&&physical.Settled
                    &&(c.Region==null||c.Region.Arrived(physical.State,new NativeMotionWorld(physical.State))),InputsAcquired:c.Updates>0,ReasonDetail:c.ReasonDetail);
        }
        if(h.Operation is "place_tile" or "place_object")
        {
            if(!ReferenceEquals(ActorItemUse.Current,h.Context))return new("unknown","native_context_changed",null,false);
            var c=(ActorItemUse.Context)h.Context;var tile=c.Placement;
            bool present=tile.Object?ActorObjectPlacement.Present(tile):Main.tile[tile.X,tile.Y].HasTile&&Main.tile[tile.X,tile.Y].TileType==tile.TileType;
            bool verified=c.Phase=="completed"&&tile.Placed&&!tile.ExternalChange&&present&&tile.ConsumedCount==1
                &&(!tile.Object||tile.NativeEvents==1&&ActorObjectPlacement.Validate(c)==null);
            string placementPhase=c.Phase=="rejected"?"failed":c.Phase=="stale"||tile.Object&&c.Phase=="completed"&&!verified?"unknown":c.Phase;
            return new(placementPhase,tile.Object&&c.Phase=="completed"&&!verified?"native_object_terminal_unconfirmed":c.Reason,
                new{x=tile.X,y=tile.Y,typeId=tile.TileType,itemType=c.Type,c.InitialStack,remainingStack=p.inventory[c.Slot].stack,
                    c.Ticks,c.Starts,c.AnimationPeak,tile.Attempts,nativePlacementEvents=tile.NativeEvents,uncertainNativeEffect=tile.UncertainNativeEffect,consumedCount=tile.ConsumedCount,placedByActor=tile.Placed,
                    objectPlacement=tile.Object,footprint=tile.Object?ActorObjectPlacement.Bounds(tile):null,fullFootprintVerified=tile.Object&&present,style=tile.Style,
                    targetPresent=present,targetChanged=tile.ExternalChange,effectStatus=verified?(tile.Object?"native_object_placed":"native_tile_placed"):"not_confirmed"},
                Released(p)&&p.itemAnimation==0&&p.itemTime==0&&(!tile.Object||p.releaseUseItem&&p.reuseDelay==0),verified,InputsAcquired:c.Ticks>0);
        }
        if(h.Operation=="use_item")
        {
            if(!ReferenceEquals(ActorItemUse.Current,h.Context))return new("unknown","native_context_changed",null,false);
            var c=(ActorItemUse.Context)h.Context;
            string status=c.Phase switch{"completed"=>"completed","running"=>"running","cancelled"=>"cancelled","stale"=>"unknown",_=>"failed"};
            bool cycle=c.Phase=="completed"&&c.Starts==1&&c.AnimationPeak>0&&p.itemAnimation==0&&p.itemTime==0&&p.reuseDelay==0;
            return new(status,c.Reason,new{itemType=c.Type,c.Ticks,c.Starts,c.AnimationPeak,c.TimePeak,c.ToolPeak,c.UsesPeak,
                c.InitialStack,stack=p.inventory[c.Slot].stack,consumptionObserved=c.Consumed,cycleVerified=cycle,effectStatus=c.AnimationPeak>0?"unconfirmed":"not_started"},
                Released(p)&&p.itemAnimation==0&&p.itemTime==0,status=="completed"&&cycle,InputsAcquired:c.Ticks>0);
        }
        if(!ReferenceEquals(ActorMining.Current,h.Context))return new("unknown","native_context_changed",null,false);
        var mine=(ActorMining.Context)h.Context;
        bool destroyed=mine.Destroyed&&!mine.ExternalChange&&WorldGen.InWorld(mine.X,mine.Y)&&!Main.tile[mine.X,mine.Y].HasTile;
        string phase=mine.Phase switch{ActionPhase.Running=>"running",ActionPhase.Completed=>"completed",ActionPhase.Cancelled=>"cancelled",ActionPhase.TimedOut=>"timeout",ActionPhase.Stale=>"unknown",_=>"failed"};
        return new(phase,AiActionReasons.Mining(phase,mine.Reason.ToString()),new{nativeActionId=mine.Id,mine.X,mine.Y,TileType=mine.InvalidTarget==null?(int?)mine.TileType:null,mine.ExpectedType,mine.Ticks,invalidTarget=mine.InvalidTarget,
            mine.Swings,mine.Strikes,mine.PeakDamage,mine.Transforms,toolKind=mine.Axe>0?"axe":"pickaxe",pick=mine.Pick,axe=mine.Axe,nativeDestroyed=mine.Destroyed,targetAbsent=destroyed,
            targetChanged=mine.ExternalChange||(!mine.Destroyed&&mine.TargetCaptured&&!ActorMining.TargetUnchanged(mine)),
            targetUnchanged=!mine.ExternalChange&&mine.Transforms==0&&!mine.Destroyed&&ActorMining.TargetUnchanged(mine),
            destroyedByActor=destroyed,effectStatus=destroyed?"native_tile_destroyed":mine.Ticks==0?"not_started":"not_confirmed"},
            Released(p)&&p.itemAnimation==0&&p.itemTime==0,phase=="completed"&&destroyed,InputsAcquired:mine.Ticks>0,ReasonDetail:string.IsNullOrWhiteSpace(mine.Detail)?null:mine.Detail);
    }
    public Result Cancel(AiReadOnlyProtocol.Identity identity,object value)
    {
        var h=(Handle)value;Require(h.Identity);
        if(h.Rejection!=null)return h.Rejection;
        if(h.Operation=="craft_recipe")
        {
            var c=(ActorCrafting.Context)h.Context;
            if(c.Status=="running"){c.Status="cancelled";c.Reason="explicit_stop";}
            return new(c.Status,c.Reason,ActorCrafting.Evidence(c),Released((Player)h.Identity.Actor),c.Status=="completed",InputsAcquired:false);
        }
        if(h.Operation=="place_object")
        {
            if(!ReferenceEquals(ActorItemUse.Current,h.Context))return new("unknown","native_context_changed",null,false);
            ActorItemUse.Stop((Player)h.Identity.Actor,h.Identity.World,h.Identity.Generation);
            return Poll(h.Identity,h);
        }
        var observed=Poll(h.Identity,h);
        if(observed.Status!="running")return observed;
        var p=(Player)h.Identity.Actor;
        if(h.Operation=="move_input")directInput.Stop("cancelled","explicit_stop");
        else if(IsNavigation(h.Operation))navigation.Stop("cancelled","explicit_stop");
        else if(h.Operation is "use_item" or "place_tile" or "place_object")ActorItemUse.Stop(p,h.Identity.World,h.Identity.Generation);
        else ActorMining.Retire(p);
        return Poll(h.Identity,h);
    }
    public void Shutdown()
    {
        try { if(active!=null&&Owned(active.Identity))Cancel(active.Identity,active); }
        catch(Exception e){ProbeSession.Quarantine("Native action shutdown unconfirmed: "+e.Message);}
        finally { if(ReferenceEquals(ActorControl.Driver,navigation)||ReferenceEquals(ActorControl.Driver,directInput))ActorControl.Driver=null; }
    }
}
