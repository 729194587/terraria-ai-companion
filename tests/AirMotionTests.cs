using System;
using System.Linq;
using System.Collections.Generic;
using CompanionBridge.Motion;

public static class AirMotionTests
{
    public static void Run()
    {
        int checks=0;void Check(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
        MovementSnapshot Snapshot(int wing=1,float time=100,int boots=0,int delay=0,int visualDelay=0)=>new(Array.Empty<ExtraJumpSnapshot>(),false,
            new(wing,time,100,59,AirMotionModel.AngelStats),new(boots,7,7,delay,visualDelay),new(false,-1,null,54),0,true,Array.Empty<GrappleSnapshot>(),new(false,0,false,false,false,false));
        Check(AirMotionModel.Select(Snapshot(wing:2))==AirMotionModel.Angel,"native Angel Wings slot matches pinned stats");
        foreach(float time in new[]{0f,1f,100f})foreach(int boots in new[]{0,1})
        {
            var m=Snapshot(time:time,boots:boots);
            Check(AirMotionModel.Strategy(m)==AirKind.Wings&&AirMotionModel.Select(m)==AirMotionModel.Angel,"functional wings always win, including exhausted wings");
        }
        Check(AirMotionModel.Select(Snapshot(wing:0,boots:1))==AirMotionModel.Rocket,"rocket only without functional wings");
        Check(AirMotionModel.Select(Snapshot(wing:0,boots:0))==null,"ordinary route without either capability");
        Check(AirMotionModel.Strategy(Snapshot(wing:99,boots:1))==AirKind.Wings&&AirMotionModel.Select(Snapshot(wing:99,boots:1))==null,"unsupported wings never fall back to rockets");
        Check(AirMotionModel.Select(Snapshot(delay:1))==null,"existing rocket pulse with wings is not combined");
        var flat=new MotionOfflineTests.World();
        var wing=PhysicsParameters.Basic with{Air=AirMotionModel.Angel};
        var rocket=PhysicsParameters.Basic with{Air=AirMotionModel.Rocket};
        var resource=new AirResources(100,100,7,7,0,false,true,false);
        var air=new PhysicsState(1600,1400,0,-4, false,Air:resource);
        var up=PhysicsSimulator.Step(air,new(1,true),wing,flat);
        Check(up.AirPhase==AirPhase.Flying&&up.Air.WingTime==99&&Math.Abs(up.Vy+4.1f)<.001f,"wing ascent consumes one tick and bypasses gravity");
        var down=PhysicsSimulator.Step(air,new(1,false),wing,flat);
        Check(down.AirPhase==AirPhase.Descending&&down.Air.WingTime==100&&Math.Abs(down.Vy+3.6f)<.001f,"release jump selects safe ordinary descent");
        var exhausted=air with{Vy=5,Air=resource with{WingTime=0,Boots=1,RocketTime=0}};
        var glide=PhysicsSimulator.Step(exhausted,new(1,true),wing,flat);
        Check(glide.AirPhase==AirPhase.Gliding&&Math.Abs(glide.Vy-wing.MaxFall/3)<.001f&&glide.Air.Delay==0,"exhausted wing glide, never rocket fallback");
        var converted=PhysicsSimulator.Step(exhausted with{Air=exhausted.Air with{RocketTime=2}},new(1,true),wing,flat);
        Check(converted.Air.WingTime==12&&converted.Air.RocketTime==0&&converted.AirPhase!=AirPhase.RocketPulse,"native fuel conversion is represented without rocket strategy");
        var r=air with{Vy=-4,Air=new(0,0,7,7,0,true,true,false,1)};
        var pulse=PhysicsSimulator.Step(r,new(1,true),rocket,flat);
        Check(pulse.AirPhase==AirPhase.RocketPulse&&pulse.Air.RocketTime==6&&pulse.Air.Delay==9&&pulse.Air.RocketFrame,"native ten-update pulse starts and consumes one charge");
        var continuing=PhysicsSimulator.Step(pulse,new(1,false),rocket,flat);
        Check(continuing.AirPhase==AirPhase.RocketPulse&&continuing.Air.Delay==8&&continuing.Air.RocketTime==6,"release does not falsely cancel active native pulse");
        var waiting=PhysicsSimulator.Step(r with{Air=r.Air with{Delay=3}},new(1,true),rocket,flat);
        Check(waiting.Air.RocketTime==7&&waiting.Air.Delay==2,"cooldown prevents another consumption");
        Check(AirMotionModel.Resources(Snapshot(wing:0,boots:1,visualDelay:12))==AirMotionModel.Resources(Snapshot(wing:0,boots:1,visualDelay:30)),"visual rocketDelay2 is diagnostic only, not a physical search dimension");
        foreach(var state in new[]{r with{Air=r.Air with{CanRocket=false},Vy=-6},r with{Air=r.Air with{RocketRelease=false}},r with{Air=r.Air with{RocketTime=0}}})
            Check(PhysicsSimulator.Step(state,new(1,true),rocket,flat).AirPhase!=AirPhase.RocketPulse,"native eligibility cannot be assumed");
        Check(!PhysicsSimulator.SupportedCheckpoint(new(1600,1558,0,0,true,Air:pulse.Air),flat),"active native pulse is not a stable checkpoint");
        var tracker=new MobilityTracker();var a=tracker.Sample(wing,20,42,false,Array.Empty<string>(),1,1,1,movement:Snapshot());
        var b=tracker.Sample(wing,20,42,false,Array.Empty<string>(),2,1,1,movement:Snapshot(time:99),observedPhysics:PhysicsParameters.Basic with{AccRunSpeed=6.25f});
        Check(a.Revision==b.Revision&&b.SourceTick==2,"normal wing resource drain keeps revision");
        Check(b.ObservedPhysics.AccRunSpeed==6.25f&&b.EffectivePhysics.AccRunSpeed==3,"raw native physics stays visible beside normalized planning parameters");
        var c=tracker.Sample(wing,20,42,false,Array.Empty<string>(),3,1,1,movement:Snapshot(time:98),unexpectedAirResource:true);
        Check(c.Revision>b.Revision,"unexpected resource loss invalidates version");
        foreach(var ph in new[]{wing,rocket})
        {
            float height=ph.Air.Kind==AirKind.Wings?160:128;
            var world=new MotionOfflineTests.World{WallX=1696,WallWidth=512,WallHeight=height,AllowWallLanding=true};
            var start=new PhysicsState(1600,1558,0,0,true,Air:ph.Air.Kind==AirKind.Wings?resource:new(0,0,7,7,0,false,true,false,1));
            var goal=new LandingRegion(1712,1760,1600-height);
            var plan=StateSpacePlanner.Plan(start,goal,ph,world,maxExpansions:128,budgetMs:5000);
            Check(plan.ReachedGoal&&plan.Actions.Any(p=>p.AirStartFrame>=0),"planner uses air model to reach elevated landing: "+ph.Air.Kind);
            var leftWorld=new MotionOfflineTests.World{WallX=1408,WallWidth=96,WallHeight=height,AllowWallLanding=true};
            Check(StateSpacePlanner.Plan(start,new LandingRegion(1424,1464,1600-height),ph,leftWorld,maxExpansions:128,budgetMs:5000).ReachedGoal,"leftward air landing also planned: "+ph.Air.Kind);
            var graphWorld=new SurfaceGraphTests.Terrain{Floor=true};
            for(int x=106;x<122;x++)for(int y=(int)((1600-height)/16);y<100;y++)graphWorld.Tiles[(x,y)]='#';
            var continuous=new NavigationTask{Continuous=true};var real=start;
            for(int frame=0;frame<1800&&!continuous.Terminal;frame++)
            {
                var input=continuous.Before(2,1,goal,real,ph,graphWorld);
                real=PhysicsSimulator.Step(real,input,ph,graphWorld) with{AirPhase=AirPhase.None};continuous.After(real,graphWorld,input==default);
            }
            Check(continuous.Snapshot.Status=="arrived"&&continuous.Snapshot.SurfaceGraph!=null,"continuous Surface Graph navigation uses observed air resources: "+ph.Air.Kind+" "+continuous.Snapshot.Reason);
            foreach(string scenario in new[]{"normal","missing_thrust","offset","contact"})
            {
                var task=new NavigationTask();var s=start;bool changed=false;int attempts=0;
                for(int frame=0;frame<1200&&!task.Terminal;frame++)
                {
                    var input=task.Before(1,1,goal,s,ph,world);var next=PhysicsSimulator.Step(s,input,ph,world);
                    if(!changed&&next.AirPhase is AirPhase.Flying or AirPhase.RocketPulse)
                    {
                        if(scenario=="missing_thrust")next=PhysicsSimulator.Step(s,new(input.Direction,false),ph,world);
                        if(scenario=="offset")next=next with{Px=next.Px-32};
                        if(scenario=="contact")next=next with{Py=s.Py,Vy=0};
                        changed=true;attempts++;
                    }
                    // A real observer has no synthetic phase label; resources are
                    // compared directly, never inferred from the requested key.
                    s=next with{AirPhase=AirPhase.None};task.After(s,world,input==default);
                }
                Check(task.Snapshot.Status=="arrived","air task reaches actual safe landing: "+ph.Air.Kind+" "+scenario+" "+task.Snapshot.Reason);
                Check(changed&&attempts==1&&(scenario=="normal"?task.Snapshot.RecoveryCount==0:task.Snapshot.RecoveryCount>=1),"real thrust/offset feedback and safe recovery: "+scenario);
            }
            var flight=plan.Actions.First(p=>p.AirStartFrame>=0);
            var memory=new NavigationFailureMemory();memory.Remember(start,flight,0);
            Check(memory.Reject(start,flight,1)&&!memory.Reject(start,flight with{AirStartFrame=flight.AirStartFrame+1},1),"air failure memory distinguishes timing");
            var exact=flight.Frames[Math.Min(20,flight.Frames.Count-2)].State;
            Check(!AirMotionModel.Matches(exact,exact with{Air=exact.Air with{RocketTime=99}}),"resource mismatch cannot be a confirmed trigger");
            if(ph.Air.Kind==AirKind.Wings)
            {
                int at=Math.Min(24,flight.Frames.Count-3);
                var actual=flight.Frames[at-1].State with{Px=flight.Frames[at-1].State.Px+1};
                var corrected=AirMotionModel.Correct(flight,at,actual,ph,world,budgetMs:1000);
                Check(!ReferenceEquals(corrected,flight)&&PhysicsSimulator.Settled(corrected.Landing,world),"small air drift rebases only with validated complete landing tail");
                var expected=PhysicsSimulator.Step(actual,corrected.Frames[at].Input,ph,world);
                Check(expected==corrected.Frames[at].State,"feedback trajectory starts from observed position, never rewrites player pose");
                Check(ReferenceEquals(AirMotionModel.Correct(flight,at,actual,ph,world,budgetMs:0),flight),"time slice cannot accept an unverified partial correction");
            }
        }
        var labels=new HashSet<PhysicsState>{air,air with{Air=resource with{WingTime=99}},r,r with{Air=r.Air with{Delay=1}}};
        Check(labels.Count==4,"search labels retain all resource/cooldown dimensions");
        foreach(bool hazard in new[]{true,false})
        {
            var unsafeWorld=new MotionOfflineTests.World{Hazard=hazard,Unknown=!hazard};
            Check(!PhysicsSimulator.Predict(new(1600,1558,0,0,true,Air:resource),1,15,wing,unsafeWorld,airStartFrame:16,airHoldFrames:16).Safe,"hazard and unknown coverage remain hard guards");
        }
        Console.WriteLine($"Phase 4.3 air motion: {checks} checks; offline collision doubles, no real-world flight claim.");
    }
}
