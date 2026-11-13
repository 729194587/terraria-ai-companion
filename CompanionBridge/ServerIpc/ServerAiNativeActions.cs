using System;
using System.Text.Json;
using Terraria;
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
    private readonly ServerAiRope rope = new();
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
    internal static bool Released(Player p)=>!p.controlLeft&&!p.controlRight&&!p.controlUp&&!p.controlDown
        &&!p.controlJump&&!p.controlUseItem&&!p.controlUseTile&&!p.channel;
    public bool CanTransferBody(AiReadOnlyProtocol.Identity identity)=>BodyIdle(identity);
    internal static bool BodyIdle(AiReadOnlyProtocol.Identity identity)=>Owned(identity)&&identity.CanAct
        &&!NativeCombatWorld.Running&&!ActorControl.Running&&!ActorItemUse.Running&&!ActorMining.Running
        &&identity.Actor is Player p&&Released(p)&&p.itemAnimation==0&&p.itemTime==0&&p.reuseDelay==0;
    public bool SupportsCombat=>true;
    public string ValidateCombatAdmission(AiReadOnlyProtocol.Identity identity,string target)=>NativeCombatWorld.Preflight(identity,target);
    public string Validate(string operation,JsonElement args)
        => AtomicParameters.ValidateServerAction(operation, args);
    public object Start(AiReadOnlyProtocol.Identity identity,string operation,JsonElement args,string actionId)=>StartCore(identity,operation,args,actionId,false);
    internal object StartMaintenancePlacement(AiReadOnlyProtocol.Identity id,Microsoft.Xna.Framework.Point at)
        =>StartCore(id,"place_tile",JsonSerializer.SerializeToElement(new{x=at.X,y=at.Y}),"auto-torch",true);
    private object StartCore(AiReadOnlyProtocol.Identity identity,string operation,JsonElement args,string actionId,bool child,bool continuous=false,
        NavigationKnowledge knowledge=null)
    {
        Require(identity);var p=(Player)identity.Actor;
        Handle Reject(string reason,string detail=null)=>new(identity,operation,null,new("failed",reason,
            new{operation,mutationAttempted=false,effectStatus="not_started"},true,InputsAcquired:false,ReasonDetail:detail));
        if(NativeCombatWorld.Running||ActorControl.Running||ActorItemUse.Running||ActorMining.Running||p.itemAnimation!=0||p.itemTime!=0||p.reuseDelay!=0||p.channel)
            return Reject("actor_busy");
        if(p.dead||p.ghost||p.gravDir!=1||p.mount.Active)return Reject("unsupported_actor_state");
        try
        {
            object context;
            if(AiAtomicActions.IsTask(operation))
            {
                if(child)throw new InvalidOperationException("Nested root task denied");
                var executor=new ChildExecutor(this,continuous:true);
                CompanionTask task=operation==CombatProtocol.Operation
                    ?new CombatController(new NativeCombatWorld(identity,navigation,actionId,args.GetProperty("entityRef").GetString()),
                        args.TryGetProperty("threat",out var threat)?"reflex":"agent",threat.ValueKind==JsonValueKind.String?threat.GetString():null)
                    :operation=="mine_target"
                    ?args.TryGetProperty("cluster",out var cluster)&&cluster.GetBoolean()
                        ?new ExcavationTask(actionId,identity,args,executor,executor.Knowledge)
                        :new MineTargetTask(actionId,new NativeMineTargetWorld(identity,args),executor)
                    :new AutonomousNavigationTask(actionId,new NativeNavigationWorld(identity,args,executor.Knowledge),executor);
                ulong budget=operation==CombatProtocol.Operation?CombatController.Budget:AutonomousNavigationTask.TripUpdates;
                context=new TaskSlot(task,actionId,null,identity with{GameUpdate=Main.GameUpdateCount},budget);
            }
            else if(operation=="move_input"){context=directInput.Start(identity,args,actionId);ActorControl.Driver=directInput;}
            else if(operation=="move_on_rope")
            {
                if(rope.Start(identity,args,out var run) is {} reason)return Reject(reason);
                context=run;ActorControl.Driver=rope;
            }
            else if(ActorInventory.IsOperation(operation))context=ActorInventory.Begin(identity,operation,args);
            else if(ActorEquipment.IsOperation(operation))context=new ActorEquipment.Context(identity,operation,args);
            else if(operation=="craft_recipe")context=ActorCrafting.Begin(identity,args);
            else if(operation=="navigate_to_x"){context=navigation.Start(identity,args.GetProperty("targetX").GetSingle(),actionId);ActorControl.Driver=navigation;}
            else if(operation=="navigate_to_landing_region"){context=navigation.Start(identity,AtomicParameters.ReadLandingRegion(args),actionId,continuous,preferFlatRegion:child,knowledge:knowledge);ActorControl.Driver=navigation;}
            else if(operation=="place_rope")
            {
                if(p.noBuilding||p.CCed||p.noItems||p.frozen||p.stoned)return Reject("unsupported_placement_state");
                int slot=ActorRope.FindSlot(p);
                if(slot<0)return Reject("rope_material_unavailable");
                int x=args.GetProperty("x").GetInt32(),y=args.GetProperty("y").GetInt32();
                string error=ActorRope.Resolve(p,p.inventory[slot],x,y,out var target);
                if(error!=null)return Reject(error);
                if(p.selectedItem!=slot)ActorItemUse.Select(p,identity.World,identity.Generation,slot);
                ActorItemUse.StartPlacement(p,identity.World,identity.Generation,x,y,rope:target);
                context=ActorItemUse.Current;
            }
            else if(operation is "place_tile" or "place_object")
            {
                int x=args.GetProperty("x").GetInt32(),y=args.GetProperty("y").GetInt32();
                if(child&&operation=="place_tile"&&args.TryGetProperty("plannedPlacementSlot",out var plannedSlot))
                    ActorItemUse.Select(p,identity.World,identity.Generation,plannedSlot.GetInt32());
                string error=ActorPlacement.Preflight(p,x,y,operation=="place_object");
                if(error!=null)return Reject(error,operation=="place_object"?ActorObjectPlacement.PreflightDetail(p,x,y,error):null);
                ActorItemUse.StartPlacement(p,identity.World,identity.Generation,x,y,operation=="place_object");
                if(child&&operation=="place_tile"&&args.TryGetProperty("waterBridgeDirection",out var direction))
                    ActorItemUse.Current.Placement.WaterBridgeDirection=direction.GetInt32();
                context=ActorItemUse.Current;
            }
            else if(operation=="use_item")
            {
                if(p.selectedItem<0||p.selectedItem>9||!ActorItemUse.Supported(p.HeldItem))
                    return Reject("unsupported_selected_item",ActorRope.Supported(p.HeldItem)
                        ?"Rope placement requires Tile coordinates. Use place_tile for an empty cell or place_rope to extend an existing rope."
                        :null);
                ActorItemUse.Start(p,identity.World,identity.Generation);context=ActorItemUse.Current;
            }
            else
            {
                int x=args.GetProperty("x").GetInt32(),y=args.GetProperty("y").GetInt32();
                if(!WorldGen.InWorld(x,y,2))return Reject("invalid_target",JsonSerializer.Serialize(ActorMining.TargetDiagnostic(x,y)));
                ActorMining.Start(p,identity.World,identity.Generation,x,y,
                    child&&args.TryGetProperty("plannedToolSlot",out var planned)?planned.GetInt32():null);context=ActorMining.Current;
            }
            var handle=new Handle(identity,operation,context);
            if(!child)active=handle;
            ProbeSession.Log(child?"task-native-start":"atomic-native-start",new{actionId,operation,identity.World,identity.Generation,identity.Slot});
            return handle;
        }
        catch
        {
            // Start preflight excludes other running actions. Release only this known actor.
            if(Owned(identity)){ActorMining.Retire(p);ActorItemUse.Retire(p);navigation.Retire(p);directInput.Retire(p);rope.Retire(p);}
            throw;
        }
    }
    public Result Poll(AiReadOnlyProtocol.Identity identity,object value)
    {
        var h=(Handle)value;Require(h.Identity);
        if(h.Rejection!=null)return h.Rejection;
        var p=(Player)h.Identity.Actor;
        if(AiAtomicActions.IsTask(h.Operation))
        {
            var task=(TaskSlot)h.Context;var result=task.Tick(identity with{GameUpdate=Main.GameUpdateCount});
            return TaskResult(task,result,h.Identity);
        }
        if(ActorInventory.IsOperation(h.Operation))
        {
            var c=(ActorInventory.Context)h.Context;ActorInventory.Step(c);
            return new(c.Status,c.Reason,ActorInventory.Evidence(c),Released(p),c.Status=="completed",InputsAcquired:false);
        }
        if(h.Context is ActorEquipment.Context equipment)
        {
            equipment.Step();
            return EquipmentResult(h.Identity,equipment);
        }
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
        if(h.Operation=="move_on_rope")
        {
            if(!ReferenceEquals(rope.Current,h.Context))return new("unknown","native_context_changed",null,false);
            var c=(ServerAiRope.Run)h.Context;
            return new(c.Status,c.Reason,rope.Evidence(),c.InputReleased&&Released(p),
                c.Status=="completed",InputsAcquired:c.Updates>0,ReasonDetail:c.Detail);
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
        if(h.Operation is "place_tile" or "place_object" or "place_rope")
        {
            if(!ReferenceEquals(ActorItemUse.Current,h.Context))return new("unknown","native_context_changed",null,false);
            var c=(ActorItemUse.Context)h.Context;var tile=c.Placement;
            bool present=tile.Object?ActorObjectPlacement.Present(tile):Main.tile[tile.X,tile.Y].HasTile&&Main.tile[tile.X,tile.Y].TileType==tile.TileType
                &&(tile.Rope==null||!Main.tile[tile.X,tile.Y].IsActuated);
            bool verified=c.Phase=="completed"&&tile.Placed&&!tile.ExternalChange&&present&&tile.NativeEvents==1&&!tile.UncertainNativeEffect&&tile.ConsumedCount==1
                &&(!tile.Object||ActorObjectPlacement.Validate(c)==null);
            string placementPhase=c.Phase=="rejected"?"failed":c.Phase=="stale"||c.Phase=="completed"&&!verified?"unknown":c.Phase;
            return new(placementPhase,c.Phase=="completed"&&!verified?"native_placement_terminal_unconfirmed":c.Reason,
                new{x=tile.X,y=tile.Y,typeId=tile.TileType,itemType=c.Type,c.InitialStack,remainingStack=p.inventory[c.Slot].stack,
                    c.Ticks,c.Starts,c.AnimationPeak,tile.Attempts,nativePlacementEvents=tile.NativeEvents,uncertainNativeEffect=tile.UncertainNativeEffect,consumedCount=tile.ConsumedCount,placedByActor=tile.Placed,
                    objectPlacement=tile.Object,footprint=tile.Object?ActorObjectPlacement.Bounds(tile):null,fullFootprintVerified=tile.Object&&present,style=tile.Style,
                    requestedX=tile.Rope?.X,requestedY=tile.Rope?.Y,extended=tile.Rope==null?(bool?)null:tile.Y>tile.Rope.Y,
                    targetPresent=present,targetChanged=tile.ExternalChange,effectStatus=verified?(tile.Rope!=null?"native_rope_placed":tile.Object?"native_object_placed":"native_tile_placed"):"not_confirmed"},
                Released(p)&&p.itemAnimation==0&&p.itemTime==0&&(!tile.Object||p.releaseUseItem&&p.reuseDelay==0),verified,InputsAcquired:c.Ticks>0);
        }
        if(h.Operation=="use_item")
        {
            if(!ReferenceEquals(ActorItemUse.Current,h.Context))return new("unknown","native_context_changed",null,false);
            var c=(ActorItemUse.Context)h.Context;
            string status=c.Phase switch{"completed"=>"completed","running"=>"running","cancelled"=>"cancelled","stale" or "unknown"=>"unknown","timeout"=>"timeout",_=>"failed"};
            bool cycle=c.Phase=="completed"&&c.Starts==1&&c.AnimationPeak>0&&p.itemAnimation==0&&p.itemTime==0&&p.reuseDelay==0;
            return new(status,c.Reason,new{itemType=c.Type,c.Ticks,c.Starts,c.AnimationPeak,c.TimePeak,c.ToolPeak,c.UsesPeak,
                c.InitialStack,stack=p.inventory[c.Slot].stack,consumptionObserved=c.Consumed,cycleVerified=cycle,effectStatus=c.AnimationPeak>0?"unconfirmed":"not_started"},
                Released(p)&&p.itemAnimation==0&&p.itemTime==0,status=="completed"&&cycle,InputsAcquired:c.Ticks>0);
        }
        if(!ReferenceEquals(ActorMining.Current,h.Context))return new("unknown","native_context_changed",null,false);
        var mine=(ActorMining.Context)h.Context;
        bool destroyed=mine.Destroyed&&!mine.ExternalChange&&WorldGen.InWorld(mine.X,mine.Y)&&!Main.tile[mine.X,mine.Y].HasTile;
        bool furnitureAbsent=ActorMining.FurnitureAbsent(mine);
        string phase=mine.Phase switch{ActionPhase.Running=>"running",ActionPhase.Completed=>destroyed&&furnitureAbsent?"completed":"unknown",ActionPhase.Cancelled=>"cancelled",ActionPhase.TimedOut=>"timeout",ActionPhase.Stale=>"unknown",_=>"failed"};
        return new(phase,AiActionReasons.Mining(phase,mine.Reason.ToString()),new{nativeActionId=mine.Id,mine.X,mine.Y,TileType=mine.InvalidTarget==null?(int?)mine.TileType:null,mine.ExpectedType,mine.Ticks,invalidTarget=mine.InvalidTarget,
            mine.Swings,mine.Strikes,mine.PeakDamage,mine.Transforms,toolKind=mine.Axe>0?"axe":"pickaxe",pick=mine.Pick,axe=mine.Axe,nativeDestroyed=mine.Destroyed,targetAbsent=destroyed,
            furnitureCells=mine.Furniture.Length,furnitureAbsent=mine.Furniture.Length>0&&furnitureAbsent,
            targetChanged=mine.ExternalChange||(!mine.Destroyed&&mine.TargetCaptured&&!ActorMining.TargetUnchanged(mine)),
            targetUnchanged=!mine.ExternalChange&&mine.Transforms==0&&!mine.Destroyed&&ActorMining.TargetUnchanged(mine),
            destroyedByActor=destroyed,effectStatus=destroyed?"native_tile_destroyed":mine.Ticks==0?"not_started":"not_confirmed"},
            Released(p)&&p.itemAnimation==0&&p.itemTime==0,phase=="completed"&&destroyed,InputsAcquired:mine.Ticks>0,ReasonDetail:string.IsNullOrWhiteSpace(mine.Detail)?null:mine.Detail);
    }
    public Result Cancel(AiReadOnlyProtocol.Identity identity,object value)
    {
        var h=(Handle)value;Require(h.Identity);
        if(h.Rejection!=null)return h.Rejection;
        if(AiAtomicActions.IsTask(h.Operation))return CancelWithReason(identity,value,"cancelled","explicit_stop");
        if(h.Context is ActorEquipment.Context equipment)
        {
            equipment.Cancel();
            return EquipmentResult(h.Identity,equipment);
        }
        if(ActorInventory.IsOperation(h.Operation))
        {
            var c=(ActorInventory.Context)h.Context;ActorInventory.Cancel(c);
            return new(c.Status,c.Reason,ActorInventory.Evidence(c),Released((Player)h.Identity.Actor),c.Status=="completed",InputsAcquired:false);
        }
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
        else if(h.Operation=="move_on_rope")rope.Stop("cancelled","explicit_stop");
        else if(IsNavigation(h.Operation))navigation.Stop("cancelled","explicit_stop");
        else if(h.Operation is "use_item" or "place_tile" or "place_object" or "place_rope")ActorItemUse.Stop(p,h.Identity.World,h.Identity.Generation);
        else ActorMining.Stop(p,h.Identity.World,h.Identity.Generation);
        return Poll(h.Identity,h);
    }
    private static Result EquipmentResult(AiReadOnlyProtocol.Identity id,ActorEquipment.Context c)
        => new(c.Status,c.Reason,c.Evidence(),Owned(id)&&Released((Player)id.Actor),c.Status=="completed",InputsAcquired:false,ReasonDetail:c.Error);
    public Result CancelWithReason(AiReadOnlyProtocol.Identity identity,object value,string status,string reason)
    {
        var h=(Handle)value;Require(h.Identity);
        if(!AiAtomicActions.IsTask(h.Operation))return Cancel(identity,value);
        if(h.Rejection!=null)return h.Rejection;
        var task=(TaskSlot)h.Context;var result=task.Stop(identity with{GameUpdate=Main.GameUpdateCount},status,reason);
        return TaskResult(task,result,h.Identity);
    }
    private static Result TaskResult(TaskSlot task,Result result,AiReadOnlyProtocol.Identity identity)
    {
        bool owned=Owned(identity),released=owned&&Released((Player)identity.Actor);
        if(!owned){task.Task.Abandon();result=result with{Status="unknown",Reason="actor_binding_changed",InputsReleased=false,EffectVerified=false};}
        else if(result.Status!="running"&&(!result.InputsReleased||!released))
            result=result with{Status="unknown",Reason="task_inputs_not_released",InputsReleased=false,EffectVerified=false};
        else result=result with{InputsReleased=result.InputsReleased&&released};
        if(result.Status!="running")
        {task.Record.Result=result;task.Record.Status=result.Status;task.Record.Phase="terminal";task.Record.Reason=result.Reason;}
        return result with{Evidence=new{task=task.View()}};
    }
    public Result BindingLost(object value)
    {
        var h=(Handle)value;
        if(!AiAtomicActions.IsTask(h.Operation)||h.Rejection!=null)return new("unknown","actor_binding_changed",null,false);
        var task=(TaskSlot)h.Context;var result=task.Abandon();return result with{Evidence=new{task=task.View()}};
    }
    // Native contexts only: no AiAtomicActions.Handle/registry/IPC start per child.
    private sealed class ChildExecutor : AiAtomicActions.IExecutor
    {
        private readonly ServerAiNativeActions owner;
        private readonly bool continuous;
        private readonly NavigationKnowledge knowledge=new();
        internal NavigationKnowledge Knowledge=>knowledge;
        internal ChildExecutor(ServerAiNativeActions owner,bool continuous){this.owner=owner;this.continuous=continuous;}
        public string Validate(string op,JsonElement args)=>owner.Validate(op,args);
        public ulong ProgressVersion(object handle)=>handle is Handle {Context:ServerAiNavigation.Run run}?run.ProgressVersion:0;
        public object Start(AiReadOnlyProtocol.Identity id,string op,JsonElement args,string taskId)=>owner.StartCore(id,op,args,taskId,true,continuous,knowledge);
        public Result Poll(AiReadOnlyProtocol.Identity id,object value)
        {
            var r=owner.Poll(id,value);
            return r.Reason is "native_mining_exception" or "native_navigation_exception"?r with{Status="unknown",EffectVerified=false}:r;
        }
        public Result Cancel(AiReadOnlyProtocol.Identity id,object value)
        {
            var r=owner.Cancel(id,value);
            return r.Reason is "native_mining_exception" or "native_navigation_exception"?r with{Status="unknown",EffectVerified=false}:r;
        }
        public void Shutdown(){}
    }
    public void Shutdown()
    {
        try { if(active!=null&&Owned(active.Identity))Cancel(active.Identity,active); }
        catch(Exception e){ProbeSession.Quarantine("Native action shutdown unconfirmed: "+e.Message);}
        finally { if(ReferenceEquals(ActorControl.Driver,navigation)||ReferenceEquals(ActorControl.Driver,directInput))ActorControl.Driver=null; }
    }
}
