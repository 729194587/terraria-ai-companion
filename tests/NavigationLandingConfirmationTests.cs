using System;
using System.Collections.Generic;
using System.Reflection;
using CompanionBridge.Motion;

public static class NavigationLandingConfirmationTests
{
    private sealed class World:IMotionWorld
    {
        internal bool Support=true;
        public PhysicsState Move(PhysicsState s,float vx,float vy,float gravity)=>new(s.Px+vx,3350,vx,0,true);
        public bool SafeSweep(PhysicsState a,PhysicsState b,out string reason){reason=null;return true;}
        public bool SafeLanding(PhysicsState s)=>Support&&s.Grounded;
    }
    private static void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS landing confirmation: "+message);}
    private static NavigationTask Replay(PhysicsState endpoint,World world)
    {
        var task=new NavigationTask();task.Begin(900,1,34475.117f,endpoint);
        var edge=new Prediction(true,"recorded walk prefix",new List<PredictedFrame>{new(new(1,false),endpoint)},endpoint,1,false);
        var executor=(LocalMotionController)typeof(NavigationTask).GetField("_executor",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(task);
        executor.Begin(900,1,1);executor.LoadPlan(edge);
        typeof(NavigationTask).GetField("_route",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(task,new Route(new(){edge},5,6.685));
        typeof(NavigationTask).GetField("_executing",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(task,true);
        task.After(endpoint,world);return task;
    }
    public static void Run()
    {
        var observed=new PhysicsState(34391.406f,3350,2.8799992f,0.33129883f,false);
        var world=new World();var task=Replay(observed,world);
        Check(task.Snapshot.MaxError==0&&task.Snapshot.Status=="confirming_landing"&&!task.Terminal
            &&task.Snapshot.ActualLanding==null,"recorded matching position/velocity is not a verified landing");
        Check(task.Before(900,1,34475.117f,observed,PhysicsParameters.Basic,world)==default
            &&task.Snapshot.CompletedActions==0,"unconfirmed landing releases intent without replan/replay");
        var settled=observed with{Vx=0,Vy=0,Grounded=true};task.After(settled,world);
        Check(task.Snapshot.ActualLanding==null,"one supported frame does not prove stable landing");
        task.After(settled,world);
        Check(task.Snapshot.Status=="running"&&task.Snapshot.CompletedActions==1&&task.Snapshot.LastLanding.Settled
            &&task.Snapshot.ActualLanding!=null,"two safe settled observations verify checkpoint but not distant goal");
        foreach(bool flagged in new[]{false,true})
        {
            world=new World{Support=flagged};task=Replay(observed with{Grounded=flagged},world);
            for(int i=1;i<NavigationTask.LandingConfirmationLimit;i++)task.After(observed with{Grounded=flagged},world);
            Check(task.Snapshot.Status=="unknown"&&task.Snapshot.Reason=="landing_confirmation_deadline"
                &&task.Snapshot.ActualLanding==null&&task.Snapshot.CompletedActions==0&&task.Terminal,
                "bounded unconfirmed descent/contact ends unknown; grounded flag="+flagged);
            Check(task.Before(900,1,34475.117f,observed,PhysicsParameters.Basic,world)==default,"unknown does not restart route");
        }
        world=new World();task=Replay(observed,world);var displaced=settled with{Px=settled.Px+20};
        task.After(displaced,world);task.After(displaced,world);
        Check(task.Snapshot.Status=="failed"&&task.Snapshot.ActualLanding!=null
            &&task.Snapshot.CompletedActions==0,"confirmed prediction deviation differs from unconfirmed landing");
        world=new World{Support=false};task=Replay(settled,world);
        for(int i=1;i<NavigationTask.LandingConfirmationLimit;i++)task.After(settled,world);
        Check(task.Snapshot.Status=="unknown"&&task.Snapshot.ObservedGrounded==true&&task.Snapshot.SafeSupport==false
            &&task.Snapshot.ActualLanding==null,"grounded flag and zero velocity cannot replace safe foot support");
        world=new World();
        task=Replay(observed,world);task.Cancel();task.After(settled,world);
        Check(task.Snapshot.Status=="idle"&&task.Snapshot.ActualLanding==null,"cancel discards pending confirmation");
        task=Replay(settled with{Vx=2.8799992f},world);
        Check(task.Snapshot.Status=="running"&&task.Snapshot.CompletedActions==1,"normal supported moving checkpoint stays immediate");
    }
}
