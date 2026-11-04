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
        internal bool ThrowStart, ThrowCancel, CompleteOnCancel, UnknownOnCancel, KeepInputHeld;
        internal string CancelStatus;
        internal AiAtomicActions.NativeResult Result=new("running","native_pending",null,false);
        public string Validate(string op,JsonElement args)=>args.TryGetProperty("bad",out _)?"invalid_arguments":null;
        public object Start(AiReadOnlyProtocol.Identity id,string op,JsonElement args,string actionId){Starts++;if(ThrowStart)throw new Exception("partial native start");return new object();}
        public AiAtomicActions.NativeResult Poll(AiReadOnlyProtocol.Identity id,object handle)=>Result;
        public AiAtomicActions.NativeResult Cancel(AiReadOnlyProtocol.Identity id,object handle){Cancels++;if(ThrowCancel)throw new Exception("native cleanup uncertain");return Result with{Status=CancelStatus??(UnknownOnCancel?"unknown":CompleteOnCancel?"completed":"cancelled"),InputsReleased=!KeepInputHeld};}
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
        internal string Start(string operation="mine_tile",CancellationToken token=default){string id=Guid.NewGuid().ToString("N");Send("start",id,operation,token,
            operation=="move_input"?(object)new{horizontal="right",vertical="none",jumpHoldUpdates=8,maxUpdates=60}:operation=="navigate_to_landing_region"?(object)new{minPlayerLeftX=100,maxPlayerLeftX=140,surfaceY=200}:operation=="craft_recipe"?new{recipeId=12,recipeToken=new string('a',64),crafts=3}:null);Tick();Tick();return id;}
    }
    public static void Run()
    {
        foreach (string trigger in new[]{"cancel","timeout"})
        foreach (string terminal in new[]{"completed","failed","timeout","cancelled","unknown","running"})
        foreach (bool released in new[]{true,false})
        {
            var f=new Fixture(); string id=f.Start();
            f.Native.CancelStatus=terminal; f.Native.KeepInputHeld=!released;
            f.Native.Result=new(terminal,"observed_native_terminal",new{actual=7},released,true);
            if(trigger=="cancel") f.Send("cancel",id); else {f.Time=30001;f.Tick();}
            var a=f.Action(id);
            string expected=!released||terminal is "unknown" or "running"?"unknown":terminal;
            Check(a.GetProperty("status").GetString()==expected && f.Native.Starts==1 && f.Native.Cancels==1,
                "actual native terminal wins "+trigger+" / "+terminal+" / released="+released);
            Check(a.GetProperty("stopRequest").GetProperty("status").GetString()==(trigger=="cancel"?"cancelled":"timeout"),
                "stop trigger retained separately from native terminal");
        }
        {
            var f=new Fixture();string id=f.Start("move_input");var a=f.Action(id);
            Check(a.GetProperty("requestedInputs").GetProperty("maxUpdates").GetInt32()==60
                &&a.GetProperty("durationMs").GetInt32()==30000,"direct-input immutable request and watchdog budget");
            Check(Array.IndexOf(f.Actions.Capabilities,"server_ai_direct_input_v1")>=0,"direct-input capability advertised");
        }
        foreach(string op in new[]{"transfer_inventory_item","swap_inventory_slots","hold_inventory_item"})
        {
            var f=new Fixture();
            string id=Guid.NewGuid().ToString("N");
            var args=new{sourceSlot=10,destinationSlot=0,count=3,sourceToken=new string('a',64),destinationToken=new string('b',64)};
            Check(f.Send("start",id,op,args:args).GetProperty("ok").GetBoolean(),"inventory lifecycle admission: "+op);
            Check(f.Action(id).GetProperty("targetInventory").GetProperty("sourceSlot").GetInt32()==10,"immutable inventory request receipt");
            f.Tick();f.Tick();f.Native.UnknownOnCancel=true;
            var stopped=f.Send("cancel",id).GetProperty("data").GetProperty("action");
            Check(stopped.GetProperty("status").GetString()=="unknown"&&f.Native.Starts==1&&f.Native.Cancels==1,"uncertain inventory cancel stays unknown, no replay");
        }
        {
            var f=new Fixture();var disconnected=new CancellationTokenSource();string id=Guid.NewGuid().ToString("N");
            f.Send("start",id,"transfer_inventory_item",token:disconnected.Token);f.Tick();f.Tick();
            f.Native.UnknownOnCancel=true;disconnected.Cancel();f.Tick();
            Check(f.Action(id).GetProperty("status").GetString()=="unknown"&&f.Native.Cancels==1,"disconnect after inventory start preserves unknown");
        }
        {
            var f=new Fixture();var disconnected=new CancellationTokenSource();string id=Guid.NewGuid().ToString("N");
            f.Send("start",id,"transfer_inventory_item",token:disconnected.Token);disconnected.Cancel();f.Tick();
            Check(f.Action(id).GetProperty("status").GetString()=="cancelled"&&f.Native.Starts==0,"disconnect before execution makes no inventory call");
        }
        foreach(string reason in new[]{"navigation_stalled","no safe local route: blocked","unsupported navigation state: wet"})
            Check(AiActionReasons.NavigationRecoveryKind(reason)!=null,"known native navigation refusal can be classified: "+reason);
        foreach(string reason in new[]{"navigation_trip_update_budget","navigation_trip_search_budget","navigation_trip_repeated_state","navigation_recovery_limit","navigation_recovery_blocked: fixture","navigation_search_expansion_budget","navigation_search_timeout","navigation_action_limit","native_navigation_exception","life decreased","navigation edge landing mismatch","local prediction exceeded 50ms budget"})
            Check(AiActionReasons.NavigationRecoveryKind(reason)==null,"uncertain/budget native navigation stop is not recoverable: "+reason);
        foreach(string reason in new[]{"navigation_trip_update_budget","navigation_trip_search_budget","navigation_trip_repeated_state","navigation_stalled","navigation_action_limit","navigation_search_timeout","navigation_search_expansion_budget"})
            Check(AiActionReasons.Navigation(reason)==reason,"navigation diagnostic survives protocol reason mapping: "+reason);
        Check(AiActionReasons.Navigation("no safe local route: blocked")=="native_navigation_failed","ordinary no-route mapping remains compatible");
        Check(AiActionReasons.Mining("completed","None")=="native_tile_destroyed","mining success translates outcome rather than failure enum");
        foreach(var pair in new[]{("InvalidTarget","invalid_target"),("InvalidTool","invalid_tool"),("TargetChanged","target_changed"),
            ("NativeRefused","native_use_refused"),("NoPickPower","native_tool_damage_refused"),("NativeException","native_mining_exception"),
            ("ExplicitStop","explicit_stop"),("Deadline","native_mining_deadline")})
            Check(AiActionReasons.Mining("failed",pair.Item1)==pair.Item2,"native reason code mapping "+pair.Item1);
        Check(AiActionReasons.Mining("failed","None")=="native_mining_unconfirmed"
            &&AiActionReasons.Mining("unknown","None")=="native_mining_state_unknown","absence of failure code cannot imply success");
        foreach(var result in new[]{
            new AiAtomicActions.NativeResult("completed","native_tile_destroyed",null,true,true),
            new AiAtomicActions.NativeResult("failed","native_tool_damage_refused",null,true,ReasonDetail:"保护判定后的伤害为零：目标 (12,34)。"),
            new AiAtomicActions.NativeResult("cancelled","explicit_stop",null,true),
            new AiAtomicActions.NativeResult("unknown","native_context_changed",null,false)})
        {
            var f=new Fixture();string id=f.Start();f.Native.Result=result;f.Tick();var a=f.Action(id);
            Check(a.GetProperty("status").GetString()==result.Status
                &&a.GetProperty("reason").GetString()==(result.InputsReleased?result.Reason:"native_inputs_not_released")
                &&a.GetProperty("reasonDetail").GetString()==(result.InputsReleased?result.ReasonDetail:result.Reason),"JSON preserves stable reason and diagnostic detail for "+result.Status);
        }
        {
            var f=new Fixture();string id=f.Start();f.Native.Result=new("completed","native_tile_destroyed",null,true,false);f.Tick();
            Check(f.Action(id).GetProperty("status").GetString()=="unknown"&&f.Action(id).GetProperty("reason").GetString()=="native_effect_unconfirmed"
                &&f.Action(id).GetProperty("reasonDetail").GetString()=="native_tile_destroyed",
                "unverified completion cannot retain a success reason");
        }
        {
            var f=new Fixture();string id=f.Start();f.Native.CompleteOnCancel=true;
            f.Native.Result=new("completed","native_tile_destroyed",null,true,true);f.Send("cancel",id);
            Check(f.Action(id).GetProperty("status").GetString()=="completed"&&f.Action(id).GetProperty("reason").GetString()=="native_tile_destroyed",
                "verified completion winning cancellation race retains success reason");
        }
        {
            var f=new Fixture();string id=Guid.NewGuid().ToString("N");
            var goal=new{minPlayerLeftX=100,maxPlayerLeftX=140,surfaceY=200};
            var a=f.Send("start",id,"navigate_to_landing_region",args:goal).GetProperty("data").GetProperty("action");
            Check(a.GetProperty("durationMs").GetInt32()==30000&&a.GetProperty("targetRegion").GetRawText()==JsonSerializer.Serialize(goal),
                "landing region receipt fixes exact target and 30-second deadline before native start");
            Check(Array.IndexOf(f.Actions.Capabilities,"landing_region_navigation_v1")>=0,
                "landing region capability is advertised with native executor");
            f.Actions.Clear();
            var precise=f.Send("start",Guid.NewGuid().ToString("N"),"navigate_to_landing_region",
                args:new{minPlayerLeftX=33333.3333,maxPlayerLeftX=33340.1234,surfaceY=200.1234}).GetProperty("data").GetProperty("action");
            Check(precise.GetProperty("targetRegion").GetProperty("minPlayerLeftX").GetDouble()==
                JsonSerializer.SerializeToElement((float)33333.3333).GetDouble(),"receipt canonicalizes coordinates to the native float region before execution");
        }
        {
            var f=new Fixture();string id=f.Start("place_object");var a=f.Action(id);
            Check(a.GetProperty("targetTile").GetProperty("x").GetInt32()==100&&a.GetProperty("durationMs").GetInt32()==10000,
                "placement receipt fixes tile coordinates and ten-second deadline");
            Check(Array.IndexOf(f.Actions.Capabilities,"server_ai_place_object_workbench_v1")>=0,"placement capability is explicit");
        }
        {
            var f=new Fixture();string id=f.Start("craft_recipe");var a=f.Action(id);
            Check(a.GetProperty("targetRecipe").GetProperty("recipeId").GetInt32()==12&&a.GetProperty("targetRecipe").GetProperty("crafts").GetInt32()==3,"craft receipt fixes recipe identity and execution count");
            Check(Array.IndexOf(f.Actions.Capabilities,"server_ai_crafting_v1")>=0,"craft capability is explicit");
        }
        {
            var f=new Fixture();string id=f.Start("mine_target");var a=f.Action(id);
            Check(a.GetProperty("durationMs").GetInt32()==55000&&a.GetProperty("targetTask").GetProperty("x").GetInt32()==100,"mine task has immutable target and bounded root deadline");
            Check(Array.IndexOf(f.Actions.Capabilities,"server_ai_tasks_v1")>=0,"task capability independent of atomic actions");
            f.Time=55001;f.Tick();Check(f.Action(id).GetProperty("status").GetString()=="cancelled"&&f.Action(id).GetProperty("stopRequest").GetProperty("status").GetString()=="timeout"&&f.Native.Cancels==1,"root deadline preserves executor cancellation and records timeout trigger");
        }
        foreach(string op in new[]{"move_input","navigate_to_x","navigate_to_landing_region","use_item","mine_tile","place_tile","place_object","craft_recipe","mine_target"})
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
        foreach(string cause in new[]{"disconnect","death","cancel","timeout","forget"})
        {
            var f=new Fixture();using var disconnected=new CancellationTokenSource();string id=f.Start("mine_target",token:disconnected.Token);
            f.Native.UnknownOnCancel=true;f.Native.Result=new("unknown","task_child_unconfirmed",null,true);
            if(cause=="disconnect")disconnected.Cancel();if(cause=="death")f.Current=f.Current with{CanAct=false};
            if(cause=="cancel")f.Send("cancel",id);if(cause=="timeout")f.Time=55001;if(cause=="forget")f.Actions.Forget(f.Owner);
            f.Tick();var a=f.Action(id);
            Check(a.GetProperty("status").GetString()=="unknown"&&f.Native.Cancels==1,"root task "+cause+" preserves uncertain child outcome");
        }
        {
            var f=new Fixture();string id=f.Start("mine_target");f.Native.Result=new("failed","actor_busy",new{mutationAttempted=false},true,InputsAcquired:false);f.Tick();
            Check(f.Action(id).GetProperty("task").GetProperty("status").GetString()=="failed","native admission refusal remains a structured task without requiring child evidence");
        }
        foreach(string operation in new[]{"place_object","move_input"})
        foreach(string cause in new[]{"cancel","death","timeout","disconnect","forget"})foreach(bool released in new[]{true,false})
        {
            var f=new Fixture();using var disconnect=new CancellationTokenSource();string id=f.Start(operation,token:disconnect.Token);
            f.Native.UnknownOnCancel=true;f.Native.KeepInputHeld=!released;
            string nativeReason=operation=="place_object"?"object_target_changed":"direct_input_observation_unconfirmed";
            f.Native.Result=new("unknown",nativeReason,operation=="place_object"?(object)new{placedByActor=true}:new{observation="unavailable"},
                released,ReasonDetail:"State observation failed after cleanup.");
            if(cause=="cancel")f.Send("cancel",id);
            if(cause=="death")f.Current=f.Current with{CanAct=false};
            if(cause=="timeout")f.Time=30001;
            if(cause=="disconnect")disconnect.Cancel();
            if(cause=="forget")f.Actions.Forget(f.Owner);
            f.Tick();var action=f.Action(id);
            Check(action.GetProperty("status").GetString()=="unknown",operation+" "+cause+" preserves uncertain side effects");
            Check(action.GetProperty("endEvidenceStatus").GetString()==(released?"known":"unknown")
                &&(released?action.GetProperty("endEvidence").GetProperty("inputsReleased").GetBoolean():action.GetProperty("endEvidence").ValueKind==JsonValueKind.Null),operation+" "+cause+" reports actual input release");
            Check(action.GetProperty("reason").GetString()==(released?nativeReason:"native_inputs_not_released")
                &&action.GetProperty("reasonDetail").GetString().Contains("State observation failed"),operation+" "+cause+" retains native diagnostic uncertainty");
            Check(!released||!action.GetProperty("endEvidence").GetProperty("effectVerified").GetBoolean(),operation+" "+cause+" release does not verify effect");
            Check(f.Send("start",id,operation).GetProperty("error").GetString()=="duplicate_action",operation+" "+cause+" cannot restart unknown action ID");
            f.Send("cancel",id);f.Tick();
            Check(f.Native.Starts==1&&f.Native.Cancels==1,operation+" "+cause+" never replays start or cleanup after unknown");
        }
        foreach(bool released in new[]{true,false})
        {
            var f=new Fixture();string id=f.Start();f.Native.Result=new("completed","no_effect",null,released,!released);f.Tick();
            Check(f.Action(id).GetProperty("status").GetString()=="unknown","animation/uncertain effect cannot prove completion");
        }
        foreach(string operation in new[]{"mine_tile","move_input"})
        foreach(string cause in new[]{"cancel","disconnect","death","timeout","forget"})
        {
            var f=new Fixture();using var disconnect=new CancellationTokenSource();string id=f.Start(operation,token:disconnect.Token);
            if(cause=="cancel")f.Send("cancel",id);
            if(cause=="disconnect")disconnect.Cancel();
            if(cause=="death")f.Current=f.Current with{CanAct=false};
            if(cause=="timeout")f.Time=30001;
            if(cause=="forget")f.Actions.Forget(f.Owner);
            f.Tick();var a=f.Action(id);
            Check(f.Native.Cancels==1&&a.GetProperty("endEvidence").GetProperty("inputsReleased").GetBoolean()
                &&a.GetProperty("status").GetString()=="cancelled",cause+" retires exactly the acquired native handle and retains native terminal");
        }
        foreach(string operation in new[]{"mine_tile","move_input"})
        foreach(string cause in new[]{"object","world","generation","slot"})
        {
            var f=new Fixture();string id=f.Start(operation);f.Current=cause switch{
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
