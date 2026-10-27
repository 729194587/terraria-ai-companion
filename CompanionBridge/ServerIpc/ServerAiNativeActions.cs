using System;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using VirtualPlayerEngineProbe;
using Result = CompanionBridge.ServerIpc.AiAtomicActions.NativeResult;

namespace CompanionBridge.ServerIpc;

// Native execution bridge only: existing G4 navigation boundary, G6 use, G7 mining.
internal sealed class ServerAiNativeActions : AiAtomicActions.IExecutor
{
    private sealed record Handle(AiReadOnlyProtocol.Identity Identity,string Operation,object Context,Result Rejection=null);
    private readonly ServerAiNavigation navigation = new();
    private Handle active;
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
        Handle Reject(string reason)=>new(identity,operation,null,new("failed",reason,
            new{operation,mutationAttempted=false,effectStatus="not_started"},true,InputsAcquired:false));
        if(ActorControl.Running||ActorItemUse.Running||ActorMining.Running||p.itemAnimation!=0||p.itemTime!=0||p.reuseDelay!=0||p.channel)
            return Reject("actor_busy");
        if(p.dead||p.ghost||p.gravDir!=1||p.mount.Active)return Reject("unsupported_actor_state");
        try
        {
            object context;
            if(operation=="navigate_to_x")context=navigation.Start(identity,args.GetProperty("targetX").GetSingle());
            else if(operation=="use_item")
            {
                if(p.selectedItem<0||p.selectedItem>9||p.HeldItem.IsAir||!ActorItemUse.Supported(p.HeldItem.type))return Reject("unsupported_selected_item");
                ActorItemUse.Start(p,identity.World,identity.Generation);context=ActorItemUse.Current;
            }
            else
            {
                int x=args.GetProperty("x").GetInt32(),y=args.GetProperty("y").GetInt32();
                if(!WorldGen.InWorld(x,y,2))return Reject("invalid_target");
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
            if(Owned(identity)){ActorMining.Retire(p);ActorItemUse.Retire(p);navigation.Retire(p);}
            throw;
        }
    }
    public Result Poll(AiReadOnlyProtocol.Identity identity,object value)
    {
        var h=(Handle)value;Require(h.Identity);
        if(h.Rejection!=null)return h.Rejection;
        var p=(Player)h.Identity.Actor;
        if(h.Operation=="navigate_to_x")
        {
            if(!ReferenceEquals(navigation.Current,h.Context))return new("unknown","native_context_changed",null,false);
            var c=(ServerAiNavigation.Run)h.Context;
            return new(c.Status,c.Reason,new{targetX=c.Target,startX=c.StartX,x=p.position.X,y=p.position.Y,vx=p.velocity.X,vy=p.velocity.Y,
                updates=c.Updates,navigation=c.Snapshot,arrived=c.Status=="completed",effectStatus="native_navigation_observed"},Released(p),c.Status=="completed",InputsAcquired:c.Updates>0);
        }
        if(h.Operation=="use_item")
        {
            if(!ReferenceEquals(ActorItemUse.Current,h.Context))return new("unknown","native_context_changed",null,false);
            var c=(ActorItemUse.Context)h.Context;
            string status=c.Phase switch{"completed"=>"completed","running"=>"running","cancelled"=>"cancelled","stale"=>"unknown",_=>"failed"};
            bool cycle=c.Phase=="completed"&&c.Starts==1&&c.AnimationPeak>0&&p.itemAnimation==0&&p.itemTime==0&&p.reuseDelay==0;
            return new(status,c.Reason,new{itemType=c.Type,c.Ticks,c.Starts,c.AnimationPeak,c.TimePeak,c.ToolPeak,c.UsesPeak,
                c.InitialStack,stack=p.inventory[c.Slot].stack,consumptionObserved=c.Consumed,cycleVerified=cycle,effectStatus="unconfirmed"},
                Released(p)&&p.itemAnimation==0&&p.itemTime==0,status=="completed"&&cycle,InputsAcquired:c.Ticks>0);
        }
        if(!ReferenceEquals(ActorMining.Current,h.Context))return new("unknown","native_context_changed",null,false);
        var mine=(ActorMining.Context)h.Context;
        bool destroyed=mine.Destroyed&&!mine.ExternalChange&&WorldGen.InWorld(mine.X,mine.Y)&&!Main.tile[mine.X,mine.Y].HasTile;
        string phase=mine.Phase switch{ActionPhase.Running=>"running",ActionPhase.Completed=>"completed",ActionPhase.Cancelled=>"cancelled",ActionPhase.TimedOut=>"timeout",ActionPhase.Stale=>"unknown",_=>"failed"};
        return new(phase,mine.Reason+": "+mine.Detail,new{nativeActionId=mine.Id,mine.X,mine.Y,mine.TileType,mine.ExpectedType,mine.Ticks,
            mine.Swings,mine.Strikes,mine.PeakDamage,mine.Transforms,nativeDestroyed=mine.Destroyed,targetAbsent=destroyed,
            targetChanged=mine.ExternalChange,destroyedByActor=destroyed,effectStatus=destroyed?"native_tile_destroyed":"not_confirmed"},
            Released(p)&&p.itemAnimation==0&&p.itemTime==0,phase=="completed"&&destroyed,InputsAcquired:mine.Ticks>0);
    }
    public Result Cancel(AiReadOnlyProtocol.Identity identity,object value)
    {
        var h=(Handle)value;Require(h.Identity);
        var observed=Poll(h.Identity,h);
        if(observed.Status!="running")return observed;
        var p=(Player)h.Identity.Actor;
        if(h.Operation=="navigate_to_x")navigation.Stop("cancelled","explicit_stop");
        else if(h.Operation=="use_item")ActorItemUse.Stop(p,h.Identity.World,h.Identity.Generation);
        else ActorMining.Retire(p);
        return Poll(h.Identity,h);
    }
    public void Shutdown()
    {
        try { if(active!=null&&Owned(active.Identity))Cancel(active.Identity,active); }
        catch(Exception e){ProbeSession.Quarantine("Native action shutdown unconfirmed: "+e.Message);}
        finally { if(ReferenceEquals(ActorControl.Driver,navigation))ActorControl.Driver=null; }
    }
}
