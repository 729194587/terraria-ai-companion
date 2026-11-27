using System;
using System.Reflection;
using CompanionBridge.Common.Systems;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.GameContent.Events;
using Terraria.Graphics.Light;
using Terraria.ID;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

// Game-thread-only actor-centred tile lighting. The dedicated renderer's light
// buffer is never read. Native engine fingerprint checks gate Server AI.
internal sealed class ServerTileLighting
{
    internal const float Threshold=.45f;
    private const int Radius=24;
    private delegate void Emission(int x,int y,ref Vector3 color);
    private delegate LightMaskMode Mask(Tile tile);
    private delegate void SkyPalette(Main.InfoToSetBackColor info,ref Color background,ref Color moon);
    private readonly LightMap map=new();
    private LightMap viewMap;
    private const int ViewWidth=119, ViewHeight=67;
    private readonly char[] viewCells=new char[ViewWidth*ViewHeight];
    private int visibleTileCount;
    private (ulong Tick, Player Player, AiMap Memory, Point Center, int Direction, Vector3 Hand)? viewSample;
    private readonly Emission emission;
    private readonly Mask mask;
    private readonly SkyPalette palette;
    private readonly Vector3[,] sources=new Vector3[Radius*2+1,Radius*2+1];
    private readonly int[] counts=new int[TileID.Count];
    private Point center;
    private int left,top,right,bottom;
    private ulong tick;
    private float brightness;
    private bool sampled,failed;

    internal ServerTileLighting()
    {
        const BindingFlags methods=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        var type=typeof(Main).Assembly.GetType("Terraria.Graphics.Light.TileLightScanner",true);
        var scanner=Activator.CreateInstance(type,nonPublic:true);
        emission=type.GetMethod("GetTileLight",methods).CreateDelegate<Emission>(scanner);
        mask=type.GetMethod("GetTileMask",methods).CreateDelegate<Mask>(scanner);
        palette=typeof(Main).GetMethod("SetBackColor",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)
            .CreateDelegate<SkyPalette>();
    }

    internal object Observe(Player p)=>Sample(p)?new{available=true,source="native_tile_light_map",brightness,gameUpdate=tick}
        : (object)new{available=false,source="native_tile_light_map",reason=failed?"native_light_sample_failed":"incomplete_light_coverage"};
    internal float Brightness=>brightness;
    internal float Source(int x,int y)=>sampled&&x>=left&&x<=right&&y>=top&&y<=bottom?Peak(sources[x-left,y-top]):0;
    private static float Peak(Vector3 v)=>Math.Max(v.X,Math.Max(v.Y,v.Z));

    internal bool Sample(Player p)
    {
        if(failed)return false;
        var position=p.Center.ToTileCoordinates();
        if(sampled&&center==position&&tick==Main.GameUpdateCount)return true;
        sampled=false;
        center=position;tick=Main.GameUpdateCount;
        left=center.X-Radius;top=center.Y-Radius;right=center.X+Radius;bottom=center.Y+Radius;
        // Scanner and biome inputs read neighboring cells. Refuse incomplete
        // coverage rather than returning artificial darkness at a world edge.
        if(!WorldGen.InWorld(left,top,2)||!WorldGen.InWorld(right,bottom,2))return false;
        var saved=(Main.tileColor,Main.ColorOfTheSkies,Main.eclipseLight);
        try
        {
            if(!PrepareSky(center))return false;
            map.SetSize(right-left+1,bottom-top+1);
            for(int x=left;x<=right;x++)for(int y=top;y<=bottom;y++)
            {
                var color=Vector3.Zero;
                emission(x,y,ref color);
                sources[x-left,y-top]=map[x-left,y-top]=color;
                map.SetMaskAt(x-left,y-top,mask(Main.tile[x,y]));
            }
            map.Blur();
            brightness=Peak(map[Radius,Radius]);
            sampled=float.IsFinite(brightness)&&brightness>=0;
            return sampled;
        }
        catch(Exception error)
        {
            failed=true;
            VirtualPlayerEngineProbe.ProbeSession.Log("dedicated-light-sample-failed",new{error=error.ToString()});
            return false;
        }
        finally{(Main.tileColor,Main.ColorOfTheSkies,Main.eclipseLight)=saved;}
    }

    internal object ReadTerrain(Player p)
    {
        SampleTerrain(p,AiMapRuntime.ActiveMap(p));
        var actor=viewSample.Value.Center;
        var rows=new string[ViewHeight];
        for(int j=0;j<ViewHeight;j++)rows[j]=new string(viewCells,j*ViewWidth,ViewWidth);
        return new{originX=actor.X-ViewWidth/2,originY=actor.Y-ViewHeight/2,width=ViewWidth,height=ViewHeight,
            actor=new{x=actor.X,y=actor.Y},visibleTiles=visibleTileCount,rows};
    }

    // Background exploration and explicit observations consume the same mask.
    // The extra margin admits light from just outside the displayed rectangle.
    internal void SampleTerrain(Player p,AiMap memory,bool force=false)
    {
        const int padding=32;
        const float visibleLight=.065f;
        var actor=p.Center.ToTileCoordinates();
        var hand=HandLight(p);
        var key=(Main.GameUpdateCount,p,memory,actor,p.direction,hand);
        if(!force && viewSample==key)return;
        viewSample=null;
        int originX=actor.X-ViewWidth/2,originY=actor.Y-ViewHeight/2;
        int x0=Math.Max(2,originX-padding),y0=Math.Max(2,originY-padding);
        int x1=Math.Min(Main.maxTilesX-3,originX+ViewWidth-1+padding);
        int y1=Math.Min(Main.maxTilesY-3,originY+ViewHeight-1+padding);
        var saved=(Main.tileColor,Main.ColorOfTheSkies,Main.eclipseLight);
        try
        {
            if(!PrepareSky(actor))throw new QueryUnavailableException("incomplete_light_coverage");
            viewMap??=new();
            viewMap.SetSize(x1-x0+1,y1-y0+1);
            for(int x=x0;x<=x1;x++)for(int y=y0;y<=y1;y++)
            {
                var color=Vector3.Zero;
                emission(x,y,ref color);
                viewMap[x-x0,y-y0]=color;
                viewMap.SetMaskAt(x-x0,y-y0,mask(Main.tile[x,y]));
            }
            int hx=Math.Clamp(actor.X+p.direction,x0,x1)-x0,hy=Math.Clamp(actor.Y,y0,y1)-y0;
            viewMap[hx,hy]=Vector3.Max(viewMap[hx,hy],hand);
            viewMap.Blur();
            Array.Fill(viewCells,'?');
            visibleTileCount=0;
            for(int y=Math.Max(y0,originY);y<=Math.Min(y1,originY+ViewHeight-1);y++)
            for(int x=Math.Max(x0,originX);x<=Math.Min(x1,originX+ViewWidth-1);x++) {
                if(!(Peak(viewMap[x-x0,y-y0])>=visibleLight))continue;
                viewCells[(y-originY)*ViewWidth+x-originX]=TerrainGlyph(Main.tile[x,y]);
                visibleTileCount++;
            }
        }
        finally{(Main.tileColor,Main.ColorOfTheSkies,Main.eclipseLight)=saved;}
        if(memory!=null)AiMapRuntime.RememberView(memory,originX,originY,ViewWidth,viewCells);
        viewSample=key;
    }

    private static Vector3 HandLight(Player p)
    {
        var held=p.HeldItem;
        if(p.dead || p.noItems || held.IsAir || (p.wet && held.noWet))return Vector3.Zero;
        if(held.type<ItemID.Count && held.createTile==TileID.Torches) {
            TorchID.TorchColor(held.placeStyle,out float r,out float g,out float b);
            return new(r,g,b);
        }
        return held.type==ItemID.Glowstick ? new(.7f,1f,.8f) : Vector3.Zero;
    }

    private static char TerrainGlyph(Tile tile)
    {
        if(tile.HasUnactuatedTile) {
            int type=tile.TileType;
            if(type==TileID.Torches)return 't';
            if(type is TileID.Containers or TileID.Containers2 or TileID.Dressers)return 'c';
            if(TileID.Sets.Ore[type])return 'o';
            if(Main.tileSolidTop[type])return '=';
            if(Main.tileSolid[type])return '#';
        }
        if(tile.LiquidAmount>0)return tile.LiquidType switch{1=>'L',2=>'H',3=>'S',_=>'~'};
        return '.';
    }

    private bool PrepareSky(Point center)
    {
        // Use the vanilla biome weights without running SceneMetrics nearby
        // effects or altering the active client's biome globals.
        Array.Clear(counts);
        bool bloodMoonMonolith=false;
        int biomeLeft=center.X-Main.buffScanAreaWidth/2,biomeTop=center.Y-Main.buffScanAreaHeight/2;
        for(int x=biomeLeft;x<biomeLeft+Main.buffScanAreaWidth;x++)
        for(int y=biomeTop;y<biomeTop+Main.buffScanAreaHeight;y++)
        {
            if(!WorldGen.InWorld(x,y,1))continue;
            if(!TerrainReader.Loaded(x,y))return false;
            var tile=Main.tile[x,y];
            if(!tile.HasTile)continue;
            if(tile.TileType<counts.Length)counts[tile.TileType]++;
            bloodMoonMonolith|=tile.TileType==TileID.BloodMoonMonolith&&tile.TileFrameY>=54;
        }
        int Count(int[] weights)
        {
            int total=0;
            for(int i=0;i<counts.Length;i++)total+=counts[i]*weights[i];
            return total;
        }
        int evil=Count(Main.remixWorld?TileID.Sets.RemixCorruptBiome:TileID.Sets.CorruptBiome);
        int blood=Count(Main.remixWorld?TileID.Sets.RemixCrimsonBiome:TileID.Sets.CrimsonBiome);
        int holy=Count(TileID.Sets.HallowBiome);
        int sunflowers=counts[TileID.Sunflower];
        var info=new Main.InfoToSetBackColor{
            CorruptionBiomeInfluence=Math.Max(0,evil-holy-10*sunflowers)/(float)SceneMetrics.CorruptionTileMax,
            CrimsonBiomeInfluence=Math.Max(0,blood-holy-10*sunflowers)/(float)SceneMetrics.CrimsonTileMax,
            JungleBiomeInfluence=Count(Main.remixWorld?TileID.Sets.RemixJungleBiome:TileID.Sets.JungleBiome)/(float)SceneMetrics.JungleTileMax,
            MushroomBiomeInfluence=Math.Clamp(Count(TileID.Sets.MushroomBiome)/(float)SceneMetrics.MushroomTileMax,0,1),
            GraveyardInfluence=Math.Clamp((counts[TileID.Tombstones]-sunflowers/2-SceneMetrics.GraveyardTileMin)
                /(float)(SceneMetrics.GraveyardTileMax-SceneMetrics.GraveyardTileMin),0,1),
            BloodMoonActive=Main.bloodMoon||bloodMoonMonolith,LanternNightActive=LanternNight.LanternsUp
        };
        Color background=default,moon=default;
        palette(info,ref background,ref moon);
        // Vanilla ApplyColorOfTheSkiesToTiles prefix. The trailing SkyManager
        // and client ModSystem hooks are intentionally excluded.
        Color sky=Main.ColorOfTheSkies;
        int sum=sky.R+sky.G+sky.B;
        Main.tileColor=new Color((sum+7*sky.R)/10,(sum+7*sky.G)/10,(sum+7*sky.B)/10);
        return true;
    }
}
