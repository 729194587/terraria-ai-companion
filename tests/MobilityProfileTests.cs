using System;
using CompanionBridge.Motion;

public static class MobilityProfileTests
{
    private static int checks;
    private static void Check(bool ok,string reason){if(!ok)throw new Exception(reason);checks++;}
    public static void Run()
    {
        var tracker=new MobilityTracker();var p=PhysicsParameters.Basic;
        var a=tracker.Sample(p,20,42,false,Array.Empty<string>(),10,1,7);
        var b=tracker.Sample(p,20,42,false,Array.Empty<string>(),11,1,7);
        Check(a.Supported&&a.AvailableModes.Length==2&&a.Revision==b.Revision&&b.SourceTick==11,"sampling tick alone does not invalidate profile");
        var c=tracker.Sample(PhysicsParameters.Swiftness,20,42,true,Array.Empty<string>(),12,1,7);
        Check(c.Supported&&c.Revision>a.Revision&&c.EffectivePhysics.MaxRun==3.75f,"measured numeric effect changes supported profile revision");
        Check(!tracker.Sample(p,20,42,true,Array.Empty<string>(),13,1,7).Supported,"buff name cannot authorize unmatching physics");
        Check(!tracker.Sample(PhysicsParameters.Swiftness,20,42,false,Array.Empty<string>(),14,1,7).Supported,"unattributed physics are not basic");
        Check(!tracker.Sample(p,20,42,false,new[]{"unsupported_equipment"},15,1,7).Supported,"unknown equipment rejects even with basic numbers");
        Check(!tracker.Sample(p,24,42,false,Array.Empty<string>(),16,1,7).Supported,"unverified body size rejected");
        Check(!tracker.Sample(p with{Gravity=.2f},20,42,false,Array.Empty<string>(),17,1,7).Supported,"unverified motion effects rejected");
        foreach(string effect in new[]{"extra_jump_usable","functional_wings","unvalidated_loaded_mod: Other"})
            Check(!tracker.Sample(p,20,42,false,new[]{effect},17,1,7).Supported,"unverified mode/mod cannot hide behind basic numbers");
        var mutable=new[]{"unknown_effect"};var immutable=tracker.Sample(p,20,42,false,mutable,17,1,7);mutable[0]="changed";
        Check(immutable.UnsupportedEffects[0]=="unknown_effect","profile evidence owns its effect snapshot");
        var scoped=tracker.Sample(p,20,42,false,Array.Empty<string>(),18,2,8);
        Check(scoped.Session==2&&scoped.Generation==8&&scoped.Revision>c.Revision,"revision is bound to control scope");
        foreach(bool upgrade in new[]{true,false})
        {
            var task=new NavigationTask();var world=new MotionOfflineTests.World();var s=new PhysicsState(1600,1558,0,0,true);
            var ph=upgrade?p:PhysicsParameters.Swiftness;bool changed=false;int neutral=0;
            StateSpacePlanner.Plan(s,1984,ph,world,budgetMs:5000);
            StateSpacePlanner.Plan(s,1984,upgrade?PhysicsParameters.Swiftness:p,world,budgetMs:5000);
            for(int n=0;n<600&&!task.Terminal;n++)
            {
                if(n==48){ph=upgrade?PhysicsParameters.Swiftness:p;task.InvalidateMobility(s,2);changed=true;}
                var input=task.Before(9,7,1984,s,ph,world);
                if(task.Snapshot.Status=="recovering"){Check(input==default,"capability transition releases old input");neutral++;}
                s=PhysicsSimulator.Step(s,input,ph,world);task.After(s,world,input==default);
            }
            Check(changed&&neutral>=2&&task.Snapshot.Status=="arrived","same action settles and replans after profile change: "+upgrade+" "+task.Snapshot.Status+" "+task.Snapshot.Reason+" "+task.Snapshot.RecoveryReason);
            Check(task.Snapshot.MobilityRevision==2&&task.Snapshot.PlanMobilityRevision==2,"no expired plan after capability change");
            Check(task.Snapshot.FailedEdgeCount==0,"old capability route is not quarantined as a new-profile obstacle");
            var ended=task.Snapshot;task.InvalidateMobility(s,3);task.Before(9,7,1984,s,p,world);
            Check(task.Snapshot==ended,"profile change cannot revive terminal action");
        }
        var regionWorld=new MotionOfflineTests.RegionWorld();var goal=new LandingRegion(1664,1744,1552);
        var nav=new NavigationTask();var state=new PhysicsState(1600,1558,0,0,true);
        for(int n=0;n<500&&!nav.Terminal;n++)
        {var input=nav.Before(10,7,goal,state,PhysicsParameters.Swiftness,regionWorld);state=PhysicsSimulator.Step(state,input,PhysicsParameters.Swiftness,regionWorld);nav.After(state,regionWorld,input==default);}
        Check(nav.Snapshot.Status=="arrived"&&goal.Arrived(state,regionWorld),"supported numeric profile works for elevated landing regions");
        Console.WriteLine($"M4.4 mobility: {checks} checks (physics fixtures; native validation reported separately).");
    }
}
