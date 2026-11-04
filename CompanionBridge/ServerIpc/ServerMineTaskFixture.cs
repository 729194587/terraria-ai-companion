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
    internal static bool Enabled=>ServerAiActionFixture.Enabled&&Program.LaunchParameters.ContainsKey("-companiontasktest");
    internal static void Tick(Player p)
    {
        if(!Enabled)return;
        string request=Path.Combine(Main.SavePath,"mine-task-request.json");if(!File.Exists(request))return;
        using var document=JsonDocument.Parse(File.ReadAllText(request));string kind=document.RootElement.GetProperty("caseId").GetString();File.Delete(request);
        if(ActorControl.Running||ActorMining.Running||ActorItemUse.Running)throw new InvalidOperationException("Cannot reseed running native action");
        int left=Main.spawnTileX,floor=Main.spawnTileY+8,x=left+10,y=floor-2;
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
            p.inventory[0]=new Item(ItemID.WoodenSword);p.inventory[1]=new Item(ItemID.CopperPickaxe);p.selectedItem=0;
            if(kind=="near")x=left+2;
            var ore=Main.tile[x,y];ore.HasTile=true;ore.TileType=TileID.Copper;
            if(kind=="blocked")
                for(int tx=left+4;tx<=left+4;tx++)for(int ty=floor-12;ty<floor;ty++){var t=Main.tile[tx,ty];t.HasTile=true;t.TileType=TileID.Stone;}
            if(kind=="no_tool")p.inventory[1]=new Item();
        }
        NetworkStateSync.Full(p);
        File.WriteAllText(Path.Combine(Main.SavePath,"mine-task-ready.json"),JsonSerializer.Serialize(new{caseId=kind,x,y,typeId=(int)TileID.Copper,world=ProbeSession.WorldSession,generation=ProbeSession.ActorGeneration,slot=p.whoAmI,gameUpdate=Main.GameUpdateCount}));
    }
}
