using System;
using System.IO;
using System.Threading;
using System.Text.Json;
using CompanionBridge;
using CompanionBridge.ServerIpc;

public static class ApiContractProtocolHost
{
    public static void Run(string readyPath, string stopPath)
    {
        // Actual TCP/protocol/action registry; a dead actor fixture admits reads
        // and deterministically refuses starts before any execution entry exists.
        var actor=new object(); ulong tick=0;
        AiReadOnlyProtocol.Identity Capture()=>new(actor,"m2-world",1,254,new{life=73},0,++tick,false);
        object Read(AiReadOnlyProtocol.Identity id,string op,JsonElement args)
        {
            AtomicParameters.Validate(op,args,true);
            if(op=="inspect_inventory")throw new InvalidOperationException("fixture engine read fault");
            return new{available=true,worldSessionId=id.World,actorGeneration=id.Generation,actorSlot=id.Slot,
                gameUpdate=id.GameUpdate,observedAtUnixMs=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),clock="server_utc_at_projection",
                data=new{player=new{life=73},motion=new{grounded=true,supported=true,playerWidth=20,playerHeight=42}}};
        }
        using var server=new AiReadOnlyServer(0,new AiAtomicActions());
        server.Start();File.WriteAllText(readyPath,server.Port.ToString());
        long deadline=Environment.TickCount64+30000;
        while(!File.Exists(stopPath)&&Environment.TickCount64<deadline)
        {server.Tick(Capture,(_,_)=>throw new Exception("Unexpected mutation"),Read);Thread.Sleep(2);}
    }
}
