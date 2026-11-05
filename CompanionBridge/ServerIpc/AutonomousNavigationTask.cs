using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using CompanionBridge.Motion;
using Result = CompanionBridge.ServerIpc.AiAtomicActions.NativeResult;

namespace CompanionBridge.ServerIpc;

internal sealed record NavigationStep(string Operation,JsonElement Arguments);

internal interface INavigationWorld
{
    string InterruptionReason()=>null;
    string Prepare();
    string Revalidate();
    bool Arrived();
    JsonElement? FindApproach();
    NavigationStep FindOpening(int remainingTiles)=>null;
    string RevalidateOpening()=>null;
    bool OpeningSettled()=>true;
    string OpeningLandingError()=>null;
    string SearchDetail=>null;
    object OpeningEvidence=>null;
    object Target{get;}
    bool ReplanAfterChange=>false;
    string ProgressKey=>null;
    bool PreferEdit(int remainingTiles)=>false;
    object ArrivalEvidence=>null;
    string NoRouteReason=>"navigation_local_frontier_exhausted";
}
internal class AutonomousNavigationTask : AbstractCompanionTask
{
    private readonly INavigationWorld world;
    protected readonly AiAtomicActions.IExecutor native;
    protected CompanionTask child;
    internal const int LocalCutTileLimit=18, MaxConsecutiveInvalidations=8;
    internal const ulong TripUpdates=2700;
    private const ulong SettleUpdates=90;
    protected string phase="preflight";
    private int clearedTiles, dugTiles, placedTiles, openingMoves, invalidatedEdits, sequence;
    private ulong settleDeadline;
    private bool openingMode;
    private readonly Dictionary<string,int> checkpoints=new();
    private readonly Queue<string> checkpointOrder=new();
    private readonly NavigationProgressWatchdog progressWatch=new();
    protected bool acquired;
    protected Result Describe(Result result)=>result with
    {
        InputsAcquired=acquired||result.InputsAcquired,
        ReasonDetail=string.Join(" | ",new[]{result.ReasonDetail,world.SearchDetail,
            $"stage={phase}; cleared={clearedTiles}; openingMoves={openingMoves}; localCutLimit={LocalCutTileLimit}"}
            .Where(detail=>!string.IsNullOrEmpty(detail)))
    };
    internal AutonomousNavigationTask(string id,INavigationWorld world,AiAtomicActions.IExecutor native):base(id){this.world=world;this.native=native;}
    internal override string Name=>"navigate_to";
    internal override string Phase=>phase;
    internal override object Progress=>new{stage=phase,completedChildren,settledChildren,
        children=children.ToArray(),childrenTruncated=settledChildren>children.Count,omittedChildren=settledChildren-children.Count,
        target=world.Target,clearedTiles,dugTiles,placedTiles,openingMoves,openingMode,search=world.SearchDetail,
        opening=world.OpeningEvidence,activity=progressWatch.Activity.ToString(),verifiedTerrainActions=progressWatch.VerifiedEffects};
    internal override Result Prepare(AiReadOnlyProtocol.Identity frame)=>(world.InterruptionReason()??world.Prepare()) is {} why?Describe(Failed(why)):null;
    internal override void Start(AiReadOnlyProtocol.Identity frame){phase="observe";}
    internal override Result Tick(AiReadOnlyProtocol.Identity frame)
    {
        if(world.InterruptionReason() is {} danger)return Stop(frame,"cancelled",danger);
        if(world.Revalidate() is {} error)
        {
            // Stop a running navigation child as well; do not orphan its controls.
            var stopped=Stop(frame,"failed",error);
            return Describe(stopped.Status=="unknown"?stopped:stopped with{Status="failed",Reason=error});
        }
        if(phase is "opening_tile" or "placing_support" && child is NativeActionTask leafOpening&&!leafOpening.Started
            &&world.RevalidateOpening() is {} stale)
        {
            var stopped=Stop(frame,"failed",stale);
            if(stopped.Status=="unknown"||!world.ReplanAfterChange)return stopped;
            if(++invalidatedEdits>=MaxConsecutiveInvalidations)return Describe(new Result("timeout","navigation_edit_replan_budget_exhausted",null,true,InputsAcquired:acquired));
            child=null;openingMode=false;phase="observe";
            return Running();
        }
        if(phase=="navigating")
        {
            var r=RunChild(child,frame,1800);if(r==null)return Running();
            acquired|=r.InputsAcquired;
            child=null;
            if(r.Status=="completed")invalidatedEdits=0;
            if(r.Status!="completed")
            {
                if(!CanOpenAfterNavigation(r)||openingMode)return Describe(r);
                openingMode=true;
            }
            phase=r.Status=="completed"&&world.ReplanAfterChange&&!openingMode?"observe":"reobserve";
            return Running();
        }
        if(phase is "opening_tile" or "placing_support")
        {
            var r=RunChild(child,frame,600);if(r==null)return Running();
            acquired|=r.InputsAcquired;
            if(r.Status!="completed")return Describe(r);
            progressWatch.Observe(NavigationActivity.VerifiedEffect);
            clearedTiles++;
            if(phase=="opening_tile")dugTiles++;else placedTiles++;
            invalidatedEdits=0;
            phase="settling";child=null;settleDeadline=frame.GameUpdate+SettleUpdates;
            return Running();
        }
        if(phase=="settling")
        {
            progressWatch.Observe(NavigationActivity.Recovery);
            if(!world.OpeningSettled())
                return frame.GameUpdate<settleDeadline?Running():Describe(world.ReplanAfterChange
                    ?new Result("timeout","navigation_edit_settle_timeout",null,true,InputsAcquired:acquired)
                    :Failed("mining_opening_did_not_settle"));
            if(world.OpeningLandingError() is {} landing && !world.ReplanAfterChange)
                return Describe(Failed(landing));
            if(world.ProgressKey is {} key)
            {
                if(!checkpoints.ContainsKey(key))
                {
                    if(checkpoints.Count>=64)checkpoints.Remove(checkpointOrder.Dequeue());
                    checkpoints[key]=0;checkpointOrder.Enqueue(key);
                }
                if(++checkpoints[key]>=3)return Describe(Failed("navigation_repeated_edit_checkpoint"));
            }
            phase=world.ReplanAfterChange?"observe":"reobserve";
            if(world.ReplanAfterChange)openingMode=false;
        }
        if(world.Arrived())
            return new("completed","navigation_destination_verified",world.ArrivalEvidence,true,true,acquired);
        progressWatch.Observe(NavigationActivity.Search);
        if(!openingMode)
        {
            if(phase!="reobserve"&&!world.PreferEdit(LocalCutTileLimit)&&world.FindApproach() is {} approach)
            {
                return StartChild(frame,"navigate_to_landing_region",approach,"navigating",TripUpdates);
            }
            openingMode=true;
        }
        var opening=world.FindOpening(LocalCutTileLimit);
        if(opening==null)return Describe(Failed(world.NoRouteReason));
        if(opening.Operation=="mine_tile")return StartChild(frame,opening.Operation,opening.Arguments,"opening_tile",600);
        if(opening.Operation=="place_tile")return StartChild(frame,opening.Operation,opening.Arguments,"placing_support",600);
        openingMoves++;
        return StartChild(frame,opening.Operation,opening.Arguments,"navigating",360);
    }
    private Result StartChild(AiReadOnlyProtocol.Identity frame,string operation,JsonElement args,string stage,ulong budget)
    {
        phase=stage;child=new NativeActionTask(native,operation,args,taskId+"/"+stage+"-"+(++sequence));
        RunChild(child,frame,budget);return Running();
    }
    private static bool CanOpenAfterNavigation(Result r)
    {
        if(r.Status!="failed"||!r.InputsReleased||r.Evidence==null)return false;
        var evidence=JsonSerializer.SerializeToElement(r.Evidence);
        return evidence.TryGetProperty("recoverableFailure",out var recoverable)
            &&recoverable.ValueKind==JsonValueKind.True
            &&evidence.TryGetProperty("failureKind",out var kind)
            &&kind.GetString() is "no_route" or "no_progress";
    }
    internal override Result Stop(AiReadOnlyProtocol.Identity frame,string status,string reason)
        => Describe(base.Stop(frame,status,reason));
}
