using System;
using System.Collections.Generic;
using System.Linq;

namespace VirtualPlayerEngineProbe;

internal readonly record struct MapPoint(int X,int Y);
internal readonly record struct MapWindow(int Left,int Top,int Right,int Bottom)
{
    internal bool Contains(MapPoint p)=>p.X>=Left && p.X<=Right && p.Y>=Top && p.Y<=Bottom;
}
internal sealed record Frontier(MapPoint Key,HashSet<MapPoint> Unknown);

internal static class ExplorationFrontiers
{
    internal static IEnumerable<MapPoint> Neighbors(MapPoint p)
    {
        yield return new(p.X-1,p.Y);
        yield return new(p.X+1,p.Y);
        yield return new(p.X,p.Y-1);
        yield return new(p.X,p.Y+1);
    }

    // The window limits known-side work. Inspect a real one-cell halo in memory
    // before classifying its neighbor, including across chunk boundaries.
    internal static List<Frontier> Find(AiMap map,MapWindow window)
    {
        var faces=new Dictionary<MapPoint,List<MapPoint>>();
        for(int y=Math.Max(0,window.Top);y<=Math.Min(map.Height-1,window.Bottom);y++)
        for(int x=Math.Max(0,window.Left);x<=Math.Min(map.Width-1,window.Right);x++)
        {
            if(map.Get(x,y)!=1)continue;
            var p=new MapPoint(x,y);
            foreach(var n in Neighbors(p))
            {
                if(n.X<0 || n.X>=map.Width || n.Y<0 || n.Y>=map.Height || map.Get(n.X,n.Y)!=0)continue;
                if(!faces.TryGetValue(p,out var adjacent))faces[p]=adjacent=new();
                adjacent.Add(n);
            }
        }
        var clusters=new List<Frontier>();
        foreach(var seed in faces.Keys.ToArray())
        {
            if(!faces.ContainsKey(seed))continue;
            var unknown=new HashSet<MapPoint>();
            var queue=new Queue<MapPoint>();
            queue.Enqueue(seed);
            while(queue.TryDequeue(out var p))
            {
                if(!faces.Remove(p,out var adjacent))continue;
                unknown.UnionWith(adjacent);
                foreach(var n in Neighbors(p))
                    if(faces.ContainsKey(n))queue.Enqueue(n);
            }
            clusters.Add(new(seed,unknown));
        }
        return clusters;
    }
}
