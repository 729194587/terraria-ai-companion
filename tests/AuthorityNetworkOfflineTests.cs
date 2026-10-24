using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using CompanionBridge;
using CompanionBridge.Common.Systems;
using Terraria;
using Terraria.ModLoader;

public static class AuthorityNetworkOfflineTests
{
    static void Check(bool ok,string why) { if(!ok) throw new Exception(why); }
    public static void Run()
    {
        var adapter=new AtomicMultiplayer(); adapter.Load(); adapter.OnWorldLoad();
        var controls=CompanionBridge.CompanionBridge.Controls;
        controls.Reset(); int session=controls.BeginSession(),requestId=0;
        string world=Guid.NewGuid().ToString("N"); ObservationReader.WorldSessionId=world;
        var client=new Player {position=new(88,88)}; var server=new Player {position=new(88,88)};
        Tile[,] clientTiles=new Tile[100,100],serverTiles=new Tile[100,100];
        void Role(bool isServer)
        { Main.netMode=isServer?2:1; Main.myPlayer=0; Main.player[0]=isServer?server:client; Main.tile=isServer?serverTiles:clientTiles; }
        byte[] Take(int target)
        {
            int index=ModPacket.Sent.FindIndex(p=>p.Target==target); Check(index>=0,"Expected authority wire message");
            var bytes=ModPacket.Sent[index].Bytes; ModPacket.Sent.RemoveAt(index); return bytes;
        }
        void Deliver(byte[] bytes,bool toServer)
        {
            Role(toServer); using var reader=new BinaryReader(new MemoryStream(bytes));
            AtomicMultiplayer.Receive(reader,toServer?0:256); adapter.PostUpdateEverything();
        }
        void RoundTrip() { Deliver(Take(-1),true); Deliver(Take(0),false); }
        ControlRequest Start(int stack=2)
        {
            controls.Reset(); Role(false); ModPacket.Sent.Clear();
            clientTiles=new Tile[100,100]; serverTiles=new Tile[100,100];
            clientTiles[4,5].WallType=serverTiles[4,5].WallType=1; Main.tile=clientTiles;
            client.inventory[0]=new Item {type=1,stack=stack,createWall=1,consumable=true};
            server.inventory[0]=new Item {type=1,stack=stack,createWall=1,consumable=true};
            client.itemAnimation=client.itemTime=0; client.selectedItem=server.selectedItem=0;
            var request=ControlState.Parse(JsonSerializer.Serialize(new {v=1,id=++requestId,cmd="start_atomic",operation="place_wall",arguments=new {x=5,y=5},expectedGeneration=controls.Generation}));
            var pending=controls.Enqueue(session,request,Stopwatch.GetTimestamp());
            controls.Update(true,Stopwatch.GetTimestamp(),new(true,new(88,88),new(0,0),100,100),worldSessionId:world);
            Check(pending.Result.Ok,"Real ControlState starts action"); AtomicOperations.Before(client,request);
            return request;
        }
        void Arm(ControlRequest request,bool hello)
        {
            Check(!client.controlUseItem && controls.AtomicAction.Status=="running","No input before server arm ACK");
            if(hello) RoundTrip();
            RoundTrip(); Role(false); AtomicOperations.Before(client,request);
            Check(client.controlUseItem && controls.AtomicAction.Status=="running","Input allowed only after matching server arm");
        }
        try
        {
            var request=Start(); Arm(request,true);
            int originals=NetMessage.Syncs;
            AtomicOperations.Aim(client);
            NetMessage.SendData(17,number:3,number2:5,number3:5,number4:1);
            clientTiles[5,5].WallType=1; client.inventory[0].stack--;
            NetMessage.SendData(5,number:0,number2:0);
            Check(NetMessage.Syncs==originals,"Captured mutation and speculative selected-slot sync are not sent untagged");
            AtomicOperations.RestoreAim(); AtomicOperations.After(client);
            Check(controls.AtomicAction.Status=="running","Client predicted wall and consumption never complete action");
            byte[] execute=Take(-1); int nativeCalls=MessageBuffer.Calls;
            Deliver(execute,true);
            Check(serverTiles[5,5].WallType==1 && server.inventory[0].stack==1 && MessageBuffer.Calls==nativeCalls+1,"Server executes native handler and consumes exactly one selected item");
            byte[] receipt=Take(0);
            Deliver(execute,true);
            Check(MessageBuffer.Calls==nativeCalls+1,"Duplicate Mod request cannot repeat native mutation or consume again");
            ModPacket.Sent.Clear();
            clientTiles[5,5].WallType=0; Deliver(receipt,false); AtomicOperations.Before(client,request);
            Check(controls.AtomicAction.Status=="running" && !client.controlUseItem,"Server completion still waits for local state convergence");
            clientTiles[5,5].WallType=1; AtomicOperations.Before(client,request);
            Check(controls.AtomicAction.Completed && controls.AtomicAction.EndEvidenceStatus=="pending","Correlated server result completes effect, not release boundary");
            var evidence=JsonSerializer.SerializeToElement(controls.AtomicAction.Evidence);
            Check(evidence.GetProperty("authority").GetString()=="server_native_handler" && evidence.GetProperty("clientStateVerified").GetBoolean(),"Tool evidence records server authority and client agreement");
            controls.ConfirmMovementInputReleased(world,10,"PostUpdateRunSpeeds_before_bridge_input",true);
            Check(controls.AtomicAction.EndEvidenceStatus=="known","Existing game-thread release receipt still required");

            request=Start(); Arm(request,false);
            AtomicOperations.Aim(client); NetMessage.SendData(17,number:3,number2:5,number3:5,number4:1);
            clientTiles[5,5].WallType=1; client.inventory[0].stack--; AtomicOperations.RestoreAim();
            controls.RevokeControl(); AtomicOperations.After(client); adapter.PostUpdateEverything();
            nativeCalls=MessageBuffer.Calls; Deliver(Take(-1),true);
            Check(MessageBuffer.Calls==nativeCalls && serverTiles[5,5].WallType==0 && server.inventory[0].stack==2,"Revocation before flush cannot execute buffered request");
            Check(controls.AtomicAction.Status=="cancelled" && !client.controlUseItem,"Cancellation keeps normal terminal/input semantics");

            request=Start(); Arm(request,false);
            Deliver(receipt,false); AtomicOperations.Before(client,request);
            Check(controls.AtomicAction.Status=="running","Old nonce/action receipt cannot complete replacement action");
            AtomicOperations.Aim(client); originals=NetMessage.Syncs;
            NetMessage.SendData(17,number:3,number2:6,number3:5,number4:1);
            AtomicOperations.RestoreAim(); AtomicOperations.After(client); AtomicOperations.Before(client,request);
            Check(controls.AtomicAction.Status=="unknown" && NetMessage.Syncs==originals,"Off-target native mutation is suppressed and never reported completed");

            request=Start(); Arm(request,false);
            clientTiles[5,5].WallType=1; client.inventory[0].stack--; // missing captured native packet
            AtomicOperations.Before(client,request);
            Check(controls.AtomicAction.Status=="unknown","Uncorrelated local prediction cannot fall through to single-player success");

            request=Start(1); Arm(request,false);
            AtomicOperations.Aim(client); NetMessage.SendData(17,number:3,number2:5,number3:5,number4:1);
            clientTiles[5,5].WallType=1; client.inventory[0].TurnToAir();
            AtomicOperations.RestoreAim(); AtomicOperations.After(client); RoundTrip();
            AtomicOperations.Before(client,request);
            Check(controls.AtomicAction.Completed && server.inventory[0].IsAir,"Last-item prediction becoming air retains correlated server completion path");

            request=Start(); Arm(request,false);
            AtomicOperations.Aim(client); NetMessage.SendData(17,number:3,number2:5,number3:5,number4:1);
            clientTiles[5,5].WallType=1; client.inventory[0].stack--;
            AtomicOperations.RestoreAim(); AtomicOperations.After(client); Deliver(Take(-1),true);
            receipt=Take(0); Role(false); controls.RevokeControl(); Deliver(receipt,false);
            AtomicOperations.Before(client,request);
            evidence=JsonSerializer.SerializeToElement(controls.AtomicAction.Evidence);
            Check(controls.AtomicAction.Status=="cancelled" && serverTiles[5,5].WallType==1 && evidence.GetProperty("mutationSent").GetBoolean(),
                "Late completed receipt cannot replace cancellation or pretend already executed server effect rolled back");
            Console.WriteLine("PASS: production multiplayer adapter + ControlState + native payload adapter across isolated client/server storage: handshake, tagged-only dispatch, prediction/ACK separation, duplicate safety, consumption including last item, convergence, cancel-before-flush/after-execute and stale receipt rejection (wire/game substitutes)");
        }
        finally { controls.EndSession(session); adapter.Unload(); Main.netMode=0; ObservationReader.WorldSessionId="hook-test"; }
    }
}
