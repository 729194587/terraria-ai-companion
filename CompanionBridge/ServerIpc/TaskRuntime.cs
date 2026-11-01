using System;
using System.Collections.Generic;
using Result = CompanionBridge.ServerIpc.AiAtomicActions.NativeResult;

namespace CompanionBridge.ServerIpc;

// Game-update driven, single-root/single-active-child runtime. No Terraria or IPC dispatch.
internal sealed class TaskRecord
{
    internal readonly string Id,Name,ParentId;
    internal readonly ulong Deadline;
    internal string Status="running",Phase="accepted",Reason="accepted";
    internal bool Started;
    internal Result Result;
    internal TaskRecord(string id,string name,string parent,ulong deadline){Id=id;Name=name;ParentId=parent;Deadline=deadline;}
}
internal abstract class CompanionTask
{
    internal virtual string Phase=>"executing";
    internal virtual object Progress=>null;
    internal virtual object CurrentChild=>null;
    internal virtual object LastResult=>null;
    internal abstract string Name{get;}
    internal virtual Result Prepare(AiReadOnlyProtocol.Identity frame)=>null;
    internal virtual void Start(AiReadOnlyProtocol.Identity frame){}
    internal abstract Result Tick(AiReadOnlyProtocol.Identity frame);
    internal abstract Result Stop(AiReadOnlyProtocol.Identity frame,string status,string reason);
    internal virtual void Abandon(){}
    internal static Result Running()=>new("running","task_running",null,true,InputsAcquired:false);
    internal static Result Failed(string reason)=>new("failed",reason,null,true,InputsAcquired:false);
}
internal sealed class TaskSlot
{
    internal readonly TaskRecord Record;
    internal readonly CompanionTask Task;
    private readonly AiReadOnlyProtocol.Identity identity;
    private ulong lastTick;
    internal TaskSlot(CompanionTask task,string id,string parent,AiReadOnlyProtocol.Identity identity,ulong budget)
    {Task=task;this.identity=identity;lastTick=identity.GameUpdate;Record=new(id,task.Name,parent,checked(identity.GameUpdate+budget));}
    internal static bool Same(AiReadOnlyProtocol.Identity a,AiReadOnlyProtocol.Identity b)=>b!=null&&ReferenceEquals(a.Actor,b.Actor)&&a.World==b.World&&a.Generation==b.Generation&&a.Slot==b.Slot;
    internal Result Tick(AiReadOnlyProtocol.Identity frame)
    {
        if(Record.Result!=null)return Record.Result;
        if(!Same(identity,frame))return Abandon();
        if(!frame.CanAct)return Stop(frame,"cancelled","actor_not_alive");
        if(frame.GameUpdate<=lastTick)return CompanionTask.Running();
        lastTick=frame.GameUpdate;
        if(frame.GameUpdate>=Record.Deadline)return Stop(frame,"timeout","task_game_update_deadline");
        try
        {
            if(!Record.Started)
            {
                var rejection=Task.Prepare(frame);if(rejection!=null)return Finish(rejection);
                Record.Started=true; // Before invoking code that may throw. Never restart.
                Task.Start(frame);Record.Phase=Task.Phase;Record.Reason="task_started";
                return CompanionTask.Running();
            }
            var result=Task.Tick(frame);Record.Phase=Task.Phase;Record.Reason=result.Reason;
            return result.Status=="running"?result:Finish(result);
        }
        catch(Exception e)
        {
            Result cleanup;
            try{cleanup=Task.Stop(frame,"unknown","task_exception");}catch{cleanup=new("unknown","task_cleanup_unconfirmed",null,false);}
            return Finish(cleanup with{Status="unknown",Reason="task_exception",EffectVerified=false,ReasonDetail=e.GetType().Name+": "+e.Message});
        }
    }
    private Result Finish(Result r)
    {
        if(r.Status!="unknown"&&(!r.InputsReleased||r.Status=="completed"&&!r.EffectVerified))r=r with{Status="unknown",Reason="task_effect_or_release_unconfirmed",EffectVerified=false};
        Record.Result=r;Record.Status=r.Status;Record.Phase="terminal";Record.Reason=r.Reason;return r;
    }
    internal Result Stop(AiReadOnlyProtocol.Identity frame,string status,string reason)
    {
        if(Record.Result!=null)return Record.Result;
        if(!Same(identity,frame))return Abandon();
        try
        {
            var stopped=Record.Started?Task.Stop(frame,status,reason):new Result(status,reason,null,true,InputsAcquired:false);
            return Finish(stopped.Status=="unknown"||!stopped.InputsReleased?stopped:stopped with{Status=status,Reason=reason,EffectVerified=false});
        }
        catch(Exception e){return Finish(new("unknown","task_cleanup_unconfirmed",null,false,ReasonDetail:e.Message));}
    }
    internal Result Abandon()
    {if(Record.Result!=null)return Record.Result;Task.Abandon();return Finish(new("unknown","actor_binding_changed",null,false));}
    internal static object Receipt(Result r)=>r==null?null:new{status=r.Status,reason=r.Reason,evidence=r.Evidence,inputsReleased=r.InputsReleased,effectVerified=r.EffectVerified,inputsAcquired=r.InputsAcquired,reasonDetail=r.ReasonDetail};
    internal object View()=>new{taskId=Record.Id,taskName=Record.Name,parentTaskId=Record.ParentId,phase=Record.Phase,status=Record.Status,
        worldSessionId=identity.World,actorGeneration=identity.Generation,actorSlot=identity.Slot,observedGameUpdate=lastTick,deadlineGameUpdate=Record.Deadline,currentChild=Task.CurrentChild,progress=Task.Progress,lastResult=Task.LastResult,reason=Record.Reason,result=Receipt(Record.Result)};
}
internal abstract class AbstractCompanionTask : CompanionTask
{
    private TaskSlot child;
    protected readonly List<object> children=new();
    protected Result lastResult;
    protected int completedChildren;
    protected readonly string taskId;
    private int sequence;
    protected AbstractCompanionTask(string id){taskId=id;}
    internal override object CurrentChild=>child?.View();
    internal override object LastResult=>TaskSlot.Receipt(lastResult);
    protected Result RunChild(CompanionTask task,AiReadOnlyProtocol.Identity frame,ulong budget)
    {
        if(child==null)child=new(task,task is NativeActionTask leaf?leaf.RequestId:taskId+"/"+(++sequence),taskId,frame,budget);
        else if(!ReferenceEquals(child.Task,task))throw new InvalidOperationException("An active child cannot be replaced");
        var r=child.Tick(frame);
        if(r.Status=="running")return null;
        lastResult=r;if(r.Status=="completed")completedChildren++;children.Add(child.View());child=null;return r;
    }
    internal override Result Stop(AiReadOnlyProtocol.Identity frame,string status,string reason)
    {
        var r=child?.Stop(frame,status,reason);
        if(child!=null){lastResult=r;children.Add(child.View());child=null;}
        return r?.Status=="unknown"||r?.InputsReleased==false?r:new(status,reason,null,true,InputsAcquired:r?.InputsAcquired??false);
    }
    internal override void Abandon(){child?.Abandon();if(child!=null){lastResult=child.Record.Result;children.Add(child.View());child=null;}}
}
internal sealed class NativeActionTask : CompanionTask
{
    private readonly AiAtomicActions.IExecutor executor;
    private readonly string operation,id;
    private readonly System.Text.Json.JsonElement args;
    private object handle;
    private bool attempted;
    internal NativeActionTask(AiAtomicActions.IExecutor executor,string operation,System.Text.Json.JsonElement args,string id)
    {this.executor=executor;this.operation=operation;this.args=args.Clone();this.id=id;}
    internal string RequestId=>id;
    internal bool Started=>attempted;
    internal override string Name=>operation;
    internal override void Start(AiReadOnlyProtocol.Identity frame){attempted=true;handle=executor.Start(frame,operation,args,id);}
    internal override Result Tick(AiReadOnlyProtocol.Identity frame)=>executor.Poll(frame,handle);
    internal override Result Stop(AiReadOnlyProtocol.Identity frame,string status,string reason)=>handle!=null?executor.Cancel(frame,handle):new(attempted?"unknown":status,attempted?"native_start_unconfirmed":reason,null,!attempted,InputsAcquired:attempted);
}
