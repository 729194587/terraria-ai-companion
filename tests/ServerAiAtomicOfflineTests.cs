using System;
using System.Text.Json;
using System.Threading;
using CompanionBridge.ServerIpc;

public static class ServerAiAtomicOfflineTests
{
    private static int checks;
    private static void Check(bool ok,string text){if(!ok)throw new Exception(text);checks++;Console.WriteLine("PASS AI atomic fixture: "+text);}
    private sealed class Fixture
    {
        internal readonly AiAtomicActions Actions = new();
        internal readonly AiReadOnlyProtocol Protocol;
        internal readonly Guid Owner = Guid.NewGuid();
        internal AiReadOnlyProtocol.Identity Current = new(new object(),"world",1,254,new {active=true},0,10);
        internal JsonElement Binding;
        internal int Writes;
        internal Fixture(){Protocol=new(Actions);Binding=Send("bind").GetProperty("binding");}
        internal JsonElement Send(string op,string id=null,int key=2,long? expires=null,CancellationToken token=default,string operation="select_hotbar_slot")
        {
            object request=op=="bind" ? new {v=1,id="r",op} : op=="start"
                ? new {v=1,id="r",op,binding=Binding,actionId=id,operation,arguments=new {slot=key},expiresAtUnixMs=expires??DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+1000}
                : new {v=1,id="r",op,binding=Binding,actionId=id};
            return JsonDocument.Parse(Protocol.Handle(Owner,JsonSerializer.Serialize(request),()=>Current,token)).RootElement.Clone();
        }
        internal void Tick(Action effect=null)=>Actions.Tick(()=>Current,(expected,slot)=>{if(!ReferenceEquals(expected.Actor,Current.Actor))throw new Exception("wrong actor");Writes++;Current=Current with{SelectedSlot=slot};effect?.Invoke();});
        internal JsonElement Action(string id)=>Send("action",id).GetProperty("data").GetProperty("action");
        internal static string Id()=>Guid.NewGuid().ToString("N");
    }
    public static void Run()
    {
        foreach(int key in new[]{0,1,9})
        {
            var f=new Fixture();string id=Fixture.Id();var start=f.Send("start",id,key).GetProperty("data").GetProperty("action");
            Check(start.GetProperty("executionState").GetString()=="accepted"&&f.Writes==0,"acceptance never mutates native state");
            f.Tick();Check(f.Action(id).GetProperty("executionState").GetString()=="running"&&f.Writes==0,"running stage precedes mutation");
            f.Tick();Check(f.Action(id).GetProperty("status").GetString()=="running"&&f.Writes==1,"native call alone is not completion");
            f.Tick();var done=f.Action(id);var end=done.GetProperty("endEvidence");
            Check(done.GetProperty("status").GetString()=="completed"&&end.GetProperty("selectionVerified").GetBoolean()&&end.GetProperty("selectedSlot").GetInt32()==(key+9)%10,"next owner-thread pump verifies key mapping and selection");
            Check(!end.GetProperty("inputsAcquired").GetBoolean()&&end.GetProperty("actorGeneration").GetInt64()==1&&end.GetProperty("worldSessionId").GetString()=="world","terminal evidence binds generation/world and no input ownership");
            Check(f.Send("start",id,key).GetProperty("error").GetString()=="duplicate_action"&&f.Writes==1,"duplicate ID never repeats mutation");
        }
        {
            var f=new Fixture();string id=Fixture.Id();f.Send("start",id);var cancelled=f.Send("cancel",id).GetProperty("data").GetProperty("action");f.Tick();
            Check(cancelled.GetProperty("status").GetString()=="cancelled"&&f.Writes==0,"cancel before mutation returns synchronous safe terminal receipt");
            Check(f.Send("action",Fixture.Id()).GetProperty("error").GetString()=="unknown_action","unknown actions rejected");
        }
        {
            var f=new Fixture();string id=Fixture.Id();using var disconnect=new CancellationTokenSource();f.Send("start",id,token:disconnect.Token);disconnect.Cancel();f.Tick();
            Check(f.Writes==0&&f.Action(id).GetProperty("status").GetString()=="cancelled","disconnect cancels accepted operation without native writes");
        }
        {
            var f=new Fixture();string id=Fixture.Id();f.Send("start",id);Guid other=Guid.NewGuid();
            f.Protocol.Handle(other,"{\"v\":1,\"id\":\"bind\",\"op\":\"bind\"}",()=>f.Current);
            string foreign=JsonSerializer.Serialize(new{v=1,id="r",op="cancel",binding=f.Binding,actionId=id});
            var reply=JsonDocument.Parse(f.Protocol.Handle(other,foreign,()=>f.Current)).RootElement;
            Check(reply.GetProperty("error").GetString()=="unknown_action"&&f.Action(id).GetProperty("status").GetString()=="running","another bound connection cannot cancel or inspect owned action");
            f.Protocol.Forget(f.Owner);f.Tick();Check(f.Writes==0,"connection retirement cancels before native mutation");
        }
        {
            var f=new Fixture();string id=Fixture.Id();f.Send("start",id);f.Current=f.Current with{CanAct=false};f.Tick();
            Check(f.Writes==0&&f.Action(id).GetProperty("status").GetString()=="cancelled","native death cancels pending selection without input writes");
            Check(f.Send("start",Fixture.Id()).GetProperty("error").GetString()=="actor_not_alive","dead AI cannot start another selection");
        }
        foreach(string change in new[]{"object","world","generation","slot"})
        {
            var f=new Fixture();string id=Fixture.Id();f.Send("start",id);f.Tick();
            f.Current=change switch {"object"=>f.Current with{Actor=new object()},"world"=>f.Current with{World="new-world"},"generation"=>f.Current with{Generation=2},_=>f.Current with{Slot=253}};
            f.Tick();Check(f.Writes==0&&f.Send("action",id).GetProperty("error").GetString()=="stale_binding","stale "+change+" refuses execution and old receipts");
        }
        {
            var f=new Fixture();Check(f.Send("start",Fixture.Id(),expires:DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()-1).GetProperty("error").GetString()=="request_expired","expired request rejected before admission");
            string id=Fixture.Id();f.Send("start",id,expires:DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+80);Thread.Sleep(100);f.Tick();
            Check(f.Writes==0&&f.Action(id).GetProperty("status").GetString()=="timeout","admitted deadline expires with safe no-input evidence");
        }
        {
            var f=new Fixture();string id=Fixture.Id();f.Send("start",id,expires:DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+80);f.Tick();f.Tick();Thread.Sleep(100);f.Tick();
            var done=f.Action(id);Check(f.Writes==1&&done.GetProperty("status").GetString()=="timeout"&&done.GetProperty("evidence").GetProperty("selectionApplied").GetBoolean(),"deadline after mutation preserves partial-effect evidence without success or rollback");
        }
        {
            var f=new Fixture();string id=Fixture.Id();f.Send("start",id);f.Tick();f.Tick(()=>throw new Exception("partial native sync failure"));f.Tick();
            Check(f.Writes==1&&f.Action(id).GetProperty("status").GetString()=="unknown"&&f.Action(id).GetProperty("endEvidenceStatus").GetString()=="unknown","ambiguous native failure is never retried or reported complete");
        }
        {
            var f=new Fixture();string id=Fixture.Id();f.Send("start",id);f.Tick();f.Tick();f.Current=f.Current with{SelectedSlot=7};f.Tick();
            Check(f.Action(id).GetProperty("status").GetString()=="unknown","external selected-slot change before confirmation is not success");
        }
        {
            var f=new Fixture();string id=Fixture.Id();f.Send("start",id);f.Tick();f.Tick();var cancelled=f.Send("cancel",id).GetProperty("data").GetProperty("action");
            Check(cancelled.GetProperty("status").GetString()=="cancelled"&&cancelled.GetProperty("evidence").GetProperty("selectionApplied").GetBoolean()&&f.Writes==1,"cancel after selection preserves the known effect without rollback or extra writes");
        }
        {
            var f=new Fixture();string id=Fixture.Id();f.Send("start",id);
            Check(f.Send("start",Fixture.Id()).GetProperty("error").GetString()=="action_busy","one global atomic selection at a time");
            f.Send("cancel",id);
            Check(f.Send("start",Fixture.Id(),operation:"mine_tile").GetProperty("error").GetString()=="unsupported_operation","unsupported native actions are not admitted");
            Check(f.Send("start",Fixture.Id(),key:10).GetProperty("error").GetString()=="invalid_arguments","out of range public key rejected");
            Check(f.Send("start","bad-id").GetProperty("error").GetString()=="invalid_action_id","malformed action identity rejected");
            Check(f.Send("start",Fixture.Id(),expires:DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+50000).GetProperty("error").GetString()=="invalid_deadline","unbounded client deadline rejected");
        }
        Console.WriteLine($"AI atomic offline fixture: {checks} checks; no Host & Play.");
    }
}
