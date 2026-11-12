using System;
using System.Collections.Generic;
using System.Text.Json;
using CompanionBridge.ServerIpc;
using Result = CompanionBridge.ServerIpc.AiAtomicActions.NativeResult;

public static class TaskRuntimeOfflineTests
{
    private static int checks;
    private static void Check(bool ok,string text){if(!ok)throw new Exception(text);checks++;}
    private sealed class World : IMineTargetWorld
    {
        internal bool Reach,Absent=true,ThrowPrepare,NoApproach,AllowOpening,PlaceSupport;
        public bool ReplanAfterChange=>PlaceSupport;
        internal bool Settled=true;
        internal bool PendingPlanning,PreferChanges;
        internal Result PlanningStop;
        internal CompanionTask Clearance;
        internal int PlanningReleases;
        public bool PlanningPending=>PendingPlanning;
        public Result PlanningFailure=>PlanningStop;
        public bool PreferEdit(int remaining)=>PreferChanges;
        public void ReleasePlanning(){PlanningReleases++;}
        internal string Error,PrepareError,Danger;
        public string InterruptionReason()=>Danger;
        internal int Preparations,Observations;
        public string Prepare(){Preparations++;if(ThrowPrepare)throw new Exception("preflight");return PrepareError;}
        public string Revalidate(){Observations++;return Error;}
        public bool CanMine()=>Reach;
        public JsonElement? FindApproach()=>NoApproach?null:JsonSerializer.SerializeToElement(new{minPlayerLeftX=100,maxPlayerLeftX=108,surfaceY=200});
        public JsonElement MineArguments()=>JsonSerializer.SerializeToElement(new{x=12,y=10});
        public NavigationStep FindOpening(int remainingTiles)=>Clearance!=null?new("excavate_tiles",default,Clearance)
            :AllowOpening?new(PlaceSupport?"place_tile":"mine_tile",JsonSerializer.SerializeToElement(new{x=11,y=10})):null;
        public bool OpeningSettled()=>Settled;
        public string OpeningLandingError()=>PlaceSupport?"fixture_landing_changed":null;
        public bool TargetAbsent()=>Absent;
        public object Target=>new{x=12,y=10};
    }
    private sealed class ClearanceRun(World world) : CompanionTask
    {
        internal int Starts;
        internal override string Name=>"excavate_tiles";
        internal override void Start(AiReadOnlyProtocol.Identity frame)=>Starts++;
        internal override Result Tick(AiReadOnlyProtocol.Identity frame)
        {
            world.Reach=true;
            return new("completed","excavation_run_verified",
                new MiningRunReceipt(2,new[]{new ExcavationProof(11,10,1,4,2),new ExcavationProof(11,11,1,8,2)},12,12,4,10),
                true,true);
        }
        internal override Result Stop(AiReadOnlyProtocol.Identity frame,string status,string reason)
            =>new(status,reason,new MiningRunReceipt(2,new[]{new ExcavationProof(11,10,1,4,2)},12,6,2,10),true);
    }
    private sealed class Native : AiAtomicActions.IExecutor
    {
        internal readonly List<string> Starts=new();
        internal int Polls,Cancels;
        internal bool Pending,ThrowStart,ThrowPoll,BadEffect,UnknownCancel,CompletedCancel;
        internal Action<string> OnPoll;
        public string Validate(string op,JsonElement args)=>null;
        public object Start(AiReadOnlyProtocol.Identity id,string op,JsonElement args,string actionId){Starts.Add(op);if(ThrowStart)throw new Exception("start");return op;}
        public Result Poll(AiReadOnlyProtocol.Identity id,object handle)
        {Polls++;if(ThrowPoll)throw new Exception("poll");OnPoll?.Invoke((string)handle);return new(Pending?"running":"completed","native_done",new{token="child_result"},!Pending,!BadEffect);}
        public Result Cancel(AiReadOnlyProtocol.Identity id,object handle){Cancels++;return new(UnknownCancel?"unknown":CompletedCancel?"completed":"cancelled","stopped",new{token="cancel_observation"},true,CompletedCancel);}
        public void Shutdown(){}
    }
    private sealed class Fixture
    {
        internal readonly World World=new();
        internal readonly Native Native=new();
        internal AiReadOnlyProtocol.Identity Frame=new(new object(),"world",1,254,null,0,0);
        internal readonly TaskSlot Root;
        internal Fixture(){Native.OnPoll=op=>{if(op=="navigate_to_landing_region"&&!Native.Pending)World.Reach=true;};Root=new(new MineTargetTask("root",World,Native),"root",null,Frame,100);}
        internal Result Tick(ulong tick){Frame=Frame with{GameUpdate=tick};return Root.Tick(Frame);}
    }
    public static void Run()
    {
        foreach(bool cancel in new[]{false,true})
        {
            var world=new World{PreferChanges=true,PlaceSupport=true};var native=new Native();
            var run=new ClearanceRun(world);world.Clearance=run;
            var frame=new AiReadOnlyProtocol.Identity(new object(),"world",1,254,null,0,0);
            var root=new TaskSlot(new AutonomousNavigationTask("batch",world,native),"batch",null,frame,100);
            for(ulong tick=1;tick<4;tick++)root.Tick(frame with{GameUpdate=tick});
            var result=cancel?root.Stop(frame with{GameUpdate=3},"cancelled","explicit_stop"):root.Tick(frame with{GameUpdate=4});
            if(!cancel)result=root.Tick(frame with{GameUpdate=5});
            var view=JsonSerializer.SerializeToElement(root.View());
            Check(result.Status==(cancel?"cancelled":"completed")&&run.Starts==1&&native.Starts.Count==0,
                "navigation uses one clearance child and preserves its lifecycle");
            Check(view.GetProperty("progress").GetProperty("dugTiles").GetInt32()==(cancel?1:2),
                "navigation counts only individually verified clearance effects, including cancellation");
        }
        {
            var world=new World{PendingPlanning=true};var native=new Native();
            var frame=new AiReadOnlyProtocol.Identity(new object(),"world",1,254,null,0,0);
            var root=new TaskSlot(new AutonomousNavigationTask("planning",world,native),"planning",null,frame,100);
            for(ulong tick=1;tick<8;tick++)Check(root.Tick(frame with{GameUpdate=tick}).Status=="running","planning budget is waiting, not no route");
            Check(native.Starts.Count==0,"pending candidate evaluation cannot dispatch any native action");
            root.Stop(frame with{GameUpdate=8},"cancelled","explicit_cancel");
            Check(world.PlanningReleases==1&&native.Starts.Count==0,"cancelling evaluation disposes pending planning without replay");
        }
        foreach(string failure in new[]{"unknown","failed"})
        {
            var world=new World{PlanningStop=new(failure,"comparison_refused",null,true),AllowOpening=true,PreferChanges=true};var native=new Native();
            var frame=new AiReadOnlyProtocol.Identity(new object(),"world",1,254,null,0,0);
            var root=new TaskSlot(new AutonomousNavigationTask("refusal",world,native),"refusal",null,frame,100);
            root.Tick(frame with{GameUpdate=1});var r=root.Tick(frame with{GameUpdate=2});
            Check(r.Status==failure&&r.InputsReleased&&native.Starts.Count==0,"unknown/resource comparison refusal never starts dependent edits");
        }
        foreach(bool absent in new[]{false,true})
        {
            var f=new Fixture();f.World.Reach=true;f.World.Absent=absent;f.Native.Pending=true;
            f.Tick(1);f.Tick(2);f.Tick(3);f.Native.CompletedCancel=true;
            var r=f.Root.Stop(f.Frame,"cancelled","explicit_cancel");
            Check(r.Status==(absent?"completed":"cancelled"),"root stop preserves completed mining only after target verification");
            Check(f.Native.Cancels==1,"completed-during-stop child is cleaned once");
        }
        foreach(string danger in new[]{"task_interrupt_nearby_hostile","task_interrupt_health_decreased","task_interrupt_lava_contact"})
        {
            var f=new Fixture();f.Native.Pending=true;f.Tick(1);f.Tick(2);f.Tick(3);
            f.World.Danger=danger;
            var stopped=f.Tick(4);
            Check(stopped.Status=="cancelled"&&stopped.InputsReleased&&stopped.Reason==danger,"game-tick hazard releases child: "+danger);
            Check(f.Native.Cancels==1&&f.Native.Starts.Count==1,"hazard never restarts child");
            var view=JsonSerializer.SerializeToElement(f.Root.View());
            Check(view.GetProperty("lastResult").GetProperty("status").GetString()=="cancelled"
                &&view.GetProperty("lastResult").GetProperty("inputsReleased").GetBoolean(),"hazard retains child cleanup evidence");
            f.World.Danger=null;f.Tick(5);
            Check(f.Native.Starts.Count==1,"cleared hazard does not automatically resume terminal task");
        }
        {
            var f=new Fixture();for(int i=0;i<20;i++)f.Tick(0);
            Check(f.World.Preparations==0&&f.Native.Starts.Count==0,"idle pulses cannot start a task");
            Check(f.Tick(1).Status=="running"&&f.World.Preparations==1,"start/precondition run once");
            for(int i=0;i<20;i++)f.Tick(1);
            Check(f.World.Preparations==1&&f.Native.Starts.Count==0,"duplicate game update cannot advance parent");
            for(ulong t=2;t<=6;t++){Check(f.Tick(t).Status=="running","parent cannot complete before mining effect");for(int i=0;i<5;i++)f.Tick(t);}
            Check(f.Native.Starts.Count==2&&f.Native.Starts[0]=="navigate_to_landing_region"&&f.Native.Starts[1]=="mine_tile","one start per child, navigation then mining");
            var r=f.Tick(7);Check(r.Status=="completed"&&r.EffectVerified&&r.InputsReleased,"root settles only verified effect and release");
            Check(JsonSerializer.Serialize(f.Root.View()).Contains("child_result"),"child results and history reach root state");
            f.Tick(8);Check(f.Native.Starts.Count==2&&f.World.Preparations==1,"terminal root cannot restart");
        }
        foreach(string cause in new[]{"cancel","timeout","death","identity","world","generation","slot","unknown_cancel"})
        {
            var f=new Fixture();f.Native.Pending=true;f.Tick(1);f.Tick(2);f.Tick(3);
            Result r;
            if(cause is "identity" or "world" or "generation" or "slot")
            {f.Frame=cause switch{"identity"=>f.Frame with{Actor=new object()},"world"=>f.Frame with{World="other"},"generation"=>f.Frame with{Generation=2},_=>f.Frame with{Slot=253}};r=f.Tick(4);}
            else if(cause=="timeout")r=f.Tick(100);
            else if(cause=="death"){f.Frame=f.Frame with{CanAct=false};r=f.Tick(4);}
            else {f.Native.UnknownCancel=cause=="unknown_cancel";r=f.Root.Stop(f.Frame,"cancelled","explicit");}
            Check(r.Status==(cause is "identity" or "world" or "generation" or "slot" or "unknown_cancel"?"unknown":cause=="timeout"?"timeout":"cancelled"),"stop status: "+cause);
            Check(f.Native.Cancels==(cause is "identity" or "world" or "generation" or "slot"?0:1),"cancel propagation without stale writes: "+cause);
            f.Root.Stop(f.Frame,"cancelled","again");Check(f.Native.Cancels==(cause is "identity" or "world" or "generation" or "slot"?0:1),"stop exactly once");
        }
        foreach(string fault in new[]{"prepare","start","poll","effect","absence"})
        {
            var f=new Fixture();f.World.ThrowPrepare=fault=="prepare";f.Native.ThrowStart=fault=="start";f.Native.ThrowPoll=fault=="poll";f.Native.BadEffect=fault=="effect";f.World.Absent=fault!="absence";
            Result r=null;for(ulong t=1;t<15;t++){r=f.Tick(t);if(r.Status!="running")break;}
            Check(r.Status=="unknown","ambiguous task failure stays unknown: "+fault);
            int count=f.Native.Starts.Count;f.Tick(20);Check(f.Native.Starts.Count==count,"no restart after uncertainty");
        }
        {
            var f=new Fixture();f.World.PrepareError="invalid_target";Check(f.Tick(1).Status=="failed"&&f.Native.Starts.Count==0&&f.World.Preparations==1,"precondition refusal has no native side effects");
        }
        {
            var f=new Fixture();
            for(ulong tick=1;tick<=4;tick++)f.Tick(tick);
            var published=f.Root.View();
            string snapshot=JsonSerializer.Serialize(published);
            for(ulong tick=5;tick<=7;tick++)f.Tick(tick);
            Check(JsonSerializer.Serialize(published)==snapshot,
                "published task history is detached from subsequent game-thread updates");
        }
        {
            var f=new Fixture();f.Tick(1);f.Tick(2);f.Tick(3);f.World.Error="target_changed";
            Check(f.Tick(4).Status=="failed"&&f.Native.Cancels==1&&f.Native.Starts.Count==1,"changed target stops active child and never starts mining");
        }
        {
            var f=new Fixture();f.World.NoApproach=f.World.AllowOpening=true;
            f.Tick(1);f.Tick(2);f.Tick(3);f.Tick(4);
            f.World.Settled=false;f.World.Reach=true;
            for(ulong tick=5;tick<10;tick++)Check(f.Tick(tick).Status=="running","fall waits for native settlement");
            Check(f.Native.Starts.Count==1,"settling never starts another child");
            f.World.Settled=true;f.Tick(10);f.Tick(11);
            Check(f.Tick(12).Status=="completed"&&f.Native.Starts.Count==2,"settled opening leads to one final mine");
        }
        {
            var f=new Fixture();f.World.NoApproach=f.World.AllowOpening=true;
            Result result=null;
            for(ulong tick=1;tick<=100;tick++)
            {result=f.Tick(tick);if(result.Status!="running")break;}
            Check(result?.Status=="timeout"&&result.Reason=="task_game_update_deadline"&&f.Native.Starts.Count>AutonomousNavigationTask.LocalCutTileLimit,
                "verified excavation continues beyond old count limit until root deadline");
        }
        foreach(bool uncertain in new[]{false,true})
        {
            var world=new World{NoApproach=true,AllowOpening=true,PlaceSupport=true};
            var native=new Native{BadEffect=uncertain};
            native.OnPoll=op=>
            {
                if(op=="place_tile")world.NoApproach=false;
                if(op=="navigate_to_landing_region")world.Reach=true;
            };
            var frame=new AiReadOnlyProtocol.Identity(new object(),"world",1,254,null,0,0);
            var root=new TaskSlot(new AutonomousNavigationTask("nav",world,native),"nav",null,frame,100);
            Result result=null;
            for(ulong tick=1;tick<=15;tick++)result=root.Tick(frame with{GameUpdate=tick});
            Check(native.Starts[0]=="place_tile","navigation selects placement without model assistance");
            if(uncertain)
                Check(result.Status=="unknown"&&native.Starts.Count==1,"unknown placement stops without replay");
            else
                Check(result.Status=="completed"&&native.Starts.Count==2&&native.Starts[1]=="navigate_to_landing_region",
                    "confirmed placement and changed landing trigger one newly observed movement");
        }
        foreach(bool placement in new[]{false,true})
        {
            var world=new World{NoApproach=true,AllowOpening=true,PlaceSupport=placement};
            var native=new Native();
            int effects=0;
            native.OnPoll=op=>{effects++;world.Reach=effects==25;};
            var frame=new AiReadOnlyProtocol.Identity(new object(),"world",1,254,null,0,0);
            var root=new TaskSlot(new AutonomousNavigationTask("many",world,native),"many",null,frame,200);
            Result result=null;
            for(ulong tick=1;tick<200;tick++)
            {
                result=root.Tick(frame with{GameUpdate=tick});
                if(result.Status!="running")break;
            }
            Check(result?.Status=="completed"&&effects==25,
                "one root continues beyond cumulative terrain action cap: "+placement);
            var view=JsonSerializer.SerializeToElement(root.View()).GetProperty("progress");
            Check(view.GetProperty("settledChildren").GetInt32()==25
                &&view.GetProperty("childrenTruncated").GetBoolean()
                &&view.GetProperty("children").GetArrayLength()==16
                &&view.GetProperty("verifiedTerrainActions").GetInt32()==25,
                "long journeys retain exact totals and bounded, explicitly truncated history");
        }
        Console.WriteLine($"Task runtime: {checks} checks passed; native executor/world are substitutes.");
    }
}
