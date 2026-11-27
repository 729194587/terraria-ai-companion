using System;
using CompanionBridge.ServerIpc;
using Terraria;

namespace VirtualPlayerEngineProbe;

internal static class AiMapRuntime
{
    internal static AiMap Map;
    private static ulong lastTick=ulong.MaxValue;
    private static bool reportedLightFailure;
    internal static void Reset(){Map=null;lastTick=ulong.MaxValue;reportedLightFailure=false;}

    internal static AiMap ActiveMap(Player p) =>
        p!=null && ReferenceEquals(p,ProbeSession.Actor) && p.active && !p.dead && !p.ghost &&
        !ProbeSession.Quarantined && WorldCharacterPersistence.Ready ? Map : null;

    internal static void Tick(ServerTileLighting lighting,bool force=false)
    {
        var p=ProbeSession.Actor;
        var map=ActiveMap(p);
        if(map==null)return;
        map.RecordFeet((int)Math.Floor(p.Center.X/16),(int)Math.Floor(p.Bottom.Y/16));
        if(lighting==null || !force && lastTick!=ulong.MaxValue && Main.GameUpdateCount-lastTick<15)return;
        lastTick=Main.GameUpdateCount;
        try
        {
            lighting.SampleTerrain(p,map,force);
            reportedLightFailure=false;
        }
        catch(Exception error)
        {
            // A failed light scan must not take down the game loop or reveal dark terrain.
            if(!reportedLightFailure)ProbeSession.Log("map-light-sample-unavailable",new{error=error.Message});
            reportedLightFailure=true;
        }
    }

    internal static void RememberView(AiMap map,int originX,int originY,int width,ReadOnlySpan<char> cells)
    {
        map.BeginSample();
        try
        {
            for(int row=0;row<cells.Length;row+=width)
            {
                int y=originY+row/width;
                for(int col=0;col<width;col++)
                    if(cells[row+col]!='?')Remember(map,originX+col,y);
            }
        }
        finally{map.EndSample();}
    }

    // Confirmed mining and placement also update the affected cells directly.
    internal static void Remember(int x,int y)=>Remember(Map,x,y);
    private static void Remember(AiMap map,int x,int y)
    {
        if(map==null||x<0||y<0||x>=map.Width||y>=map.Height)return;
        var t=Main.tile[x,y];
        byte value=t.LiquidAmount>0?(byte)(4+t.LiquidType):
            t.HasUnactuatedTile && Main.tileSolidTop[t.TileType]?(byte)3:
            t.HasUnactuatedTile && Main.tileSolid[t.TileType]?(byte)2:(byte)1;
        map.Remember(x,y,value,checked((long)Main.GameUpdateCount));
    }
}
