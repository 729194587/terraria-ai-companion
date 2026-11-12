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
        internal bool Changed;
        internal string Status=>Changed?"changed":Proof==null?"pending":"verified";
        internal object View()=>new{x=Cell.X,y=Cell.Y,typeId=Cell.Type,status=Status,proof=Proof};
    }

    private sealed record WorkPose(LandingRegion Region,int Coverage,float Mining,float LowerBound);
    private readonly List<Target> targets=new();
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
        if(!ActorMining.Healthy(player)||!GameNavigation.Ordinary(player))return Failed("unsupported_mining_profile");
        if(SceneMineTargets.Resolve(identity,args,out seedX,out seedY) is {} error)return Failed(error);
        if(!WorldGen.InWorld(seedX,seedY,2)||!Main.tile[seedX,seedY].HasUnactuatedTile)return Failed("invalid_mining_target");
        seedType=Main.tile[seedX,seedY].TileType;
        if(!TileID.Sets.Ore[seedType]||Main.tileFrameImportant[seedType])return Failed("excavation_seed_not_ore");

        var queue=new Queue<(int X,int Y)>();
        var seen=new HashSet<(int,int)>();
        queue.Enqueue((seedX,seedY));
        while(queue.TryDequeue(out var point))
        {
            var (x,y)=point;
            if(!seen.Add(point)||!WorldGen.InWorld(x,y,2))continue;
            var tile=Main.tile[x,y];
            if(!tile.HasUnactuatedTile||tile.TileType!=seedType)continue;
            if(targets.Count==NativeExcavation.MaxTargets)return Failed("excavation_cluster_too_large");
            targets.Add(new(ExcavationCell.Capture(x,y)));
            queue.Enqueue((x-1,y));queue.Enqueue((x+1,y));queue.Enqueue((x,y-1));queue.Enqueue((x,y+1));
        }
        return null;
    }

    internal override void Start(AiReadOnlyProtocol.Identity frame)=>stage="observe";

    internal override Result Tick(AiReadOnlyProtocol.Identity frame)
    {
        if(NativeNavigationWorld.CheckHazards(identity,initialLife) is {} danger)return Stop(frame,"cancelled",danger);
        if(!ActorMining.Healthy(player)||stage!="mining"&&!GameNavigation.Ordinary(player))
            return Stop(frame,"cancelled","actor_cannot_mine");
        if(active!=null)
        {
            if(stage=="navigating"&&(capability!=ActorMining.CapabilityStamp(player)
                ||targets.Any(t=>!t.Changed&&t.Proof==null&&!t.Cell.Matches())))
            {
                var stopped=base.Stop(frame,"cancelled","excavation_needs_replan");
                if(stopped.Status=="unknown"||!stopped.InputsReleased)return stopped;
                active=null;stage="observe";return Running();
            }
            var result=RunChild(active,frame,stage=="mining"?605UL:900UL);
            if(result==null)return Running();
            active=null;
            if(stage=="mining")Record(result);
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
                if(result.Status!="completed"&&result.Reason!="excavation_needs_replan")
                    return Finish(result.Status,result.Reason);
                if(result.Evidence is MiningRunReceipt {verified.Length:>0})
                {failedStances.Clear();invalidations=0;}
                else if(++invalidations>=AutonomousNavigationTask.MaxConsecutiveInvalidations)
                    return Finish("failed","excavation_replan_budget_exhausted");
            }
            else if(result.Status!="completed")
            {
                if(result.Status!="failed")return Finish(result.Status,result.Reason);
                failedStances.Add(destination.Region);
                if(failedStances.Count>=6)return Finish("failed","excavation_no_work_position");
            }
            stage="observe";
        }
        RefreshTargets();
        var pending=targets.Where(t=>t.Status=="pending").Select(t=>t.Cell).ToArray();
        if(pending.Length==0)return targets.All(t=>t.Status=="verified")
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
            if(currentTerrain!=terrain||currentCapability!=capability||searchTargetCount!=pending.Length)failedStances.Clear();
            ReleaseSearch();
            searchStart=pose;searchPhysics=physics;terrain=currentTerrain;capability=currentCapability;searchTargetCount=pending.Length;
            var here=NativeExcavation.Choose(identity,pose,pending,new(TerrainReader.ReadEnvironment),ordered:false,continuationSlot:next);
            if(here!=null)
            {
                stage="mining";activePlan=here;active=new MiningRunTask(identity,here,pose);
                RunChild(active,frame,605);
                return Running();
            }
            destination=null;
            search=FindWorkPosition(pose,world,physics,pending).GetEnumerator();
        }
        stage="planning";
        var timer=Stopwatch.StartNew();
        for(int work=0;work<16&&timer.Elapsed.TotalMilliseconds<3;work++)
        {
            if(search.MoveNext())continue;
            ReleaseSearch();
            if(destination==null)return Finish("failed","excavation_no_work_position");
            var region=destination.Region;
            var arguments=JsonSerializer.SerializeToElement(new{minPlayerLeftX=region.MinPlayerLeftX,
                maxPlayerLeftX=region.MaxPlayerLeftX,surfaceY=region.SurfaceY});
            stage="navigating";
            active=new NativeActionTask(native,"navigate_to_landing_region",arguments,taskId+"/work-"+(++sequence));
            RunChild(active,frame,900);
            return Running();
        }
        return Running();
    }

    private IEnumerable<int> FindWorkPosition(PhysicsState start,NativeMotionWorld world,PhysicsParameters physics,ExcavationCell[] pending)
    {
        var candidates=new List<WorkPose>();
        int centerX=(int)((start.Px+10)/16),centerY=(int)((start.Py+42)/16);
        int left=Math.Max(centerX-15,pending.Min(c=>c.X)-6)*16-10;
        int right=Math.Min(centerX+15,pending.Max(c=>c.X)+7)*16-10;
        int top=Math.Max(centerY-12,pending.Min(c=>c.Y)-6)*16;
        int bottom=Math.Min(centerY+12,pending.Max(c=>c.Y)+6)*16;
        for(int feet=top;feet<=bottom;feet+=8)
        for(int x=left;x<=right;x+=8)
        {
            yield return 0;
            var stance=new PhysicsState(x,feet-42,0,0,true,0,true);
            var region=new LandingRegion(x-2,x+2,feet);
            if(failedStances.Contains(region)||region.Contains(start)||!world.SafeLanding(stance))continue;
            var quote=NativeExcavation.Choose(identity,stance,pending,new(TerrainReader.ReadEnvironment),ordered:false);
            if(quote!=null)candidates.Add(new(region,quote.Coverage,quote.TotalUpdates,region.Distance(start)/physics.MaxRun));
        }

        float best=float.PositiveInfinity;
        knowledge.Bind(player,identity.World,identity.Generation,identity.Slot);
        knowledge.ObservePhysics(physics);
        knowledge.Search.Scope(terrain,physics,(long)Main.GameUpdateCount);
        var tried=new List<WorkPose>();
        foreach(var candidate in candidates.OrderByDescending(c=>c.Coverage).ThenBy(c=>c.LowerBound+c.Mining))
        {
            if(destination!=null&&candidate.Coverage<destination.Coverage)break;
            float x=(candidate.Region.MinPlayerLeftX+candidate.Region.MaxPlayerLeftX)/2;
            if(tried.Any(c=>c.Region.SurfaceY==candidate.Region.SurfaceY
                &&Math.Abs(c.Region.MinPlayerLeftX-candidate.Region.MinPlayerLeftX)<16))continue;
            if(tried.Count==8)break;
            tried.Add(candidate);
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
            }
            if(route?.ReachedGoal!=true)continue;
            float cost=candidate.Mining+route.Actions.Sum(a=>a.ExecuteFrames);
            if(destination!=null&&cost>=best)continue;
            destination=candidate;best=cost;
        }
    }

    private void Record(Result result)
    {
        if(result?.Evidence is not MiningRunReceipt run)return;
        foreach(var proof in run.verified)
            targets.Single(t=>t.Cell.X==proof.X&&t.Cell.Y==proof.Y).Proof=proof;
    }
    private void RefreshTargets()
    {
        foreach(var t in targets.Where(t=>!t.Changed))
            t.Changed=t.Proof==null?!t.Cell.Matches():Main.tile[t.Cell.X,t.Cell.Y].HasTile;
    }
    private object Evidence(string outcome)=>new{outcome,seedX,seedY,typeId=seedType,targets=targets.Select(t=>t.View()).ToArray()};
    private Result Finish(string status,string reason)
    {
        ReleaseSearch();RefreshTargets();
        outcome=status=="completed"?"completed":status is "cancelled" or "timeout"?"interrupted"
            :targets.Any(t=>t.Proof!=null)?"partial":"failed";
        return new(status,reason,Evidence(outcome),true,status=="completed",
            InputsAcquired:completedChildren>0||lastResult?.InputsAcquired==true||targets.Any(t=>t.Proof!=null));
    }
    private void ReleaseSearch(){search?.Dispose();search=null;}
    internal override Result Stop(AiReadOnlyProtocol.Identity frame,string status,string reason)
    {
        var stopped=base.Stop(frame,status,reason);
        ReleaseSearch();
        if(stage=="mining")Record(lastResult);
        if(stopped.Status=="unknown"||!stopped.InputsReleased)
        {
            outcome="unknown";
            return stopped with{Evidence=Evidence(outcome)};
        }
        active=null;
        return Finish(status,reason);
    }
    internal override void Abandon(){ReleaseSearch();base.Abandon();}
}
