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

    private sealed record WorkPose(LandingRegion Region,ExcavationCell[] Batch,int Coverage,float Mining,float LowerBound);
    private const int DiscoveryLimit=128;
    private readonly List<Target> targets=new();
    private readonly Queue<(int X,int Y)> frontier=new();
    private readonly HashSet<(int,int)> seen=new();
    private readonly HashSet<ExcavationCell> attemptedOpenings=new();
    private ExcavationCell openingTarget;
    private bool discoveryTruncated,routeBudgetReached;
    private NativeMineTargetWorld directTarget;
    private readonly ExcavationStanceSearch stanceSearch=new();
    private ulong lastUpdate;
    private PhysicsState? exhaustedAt;
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
    private WorkPose destination;
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
        if(!ActorMining.Healthy(player)||stage is not ("mining" or "opening" or "clearing")&&!GameNavigation.Ordinary(player))
            return Stop(frame,"cancelled","actor_cannot_mine");
        ulong elapsed=frame.GameUpdate-lastUpdate;
        lastUpdate=frame.GameUpdate;
        updates[Category(stage)]=updates.GetValueOrDefault(Category(stage))+elapsed;
        if(stage is "planning" or "navigating" or "approaching" or "clearing" or "settling" or "opening")
        {
            stanceSearch.Charge(elapsed,stage is "planning" or "navigating" or "approaching");
            if(stanceSearch.WorkExhausted)return Stop(frame,"timeout","excavation_work_search_budget_exhausted");
            if(stanceSearch.TravelExhausted&&(stage is "navigating" or "approaching"||stage=="opening"&&active?.Phase=="navigating"))
            {
                var stopped=base.Stop(frame,"timeout","excavation_route_search_budget_exhausted");
                if(stopped.Status=="unknown"||!stopped.InputsReleased)
                    return stopped with{Evidence=Evidence(outcome="unknown")};
                return Finish("timeout","excavation_route_search_budget_exhausted");
            }
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
            if(active is NativeActionTask {Started:false} && selected!=null && !ReadyStep())
            {base.Stop(frame,"cancelled","excavation_needs_replan");InvalidatePlan();return Running();}
            if(selected!=null && !PlanCellsMatch(stage=="clearing"))
            {
                bool hadChild=CurrentChild!=null;
                var stopped=base.Stop(frame,"cancelled","excavation_needs_replan");
                if(hadChild)Record(lastResult);
                if(stopped.Status=="unknown"||!stopped.InputsReleased)return stopped with{Evidence=Evidence("unknown")};
                InvalidatePlan();return Running();
            }
            if(stage is not ("mining" or "opening" or "clearing")&&(capability!=ActorMining.CapabilityStamp(player)
                ||targets.Any(t=>!t.Changed&&t.Proof==null&&!t.Cell.Matches())))
            {
                var stopped=base.Stop(frame,"cancelled","excavation_needs_replan");
                if(stopped.Status=="unknown"||!stopped.InputsReleased)
                {outcome="unknown";return stopped with{Evidence=Evidence(outcome)};}
                InvalidatePlan();return Running();
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
            if(selected!=null)
            {
                if(result.Status=="completed"&&result.EffectVerified)
                {
                    var step=selected.Actions[stepIndex];
                    if(stage=="clearing")
                    {
                        if(Main.tile[step.X,step.Y].HasTile)return Finish("failed","excavation_clearance_changed");
                        selected.Dependencies[(step.X,step.Y)]=new('.',default);
                    }
                    stepIndex++;
                    stage="settling";
                    return Running();
                }
                if(result.Status is not ("failed" or "timeout"))return Finish(result.Status,result.Reason);
                InvalidatePlan();
            }
            else if(stage=="mining")
            {
                continuationSlot=result.Status=="completed"?activePlan.NextSlot:null;
                expectedPending=searchTargetCount-activePlan.Cells.Length;
                activePlan=null;
                bool changedDuringRun=result.Status=="failed"&&result.Reason=="target_changed"
                    &&targets.Any(t=>t.Proof==null&&!t.Changed&&!t.Cell.Matches());
                if(result.Status!="completed"&&result.Reason!="excavation_needs_replan"&&!changedDuringRun)
                    return Finish(result.Status,result.Reason);
                if(result.Evidence is MiningRunReceipt {verified.Length:>0})
                {stanceSearch.Mined();invalidations=0;cutsSinceOre=0;}
                else if(++invalidations>=AutonomousNavigationTask.MaxConsecutiveInvalidations)
                    return Finish("failed","excavation_replan_budget_exhausted");
            }
            else if(stage=="opening")
            {
                openingTarget=null;
                if(result.Status!="completed")return Finish(result.Status,result.Reason);
                if(singleOre)return result with{Reason="mine_target_verified"};
                stanceSearch.Mined();cutsSinceOre=0;
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
        if(selected!=null)
        {
            if(!ContinuePlan(pose))InvalidatePlan();
            else return Running();
        }
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
            destination=null;
            // A fixed-stance run cannot include the final supporting ore. A
            // single cut can use the existing bounded, dry fall prediction.
            var support=pending.FirstOrDefault(c=>c.Y*16>=pose.Py+40&&c.Y*16<=pose.Py+43
                &&c.X*16<pose.Px+20&&(c.X+1)*16>pose.Px&&!attemptedOpenings.Contains(c));
            if(support!=null&&new NativeNavigationEdits(identity,pose).LandingAfterCut(pose,new[]{(support.X,support.Y)}).HasValue)
            {
                if(TrySupport(support))return Running();
            }
            localPlan=null;localWalk=null;nativeCost=null;
            search=FindApproach(pose,world,physics,pending).GetEnumerator();
        }
        stage="planning";
        var timer=Stopwatch.StartNew();
        try
        {
            for(int work=0;work<16&&timer.Elapsed.TotalMilliseconds<3;work++)
            {
                if(!stanceSearch.TravelExhausted&&search.MoveNext())continue;
                if(stanceSearch.TravelExhausted){routeBudgetReached=true;destination=null;}
                ReleaseSearch();
                if(!stanceSearch.TravelExhausted&&SelectLocalPlan())return Running();
                if(destination==null)
                    return routeBudgetReached||stanceSearch.TravelExhausted
                        ?Finish("timeout","excavation_route_search_budget_exhausted")
                        :Finish("failed",unsupported?"excavation_unsupported_terrain":"excavation_no_plan_in_search_window");
                var region=destination.Region;
                stanceSearch.Attempt(region,destination.Batch);
                var arguments=JsonSerializer.SerializeToElement(new{minPlayerLeftX=region.MinPlayerLeftX,
                    maxPlayerLeftX=region.MaxPlayerLeftX,surfaceY=region.SurfaceY});
                navigationDispatches++;
                stage=InLocalSearch(pose,region)?"navigating":"approaching";
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
        exhaustedAt=null;localRouteProbes.Clear();attemptedOpenings.Clear();routeBudgetReached=false;
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

    private static bool InLocalSearch(PhysicsState start,LandingRegion region)
    {
        int centerX=(int)((start.Px+10)/16),centerY=(int)((start.Py+42)/16);
        float x=(region.MinPlayerLeftX+region.MaxPlayerLeftX)/2+10;
        return Math.Abs(x-centerX*16)<=15*16&&Math.Abs(region.SurfaceY-centerY*16)<=12*16;
    }

    private IEnumerable<int> FindWorkPosition(PhysicsState start,NativeMotionWorld world,PhysicsParameters physics,ExcavationCell[] pending)
    {
        if(stanceSearch.Attempts>=6){routeBudgetReached=true;yield break;}
        if(exhaustedAt==start)yield break;
        var candidates=new List<WorkPose>();
        WorkPose approach=null;
        int left=(pending.Min(c=>c.X)-6)*16-10,right=(pending.Max(c=>c.X)+7)*16-10;
        int top=(pending.Min(c=>c.Y)-6)*16,bottom=(pending.Max(c=>c.Y)+6)*16;
        for(int feet=top;feet<=bottom;feet+=8)
        for(int x=left;x<=right;x+=8)
        {
            yield return 0;
            var stance=new PhysicsState(x,feet-42,0,0,true,0,true);
            var region=new LandingRegion(x-2,x+2,feet);
            // A distant landing is a travel goal only. The actor's collision window
            // cannot validate it; probe the destination in its own native window.
            bool local=InLocalSearch(start,region);
            if(region.Contains(start)
                ||!(local?world:new NativeMotionWorld(stance)).SafeLanding(stance))continue;
            var quote=NativeExcavation.Choose(identity,stance,pending,new(TerrainReader.ReadEnvironment),ordered:false);
            if(quote==null)continue;
            if(stanceSearch.Tried(region,quote.Cells)){routeBudgetReached=true;continue;}
            var first=NativeExcavation.Choose(identity,stance,quote.Cells.Take(1),new(TerrainReader.ReadEnvironment),false);
            var candidate=new WorkPose(region,quote.Cells,quote.Coverage,first.RunUpdates,region.Distance(start)/physics.MaxRun);
            if(local)candidates.Add(candidate);
            else if(approach==null||candidate.Coverage>approach.Coverage||candidate.Coverage==approach.Coverage
                &&candidate.LowerBound+candidate.Mining<approach.LowerBound+approach.Mining)approach=candidate;
        }

        knowledge.Bind(player,identity.World,identity.Generation,identity.Slot);
        knowledge.ObservePhysics(physics);
        knowledge.Search.Scope(terrain,physics,(long)Main.GameUpdateCount);
        // Anchor the eight-update coverage tie-break to this search. A common
        // first-strike cost must not split otherwise near-equal work positions.
        float cheapest=candidates.Count==0?0:candidates.Min(c=>c.LowerBound+c.Mining);
        foreach(var candidate in candidates.OrderBy(c=>Math.Floor((c.LowerBound+c.Mining-cheapest)/8))
            .ThenByDescending(c=>c.Coverage).ThenBy(c=>c.LowerBound+c.Mining))
        {
            float x=(candidate.Region.MinPlayerLeftX+candidate.Region.MaxPlayerLeftX)/2;

            // Starting the same search after a child settles must not spend the
            // same 32 route probes again. A different origin can have new routes.
            if(localRouteProbes.Contains(candidate.Region))continue;
            if(localRouteProbes.Count>=32){routeBudgetReached=true;break;}
            var route=SafeGroundWalk.TryPlan(start,x,x>=start.Px?1:-1,physics,world,candidate.Region);
            yield return 0;
            if(route?.ReachedGoal!=true)
            {
                using var navigator=new SurfaceNavigator(knowledge.Surfaces,start,candidate.Region,physics,world,world,knowledge.Search,
                    (at,edge)=>knowledge.Failures.Reject(at,edge,(long)Main.GameUpdateCount,world,physics));
                for(int slice=0;slice<48&&!navigator.Finished;slice++)
                {
                    route=navigator.Advance(budgetMs:2);
                    yield return 0;
                }
                if(!navigator.Finished||route?.TimeBudgetReached==true||route?.SearchExit=="safe_partial")routeBudgetReached=true;
            }
            if(route?.ReachedGoal!=true){localRouteProbes.Add(candidate.Region);continue;}
            nativeCost=new(route.Actions.Sum(a=>a.ExecuteFrames),0,candidate.Mining,0);
            destination=candidate;
            yield break;
        }
        // Travel through the normal rolling navigator. On arrival Tick observes the
        // live pose and quotes the remaining ore anew before starting a mining run.
        destination??=approach;
        if(destination==null&&localPlan==null&&localWalk==null)exhaustedAt=start;
    }

    private void Record(Result result)
    {
        if(stage=="clearing"&&selected!=null&&result?.Evidence!=null)
        {
            var cut=selected.Actions[stepIndex];
            var evidence=JsonSerializer.SerializeToElement(result.Evidence);
            if(evidence.TryGetProperty("destroyedByActor",out var owned)&&owned.GetBoolean()
                &&evidence.GetProperty("X").GetInt32()==cut.X&&evidence.GetProperty("Y").GetInt32()==cut.Y
                &&evidence.GetProperty("TileType").GetInt32()==cut.Type)
            {actualCuts++;cutsSinceOre++;}
        }
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
        if(hadChild)Record(lastResult);
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
        if(hadChild)Record(lastResult);
    }
}

// Failure scope and attempt accounting are independent of the rolling route planner.
internal sealed class ExcavationStanceSearch
{
    private (long Terrain,int Capability,PhysicsParameters Physics,int Pending)? scope;
    private readonly List<(LandingRegion Region,ExcavationCell[] Batch)> attempts=new();
    private readonly List<LandingRegion> failedOpeningMoves=new();
    internal int Attempts=>attempts.Count;

    internal bool OpeningMoveLimited(LandingRegion region)=>TravelExhausted||failedOpeningMoves.Count>=16
        ||failedOpeningMoves.Any(prior=>SameLanding(prior,region));
    internal void RejectOpeningMove(LandingRegion region)
    {
        if(!OpeningMoveLimited(region))failedOpeningMoves.Add(region);
    }

    // Charge actual search and child updates, including unsuccessful opening attempts.
    // A scope invalidation restores candidates but never refunds spent updates.
    internal ulong WorkUpdates { get; private set; }
    internal ulong TravelUpdates { get; private set; }
    internal bool WorkExhausted=>WorkUpdates>=1800;
    internal bool TravelExhausted=>TravelUpdates>=900;
    internal void Charge(ulong elapsed,bool travel)
    {
        WorkUpdates+=elapsed;
        if(travel)TravelUpdates+=elapsed;
    }
    internal void Mined(){WorkUpdates=TravelUpdates=0;}

    internal bool Observe((int,int,int,int) work,(int,int,int,int) departure,int capability,PhysicsParameters physics,int pending)
    {
        long terrain=TerrainSignature(work,TerrainSignature(departure,1469598103934665603));
        var observed=(terrain,capability,physics,pending);
        if(scope==observed)return false;
        scope=observed;
        attempts.Clear();failedOpeningMoves.Clear();
        return true;
    }

    internal void Attempt(LandingRegion region,ExcavationCell[] batch)=>attempts.Add((region,batch));

    // Adjacent half-tile samples overlap most of the player's 20px body. Keep
    // alternative floors and samples offering a different first batch eligible.
    internal bool Tried(LandingRegion region,ExcavationCell[] batch)=>attempts.Any(a=>
        SameLanding(a.Region,region)&&a.Batch.SequenceEqual(batch));

    private static bool SameLanding(LandingRegion prior,LandingRegion region)=>
        Math.Abs(prior.SurfaceY-region.SurfaceY)<=LandingRegion.FeetTolerance
        &&Math.Abs(prior.MinPlayerLeftX-region.MinPlayerLeftX)<=8
        &&Math.Abs(prior.MaxPlayerLeftX-region.MaxPlayerLeftX)<=8;

    private static long TerrainSignature((int Left,int Right,int Top,int Bottom) bounds,long hash)
    {
        for(int x=bounds.Left;x<=bounds.Right;x++)
        for(int y=bounds.Top;y<=bounds.Bottom;y++)
        {
            var cell=TerrainReader.ReadEnvironment(x,y);
            long stamp=(long)(cell.TileType.GetValueOrDefault(-1)+1)<<32 | (long)cell.Shape<<16 | (uint)cell.Liquid.Stamp;
            hash=unchecked((hash^stamp^(cell.Actuated?1L<<56:0))*1099511628211);
        }
        return hash;
    }
}
