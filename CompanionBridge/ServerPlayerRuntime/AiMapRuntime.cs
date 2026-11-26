using System;
using Terraria;
using Terraria.ID;

namespace VirtualPlayerEngineProbe;

internal static class AiMapRuntime
{
    internal static AiMap Map;
    private static ulong lastTick=ulong.MaxValue;
    internal static void Reset(){Map=null;lastTick=ulong.MaxValue;}
    private static bool Solid(int x,int y)
    {var t=Main.tile[x,y];return t.HasTile&&!t.IsActuated&&Main.tileSolid[t.TileType]&&!Main.tileSolidTop[t.TileType];}
    // Target itself is visible; opaque intermediate cells block sight/light.
    private static bool Sight(int ax,int ay,int bx,int by)
    {
        int dx=Math.Abs(bx-ax),sx=ax<bx?1:-1,dy=-Math.Abs(by-ay),sy=ay<by?1:-1,err=dx+dy;
        while(ax!=bx||ay!=by)
        {
            int e=2*err;if(e>=dy){err+=dy;ax+=sx;}if(e<=dx){err+=dx;ay+=sy;}
            if(ax==bx&&ay==by)return true;
            if(Solid(ax,ay))return false;
        }
        return true;
    }
    internal static void Tick(bool force=false)
    {
        var p=ProbeSession.Actor;
        if(Map==null||p==null||!WorldCharacterPersistence.Ready||p.dead||p.ghost||!p.active||ProbeSession.Quarantined)return;
        if(!force&&lastTick!=ulong.MaxValue&&Main.GameUpdateCount-lastTick<15)return;
        lastTick=Main.GameUpdateCount;
        Map.BeginSample();
        try { Sample(p); }
        finally { Map.EndSample(); }
    }
    private static void Sample(Player p)
    {
        int cx=Math.Clamp((int)(p.Center.X/16),0,Map.Width-1),cy=Math.Clamp((int)(p.Center.Y/16),0,Map.Height-1);
        int left=Math.Max(0,cx-60),right=Math.Min(Map.Width-1,cx+59),top=Math.Max(0,cy-34),bottom=Math.Min(Map.Height-1,cy+33);
        var torches=new System.Collections.Generic.List<(int x,int y)>();
        for(int y=top;y<=bottom;y++)for(int x=left;x<=right;x++)
        {var t=Main.tile[x,y];if(t.HasTile&&t.TileType==TileID.Torches&&t.TileFrameX<66&&Sight(cx,cy,x,y))torches.Add((x,y));}
        for(int y=top;y<=bottom;y++)for(int x=left;x<=right;x++)
        {
            if(!Sight(cx,cy,x,y))continue;
            bool lit=(x-cx)*(x-cx)+(y-cy)*(y-cy)<=16;
            if(!lit&&Main.dayTime&&y<Main.worldSurface&&Main.tile[x,y].WallType==0)
            {
                // Only a vertical opening to the top of this viewport counts as sky.
                lit=true;for(int sky=y-1;sky>=0;sky--)if(Solid(x,sky)||Main.tile[x,sky].WallType!=0){lit=false;break;}
            }
            if(!lit)foreach(var torch in torches)if((x-torch.x)*(x-torch.x)+(y-torch.y)*(y-torch.y)<=196&&Sight(torch.x,torch.y,x,y)){lit=true;break;}
            if(lit)Remember(x,y);
        }
    }
    internal static void Remember(int x,int y)
    {
        if(Map==null||x<0||y<0||x>=Map.Width||y>=Map.Height)return;
        var t=Main.tile[x,y];byte value=t.LiquidAmount>0?(byte)(4+t.LiquidType):t.HasTile&&!t.IsActuated&&Main.tileSolidTop[t.TileType]?(byte)3:Solid(x,y)?(byte)2:(byte)1;
        Map.Remember(x,y,value,checked((long)Main.GameUpdateCount));
    }
}
