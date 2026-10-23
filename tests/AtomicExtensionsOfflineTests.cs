using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using CompanionBridge;
using CompanionBridge.Common.Systems;
using Terraria;
using Terraria.ID;
using Terraria.GameContent;

// Runs the production adapter against storage/API substitutes, not Terraria ItemCheck.
public static class AtomicExtensionsOfflineTests
{
    static void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); }
    static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    public static string ProjectInventoryJson(string input)
        => JsonSerializer.Serialize(AtomicOperations.FullInventory(JsonDocument.Parse(input).RootElement));

    public static void Run()
    {
        var controls=CompanionBridge.CompanionBridge.Controls;
        if(controls.SessionId!=0) controls.EndSession(controls.SessionId);
        controls.Reset(); int session=controls.BeginSession(), id=0;
        Main.netMode=0; Main.gameMenu=false; Main.myPlayer=0;
        ObservationReader.WorldSessionId="extensions";
        Main.tile=new Tile[100,100]; Main.tileSolid[2]=true;
        var player=Main.player[0]=new Player { position=new(88,88) };
        Item Hammer() => new() { hammer=50, pick=0 };
        Item Wall(int stack=10) => new() { createWall=1, consumable=true, stack=stack };
        MovementSnapshot Start(string op,Item item,int x=5,int y=5)
        {
            AtomicOperations.RestoreAim(); AtomicOperations.After(player); controls.Reset();
            player.itemAnimation=player.itemTime=0; player.selectedItem=0; player.inventory[0]=item;
            var request=ControlState.Parse(JsonSerializer.Serialize(new { v=1,id=++id,cmd="start_atomic",operation=op,
                arguments=new {x,y},expectedGeneration=controls.Generation }));
            var task=controls.Enqueue(session,request,Stopwatch.GetTimestamp());
            controls.Update(true,Stopwatch.GetTimestamp(),new(true,new(88,88),new(0,0),100,100),worldSessionId:ObservationReader.WorldSessionId);
            Check(task.Result.Ok,"Extension start accepted"); AtomicOperations.Before(player,request); return controls.AtomicAction;
        }
        void Tick() => AtomicOperations.Before(player,controls.Input.Atomic);
        JsonElement Query(string op,object args)
        {
            var request=ControlState.Parse(JsonSerializer.Serialize(new {v=1,id=++id,cmd="query_atomic",operation=op,arguments=args}));
            var task=controls.Enqueue(session,request,Stopwatch.GetTimestamp());
            controls.Update(true,Stopwatch.GetTimestamp(),new(true,new(88,88),new(0,0),100,100),
                worldSessionId:ObservationReader.WorldSessionId,queryAtomic:AtomicOperations.Query);
            Check(task.Result.Ok,"Actual query protocol dispatch succeeds"); return Json(task.Result.Data);
        }
        var bounds=new {minX=4,minY=4,maxX=6,maxY=6,tileTypes=new[]{2},maxResults=1};
        var query=Query("scan_tiles",bounds).GetProperty("data");
        Check(query.GetProperty("status").GetString()=="not_found" && query.GetProperty("returnedCount").GetInt32()==0,"Empty scan explicitly not found");
        Main.tile[4,4]=Main.tile[6,6]=new Tile {HasTile=true,TileType=2};
        query=Query("scan_tiles",bounds).GetProperty("data");
        Check(query.GetProperty("matchedCount").GetInt32()==2 && query.GetProperty("returnedCount").GetInt32()==1 && query.GetProperty("truncated").GetBoolean(),"Bounded scan exact matches and truncation");
        Check(query.GetProperty("items")[0].GetProperty("x").GetInt32()==4,"Deterministic row-major scan coordinates");
        Check(!Query("scan_tiles",new {minX=99,minY=0,maxX=100,maxY=1,tileTypes=new[]{2}}).GetProperty("available").GetBoolean(),"World bounds checked on game thread");
        Check(Query("scan_tiles",new {minX=0,minY=0,maxX=1,maxY=1,tileTypes=new[]{1000}}).GetProperty("reason").GetString()=="invalid_tile_type","Unknown tile ID rejected");
        Main.netMode=1; Main.sectionManager.Loaded=false;
        query=Query("scan_tiles",bounds).GetProperty("data");
        Check(query.GetProperty("unknownCount").GetInt32()==9 && query.GetProperty("status").GetString()=="partial_unloaded","Unloaded scan never claims empty");
        Main.netMode=0; Main.sectionManager.Loaded=true;
        ContentSamples.ItemsByType.Clear(); ContentSamples.ItemsByType.Add(2,new Item {type=2,DisplayName="Stone"});
        player.inventory[1]=new Item {type=2,stack=3}; player.inventory[4]=new Item {type=2,stack=7};
        query=Query("inspect_inventory",new {mode="item",item="Stone"}).GetProperty("data");
        Check(query.GetProperty("count").GetInt32()==10 && query.GetProperty("slots").GetArrayLength()==2 && !query.TryGetProperty("inventory",out _),"Item count and slots are compact");
        Check(Query("inspect_inventory",new {mode="item",item="Terraria/Type2"}).GetProperty("data").GetProperty("count").GetInt32()==10,"Content key resolves independently of display name");
        query=Query("inspect_inventory",new {}).GetProperty("data");
        Check(query.GetProperty("inventory").GetArrayLength()==1 && query.GetProperty("selectedSlot").GetInt32()==0,"Default selected-only response");
        Check(Query("inspect_inventory",new {mode="hotbar"}).GetProperty("data").GetProperty("inventory").GetArrayLength()==10,"Hotbar explicit");
        object Empty(int slot) => new { slot, empty=true, typeId=0, stack=0, name="", use=new {damage=0} };
        object Filled(int slot) => new { slot, empty=false, typeId=5000, contentKey="Fixture/ModPick",
            name="Mod pick", stack=7, hotbarKey=HotbarSlots.KeyForInventorySlot(slot), prefix=3, favorited=true,
            use=new {pick=100,axe=20,hammer=30,damage=40,createTile=2,createWall=1,
                useStyle=1,useTime=10,useAnimation=10,consumable=true,ammo=0,shoot=0},
            customState="unknown; custom ModItem data not serialized" };
        var slots = new object[] { Empty(0), Filled(1), Filled(9) };
        ObservationReader.PlayerSnapshot = new { selectedSlot=9, selectedHotbarKey=0,
            hotbarKeyBindings=Enumerable.Range(0,10).Select(slot => new { key=HotbarSlots.KeyForInventorySlot(slot), inventorySlot=slot }).ToArray(),
            inventory=slots, equipment=slots, dyes=slots, miscEquipment=slots, miscDyes=slots,
            buffs=new[] { new {slot=0,typeId=0,timeTicks=0,contentKey=(string)null},
                new {slot=1,typeId=5000,timeTicks=600,contentKey="Fixture/ModBuff"},
                new {slot=2,typeId=2,timeTicks=0,contentKey="Terraria/Expired"},
                new {slot=3,typeId=3,timeTicks=20,contentKey="Terraria/Active"} } };
        var fullBefore=Json(ObservationReader.PlayerSnapshot);
        query=Query("inspect_inventory",new {mode="full"}).GetProperty("data");
        foreach(string name in new[] {"inventory","equipment","dyes","miscEquipment","miscDyes"})
        {
            var items=query.GetProperty(name);
            Check(items.GetArrayLength()==3 && items[0].EnumerateObject().Count()==2
                && items[0].GetProperty("slot").GetInt32()==0 && items[0].GetProperty("empty").GetBoolean(),"All empty slots compact with explicit indices");
            Check(items[1].GetRawText()==fullBefore.GetProperty(name)[1].GetRawText()
                && items[2].GetRawText()==fullBefore.GetProperty(name)[2].GetRawText(),"Nonempty Mod item identity, count and all action attributes retained");
        }
        Check(query.GetProperty("selectedHotbarKey").GetInt32()==0
            && query.GetProperty("hotbarKeyBindings")[9].GetProperty("key").GetInt32()==0
            && query.GetProperty("inventory")[1].GetProperty("hotbarKey").GetInt32()==2,"Hotbar mapping preserved");
        Check(query.GetProperty("buffs").GetArrayLength()==2
            && query.GetProperty("buffs")[0].GetRawText()==fullBefore.GetProperty("buffs")[1].GetRawText()
            && query.GetProperty("buffs")[1].GetRawText()==fullBefore.GetProperty("buffs")[3].GetRawText(),"All active buffs retain slot/type/content key/duration; inactive omitted");
        Check(query.GetProperty("buffScope").GetString().Contains("Complete active")
            && query.GetProperty("customState").GetString().Contains("unknown"),"Coverage and unknown custom state explicit");
        Check(Json(ObservationReader.PlayerSnapshot).GetRawText()==fullBefore.GetRawText(),"Shared full snapshot unchanged");
        ObservationReader.PlayerSnapshot=null;
        player.inventory[1].stack=player.inventory[4].stack=0;
        Check(Query("inspect_inventory",new {mode="item",item="Stone"}).GetProperty("data").GetProperty("count").GetInt32()==0,"Known item absent reports zero");
        Check(Query("inspect_inventory",new {mode="item",item="NoSuchItem"}).GetProperty("data").GetProperty("status").GetString()=="unknown_item","Unknown name differs from zero");
        ContentSamples.ItemsByType.Add(3,new Item {type=3,DisplayName="Stone"});
        Check(Query("inspect_inventory",new {mode="item",item="Stone"}).GetProperty("data").GetProperty("status").GetString()=="ambiguous_item","Duplicate localized name does not silently choose");

        Main.tile[5,5]=new Tile {WallType=1};
        Check(Start("mine_wall",Hammer()).Status=="running" && player.controlUseItem,"Wall starts native input and waits");
        AtomicOperations.After(player); Check(!player.controlUseItem,"Wall input released each update");
        Main.tile[5,5].WallType=0; Tick(); Check(controls.AtomicAction.Completed,"Actual wall removal completes");
        Check(Start("mine_wall",Hammer()).Reason=="target_wall_missing","Empty wall distinct failure");
        Main.tile[5,5]=new Tile {WallType=1}; WorldGen.WallSmashable=false;
        Check(Start("mine_wall",Hammer()).Reason=="wall_not_smashable_by_native_rules","Native wall permission refusal"); WorldGen.WallSmashable=true;
        Main.tile[5,5].HasTile=true;
        Check(Start("mine_wall",Hammer()).Reason.Contains("foreground"),"Wall never pounds foreground"); Main.tile[5,5].HasTile=false;
        Check(Start("mine_wall",new Item {hammer=50,pick=50}).Reason.Contains("pure_hammer"),"Combined tools cannot accidentally mine foreground");
        Start("mine_wall",Hammer()); Main.tile[5,5].WallType=2; Tick(); Check(controls.AtomicAction.Reason=="target_wall_replaced","Replaced wall invalidates target");
        Check(Start("mine_wall",Hammer(),20,20).Status=="failed","Distant/empty wall fails");
        Main.netMode=1; Check(Start("mine_wall",Hammer()).Reason.Contains("multiplayer"),"No unsupported network claim"); Main.netMode=0;

        Main.tile[5,5]=new Tile();
        Start("place_wall",Wall(1)); Check(controls.AtomicAction.Status=="running","Placement ACK/input not completion");
        Main.tile[5,5].WallType=1; player.inventory[0].stack=0; Tick(); Check(controls.AtomicAction.Completed,"Last wall item consumption plus expected wall verified");
        Main.tile[5,5]=new Tile(); Start("place_wall",Wall()); Main.tile[5,5].WallType=1; Tick(); Check(controls.AtomicAction.Status=="failed","Wall appears without consumption fails");
        Main.tile[5,5]=new Tile(); Start("place_wall",Wall()); Main.tile[5,5].WallType=2; player.inventory[0].stack--; Tick(); Check(controls.AtomicAction.Status=="failed","Wrong wall with consumption fails");
        Main.tile[5,5]=new Tile(); Check(Start("place_wall",new Item()).Status=="failed","No wall item rejected");
        Check(Start("place_wall",Wall(0)).Reason=="selected_item_empty","Exhausted wall rejected");
        Main.tile[3,5].WallType=Main.tile[4,4].WallType=Main.tile[4,6].WallType=1;
        Check(Start("place_wall",Wall()).Reason=="unsupported_native_neighbour_wall_autofill","Reject native neighbouring-hole fill before spending extra walls");
        Main.tile[3,5].WallType=Main.tile[4,4].WallType=Main.tile[4,6].WallType=0;
        player.autoPaint=true; Check(Start("place_wall",Wall()).Reason=="unsupported_wall_auto_paint","Auto paint cannot spend unrequested resources"); player.autoPaint=false;
        Start("place_wall",Wall()); AtomicOperations.Aim(player); controls.RevokeControl(); AtomicOperations.RestoreAim(); AtomicOperations.Aim(player);
        Check(!player.controlUseItem && controls.AtomicAction.Status=="cancelled","Placement cancellation releases input");

        Main.tile[5,5]=new Tile {HasTile=true,TileType=2};
        Start("hammer_tile",Hammer()); AtomicOperations.After(player); Check(controls.AtomicAction.Status=="running","Input is not hammer success");
        Main.tile[5,5].TileFrameX++; Tick(); Check(controls.AtomicAction.Status=="running","Cosmetic frame alone cannot prove block shape change");
        Main.tile[5,5].IsHalfBlock=true; Tick(); Check(controls.AtomicAction.Completed,"Half block change verified");
        Start("hammer_tile",Hammer()); Main.tile[5,5].Slope=1; Tick(); Check(controls.AtomicAction.Completed,"Slope change verified");
        Start("hammer_tile",Hammer()); Main.tile[5,5].HasTile=false; Tick(); Check(controls.AtomicAction.Status=="failed","Tile destruction not hammer success");
        Main.tile[5,5]=new Tile {HasTile=true,TileType=2,WallType=1}; Check(Start("hammer_tile",Hammer()).Status=="failed","Hammer cannot damage background");
        Main.tile[5,5]=new Tile {HasTile=true,TileType=2}; Start("hammer_tile",Hammer());
        Main.netMode=1; Main.sectionManager.Loaded=false; Tick(); Check(controls.AtomicAction.Reason.Contains("unloaded"),"Target unload fails"); Main.netMode=0; Main.sectionManager.Loaded=true;
        Start("hammer_tile",Hammer()); ObservationReader.WorldSessionId="new-world"; Tick();
        Check(controls.AtomicAction.Status=="cancelled" && controls.AtomicAction.Reason=="world_session_changed" && !player.controlUseItem,"World switch cannot execute old action"); ObservationReader.WorldSessionId="extensions";

        void Object(int type,int width,int height)
        {
            Main.tile=new Tile[100,100];
            for(int x=0;x<width;x++) for(int y=0;y<height;y++) Main.tile[5+x,5+y]=new Tile {HasTile=true,TileType=(ushort)type,TileFrameX=(short)(x*18),TileFrameY=(short)(y*18)};
        }
        Object(TileID.Containers,2,2); player.chest=-1;
        Check(Start("interact_tile",new Item(),6,6).Completed && player.chest==2 && player.chestX==5 && player.chestY==5,"Chest bottom-right resolves native anchor and opens");
        Check(Start("interact_tile",new Item(),6,6).Completed && player.chest==-1,"Chest second interaction closes");
        Check(!player.releaseUseTile && !player.tileInteractAttempted,"Native interaction input gates restored immediately");
        Chest.Locked=true; Check(Start("interact_tile",new Item()).Reason=="locked_chest_unsupported","Locked chest does not consume keys"); Chest.Locked=false;
        Chest.Blocked=true; Check(Start("interact_tile",new Item()).Status=="failed","Native chest no-op cannot succeed"); Chest.Blocked=false;
        Object(TileID.Switches,1,1); Check(Start("interact_tile",new Item()).Completed && Main.tile[5,5].TileFrameY!=0,"Switch uses wiring and actual frame change");
        Object(TileID.Lever,2,2); Check(Start("interact_tile",new Item(),6,6).Completed,"Lever uses all four frames and clicked location");
        Wiring.Blocked=true; Check(Start("interact_tile",new Item(),6,6).Status=="failed","Wiring call without state change not success"); Wiring.Blocked=false;
        Main.tile[5,5].HasTile=false; Check(Start("interact_tile",new Item(),6,6).Reason=="invalid_interaction_footprint","Broken multi-tile footprint rejected");
        Object(TileID.ClosedDoor,1,3); Check(Start("interact_tile",new Item(),5,6).Completed,"Door interaction preserved from middle cell");
        Object(2,1,1); Check(Start("interact_tile",new Item()).Reason=="unsupported_interaction_type","Arbitrary tile never treated as door");
        Object(TileID.Switches,1,1); player.NativeInteractionRange=false;
        Check(Start("interact_tile",new Item()).Reason=="out_of_native_interaction_range","Native range check in addition to conservative reach"); player.NativeInteractionRange=true;
        Main.tile[20,20]=new Tile {WallType=1}; Check(Start("mine_wall",Hammer(),20,20).Reason.Contains("reach"),"Loaded distant wall is out of reach");
        Check(Start("place_wall",Wall(),99,99).Reason.Contains("out_of_world"),"World edge rejected");
        controls.EndSession(session); controls.Reset(); ObservationReader.WorldSessionId="hook-test";
        Console.WriteLine("PASS: atomic extensions real adapter/storage substitutes: scan limits/unknown, item resolution/count/slots, wall/hammer effect evidence, object footprints, cancel/world change; native game behavior not simulated");
    }
}
