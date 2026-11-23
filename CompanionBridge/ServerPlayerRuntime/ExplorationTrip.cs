using System;
using System.Collections.Generic;
using System.Linq;

namespace VirtualPlayerEngineProbe;

// All history here is scoped to one bounded trip. Terrain remains owned by AiMap.
internal sealed class ExplorationTrip
{
    private readonly AiMap map;
    internal readonly MapPoint Anchor;
    internal readonly MapWindow Window;
    private readonly HashSet<MapPoint> unknown=new();
    private readonly int maxSegments;
    private readonly Dictionary<(MapPoint,MapPoint),(long Signature,long Until)> failures=new();
    private List<Frontier> clusters;
    private long[] chunkVersions;
    private readonly Dictionary<MapPoint,(long Version,long Until)> observed=new();
    internal ExplorationLeg Next{get;private set;}
    internal int Segments{get;private set;}
    internal bool Departed{get;private set;}
    internal int NewTiles{get;private set;}
    internal bool Returning{get;private set;}
    internal List<ExplorationChoice> Choices{get;private set;}=new();
    internal ExplorationTrip(AiMap map,MapPoint anchor,int radius,int maxSegments)
    {
        this.map=map;Anchor=anchor;this.maxSegments=maxSegments;
        Window=new(Math.Max(0,anchor.X-radius),Math.Max(0,anchor.Y-radius),
            Math.Min(map.Width-1,anchor.X+radius),Math.Min(map.Height-1,anchor.Y+radius));
        for(int y=Window.Top;y<=Window.Bottom;y++)
        for(int x=Window.Left;x<=Window.Right;x++)if(map.Get(x,y)==0)unknown.Add(new(x,y));
    }
    internal void Observe()
    {
        NewTiles+=unknown.RemoveWhere(p=>map.Get(p.X,p.Y)!=0);
    }
    private List<Frontier> Boundaries()
    {
        var versions=new List<long>();
        for(int y=Math.Max(0,Window.Top-1)/32;y<=Math.Min(map.Height-1,Window.Bottom+1)/32;y++)
        for(int x=Math.Max(0,Window.Left-1)/32;x<=Math.Min(map.Width-1,Window.Right+1)/32;x++)
            versions.Add(map.ChunkRevision(x*32,y*32));
        if(chunkVersions==null || !versions.SequenceEqual(chunkVersions))
        {
            clusters=ExplorationFrontiers.Find(map,Window);chunkVersions=versions.ToArray();
        }
        return clusters;
    }
    internal IEnumerable<int> Plan(ExplorationCostmap costs,MapPoint at,long now,int remaining,Func<ExplorationLeg,long> signature)
    {
        Observe();
        bool Blocked(ExplorationLeg e)=>failures.TryGetValue((e.From,e.To),out var f)&&now<f.Until&&signature(e)==f.Signature;
        Returning|=Segments>=maxSegments || Segments>0 && NewTiles>0;
        bool Visited(MapPoint p)=>observed.TryGetValue(p,out var last)&&last.Version==map.Revision&&last.Until>now;
        foreach(int work in costs.Evaluate(at,Anchor,Returning?null:Boundaries(),remaining,Blocked,Visited))yield return work;
        Choices=costs.Choices;
        if(Choices.Count>0)Next=Choices[0].Next;
        else {Returning=true;Next=costs.ReturnLeg;}
    }
    internal void ArrivedAt(MapPoint p,long now)
    {
        if(!Returning && p!=Anchor)Departed=true;
        if(Choices.Count>0 && p==Choices[0].Stance)observed[p]=(map.Revision,now+300);
    }
    internal void Attempt(){if(!Returning)Segments++;}
    internal void ReturnNow(){Returning=true;Choices.Clear();}
    internal void Reject(ExplorationLeg e,long now,long signature)=>failures[(e.From,e.To)]=(signature,now+300);
}
