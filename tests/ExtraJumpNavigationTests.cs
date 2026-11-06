using System;
using System.Linq;
using CompanionBridge.Motion;

public static class ExtraJumpNavigationTests
{
    public static void Run()
    {
        int checks=0;
        void Check(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
        var ph=PhysicsParameters.Basic with{ExtraJump=ExtraJumpModel.Cloud};
        var flat=new MotionOfflineTests.World();
        var s=new PhysicsState(1600,1558,0,0,true,ExtraAvailable:true);
        for(int i=0;i<15;i++)s=PhysicsSimulator.Step(s,new(1,true),ph,flat);
        s=PhysicsSimulator.Step(s,new(1,false),ph,flat);
        Check(s.ReleaseJump&&s.JumpFramesLeft==0&&s.ExtraAvailable,"ordinary jump then release preserves resource");
        s=PhysicsSimulator.Step(s,new(1,true),ph,flat);
        Check(s.ExtraActive&&!s.ExtraAvailable&&s.ExtraUsed&&s.JumpFramesLeft==11&&Math.Abs(s.Vy+4.61f)<.001f,"native Cloud launch model");
        s=PhysicsSimulator.Step(s,new(1,false),ph,flat);float vy=s.Vy;
        s=PhysicsSimulator.Step(s,new(1,true),ph,flat);
        Check(!s.ExtraActive&&s.JumpFramesLeft==0&&s.Vy>vy,"one charge cannot trigger twice in air");
        var renewable=s with{ExtraAvailable=true,ExtraActive=true,ExtraUsed=true};
        Check(ExtraJumpModel.MatchesResource(renewable,renewable),"active plus available still proves activation");
        Check(PhysicsSimulator.Step(renewable with{ReleaseJump=true},new(1,true),ph,flat).JumpFramesLeft==0,"cannot reuse consumed charge even with Available true");
        var world=new MotionOfflineTests.World{WallX=1696,WallWidth=512,WallHeight=128,AllowWallLanding=true};
        var start=new PhysicsState(1600,1558,0,0,true,ExtraAvailable:true);
        var goal=new LandingRegion(1712,1760,1472);
        var ordinary=StateSpacePlanner.Plan(start,goal,PhysicsParameters.Basic,world,maxExpansions:128,budgetMs:5000);
        var route=StateSpacePlanner.Plan(start,goal,ph,world,maxExpansions:128,budgetMs:5000);
        Check(!ordinary.ReachedGoal&&route.ReachedGoal,"ExtraJump reaches a platform ordinary jump cannot reach");
        Check(route.Actions.Any(a=>a.ExtraJumpFrame>=0),"planner selects extra jump");
        foreach(var action in route.Actions.Where(a=>a.ExtraJumpFrame>=0))
        {
            Check(!action.Frames[action.ExtraJumpFrame-1].Input.Jump&&action.Frames[action.ExtraJumpFrame].Input.Jump,"release/repress input edge");
            Check(action.Frames.Any(f=>f.State.ExtraActive&&!f.State.ExtraAvailable),"explicit consumption and activation");
        }
        MovementSnapshot Snapshot(PhysicsState state,string name="Terraria/CloudInABottleJump",bool enabled=true,bool blocked=false)=>new(
            new[]{new ExtraJumpSnapshot(name,0,enabled,state.ExtraAvailable,state.ExtraActive)},blocked,
            new(0,0,0,64,null),new(0,0,0),new(false,-1,null,54),0,true,Array.Empty<GrappleSnapshot>(),new(false,0,false,false,false,false));
        Check(ExtraJumpModel.Select(Snapshot(start))==ExtraJumpModel.Cloud,"registry-based model selection");
        Check(ExtraJumpModel.Select(Snapshot(start,"Mod/CloudInABottleJump"))==null,"short names do not grant support");
        foreach(string scenario in new[]{"normal","failed_trigger","offset","alternate_landing","ceiling_contact"})
        {
            var task=new NavigationTask();var tracker=new MobilityTracker();s=start;long revision=0;bool injected=false;int versions=0;
            for(int frame=0;frame<1000&&!task.Terminal;frame++)
            {
                var p=tracker.Sample(ph,20,42,false,Array.Empty<string>(),(ulong)frame,1,1,movement:Snapshot(s),
                    expectedExtraJump:ExtraJumpModel.ExpectedBoundary(task.ExpectedState,s));
                if(p.Revision!=revision){revision=p.Revision;versions++;task.InvalidateMobility(s,revision,ph);}
                var input=task.Before(1,1,goal,s,ph,world);
                if(scenario=="failed_trigger"&&!injected&&input.Jump&&s.ReleaseJump&&!s.Grounded&&s.JumpFramesLeft==0)
                {input=input with{Jump=false};injected=true;}
                s=PhysicsSimulator.Step(s,input,ph,world);
                if(!injected&&scenario=="offset"&&s.ExtraActive){s=s with{Px=s.Px-32};injected=true;}
                if(!injected&&scenario=="alternate_landing"&&s.ExtraUsed&&!s.ExtraActive&&s.Vy>0)
                {s=start with{Px=start.Px+32};injected=true;}
                if(!injected&&scenario=="ceiling_contact"&&s.ExtraActive)
                {s=s with{Vy=0,JumpFramesLeft=0};injected=true;}
                task.After(s,world,input==default);
            }
            Check(task.Snapshot.Status=="arrived","task completes after "+scenario+": "+task.Snapshot.Reason);
            if(scenario=="normal")Check(versions==1&&task.Snapshot.RecoveryCount==0,"expected resource transitions retain plan revision");
            else Check(injected&&task.Snapshot.RecoveryCount>=1&&task.Snapshot.RecoveryReplans>=1,"deviation settles and replans: "+scenario);
        }
        var tracker2=new MobilityTracker();var before=tracker2.Sample(ph,20,42,false,Array.Empty<string>(),0,1,1,movement:Snapshot(start));
        var changed=tracker2.Sample(ph,20,42,false,Array.Empty<string>(),1,1,1,movement:Snapshot(start with{ExtraAvailable=false}));
        Check(changed.Revision>before.Revision,"unexpected resource loss still invalidates");
        var disabled=tracker2.Sample(ph,20,42,false,Array.Empty<string>(),2,1,1,movement:Snapshot(start,enabled:false),expectedExtraJump:true);
        Check(disabled.Revision>changed.Revision,"expected resource exemption never hides enabled-state change");
        var blocked=tracker2.Sample(ph,20,42,false,Array.Empty<string>(),3,1,1,movement:Snapshot(start,blocked:true),expectedExtraJump:true);
        Check(blocked.Revision>disabled.Revision&&ExtraJumpModel.Select(Snapshot(start,blocked:true))==null,"blocking invalidates and prevents model selection");
        var physical=tracker2.Sample(ph with{AccRun=.1f},20,42,false,Array.Empty<string>(),4,1,1,movement:Snapshot(start),expectedExtraJump:true);
        Check(physical.Revision>blocked.Revision,"unexpected physics is never swallowed by a resource transition");
        Check(!ExtraJumpModel.ExpectedBoundary(start,start with{ExtraAvailable=false}),"unexpected loss cannot authorize expected transition");
        var lost=new System.Collections.Generic.HashSet<PhysicsState>{start,start with{ExtraAvailable=false},start with{ExtraActive=true},start with{ExtraUsed=true}};
        Check(lost.Count==4,"search state equality retains every resource dimension");
        var memory=new NavigationFailureMemory();var edge=route.Actions.First(a=>a.ExtraJumpFrame>=0);
        memory.Remember(start,edge,0);
        Check(memory.Reject(start,edge,1)&&!memory.Reject(start,edge with{ExtraJumpFrame=edge.ExtraJumpFrame+8},1),"failure memory distinguishes trigger schedule");
        Console.WriteLine($"Phase 4.2 ExtraJump: {checks} checks; collision doubles, native tests separate.");
    }
}
