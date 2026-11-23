using System;
using System.IO;
using System.Threading;
using System.Text.Json;
using CompanionBridge;
using CompanionBridge.ServerIpc;

public static class ApiContractProtocolHost
{
    private sealed record Point(float X, float Y);
    private sealed record MiningCell(int X, int Y, int TileType);

    private static object MiningProof(string id, MiningCell cell) => new {
        nativeActionId=Guid.ParseExact(id,"N"),cell.X,cell.Y,cell.TileType,ExpectedType=7,
        Ticks=5,invalidTarget=(object)null,Swings=1,Strikes=1,PeakDamage=100,Transforms=0,
        toolKind="pickaxe",pick=35,axe=0,nativeDestroyed=true,targetAbsent=true,furnitureCells=0,
        furnitureAbsent=false,targetChanged=false,targetUnchanged=false,destroyedByActor=true,
        effectStatus="native_tile_destroyed"
    };

    private sealed class Executor(string advancePath) : AiAtomicActions.IExecutor
    {
        private sealed record Handle(AiReadOnlyProtocol.Identity Identity,string Id,string Operation,JsonElement Arguments);
        internal int Starts;
        public bool SupportsExploration=>File.Exists(advancePath+".frontier");
        public string Validate(string operation,JsonElement arguments)=>null;
        public object Start(AiReadOnlyProtocol.Identity identity,string operation,JsonElement arguments,string actionId)
        {Starts++;File.WriteAllText(advancePath+".starts",Starts.ToString());return new Handle(identity,actionId,operation,arguments.Clone());}
        public AiAtomicActions.NativeResult Poll(AiReadOnlyProtocol.Identity identity,object handle)
        {
            if(File.Exists(advancePath+".danger"))return new("cancelled","task_interrupt_nearby_hostile",null,true);
            if(!File.Exists(advancePath))return new("running","fixture_running",null,false);
            var h=(Handle)handle;
            object evidence=null;
            if(h.Operation=="explore_frontier")
            {
                var proof=new{segments=2,newTiles=3,returned=!File.Exists(advancePath+".no_return"),mapRevision=4,
                    actualLeft=File.Exists(advancePath+".off_anchor")?176:160,anchorLeft=160,
                    actualFeetY=320,anchorFeetY=320,candidates=Array.Empty<object>()};
                var result=new{status="completed",reason="frontier_observed_and_returned",inputsReleased=true,effectVerified=true,
                    inputsAcquired=true,reasonDetail=(string)null,evidence=proof};
                evidence=new{task=new{taskId=h.Id,taskName=h.Operation,phase="terminal",status="completed",parentTaskId=(string)null,
                    worldSessionId=h.Identity.World,actorGeneration=h.Identity.Generation,actorSlot=h.Identity.Slot,
                    observedGameUpdate=identity.GameUpdate,deadlineGameUpdate=h.Identity.GameUpdate+3600,
                    currentChild=(object)null,progress=proof,lastResult=(object)null,reason=result.reason,result}};
            }
            if(h.Operation is "mine_tile" or "mine_target")
            {
                var target=h.Arguments.TryGetProperty("target",out var structured)?structured:h.Arguments;
                var cell=new MiningCell(target.GetProperty("x").GetInt32(),target.GetProperty("y").GetInt32(),7);
                var proof=MiningProof(h.Id,cell);
                evidence=proof;
                if(h.Operation=="mine_target")
                {
                    var result=new{status="completed",reason="fixture_completed",inputsReleased=true,effectVerified=true,
                        inputsAcquired=true,reasonDetail=(string)null,evidence=proof};
                    var taskIdentity=new{worldSessionId=h.Identity.World,actorGeneration=h.Identity.Generation,actorSlot=h.Identity.Slot,
                        observedGameUpdate=identity.GameUpdate,deadlineGameUpdate=h.Identity.GameUpdate+300};
                    var child=new{taskId=h.Id+"/mine",taskName="mine_tile",phase="terminal",status="completed",parentTaskId=h.Id,
                        taskIdentity.worldSessionId,taskIdentity.actorGeneration,taskIdentity.actorSlot,taskIdentity.observedGameUpdate,taskIdentity.deadlineGameUpdate,
                        currentChild=(object)null,progress=(object)null,lastResult=(object)null,reason="fixture_completed",result};
                    evidence=new{task=new{taskId=h.Id,taskName="mine_target",phase="terminal",status="completed",parentTaskId=(string)null,
                        taskIdentity.worldSessionId,taskIdentity.actorGeneration,taskIdentity.actorSlot,taskIdentity.observedGameUpdate,taskIdentity.deadlineGameUpdate,
                        currentChild=(object)null,progress=new{stage="verify",completedChildren=1,settledChildren=1,
                            children=new[]{child},childrenTruncated=false,omittedChildren=0,
                            target=new{x=cell.X,y=cell.Y,typeId=cell.TileType,toolSlot=0,scope="one specified tile"},
                            clearedTiles=0,dugTiles=0,placedTiles=0,openingMoves=0,openingMode=false,search=(string)null,
                            opening=(object)null,planning=(object)null,activity="Search",verifiedTerrainActions=0},
                        lastResult=result,reason="fixture_completed",result}};
                }
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
            if(File.Exists(readyPath+".unavailable"))return new{available=false,reason="fixture_unavailable"};
            var position=new Point(160,320);
            var velocity=new Point(0,0);
            // Mirror the query serializer's camelCase, then send through the
            // ordinary protocol serializer and the Python state flattening.
            object data=File.Exists(readyPath+".typed")?JsonSerializer.SerializeToElement(new{
                source="server_ai_player",customState="unknown",inventoryTokenStatus="fixture",
                player=new{id=id.Slot,name="fixture",source="server_ai_player",life=73,lifeMax=100,mana=20,manaMax=20,
                    breath=200,breathMax=200,immune=false,immuneTime=0,direction=1,selectedSlot=0,selectedHotbarKey=1,
                    dead=false,ghost=false,active=true,inWorld=true,statDefense=new{positive=0,negative=0},
                    positionWorldPixels=position,velocityPixelsPerTick=velocity,position,velocity,
                    boundsWorldPixels=new{x=160,y=320,width=20,height=42},hotbarInventorySlots=new[]{0},
                    hotbarKeyBindings=new[]{new{key=1,inventorySlot=0}},hotbarNumbering="game keys",equipmentScope="vanilla",
                    mount=new{active=false,typeId=(int?)null},
                    movement=new{contactStatus="observed",grapCount=0,jump=0,sloping=false,stairFall=false,wet=false,
                        lavaWet=false,honeyWet=false,shimmerWet=false,pulley=false,controlDown=false,controlJump=false,
                        frozen=false,webbed=false,stoned=false,gravDir=1,runAcceleration=0.08,maxRunSpeed=3,gravity=0.4,maxFallSpeed=10.01}},
                motion=new{grounded=true,supported=true,playerWidth=20,playerHeight=42,knowledge="fixture"},
                functionalEquipmentSlots=Array.Empty<object>()},
                new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase})
                :new{player=new{life=73},motion=new{grounded=true,supported=true,playerWidth=20,playerHeight=42}};
            return new{available=true,worldSessionId=id.World,actorGeneration=id.Generation,actorSlot=id.Slot,
                gameUpdate=id.GameUpdate,observedAtUnixMs=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),clock="server_utc_at_projection",
                data};
        }
        using var server=new AiReadOnlyServer(0,new AiAtomicActions(live?executor:null));
        server.Start();File.WriteAllText(readyPath,server.Port.ToString());
        long deadline=Environment.TickCount64+30000;
        while(!File.Exists(stopPath)&&Environment.TickCount64<deadline)
        {server.Tick(Capture,(_,_)=>throw new Exception("Unexpected mutation"),Read,
            (id,text)=>new{status="published",authorSlot=id.Slot,worldSessionId=id.World,actorGeneration=id.Generation,gameUpdate=id.GameUpdate});Thread.Sleep(2);}
    }
}
