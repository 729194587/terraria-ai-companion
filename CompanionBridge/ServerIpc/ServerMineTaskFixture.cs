using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Terraria;
using Terraria.ID;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

internal static class ServerMineTaskFixture
{
    private static bool expireMining, obscureMining, replaceBorrowedPick;
    internal static bool Enabled=>ServerAiActionFixture.Enabled&&Program.LaunchParameters.ContainsKey("-companiontasktest");
    internal static void Tick(Player p)
    {
        if(!Enabled)return;
        if(replaceBorrowedPick && ActorMining.Running && ActorMining.Current.Strikes>0)
        {
            var mining=ActorMining.Current;
            if(mining.SourceSlot!=20 || !ReferenceEquals(p.HeldItem,mining.Item))
                throw new InvalidOperationException("Expected the borrowed bag pick in hand.");
            p.inventory[mining.Slot]=mining.Item.Clone();
            replaceBorrowedPick=false;
        }
        if(expireMining&&ActorMining.Running&&ActorMining.Current.Strikes>0)
        {
            ActorMining.Current.Ticks=600;
            expireMining=false;
        }
        if(obscureMining&&ActorMining.Running&&ActorMining.Current.Strikes>0)
        {
            ActorMining.Stop(p,ProbeSession.WorldSession,ProbeSession.ActorGeneration);
            ActorMining.Current.Phase=ActionPhase.Stale; // Isolated lost-effect observation.
            obscureMining=false;
        }
        string request=Path.Combine(Main.SavePath,"mine-task-request.json");if(!File.Exists(request))return;
        string requestBody;
        try
        {
            requestBody=File.ReadAllText(request);
            File.Delete(request);
        }
        catch(IOException)
        {
            // Windows can still hold the file briefly after the atomic rename.
            return;
        }
        using var document=JsonDocument.Parse(requestBody);
        string kind=document.RootElement.GetProperty("caseId").GetString();
        if(ActorControl.Running||ActorMining.Running||ActorItemUse.Running)throw new InvalidOperationException("Cannot reseed running native action");
        int left=Main.spawnTileX,floor=Main.spawnTileY+8,x=left+10,y=floor-2;
        int? destinationX=null,destinationY=null;
        if(kind is "change" or "replace_same")
        {
            if(kind=="change")Main.tile[x,y].TileType=TileID.Stone;
            else
            {
                short fx=Main.tile[x,y].TileFrameX,fy=Main.tile[x,y].TileFrameY;
                WorldGen.KillTile(x,y,false,false,true);
                if(!WorldGen.PlaceTile(x,y,TileID.Copper,false,true))throw new InvalidOperationException("Native same-type fixture replacement failed");
                Main.tile[x,y].TileFrameX=fx;Main.tile[x,y].TileFrameY=fy;
            }
        }
        else
        {
            for(int tx=left-24;tx<=left+24;tx++)for(int ty=floor-20;ty<=floor+16;ty++)
            {var t=Main.tile[tx,ty];t.ClearEverything();if(ty>=floor){t.HasTile=true;t.TileType=TileID.Dirt;}}
            foreach(var drop in Main.item)drop.active=false;
            Array.Clear(p.buffType);Array.Clear(p.buffTime); // Isolate from preceding native apple regression.
            p.jump=0;p.releaseJump=true;
            p.position=new(left*16,floor*16-p.height);p.velocity=Microsoft.Xna.Framework.Vector2.Zero;
            for(int i=0;i<p.inventory.Length;i++)p.inventory[i]=new Item();
            p.inventory[0]=new Item(ItemID.CopperShortsword);p.inventory[1]=new Item(ItemID.CopperPickaxe);p.selectedItem=0;
            if(kind is "near" or "hotbar" or "bag" or "underpowered" or "workbench" or "workbench_task" or "cutout" or "deadline")x=left+2;
            expireMining=kind is "deadline" or "opening_deadline";
            obscureMining=kind=="opening_unknown";
            replaceBorrowedPick=kind=="opening_bag_replaced";
            if(kind is "bag" or "cutout" or "deadline" or "opening_bag" or "opening_bag_replaced")
                Utils.Swap(ref p.inventory[1],ref p.inventory[20]);
            if(kind=="cutout")
            {
                // A two-tile opening in the surface after removing the two cover tiles.
                for(int tx=left;tx<left+2;tx++)
                {
                    Main.tile[tx,floor].ClearTile();
                    Main.tile[tx,floor+1].ClearTile();
                }
                p.position=new(left*16+6,(floor+2)*16-p.height);
                y=floor+1;
            }
            if(kind is "workbench" or "workbench_task")y=floor-1;
            if(kind=="reach_corner")
            {
                // In copper pick's native rectangle, over 80px from the actor centre.
                x=left-4;
                y=floor-5;
            }
            if(kind is "opening_across" or "opening_bag" or "opening_bag_replaced")
            {
                x=left+7;y=floor-2;
                for(int tx=left+2;tx<=left+17;tx++)
                    for(int ty=floor-9;ty<floor;ty++)
                    {var rock=Main.tile[tx,ty];rock.HasTile=true;rock.TileType=TileID.Stone;}
            }
            if(kind is "opening_down" or "opening_probe" or "opening_hazard" or "opening_hard" or "opening_unsafe" or "opening_deadline" or "opening_unknown" or "opening_cancel")
            {
                x=left;y=floor+4;
                if(kind=="opening_probe")
                {
                    // ore8: actor left at column +3.4px; copper one column left,
                    // three rows below feet. Rebuild that layout in copied rock.
                    p.position.X+=3.402f;x=left-1;y=floor+3;
                }
                for(int tx=left-4;tx<=left+5;tx++)
                    for(int ty=floor;ty<=floor+8;ty++)Main.tile[tx,ty].TileType=TileID.Stone;
                if(kind=="opening_hard")
                    for(int tx=left;tx<=left+1;tx++)Main.tile[tx,floor].TileType=TileID.Obsidian;
                if(kind=="opening_unsafe")
                    for(int tx=left;tx<=left+1;tx++)
                        for(int ty=floor+1;ty<=floor+3;ty++)Main.tile[tx,ty].ClearTile();
                if(kind=="opening_hazard")
                {
                    var lava=Main.tile[left,floor+1];lava.ClearTile();lava.LiquidType=LiquidID.Lava;lava.LiquidAmount=255;
                }
            }
            var ore=Main.tile[x,y];ore.HasTile=true;ore.TileType=kind=="underpowered"?TileID.Obsidian:TileID.Copper;
            if(kind is "workbench" or "workbench_task")
            {
                for(int col=0;col<2;col++)
                {
                    var part=Main.tile[x+col,y];part.HasTile=true;part.TileType=TileID.WorkBenches;
                    part.TileFrameX=(short)(col*18);part.TileFrameY=0;
                }
                x++; // Mine the non-origin segment.
            }
            if(kind=="blocked")
                for(int tx=left+4;tx<=left+4;tx++)for(int ty=floor-12;ty<floor;ty++){var t=Main.tile[tx,ty];t.HasTile=true;t.TileType=TileID.Obsidian;}
            if(kind=="no_tool")p.inventory[1]=new Item();
            if(kind.StartsWith("navigation_",StringComparison.Ordinal))
            {
                ore.ClearTile();
                destinationX=(left+5)*16+8;
                destinationY=floor*16;
                if(kind is "navigation_single" or "navigation_single_platform" or "navigation_half" or "navigation_slope")
                {
                    for(int tx=left-2;tx<=left+5;tx++)Main.tile[tx,floor].ClearTile();
                    var support=Main.tile[left+1,floor];
                    support.HasTile=true;
                    support.TileType=kind=="navigation_single_platform"?TileID.Platforms:TileID.Stone;
                    support.IsHalfBlock=kind=="navigation_half";
                    if(kind=="navigation_slope")support.Slope=SlopeType.SlopeDownRight;
                    int feetOffset=kind=="navigation_half"?8:kind=="navigation_slope"?6:0;
                    int playerX=kind=="navigation_slope"?(left+1)*16+6:(left+2)*16-2;
                    destinationX=playerX+10;
                    destinationY=floor*16+feetOffset;
                    p.position=new(playerX,destinationY.Value-p.height-4);
                }
                if(kind=="navigation_trip")
                {
                    destinationX=(left+20)*16+8;
                    foreach(int column in new[]{3,11})
                        for(int ty=floor-2;ty<floor;ty++)
                        {var rock=Main.tile[left+column,ty];rock.HasTile=true;rock.TileType=TileID.Stone;}
                }
                if(kind=="navigation_long_dig")
                {
                    destinationX=(left+11)*16+8;
                    for(int tx=left+2;tx<=left+10;tx++)
                        for(int ty=floor-10;ty<floor;ty++)
                        {var rock=Main.tile[tx,ty];rock.HasTile=true;rock.TileType=TileID.Stone;}
                }
                if(kind=="navigation_long_place")
                {
                    for(int tx=left+1;tx<=left+20;tx++)
                        for(int ty=floor;ty<=floor+8;ty++)Main.tile[tx,ty].ClearTile();
                    destinationX=(left+11)*16+8;
                    p.inventory[2]=new Item(ItemID.WoodPlatform){stack=40};
                }
                if(kind is "navigation_dig" or "navigation_jump")
                    for(int tx=left+2;tx<=left+3;tx++)
                        for(int ty=floor-(kind=="navigation_dig"?10:2);ty<floor;ty++)
                        {var rock=Main.tile[tx,ty];rock.HasTile=true;rock.TileType=TileID.Stone;}
                // Isolate movement acceptance from the independently permitted
                // terrain-modification choice in these two movement fixtures.
                if(kind is "navigation_jump" or "navigation_trip")p.inventory[1]=new Item();
                if(kind=="navigation_down")
                {
                    destinationX=left*16+10;
                    destinationY=(floor+1)*16;
                }
                if(kind=="navigation_drop")
                {
                    for(int tx=left+3;tx<=left+22;tx++)Main.tile[tx,floor].ClearTile();
                    destinationY=(floor+1)*16;
                }
                if(kind=="navigation_place")
                {
                    for(int tx=left+1;tx<=left+20;tx++)
                        for(int ty=floor;ty<=floor+8;ty++)Main.tile[tx,ty].ClearTile();
                    destinationX=(left+2)*16+8;
                    p.inventory[2]=new Item(ItemID.WoodPlatform){stack=5};
                }
            }
        }
        NetworkStateSync.Full(p);
        File.WriteAllText(Path.Combine(Main.SavePath,"mine-task-ready.json"),JsonSerializer.Serialize(new{caseId=kind,x,y,destinationX,destinationY,typeId=(int)Main.tile[x,y].TileType,world=ProbeSession.WorldSession,generation=ProbeSession.ActorGeneration,slot=p.whoAmI,gameUpdate=Main.GameUpdateCount}));
    }
}
