using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using CompanionBridge.Common.Systems;
using CompanionBridge.Motion;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

internal sealed class TerrainApproachPlanner(AiReadOnlyProtocol.Identity identity,NavigationKnowledge knowledge,
    TerrainRouteMemory stanceSearch,HashSet<LandingRegion> localRouteProbes,TerrainRouteGoal goal,
    NativeNavigationEdits corridor,int remainingChanges)
{
    internal sealed record WorkPose(LandingRegion Region,ExcavationCell[] Batch,int Coverage,float Mining,float LowerBound);
    private readonly Player player=(Player)identity.Actor;
    private long terrain;
    private TerrainRouteSearch local;
    private TerrainRouteSearch.Plan localWalk,localPlan;
    private TerrainRouteSearch.Cost nativeCost;
    internal TerrainRouteSearch.Plan Selected {get;private set;}
    internal WorkPose Destination {get;private set;}
    internal bool unsupported,routeBudgetReached;
    internal IEnumerable<int> Run(PhysicsState pose,NativeMotionWorld world,PhysicsParameters physics,long signature)
    {
        terrain=signature;
        var pending=goal.Cells;
        if(goal.Region is {} endRegion&&endRegion.Distance(pose)<=64)
        {
            float x=(endRegion.MinPlayerLeftX+endRegion.MaxPlayerLeftX)/2;
            var flat=SafeGroundWalk.TryPlan(pose,x,x>=pose.Px?1:-1,physics,world,endRegion);
            yield return 0;
            if(flat?.ReachedGoal==true&&!stanceSearch.OpeningMoveLimited(Region(flat.Actions[^1].Landing)))
            {
                nativeCost=new(flat.Actions.Sum(a=>a.ExecuteFrames),0,0,0);
                Destination=new(endRegion,Array.Empty<ExcavationCell>(),1,0,nativeCost.Total);
                yield break;
            }
        }
        if(goal.InWindow(pose))
        {
            local=new TerrainRouteSearch(identity,pose,physics,goal,corridor,remainingChanges,stanceSearch);
            foreach(var slice in local.Run())yield return slice;
            localWalk=Eligible(local.Walk);localPlan=Eligible(local.Clearance);
            unsupported=local.Unsupported;
            routeBudgetReached|=local.Limited;
            // A complete short walk on the current floor already wins the local comparison.
            if(localWalk!=null&&localPlan==null)yield break;
            if(localPlan!=null)
            {
                // A bypass can end on a different floor, between sampled
                // stance bands. Price the actual native landing of each stride.
                int side=(localPlan.Goal?.X*16+8??goal.Aim(pose).X)>=pose.Px+10?1:-1;
                foreach(int direction in new[]{side,-side})
                foreach(var (stride,hold) in new[]{(24,0),(36,0),(48,0),(24,physics.JumpHeight)})
                {
                    var edge=PhysicsSimulator.Predict(pose,direction,hold,physics,world,walkFrames:stride);
                    yield return 0;
                    if(!edge.Safe)continue;
                    var region=Region(edge.Landing);
                    if(stanceSearch.OpeningMoveLimited(region))continue;
                    if(goal.Region is {} end&&!end.Contains(edge.Landing)&&edge.Landing.Py>pose.Py
                        &&Math.Abs(edge.Landing.Py+42-end.SurfaceY)<=LandingRegion.FeetTolerance)
                    {
                        float x=(end.MinPlayerLeftX+end.MaxPlayerLeftX)/2;
                        foreach(int duration in new[]{24,32,36,40,44,48})
                        {
                            var finish=PhysicsSimulator.Predict(edge.Landing,x>=edge.Landing.Px?1:-1,0,physics,world,
                                walkFrames:duration,walkingGoal:end);
                            yield return 0;
                            if(!finish.Safe||!end.Arrived(finish.Landing,world))continue;
                            var cost=new TerrainRouteSearch.Cost(edge.Frames.Count+finish.Frames.Count,0,0,0);
                            if(nativeCost==null||cost.Total<nativeCost.Total)
                            {nativeCost=cost;Destination=new(region,Array.Empty<ExcavationCell>(),1,0,cost.Total);}
                        }
                    }
                    foreach(var (cell,updates) in goal.Quotes(edge.Landing,new(TerrainReader.ReadEnvironment),false))
                    {
                        var batch=cell==null?Array.Empty<ExcavationCell>():new[]{cell};
                        if(stanceSearch.Tried(region,batch))continue;
                        var price=new TerrainRouteSearch.Cost(edge.Frames.Count,0,updates,0);
                        if(nativeCost!=null&&nativeCost.Total<=price.Total)continue;
                        nativeCost=price;
                        Destination=new(region,batch,batch.Length,updates,edge.Frames.Count);
                    }
                }
                if(nativeCost!=null||localWalk!=null)yield break;
            }
        }
        // Keep native special movement and distant approach candidates. When a
        // complete local plan is in hand reserve the remaining lease for execution.
        int probes=0;
        foreach(var slice in FindWorkPosition(pose,world,physics,pending))
        {
            yield return slice;
            if((localWalk!=null||localPlan!=null)&&
                (++probes>=1600||localRouteProbes.Count>=4||stanceSearch.TravelUpdates>=450))break;
        }
    }
    private TerrainRouteSearch.Plan Eligible(TerrainRouteSearch.Plan plan)
    {
        if(plan==null)return null;
        return plan.Actions.Any(a=>a.Kind==LocalEditKind.Move&&stanceSearch.OpeningMoveLimited(Region(a.Expected)))?null:plan;
    }
    internal static LandingRegion Region(PhysicsState pose)=>new(pose.Px-2,pose.Px+2,pose.Py+PhysicsSimulator.PlayerH);

    internal object Decide(int capability)
    {
        var walk=localWalk!=null&&(nativeCost==null||localWalk.Cost.Total<nativeCost.Total)?localWalk:null;
        var movement=walk?.Cost??nativeCost;
        Selected=localPlan!=null&&LocalEditEvaluation.PreferClearance(localPlan.Cost.Total,movement?.Total)?localPlan:walk;
        return new{choice=Selected!=null?(Selected.Cost.Cuts+Selected.Cost.Places>0?"clearance":"movement"):
            nativeCost!=null?"movement":Destination!=null?"approach":"none",movement,clearance=localPlan?.Cost,
            predictedGoal=Selected?.Goal,actions=Selected?.Actions.Select(a=>new{kind=a.Kind.ToString(),a.X,a.Y,
                fromX=a.Start.Px,fromY=a.Start.Py,toX=a.Expected.Px,toY=a.Expected.Py}).ToArray(),
            dependencies=Selected==null?null:new{count=Selected.Dependencies.Count,
                left=Selected.Dependencies.Keys.Min(k=>k.X),right=Selected.Dependencies.Keys.Max(k=>k.X),
                top=Selected.Dependencies.Keys.Min(k=>k.Y),bottom=Selected.Dependencies.Keys.Max(k=>k.Y)},
            search=local?.Evidence,protectedEdit=corridor.ProtectedEdit,capability,gameUpdate=Main.GameUpdateCount};
    }
    internal static bool InLocalSearch(PhysicsState start,LandingRegion region)
    {
        int centerX=(int)((start.Px+10)/16),centerY=(int)((start.Py+42)/16);
        float x=(region.MinPlayerLeftX+region.MaxPlayerLeftX)/2+10;
        return Math.Abs(x-centerX*16)<=15*16&&Math.Abs(region.SurfaceY-centerY*16)<=12*16;
    }

    private IEnumerable<int> FindWorkPosition(PhysicsState start,NativeMotionWorld world,PhysicsParameters physics,ExcavationCell[] pending)
    {
        if(stanceSearch.Attempts>=6){routeBudgetReached=true;yield break;}
        var candidates=new List<WorkPose>();
        WorkPose approach=null;
        if(goal.Region is {} regionGoal)
            candidates.Add(new(regionGoal,Array.Empty<ExcavationCell>(),1,0,regionGoal.Distance(start)/physics.MaxRun));
        else
        {
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
            if(route?.ReachedGoal!=true||stanceSearch.OpeningMoveLimited(Region(route.Actions[^1].Landing))){localRouteProbes.Add(candidate.Region);continue;}
            nativeCost=new(route.Actions.Sum(a=>a.ExecuteFrames),0,candidate.Mining,0);
            Destination=candidate;
            yield break;
        }
        // Travel through the normal rolling navigator. On arrival Tick observes the
        // live pose and quotes the remaining ore anew before starting a mining run.
        Destination??=approach;
    }

}
