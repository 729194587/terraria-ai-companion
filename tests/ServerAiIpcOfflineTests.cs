using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CompanionBridge.ServerIpc;

public static class ServerAiIpcOfflineTests
{
    private static int checks;
    private static void Check(bool ok, string description) { if (!ok) throw new Exception(description); checks++; Console.WriteLine("PASS AI IPC: " + description); }
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
    private static string Bind(string id="b") => "{\"v\":1,\"id\":\""+id+"\",\"op\":\"bind\"}";
    private static string State(JsonElement binding) => "{\"v\":1,\"id\":\"s\",\"op\":\"state\",\"binding\":"+binding.GetRawText()+"}";
    private static bool Error(string response, string code) => Json(response).GetProperty("error").GetString()==code;
    private static void Wait(Func<bool> done) { for (int i=0;i<400&&!done();i++) Thread.Sleep(5); if(!done()) throw new Exception("Timed out waiting for fixture network"); }
    public static void Run()
    {
        var protocol=new AiReadOnlyProtocol(); var connection=Guid.NewGuid(); object actor=new();
        var current=new AiReadOnlyProtocol.Identity(actor,"world-1",1,7,new { life=100, inventory=new[]{new {slot=0,type=1,stack=2}} });
        int captures=0;
        AiReadOnlyProtocol.Identity Capture(){captures++;return current;}
        Check(AiReadOnlyServer.ParsePort(null,18777)==18779,"independent default port");
        foreach(var value in new[]{"18777","0","65536","invalid"}) { bool rejected=false;try{AiReadOnlyServer.ParsePort(value,18777);}catch(ArgumentException){rejected=true;}Check(rejected,"reject invalid/conflicting port "+value); }
        foreach(var line in new[]{"{","[]","{\"v\":1,\"v\":1,\"id\":\"x\",\"op\":\"bind\"}","{\"v\":2,\"id\":\"x\",\"op\":\"bind\"}","{\"v\":1,\"id\":\"x\",\"op\":\"state\"}"})
            Check(Error(protocol.Handle(connection,line,Capture),"invalid_request"),"malformed protocol fails closed");
        Check(captures==0,"invalid requests never invoke state capture");
        Check(Error(protocol.Handle(connection,"{\"v\":1,\"id\":\"x\",\"op\":\"mine\"}",Capture),"unsupported_operation"),"actions are unavailable");
        var binding=Json(protocol.Handle(connection,Bind(),Capture)).GetProperty("binding");
        Check(binding.GetProperty("world").GetString()=="world-1"&&binding.GetProperty("generation").GetInt64()==1,"bind returns world and generation");
        Check(Json(protocol.Handle(connection,State(binding),Capture)).GetProperty("data").GetProperty("life").GetInt32()==100,"bound state returned");
        int beforeRejected=captures;
        Check(Error(protocol.Handle(Guid.NewGuid(),State(binding),Capture),"not_bound"),"binding cannot cross connections");
        Check(captures==beforeRejected,"unbound state rejects before capture");
        Check(Error(protocol.Handle(connection,Bind(),Capture),"already_bound"),"connection cannot silently rebind");
        Check(captures==beforeRejected,"duplicate bind rejects before capture");
        foreach(var mismatched in new[]{
            new AiReadOnlyProtocol.Binding("wrong-actor","world-1",1,7),
            new AiReadOnlyProtocol.Binding(binding.GetProperty("actor").GetString(),"wrong-world",1,7),
            new AiReadOnlyProtocol.Binding(binding.GetProperty("actor").GetString(),"world-1",2,7),
            new AiReadOnlyProtocol.Binding(binding.GetProperty("actor").GetString(),"world-1",1,8)})
        {
            Check(Error(protocol.Handle(connection,State(Json(JsonSerializer.Serialize(mismatched))),Capture),"stale_binding"),"mismatched binding rejects before engine validation");
            Check(captures==beforeRejected,"mismatched binding never invokes capture");
        }
        current=current with {Actor=new object()};
        Check(Error(protocol.Handle(connection,State(binding),Capture),"stale_binding"),"object replacement rejected even at same slot/generation");
        Check(captures==++beforeRejected,"matching lease still captures current object for secondary validation");
        current=current with {Actor=actor,Generation=2};
        Check(Error(protocol.Handle(connection,State(binding),Capture),"stale_binding"),"old generation rejected");
        Check(captures==++beforeRejected,"current generation change is checked after capture");
        current=current with {Generation=1,World="world-2"};
        Check(Error(protocol.Handle(connection,State(binding),Capture),"stale_binding"),"old world rejected");
        Check(captures==++beforeRejected,"current world change is checked after capture");
        current=current with {World="world-1",Slot=8};
        Check(Error(protocol.Handle(connection,State(binding),Capture),"stale_binding"),"current slot replacement rejects matching old binding");
        Check(captures==++beforeRejected,"current slot change is checked after capture");
        current=null;
        Check(Error(protocol.Handle(connection,State(binding),Capture),"identity_unavailable"),"missing identity cannot fall back to human");
        Check(Error(protocol.Handle(connection,State(binding),()=>throw new InvalidOperationException()),"engine_unavailable"),"engine/quarantine failures return structured refusal");
        current=new(actor,"world-1",1,7,new {life=99});protocol.Clear();
        Check(Error(protocol.Handle(connection,State(binding),Capture),"not_bound"),"unload invalidates all leases");
        int owner=Environment.CurrentManagedThreadId;
        using(var server=new AiReadOnlyServer(0))
        {
            server.Start();int port=server.Port;
            using(var client=new TcpClient("127.0.0.1",port))
            using(var reader=new StreamReader(client.GetStream(),Encoding.UTF8,false,1024,true))
            {
                void Send(string line){byte[] bytes=Encoding.UTF8.GetBytes(line+"\n");client.GetStream().Write(bytes);}
                string Roundtrip(string line){Send(line);Wait(()=>server.PendingCount>0);var read=reader.ReadLineAsync();while(!read.IsCompleted){server.Tick(()=>{Check(Environment.CurrentManagedThreadId==owner,"native provider stays on simulation thread");return current;});Thread.Sleep(5);}return read.GetAwaiter().GetResult();}
                int before=captures;Send(Bind());Wait(()=>server.PendingCount==1);
                Check(captures==before,"TCP worker queues without reading Player provider");
                Exception offThread=null;Task.Run(()=>{try{server.Tick(Capture);}catch(Exception e){offThread=e;}}).GetAwaiter().GetResult();
                Check(offThread is InvalidOperationException&&server.PendingCount==1,"off-thread pump rejects before dequeuing");
                server.Tick(Capture);var networkBinding=Json(reader.ReadLine()).GetProperty("binding");
                Check(Json(Roundtrip(State(networkBinding))).GetProperty("ok").GetBoolean(),"real loopback bind/state framing");
                current=current with{Generation=2};
                Check(Error(Roundtrip(State(networkBinding)),"stale_binding"),"real TCP rejects stale generation");
                Send(State(networkBinding));Wait(()=>server.PendingCount>0);client.Close();Thread.Sleep(100);
                before=captures;server.Tick(Capture);Check(captures==before,"disconnect cancels queued state read");
            }
            server.Dispose();
            using var replacement=new AiReadOnlyServer(port);replacement.Start();
            Check(true,"unload releases IPC listener");
        }
        Console.WriteLine($"AI IPC offline fixture: {checks} checks passed; NOT Host & Play.");
    }
}
