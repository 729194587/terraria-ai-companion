using System;
using CompanionBridge.Motion;

public static class MobilityProfileTests
{
    private static int checks;
    private static void Check(bool ok,string reason){if(!ok)throw new Exception(reason);checks++;}
    public static void Run()
    {
        MovementSnapshotTests.Run();
        var budget=new TravelHealthBudget();
        foreach(int life in new[]{100,90,80,70,60})
        {
            budget.Begin(0,0,life);
            Check(TravelHealthBudget.Exceeded(budget.StartLife,life,100)==(life==60),"accumulate separate travel hits");
        }
        budget.Begin(0,1,60);
        Check(budget.StartLife==60,"new generation starts a fresh budget");
        budget.Begin(1,1,80);
        Check(budget.StartLife==80,"new session starts a fresh budget");
        var tracker=new MobilityTracker();var p=PhysicsParameters.Basic;
        var a=tracker.Sample(p,20,42,false,Array.Empty<string>(),10,1,7);
        var b=tracker.Sample(p,20,42,false,Array.Empty<string>(),11,1,7);
        Check(a.Supported&&a.AvailableModes.Length==2&&a.Revision==b.Revision&&b.SourceTick==11,"sampling tick alone does not invalidate profile");
        var healed=p with{Fall=new(0,false,90)};
        Check(tracker.Sample(healed,20,42,false,Array.Empty<string>(),12,1,7).Revision==a.Revision
            && p==healed && p.GetHashCode()==healed.GetHashCode(),"health is diagnostic, not route identity");
        Check(tracker.Sample(healed with{Fall=new(1,false,90)},20,42,false,Array.Empty<string>(),12,1,7).Revision>a.Revision,
            "extra fall protection invalidates route");
        Check(!GrappleClimbPlan.Supports(p with{NoAirRepress=true}) && !GrappleClimbPlan.Supports(p with{Air=AirMotionModel.Rocket})
            && GrappleClimbPlan.Supports(p with{Air=AirMotionModel.Angel,ExtraJump=ExtraJumpModel.Cloud}),
            "grapple accepts modeled wings and jumps, rejects unsupported repress and rocket-only");
        var jumps=new ExtraJumpModel("first",2,2,Next:ExtraJumpModel.Cloud);
        var wing=healed with{Air=AirMotionModel.Angel,ExtraJump=jumps};
        var pose=new PhysicsState(0,0,0,0,false,ExtraAvailable:true,RemainingJumps:1,Air:new(40,40,0,0,0,false,false,false));
        foreach(var launch in SurfaceNavigator.UphillLaunches(pose,1,wing))
            if(launch.AirStart>=0 && launch.ExtraFrame>=0)
                Check(launch.ExtraCount==1 && launch.AirStart==launch.ExtraFrame+ExtraJumpModel.Cloud.HoldFrames(wing)+2,
                    "spent first jump excluded from wing activation timing");
        var spent=pose with{ExtraAvailable=false,RemainingJumps=-1};
        foreach(var launch in SurfaceNavigator.UphillLaunches(spent,1,wing))
            Check(launch.ExtraFrame<0,"no modeled jump remains on this airborne observation");
        var c=tracker.Sample(PhysicsParameters.Swiftness,20,42,true,Array.Empty<string>(),12,1,7);
        Check(c.Supported&&c.Revision>a.Revision&&c.EffectivePhysics.MaxRun==3.75f,"measured numeric effect changes supported profile revision");
        Check(tracker.Sample(p,20,42,true,Array.Empty<string>(),13,1,7).Supported,"buff name does not override effective basic physics");
        Check(tracker.Sample(PhysicsParameters.Swiftness,20,42,false,Array.Empty<string>(),14,1,7).Supported,"matching physics needs no buff identity");
        Check(!tracker.Sample(p,20,42,false,new[]{"unsupported_motion_mode"},15,1,7).Supported,"unsupported motion mode rejects even with basic numbers");
        Check(!tracker.Sample(p,24,42,false,Array.Empty<string>(),16,1,7).Supported,"unverified body size rejected");
        Check(tracker.Sample(p with{Gravity=.2f},20,42,false,Array.Empty<string>(),17,1,7).Supported,"measured positive gravity is supported");
        Check(!tracker.Sample(p with{Gravity=float.NaN},20,42,false,Array.Empty<string>(),17,1,7).Supported,"invalid physics rejected");
        foreach(string effect in new[]{"extra_jump_usable","functional_wings"})
            Check(!tracker.Sample(p,20,42,false,new[]{effect},17,1,7).Supported,"supplied unsupported effect is retained despite matching numbers (not Mod discovery)");
        var mutable=new[]{"unknown_effect"};var immutable=tracker.Sample(p,20,42,false,mutable,17,1,7);mutable[0]="changed";
        Check(immutable.UnsupportedEffects[0]=="unknown_effect","profile evidence owns its effect snapshot");
        var scoped=tracker.Sample(p,20,42,false,Array.Empty<string>(),18,2,8);
        Check(scoped.Session==2&&scoped.Generation==8&&scoped.Revision>c.Revision,"revision is bound to control scope");
        var expiry=new MobilityTracker();var swift=PhysicsParameters.Swiftness;
        var last=expiry.Sample(swift,20,42,true,Array.Empty<string>(),100,1,7,1);
        Check(!expiry.IsSwiftnessExpiryTail(swift,false,102,1,7),"sampling gap cannot authorize expiry tail");
        Check(!expiry.IsSwiftnessExpiryTail(swift,false,101,2,7)&&!expiry.IsSwiftnessExpiryTail(swift,false,101,1,8),"expiry cannot cross identity scope");
        Check(!expiry.IsSwiftnessExpiryTail(p,false,101,1,7),"tail requires measured Swift physics");
        var tail=expiry.Sample(swift,20,42,false,Array.Empty<string>(),101,1,7);
        Check(tail.Supported&&tail.SwiftnessExpiryTail&&tail.Revision==last.Revision,"last native effect frame retains correct physics version");
        Check(!expiry.IsSwiftnessExpiryTail(swift,false,102,1,7),"tail cannot renew itself");
        var expired=expiry.Sample(p,20,42,false,Array.Empty<string>(),102,1,7);
        Check(expired.Supported&&!expired.SwiftnessExpiryTail&&expired.Revision>tail.Revision,"next Basic frame invalidates Swift plan");
        expiry.Reset();Check(!expiry.IsSwiftnessExpiryTail(swift,false,103,1,7),"cancel cannot retain expiry permission");
        expiry.Sample(swift,20,42,true,Array.Empty<string>(),110,1,7,2);
        Check(!expiry.Sample(swift,20,42,false,Array.Empty<string>(),111,1,7).SwiftnessExpiryTail,"early disappearance cannot masquerade as natural expiry");
        expiry.Sample(swift,20,42,true,new[]{"unknown_mod"},120,1,7,1);
        Check(!expiry.Sample(swift,20,42,false,Array.Empty<string>(),121,1,7).SwiftnessExpiryTail,"unsupported prior state does not mark expiry");
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
        // Remove and restore on adjacent updates while the first recovery is still braking.
        {
            var rapid=new MobilityTracker();var task=new NavigationTask();var world=new MotionOfflineTests.World();
            var s=new PhysicsState(1600,1558,0,0,true);long first=0,removed=0,restored=0,activeRevision=0;int changedNeutral=0;
            for(int n=0;n<600&&!task.Terminal;n++)
            {
                bool on=n!=48;var ph=on?swift:p;
                var profile=rapid.Sample(ph,20,42,on,Array.Empty<string>(),(ulong)n,9,7,on?100:0);
                if(n==0)first=profile.Revision;if(n==48)removed=profile.Revision;if(n==49)restored=profile.Revision;
                if(activeRevision!=profile.Revision){task.InvalidateMobility(s,profile.Revision);activeRevision=profile.Revision;}
                var input=task.Before(9,7,1984,s,ph,world);
                if(n is 48 or 49){Check(input==default,"rapid toggles cannot execute abandoned input");changedNeutral++;}
                s=PhysicsSimulator.Step(s,input,ph,world);task.After(s,world,input==default);
            }
            Check(first<removed&&removed<restored&&changedNeutral==2,"ABA profile transition never reuses old revision");
            Check(task.Snapshot.Status=="arrived"&&task.Snapshot.RecoveryCount==1&&task.Snapshot.RecoveryReplans==1,"rapid changes coalesce in one safe same-action recovery");
            Check(task.Snapshot.PlanMobilityRevision==restored&&task.Snapshot.FailedEdgeCount==0,"resumed route uses latest version without stale obstacle memory");
        }
        var regionWorld=new MotionOfflineTests.RegionWorld();var goal=new LandingRegion(1664,1744,1552);
        var nav=new NavigationTask();var state=new PhysicsState(1600,1558,0,0,true);
        for(int n=0;n<500&&!nav.Terminal;n++)
        {var input=nav.Before(10,7,goal,state,PhysicsParameters.Swiftness,regionWorld);state=PhysicsSimulator.Step(state,input,PhysicsParameters.Swiftness,regionWorld);nav.After(state,regionWorld,input==default);}
        Check(nav.Snapshot.Status=="arrived"&&goal.Arrived(state,regionWorld),"supported numeric profile works for elevated landing regions");
        Console.WriteLine($"M4.4 mobility: {checks} checks (physics fixtures; native validation reported separately).");
    }
}
