using System;
using System.Text.Json;
using System.Threading;
using CompanionBridge.ServerIpc;

public static class ServerAiChatProtocolTests
{
    private static int checks;
    private static void Check(bool ok,string text){if(!ok)throw new Exception(text);checks++;}
    private sealed class Fixture
    {
        internal AiReadOnlyProtocol Protocol=new(new AiAtomicActions());
        internal AiReadOnlyProtocol.Identity Current=new(new object(),"world",1,254,new{});
        internal Guid Owner=Guid.NewGuid();internal JsonElement Binding;internal int Captures,Publishes;internal bool Throw;
        internal Fixture(){Binding=Request("bind").GetProperty("binding");}
        internal JsonElement Request(string op,string message=null,string text="AI reply",long? expires=null,JsonElement? binding=null,CancellationToken token=default)
        {
            object request=op=="bind"?new{v=1,id="r",op}:new{v=1,id="r",op,binding=binding??Binding,messageId=message??Guid.NewGuid().ToString("N"),text,expiresAtUnixMs=expires??DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+1500};
            return JsonDocument.Parse(Protocol.Handle(Owner,JsonSerializer.Serialize(request),()=>{Captures++;return Current;},token,say:(id,value)=>{
                Check(ReferenceEquals(id.Actor,Current.Actor),"publisher receives exact current actor reference");Publishes++;if(Throw)throw new Exception("partial send");
                return new{status="published",authorSlot=id.Slot,worldSessionId=id.World,actorGeneration=id.Generation,gameUpdate=1};
            })).RootElement.Clone();
        }
    }
    public static void Run()
    {
        var f=new Fixture();string id=Guid.NewGuid().ToString("N");var sent=f.Request("say",id);
        Check(sent.GetProperty("ok").GetBoolean()&&sent.GetProperty("data").GetProperty("authorSlot").GetInt32()==254,"native author reply uses bound AI slot");
        f.Request("say",id);Check(f.Publishes==1,"duplicate message never broadcasts twice");
        Check(f.Request("say",id,"changed").GetProperty("error").GetString()=="duplicate_message"&&f.Publishes==1,"same message ID cannot change text");
        var wrong=JsonDocument.Parse("{\"actor\":\"wrong\",\"world\":\"world\",\"generation\":1,\"slot\":254}").RootElement.Clone();
        int captures=f.Captures;f.Request("say",binding:wrong);
        Check(f.Captures==captures&&f.Publishes==1,"wrong binding rejects before capture/publication");
        f.Owner=Guid.NewGuid();captures=f.Captures;
        Check(f.Request("say").GetProperty("error").GetString()=="not_bound"&&f.Captures==captures,"unbound connection cannot publish");
        foreach(string change in new[]{"object","world","generation","slot"})
        {
            var a=new Fixture();a.Current=change switch{"object"=>a.Current with{Actor=new object()},"world"=>a.Current with{World="new"},"generation"=>a.Current with{Generation=2},_=>a.Current with{Slot=253}};
            Check(a.Request("say").GetProperty("error").GetString()=="stale_binding"&&a.Publishes==0,"changed "+change+" rejects old reply binding");
        }
        foreach(string text in new[]{"","bad\ntext",new string('x',401)})
        {
            var a=new Fixture();captures=a.Captures;
            Check(a.Request("say",text:text).GetProperty("error").GetString()=="invalid_request"&&a.Captures==captures&&a.Publishes==0,"invalid reply text fails before engine access");
        }
        {
            var a=new Fixture();a.Request("say",expires:DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()-1);
            Check(a.Publishes==0,"expired reply never broadcasts");
            using var cancel=new CancellationTokenSource();cancel.Cancel();a.Request("say",token:cancel.Token);
            Check(a.Publishes==0,"disconnected request cannot broadcast");
        }
        {
            var a=new Fixture{Throw=true};id=Guid.NewGuid().ToString("N");a.Request("say",id);var again=a.Request("say",id);
            Check(again.GetProperty("error").GetString()=="reply_outcome_unknown"&&a.Publishes==1,"partial native failure is fenced before publication and never retried");
        }
        Console.WriteLine($"AI native chat protocol: {checks} checks; no graphical client.");
    }
}
