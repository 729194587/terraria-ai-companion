using System;
using System.Collections.Generic;
using System.Text.Json;
using CompanionBridge.Motion;

public static class NavigationRecoveryTests
{
    private static int checks;
    private static readonly PhysicsParameters Ph=PhysicsParameters.Basic;
    private static readonly PhysicsState Start=new(1600,1558,0,0,true);
    private static void Check(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
    private sealed class World:IMotionWorld
    {
        internal readonly MotionOfflineTests.World Inner=new();
        internal bool AirOnly,Danger,Flat;
        public PhysicsState Move(PhysicsState s,float vx,float vy,float gravity)=>AirOnly
            ?new(s.Px+vx,s.Py+vy,vx,vy,false):Inner.Move(s,vx,vy,gravity);
        public bool SafeLanding(PhysicsState s)=>!AirOnly&&Inner.SafeLanding(s);
        public bool SafeFlatLanding(PhysicsState s)=>Flat&&SafeLanding(s);
        public bool SafeSweep(PhysicsState a,PhysicsState b,out string reason){reason=Danger?"hazard":null;return !Danger;}
    }
    private static void Neutral(NavigationTask task,ref PhysicsState s,World w,bool released=true,int session=41,int generation=7,float target=1800)
    {
        var input=task.Before(session,generation,target,s,Ph,w);
        Check(input==default,"recovery must only emit neutral input");
        s=PhysicsSimulator.Step(s,input,Ph,w);task.After(s,w,inputsReleased:released);
    }
    private static NavigationTask Deviate(World w,out PhysicsState s,bool fast=false)
    {
        var task=new NavigationTask(fast);s=Start;
        var input=task.Before(41,7,1800,s,Ph,w);
        s=PhysicsSimulator.Step(s,input,Ph,w) with{Px=Start.Px-24}; // Diagnostic disturbance only.
        task.After(s,w);
        Check(task.Snapshot.Status=="recovering"&&task.Snapshot.RecoveryCount==1,"deviation stays inside same action");
        Check(task.Snapshot.CompletedActions==0&&task.Snapshot.RecoveryInputsReleased==null,"no invented completion or release");
        return task;
    }
    public static void Run()
    {
        var w=new World();var task=Deviate(w,out var s);
        int plans=task.Snapshot.PlanNumber;
        task.After(s,w,inputsReleased:true);
        Check(task.Snapshot.RecoveryReplans==0,"After-only release claim is insufficient");
        Neutral(task,ref s,w);
        int confirmations=task.Snapshot.LandingConfirmationFrames;
        task.After(s,w,inputsReleased:true);
        Check(task.Snapshot.Status=="recovering"&&task.Snapshot.LandingConfirmationFrames==confirmations,
            "duplicate After cannot supply the second release/settling observation");
        for(int n=0;n<60&&task.Snapshot.Status=="recovering";n++)Neutral(task,ref s,w);
        Check(task.Snapshot.Status=="running"&&task.Snapshot.RecoveryReplans==0&&task.Snapshot.CompletedActions==0,
            "confirmed actual checkpoint permits replan, never completes old edge");
        Check(task.Snapshot.PlanNumber==plans&&task.Snapshot.Session==41&&task.Snapshot.Generation==7
            &&task.Snapshot.TargetX==1800&&task.Snapshot.FailedEdgeCount==1,"original identity/goal and failed edge retained");
        var checkpoint=s;
        for(int n=0;n<700&&!task.Terminal;n++)
        {
            var input=task.Before(41,7,1800,s,Ph,w);
            if(task.Snapshot.PlanNumber>plans){Check(task.Snapshot.PlanStart==checkpoint,"replan uses actual settled position");plans=int.MaxValue;}
            s=PhysicsSimulator.Step(s,input,Ph,w);task.After(s,w,input==default);
        }
        Check(task.Snapshot.Status=="arrived"&&s.Px>=1788&&Math.Abs(s.Vx)<=.1f,"same action reaches real goal after recovery");
        Console.WriteLine(JsonSerializer.Serialize(new{scenario="displaced_safe_landing",task.Snapshot.RecoveryCount,
            task.Snapshot.RecoveryReplans,task.Snapshot.PlanNumber,task.Snapshot.Status,x=s.Px}));
        // Offset an actual simulated jump at its first grounded landing, rather
        // than injecting a new plan or declaring the predicted edge completed.
        w=new World();w.Inner.WallX=1624;w.Inner.WallWidth=32;task=new();s=Start;
        bool shiftedLanding=false;
        for(int n=0;n<900&&!task.Terminal;n++)
        {
            var input=task.Before(41,7,1800,s,Ph,w);
            var next=PhysicsSimulator.Step(s,input,Ph,w);
            if(!shiftedLanding&&!s.Grounded&&next.Grounded)
            {next=next with{Px=next.Px+24};shiftedLanding=true;}
            s=next;task.After(s,w,input==default);
        }
        Check(shiftedLanding&&task.Snapshot.Status=="arrived"&&task.Snapshot.RecoveryCount==1
            &&task.Snapshot.RecoveryReplans==1,"safe non-predicted jump landing continues the original goal");
        Console.WriteLine(JsonSerializer.Serialize(new{scenario="shifted_jump_landing",task.Snapshot.RecoveryCount,
            task.Snapshot.RecoveryReplans,task.Snapshot.Status,x=s.Px}));
        foreach(bool blocked in new[]{false,true})
        {
            w=new World();task=new();s=Start;
            for(int n=0;n<900&&!task.Terminal;n++)
            {
                if(n==5){w.Inner.WallX=1640;w.Inner.WallWidth=32;w.Inner.WallHeight=blocked?200:32;}
                var input=task.Before(41,7,1800,s,Ph,w);
                s=PhysicsSimulator.Step(s,input,Ph,w);task.After(s,w,input==default);
            }
            Check(task.Terminal&&task.Snapshot.RecoveryCount>0&&task.Snapshot.RecoveryCount<=NavigationTask.RecoveryLimit,
                "dynamic obstacle produces bounded route recovery");
            Check(blocked?task.Snapshot.Status!="arrived":task.Snapshot.Status=="arrived",
                "new obstacle is bypassed or ends with bounded failure: "+task.Snapshot.Reason);
            var terminal=task.Snapshot;task.Before(41,7,1900,s,Ph,w);task.After(s,w,true);
            Check(task.Snapshot==terminal,"published terminal cannot revive even with changed target");
            Console.WriteLine(JsonSerializer.Serialize(new{scenario=blocked?"new_high_wall":"new_low_wall",
                task.Snapshot.RecoveryCount,task.Snapshot.RecoveryReplans,task.Snapshot.Status,task.Snapshot.Reason,x=s.Px}));
        }
        foreach(string stop in new[]{"release","hazard","air","cancel","identity","timeout"})
        {
            w=new World();task=Deviate(w,out s);
            if(stop=="air"){w.AirOnly=true;s=s with{Grounded=false,Vy=1};}
            if(stop=="hazard")w.Danger=true;
            if(stop=="cancel")task.Cancel();
            if(stop=="timeout")task.Fail("navigation_frame_budget");
            if(stop=="identity")
            {
                s=s with{Grounded=false,Vy=1};task.Before(41,8,1800,s,Ph,w);
                Check(task.Terminal&&task.Snapshot.RecoveryCount==0,"new generation discards old pending recovery");continue;
            }
            if(stop is "cancel" or "timeout")
            {
                var snapshot=task.Snapshot;task.Before(41,7,1800,s,Ph,w);task.After(s,w,true);
                Check(task.Snapshot==snapshot,"cancel/timeout cannot re-enter old action");continue;
            }
            int count=stop=="air"?NavigationTask.LandingConfirmationLimit:1;
            for(int n=0;n<count&&!task.Terminal;n++)Neutral(task,ref s,w,stop!="release");
            Check(task.Snapshot.Status=="unknown"&&task.Snapshot.RecoveryReplans==0,"unsafe/unconfirmed recovery stays unknown: "+stop);
            var final=task.Snapshot;task.Before(41,7,1800,Start,Ph,new World());task.After(Start,w,true);
            Check(task.Snapshot==final,"unknown never replays");
        }
        // Repeated disturbances consume a separate budget; progress cannot reset it.
        w=new World();task=new();s=Start;
        for(int n=0;n<800&&!task.Terminal;n++)
        {
            var input=task.Before(42,7,5000,s,Ph,w);
            s=PhysicsSimulator.Step(s,input,Ph,w);
            if(input!=default)s=s with{Px=s.Px+24};
            task.After(s,w,input==default);
        }
        Check(task.Terminal&&task.Snapshot.RecoveryCount==NavigationTask.RecoveryLimit
            &&task.Snapshot.Reason=="navigation_recovery_limit","recovery budget cannot be reset by apparent progress");
        var memory=new NavigationFailureMemory();w=new World();
        var edge=PhysicsSimulator.Predict(Start,1,0,Ph,w,walkFrames:24);
        memory.Remember(Start,edge,10);
        Check(memory.Reject(Start,edge,11),"same local failed action is quarantined");
        var other=PhysicsSimulator.Predict(Start,1,0,Ph,w,walkFrames:6);
        Check(!memory.Reject(Start,other,11),"memory does not ban every forward action");
        Check(!memory.Reject(Start with{Px=Start.Px+64},edge,11),"memory is spatially local");
        Check(!memory.Reject(Start,edge,10+NavigationFailureMemory.LifetimeUpdates)&&memory.Count==0,"temporary obstruction expires");
        var filtered=StateSpacePlanner.Plan(Start,1800,Ph,w,budgetMs:5000,rejectEdge:(_,_)=>true);
        Check(filtered.Actions.Count==0&&!filtered.ReachedGoal&&filtered.Rejections.ContainsKey("recently failed local edge"),
            "quarantined candidates cannot become partial or complete incumbents");
        w=new World{Flat=true};task=Deviate(w,out s,fast:true);
        for(int n=0;n<60&&task.Snapshot.Status=="recovering";n++)Neutral(task,ref s,w);
        task.Before(41,7,1800,s,Ph,w);
        Check(task.Snapshot.PlanKind=="state_space"&&task.Snapshot.SearchRejections.ContainsKey("recently failed local edge"),
            "fast walk cannot bypass failed-edge quarantine");
        Console.WriteLine($"M4.2 recovery: {checks} checks (collision fixtures, not native execution).");
    }
}
