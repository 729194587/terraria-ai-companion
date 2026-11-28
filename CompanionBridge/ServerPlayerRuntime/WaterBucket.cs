using System.Linq;
using Terraria;
using Terraria.ID;

namespace VirtualPlayerEngineProbe;

// Evidence is sampled around a single natural ItemCheck, before the next liquid update.
internal sealed class WaterBucket(int x,int y,bool scoop)
{
    internal int X=>x;
    internal int Y=>y;
    internal int Source=>scoop?ItemID.EmptyBucket:ItemID.WaterBucket;
    internal bool Verified {get; private set;}
    internal bool Changed=>before!=after;
    private (int water,int empty,int filled) before,after;

    private (int,int,int) Snapshot(Player p)
    {
        int water=0;
        // Vanilla scoops from the target and its eight neighbours; pouring affects one cell.
        int radius=scoop?1:0;
        for(int tx=x-radius;tx<=x+radius;tx++)
            for(int ty=y-radius;ty<=y+radius;ty++)
            {
                var tile=Main.tile[tx,ty];
                if(tile.LiquidType==LiquidID.Water)water+=tile.LiquidAmount;
            }
        int Count(int type)=>p.inventory.Take(50).Where(i=>i.type==type && i.stack>0).Sum(i=>i.stack);
        return (water,Count(ItemID.EmptyBucket),Count(ItemID.WaterBucket));
    }

    internal string Prepare(Player p)
    {
        if(!WorldGen.InWorld(x,y,2))return "bucket_target_out_of_world";
        if(p.HeldItem.type!=Source || p.noBuilding)return "bucket_use_unavailable";
        var tile=Main.tile[x,y];
        if(tile.LiquidAmount>0 && tile.LiquidType!=LiquidID.Water || scoop && tile.LiquidAmount==0)
            return "bucket_target_not_water";
        int output=scoop?ItemID.WaterBucket:ItemID.EmptyBucket;
        if(p.HeldItem.stack>1 && !p.inventory.Take(50).Any(i=>i.IsAir || i.type==output && i.stack<i.maxStack))
            return "inventory_full";
        before=after=Snapshot(p);
        return null;
    }

    internal void Observe(Player p)
    {
        after=Snapshot(p);
        int sign=scoop?1:-1;
        Verified=(before.water-after.water)*sign>0 && (after.filled-before.filled)*sign==1 &&
            (before.empty-after.empty)*sign==1;
        if(Changed)NetMessage.SendTileSquare(-1,x-1,y-1,3,3);
    }

    internal object Evidence()=>new{x,y,mode=scoop?"scoop":"pour",verified=Verified,
        beforeWater=before.water,afterWater=after.water,beforeEmpty=before.empty,afterEmpty=after.empty,
        beforeFilled=before.filled,afterFilled=after.filled};
}
