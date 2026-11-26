using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Terraria;
using Terraria.ID;
using CompanionBridge.Common.Systems;
using CompanionBridge.Motion;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

internal sealed partial class ExcavationTask
{
    private NativeNavigationEdits corridor;
    private OreApproachSearch.Plan localWalk,localPlan,selected;
    private OreApproachSearch.Cost nativeCost;
    private PhysicsParameters planPhysics;
    private int stepIndex,cutsSinceOre,navigationDispatches,navigationFailures,actualCuts,replans;
    private double planningMs,maxSliceMs;
    private bool unsupported;
    private readonly Dictionary<string,ulong> updates=new();
    private readonly Queue<object> decisions=new();
    private static string Category(string phase)=>phase switch
    {
        "planning"=>"planning","approaching" or "navigating"=>"movement","clearing"=>"clearance",
        "mining" or "opening"=>"ore",_=>"waiting"
    };
    private object ApproachEvidence()=>new{decisions=decisions.ToArray(),navigationDispatches,navigationFailures,
        actualCuts,replans,planningMs,maxSliceMs,updates};

    internal static bool IsOre(AiReadOnlyProtocol.Identity identity,JsonElement args)=>
        SceneMineTargets.Resolve(identity,args,out int x,out int y)==null&&WorldGen.InWorld(x,y,2)
        &&Main.tile[x,y].HasUnactuatedTile&&TileID.Sets.Ore[Main.tile[x,y].TileType]
        &&!Main.tileFrameImportant[Main.tile[x,y].TileType];

    private bool TrySupport(ExcavationCell cell)
    {
        if(!singleOre)
        {
            directTarget=new NativeMineTargetWorld(identity,JsonSerializer.SerializeToElement(new{x=cell.X,y=cell.Y}));
            if(directTarget.Prepare()!=null)return false;
        }
        if(!directTarget.CanMine())return false;
        StartSingle(cell);return true;
    }

    private IEnumerable<int> FindApproach(PhysicsState pose,NativeMotionWorld world,PhysicsParameters physics,ExcavationCell[] pending)
    {
        planPhysics=physics;
        corridor=new(identity,pose,(x,y)=>targets.Any(t=>t.Cell.X==x&&t.Cell.Y==y)
            ||identity.Autonomy!=null&&AutonomyPolicy.ProtectedTile(x,y));
        if(pending.Any(c=>Math.Abs(c.X*16-pose.Px)<=16*18&&Math.Abs(c.Y*16-pose.Py)<=16*14))
        {
            var local=new OreApproachSearch(identity,pose,physics,pending,corridor,LocalEditEvaluation.MaxChanges-cutsSinceOre);
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
                int side=localPlan.Goal.X*16+8>=pose.Px+10?1:-1;
                foreach(int direction in new[]{side,-side})
                foreach(var (stride,hold) in new[]{(24,0),(36,0),(48,0),(24,physics.JumpHeight)})
                {
                    var edge=PhysicsSimulator.Predict(pose,direction,hold,physics,world,walkFrames:stride);
                    yield return 0;
                    if(!edge.Safe)continue;
                    var region=Region(edge.Landing);
                    foreach(var cell in pending)
                    {
                        var quote=NativeExcavation.Choose(identity,edge.Landing,new[]{cell},new(TerrainReader.ReadEnvironment),false);
                        if(quote==null||stanceSearch.Tried(region,quote.Cells))continue;
                        var price=new OreApproachSearch.Cost(edge.Frames.Count,0,quote.RunUpdates,0);
                        if(nativeCost!=null&&nativeCost.Total<=price.Total)continue;
                        nativeCost=price;
                        destination=new(region,quote.Cells,quote.Coverage,quote.RunUpdates,edge.Frames.Count);
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
    private OreApproachSearch.Plan Eligible(OreApproachSearch.Plan plan)
    {
        if(plan==null)return null;
        return plan.Actions.Any(a=>a.Kind==LocalEditKind.Move&&stanceSearch.OpeningMoveLimited(Region(a.Expected)))?null:plan;
    }
    private static LandingRegion Region(PhysicsState pose)=>new(pose.Px-2,pose.Px+2,pose.Py+PhysicsSimulator.PlayerH);

    private bool SelectLocalPlan()
    {
        var walk=localWalk!=null&&(nativeCost==null||localWalk.Cost.Total<nativeCost.Total)?localWalk:null;
        var movement=walk?.Cost??nativeCost;
        selected=localPlan!=null&&LocalEditEvaluation.PreferClearance(localPlan.Cost.Total,movement?.Total)?localPlan:walk;
        if(decisions.Count==8)decisions.Dequeue();
        decisions.Enqueue(new{choice=selected!=null?(selected.Cost.Cuts>0?"clearance":"movement"):
            nativeCost!=null?"movement":destination!=null?"approach":"none",movement,clearance=localPlan?.Cost,
            predictedGoal=selected?.Goal,actions=selected?.Actions.Select(a=>new{kind=a.Kind.ToString(),a.X,a.Y,
                fromX=a.Start.Px,fromY=a.Start.Py,toX=a.Expected.Px,toY=a.Expected.Py}).ToArray(),
            dependencies=selected==null?null:new{count=selected.Dependencies.Count,
                left=selected.Dependencies.Keys.Min(k=>k.X),right=selected.Dependencies.Keys.Max(k=>k.X),
                top=selected.Dependencies.Keys.Min(k=>k.Y),bottom=selected.Dependencies.Keys.Max(k=>k.Y)},
            capability,gameUpdate=Main.GameUpdateCount});
        if(selected==null)return false;
        stepIndex=0;stage="settling";return true;
    }

    private bool PlanCellsMatch(bool clearing)
    {
        if(!selected.Goal.Matches()||identity.Autonomy is {Valid:false})return false;
        foreach(var (position,expected) in selected.Dependencies)
        {
            var actual=corridor.Environment(position.X,position.Y);
            if(actual==expected)continue;
            if(clearing&&selected.Actions[stepIndex] is {Kind:LocalEditKind.Mine} cut
                &&position==(cut.X,cut.Y)&&actual==new TerrainCell('.',default))continue;
            return false;
        }
        return true;
    }
    private bool ReadyStep()=>PlanCellsMatch(false)&&capability==ActorMining.CapabilityStamp(player)
        &&planPhysics==NativeNavigation.Parameters(player)&&
        (stage!="clearing"||corridor.RevalidateOpening()==null);

    private bool ContinuePlan(PhysicsState pose)
    {
        if(!PlanCellsMatch(false)||planPhysics!=NativeNavigation.Parameters(player)||capability!=ActorMining.CapabilityStamp(player))
            return false;
        if(stepIndex>0&&!Region(selected.Actions[stepIndex-1].Expected).Contains(pose))return false;
        if(stepIndex==selected.Actions.Length)
        {
            selected=null;stage="observe";
            return true;
        }
        var step=selected.Actions[stepIndex];
        if(!Region(step.Start).Contains(pose))return false;
        var armed=corridor.Arm(step with{Start=pose});
        if(armed==null)return false;
        if(step.Kind==LocalEditKind.Move){navigationDispatches++;stage="navigating";}
        else stage="clearing";
        active=new NativeActionTask(native,armed.Operation,armed.Arguments,taskId+"/"+stage+"-"+(++sequence));
        return true;
    }
    private void InvalidatePlan()
    {
        if(selected!=null && stepIndex<selected.Actions.Length&&stage=="navigating")
            stanceSearch.RejectOpeningMove(Region(selected.Actions[stepIndex].Expected));
        replans++;selected=null;active=null;ReleaseSearch();stage="observe";
    }
}
