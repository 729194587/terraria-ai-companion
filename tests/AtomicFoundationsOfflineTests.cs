using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using CompanionBridge;
using CompanionBridge.Common.Systems;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;

// Actual query/validation code and protocol queue; Terraria storage/API substitutes.
public static class AtomicFoundationsOfflineTests
{
    static void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); }
    public static void Run()
    {
        var controls=CompanionBridge.CompanionBridge.Controls;
        if (controls.SessionId!=0) controls.EndSession(controls.SessionId);
        controls.Reset(); int session=controls.BeginSession(), id=0;
        Main.netMode=0; Main.gameMenu=false; Main.myPlayer=0;
        ObservationReader.WorldSessionId="foundations";
        var p=Main.player[0]=new Player {position=new(88,88)};
        JsonElement Query(string op, object args)
        {
            var request=ControlState.Parse(JsonSerializer.Serialize(new {v=1,id=++id,cmd="query_atomic",operation=op,arguments=args}));
            var task=controls.Enqueue(session,request,Stopwatch.GetTimestamp());
            controls.Update(true,Stopwatch.GetTimestamp(),new(true,new(88,88),new(0,0),100,100),
                worldSessionId:ObservationReader.WorldSessionId,queryAtomic:AtomicOperations.Query);
            Check(task.Result.Ok,"Actual query protocol dispatch succeeds");
            var envelope=JsonSerializer.SerializeToElement(task.Result.Data);
            Check(envelope.GetProperty("worldSessionId").GetString()=="foundations" && envelope.TryGetProperty("gameUpdate",out _),"Query freshness identity");
            return envelope.GetProperty("data");
        }
        void Invalid(string op, object args)
        {
            bool rejected=false;
            try { Query(op,args); } catch(Exception e) when(e is FormatException or InvalidOperationException) {rejected=true;}
            Check(rejected,"Strict schema rejects invalid "+op);
        }
        ContentSamples.ItemsByType.Clear();
        ContentSamples.ItemsByType[2]=new Item {type=2,DisplayName="Stone",createTile=2};
        ContentSamples.ItemsByType[3]=new Item {type=3,DisplayName="Stone"};
        var r=Query("resolve_content",new {kind="item",query="Stone"});
        Check(r.GetProperty("status").GetString()=="ambiguous" && r.GetProperty("matchedCount").GetInt32()==2,"Duplicate localized names never silently choose");
        r=Query("resolve_content",new {kind="item",query="terraria/type2"});
        Check(r.GetProperty("status").GetString()=="ok" && r.GetProperty("candidates")[0].GetProperty("creates").GetProperty("tileType").GetInt32()==2,"Case-insensitive stable key with placement type");
        r=Query("resolve_content",new {kind="tile",query="Type",mode="search",maxResults=1});
        Check(r.GetProperty("truncated").GetBoolean() && r.GetProperty("matchedCount").GetInt32()==1000,"Bounded registry search with full matched count");
        Check(Query("resolve_content",new {kind="wall",query="Terraria/Type1"}).GetProperty("candidates")[0].GetProperty("typeId").GetInt32()==1,"Wall registry resolution");
        Check(Query("resolve_content",new {kind="tile",query="absent"}).GetProperty("status").GetString()=="not_found","Unknown content distinct from absence in world");
        Invalid("resolve_content",new {kind="npc",query="x"});
        Invalid("resolve_content",new {kind="tile",query=""});
        Invalid("resolve_content",new {kind="tile",query="Type",maxResults=33});
        Invalid("scan_entities",new {category="all",radius=100,typeIds=new[]{1}});
        Invalid("scan_entities",new {category="item",radius=100,npcFilter="critter"});
        Invalid("scan_entities",new {category="npc",radius=100,typeIds=new[]{1,1}});
        Invalid("scan_entities",new {category="npc",radius=100,npcFilter="hostile"});
        Invalid("scan_entities",new {category="npc",radius=100,maxResults=129});

        Main.tile=new Tile[100,100];
        Main.tile[4,4]=new Tile {HasTile=true,TileType=2};
        Main.tile[5,5]=new Tile {HasTile=true,TileType=2,WallType=1};
        r=Query("scan_tiles",new {minX=4,minY=4,maxX=5,maxY=5,tileTypes=new[]{2},maxResults=1});
        Check(r.GetProperty("items")[0].GetProperty("x").GetInt32()==5 && r.GetProperty("matchedCount").GetInt32()==2,"Nearest tile selected before limit, not row-major");
        p.inventory[0]=new Item {pick=35};
        r=Query("inspect_tile",new {x=5,y=5});
        Check(r.GetProperty("preflight").GetProperty("withinConservativeReach").GetBoolean()
            && r.GetProperty("preflight").GetProperty("checks").GetProperty("pickSelected").GetBoolean()
            && r.GetProperty("preflightStatus").GetString()=="partial_checks_only","Preflight exposes checks without authorizing action");
        Check(r.GetProperty("content").GetString()=="Terraria/Type2" && r.GetProperty("wallContent").GetString()=="Terraria/Type1","Target content identity");
        Main.netMode=1; Main.sectionManager.Loaded=false;
        Check(Query("inspect_tile",new {x=5,y=5}).GetProperty("preflight").ValueKind==JsonValueKind.Null,"Unknown target has no invented operability");
        Main.netMode=0; Main.sectionManager.Loaded=true;

        var oldNpcs=Main.npc; var oldItems=Main.item;
        Main.npc=new[] { new NPC {type=2,whoAmI=0,position=new(150,88),damage=10},
            new NPC {type=3,whoAmI=1,position=new(100,88),friendly=true},
            new NPC {type=4,whoAmI=2,position=new(90,88),ModNPC=new object(),damage=10} };
        Main.item=new[] {new Item {type=2,position=new(89,88)}};
        r=Query("scan_entities",new {category="all",radius=200,maxResults=1});
        Check(r.GetProperty("items")[0].GetProperty("kind").GetString()=="item","Nearest mixed entity is not lost to NPC-first append order");
        r=Query("scan_entities",new {category="npc",radius=200,typeIds=new[]{2},npcFilter="hostile_creature",maxResults=1});
        Check(r.GetProperty("matchedCount").GetInt32()==1 && r.GetProperty("items")[0].GetProperty("details").GetProperty("entityRef").GetString().StartsWith("foundations:npc:0:"),"Filter before truncate retains actionable identity");
        r=Query("scan_entities",new {category="npc",radius=200,npcFilter="unknown"});
        Check(r.GetProperty("matchedCount").GetInt32()==1 && r.GetProperty("items")[0].GetProperty("typeId").GetInt32()==4,"Mod nonfriendly flag does not fabricate hostility");
        Main.npc=oldNpcs; Main.item=oldItems;

        object empty=new {slot=0,empty=true};
        object held=new {slot=1,empty=false,typeId=2,stack=5,use=new {pick=35}};
        ObservationReader.PlayerSnapshot=new {life=75,mana=20,selectedSlot=1,selectedHotbarKey=2,
            inventory=new[] {empty,held}, equipment=new[] {empty,held}, miscEquipment=new[] {empty},
            dyes=new[] {held},miscDyes=new[] {held},
            buffs=new[] {new {typeId=0,timeTicks=0},new {typeId=1,timeTicks=100}}, movement=new {gravDir=1} };
        r=Query("get_player_status",new {});
        Check(!r.TryGetProperty("inventory",out _) && !r.TryGetProperty("dyes",out _)
            && r.GetProperty("inventorySummary").GetProperty("occupiedSlots").GetInt32()==1
            && r.GetProperty("selectedItem").GetProperty("typeId").GetInt32()==2,"Compact status omits backpack but retains selected tool");
        Check(r.GetProperty("equipment").GetArrayLength()==1 && r.GetProperty("buffs").GetArrayLength()==1,"Nonempty equipment and active buffs");
        ObservationReader.PlayerSnapshot=null;
        Check(Query("get_world_state",new {}).GetProperty("progression").TryGetProperty("hardMode",out _),"World progression explicitly scoped");
        Check(controls.AtomicAction==null && !p.controlUseItem,"All foundation queries leave action and input untouched");
        controls.EndSession(session); controls.Reset(); ObservationReader.WorldSessionId="hook-test";
        Console.WriteLine("PASS: atomic foundations actual query protocol/schema: content ambiguity/limits, compact status, nearest/filter before truncation, unknown preflight, no input mutation (offline storage substitutes)");
    }
}
