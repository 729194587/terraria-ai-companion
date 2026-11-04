using System.Linq;
using System.Text.Json;
using Result = CompanionBridge.ServerIpc.AiAtomicActions.NativeResult;

namespace CompanionBridge.ServerIpc;

internal sealed record MiningOpening(string Operation,JsonElement Arguments);

internal interface IMineTargetWorld
{
    string InterruptionReason()=>null;
    string Prepare();
    string Revalidate();
    bool CanMine();
    JsonElement? FindApproach();
    JsonElement MineArguments();
    MiningOpening FindOpening(int remainingTiles)=>null;
    string RevalidateOpening()=>null;
    bool OpeningSettled()=>true;
    string OpeningLandingError()=>null;
    string SearchDetail=>null;
    object OpeningEvidence=>null;
    bool TargetAbsent();
    object Target{get;}
}
internal sealed class MineTargetTask : AbstractCompanionTask
{
    private readonly IMineTargetWorld world;
    private readonly AiAtomicActions.IExecutor native;
    private CompanionTask child;
    internal const int MaxOpeningTiles=18, MaxOpeningMoves=8;
    private const ulong SettleUpdates=90;
    private string phase="preflight";
    private int clearedTiles, openingMoves, sequence;
    private ulong settleDeadline;
    private bool openingMode, acquired;
    private Result Describe(Result result)=>result with
    {
        InputsAcquired=acquired||result.InputsAcquired,
        ReasonDetail=string.Join(" | ",new[]{result.ReasonDetail,world.SearchDetail,
            $"stage={phase}; cleared={clearedTiles}/{MaxOpeningTiles}; openingMoves={openingMoves}/{MaxOpeningMoves}"}
            .Where(detail=>!string.IsNullOrEmpty(detail)))
    };
    internal MineTargetTask(string id,IMineTargetWorld world,AiAtomicActions.IExecutor native):base(id){this.world=world;this.native=native;}
    internal override string Name=>"mine_target";
    internal override string Phase=>phase;
    internal override object Progress=>new{stage=phase,completedChildren,settledChildren=children.Count,children,target=world.Target,clearedTiles,openingMoves,openingMode,search=world.SearchDetail,opening=world.OpeningEvidence};
    internal override Result Prepare(AiReadOnlyProtocol.Identity frame)=>(world.InterruptionReason()??world.Prepare()) is {} why?Describe(Failed(why)):null;
    internal override void Start(AiReadOnlyProtocol.Identity frame){phase="observe";}
    internal override Result Tick(AiReadOnlyProtocol.Identity frame)
    {
        if(world.InterruptionReason() is {} danger)return Stop(frame,"cancelled",danger);
        if(phase=="mining")
        {
            if(child is NativeActionTask leaf&&!leaf.Started&&world.Revalidate() is {} changed)return Stop(frame,"failed",changed);
            var r=RunChild(child,frame,600);if(r==null)return Running();
            acquired|=r.InputsAcquired;
            if(r.Status!="completed")return Describe(r);
            phase="verify";
            return world.TargetAbsent()?new("completed","mine_target_verified",r.Evidence,r.InputsReleased,r.EffectVerified,r.InputsAcquired):Describe(new("unknown","target_not_absent_after_mining",r.Evidence,r.InputsReleased));
        }
        if(world.Revalidate() is {} error)
        {
            // Stop a running navigation child as well; do not orphan its controls.
            var stopped=Stop(frame,"failed",error);
            return Describe(stopped.Status=="unknown"?stopped:stopped with{Status="failed",Reason=error});
        }
        if(phase=="opening_tile" && child is NativeActionTask leafOpening&&!leafOpening.Started
            &&world.RevalidateOpening() is {} stale)return Stop(frame,"failed",stale);
        if(phase=="navigating")
        {
            var r=RunChild(child,frame,1800);if(r==null)return Running();
            acquired|=r.InputsAcquired;
            child=null;
            if(r.Status!="completed")
            {
                if(openingMode||!CanOpenAfterNavigation(r))return Describe(r);
                openingMode=true;
            }
            phase="reobserve";return Running();
        }
        if(phase=="opening_tile")
        {
            var r=RunChild(child,frame,600);if(r==null)return Running();
            acquired|=r.InputsAcquired;
            if(r.Status!="completed")return Describe(r);
            clearedTiles++;
            phase="settling";child=null;settleDeadline=frame.GameUpdate+SettleUpdates;
            return Running();
        }
        if(phase=="settling")
        {
            if(!world.OpeningSettled())
                return frame.GameUpdate<settleDeadline?Running():Describe(Failed("mining_opening_did_not_settle"));
            if(world.OpeningLandingError() is {} landing)return Describe(Failed(landing));
            phase="reobserve";
        }
        if(world.CanMine())
        {
            phase="mining";child=new NativeActionTask(native,"mine_tile",world.MineArguments(),taskId+"/mine");
            RunChild(child,frame,600);return Running();
        }
        if(!openingMode)
        {
            if(phase!="reobserve"&&world.FindApproach() is {} approach)
                return StartChild(frame,"navigate_to_landing_region",approach,"navigating",1800);
            openingMode=true;
        }
        if(clearedTiles>=MaxOpeningTiles||openingMoves>=MaxOpeningMoves)
            return Describe(Failed("mining_opening_budget_exhausted"));
        var opening=world.FindOpening(MaxOpeningTiles-clearedTiles);
        if(opening==null)return Describe(Failed("no_safe_mining_stance_in_local_window"));
        if(opening.Operation=="mine_tile")return StartChild(frame,opening.Operation,opening.Arguments,"opening_tile",600);
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
    {
        var stopped=base.Stop(frame,status,reason);
        // A child completion during stop still needs the root's effect verification.
        if(phase=="mining"&&lastResult?.Status=="completed"&&lastResult.EffectVerified
            &&lastResult.InputsReleased&&world.TargetAbsent())
            return new("completed","mine_target_verified",lastResult.Evidence,true,true,lastResult.InputsAcquired);
        return Describe(stopped);
    }
}
