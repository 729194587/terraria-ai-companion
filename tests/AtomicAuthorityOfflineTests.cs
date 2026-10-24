using System;
using System.Linq;
using System.Text;
using CompanionBridge;
using CompanionBridge.Common.Systems;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

public static class AtomicAuthorityOfflineTests
{
    static void Check(bool ok,string why) { if(!ok) throw new Exception(why); }
    sealed class World : IAtomicAuthorityWorld
    {
        internal int Executions,Actor;
        internal string Invalid;
        internal bool Throw;
        public string Validate(int player,AuthorityIntent intent) => Invalid;
        public AuthorityOutcome Execute(int player,AuthorityIntent intent,NativeMutation native,int stack)
        { Executions++; Actor=player; if(Throw) throw new Exception(); return Rejected(player,intent,"completed","verified"); }
        public AuthorityOutcome Rejected(int player,AuthorityIntent t,string status,string reason) => new(status,reason,t.Cells,t.Stack,t.ItemType,-1,10);
    }
    public static void Run()
    {
        ProtocolAndLifecycle(); NativeAdapter();
        Console.WriteLine("PASS: server authority: strict bounded wire, actor/world/nonce/generation identity, exactly-once native dispatch, lease/absolute timeout/cancel/replay, actual server adapter on storage/wire substitutes (not game physics)");
    }
    static void ProtocolAndLifecycle()
    {
        var server=new AtomicAuthorityServer(); var world=new World(); Guid client=Guid.NewGuid();
        var welcome=server.Handle(0,new(AuthorityKind.Hello,client),0,world);
        var id=new AuthorityIdentity(client,welcome.ServerWorld,1,2,1,Guid.NewGuid());
        var cells=new[]{new AuthorityCell(5,5,false,0,0,false,0,0,0,false)};
        var intent=new AuthorityIntent("place_wall",5,5,0,1,10,-1,cells);
        AuthorityMessage Message(AuthorityKind kind,AuthorityIdentity identity=null) => new(kind,client,(identity??id).ServerWorld,identity??id,
            Intent:kind==AuthorityKind.Arm?intent:null,Native:kind==AuthorityKind.Execute?new(17,3,5,5,1):null,AfterStack:9);
        var arm=Message(AuthorityKind.Arm);
        var decoded=AuthorityCodec.Decode(AuthorityCodec.Encode(arm));
        Check(decoded.Identity==id && decoded.Intent.Cells.SequenceEqual(cells),"Authority wire identity and cells round trip");
        void Bad(string json)
        {
            bool bad=false; try { AuthorityCodec.Decode(Encoding.UTF8.GetBytes(json)); } catch(Exception e) when(e is FormatException or System.Text.Json.JsonException) { bad=true; }
            Check(bad,"Malformed authority packet rejected");
        }
        Bad("{}"); Bad("null");
        string raw=Encoding.UTF8.GetString(AuthorityCodec.Encode(arm));
        Bad(raw[..^1]+",\"extra\":1}"); Bad(raw[..^1]+",\"Kind\":2}");
        Bad(raw.Replace("\"Cells\":[{","\"Cells\":[null,{"));
        bool oversized=false; try { AuthorityCodec.Decode(new byte[8193]); } catch(FormatException) { oversized=true; }
        Check(oversized,"Authority maximum packet bytes enforced");
        Check(server.Handle(0,arm,1,world).Kind==AuthorityKind.Armed && world.Executions==0,"Arm is never completion or native execution");
        Check(server.Handle(1,Message(AuthorityKind.Execute),2,world)==null && world.Executions==0,"Sender cannot execute another player's armed request");
        Check(server.Handle(0,Message(AuthorityKind.Execute,id with {Nonce=Guid.NewGuid()}),2,world)==null,"Wrong nonce ignored");
        Check(server.Handle(0,Message(AuthorityKind.Execute,id with {Generation=3}),2,world)==null,"Wrong generation ignored");
        var receipt=server.Handle(0,Message(AuthorityKind.Execute),3,world);
        Check(receipt.Outcome.Status=="completed" && world.Executions==1 && world.Actor==0,"Correlated request invokes native host once with actual sender");
        Check(server.Handle(0,Message(AuthorityKind.Execute),4,world)==receipt && world.Executions==1,"Duplicate execute returns cached receipt without toggling again");
        Check(server.Handle(0,Message(AuthorityKind.Cancel),5,world)==receipt,"Cancel after server effect cannot pretend to roll back");
        id=id with {ActionId=2,Nonce=Guid.NewGuid()};
        Check(server.Handle(0,Message(AuthorityKind.Arm),10,world).Kind==AuthorityKind.Armed,"Next action allowed");
        Check(server.Handle(0,Message(AuthorityKind.Cancel),11,world).Outcome.Status=="cancelled","Cancel before execution confirmed");
        Check(server.Handle(0,Message(AuthorityKind.Execute),12,world).Outcome.Status=="cancelled" && world.Executions==1,"Cancelled request cannot execute late");
        id=id with {ActionId=3,Nonce=Guid.NewGuid()}; server.Handle(0,Message(AuthorityKind.Arm),1000,world);
        Check(server.Handle(0,Message(AuthorityKind.Execute),1750,world).Outcome.Status=="timeout" && world.Executions==1,"Server lease expires at 750ms");
        id=id with {ActionId=4,Nonce=Guid.NewGuid()}; server.Handle(0,Message(AuthorityKind.Arm),2000,world);
        for(int elapsed=500;elapsed<10000;elapsed+=500) server.Handle(0,Message(AuthorityKind.Renew),2000+elapsed,world);
        Check(server.Handle(0,Message(AuthorityKind.Execute),12000,world).Outcome.Status=="timeout","Server absolute deadline not extended by heartbeat");
        id=id with {ActionId=5,Nonce=Guid.NewGuid()}; server.Handle(0,Message(AuthorityKind.Arm),13000,world);
        world.Invalid="server_target_changed";
        Check(server.Handle(0,Message(AuthorityKind.Execute),13001,world).Outcome.Reason==world.Invalid && world.Executions==1,"Server revalidates state after arm before mutation"); world.Invalid=null;
        id=id with {ActionId=6,Nonce=Guid.NewGuid()}; server.Handle(0,Message(AuthorityKind.Arm),14000,world); world.Throw=true;
        Check(server.Handle(0,Message(AuthorityKind.Execute),14001,world).Outcome.Status=="unknown","Native exception yields unknown");
        server.Handle(0,Message(AuthorityKind.Execute),14002,world); Check(world.Executions==2,"Native exception is never retried");
        server.Disconnect(0);
        Check(server.Handle(0,Message(AuthorityKind.Execute),15000,world)==null,"Disconnect invalidates old peer identity");
        var oldWorld=server.World; server.Reset();
        Check(server.World!=oldWorld && server.Handle(0,Message(AuthorityKind.Execute),16000,world)==null,"World replacement invalidates old action");
        var controls=new ControlState(); int session=controls.BeginSession(); long now=System.Diagnostics.Stopwatch.GetTimestamp();
        var pending=controls.Enqueue(session,new(1,"start_atomic",ExpectedGeneration:0,Operation:"place_wall",Arguments:System.Text.Json.JsonDocument.Parse("{\"x\":5,\"y\":5}").RootElement.Clone()),now);
        controls.Update(true,now,new(true,new(80,80),new(0,0),100,100),worldSessionId:"world");
        int action=controls.AtomicAction.ActionId;
        controls.UpdateAtomicEvidence(action,new {authority="pending_server",mutationSent=true});
        controls.FinishAtomic(action,"timeout","action_deadline",null);
        Check(controls.AtomicAction.Evidence!=null && controls.AtomicAction.EndEvidenceStatus=="pending","Timeout preserves uncertainty and still waits for input release");
        controls.UpdateAtomicEvidence(action,new {wrong=true});
        Check(!System.Text.Json.JsonSerializer.Serialize(controls.AtomicAction.Evidence).Contains("wrong"),"Late metadata cannot alter terminal action"); controls.EndSession(session);
    }
    static void NativeAdapter()
    {
        var host=new NativeAuthorityWorld(); Main.netMode=2; Main.tile=new Tile[100,100];
        var p=Main.player[0]=new Player {position=new(88,88)}; p.inventory[0]=new Item {type=1,stack=2,createWall=1,consumable=true};
        Main.tile[4,5].WallType=1;
        AuthorityIntent Intent(string op,int x=5,int y=5) => new(op,x,y,0,p.inventory[0].type,p.inventory[0].stack,p.chest,host.Capture(op,x,y));
        var intent=Intent("place_wall"); Check(host.Validate(0,intent)==null,"Real server adapter accepts valid vanilla wall prerequisites");
        var result=host.Execute(0,intent,new(17,3,5,5,1),1);
        Check(result.Status=="completed" && Main.tile[5,5].WallType==1 && p.inventory[0].stack==1,"Native payload plus server-owned selected-stack consumption");
        Check(host.ClientMatches(p,intent,result),"Server receipt requires converged client state");
        Main.tile[5,5].WallType=0; Check(!host.ClientMatches(p,intent,result),"Client prediction mismatch cannot become success");
        intent=Intent("place_wall"); MessageBuffer.RejectNative=true;
        result=host.Execute(0,intent,new(17,3,5,5,1),0);
        Check(result.Status=="failed" && p.inventory[0].stack==1,"Native refusal does not consume stack or claim success"); MessageBuffer.RejectNative=false;
        Check(host.Execute(0,intent,new(17,3,6,5,1),0).Status=="failed","Off-target request rejected");
        Check(host.Execute(0,intent,new(17,3,5,5,2),0).Status=="failed","Wrong wall type rejected");
        Check(host.Execute(0,intent,new(17,3,5,5,1),1).Status=="failed","Wrong client consumption evidence rejected");
        result=host.Execute(0,intent,new(17,3,5,5,1),0);
        Check(result.Status=="completed" && p.inventory[0].IsAir && result.Stack==0,"Last wall item verified and synchronized as air");
        Main.tile[5,5]=new Tile {WallType=1}; p.inventory[0]=new Item {hammer=50};
        intent=Intent("mine_wall"); Check(host.Validate(0,intent)==null,"Server wall hammer validation");
        Check(host.Execute(0,intent,new(17,2,5,5,0),10).Status=="completed" && Main.tile[5,5].WallType==0,"Wall mutation uses native wire action 2");
        Main.tile[5,5]=new Tile {HasTile=true,TileType=2}; Main.tileSolid[2]=true;
        intent=Intent("hammer_tile"); Check(host.Validate(0,intent)==null,"Server pound validation");
        Check(host.Execute(0,intent,new(17,7,5,5,1),10).Status=="completed" && Main.tile[5,5].IsHalfBlock,"Native pound result verified");
        intent=Intent("hammer_tile"); Check(host.Execute(0,intent,new(17,14,5,5,1),10).Status=="completed" && Main.tile[5,5].Slope==1,"Native slope result verified");
        p.noBuilding=true; Check(host.Validate(0,Intent("hammer_tile"))!=null,"Server noBuilding cannot be bypassed"); p.noBuilding=false;
        p.NativeItemRange=false; Check(host.Validate(0,Intent("hammer_tile"))=="server_out_of_native_item_range","Server native item reach enforced"); p.NativeItemRange=true;
        var changed=Intent("hammer_tile"); Main.tile[5,5].TileType=3;
        Check(host.Validate(0,changed)=="server_target_changed","Intervening player target replacement rejected");
        Main.tile[5,5].TileType=2; Netplay.Clients[0].TileSections[0,0]=false;
        Check(host.Validate(0,Intent("hammer_tile"))=="server_target_section_not_sent","Unsent server region rejected"); Netplay.Clients[0].TileSections[0,0]=true;
        void Object(int type,int width,int height)
        {
            Main.tile=new Tile[100,100];
            for(int x=0;x<width;x++) for(int y=0;y<height;y++) Main.tile[5+x,5+y]=new Tile {HasTile=true,TileType=(ushort)type,TileFrameX=(short)(x*18),TileFrameY=(short)(y*18)};
        }
        Object(TileID.Containers,2,2); p.chest=-1; intent=Intent("interact_tile",6,6);
        Check(host.Validate(0,intent)==null,"Server multi-cell chest validation");
        Check(host.Execute(0,intent,new(31,0,5,5,0),10).Status=="completed" && p.chest==2,"Native chest-open handler changes server player chest");
        intent=Intent("interact_tile",6,6); Check(host.Execute(0,intent,new(33,-1,0,0,0),10).Status=="completed" && p.chest==-1,"Chest close confirmed independently");
        Chest.Owner=1; Check(host.Validate(0,Intent("interact_tile",6,6))=="server_chest_in_use","Another player owns chest"); Chest.Owner=-1;
        Chest.Locked=true; Check(host.Validate(0,Intent("interact_tile",6,6))=="server_missing_or_locked_chest","Locked chest not unlocked by network envelope"); Chest.Locked=false;
        Object(TileID.Switches,1,1); intent=Intent("interact_tile");
        Check(host.Execute(0,intent,new(59,0,5,5,0),10).Status=="completed","Native switch frame confirmed");
        Object(TileID.Lever,2,2); intent=Intent("interact_tile",6,6);
        Check(host.Execute(0,intent,new(59,0,6,6,0),10).Status=="completed","Native lever all four frames confirmed");
        Object(TileID.ClosedDoor,1,3); intent=Intent("interact_tile",5,6);
        Check(host.Execute(0,intent,new(19,0,5,6,1),10).Status=="completed","Door native handler state transition confirmed");
        Check(!host.Allowed(intent,new(19,0,6,6,1)),"Door envelope cannot target adjacent door");
        Main.netMode=0; Main.tile=new Tile[100,100];
    }
}
