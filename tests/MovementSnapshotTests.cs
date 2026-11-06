using System;
using System.Collections.Generic;
using System.Linq;
using CompanionBridge.Motion;

public static class MovementSnapshotTests
{
    public static void Run()
    {
        int checks=0;
        void Check(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
        MovementSnapshot Make(ExtraJumpSnapshot[] jumps=null,bool block=false,WingSnapshot wings=null,
            RocketSnapshot rockets=null,MountSnapshot mount=null,GrappleSnapshot[] hooks=null,MovementEnvironment env=null)
            =>new(jumps??Array.Empty<ExtraJumpSnapshot>(),block,wings??new(0,0,0,64,null),rockets??new(0,0,0),
                mount??new(false,-1,null,50),hooks?.Length??0,true,hooks??Array.Empty<GrappleSnapshot>(),env??new(false,0,false,false,false,false));
        var ordered=new[]{new ExtraJumpSnapshot("A/Jump",0,true,true,false),new ExtraJumpSnapshot("B/Jump",1,true,false,true)};
        var snapshot=Make(ordered);ordered[0]=null;
        Check(snapshot.ExtraJumps[0].FullName=="A/Jump"&&snapshot.ExtraJumps[1].Order==1,"own ordered FullName identity copy");
        var exposed=snapshot.ExtraJumps;exposed[1]=null;
        Check(snapshot.ExtraJumps[1].FullName=="B/Jump","output cannot mutate prior snapshot");
        var components=new[]{typeof(MovementSnapshot),typeof(ExtraJumpSnapshot),typeof(WingSnapshot),typeof(WingBaseSnapshot),typeof(RocketSnapshot),typeof(MountSnapshot),typeof(GrappleSnapshot),typeof(MovementEnvironment)};
        foreach(var component in components)foreach(var property in component.GetProperties())
        {
            var t=property.PropertyType;
            Check(t.IsPrimitive||t==typeof(string)||components.Contains(t)||(t.IsArray&&components.Contains(t.GetElementType())),"snapshot exposes only primitive/string/owned value components: "+property.Name);
        }
        foreach(bool enabled in new[]{false,true})foreach(bool available in new[]{false,true})foreach(bool active in new[]{false,true})foreach(bool blocked in new[]{false,true})
        {
            var m=Make(new[]{new ExtraJumpSnapshot("Mod/Jump",0,enabled,available,active)},blocked);
            Check(m.ExtraJumps[0]==new ExtraJumpSnapshot("Mod/Jump",0,enabled,available,active),"retain every native state combination");
            Check(m.ActiveExtraJump==active&&m.PotentialExtraJump==(!blocked&&available),"active never hidden by block; available is potential only");
        }
        var tracker=new MobilityTracker();ulong tick=0;
        MobilityProfile Sample(MovementSnapshot m)=>tracker.Sample(PhysicsParameters.Basic,20,42,false,
            m.ActiveExtraJump||m.PotentialExtraJump?new[]{"unmodeled_extra_jump"}:Array.Empty<string>(),++tick,2,3,movement:m);
        var empty=Make();var first=Sample(empty);
        var resource=Make(wings:new(0,15,100,64,null),rockets:new(0,10,20));var second=Sample(resource);
        Check(first.Revision==second.Revision&&first.SourceTick<second.SourceTick,"tick and current resources do not invalidate");
        var discrete=new[]{snapshot,Make(block:true),Make(wings:new(2,1,10,64,new(100,6,1,true,2,1))),
            Make(rockets:new(1,1,10)),Make(mount:new(true,7,"Mod/Mount",51)),
            Make(hooks:new[]{new GrappleSnapshot(0,1,true,true,13,55,2,true)}),
            Make(env:new(true,1,false,false,false,false)),Make(env:new(false,0,true,false,false,false)),
            Make(env:new(false,0,false,true,false,false)),Make(env:new(false,0,false,false,true,false)),Make(env:new(false,0,false,false,false,true))};
        foreach(var changed in discrete)
        {
            var a=Sample(empty);var b=Sample(changed);var c=Sample(empty);
            Check(a.Revision<b.Revision&&b.Revision<c.Revision,"discrete A-B-A revision monotonic");
            Check(b.AvailableModes.All(mode=>mode=="ground_walk"||mode=="ground_jump"),"discovery adds no movement mode");
        }
        var jump=new ExtraJumpSnapshot("A/Jump",0,true,false,false);
        var mutations=new[]{jump with{FullName="B/Jump"},jump with{Order=1},jump with{Enabled=false},jump with{Available=true},jump with{Active=true}};
        foreach(var mutation in mutations)
            Check(!Make(new[]{jump}).SamePlanningAssumptions(Make(new[]{mutation})),"identity/order/state affects planning assumptions");
        Check(!snapshot.SamePlanningAssumptions(Make(snapshot.ExtraJumps.Reverse().ToArray())),"registry reorder invalidates");
        Check(!snapshot.SamePlanningAssumptions(Make(new[]{snapshot.ExtraJumps[0]})),"registry removal invalidates");
        Check(Sample(snapshot).AvailableModes.Length==0,"unmodeled available/active jump has no available navigation mode");
        var wings=new WingSnapshot(2,100,100,64,new(100,6,1,false,0,0));
        Check(Make(wings:wings).SamePlanningAssumptions(Make(wings:wings with{Time=0})),"functional wing resource drains remain observational");
        Check(!Make(wings:wings).SamePlanningAssumptions(Make(wings:wings with{RegisteredSlots=65})),"registry size change invalidates");
        var logs=new List<string>();var diagnostics=new NavigationDiagnostics(logs.Add);
        var nav=new NavigationTask().Snapshot with{Session=2,Generation=3,Mobility=first};
        diagnostics.Observe(nav);diagnostics.Observe(nav with{Mobility=second});
        Check(logs.Count(s=>s.Contains("navigation_mobility"))==1,"resources do not flood diagnostic log");
        var changedProfile=Sample(snapshot);diagnostics.Observe(nav with{Mobility=changedProfile});
        Check(logs.Count(s=>s.Contains("navigation_mobility"))==2&&logs.Last(s=>s.Contains("navigation_mobility")).Contains("previousMobility")&&logs.Last(s=>s.Contains("navigation_mobility")).Contains("extraJumps"),"discrete diagnostic has before/after snapshots");
        Console.WriteLine($"Phase 4.1 movement snapshots: {checks} checks.");
    }
}
