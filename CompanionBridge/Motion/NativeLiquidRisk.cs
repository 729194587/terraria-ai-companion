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
    internal static string Rejection(PhysicsState start,PhysicsState end)
    {
        float left=Math.Min(start.Px,end.Px),top=Math.Min(start.Py,end.Py);
        float right=Math.Max(start.Px,end.Px)+PhysicsSimulator.PlayerW;
        float bottom=Math.Max(start.Py,end.Py)+PhysicsSimulator.PlayerH;
        int minX=(int)Math.Floor(left/16),maxX=(int)Math.Ceiling(right/16)-1;
        int minY=(int)Math.Floor(top/16),maxY=(int)Math.Ceiling(bottom/16)-1;
        bool wetCandidate=false,lavaCandidate=false;
        for(int x=minX;x<=maxX;x++)
            // WetCollision can see liquid in the cell above an active slope.
            for(int y=minY-1;y<=maxY;y++)
            {
                if(!TerrainReader.Loaded(x,y))return "unknown liquid coverage";
                var tile=Main.tile[x,y];
                if(tile.LiquidAmount==0)continue;
                var liquid=new TileLiquid(tile.LiquidType,tile.LiquidAmount);
                wetCandidate=true;
                lavaCandidate|=liquid.Kind==LiquidKind.Lava;
                if(liquid.Kind is not (LiquidKind.Honey or LiquidKind.Shimmer or LiquidKind.Unknown))continue;
                // For unmodeled special liquids use the full swept body,
                // including contact outside the engine's narrow wet probe.
                if(liquid.OverlapsVertical(y,top,bottom))return Reason(liquid.Kind);
                if(y<maxY&&bottom>(y+1)*16&&top<(y+2)*16
                    &&TerrainReader.Loaded(x,y+1)&&Main.tile[x,y+1].HasTile
                    &&Main.tile[x,y+1].Slope!=SlopeType.Solid&&Main.tile[x,y+1].LiquidAmount==0)
                    return Reason(liquid.Kind);
            }
        if(!wetCandidate)return null;

        // LavaCollision uses the supplied full box and does not write flags.
        // The enclosing integer box conservatively covers intermediate poses.
        if(lavaCandidate&&Collision.LavaCollision(new Vector2(left,top),
            (int)Math.Ceiling(right-left),(int)Math.Ceiling(bottom-top)))return Reason(LiquidKind.Lava);

        float dx=end.Px-start.Px,dy=end.Py-start.Py;
        int steps=(int)Math.Ceiling(Math.Max(Math.Abs(dx),Math.Abs(dy))/4);
        if(steps==0)return WetRejection(left,top);
        for(int i=0;i<=steps;i++)
        {
            float t=(float)i/steps;
            string reason=WetRejection(start.Px+dx*t,start.Py+dy*t);
            if(reason!=null)return reason;
        }
        return null;
    }

    private static string WetRejection(float x,float y)
    {
        var flags=(Collision.honey,Collision.shimmer);
        try
        {
            if(!Collision.WetCollision(new Vector2(x,y),PhysicsSimulator.PlayerW,PhysicsSimulator.PlayerH))return null;
            return Collision.honey?Reason(LiquidKind.Honey):Collision.shimmer?Reason(LiquidKind.Shimmer)
                :"liquid wet contact requires unsupported motion";
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
