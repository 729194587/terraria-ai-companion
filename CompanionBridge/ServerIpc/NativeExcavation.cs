using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using CompanionBridge.Common.Systems;
using CompanionBridge.Motion;
using VirtualPlayerEngineProbe;
using Result = CompanionBridge.ServerIpc.AiAtomicActions.NativeResult;

namespace CompanionBridge.ServerIpc;

internal static class NativeExcavation
{
    internal const int BatchLimit=18;
    private const int ToolHandoffUpdates=1;
    internal sealed record Plan(ExcavationCell[] Cells,int Slot,float RunUpdates,int Coverage,float TotalUpdates,int? NextSlot);
    private readonly record struct ToolRun(int Tool,int End,float Cost,float Total,int? NextTool)
    {
        internal bool BetterThan(ToolRun? other)=>other==null||Total<other.Value.Total
            ||Total==other.Value.Total&&End>other.Value.End;
    }

    internal static bool SafeCut(PhysicsState pose,ExcavationCell cell,LocalTerrainView view,bool clearance)
        =>cell.Matches()&&(!clearance||(view.Cell(cell.X,cell.Y)=='#'&&!view.LiquidNear(cell.X,cell.Y)
            &&NativeNavigationEdits.SafeExcavation(cell.X,cell.Y,view)))
            &&SafeOrePose(pose,cell.X,cell.Y,view);

    internal static bool SafeOrePose(PhysicsState pose,int x,int y,LocalTerrainView view)
        =>view.DryCell(x,y) is not ('?' or '!') && MotionSupport.SafeLanding(pose,view.DryCell)
            &&MotionSupport.SafeLanding(pose,(tx,ty)=>tx==x&&ty==y?'.':view.DryCell(tx,ty));

    internal static Plan Choose(AiReadOnlyProtocol.Identity identity,PhysicsState pose,
        IEnumerable<ExcavationCell> targets,LocalTerrainView view,bool ordered,int? continuationSlot=null)
    {
        var player=(Player)identity.Actor;
        var slots=Enumerable.Range(0,Math.Min(50,player.inventory.Length)).Where(s=>ActorMining.SupportedPick(player.inventory[s]))
            .OrderByDescending(s=>s==player.selectedItem).ToArray();
        var timings=slots.Select(s=>ActorMining.Timing(player,player.inventory[s])).ToArray();
        var eligible=new List<(ExcavationCell Cell,int[] Hits)>();
        foreach(var cell in targets)
        {
            if(!SafeCut(pose,cell,view,ordered))
            {
                if(ordered)break;
                continue;
            }
            var hits=new int[slots.Length];
            for(int i=0;i<slots.Length;i++)
            {
                int slot=slots[i];
                if(ActorMining.CanQuotePick(player,slot,cell.X,cell.Y,out int strikes)
                    &&timings[i].CompletionUpdates(strikes)<=ActorMining.MaxUpdates
                    &&ActorMining.QueryReach(player,identity.World,identity.Generation,player.inventory[slot],cell.X,cell.Y,pose.Px,pose.Py))
                    hits[i]=strikes;
            }
            if(!hits.Any(n=>n>0)||!view.Set(cell.X,cell.Y,'.'))
            {
                if(ordered)break;
                continue;
            }
            eligible.Add((cell,hits));
            if(eligible.Count==BatchLimit)break;
        }
        if(eligible.Count==0)return null;

        // Price every legal contiguous tool run. Each switch drains the old
        // run and starts a new press. The hotbar swap itself is synchronous.
        var best=new ToolRun?[eligible.Count];
        var alternate=new ToolRun?[eligible.Count];
        ToolRun? continuation=null;
        for(int start=eligible.Count-1;start>=0;start--)
        {
            for(int tool=0;tool<slots.Length;tool++)
            {
                int strikes=0;
                ToolRun? bestForTool=null;
                for(int end=start;end<eligible.Count&&eligible[end].Hits[tool]>0;end++)
                {
                    strikes+=eligible[end].Hits[tool];
                    float cost=timings[tool].CompletionUpdates(strikes);
                    if(cost>ActorMining.MaxUpdates)break;
                    float finish=cost;
                    int? nextTool=null;
                    if(end+1<eligible.Count)
                    {
                        var tail=best[end+1];
                        // Keep the current input if this tool continues. A split
                        // with the same tool is only needed at the native limit.
                        if(tail?.Tool==tool
                            &&timings[tool].CompletionUpdates(strikes+eligible[end+1].Hits[tool])<=ActorMining.MaxUpdates)
                            tail=alternate[end+1];
                        if(tail==null)continue;
                        finish+=tail.Value.Total+ToolHandoffUpdates;
                        nextTool=tail.Value.Tool;
                    }
                    var candidate=new ToolRun(tool,end,cost,finish,nextTool);
                    if(candidate.BetterThan(bestForTool))bestForTool=candidate;
                }
                if(bestForTool is not {} run)continue;
                if(start==0&&slots[tool]==continuationSlot)continuation=run;
                if(run.BetterThan(best[start])){alternate[start]=best[start];best[start]=run;}
                else if(run.BetterThan(alternate[start]))alternate[start]=run;
            }
        }
        return (continuation??best[0]) is {} first
            ?new(eligible.Take(first.End+1).Select(t=>t.Cell).ToArray(),slots[first.Tool],first.Cost,eligible.Count,first.Total,
                first.NextTool is int next?slots[next]:null)
            :null;
    }

    internal static bool SafeAtLivePose(Player player,PhysicsState expected,ExcavationCell cell,bool clearance)
    {
        var now=ServerAiNavigation.Observe(player);
        return now.Settled&&new NativeMotionWorld(now.State).SafeLanding(now.State)&&Math.Abs(now.State.Px-expected.Px)<=1&&Math.Abs(now.State.Py-expected.Py)<=1
            &&SafeCut(now.State,cell,new(TerrainReader.ReadEnvironment),clearance);
    }
}

// One tool lease and one native ItemCheck stream, with explicit receipts for
// each removal. The parent owns retries, work poses and remaining targets.
internal sealed class MiningRunTask(AiReadOnlyProtocol.Identity identity,NativeExcavation.Plan plan,PhysicsState pose,
    Func<bool> corridorUnchanged=null) : CompanionTask
{
    private ActorMining.Context context;
    private readonly int capability=ActorMining.CapabilityStamp((Player)identity.Actor);
    internal override string Name=>"excavate_tiles";
    internal override string Phase=>context?.Batch.Draining==true?"draining":"mining";
    internal override object Progress=>context==null?null:Receipt();
    internal override object AbandonEvidence=>Progress;
    private MiningRunReceipt Receipt()=>new(plan.Cells.Length,context.Batch.Verified.ToArray(),
        context.Batch.Estimate,context.Ticks,context.Strikes,context.Type,pose.Px,pose.Py+PhysicsSimulator.PlayerH);
    internal override void Start(AiReadOnlyProtocol.Identity frame)
    {
        ServerAiNativeActions.Require(identity);
        var player=(Player)identity.Actor;
        if(capability!=ActorMining.CapabilityStamp(player))return;
        var batch=new ActorMining.Batch(plan.Cells,
            cell=>(identity.Autonomy == null || identity.Autonomy.Valid && !AutonomyPolicy.ProtectedTile(cell.X,cell.Y))
                &&(corridorUnchanged?.Invoke()??true)&&NativeExcavation.SafeAtLivePose(player,pose,cell,corridorUnchanged!=null),plan.RunUpdates);
        try{context=ActorMining.StartBatch(player,identity.World,identity.Generation,batch,plan.Slot);}
        catch
        {
            // TaskSlot calls Stop after a failed start. Keep the lease only if
            // this start actually acquired it.
            if(ReferenceEquals(ActorMining.Current?.Batch,batch))context=ActorMining.Current;
            throw;
        }
    }
    internal override Result Tick(AiReadOnlyProtocol.Identity frame)
    {
        ServerAiNativeActions.Require(identity);
        if(context==null)return Failed("excavation_needs_replan");
        if(!ReferenceEquals(ActorMining.Current,context))return new("unknown","native_context_changed",Receipt(),false);
        var phase=context.Phase;
        if(phase==ActionPhase.Running)return Running();
        var p=(Player)identity.Actor;
        bool released=!p.controlUseItem&&!p.channel&&p.itemAnimation==0&&p.itemTime==0&&p.reuseDelay==0;
        bool complete=phase==ActionPhase.Completed&&context.Batch.Verified.Count==plan.Cells.Length;
        var evidence=Receipt();
        string status=complete?"completed":phase switch
        {
            ActionPhase.Cancelled=>"cancelled",ActionPhase.TimedOut=>"timeout",ActionPhase.Stale=>"unknown",
            _=>context.Reason==ActorMining.Failure.NativeException?"unknown":"failed"
        };
        string reason=context.Reason switch
        {
            ActorMining.Failure.BatchNeedsReplan or ActorMining.Failure.ItemChanged=>"excavation_needs_replan",
            _=>complete?"excavation_run_verified":AiActionReasons.Mining(status,context.Reason.ToString())
        };
        return new(status,reason,evidence,released,complete,context.Ticks>0);
    }
    internal override Result Stop(AiReadOnlyProtocol.Identity frame,string status,string reason)
    {
        if(context==null)return new(status,reason,null,true);
        if(ReferenceEquals(ActorMining.Current,context)&&ActorMining.Running)
            ActorMining.Stop((Player)identity.Actor,identity.World,identity.Generation);
        return Tick(frame);
    }
}
