using System;
using System.Reflection;
using CompanionBridge.Common.Systems;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.GameContent.Events;
using Terraria.Graphics.Light;
using Terraria.ID;

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
            if(!PrepareSky())return false;
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

    private bool PrepareSky()
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
