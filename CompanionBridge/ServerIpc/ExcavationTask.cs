using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using Terraria;
using Terraria.ID;
using CompanionBridge.Common.Systems;
using CompanionBridge.Motion;
using VirtualPlayerEngineProbe;
using Result = CompanionBridge.ServerIpc.AiAtomicActions.NativeResult;

namespace CompanionBridge.ServerIpc;

internal sealed partial class ExcavationTask(string id,AiReadOnlyProtocol.Identity identity,JsonElement args,
    AiAtomicActions.IExecutor native,NavigationKnowledge knowledge,bool singleOre=false) : AbstractCompanionTask(id)
{
    private sealed class Target(ExcavationCell cell)
    {
        internal readonly ExcavationCell Cell=cell;
        internal ExcavationProof Proof;
        internal string ChangeReason;
        internal bool Changed=>ChangeReason!=null;
        internal string Status=>Changed?"changed":Proof==null?"pending":"verified";
        internal object View()=>new{x=Cell.X,y=Cell.Y,typeId=Cell.Type,status=Status,proof=Proof,changeReason=ChangeReason};
    }

    private const int DiscoveryLimit=128;
    private readonly List<Target> targets=new();
    private readonly Queue<(int X,int Y)> frontier=new();
    private readonly HashSet<(int,int)> seen=new();
    private readonly HashSet<ExcavationCell> attemptedOpenings=new();
    private ExcavationCell openingTarget;
    private bool discoveryTruncated,routeBudgetReached;
    private NativeMineTargetWorld directTarget;
    private readonly TerrainRouteMemory stanceSearch=new();
    private ulong lastUpdate;
    private readonly HashSet<LandingRegion> localRouteProbes=new();
    private (int Left,int Right,int Top,int Bottom) departureBounds;
    private (int Left,int Right,int Top,int Bottom) workBounds;
    private readonly Player player=(Player)identity.Actor;
    private readonly int initialLife=((Player)identity.Actor).statLife;
    private CompanionTask active;
    private NativeExcavation.Plan activePlan;
    private int? continuationSlot;
    private int expectedPending;
    private IEnumerator<int> search;
    private PhysicsState searchStart;
    private long terrain;
    private int capability,searchTargetCount,invalidations,sequence,seedX,seedY,seedType;
    private string stage="preflight",outcome="running";
    internal override string Name=>"mine_target";
    internal override string Phase=>stage;
    internal override object Progress=>new{stage,completedChildren,settledChildren,children=children.ToArray(),
        childrenTruncated=settledChildren>children.Count,omittedChildren=settledChildren-children.Count,
        excavation=Evidence(outcome)};

    internal override Result Prepare(AiReadOnlyProtocol.Identity frame)
    {
        ServerAiNativeActions.Require(identity);
        if(!ActorMining.Healthy(player)||!GameNavigation.Ordinary(player)||player.width!=PhysicsSimulator.PlayerW||player.height!=PhysicsSimulator.PlayerH)return Failed("unsupported_mining_profile");
        if(SceneMineTargets.Resolve(identity,args,out seedX,out seedY) is {} error)return Failed(error);
        if(!WorldGen.InWorld(seedX,seedY,2)||!Main.tile[seedX,seedY].HasUnactuatedTile)return Failed("invalid_mining_target");
        seedType=Main.tile[seedX,seedY].TileType;
        if(!TileID.Sets.Ore[seedType]||Main.tileFrameImportant[seedType])return Failed("excavation_seed_not_ore");

        if(singleOre)
        {
            directTarget=new NativeMineTargetWorld(identity,args);
            if(directTarget.Prepare() is {} failure)return Failed(failure);
        }
        AddTarget(seedX,seedY);
        return null;
    }

    private void AddTarget(int x,int y)
    {
        targets.Add(new(ExcavationCell.Capture(x,y)));
        if(singleOre)return;
        frontier.Enqueue((x-1,y));frontier.Enqueue((x+1,y));frontier.Enqueue((x,y-1));frontier.Enqueue((x,y+1));
        seen.Add((x,y));
    }

    private void Discover()
    {
        for(int work=0;work<64&&frontier.TryDequeue(out var point);work++)
        {
            var (x,y)=point;
            if(!seen.Add(point)||!WorldGen.InWorld(x,y,2))continue;
            var tile=Main.tile[x,y];
            if(!tile.HasUnactuatedTile||tile.TileType!=seedType)continue;
            if(targets.Count==DiscoveryLimit){discoveryTruncated=true;frontier.Clear();break;}
            AddTarget(x,y);
        }
    }

    internal override void Start(AiReadOnlyProtocol.Identity frame)
    {
        stage="discover";lastUpdate=frame.GameUpdate;
        var departure=ServerAiNavigation.Observe(player).State;
        int x=(int)((departure.Px+10)/16),y=(int)((departure.Py+42)/16);
        departureBounds=(x-22,x+22,y-17,y+11);
    }

    internal override Result Tick(AiReadOnlyProtocol.Identity frame)
    {
        if(NativeNavigationWorld.CheckHazards(identity,initialLife) is {} danger)return Stop(frame,"cancelled",danger);
        if(!ActorMining.Healthy(player)||stage is not ("mining" or "opening" or "clearing" or "placing")&&!GameNavigation.Ordinary(player))
            return Stop(frame,"cancelled","actor_cannot_mine");
        ulong elapsed=frame.GameUpdate-lastUpdate;
        lastUpdate=frame.GameUpdate;
        updates[Category(stage)]=updates.GetValueOrDefault(Category(stage))+elapsed;
        if(stage is "planning" or "navigating" or "approaching" or "clearing" or "placing" or "settling" or "opening")
        {
            stanceSearch.Charge(elapsed,stage is "planning" or "navigating" or "approaching");
            if(stanceSearch.WorkExhausted)return Stop(frame,"timeout","excavation_work_search_budget_exhausted");
            if(stanceSearch.TravelExhausted&&(stage is "navigating" or "approaching"||stage=="opening"&&active?.Phase=="navigating"))
            {
                return Stop(frame,"timeout","excavation_route_search_budget_exhausted");
            }
        }
        if(route!=null)
        {
            var result=route.Tick(this,frame);
            stage=route.Phase;
            if(result==null)return Running();
            if(result.Status!="completed"&&route.MoveRegion is {} rejected)stanceSearch.RejectOpeningMove(rejected);
            CollectRoute();
            if(result.Status=="unknown"||!result.InputsReleased)return result with{Evidence=Evidence(outcome="unknown")};
            if(result.Status!="completed")
            {
                if(result.Status is not ("failed" or "timeout"))return Finish(result.Status,result.Reason);
                replans++;
                if(++invalidations>=AutonomousNavigationTask.MaxConsecutiveInvalidations)
                    return Finish("timeout","excavation_replan_budget_exhausted");
            }
            ReleaseSearch();stage="observe";
        }
        if(stage=="discover")
        {
            Discover();
            if(frontier.Count>0)return Running();
            workBounds=(targets.Min(t=>t.Cell.X)-8,targets.Max(t=>t.Cell.X)+8,
                targets.Min(t=>t.Cell.Y)-10,targets.Max(t=>t.Cell.Y)+8);
            stage="observe";
        }
        if(active!=null)
        {
            if(stage=="opening"&&active is NativeActionTask {Started:false}
                &&(!openingTarget.Matches()||directTarget.Revalidate()!=null||!directTarget.CanMine()))
            {base.Stop(frame,"cancelled","excavation_needs_replan");active=null;openingTarget=null;stage="observe";return Running();}
            if(stage is not ("mining" or "opening" or "clearing" or "placing")&&(capability!=ActorMining.CapabilityStamp(player)
                ||targets.Any(t=>!t.Changed&&t.Proof==null&&!t.Cell.Matches())))
            {
                var stopped=base.Stop(frame,"cancelled","excavation_needs_replan");
                if(stopped.Status=="unknown"||!stopped.InputsReleased)
                {outcome="unknown";return stopped with{Evidence=Evidence(outcome)};}
                replans++;active=null;ReleaseSearch();stage="observe";return Running();
            }
            var result=RunChild(active,frame,stage=="mining"?605UL:900UL);
            if(result==null)return Running();
            active=null;
            Record(result);
            if(result.Status=="unknown"||!result.InputsReleased)
            {
                outcome="unknown";
                return result with{Evidence=Evidence(outcome)};
            }
            if(stage is "navigating" or "approaching" && result.Status!="completed")navigationFailures++;
            if(stage=="mining")
            {
                continuationSlot=result.Status=="completed"?activePlan.NextSlot:null;
                expectedPending=searchTargetCount-activePlan.Cells.Length;
                activePlan=null;
                bool changedDuringRun=result.Status=="failed"&&result.Reason=="target_changed"
                    &&targets.Any(t=>t.Proof==null&&!t.Changed&&!t.Cell.Matches());
                if(result.Status!="completed"&&result.Reason!="excavation_needs_replan"&&!changedDuringRun)
                    return Finish(result.Status,result.Reason);
                if(result.Evidence is MiningRunReceipt {verified.Length:>0})
                {stanceSearch.Mined();invalidations=0;changesSinceOre=0;}
                else if(++invalidations>=AutonomousNavigationTask.MaxConsecutiveInvalidations)
                    return Finish("failed","excavation_replan_budget_exhausted");
            }
            else if(stage=="opening")
            {
                openingTarget=null;
                if(result.Status!="completed")return Finish(result.Status,result.Reason);
                if(singleOre)return result with{Reason="mine_target_verified"};
                stanceSearch.Mined();changesSinceOre=0;
            }
            else if(result.Status!="completed")
            {
                if(result.Status=="timeout")routeBudgetReached=true;
                else if(result.Status!="failed")return Finish(result.Status,result.Reason);
            }
            stage="observe";
        }
        RefreshTargets();
        var pending=targets.Where(t=>t.Status=="pending").Select(t=>t.Cell).ToArray();
        if(pending.Length==0&&discoveryTruncated)return Finish("timeout","excavation_discovery_budget_exhausted");
        if(pending.Length==0)return targets.Count>0&&targets.All(t=>t.Status=="verified")
            ?Finish("completed","excavation_cluster_verified")
            :Finish("failed","excavation_target_changed");

        var observed=ServerAiNavigation.Observe(player);
        if(!observed.Settled)return Running();
        var pose=observed.State;
        var world=new NativeMotionWorld(pose);
        var physics=NativeNavigation.Parameters(player);
        int currentCapability=ActorMining.CapabilityStamp(player);
        long currentTerrain=world.TerrainSignature(pose);
        // The navigation graph follows the actor. Negative work-position evidence instead
        // watches world coordinates around the vein and the original approach.
        bool changed=RefreshStances(currentCapability,physics,pending.Length);
        if(changed||search==null||pose!=searchStart||currentTerrain!=terrain)
        {
            int? next=pose==searchStart&&currentCapability==capability&&pending.Length==expectedPending?continuationSlot:null;
            continuationSlot=null;
            if(changed||pose!=searchStart||currentTerrain!=terrain)localRouteProbes.Clear();
            ReleaseSearch();
            searchStart=pose;terrain=currentTerrain;capability=currentCapability;searchTargetCount=pending.Length;
            var here=world.SafeLanding(pose)?NativeExcavation.Choose(identity,pose,pending,new(TerrainReader.ReadEnvironment),ordered:false,continuationSlot:next):null;
            if(singleOre && directTarget.CanMine())
            {
                StartSingle(pending[0]);return Running();
            }
            if(!singleOre&&here!=null)
            {
                stage="mining";activePlan=here;active=new MiningRunTask(identity,here,pose);
                return Running();
            }
            // A fixed-stance run cannot include the final supporting ore. A
            // single cut can use the existing bounded, dry fall prediction.
            var support=pending.FirstOrDefault(c=>c.Y*16>=pose.Py+40&&c.Y*16<=pose.Py+43
                &&c.X*16<pose.Px+20&&(c.X+1)*16>pose.Px&&!attemptedOpenings.Contains(c));
            if(support!=null&&new NativeNavigationEdits(identity,pose).LandingAfterCut(pose,new[]{(support.X,support.Y)}).HasValue)
            {
                if(TrySupport(support))return Running();
            }
            corridor=new(identity,pose,(x,y)=>targets.Any(t=>t.Cell.X==x&&t.Cell.Y==y)
                ||identity.Autonomy!=null&&AutonomyPolicy.ProtectedTile(x,y));
            planner=new(identity,knowledge,stanceSearch,localRouteProbes,new(identity,pending),corridor,
                LocalEditEvaluation.MaxChanges-changesSinceOre);
            search=planner.Run(pose,world,physics,terrain).GetEnumerator();
        }
        stage="planning";
        var timer=Stopwatch.StartNew();
        try
        {
            for(int work=0;work<16&&timer.Elapsed.TotalMilliseconds<3;work++)
            {
                if(!stanceSearch.TravelExhausted&&search.MoveNext())continue;
                routeBudgetReached|=stanceSearch.TravelExhausted||planner.routeBudgetReached;
                ReleaseSearch();
                var destination=stanceSearch.TravelExhausted?null:planner.Destination;
                if(!stanceSearch.TravelExhausted)
                {
                    if(decisions.Count==8)decisions.Dequeue();
                    decisions.Enqueue(planner.Decide(capability));
                    if(planner.Selected!=null)
                    {
                        route=new(identity,native,corridor,planner.Selected,physics,capability,taskId+"/route-"+(++sequence));
                        stage=route.Phase;
                        return Running();
                    }
                }
                if(destination==null)
                    return routeBudgetReached||stanceSearch.TravelExhausted
                        ?Finish("timeout","excavation_route_search_budget_exhausted")
                        :Finish("failed",planner.unsupported?"excavation_unsupported_terrain":"excavation_no_plan_in_search_window");
                var region=destination.Region;
                stanceSearch.Attempt(region,destination.Batch);
                var arguments=NativeNavigationWorld.RegionArguments(region);
                navigationDispatches++;
                stage=TerrainApproachPlanner.InLocalSearch(pose,region)?"navigating":"approaching";
                active=new NativeActionTask(native,"navigate_to_landing_region",arguments,taskId+"/"+stage+"-"+(++sequence));

                return Running();
            }
            return Running();
        }
        finally
        {
            double ms=timer.Elapsed.TotalMilliseconds;
            planningMs+=ms;maxSliceMs=Math.Max(maxSliceMs,ms);
        }
    }

    private bool RefreshStances(int currentCapability,PhysicsParameters physics,int pending)
    {
        if(!stanceSearch.Observe(workBounds,departureBounds,currentCapability,physics,pending))return false;
        localRouteProbes.Clear();attemptedOpenings.Clear();routeBudgetReached=false;
        if(openingTarget!=null)attemptedOpenings.Add(openingTarget);
        return true;
    }

    private void StartSingle(ExcavationCell cell)
    {
        attemptedOpenings.Add(cell);
        openingTarget=cell;
        active=new NativeActionTask(native,"mine_tile",directTarget.MineArguments(),taskId+"/mine-"+(++sequence));
        stage="opening";
    }

    private void Record(Result result)
    {
        if(stage=="opening"&&openingTarget!=null&&result is {Status:"completed",EffectVerified:true,InputsReleased:true})
        {
            var e=JsonSerializer.SerializeToElement(result.Evidence);
            if(e.GetProperty("destroyedByActor").GetBoolean()&&e.GetProperty("X").GetInt32()==openingTarget.X
                &&e.GetProperty("Y").GetInt32()==openingTarget.Y&&e.GetProperty("TileType").GetInt32()==openingTarget.Type)
                targets.Single(t=>t.Cell==openingTarget).Proof=new(openingTarget.X,openingTarget.Y,openingTarget.Type,
                    e.GetProperty("Ticks").GetInt32(),e.GetProperty("Strikes").GetInt32());
            return;
        }
        if(stage!="mining"||result?.Evidence is not MiningRunReceipt run)return;
        foreach(var proof in run.verified)
            targets.Single(t=>t.Cell.X==proof.X&&t.Cell.Y==proof.Y).Proof=proof;
    }
    private void RefreshTargets()
    {
        foreach(var t in targets.Where(t=>!t.Changed))
        {
            bool present=Main.tile[t.Cell.X,t.Cell.Y].HasTile;
            if(t.Proof==null&&!t.Cell.Matches())t.ChangeReason=present?"identity_changed":"removed_externally";
            else if(t.Proof!=null&&present)t.ChangeReason="replaced_after_verified_cut";
        }
    }
    private object Evidence(string outcome)=>new{outcome,seedX,seedY,typeId=seedType,approach=ApproachEvidence(),workSearchUpdates=stanceSearch.WorkUpdates,routeSearchUpdates=stanceSearch.TravelUpdates,stanceAttempts=stanceSearch.Attempts,discoveryComplete=!discoveryTruncated&&frontier.Count==0,targets=targets.Select(t=>t.View()).ToArray()};
    private Result Finish(string status,string reason)
    {
        ReleaseSearch();RefreshTargets();
        outcome=status=="unknown"?"unknown":status=="completed"?"completed":status=="cancelled"?"interrupted"
            :targets.Any(t=>t.Proof!=null)?"partial":"failed";
        return new(status,reason,Evidence(outcome),true,status=="completed",
            InputsAcquired:completedChildren>0||lastResult?.InputsAcquired==true||targets.Any(t=>t.Proof!=null));
    }
    private void ReleaseSearch(){search?.Dispose();search=null;}
    internal override Result Stop(AiReadOnlyProtocol.Identity frame,string status,string reason)
    {
        bool hadChild=CurrentChild!=null;
        var stopped=base.Stop(frame,status,reason);
        ReleaseSearch();
        if(route!=null){if(hadChild)route.Record(lastResult);CollectRoute();}
        else if(hadChild)Record(lastResult);
        if(stopped.Status=="unknown"||!stopped.InputsReleased)
        {
            outcome="unknown";
            return stopped with{Evidence=Evidence(outcome)};
        }
        if(singleOre&&stage=="opening"&&hadChild&&lastResult is {Status:"completed",InputsReleased:true,EffectVerified:true}&&directTarget.TargetAbsent())
            return lastResult with{Reason="mine_target_verified"};
        active=null;
        return Finish(status,reason);
    }
    internal override object AbandonEvidence=>Evidence("unknown");
    internal override void Abandon()
    {
        bool hadChild=CurrentChild!=null;
        outcome="unknown";ReleaseSearch();base.Abandon();
        // Only copy already-owned strike receipts. The replacement binding
        // must never be used to refresh the old world's remaining tiles.
        if(route!=null){if(hadChild)route.Record(lastResult);CollectRoute();}
        else if(hadChild)Record(lastResult);
    }
}
