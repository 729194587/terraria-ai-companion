using System;
using CompanionBridge.Common.Systems;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace CompanionBridge.Motion;

// Game-thread-only queries into the current world, shared by prediction,
// landing checks and revalidation of the next executed frame.
internal static class NativeLiquidRisk
{
    internal static string Rejection(PhysicsState start,PhysicsState end,bool allowOrdinaryWater=false)
    {
        float left=Math.Min(start.Px,end.Px),top=Math.Min(start.Py,end.Py);
        float right=Math.Max(start.Px,end.Px)+PhysicsSimulator.PlayerW;
        float bottom=Math.Max(start.Py,end.Py)+PhysicsSimulator.PlayerH;
        int minX=(int)Math.Floor(left/16),maxX=(int)Math.Ceiling(right/16)-1;
        int minY=(int)Math.Floor(top/16),maxY=(int)Math.Ceiling(bottom/16)-1;
        bool lavaCandidate=false;
        for(int x=minX;x<=maxX;x++)
            // WetCollision can see liquid in the cell above an active slope.
            for(int y=minY-1;y<=maxY;y++)
            {
                if(!TerrainReader.Loaded(x,y))return "unknown liquid coverage";
                var tile=Main.tile[x,y];
                if(tile.LiquidAmount==0)continue;
                var liquid=new TileLiquid(tile.LiquidType,tile.LiquidAmount);
                if(liquid.Kind==LiquidKind.Water)
                {
                    // Grounded wading and ordinary jumps are supported. Keep
                    // the head above water without relying on oxygen reserves.
                    // DrownCollision uses a head box from top - 2 to top + 10.
                    // This swept envelope uses the whole body width.
                    if(!allowOrdinaryWater&&liquid.OverlapsVertical(y,top-2,Math.Max(start.Py,end.Py)+10))
                        return "submerged head requires unsupported swimming";
                    continue;
                }
                lavaCandidate|=liquid.Kind==LiquidKind.Lava;
                // For unmodeled special liquids use the full swept body,
                // including contact outside the engine's narrow wet probe.
                if(liquid.Kind!=LiquidKind.Lava&&liquid.OverlapsVertical(y,top,bottom))
                    return Reason(liquid.Kind);
                if(y<maxY&&bottom>(y+1)*16&&top<(y+2)*16
                    &&TerrainReader.Loaded(x,y+1)&&Main.tile[x,y+1].HasTile
                    &&Main.tile[x,y+1].Slope!=SlopeType.Solid&&Main.tile[x,y+1].LiquidAmount==0)
                    return Reason(liquid.Kind);
            }

        // LavaCollision uses the supplied full box and does not write flags.
        // The enclosing integer box conservatively covers intermediate poses.
        if(lavaCandidate&&Collision.LavaCollision(new Vector2(left,top),
            (int)Math.Ceiling(right-left),(int)Math.Ceiling(bottom-top)))return Reason(LiquidKind.Lava);

        return null;
    }

    internal static bool WaterContact(PhysicsState state)
    {
        int minX=FallSafety.Row(state.Px),maxX=FallSafety.Row(state.Px+PhysicsSimulator.PlayerW);
        int minY=FallSafety.Row(state.Py)-1,maxY=FallSafety.Row(state.Py+PhysicsSimulator.PlayerH);
        bool water=false;
        for(int x=minX;x<=maxX;x++)
            for(int y=minY;y<=maxY;y++)
            {
                if(!TerrainReader.Loaded(x,y))return false; // Sweep rejects unknown coverage.
                var tile=Main.tile[x,y];
                water|=tile.LiquidAmount>0&&tile.LiquidType==LiquidID.Water;
            }
        if(!water)return false;
        var flags=(Collision.honey,Collision.shimmer);
        try
        {
            return Collision.WetCollision(new Vector2(state.Px,state.Py),PhysicsSimulator.PlayerW,PhysicsSimulator.PlayerH)
                &&!Collision.honey&&!Collision.shimmer;
        }
        finally{(Collision.honey,Collision.shimmer)=flags;}
    }

    private static string Reason(LiquidKind kind)=>kind switch
    {
        LiquidKind.Lava=>"lava contact",
        LiquidKind.Honey=>"honey contact requires unsupported motion",
        LiquidKind.Shimmer=>"shimmer contact requires unsupported motion",
        _=>"unknown liquid contact"
    };
}
