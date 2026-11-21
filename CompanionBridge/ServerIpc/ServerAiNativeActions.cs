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
    internal Func<AiReadOnlyProtocol.Identity,string,bool> ReflexTargetAllowed {get;set;}
    public string CurrentCombatTarget(object handle)=>
        handle is Handle {Context:TaskSlot {Task:CombatController combat}}?combat.TargetRef:null;
    public bool SupportsBossEncounter=>true;
    public bool SupportsCombat=>true;
    public bool SupportsOpening=>ServerItemOpening.Available;
    public bool SupportsDismissSummon=>true;
    public bool SupportsDoors=>true;
    public bool SupportsRegroup=>SupportsCollection;
    public AiAtomicActions.RespawnPlan AfterRespawn(object handle,AiReadOnlyProtocol.Identity frame)=>
        handle is Handle {Context:TaskSlot {Task:BossEncounterTask boss}} ? boss.AfterRespawn(frame) : null;
    public object StartRejoin(AiReadOnlyProtocol.Identity frame,JsonElement args,string id,AiAtomicActions.RespawnPlan plan)
        =>StartCore(frame,CombatProtocol.Boss,args,id,false,rejoin:plan);
    public bool WaitingForRespawn(object handle,AiReadOnlyProtocol.Identity frame)=>
        handle is Handle {Context:TaskSlot slot} && slot.Record.Started && slot.Task.CanWaitForRespawn(frame);
    public bool SupportsLoadout=>true;
    public float? WorkVelocityX(AiReadOnlyProtocol.Identity frame)=>Owned(frame)?((Player)frame.Actor).velocity.X:null;
    public Func<bool> CapturePrimary(AiReadOnlyProtocol.Identity frame)=>NativeCombatWorld.CapturePrimary(frame);
    public object StartCombat(AiReadOnlyProtocol.Identity frame,JsonElement args,string id,AiAtomicActions.CombatAdmission admission)
        =>StartCore(frame,CombatProtocol.Operation,args,id,false,admission:admission);
    public bool SupportsContainers=>true;
    public bool SupportsSorting=>ActorSorting.Available;
    public bool SupportsCollection => VirtualPlayerEngineProbe.VirtualPlayerEngineProbe.Engine?.Selected == "2026.09.3.1";
    public bool SupportsReforge => MerchantShop.VanillaServicesSupported;
    public bool SupportsNurse => NurseService.Supported;
    public bool SupportsMerchantSale => MerchantShop.SaleSupported;
    public bool SupportsMerchantPurchase => MerchantShop.Supported;
    public bool SupportsHousingAssignment => VirtualPlayerEngineProbe.VirtualPlayerEngineProbe.Engine?.Selected == "2026.09.3.1";
    public bool SupportsBuildPlace => SupportsPlacement;
    public bool SupportsPlacement => ActorPlacement.UnifiedAvailable;
    public string ValidateCombatAdmission(AiReadOnlyProtocol.Identity identity,string target)=>NativeCombatWorld.Preflight(identity,target);
    public string Validate(string operation,JsonElement args)
        => operation=="build_place" ? BuildingBlueprint.ValidEnvelope(args) ? null : "invalid_arguments" : AtomicParameters.ValidateServerAction(operation, args);
    public object Start(AiReadOnlyProtocol.Identity identity,string operation,JsonElement args,string actionId)=>StartCore(identity,operation,args,actionId,false);
    internal object StartMaintenancePlacement(AiReadOnlyProtocol.Identity id,Microsoft.Xna.Framework.Point at)
        =>StartCore(id,"place_tile",JsonSerializer.SerializeToElement(new{x=at.X,y=at.Y}),"auto-torch",true);
    private object StartCore(AiReadOnlyProtocol.Identity identity,string operation,JsonElement args,string actionId,bool child,bool continuous=false,
        NavigationKnowledge knowledge=null,AiAtomicActions.CombatAdmission admission=null,AiAtomicActions.RespawnPlan rejoin=null,bool buildTravel=false)
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
            if(operation=="dismiss_summon")
            {
                int type=args.GetProperty("itemType").GetInt32();
                if(!Terraria.ID.ContentSamples.ItemsByType.TryGetValue(type,out var staff) ||
                    !NativeCombatWorld.MinionStaff(staff))return Reject("not_minion_weapon");
                bool active=p.HasBuff(staff.buffType);
                if(active)
                {
                    p.ClearBuff(staff.buffType);
                    NetMessage.SendData(Terraria.ID.MessageID.PlayerBuffs,-1,-1,null,p.whoAmI);
                }
                bool absent=!p.HasBuff(staff.buffType);
                if(absent)NativeCombatWorld.PauseSummon(identity,type);
                return new Handle(identity,operation,null,new(absent?"completed":"unknown",
                    absent?"summon_buff_absent":"summon_buff_unconfirmed",new{itemType=type,buffType=staff.buffType,wasActive=active,absent},
                    true,absent,InputsAcquired:false));
            }
            if(operation=="sell_npc_item") return new Handle(identity,operation,null,MerchantShop.Sell(identity,args));
            if(operation=="reforge_item") return new Handle(identity,operation,null,GoblinReforge.Execute(identity,args));
            if(operation=="heal_at_nurse") return new Handle(identity,operation,null,NurseService.Heal(identity,args));
            if(operation=="buy_npc_item") return new Handle(identity,operation,null,MerchantShop.Buy(identity,args));
            if(operation=="assign_npc_home") return new Handle(identity,operation,null,NpcHousing.Assign(identity,args));
            if(operation=="set_team")return new Handle(identity,operation,null,ServerRegroup.SetTeam(identity,args.GetProperty("team").GetInt32()));
            if(operation=="teleport_to_teammate")return new Handle(identity,operation,null,ServerRegroup.Teleport(identity,args.GetProperty("teammateRef").GetString()));
            if(operation=="open_inventory_item")return new Handle(identity,operation,null,ServerItemOpening.Open(identity,args));
            if(operation is "open_door" or "close_door")
            {
                string reason=ServerDoor.Set(p,args.GetProperty("x").GetInt32(),args.GetProperty("y").GetInt32(),operation=="open_door",out var proof);
                return new Handle(identity,operation,null,new(reason==null?"completed":reason=="door_state_unconfirmed"?"unknown":"failed",
                    reason??"door_state_verified",proof,true,reason==null,InputsAcquired:false));
            }
            if(operation=="configure_combat_loadout")
            {
                string reason=NativeCombatWorld.Configure(identity,args);
                if(reason!=null)return Reject(reason);
                return new Handle(identity,operation,null,new("completed","combat_loadout_configured",
                    NativeCombatWorld.Loadout(identity),true,true,InputsAcquired:false));
            }
            if(AiAtomicActions.IsTask(operation))
            {
                if(child)throw new InvalidOperationException("Nested root task denied");
                var executor=new ChildExecutor(this,continuous:operation!="build_place",buildTravel:operation=="build_place");
                CompanionTask task=operation==CombatProtocol.Operation
                    ?new CombatController(new NativeCombatWorld(identity,navigation,actionId,args.GetProperty("entityRef").GetString(),
                        args.TryGetProperty("threat",out _)?ReflexTargetAllowed??((_,_)=>true):null,admission,alternate:args.TryGetProperty("button",out var button) && button.GetString()=="right"),
                        args.TryGetProperty("threat",out var threat)?"reflex":"agent",threat.ValueKind==JsonValueKind.String?threat.GetString():null)
                    :operation==CombatProtocol.Boss
                    ?new BossEncounterTask(actionId,new NativeCombatWorld(identity,navigation,actionId,args.GetProperty("entityRef").GetString(),bossEncounter:true),navigation,args.TryGetProperty("regroupTeammateRef",out var teammate)?teammate.GetString():null,rejoin)
                    :operation=="mine_target"
                    ?args.TryGetProperty("cluster",out var cluster)&&cluster.GetBoolean()
                        ?new ExcavationTask(actionId,identity,args,executor,executor.Knowledge)
                        :new MineTargetTask(actionId,new NativeMineTargetWorld(identity,args),executor)
                    :operation=="collect_drops" ? new CollectDropsTask(actionId,args,executor)
                    :operation=="build_place" ? new BuildPlaceTask(actionId,args,executor)
                    :operation=="place_item" ? new NativeActionTask(executor,"place_item_native",args,actionId,operation)
                    :new AutonomousNavigationTask(actionId,new NativeNavigationWorld(identity,args,executor.Knowledge),executor);
                ulong budget=operation switch {CombatProtocol.Boss=>BossEncounterTask.Budget,CombatProtocol.Operation=>CombatController.Budget,"collect_drops"=>CollectDropsTask.Budget,
                    "build_place"=>10800,"place_item"=>660,_=>AutonomousNavigationTask.TripUpdates};
                context=new TaskSlot(task,actionId,null,identity with{GameUpdate=Main.GameUpdateCount},budget);
            }
            else if(operation=="move_input"){context=directInput.Start(identity,args,actionId);ActorControl.Driver=directInput;}
            else if(operation=="move_on_rope")
            {
                if(rope.Start(identity,args,out var run) is {} reason)return Reject(reason);
                context=run;ActorControl.Driver=rope;
            }
            else if (operation is "sort_inventory" or "sort_container") context = new ActorContainer.SortAction(identity, operation, args);
            else if (ActorContainer.IsBatch(operation)) context = new ActorContainer.Batch(identity, operation, args);
            else if (operation is "deposit_container_item" or "withdraw_container_item") context = new ActorContainer.Transfer(identity, operation, args);
            else if (operation is "open_container" or "close_container") context = new ActorContainer.Action(identity, operation, args);
            else if(ActorInventory.IsOperation(operation))context=ActorInventory.Begin(identity,operation,args);
            else if(ActorEquipment.IsOperation(operation))context=new ActorEquipment.Context(identity,operation,args);
            else if(operation=="craft_recipe")context=ActorCrafting.Begin(identity,args);
            else if(operation=="navigate_to_x"){context=navigation.Start(identity,args.GetProperty("targetX").GetSingle(),actionId,
                movementOnly:child && args.TryGetProperty("movementOnly",out var movementOnly) && movementOnly.GetBoolean());ActorControl.Driver=navigation;}
            else if(operation=="navigate_to_landing_region"){context=navigation.Start(identity,AtomicParameters.ReadLandingRegion(args),actionId,continuous,preferFlatRegion:child,knowledge:knowledge,
                movementOnly:buildTravel);ActorControl.Driver=navigation;}
            else if(operation=="place_rope")
            {
                if(p.noBuilding||p.CCed||p.noItems||p.frozen||p.stoned)return Reject("unsupported_placement_state");
                int slot=ActorRope.FindSlot(p);
                if(slot<0)return Reject("rope_material_unavailable");
                int x=args.GetProperty("x").GetInt32(),y=args.GetProperty("y").GetInt32();
                string error=ActorRope.Resolve(p,p.inventory[slot],x,y,out var target);
                if(error!=null)return Reject(error);
                if(p.selectedItem!=slot)ActorItemUse.Select(p,identity.World,identity.Generation,slot);
                ActorItemUse.StartPlacement(p,identity.World,identity.Generation,ActorPlacement.FromRope(p.HeldItem,target));
                context=ActorItemUse.Current;
            }
            else if(operation is "place_tile" or "place_object")
            {
                int x=args.GetProperty("x").GetInt32(),y=args.GetProperty("y").GetInt32();
                if(child&&operation=="place_tile"&&args.TryGetProperty("plannedPlacementSlot",out var plannedSlot))
                    ActorItemUse.Select(p,identity.World,identity.Generation,plannedSlot.GetInt32());
                string error=ActorPlacement.Prepare(p,x,y,operation=="place_object",out var placement);
                if(error!=null)return Reject(error,operation=="place_object"?ActorObjectPlacement.PreflightDetail(p,x,y,error):null);
                ActorItemUse.StartPlacement(p,identity.World,identity.Generation,placement);
                if(child&&operation=="place_tile"&&args.TryGetProperty("waterBridgeDirection",out var direction))
                    ActorItemUse.Current.Placement.WaterBridgeDirection=direction.GetInt32();
                context=ActorItemUse.Current;
            }
            else if(child&&operation=="place_item_native")
            {
                int x=args.GetProperty("x").GetInt32(),y=args.GetProperty("y").GetInt32();
                if(args.TryGetProperty("expectedType",out var expected) && (p.selectedItem is <0 or >9 || p.HeldItem.type!=expected.GetInt32()))return Reject("held_material_changed");
                string error=ActorPlacement.PrepareUnified(p,x,y,out var placement);
                if(error!=null)return new Handle(identity,operation,null,new("failed",error,null,true,InputsAcquired:false));
                ActorItemUse.StartPlacement(p,identity.World,identity.Generation,placement);
                context=ActorItemUse.Current;
            }
            else if(operation=="use_item" && NativeCombatWorld.MinionStaff(p.HeldItem))
            {
                if(NativeCombatWorld.SummonRefusal(p,p.HeldItem,true) is string error) return Reject(error);
                context=NativeCombatWorld.StartSummon(identity);
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
        if (h.Context is NativeCombatWorld summon) return summon.PollSummon();
        if (h.Context is ActorContainer.SortAction sort) return sort.Poll();
        if (h.Context is ActorContainer.Batch batch) return batch.Poll();
        if (h.Context is ActorContainer.Transfer transfer) return transfer.Poll();
        if (h.Context is ActorContainer.Action container) return container.Poll();
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
        if(h.Operation is "place_tile" or "place_object" or "place_rope" or "place_item_native")
        {
            if(!ReferenceEquals(ActorItemUse.Current,h.Context))return new("unknown","native_context_changed",null,false);
            var c=(ActorItemUse.Context)h.Context;var tile=c.Placement;
            bool present=ActorPlacement.Present(tile);
            bool placementVerified=tile.Placed&&!tile.ExternalChange&&present&&tile.NativeEvents==1&&!tile.UncertainNativeEffect&&tile.ConsumedCount==1
                &&(!tile.Object||ActorObjectPlacement.Validate(c)==null);
            bool verified=c.Phase=="completed" && placementVerified;
            string placementPhase=c.Phase=="rejected"?"failed":c.Phase=="stale"||c.Phase=="completed"&&!verified?"unknown":c.Phase;
            if(tile.Unified)
                return new(placementPhase,c.Phase=="completed"&&!verified?"native_placement_terminal_unconfirmed":c.Reason,
                    new {placementVerified,kind=tile.Kind,requested=new{x=tile.Rope?.X??tile.X,y=tile.Rope?.Y??tile.Y},
                        anchor=new{x=tile.X,y=tile.Y},footprint=tile.Object?ActorObjectPlacement.Bounds(tile):new{x=tile.X,y=tile.Y,width=1,height=1},
                        tileType=tile.TileType<0?(int?)null:tile.TileType,wallType=tile.WallType>0?(int?)tile.WallType:null,
                        style=tile.ActualStyle??tile.Style,alternate=tile.Object?(int?)tile.Alternate:null,direction=tile.Object?(int?)tile.Direction:null,
                        chestIndex=tile.ChestIndex>=0?(int?)tile.ChestIndex:null,chestVerified=tile.IsChest&&present,
                        itemType=c.Type,initialStack=c.InitialStack,remainingStack=p.inventory[c.Slot].stack,consumedCount=tile.ConsumedCount,
                        nativePlacementEvents=tile.NativeEvents,nativeClearEvents=tile.NativeClearEvents,targetPresent=present,placedByActor=tile.Placed,
                        targetChanged=tile.ExternalChange,uncertainNativeEffect=tile.UncertainNativeEffect},
                    Released(p)&&p.releaseUseItem&&p.itemAnimation==0&&p.itemTime==0&&p.reuseDelay==0,verified,InputsAcquired:c.Ticks>0);
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
            mine.Swings,mine.Strikes,mine.PeakDamage,mine.Transforms,toolKind=ActorMining.TreeTarget(mine.TileType)?"axe":"pickaxe",pick=mine.Pick,axe=mine.Axe,nativeDestroyed=mine.Destroyed,targetAbsent=destroyed,
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
        if (h.Context is NativeCombatWorld summon) return summon.PollSummon(cancel:true);
        if (h.Context is ActorContainer.SortAction sort) return sort.Poll(cancel: true);
        if (h.Context is ActorContainer.Batch batch) return batch.Poll(cancel: true);
        if (h.Context is ActorContainer.Transfer transfer) return transfer.Poll(cancel: true);
        if (h.Context is ActorContainer.Action container) return container.Poll(cancel: true);
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
        if(h.Operation is "place_object" or "place_item_native")
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
    public string Retreat(AiReadOnlyProtocol.Identity frame,object handle,int x,int y)=>
        handle is Handle {Context:TaskSlot {Task:BossEncounterTask boss}} h && TaskSlot.Same(frame,h.Identity)
            ?boss.RequestRetreat(frame,x,y):"boss_not_started";

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
        {if(task.Task is BossEncounterTask)ServerCombatFixture.WholeTerminal(task.Record.Id,result.Status,result.Reason);
         task.Record.Result=result;task.Record.Status=result.Status;task.Record.Phase="terminal";task.Record.Reason=result.Reason;}
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
        private readonly bool buildTravel;
        internal ChildExecutor(ServerAiNativeActions owner,bool continuous,bool buildTravel=false){this.owner=owner;this.continuous=continuous;this.buildTravel=buildTravel;}
        public string Validate(string op,JsonElement args)=>owner.Validate(op,args);
        public ulong ProgressVersion(object handle)=>handle is Handle {Context:ServerAiNavigation.Run run}?run.ProgressVersion:0;
        public object Start(AiReadOnlyProtocol.Identity id,string op,JsonElement args,string taskId)=>owner.StartCore(id,op,args,taskId,true,continuous,knowledge,buildTravel:buildTravel);
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
        finally
        {
            ServerRegroup.Clear();
            NativeCombatWorld.ClearLoadout();
            ServerDrops.Current = null;
            if(ReferenceEquals(ActorControl.Driver,navigation)||ReferenceEquals(ActorControl.Driver,directInput))ActorControl.Driver=null;
        }
    }
}
