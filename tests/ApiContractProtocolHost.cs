using System;
using System.IO;
using System.Threading;
using System.Text.Json;
using CompanionBridge;
using CompanionBridge.ServerIpc;

public static class ApiContractProtocolHost
{
    private sealed class Executor(string advancePath) : AiAtomicActions.IExecutor
    {
        private sealed record Handle(AiReadOnlyProtocol.Identity Identity,string Id,string Operation,JsonElement Arguments);
        internal int Starts;
        public string Validate(string operation,JsonElement arguments)=>null;
        public object Start(AiReadOnlyProtocol.Identity identity,string operation,JsonElement arguments,string actionId)
        {Starts++;File.WriteAllText(advancePath+".starts",Starts.ToString());return new Handle(identity,actionId,operation,arguments.Clone());}
        public AiAtomicActions.NativeResult Poll(AiReadOnlyProtocol.Identity identity,object handle)
        {
            if(File.Exists(advancePath+".danger"))return new("cancelled","task_interrupt_nearby_hostile",null,true);
            if(!File.Exists(advancePath))return new("running","fixture_running",null,false);
            var h=(Handle)handle;
            object evidence=null;
            if(h.Operation=="mine_target")
            {
                var target=h.Arguments.TryGetProperty("target",out var structured)?structured:h.Arguments;
                var proof=new{X=target.GetProperty("x").GetInt32(),Y=target.GetProperty("y").GetInt32(),TileType=7,
                    destroyedByActor=true,targetAbsent=true,targetChanged=false};
                evidence=new{task=new{taskId=h.Id,taskName="mine_target",phase="terminal",status="completed",
                    worldSessionId=h.Identity.World,actorGeneration=h.Identity.Generation,actorSlot=h.Identity.Slot,
                    lastResult=new{status="completed",inputsReleased=true,effectVerified=true,evidence=proof}}};
            }
            return new("completed","fixture_completed",evidence,!File.Exists(advancePath+".unreleased"),true);
        }
        public AiAtomicActions.NativeResult Cancel(AiReadOnlyProtocol.Identity identity,object handle)=>new("cancelled","fixture_cancelled",null,!File.Exists(advancePath+".unreleased"));
        public void Shutdown(){}
    }
    public static void Run(string readyPath, string stopPath)
    {
        // Actual TCP/protocol/action registry; a dead actor fixture admits reads
        // and deterministically refuses starts before any execution entry exists.
        var actor=new object(); ulong tick=0;
        bool live=File.Exists(readyPath+".live");var executor=new Executor(readyPath+".advance");
        AiReadOnlyProtocol.Identity Capture()=>new(actor,"m2-world",File.Exists(readyPath+".identity")?2:1,254,new{life=73,starts=executor.Starts},0,++tick,live);
        object Read(AiReadOnlyProtocol.Identity id,string op,JsonElement args)
        {
            AtomicParameters.Validate(op,args,true);
            if(op=="inspect_inventory")throw new InvalidOperationException("fixture engine read fault");
            return new{available=true,worldSessionId=id.World,actorGeneration=id.Generation,actorSlot=id.Slot,
                gameUpdate=id.GameUpdate,observedAtUnixMs=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),clock="server_utc_at_projection",
                data=new{player=new{life=73},motion=new{grounded=true,supported=true,playerWidth=20,playerHeight=42}}};
        }
        using var server=new AiReadOnlyServer(0,new AiAtomicActions(live?executor:null));
        server.Start();File.WriteAllText(readyPath,server.Port.ToString());
        long deadline=Environment.TickCount64+30000;
        while(!File.Exists(stopPath)&&Environment.TickCount64<deadline)
        {server.Tick(Capture,(_,_)=>throw new Exception("Unexpected mutation"),Read,
            (id,text)=>new{status="published",authorSlot=id.Slot,worldSessionId=id.World,actorGeneration=id.Generation,gameUpdate=id.GameUpdate});Thread.Sleep(2);}
    }
}
