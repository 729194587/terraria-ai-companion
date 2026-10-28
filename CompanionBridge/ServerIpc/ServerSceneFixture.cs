using System;
using System.IO;
using System.Text.Json;
using Terraria;
using Terraria.ID;

namespace CompanionBridge.ServerIpc;

// Only invoked by the existing isolated copied-world action fixture, never a
// normal server query or player profile. Native object growth/placement below.
internal static class ServerSceneFixture
{
    internal static void Prepare(Player p,int left,int floor)
    {
        for(int x=left-7;x<=left-1;x++)for(int y=floor-38;y<=floor;y++)
        {var t=Main.tile[x,y];t.ClearEverything();if(y==floor){t.HasTile=true;t.TileType=TileID.Grass;}}
        if(!WorldGen.GrowTree(left-4,floor))throw new InvalidOperationException("Scene native tree fixture failed");
        for(int x=left-10;x<=left-9;x++){var t=Main.tile[x,floor+1];t.HasTile=true;t.TileType=TileID.Copper;}
        for(int i=0;i<4;i++)
        {
            int x=left-17+i*3,y=floor+4;
            for(int dx=-1;dx<=1;dx++)for(int dy=-1;dy<=1;dy++)
            {var t=Main.tile[x+dx,y+dy];t.ClearEverything();t.HasTile=true;t.TileType=TileID.Dirt;}
            var pool=Main.tile[x,y];pool.ClearEverything();pool.LiquidType=i;pool.LiquidAmount=255;
        }
        for(int x=left+9;x<=left+15;x++)
        {var tile=Main.tile[x,floor-4];tile.ClearEverything();tile.HasTile=true;tile.TileType=TileID.Platforms;}
        void Object(int x,int type)
        {
            if(!TileObject.CanPlace(x,floor-1,(ushort)type,0,1,out var data)||!TileObject.Place(data))
                throw new InvalidOperationException("Scene native object fixture failed: "+type);
        }
        Object(left-14,TileID.WorkBenches);
        // A door needs both sill and lintel; build only its diagnostic frame.
        var lintel=Main.tile[left-19,floor-4];lintel.HasTile=true;lintel.TileType=TileID.Dirt;
        Object(left-19,TileID.ClosedDoor);
        int chest=WorldGen.PlaceChest(left-11,floor-1);
        if(chest<0)throw new InvalidOperationException("Scene native chest fixture failed");
        p.inventory[4].SetDefaults(ItemID.Wood);p.inventory[4].stack=8;
        File.WriteAllText(Path.Combine(Main.SavePath,"scene-fixture.json"),JsonSerializer.Serialize(new{left,floor,chest,
            pools=new[]{new{x=left-17,y=floor+4,type="water"},new{x=left-14,y=floor+4,type="lava"},new{x=left-11,y=floor+4,type="honey"},new{x=left-8,y=floor+4,type="shimmer"}},
            mode="native grown tree/placed furniture in explicitly isolated constructed scene; not natural world proof"}));
    }
}
