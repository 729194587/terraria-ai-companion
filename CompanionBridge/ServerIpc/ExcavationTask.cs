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

internal sealed class ExcavationTask(string id,AiReadOnlyProtocol.Identity identity,JsonElement args,
    AiAtomicActions.IExecutor native,NavigationKnowledge knowledge) : AbstractCompanionTask(id)
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

    private sealed record WorkPose(LandingRegion Region,int Coverage,float Mining,float LowerBound);
    private const int DiscoveryLimit=128;
    private readonly List<Target> targets=new();
    private readonly Queue<(int X,int Y)> frontier=new();
    private readonly HashSet<(int,int)> seen=new();
    private readonly HashSet<ExcavationCell> attemptedOpenings=new();
    private ExcavationCell openingTarget;
    private bool discoveryTruncated,routeBudgetReached;
    private string openingFailure;
    private readonly HashSet<LandingRegion> failedStances=new();
    private readonly Player player=(Player)identity.Actor;
    private readonly int initialLife=((Player)identity.Actor).statLife;
    private CompanionTask active;
    private NativeExcavation.Plan activePlan;
    private int? continuationSlot;
    private int expectedPending;
    private IEnumerator<int> search;
    private WorkPose destination;
    private PhysicsState searchStart;
    private PhysicsParameters searchPhysics;
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

        AddTarget(seedX,seedY);
        return null;
    }

    private void AddTarget(int x,int y)
    {
        targets.Add(new(ExcavationCell.Capture(x,y)));
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

    internal override void Start(AiReadOnlyProtocol.Identity frame)=>stage="discover";

    internal override Result Tick(AiReadOnlyProtocol.Identity frame)
    {
        if(NativeNavigationWorld.CheckHazards(identity,initialLife) is {} danger)return Stop(frame,"cancelled",danger);
        if(!ActorMining.Healthy(player)||stage is not ("mining" or "opening")&&!GameNavigation.Ordinary(player))
            return Stop(frame,"cancelled","actor_cannot_mine");
        if(stage=="discover")
        {
            Discover();
            if(frontier.Count>0)return Running();
            stage="observe";
        }
        if(active!=null)
        {
            if(stage=="opening"&&CurrentChild==null&&!openingTarget.Matches())
            {active=null;openingTarget=null;stage="observe";return Running();}
            if(stage!="mining"&&stage!="opening"&&(capability!=ActorMining.CapabilityStamp(player)
                ||targets.Any(t=>!t.Changed&&t.Proof==null&&!t.Cell.Matches())))
            {
                var stopped=base.Stop(frame,"cancelled","excavation_needs_replan");
                if(stopped.Status=="unknown"||!stopped.InputsReleased)
                {outcome="unknown";return stopped with{Evidence=Evidence(outcome)};}
                active=null;stage="observe";return Running();
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
                {failedStances.Clear();invalidations=0;}
                else if(++invalidations>=AutonomousNavigationTask.MaxConsecutiveInvalidations)
                    return Finish("failed","excavation_replan_budget_exhausted");
            }
            else if(stage=="opening")
            {
                openingTarget=null;
                if(result.Status=="completed"){attemptedOpenings.Clear();failedStances.Clear();}
                else if(result.Status=="failed"&&result.Reason!="no_safe_mining_stance_in_local_window")
                    openingFailure=result.Reason;
                else if(result.Status=="timeout"&&result.InputsReleased){routeBudgetReached=true;openingFailure=result.Reason;}
                else if(result.Status!="failed")return Finish(result.Status,result.Reason);
            }
            else if(result.Status!="completed")
            {
                if(result.Status!="failed")return Finish(result.Status,result.Reason);
                failedStances.Add(destination.Region);
                if(failedStances.Count>=6)routeBudgetReached=true;
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
        if(search==null||pose!=searchStart||physics!=searchPhysics||currentTerrain!=terrain||currentCapability!=capability
            ||searchTargetCount!=pending.Length)
        {
            int? next=pose==searchStart&&currentCapability==capability&&pending.Length==expectedPending?continuationSlot:null;
            continuationSlot=null;
            if(currentTerrain!=terrain||currentCapability!=capability||searchTargetCount!=pending.Length)
            {failedStances.Clear();routeBudgetReached=false;}
            if(currentCapability!=capability||searchTargetCount!=pending.Length){attemptedOpenings.Clear();openingFailure=null;}
            ReleaseSearch();
            searchStart=pose;searchPhysics=physics;terrain=currentTerrain;capability=currentCapability;searchTargetCount=pending.Length;
            var here=world.SafeLanding(pose)?NativeExcavation.Choose(identity,pose,pending,new(TerrainReader.ReadEnvironment),ordered:false,continuationSlot:next):null;
            if(here!=null)
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
                StartSingle(support);
                return Running();
            }
            search=FindWorkPosition(pose,world,physics,pending).GetEnumerator();
        }
        stage="planning";
        var timer=Stopwatch.StartNew();
        for(int work=0;work<16&&timer.Elapsed.TotalMilliseconds<3;work++)
        {
            if(search.MoveNext())continue;
            ReleaseSearch();
            if(destination==null)
            {
                openingTarget=pending.Where(c=>!attemptedOpenings.Contains(c))
                    .OrderBy(c=>Math.Abs(c.X*16-player.Center.X)+Math.Abs(c.Y*16-player.Center.Y)).FirstOrDefault();
                if(openingTarget==null)return Finish(routeBudgetReached?"timeout":"failed",
                    routeBudgetReached?"excavation_route_search_budget_exhausted":openingFailure??"excavation_no_work_position");
                StartSingle(openingTarget);
                return Running();
            }
            var region=destination.Region;
            var arguments=JsonSerializer.SerializeToElement(new{minPlayerLeftX=region.MinPlayerLeftX,
                maxPlayerLeftX=region.MaxPlayerLeftX,surfaceY=region.SurfaceY});
            stage=InLocalSearch(pose,region)?"navigating":"approaching";
            active=new NativeActionTask(native,"navigate_to_landing_region",arguments,taskId+"/"+stage+"-"+(++sequence));

            return Running();
        }
        return Running();
    }

    private void StartSingle(ExcavationCell cell)
    {
        attemptedOpenings.Add(cell);
        openingTarget=cell;
        var goal=JsonSerializer.SerializeToElement(new{x=cell.X,y=cell.Y});
        var single=new NativeMineTargetWorld(identity,goal,(x,y)=>targets.Any(t=>t.Cell.X==x&&t.Cell.Y==y),knowledge);
        active=new MineTargetTask(taskId+"/opening-"+(++sequence),single,native,compactHistory:true);
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
        if(failedStances.Count>=6||attemptedOpenings.Count>0)yield break;
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
            if(failedStances.Contains(region)||region.Contains(start)
                ||!(local?world:new NativeMotionWorld(stance)).SafeLanding(stance))continue;
            var quote=NativeExcavation.Choose(identity,stance,pending,new(TerrainReader.ReadEnvironment),ordered:false);
            if(quote==null)continue;
            var candidate=new WorkPose(region,quote.Coverage,quote.TotalUpdates,region.Distance(start)/physics.MaxRun);
            if(local)candidates.Add(candidate);
            else if(approach==null||candidate.Coverage>approach.Coverage||candidate.Coverage==approach.Coverage
                &&candidate.LowerBound+candidate.Mining<approach.LowerBound+approach.Mining)approach=candidate;
        }

        knowledge.Bind(player,identity.World,identity.Generation,identity.Slot);
        knowledge.ObservePhysics(physics);
        knowledge.Search.Scope(terrain,physics,(long)Main.GameUpdateCount);
        int tried=0;
        foreach(var candidate in candidates.OrderByDescending(c=>c.Coverage).ThenBy(c=>c.LowerBound+c.Mining))
        {
            float x=(candidate.Region.MinPlayerLeftX+candidate.Region.MaxPlayerLeftX)/2;

            if(tried>=32){routeBudgetReached=true;break;}
            tried++;
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
                if(!navigator.Finished||route?.TimeBudgetReached==true)routeBudgetReached=true;
            }
            if(route?.ReachedGoal!=true)continue;
            destination=candidate;
            yield break;
        }
        // Travel through the normal rolling navigator. On arrival Tick observes the
        // live pose and quotes the remaining ore anew before starting a mining run.
        destination??=approach;
    }

    protected override object ChildView(TaskSlot slot)
    {
        if(slot.Task is not MineTargetTask single)return base.ChildView(slot);
        var record=slot.Record;
        var result=record.Result;
        return new{taskName=record.Name,taskId=record.Id,status=result.Status,reason=result.Reason,
            target=openingTarget is {} c?new{x=c.X,y=c.Y}:null,destination=destination?.Region,
            inputsReleased=result.InputsReleased,effectVerified=result.EffectVerified,reasonDetail=result.ReasonDetail,
            steps=single.MiningSteps};
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
    private object Evidence(string outcome)=>new{outcome,seedX,seedY,typeId=seedType,discoveryComplete=!discoveryTruncated&&frontier.Count==0,targets=targets.Select(t=>t.View()).ToArray()};
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
