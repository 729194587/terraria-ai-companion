using System.Text.Json;
using Result = CompanionBridge.ServerIpc.AiAtomicActions.NativeResult;

namespace CompanionBridge.ServerIpc;

internal interface IMineTargetWorld : INavigationWorld
{
    bool CanMine();
    void RejectMove(JsonElement arguments){}
    bool INavigationWorld.Arrived()=>CanMine();
    string INavigationWorld.NoRouteReason=>"no_safe_mining_stance_in_local_window";
    JsonElement MineArguments();
    bool TargetAbsent();
}

internal sealed class MineTargetTask : AutonomousNavigationTask
{
    private readonly IMineTargetWorld world;

    private readonly bool compactHistory;
    private object workPosition;
    internal object MiningSteps=>new{workPosition,steps=children.ToArray(),omitted=settledChildren-children.Count};

    protected override object ChildView(TaskSlot slot)
    {
        if(!compactHistory)return base.ChildView(slot);
        var result=slot.Record.Result;
        if(slot.Task is NativeActionTask move&&move.Name=="navigate_to_landing_region"
            &&result.InputsReleased&&result.Status is ("failed" or "timeout"))
            world.RejectMove(move.Arguments);
        return new{operation=slot.Record.Name,result.Status,result.Reason,result.InputsReleased,result.EffectVerified,
            arguments=(slot.Task as NativeActionTask)?.Arguments};
    }

    internal MineTargetTask(string id,IMineTargetWorld world,AiAtomicActions.IExecutor native,bool compactHistory=false)
        :base(id,world,native){this.world=world;this.compactHistory=compactHistory;}

    internal override string Name=>"mine_target";
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
        var navigation=base.Tick(frame);
        if(navigation.Status!="completed")return navigation;
        workPosition=navigation.Evidence;
        phase="mining";
        child=new NativeActionTask(native,"mine_tile",world.MineArguments(),taskId+"/mine");
        RunChild(child,frame,600);
        return Running();
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
