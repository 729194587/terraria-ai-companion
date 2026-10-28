using System;
using System.Text.Json;
using CompanionBridge.ServerIpc;

public static class ServerAiReadProtocolTests
{
    public static void Run()
    {
        int checks=0,captures=0,queries=0;
        void Check(bool ok,string text){if(!ok)throw new Exception(text);checks++;Console.WriteLine("PASS AI read protocol fixture: "+text);}
        var protocol=new AiReadOnlyProtocol(new AiAtomicActions());var owner=Guid.NewGuid();
        var current=new AiReadOnlyProtocol.Identity(new object(),"world",1,254,new {life=73},0,42);
        AiReadOnlyProtocol.Identity Capture(){captures++;return current;}
        object Query(AiReadOnlyProtocol.Identity identity,string operation,JsonElement args){queries++;Check(ReferenceEquals(identity.Actor,current.Actor),"projection receives the validated actor reference");return new{available=true,worldSessionId=identity.World,actorGeneration=identity.Generation,actorSlot=identity.Slot,gameUpdate=identity.GameUpdate,data=new{sample=operation}};}
        JsonElement Handle(object request,Guid? connection=null)=>JsonDocument.Parse(protocol.Handle(connection??owner,JsonSerializer.Serialize(request),Capture,query:Query)).RootElement.Clone();
        var bound=Handle(new{v=1,id="b",op="bind"});var binding=bound.GetProperty("binding");
        Check(bound.GetProperty("data").GetProperty("capabilities").ToString().Contains("server_ai_reads_v1"),"read capability is advertised only with an installed query adapter");
        object Request(string operation="get_player_state",object b=null)=>new{v=1,id="q",op="query",binding=b??(object)binding,operation,arguments=new{}};
        Check(Handle(Request()).GetProperty("data").GetProperty("gameUpdate").GetInt32()==42,"query result carries actual capture tick");
        foreach(string operation in new[]{"observe_terrain","scan_tiles","scan_entities","inspect_tile","resolve_content","observe_scene","query_recipes"})
            Check(Handle(Request(operation)).GetProperty("ok").GetBoolean(),operation+" reaches bound read adapter");
        int before=captures,projected=queries;
        Check(Handle(Request(),Guid.NewGuid()).GetProperty("error").GetString()=="not_bound"&&captures==before&&queries==projected,"unbound query cannot read Player or project data");
        Check(Handle(Request(b:new{actor="wrong",world="world",generation=1,slot=254})).GetProperty("error").GetString()=="stale_binding"&&captures==before&&queries==projected,"mismatched query binding rejects before native access");
        Check(Handle(Request("attack_entity")).GetProperty("error").GetString()=="unsupported_operation"&&captures==before,"unsupported queries reject without capture");
        Check(Handle(new{v=1,id="q",op="query",binding,operation="inspect_inventory",arguments=new{},actionId="bad"}).GetProperty("error").GetString()=="invalid_request"&&captures==before,"read query cannot smuggle action parameters");
        var original=current;
        foreach(string change in new[]{"actor","world","generation","slot"})
        {
            current=change switch{"actor"=>original with{Actor=new object()},"world"=>original with{World="new"},"generation"=>original with{Generation=2},_=>original with{Slot=253}};
            foreach(string operation in new[]{"get_player_state","observe_terrain","scan_tiles","scan_entities","inspect_tile","resolve_content","observe_scene","query_recipes"})
                Check(Handle(Request(operation)).GetProperty("error").GetString()=="stale_binding"&&queries==projected,"current "+change+" change refuses "+operation+" projection");
        }
        current=original;
        current=null;Check(Handle(Request()).GetProperty("error").GetString()=="identity_unavailable"&&queries==projected,"unavailable identity never returns invented state");
        current=original with{CanAct=false};Check(Handle(Request()).GetProperty("ok").GetBoolean(),"managed dead actor remains observable without enabling actions");
        protocol.Forget(owner);before=captures;
        Check(Handle(Request()).GetProperty("error").GetString()=="not_bound"&&captures==before,"disconnected lease cannot query");
        Console.WriteLine($"AI read protocol fixture: {checks} checks; no Terraria process.");
    }
}
