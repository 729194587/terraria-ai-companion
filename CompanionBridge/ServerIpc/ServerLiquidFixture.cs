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
    internal static void Tick(Player p)
    {
        if(!Program.LaunchParameters.ContainsKey("-companionliquidtest"))return;
        string request=Path.Combine(Main.SavePath,"liquid-request");
        if(!File.Exists(request))return;
        string mode=File.ReadAllText(request).Trim();
        File.Delete(request);
        int x=Main.spawnTileX,floor=Main.spawnTileY+8;
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
}
