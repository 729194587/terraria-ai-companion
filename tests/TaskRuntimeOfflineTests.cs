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
        internal bool Reach,Absent=true,ThrowPrepare;
        internal string Error,PrepareError;
        internal int Selections,Observations;
        public string Prepare(){if(ThrowPrepare)throw new Exception("preflight");return PrepareError;}
        public void SelectTool(){Selections++;}
        public string Revalidate(){Observations++;return Error;}
        public bool CanMine()=>Reach;
        public JsonElement? FindApproach()=>JsonSerializer.SerializeToElement(new{minPlayerLeftX=100,maxPlayerLeftX=108,surfaceY=200});
        public JsonElement MineArguments()=>JsonSerializer.SerializeToElement(new{x=12,y=10});
        public bool TargetAbsent()=>Absent;
        public object Target=>new{x=12,y=10};
    }
    private sealed class Native : AiAtomicActions.IExecutor
    {
        internal readonly List<string> Starts=new();
        internal int Polls,Cancels;
        internal bool Pending,ThrowStart,ThrowPoll,BadEffect,UnknownCancel;
        internal Action<string> OnPoll;
        public string Validate(string op,JsonElement args)=>null;
        public object Start(AiReadOnlyProtocol.Identity id,string op,JsonElement args,string actionId){Starts.Add(op);if(ThrowStart)throw new Exception("start");return op;}
        public Result Poll(AiReadOnlyProtocol.Identity id,object handle)
        {Polls++;if(ThrowPoll)throw new Exception("poll");OnPoll?.Invoke((string)handle);return new(Pending?"running":"completed","native_done",new{token="child_result"},!Pending,!BadEffect);}
        public Result Cancel(AiReadOnlyProtocol.Identity id,object handle){Cancels++;return new(UnknownCancel?"unknown":"cancelled","stopped",null,true);}
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
        {
            var f=new Fixture();for(int i=0;i<20;i++)f.Tick(0);
            Check(f.World.Selections==0&&f.Native.Starts.Count==0,"idle pulses cannot start a task");
            Check(f.Tick(1).Status=="running"&&f.World.Selections==1,"start/precondition run once");
            for(int i=0;i<20;i++)f.Tick(1);
            Check(f.World.Selections==1&&f.Native.Starts.Count==0,"duplicate game update cannot advance parent");
            for(ulong t=2;t<=6;t++){Check(f.Tick(t).Status=="running","parent cannot complete before mining effect");for(int i=0;i<5;i++)f.Tick(t);}
            Check(f.Native.Starts.Count==2&&f.Native.Starts[0]=="navigate_to_landing_region"&&f.Native.Starts[1]=="mine_tile","one start per child, navigation then mining");
            var r=f.Tick(7);Check(r.Status=="completed"&&r.EffectVerified&&r.InputsReleased,"root settles only verified effect and release");
            Check(JsonSerializer.Serialize(f.Root.View()).Contains("child_result"),"child results and history reach root state");
            f.Tick(8);Check(f.Native.Starts.Count==2&&f.World.Selections==1,"terminal root cannot restart");
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
            var f=new Fixture();f.World.PrepareError="invalid_target";Check(f.Tick(1).Status=="failed"&&f.Native.Starts.Count==0&&f.World.Selections==0,"precondition refusal has no native side effects");
        }
        {
            var f=new Fixture();f.Tick(1);f.Tick(2);f.Tick(3);f.World.Error="target_changed";
            Check(f.Tick(4).Status=="failed"&&f.Native.Cancels==1&&f.Native.Starts.Count==1,"changed target stops active child and never starts mining");
        }
        Console.WriteLine($"Task runtime: {checks} checks passed; native executor/world are substitutes.");
    }
}
