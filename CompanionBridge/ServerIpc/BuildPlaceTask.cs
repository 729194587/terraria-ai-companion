using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Terraria;
using VirtualPlayerEngineProbe;
using Target = CompanionBridge.ServerIpc.BuildingBlueprint.Target;
using Result = CompanionBridge.ServerIpc.AiAtomicActions.NativeResult;

namespace CompanionBridge.ServerIpc;

internal sealed class BuildPlaceTask : AbstractCompanionTask
{
    private readonly JsonElement args;
    private readonly AiAtomicActions.IExecutor executor;
    private IReadOnlyList<Target> targets = Array.Empty<Target>();
    private readonly Queue<Target> pending = new();
    private readonly List<Target> deferred = new();
    private readonly Dictionary<Target,Entry> results = new();
    private sealed record Entry(string layer,int x,int y,int? itemType,string status,string reason,object nativeEvidence);
    private Target active;
    private NativeActionTask leaf;
    private int sequence;
    private bool approached,preparedMaterial,changed;
    private string phase="inspect";

    internal BuildPlaceTask(string id,JsonElement args,AiAtomicActions.IExecutor executor):base(id)
    { this.args=args.Clone(); this.executor=executor; }
    internal override string Name=>"build_place";
    internal override string Phase=>phase;
    internal override object Progress=>new{total=targets.Count,settled=results.Count};
    internal override object LastResult=>null;
    internal override Result Prepare(AiReadOnlyProtocol.Identity frame)
    {
        try { targets=BuildingBlueprint.Parse(args); }
        catch(FormatException){return Failed("invalid_arguments");}
        foreach(var t in targets.OrderBy(t=>t.Layer=="tiles"?0:t.Layer=="walls"?1:2).ThenByDescending(t=>t.Y))
            pending.Enqueue(t);
        return null;
    }

    private void Record(Target t,string status,string reason,Result placement=null)
    {
        results[t]=new(t.Layer,t.X,t.Y,t.Material?.type,status,reason,CompactEvidence(placement));
    }

    private static object CompactEvidence(Result placement)
    {
        if(placement?.Evidence==null)return null;
        var proof=JsonSerializer.SerializeToElement(placement.Evidence);
        var fields=new Dictionary<string,JsonElement>();
        foreach(var key in new[]{"requested","itemType","kind","placedByActor","targetPresent","targetChanged",
            "uncertainNativeEffect","nativePlacementEvents","consumedCount","chestVerified"})
            if(proof.TryGetProperty(key,out var value))fields.Add(key,value);
        return fields;
    }

    private Result Finish(AiReadOnlyProtocol.Identity frame,string status,string reason,bool released)
    {
        var entries=targets.Select(t=>results.GetValueOrDefault(t) ??
            new Entry(t.Layer,t.X,t.Y,t.Material?.type,"pending",reason,null)).ToArray();
        var counts=entries.GroupBy(e=>e.layer).ToDictionary(g=>g.Key,g=>new{
            placed=g.Count(e=>e.status=="placed"),matched=g.Count(e=>e.status=="matched"),
            pending=g.Count(e=>e.status!="placed" && e.status!="matched")});
        var completedByLayer=new[]{"tiles","walls","objects"}.ToDictionary(layer=>layer,
            layer=>entries.Count(e=>e.layer==layer && e.status is "placed" or "matched"));
        return new(status,reason,new{outcome=reason=="build_partial"?"partial":status,entries,counts,completedByLayer,worldSessionId=frame.World,actorGeneration=frame.Generation,
            actorSlot=frame.Slot,inputsReleased=released},released,status=="completed");
    }

    private void Begin(string op,object arguments)
    {
        phase=op;
        leaf=new NativeActionTask(executor,op,JsonSerializer.SerializeToElement(arguments),taskId+"/"+(++sequence));
    }

    internal override Result Tick(AiReadOnlyProtocol.Identity frame)
    {
        var player=(Player)frame.Actor;
        if(leaf!=null)
        {
            var r=RunChild(leaf,frame,phase=="navigate_to_x"?1800UL:660UL);
            if(r==null)return Running();
            leaf=null;
            if(r.Status!="completed" || phase=="place_item_native")
            {
                bool verified=r.Status=="completed" && BuildDiff.Observe(player,active,out _)=="already_matches";
                bool unknown=r.Status=="unknown" || !r.InputsReleased || r.Status=="completed" && !verified;
                Record(active,unknown?"unknown":verified?"placed":"failed",r.Reason,phase=="place_item_native"?r:null);
                changed|=verified;
                active=null;
                if(unknown)return Finish(frame,"unknown","build_child_unconfirmed",r.InputsReleased);
            }
        }
        phase="inspect";
        if(active==null)
        {
            if(pending.Count==0 && deferred.Count>0)
            {
                foreach(var t in deferred)
                    if(changed)pending.Enqueue(t);else Record(t,"blocked","missing_support_or_anchor");
                deferred.Clear(); changed=false;
            }
            if(!pending.TryDequeue(out active))
            {
                foreach(var target in targets)
                {
                    var entry=results[target];
                    if(entry.status is not ("placed" or "matched"))continue;
                    string observed=BuildDiff.Observe(player,target,out _);
                    if(observed!="already_matches")
                        results[target]=entry with{status="changed",reason=observed??"target_no_longer_matches"};
                }
                bool complete=results.Values.All(e=>e.status is "placed" or "matched");
                return Finish(frame,complete?"completed":"failed",complete?"blueprint_verified":"build_partial",ServerAiNativeActions.Released(player));
            }
            approached=false;preparedMaterial=false;
        }
        string reason=BuildDiff.Observe(player,active,out bool reachable);
        if(reason=="missing_support_or_anchor"){deferred.Add(active);active=null;return Running();}
        reason??=new BuildingMaterials(player).ReserveOne(active.Material.type);
        if(reason==null && !reachable)
        {
            // Horizontal native motion only. Never invoke the autonomous edit planner.
            float goal=active.X*16+8+(player.Center.X<active.X*16?-48:48)-player.width/2f;
            if(!approached && Math.Abs(goal-player.position.X)<=64*16 && Math.Abs(goal-player.position.X)>8)
            { approached=true;Begin("navigate_to_x",new{targetX=goal,movementOnly=true});return Running(); }
            reason="out_of_reach";
        }
        if(reason!=null)
        {
            Record(active,reason=="already_matches"?"matched":"blocked",reason);
            active=null;return Running();
        }
        int type=active.Material.type;
        if(player.selectedItem is >=0 and <10 && player.HeldItem.type==type)
            Begin("place_item_native",new{x=active.X,y=active.Y,expectedType=type});
        else if(preparedMaterial)
        {Record(active,"blocked","held_material_changed");active=null;}
        else
        {
            int source=Array.FindIndex(player.inventory,0,50,i=>i!=null && !i.IsAir && i.type==type);
            int destination=source<10?source:Math.Clamp(player.selectedItem,0,9);
            preparedMaterial=true;
            Begin("hold_inventory_item",new{sourceSlot=source,hotbarKey=HotbarSlots.KeyForInventorySlot(destination),
                sourceToken=ActorInventory.Token(frame,player.inventory[source],source),
                destinationToken=ActorInventory.Token(frame,player.inventory[destination],destination)});
        }
        return Running();
    }

    internal override Result Stop(AiReadOnlyProtocol.Identity frame,string status,string reason)
    {
        bool started=leaf?.Started==true;
        var r=base.Stop(frame,status,reason);
        if(started && active!=null && phase=="place_item_native" && lastResult!=null)
        {
            var proof=JsonSerializer.SerializeToElement(lastResult.Evidence);
            bool placed=lastResult.Status!="unknown" && lastResult.InputsReleased && proof.ValueKind==JsonValueKind.Object &&
                proof.TryGetProperty("placementVerified",out var verified) && verified.ValueKind==JsonValueKind.True &&
                BuildDiff.Observe((Player)frame.Actor,active,out _)=="already_matches";
            Record(active,placed?"placed":lastResult.Status,lastResult.Reason,lastResult);
        }
        return Finish(frame,r.Status,r.Reason,r.InputsReleased && ServerAiNativeActions.Released((Player)frame.Actor));
    }
}
