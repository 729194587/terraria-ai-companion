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
        internal Fixture(){Binding=Request("bind").GetProperty("binding");Request("reply_turn");}
        internal JsonElement Request(string op,string message=null,string text="AI reply",long? expires=null,JsonElement? binding=null,CancellationToken token=default,long turn=1)
        {
            object request=op=="bind"?new{v=1,id="r",op}:op=="reply_turn"?new{v=1,id="r",op,binding=binding??Binding,replyTurn=turn}:new{v=1,id="r",op,binding=binding??Binding,replyTurn=turn,messageId=message??Guid.NewGuid().ToString("N"),text,expiresAtUnixMs=expires??DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+1500};
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
        {
            var a=new Fixture();
            // Simulate the TCP reader seeing the newer instruction before the
            // simulation thread drains the already queued old say.
            string newer=JsonSerializer.Serialize(new{v=1,id="fence",op="reply_turn",binding=a.Binding,replyTurn=2});
            System.Threading.Tasks.Task.Run(()=>a.Protocol.ObserveReplyTurn(a.Owner,newer)).GetAwaiter().GetResult();
            Check(a.Request("say").GetProperty("error").GetString()=="superseded"&&a.Publishes==0,"reader fence rejects old queued publication before newer game-thread ACK");
            Check(a.Request("reply_turn",turn:2).GetProperty("ok").GetBoolean(),"current turn acknowledged with actor validation");
            Check(a.Request("reply_turn",turn:1).GetProperty("error").GetString()=="superseded","older turn cannot roll back fence");
            Check(a.Request("say",turn:2).GetProperty("ok").GetBoolean()&&a.Publishes==1,"current turn publishes once with true AI author");
            a.Protocol.RevokeReplies(a.Owner);
            Check(a.Request("say",turn:2).GetProperty("error").GetString()=="superseded"&&a.Publishes==1,"disconnect revokes queued speech even before main-thread cleanup");
        }
        {
            var fence=new AiReplyFence();fence.Advance(1);
            using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
            var publishing=System.Threading.Tasks.Task.Run(()=>fence.Publish(1,()=>{entered.Set();if(!release.Wait(2000))throw new Exception("release timeout");}));
            Check(entered.Wait(2000),"native broadcast entered publication critical section");
            var advancing=System.Threading.Tasks.Task.Run(()=>fence.Advance(2));release.Set();
            Check(publishing.GetAwaiter().GetResult()&&advancing.GetAwaiter().GetResult(),"started broadcast completes before new fence; no retry or recall");
            Check(!fence.Publish(1,()=>throw new Exception("old broadcast")),"after fence advance no old callback can enter");
        }
        {
            using var server=new AiReadOnlyServer(0,new AiAtomicActions());server.Start();
            using var client=new System.Net.Sockets.TcpClient("127.0.0.1",server.Port);
            using var reader=new System.IO.StreamReader(client.GetStream());
            var identity=new AiReadOnlyProtocol.Identity(new object(),"tcp-world",7,254,new{});
            int nativeCalls=0,owner=Environment.CurrentManagedThreadId;
            object Say(AiReadOnlyProtocol.Identity id,string text){Check(Environment.CurrentManagedThreadId==owner,"TCP fence never moves native broadcast off simulation thread");nativeCalls++;return new{status="published",authorSlot=id.Slot};}
            void Send(object value){byte[] bytes=System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)+"\n");client.GetStream().Write(bytes);}
            void WaitJobs(int count){if(!SpinWait.SpinUntil(()=>server.PendingCount==count,2000))throw new Exception("queued TCP requests timeout");}
            void Pump(){server.Tick(()=>identity,say:Say);}
            Send(new{v=1,id="bind",op="bind"});WaitJobs(1);Pump();
            var binding=JsonDocument.Parse(reader.ReadLine()).RootElement.GetProperty("binding").Clone();
            Send(new{v=1,id="turn1",op="reply_turn",binding,replyTurn=1});WaitJobs(1);Pump();reader.ReadLine();
            Send(new{v=1,id="old",op="say",binding,replyTurn=1,text="old",messageId=Guid.NewGuid().ToString("N"),expiresAtUnixMs=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+1800});WaitJobs(1);
            Send(new{v=1,id="turn2",op="reply_turn",binding,replyTurn=2});WaitJobs(2);
            Pump();
            var rejected=JsonDocument.Parse(reader.ReadLine()).RootElement;
            Check(rejected.GetProperty("error").GetString()=="superseded"&&nativeCalls==0,"real TCP reader fences old queued say while both await game-thread processing");
            Check(JsonDocument.Parse(reader.ReadLine()).RootElement.GetProperty("ok").GetBoolean(),"pipelined new turn acknowledges on original TCP connection");
            Send(new{v=1,id="fresh",op="say",binding,replyTurn=2,text="new",messageId=Guid.NewGuid().ToString("N"),expiresAtUnixMs=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+1800});WaitJobs(1);Pump();
            Check(JsonDocument.Parse(reader.ReadLine()).RootElement.GetProperty("ok").GetBoolean()&&nativeCalls==1,"real TCP current turn publishes once as bound AI");
        }
        Console.WriteLine($"AI native chat protocol: {checks} checks; no graphical client.");
    }
}
