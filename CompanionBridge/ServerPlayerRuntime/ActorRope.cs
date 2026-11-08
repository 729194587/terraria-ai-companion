using Terraria;
using Terraria.ID;

namespace VirtualPlayerEngineProbe;

internal static class ActorRope
{
    internal const int MaxExtension=16;
    internal sealed record Target(int X,int Y,int EndY);

    internal static bool Supported(Item item) => !item.IsAir&&item.stack>0&&item.ModItem==null
        &&item.consumable&&!item.channel&&item.createWall<0&&item.tileWand<0&&item.placeStyle==0
        &&(item.type,item.createTile) is ((ItemID.Rope,TileID.Rope) or (ItemID.VineRope,TileID.VineRope)
            or (ItemID.SilkRope,TileID.SilkRope) or (ItemID.WebRope,TileID.WebRope));

    internal static int FindSlot(Player p)
    {
        if(p.selectedItem is >=0 and <10&&Supported(p.HeldItem))return p.selectedItem;
        for(int slot=0;slot<10;slot++)if(Supported(p.inventory[slot]))return slot;
        return -1;
    }

    internal static bool IsRope(Tile tile)=>tile.HasUnactuatedTile
        &&tile.TileType is TileID.Rope or TileID.VineRope or TileID.SilkRope or TileID.WebRope;

    // PlaceThing_Tiles checks its requested target before retargeting. Apply
    // the same native rectangle to the final cell as a server action bound.
    private static bool InReach(Player p,Item item,int x,int y)
        =>x>=p.position.X/16-Player.tileRangeX-item.tileBoost-p.blockRange
        &&x<=(p.position.X+p.width)/16+Player.tileRangeX+item.tileBoost-1+p.blockRange
        &&y>=p.position.Y/16-Player.tileRangeY-item.tileBoost-p.blockRange
        &&y<=(p.position.Y+p.height)/16+Player.tileRangeY+item.tileBoost-2+p.blockRange;

    internal static string Resolve(Player p,Item item,int x,int y,out Target target)
    {
        target=null;
        if(!WorldGen.InWorld(x,y,5))return "invalid_target";
        if(!Supported(item))return "rope_material_unavailable";
        if(!InReach(p,item,x,y))return "rope_out_of_range";
        int end=y;
        while(Main.tile[x,end].HasTile)
        {
            var segment=Main.tile[x,end];
            if(!IsRope(segment))return end==y?"rope_target_occupied":"rope_extension_blocked";
            if(segment.LiquidAmount>0&&segment.LiquidType!=LiquidID.Water)return "rope_unsafe_liquid";
            if(end-y>=MaxExtension||!WorldGen.InWorld(x,end+1,5))return "rope_extension_limit";
            end++;
        }
        var tile=Main.tile[x,end];
        if(tile.LiquidAmount>0&&tile.LiquidType!=LiquidID.Water)return "rope_unsafe_liquid";
        if(!InReach(p,item,x,end))return "rope_out_of_range";
        target=new(x,y,end);
        return null;
    }
}
