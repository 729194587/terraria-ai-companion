using System;
using System.Collections.Generic;
using System.Linq;
using CompanionBridge.Motion;
using CompanionBridge.Common.Systems;
using Terraria;
using Terraria.ID;

namespace CompanionBridge.ServerIpc;

// Read-only fixture selection inside a disposable generated-world diagnostic.
internal static class NaturalMiningSurvey
{
    internal static Dictionary<string,object> Find()
    {
        var cases=new Dictionary<string,object>();
        var seen=new HashSet<(int,int)>();
        int spawn=Main.spawnTileX;
        for(int radius=0;radius<500 && cases.Count<5;radius++)
        foreach(int x in new[]{spawn-radius,spawn+radius})
        for(int y=Math.Max(40,Main.spawnTileY-32);y<Main.spawnTileY+520;y++)
        {
            var tile=Main.tile[x,y];
            if(!tile.HasUnactuatedTile||!TileID.Sets.Ore[tile.TileType]||seen.Contains((x,y)))continue;
            var vein=new List<(int X,int Y)>();
            var queue=new Queue<(int,int)>();
            queue.Enqueue((x,y));
            while(queue.TryDequeue(out var c)&&vein.Count<129)
            {
                var (tx,ty)=c;
                if(!WorldGen.InWorld(tx,ty,2)||!Main.tile[tx,ty].HasUnactuatedTile||
                    Main.tile[tx,ty].TileType!=tile.TileType||!seen.Add(c))continue;
                vein.Add(c);
                queue.Enqueue((tx+1,ty));queue.Enqueue((tx-1,ty));queue.Enqueue((tx,ty+1));queue.Enqueue((tx,ty-1));
            }
            if(vein.Count is 0 or >128)continue;
            for(int dx=-8;dx<=8;dx++)
            for(int dy=-8;dy<=5;dy++)
            {
                int px=x+dx,floor=y+dy;
                if(Math.Abs(px-spawn)>511||!WorldGen.SolidTile(px,floor))continue;
                var pose=new PhysicsState(px*16,floor*16-42,0,0,true,0,true);
                if(!MotionSupport.SafeFlatLanding(pose,TerrainReader.ReadDryCell)||
                    Collision.SolidCollision(new(px*16,floor*16-42),20,42))continue;
                var closest=vein.MinBy(c=>Math.Abs(px-c.X)+Math.Abs(floor-1-c.Y));
                int distance=Math.Abs(px-closest.X)+Math.Abs(floor-1-closest.Y);
                string name=null;
                if(tile.TileType==TileID.Copper&&vein.Count is 2 or 3&&distance<=5&&!cases.ContainsKey("small-copper"))name="small-copper";
                else if(vein.Count is >=21 and <=32&&distance<=3&&!cases.ContainsKey("large-vein"))name="large-vein";
                else if(vein.Count<=20&&distance<=4&&!cases.ContainsKey("change-stance")&&vein.Any(c=>Math.Abs(px-c.X)>6||Math.Abs(floor-c.Y)>7))name="change-stance";
                else if(!cases.ContainsKey("buried")&&Math.Abs(closest.X-px)<=1&&closest.Y-floor is >=4 and <=7
                    &&Enumerable.Range(px,2).All(tx=>Enumerable.Range(floor,2).All(ty=>
                        Main.tile[tx,ty].HasUnactuatedTile&&Main.tile[tx,ty].TileType==TileID.Dirt
                        &&NativeNavigationEdits.SafeExcavation(tx,ty,new(TerrainReader.ReadEnvironment)))))name="buried";
                else if(distance>=10&&vein.All(c=>WorldGen.SolidTile(c.X,c.Y-1)&&WorldGen.SolidTile(c.X,c.Y+1)
                    &&WorldGen.SolidTile(c.X-1,c.Y)&&WorldGen.SolidTile(c.X+1,c.Y)))name="enclosed";
                if(name==null||cases.ContainsKey(name))continue;
                cases.Add(name,new{offset=px-spawn,floor,x=closest.X,y=closest.Y,count=vein.Count,type=tile.TileType});
                goto NextVein;
            }
            NextVein:;
        }
        return cases;
    }
}
