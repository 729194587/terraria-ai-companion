using System.Text.Json;
using Result = CompanionBridge.ServerIpc.AiAtomicActions.NativeResult;

namespace CompanionBridge.ServerIpc;

internal interface IMineTargetWorld
{
    string InterruptionReason()=>null;
    string Prepare();
    string Revalidate();
    bool CanMine();
    JsonElement? FindApproach();
    JsonElement MineArguments();
    bool TargetAbsent();
    object Target{get;}
}
internal sealed class MineTargetTask : AbstractCompanionTask
{
    private readonly IMineTargetWorld world;
    private readonly AiAtomicActions.IExecutor native;
    private CompanionTask child;
    private string phase="preflight";
    internal MineTargetTask(string id,IMineTargetWorld world,AiAtomicActions.IExecutor native):base(id){this.world=world;this.native=native;}
    internal override string Name=>"mine_target";
    internal override string Phase=>phase;
    internal override object Progress=>new{stage=phase,completedChildren,settledChildren=children.Count,children,target=world.Target,recoveryAttempts=0};
    internal override Result Prepare(AiReadOnlyProtocol.Identity frame)=>(world.InterruptionReason()??world.Prepare()) is {} why?Failed(why):null;
    internal override void Start(AiReadOnlyProtocol.Identity frame){phase="observe";}
    internal override Result Tick(AiReadOnlyProtocol.Identity frame)
    {
        if(world.InterruptionReason() is {} danger)return Stop(frame,"cancelled",danger);
        if(phase=="mining")
        {
            if(child is NativeActionTask leaf&&!leaf.Started&&world.Revalidate() is {} changed)return Stop(frame,"failed",changed);
            var r=RunChild(child,frame,600);if(r==null)return Running();
            if(r.Status!="completed")return r;
            phase="verify";
            return world.TargetAbsent()?new("completed","mine_target_verified",r.Evidence,r.InputsReleased,r.EffectVerified,r.InputsAcquired):new("unknown","target_not_absent_after_mining",r.Evidence,r.InputsReleased);
        }
        if(world.Revalidate() is {} error)
        {
            // Stop a running navigation child as well; do not orphan its controls.
            var stopped=Stop(frame,"failed",error);
            return stopped.Status=="unknown"?stopped:stopped with{Status="failed",Reason=error};
        }
        if(phase=="navigating")
        {
            var r=RunChild(child,frame,1800);if(r==null)return Running();
            if(r.Status!="completed")return r; // M1 has no automatic recovery/retry.
            phase="reobserve";child=null;return Running();
        }
        if(world.CanMine())
        {
            phase="mining";child=new NativeActionTask(native,"mine_tile",world.MineArguments(),taskId+"/mine");
            RunChild(child,frame,600);return Running();
        }
        if(phase=="reobserve")return Failed("mining_stance_or_reach_changed_after_navigation");
        var approach=world.FindApproach();if(!approach.HasValue)return Failed("no_safe_mining_stance_in_local_window");
        phase="navigating";child=new NativeActionTask(native,"navigate_to_landing_region",approach.Value,taskId+"/approach");
        RunChild(child,frame,1800);return Running();
    }
    internal override Result Stop(AiReadOnlyProtocol.Identity frame,string status,string reason)
    {
        var stopped=base.Stop(frame,status,reason);
        // A child completion during stop still needs the root's effect verification.
        if(phase=="mining"&&lastResult?.Status=="completed"&&lastResult.EffectVerified
            &&lastResult.InputsReleased&&world.TargetAbsent())
            return new("completed","mine_target_verified",lastResult.Evidence,true,true,lastResult.InputsAcquired);
        return stopped;
    }
}
