using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CompanionBridge.Common.Systems;
using CompanionBridge.Motion;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace CompanionBridge.ServerIpc;

// Invoked by the existing copied-world fixture on the game thread.
internal static class ServerLiquidFixture
{
    private static string scene;
    private static bool gateOpened;
    private static int samples;
    private static float minY;

    internal static void Tick(Player p)
    {
        if(!Program.LaunchParameters.ContainsKey("-companionliquidtest"))return;
        string request=Path.Combine(Main.SavePath,"liquid-request");
        if(!File.Exists(request))
        {
            if(scene!=null)ObserveScene(p);
            return;
        }
        string mode=File.ReadAllText(request).Trim();
        File.Delete(request);
        int x=Main.spawnTileX,floor=Main.spawnTileY+8;
        scene=null;
        if(mode is "bridge" or "detour" or "hazard" or "mine" or "flood")
        {
            PrepareScene(p,mode,x,floor);
            return;
        }
        if(mode=="wet")
        {
            p.position=new((x+4)*16-2,floor*16-3);
            p.velocity=Vector2.Zero;
            File.WriteAllText(Path.Combine(Main.SavePath,"liquid-wet-ready"),"native liquid update pending");
            return;
        }

        for(int tx=x-22;tx<=x+24;tx++)
            for(int ty=floor-17;ty<=floor+5;ty++)
            {
                var tile=Main.tile[tx,ty];
                tile.ClearEverything();
                if(ty>=floor&&(tx<x+3||tx>x+5||ty>=floor+3))
                {tile.HasTile=true;tile.TileType=TileID.Stone;}
                else if(ty>=floor)tile.LiquidAmount=255;
            }
        var start=new PhysicsState((x+2)*16-4,floor*16-42,0,0,true);
        p.position=new(start.Px,start.Py);
        p.velocity=Vector2.Zero;
        var world=new NativeMotionWorld(start);
        var checks=new Dictionary<string,bool>();
        var ph=PhysicsParameters.Basic;
        var walk=PhysicsSimulator.Predict(start,1,0,ph,world,walkFrames:48,maxDropPixels:96);
        var jump=PhysicsSimulator.Predict(start,1,15,ph,world,maxDropPixels:96);
        checks["wetWalkRejected"]=!walk.Safe&&walk.Reason.Contains("wet contact");
        checks["dryJumpSafe"]=jump.Safe&&jump.Landing.Px+20>(x+6)*16
            &&Math.Abs(jump.Landing.Py+42-floor*16)<.01f;
        if(jump.Safe)
        {
            var failures=new NavigationFailureMemory();
            failures.Remember(start,jump,1,world,ph,"fixture deviation",jump.Landing);
            var water=Main.tile[x+4,floor];
            water.LiquidAmount=128;
            failures.Validate(world,ph,2);
            checks["liquidInvalidatesFailedEdge"]=failures.Count==0;
            water.LiquidAmount=255;
        }
        int sx=x-4,sy=floor-4;
        var sample=Main.tile[sx,sy];
        sample.HasTile=true;
        sample.TileType=TileID.Platforms;
        var shallow=new PhysicsState(sx*16-2,sy*16-24,0,0,false);
        var above=shallow with{Py=sy*16-44};
        var outer=shallow with{Py=sy*16-29};
        string Risk(PhysicsState pose)=>NativeLiquidRisk.Rejection(pose,pose);
        var saved=(Collision.honey,Collision.shimmer);
        var observations=new List<object>();
        var search=new NavigationSearchMemory();
        try
        {
            sample.LiquidType=LiquidID.Water;
            long previous=0;
            foreach(byte amount in new byte[]{1,128,255})
            {
                sample.LiquidAmount=amount;
                var snapshot=TerrainReader.Read(p);
                var liquid=snapshot.Liquids.Single(c=>c.X==sx&&c.Y==sy);
                char shape=snapshot.Rows[sy-snapshot.OriginY][sx-snapshot.OriginX];
                long stamp=world.TerrainSignature(start);
                observations.Add(new{amount,shape,liquid,stamp,wet=Risk(shallow)});
                checks["split-"+amount]=shape=='='&&liquid.Type=="water"&&liquid.Amount==amount;
                checks["stamp-"+amount]=stamp!=previous;
                previous=stamp;
                checks["native-wet-"+amount]=(Risk(shallow)!=null)==(amount==255);
            }
            checks["aboveFullTileDry"]=Risk(above)==null;
            float tileLeft=sx*16;
            var a=shallow with{Px=tileLeft-15};
            var b=shallow with{Px=tileLeft+11};
            checks["crossingBetweenDryEndpoints"]=Risk(a)==null&&Risk(b)==null
                &&NativeLiquidRisk.Rejection(a,b)?.Contains("wet contact")==true;
            search.Scope(previous,ph,1);
            search.Predict(start,1,0,48,ph,world);
            checks["cacheContainsRejectedWalk"]=search.Count==1;
            var surface=world.ReadSurfaceCell(sx,sy);
            sample.LiquidType=LiquidID.Honey;
            checks["typeChangesSurfaceStamp"]=world.ReadSurfaceCell(sx,sy).Signature!=surface.Signature;
            long typeChanged=world.TerrainSignature(start);
            search.Scope(typeChanged,ph,2);
            checks["cacheClearedOnTypeChange"]=typeChanged!=previous&&search.Count==0;
            search.Predict(start,1,0,48,ph,world);
            sample.LiquidAmount=128;
            search.Scope(world.TerrainSignature(start),ph,3);
            checks["cacheClearedOnAmountChange"]=search.Count==0;
            checks["honeyOuterBody"]=Risk(outer)?.StartsWith("honey")==true;
            sample.LiquidType=LiquidID.Shimmer;
            checks["shimmerOuterBody"]=Risk(outer)?.StartsWith("shimmer")==true;
            sample.LiquidType=LiquidID.Lava;
            checks["lavaOuterBody"]=Risk(outer)=="lava contact";
            sample.LiquidAmount=0;
            long empty=world.TerrainSignature(start);
            sample.LiquidType=LiquidID.Water;
            checks["dryTypeIgnored"]=empty==world.TerrainSignature(start);
            sample.LiquidAmount=255;
            Collision.honey=true;Collision.shimmer=false;
            checks["flagsRestoredAfterWet"]=Risk(shallow)!=null&&Collision.honey&&!Collision.shimmer;
            Collision.honey=false;Collision.shimmer=true;
            sample.LiquidAmount=128;
            checks["flagsRestoredAfterDry"]=Risk(shallow)==null&&!Collision.honey&&Collision.shimmer;
            sample.LiquidAmount=255;
            var slope=Main.tile[sx,sy+1];
            slope.HasTile=true;slope.TileType=TileID.Stone;slope.Slope=SlopeType.SlopeDownRight;
            var below=shallow with{Py=(sy+1)*16-5};
            checks["nativeWetOnSlopeBelowLiquid"]=Risk(below)?.Contains("wet contact")==true;
            slope.ClearEverything();
        }
        finally
        {
            (Collision.honey,Collision.shimmer)=saved;
            sample.ClearEverything();
        }
        File.WriteAllText(Path.Combine(Main.SavePath,"liquid-proof.json"),JsonSerializer.Serialize(new
        {
            checks,observations,walk=walk.Reason,jump=jump.Reason,
            x=(x+8)*16,y=floor*16
        }));
    }

    private static void PrepareScene(Player p,string mode,int x,int floor)
    {
        scene=mode;gateOpened=false;samples=0;
        bool tunnel=mode is "mine" or "flood";
        for(int tx=x-28;tx<=x+28;tx++)
            for(int ty=floor-23;ty<=floor+8;ty++)
            {
                var tile=Main.tile[tx,ty];
                tile.ClearEverything();
                bool roof=mode!="detour"&&ty<=floor-4;
                bool gap=!tunnel&&tx>=x+3&&tx<=x+8&&ty>=floor&&ty<floor+4;
                if(roof||ty>=floor&&!gap||tunnel&&tx==x+3&&ty>=floor-3&&ty<floor)
                {tile.HasTile=true;tile.TileType=TileID.Stone;}
                else if(gap)
                {tile.LiquidType=mode=="hazard"?LiquidID.Lava:LiquidID.Water;tile.LiquidAmount=255;}
                if(mode=="detour"&&ty==floor-4&&tx>=x+2&&tx<=x+10)
                {tile.HasTile=true;tile.TileType=TileID.Platforms;}
                if(tunnel&&tx==x+4&&ty>=floor-11&&ty<=floor-5)
                {tile.ClearEverything();tile.LiquidAmount=255;}
            }
        p.miscEquips[4].TurnToAir();
        if(tunnel)p.inventory[1].SetDefaults(ItemID.CopperPickaxe);
        else p.inventory[1].TurnToAir();
        p.inventory[4].SetDefaults(ItemID.WoodPlatform);
        p.inventory[4].stack=16;
        p.position=new((x+2)*16-4,floor*16-p.height);
        p.velocity=Vector2.Zero;minY=p.position.Y;
        File.WriteAllText(Path.Combine(Main.SavePath,"liquid-edit-ready.json"),JsonSerializer.Serialize(new
            {x=(x+11)*16,y=floor*16,left=x+3,right=x+8,floor,startX=p.position.X,startY=p.position.Y}));
    }

    private static void ObserveScene(Player p)
    {
        int x=Main.spawnTileX,floor=Main.spawnTileY+8;
        minY=Math.Min(minY,p.position.Y);
        if(scene=="flood"&&!gateOpened&&Enumerable.Range(floor-3,3).Any(y=>!Main.tile[x+3,y].HasTile))
        {
            // Release a pre-existing upstream reservoir after observing a real
            // native cut. Terraria propagates the water on its ordinary ticks.
            gateOpened=true;
            Main.tile[x+4,floor-4].ClearTile();
            Liquid.AddWater(x+4,floor-5);
            Liquid.AddWater(x+4,floor-4);
        }
        if(++samples%4!=0)return;
        string path=Path.Combine(Main.SavePath,"liquid-edit-"+scene+".jsonl");
        File.AppendAllText(path,JsonSerializer.Serialize(new
        {
            minY,x=p.position.X,p.wet,p.lavaWet,gateOpened,
            inflow=Main.tile[x+4,floor-1].LiquidAmount,
            wetCorridor=gateOpened&&NativeLiquidRisk.Rejection(
                new((x+4)*16,floor*16-42,0,0,true),new((x+4)*16,floor*16-42,0,0,true))!=null,
            platforms=Enumerable.Range(x+3,6).Count(tx=>Main.tile[tx,floor].HasTile&&Main.tile[tx,floor].TileType==TileID.Platforms),
            waterPlatforms=Enumerable.Range(x+3,6).Count(tx=>Main.tile[tx,floor].HasTile&&Main.tile[tx,floor].TileType==TileID.Platforms
                &&Main.tile[tx,floor].LiquidAmount>0)
        })+Environment.NewLine);
    }
}
