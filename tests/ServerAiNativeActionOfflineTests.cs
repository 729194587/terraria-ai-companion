using System;
using System.Text.Json;
using System.Threading;
using CompanionBridge.ServerIpc;

public static class ServerAiNativeActionOfflineTests
{
    private static int checks;
    private static void Check(bool ok,string text){if(!ok)throw new Exception(text);checks++;Console.WriteLine("PASS AI native-adapter fixture: "+text);}
    private sealed class Executor : AiAtomicActions.IExecutor
    {
        internal int Starts,Cancels,Shutdowns;
        internal bool ThrowStart, ThrowCancel;
        internal AiAtomicActions.NativeResult Result=new("running","native_pending",null,false);
        public string Validate(string op,JsonElement args)=>args.TryGetProperty("bad",out _)?"invalid_arguments":null;
        public object Start(AiReadOnlyProtocol.Identity id,string op,JsonElement args,string actionId){Starts++;if(ThrowStart)throw new Exception("partial native start");return new object();}
        public AiAtomicActions.NativeResult Poll(AiReadOnlyProtocol.Identity id,object handle)=>Result;
        public AiAtomicActions.NativeResult Cancel(AiReadOnlyProtocol.Identity id,object handle){Cancels++;if(ThrowCancel)throw new Exception("native cleanup uncertain");return Result with{Status="cancelled",InputsReleased=true};}
        public void Shutdown(){Shutdowns++;}
    }
    private sealed class Fixture
    {
        internal readonly Executor Native=new();
        internal readonly AiAtomicActions Actions;
        internal readonly AiReadOnlyProtocol Protocol;
        internal readonly Guid Owner=Guid.NewGuid();
        internal AiReadOnlyProtocol.Identity Current=new(new object(),"world",1,254,new{},0,10);
        internal JsonElement Binding;
        internal long Time;
        internal Fixture(){Actions=new(Native,()=>Time);Protocol=new(Actions);Binding=Send("bind").GetProperty("binding");}
        internal JsonElement Send(string op,string id=null,string operation="mine_tile",CancellationToken token=default,object args=null)
        {
            object request=op=="bind"?new{v=1,id="r",op} : op=="start"?new{v=1,id="r",op,binding=Binding,actionId=id,operation,
                arguments=args??new{x=100,y=100},expiresAtUnixMs=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+1500}:new{v=1,id="r",op,binding=Binding,actionId=id};
            return JsonDocument.Parse(Protocol.Handle(Owner,JsonSerializer.Serialize(request),()=>Current,token)).RootElement.Clone();
        }
        internal void Tick()=>Actions.Tick(()=>Current,(_,_)=>throw new Exception("long actions may not use selection adapter"));
        internal JsonElement Action(string id)=>Send("action",id).GetProperty("data").GetProperty("action");
        internal string Start(string operation="mine_tile",CancellationToken token=default){string id=Guid.NewGuid().ToString("N");Send("start",id,operation,token);Tick();Tick();return id;}
    }
    public static void Run()
    {
        foreach(string op in new[]{"navigate_to_x","use_item","mine_tile"})
        {
            var f=new Fixture();string id=f.Start(op);
            Check(f.Native.Starts==1&&f.Action(id).GetProperty("status").GetString()=="running","accepted/running precede "+op+" native completion");
            f.Tick();Check(f.Native.Starts==1,"polling never repeats native start");
            f.Native.Result=new("completed","native_observed",new{proof="fixture"},true,true);f.Tick();
            var a=f.Action(id);var end=a.GetProperty("endEvidence");
            Check(a.GetProperty("status").GetString()=="completed"&&end.GetProperty("inputsReleased").GetBoolean()
                &&end.GetProperty("operation").GetString()==op&&end.GetProperty("actorGeneration").GetInt32()==1,"verified "+op+" has bound game-thread release evidence");
            Check(f.Send("start",id,op).GetProperty("error").GetString()=="duplicate_action","long action duplicate ID rejected");
        }
        foreach(bool released in new[]{true,false})
        {
            var f=new Fixture();string id=f.Start();f.Native.Result=new("completed","no_effect",null,released,!released);f.Tick();
            Check(f.Action(id).GetProperty("status").GetString()=="unknown","animation/uncertain effect cannot prove completion");
        }
        foreach(string cause in new[]{"cancel","disconnect","death","timeout","forget"})
        {
            var f=new Fixture();using var disconnect=new CancellationTokenSource();string id=f.Start(token:disconnect.Token);
            if(cause=="cancel")f.Send("cancel",id);
            if(cause=="disconnect")disconnect.Cancel();
            if(cause=="death")f.Current=f.Current with{CanAct=false};
            if(cause=="timeout")f.Time=10001;
            if(cause=="forget")f.Actions.Forget(f.Owner);
            f.Tick();var a=f.Action(id);
            Check(f.Native.Cancels==1&&a.GetProperty("endEvidence").GetProperty("inputsReleased").GetBoolean()
                &&a.GetProperty("status").GetString()==(cause=="timeout"?"timeout":"cancelled"),cause+" retires exactly the acquired native handle");
        }
        foreach(string cause in new[]{"object","world","generation","slot"})
        {
            var f=new Fixture();string id=f.Start();f.Current=cause switch{
                "object"=>f.Current with{Actor=new object()},"world"=>f.Current with{World="new"},"generation"=>f.Current with{Generation=2},_=>f.Current with{Slot=253}};
            f.Tick();Check(f.Native.Cancels==0&&f.Send("action",id).GetProperty("error").GetString()=="stale_binding","changed "+cause+" never writes through stale handle");
        }
        {
            var f=new Fixture();string id=f.Start();Check(f.Send("start",Guid.NewGuid().ToString("N")).GetProperty("error").GetString()=="action_busy","movement/use/mining share global mutual exclusion");
            f.Actions.Clear();Check(f.Native.Shutdowns==1,"world unload delegates native retirement");
        }
        {
            var f=new Fixture();Check(f.Send("start",Guid.NewGuid().ToString("N"),args:new{bad=true}).GetProperty("error").GetString()=="invalid_arguments"&&f.Native.Starts==0,"native argument rejection precedes start");
        }
        {
            var f=new Fixture();f.Native.ThrowStart=true;string id=f.Start();f.Tick();
            Check(f.Native.Starts==1&&f.Action(id).GetProperty("status").GetString()=="unknown"
                &&f.Action(id).GetProperty("endEvidenceStatus").GetString()=="unknown","ambiguous native start is never repeated or claimed released");
        }
        {
            var f=new Fixture();string id=f.Start();f.Native.ThrowCancel=true;f.Send("cancel",id);f.Tick();
            Check(f.Native.Cancels==1&&f.Action(id).GetProperty("status").GetString()=="unknown"
                &&f.Action(id).GetProperty("endEvidenceStatus").GetString()=="unknown","cleanup exception preserves unknown evidence without retry");
        }
        Console.WriteLine($"AI native adapter offline: {checks} checks; executor is a substitute, not Terraria.");
    }
}
