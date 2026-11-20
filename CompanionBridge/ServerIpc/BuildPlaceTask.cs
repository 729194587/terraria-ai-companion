using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using CompanionBridge.Motion;
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
    private readonly Dictionary<Target,Entry> results=new();
    private readonly Dictionary<Target,NativePlacementSurvey.Result> observations=new();
    private readonly Dictionary<Target,Target[]> providers=new();
    private readonly Dictionary<int,int> consumed=new();
    private readonly HashSet<LandingRegion> failedSites=new();
    private readonly Dictionary<string,double> durations=new();
    private readonly Stopwatch clock=Stopwatch.StartNew();
    private double phaseStart;
    private sealed record Entry(string layer,int x,int y,int? itemType,string status,string reason,object nativeEvidence,NativePlacementSurvey.Obstacle blocker,string observedStatus);
    private Target active,previous;
    private NativeActionTask leaf;
    private BuildWorksiteSearch planner;
    private IEnumerator<int> search;
    private LandingRegion destination;
    private int sequence,surveys,passes,localSurveys,deferred,resolved,planned,visited,navigations,switches,attempts,successes,unknowns;
    private bool atSite,refreshed,held;
    private string phase="survey",lastTravelFailure;
    private ulong startTick;
    private int settlingTicks;
    private void PhaseTo(string next)
    {
        var now=clock.Elapsed.TotalMilliseconds;
        durations[phase]=durations.GetValueOrDefault(phase)+now-phaseStart;
        phaseStart=now;phase=next;
    }

    internal BuildPlaceTask(string id,JsonElement args,AiAtomicActions.IExecutor executor):base(id)
    { this.args=args.Clone(); this.executor=executor; }
    internal override string Name=>"build_place";
    internal override string Phase=>phase;
    internal override object Progress=>new{total=targets.Count,settled=results.Count,phase,site=destination,
        currentTarget=active==null?null:BuildDiff.Id(active),reservedItem=active?.Material?.type,navigationCalls=navigations};
    internal override object LastResult=>null;
    internal override Result Prepare(AiReadOnlyProtocol.Identity frame)
    {
        try { targets=BuildingBlueprint.Parse(args,(Player)frame.Actor); }
        catch(FormatException){return Failed("invalid_arguments");}
        startTick=frame.GameUpdate;
        passes++;
        foreach(var target in targets)Survey((Player)frame.Actor,target);
        PhaseTo("prepare");
        return null;
    }

    private void Record(Target t,string status,string reason,Result placement=null,NativePlacementSurvey.Result survey=null)
    {
        results[t]=new(t.Layer,t.X,t.Y,t.Material?.type,status,reason,CompactEvidence(placement),survey?.Blocker,survey?.Status);
    }

    private static object CompactEvidence(Result placement)
    {
        if(placement?.Evidence==null)return null;
        var proof=JsonSerializer.SerializeToElement(placement.Evidence);
        var fields=new Dictionary<string,JsonElement>();
        foreach(var key in new[]{"requested","itemType","kind","placedByActor","targetPresent","targetChanged",
            "uncertainNativeEffect","nativePlacementEvents","nativeClearEvents","consumedCount","chestVerified"})
            if(proof.TryGetProperty(key,out var value))fields.Add(key,value);
        return fields;
    }

    private Result Finish(AiReadOnlyProtocol.Identity frame,string status,string reason,bool released)
    {
        var entries=targets.Select(t=>results.GetValueOrDefault(t) ??
            new Entry(t.Layer,t.X,t.Y,t.Material?.type,"pending",reason,null,null,null)).ToArray();
        var counts=entries.GroupBy(e=>e.layer).ToDictionary(g=>g.Key,g=>new{
            placed=g.Count(e=>e.status=="placed"),matched=g.Count(e=>e.status=="matched"),
            pending=g.Count(e=>e.status!="placed" && e.status!="matched")});
        var completedByLayer=new[]{"tiles","walls","objects"}.ToDictionary(layer=>layer,
            layer=>entries.Count(e=>e.layer==layer && e.status is "placed" or "matched"));
        PhaseTo(status=="completed"?"completed":phase);
        var ledger=new BuildingMaterials((Player)frame.Actor);
        foreach(var t in targets.Where(t=>t.Supported))
            ledger.RequireOne(t.Material.type,results.TryGetValue(t,out var e) && e.status is "matched" or "placed");
        return new(status,reason,new{outcome=reason=="build_partial"?"partial":status,
            entries=entries.Select((e,i)=>new{e.layer,e.x,e.y,e.itemType,e.status,e.reason,e.nativeEvidence,e.blocker,e.observedStatus,
                id=BuildDiff.Id(targets[i]),left=targets[i].Left,top=targets[i].Top,width=targets[i].Width,height=targets[i].Height,nextAction=BuildDiff.Next(e.status is "placed" or "matched"?e.status:e.reason)}),
            counts,completedByLayer,materials=ledger.Summary(consumed),
            scheduler=new{phase,worksitesPlanned=planned,worksitesVisited=visited,navigationCalls=navigations,materialSwitches=switches,
                surveyPasses=passes,targetSurveys=surveys,localSurveys,nativePlacementAttempts=attempts,nativePlacementSuccesses=successes,
                nativePlacementUnknown=unknowns,dependencyDeferrals=deferred,deferredResolved=resolved},
            timing=new{surveyMs=durations.GetValueOrDefault("survey"),planningMs=durations.GetValueOrDefault("prepare")+durations.GetValueOrDefault("replan"),
                travelMs=durations.GetValueOrDefault("travel"),workMs=durations.GetValueOrDefault("work"),reconcileMs=durations.GetValueOrDefault("reconcile"),
                totalMs=clock.Elapsed.TotalMilliseconds,totalTicks=frame.GameUpdate-startTick},
            worldSessionId=frame.World,actorGeneration=frame.Generation,actorSlot=frame.Slot,inputsReleased=released},released,status=="completed");
    }

    private void Survey(Player p,Target t)
    {
        var observation=BuildDiff.Survey(p,t,out _);
        surveys++;
        bool wasDeferred=providers.TryGetValue(t,out var old) && old.Length>0;
        observations[t]=observation;
        providers[t]=BuildDiff.Providers(t,targets,observation);
        if(!wasDeferred && providers[t].Length>0)deferred++;
        if(wasDeferred && (observation.Attempt || observation.Status=="already_matches"))resolved++;
        if(observation.Status=="already_matches" && !results.ContainsKey(t))Record(t,"matched","already_matches",survey:observation);
    }

    private void Begin(string op,object arguments,string stage)
    {
        PhaseTo(stage);
        leaf=new NativeActionTask(executor,op,JsonSerializer.SerializeToElement(arguments),taskId+"/"+(++sequence));
    }

    private bool OnCycle(Target root,HashSet<Target> closed)
    {
        var seen=new HashSet<Target>();
        var queue=new Queue<Target>(providers[root]);
        while(queue.TryDequeue(out var next))
        {
            if(next==root)return true;
            if(!closed.Contains(next) || !seen.Add(next))continue;
            foreach(var parent in providers[next])queue.Enqueue(parent);
        }
        return false;
    }

    private void VerifyCompleted(Player p)
    {
        foreach(var t in targets)
        {
            if(!results.TryGetValue(t,out var e) || e.status is not ("placed" or "matched"))continue;
            var observed=BuildDiff.Survey(p,t,out _);surveys++;
            if(observed.Status!="already_matches")results[t]=e with{
                status=observed.Status=="unknown_or_not_loaded"?"unknown":"changed",reason=observed.Status,observedStatus=observed.Status};
        }
    }

    private Result Reconcile(AiReadOnlyProtocol.Identity frame,string noSite)
    {
        var p=(Player)frame.Actor;
        PhaseTo("reconcile");
        // Peel nodes with an alternative outside the blocked component.
        var closed=targets.Where(t=>!results.ContainsKey(t) && providers[t].Length>0).ToHashSet();
        while(closed.RemoveWhere(t=>providers[t].Any(parent=>!closed.Contains(parent)))>0){}
        foreach(var target in targets)
        {
            if(results.ContainsKey(target))continue;
            var s=observations[target];
            string reason=s.Status;
            if(closed.Contains(target) && OnCycle(target,closed))reason="dependency_cycle";
            else if(providers[target].Length>0)reason="dependency_blocked";
            else if(s.Attempt)reason=new BuildingMaterials(p).ReserveOne(target.Material.type)??noSite;
            Record(target,reason=="unknown_or_not_loaded"?"unknown":"blocked",reason,survey:s);
        }
        VerifyCompleted(p);
        bool complete=results.Values.All(e=>e.status is "placed" or "matched");
        bool unknown=results.Values.Any(e=>e.status=="unknown");
        bool hasProgress=results.Values.Any(e=>e.status is "placed" or "matched" or "changed");
        string reasonCode=unknown?"unknown_or_not_loaded":complete?"blueprint_verified":hasProgress?"build_partial":"blueprint_blocked";
        return Finish(frame,unknown?"unknown":complete?"completed":"failed",reasonCode,ServerAiNativeActions.Released(p));
    }

    private void RefreshPending(Player p)
    {
        PhaseTo("survey");passes++;
        foreach(var t in targets.Where(t=>!results.ContainsKey(t)))Survey(p,t);
        PhaseTo("prepare");
    }

    // Native wall placement may also fill an adjacent one-cell hole. Prefer a
    // continuous row and fill that hole first so one request consumes one item.
    private static int WallGapRisk(Target target)
    {
        if(target.Layer!="walls")return 0;
        (int x,int y)[] offsets={(-1,0),(1,0),(0,-1),(0,1)};
        return offsets.Count(d=>
        {
            int x=target.X+d.x,y=target.Y+d.y;
            return Main.tile[x,y].WallType==0 && offsets.Count(n=>Main.tile[x+n.x,y+n.y].WallType!=0)==3;
        });
    }

    private Result Select(AiReadOnlyProtocol.Identity frame,Player p)
    {
        if(search!=null)
        {
            var slice=Stopwatch.StartNew();
            do{if(!search.MoveNext())break; if(slice.ElapsedMilliseconds>=3)return Running();}while(true);
            search.Dispose();search=null;destination=planner.Destination;
            if(destination==null)return Reconcile(frame,lastTravelFailure??planner.Reason);
            planned++;navigations++;atSite=false;
            Begin("navigate_to_landing_region",new{minPlayerLeftX=destination.MinPlayerLeftX,maxPlayerLeftX=destination.MaxPlayerLeftX,
                surfaceY=destination.SurfaceY},"travel");
            return Running();
        }
        if(targets.Any(t=>!results.ContainsKey(t) && observations[t].Status=="unknown_or_not_loaded"))
            return Reconcile(frame,"resurvey_required");
        var pose=ServerAiNavigation.Observe(p);
        var bag=new BuildingMaterials(p);
        var ready=targets.Where(t=>!results.ContainsKey(t) && (observations[t].Attempt || observations[t].Status=="blocked_by_player") &&
            bag.CanHold(t.Material.type)).ToArray();
        var nearby=ready.Where(t=>observations[t].Attempt && ActorRope.InReach(p,t.Material,t.X,t.Y)).ToArray();
        if(nearby.Length>0 && pose.Settled)
        {
            if(!atSite){planned++;visited++;atSite=true;}
            active=nearby.OrderBy(WallGapRisk)
                .ThenByDescending(t=>providers.Count(edge=>edge.Value.Contains(t) && (t.Layer!="walls" || edge.Key.Layer!="walls")))
                .ThenByDescending(t=>p.HeldItem.type==t.Material.type)
                .ThenByDescending(t=>nearby.Count(other=>other.Material.type==t.Material.type))
                .ThenByDescending(t=>t.Layer=="walls"?t.Y:int.MinValue)
                .ThenBy(t=>previous==null?0:Math.Abs(t.X-previous.X)+Math.Abs(t.Y-previous.Y)).First();
            held=false;
            return null;
        }
        if(!refreshed && targets.Any(t=>!results.ContainsKey(t) && !observations[t].Attempt))
        {RefreshPending(p);refreshed=true;return Running();}
        if(failedSites.Count>=8)return Reconcile(frame,lastTravelFailure??"worksite_search_budget");
        if(ready.Length==0)return Reconcile(frame,"out_of_reach");
        if(!pose.Settled)return ++settlingTicks<60?Running():Reconcile(frame,"worksite_not_settled");
        settlingTicks=0;
        planner=new();search=planner.Find(p,ready,failedSites).GetEnumerator();PhaseTo("replan");return Running();
    }

    internal override Result Tick(AiReadOnlyProtocol.Identity frame)
    {
        var p=(Player)frame.Actor;
        if(leaf!=null)
        {
            bool alreadyStarted=leaf.Started;
            var r=RunChild(leaf,frame,phase=="travel"?1800UL:660UL);
            if(!alreadyStarted && leaf.Started && leaf.Name=="place_item_native")attempts++;
            if(r==null)return Running();
            string op=leaf.Name;leaf=null;
            if(op=="navigate_to_landing_region")
            {
                if(r.Status=="unknown" || !r.InputsReleased)return Finish(frame,"unknown",r.Reason,r.InputsReleased);
                var pose=ServerAiNavigation.Observe(p);
                if(r.Status=="completed" && pose.Settled && destination.Contains(pose.State)){atSite=true;visited++;}
                else {lastTravelFailure=r.Reason;failedSites.Add(destination);}
                if(atSite){lastTravelFailure=null;failedSites.Add(destination);}
                RefreshPending(p);refreshed=true;return Running();
            }
            PhaseTo("reconcile");
            bool placement=op=="place_item_native";
            if(placement || r.Status!="completed"){Survey(p,active);localSurveys++;}
            var observed=observations[active];
            if(r.Status!="completed" || placement)
            {
                bool verified=r.Status=="completed" && observed.Status=="already_matches";
                bool unknown=r.Status=="unknown" || !r.InputsReleased || r.Status=="completed" && !verified;
                Record(active,unknown?"unknown":verified?"placed":"failed",r.Reason,placement?r:null,observed);
                if(verified)
                {
                    successes++;previous=active;refreshed=false;failedSites.Clear();
                    var proof=JsonSerializer.SerializeToElement(r.Evidence);
                    if(proof.TryGetProperty("consumedCount",out var count) && count.TryGetInt32(out int n))
                        consumed[active.Material.type]=consumed.GetValueOrDefault(active.Material.type)+n;
                    foreach(var t in targets.Where(t=>!results.ContainsKey(t) && providers[t].Contains(active)))
                    {Survey(p,t);localSurveys++;}
                }
                active=null;
                if(unknown)
                {
                    if(placement)unknowns++;
                    VerifyCompleted(p);
                    return Finish(frame,"unknown","build_child_unconfirmed",r.InputsReleased);
                }
            }
            else if(results.ContainsKey(active))active=null;
        }
        PhaseTo("prepare");
        if(active==null)
        {
            var step=Select(frame,p);
            if(step!=null)return step;
        }
        Survey(p,active);localSurveys++;
        var survey=observations[active];
        if(!survey.Attempt || !ActorRope.InReach(p,active.Material,active.X,active.Y))
        {active=null;return Running();}
        if(!ServerAiNavigation.Observe(p).Settled){atSite=false;active=null;return Running();}
        if(survey.Shape is {} shape && targets.Any(other=>other!=active && other.Layer!="walls" &&
            active.X-shape.OriginX < other.Left+other.Width && active.X-shape.OriginX+shape.Width > other.Left &&
            active.Y-shape.OriginY < other.Top+other.Height && active.Y-shape.OriginY+shape.Height > other.Top))
        {Record(active,"blocked","blueprint_footprint_conflict",survey:survey);active=null;return Running();}
        int type=active.Material.type;
        if(p.selectedItem is >=0 and <10 && p.HeldItem.type==type)
        {
            Begin("place_item_native",new{x=active.X,y=active.Y,expectedType=type},"work");
        }
        else
        {
            int source=Array.FindIndex(p.inventory,0,50,i=>i!=null && !i.IsAir && i.type==type);
            if(held || source<0){Record(active,"blocked",held?"held_material_changed":"missing_material");active=null;return Running();}
            int dst=source<10?source:Math.Clamp(p.selectedItem,0,9);
            held=true;switches++;
            Begin("hold_inventory_item",new{sourceSlot=source,hotbarKey=HotbarSlots.KeyForInventorySlot(dst),
                sourceToken=ActorInventory.Token(frame,p.inventory[source],source),destinationToken=ActorInventory.Token(frame,p.inventory[dst],dst)},"work");
        }
        return Running();
    }

    internal override void Abandon(){search?.Dispose();base.Abandon();}

    internal override Result Stop(AiReadOnlyProtocol.Identity frame,string status,string reason)
    {
        search?.Dispose();
        bool started=leaf?.Started==true;
        var r=base.Stop(frame,status,reason);
        if(started && active!=null && leaf.Name=="place_item_native" && lastResult!=null)
        {
            var proof=JsonSerializer.SerializeToElement(lastResult.Evidence);
            bool placed=lastResult.Status!="unknown" && lastResult.InputsReleased && proof.ValueKind==JsonValueKind.Object &&
                proof.TryGetProperty("placementVerified",out var verified) && verified.ValueKind==JsonValueKind.True &&
                BuildDiff.Observe((Player)frame.Actor,active,out _)=="already_matches";
            if(placed)
            {
                successes++;
                if(proof.TryGetProperty("consumedCount",out var amount) && amount.TryGetInt32(out int n))
                    consumed[active.Material.type]=consumed.GetValueOrDefault(active.Material.type)+n;
            }
            if(lastResult.Status=="unknown")unknowns++;
            Record(active,placed?"placed":lastResult.Status,lastResult.Reason,lastResult);
        }
        VerifyCompleted((Player)frame.Actor);
        return Finish(frame,r.Status,r.Reason,r.InputsReleased && ServerAiNativeActions.Released((Player)frame.Actor));
    }
}
