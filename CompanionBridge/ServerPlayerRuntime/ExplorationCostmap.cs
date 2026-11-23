using System;
using System.Collections.Generic;
using System.Linq;

namespace VirtualPlayerEngineProbe;

// A stance is the left body column and the support row, measured in Tiles.
internal readonly record struct TravelProfile(int Rise,int Drop,int WalkUpdates);
internal readonly record struct TravelCell(char Shape,int MiningUpdates=0);
internal sealed record ExplorationLeg(MapPoint From,MapPoint To,int Updates,MapPoint? Cut);
internal sealed record ExplorationChoice(MapPoint Boundary,MapPoint Stance,int Gain,int Outbound,int Return,ExplorationLeg Next);

internal sealed class ExplorationCostmap
{
    private readonly AiMap map;
    private readonly MapWindow bounds;
    private readonly TravelProfile profile;
    private readonly Func<MapPoint,TravelCell> inspect;
    private readonly Dictionary<MapPoint,TravelCell> cells=new();
    internal int InspectedCells=>cells.Count;
    internal ExplorationLeg ReturnLeg{get;private set;}
    internal List<ExplorationChoice> Choices{get;private set;}=new();
    internal ExplorationCostmap(AiMap map,MapWindow bounds,TravelProfile profile,Func<MapPoint,TravelCell> inspect)
    {this.map=map;this.bounds=bounds;this.profile=profile;this.inspect=inspect;}

    private TravelCell Cell(MapPoint p)
    {
        if(!bounds.Contains(p)||map.Get(p.X,p.Y)==0)return new('?');
        if(!cells.TryGetValue(p,out var cell))cells[p]=cell=inspect(p);
        return cell;
    }
    internal bool Supported(MapPoint p)
    {
        char a=Cell(p).Shape,b=Cell(new(p.X+1,p.Y)).Shape;
        return a is '#' or '=' or '.' && b is '#' or '=' or '.' && (a!='.'||b!='.');
    }
    internal ExplorationLeg Edge(MapPoint from,MapPoint to)
    {
        int dy=to.Y-from.Y;
        if(Math.Abs(to.X-from.X)!=1||dy < -profile.Rise||dy>profile.Drop||!Supported(to)||!Supported(from))return null;
        int cost=profile.WalkUpdates+Math.Abs(dy)*(dy<0?12:6);
        MapPoint? cut=null;
        for(int x=to.X;x<to.X+2;x++)
        for(int y=Math.Min(from.Y,to.Y)-3;y<to.Y;y++)
        {
            var p=new MapPoint(x,y);var cell=Cell(p);
            if(cell.Shape=='.')continue;
            if(cell.Shape=='~'&&y==to.Y-1){cost+=6;continue;}
            // A single quoted obstruction is considered on a level approach only.
            if(cell.Shape!='#'||cell.MiningUpdates<=0||dy!=0||cut!=null)return null;
            cut=p;cost+=cell.MiningUpdates+30;
        }
        return new(from,to,cost,cut);
    }
    private sealed class Paths
    {
        internal readonly Dictionary<MapPoint,int> Cost=new();
        internal readonly Dictionary<MapPoint,ExplorationLeg> Next=new();
    }
    private IEnumerable<int> Search(Paths paths,MapPoint start,bool reverse,Func<ExplorationLeg,bool> blocked)
    {
        var queue=new PriorityQueue<MapPoint,(int,int,int)>();
        paths.Cost[start]=0;queue.Enqueue(start,(0,start.X,start.Y));
        while(queue.TryDequeue(out var p,out var priority))
        {
            yield return 1;
            if(paths.Cost[p]!=priority.Item1)continue;
            for(int dx=-1;dx<=1;dx+=2)
            for(int dy= -profile.Rise;dy<=profile.Drop;dy++)
            {
                var n=new MapPoint(p.X+dx,p.Y+(reverse?-dy:dy));
                var edge=reverse?Edge(n,p):Edge(p,n);
                if(edge==null||blocked(edge)||reverse&&edge.Cut!=null)continue;
                int total=priority.Item1+edge.Updates;
                if(total>=paths.Cost.GetValueOrDefault(n,int.MaxValue))continue;
                paths.Cost[n]=total;
                paths.Next[n]=reverse||p==start?edge:paths.Next[p];
                queue.Enqueue(n,(total,n.X,n.Y));
            }
        }
    }
    // Rays stop at the first unseen cell. Never consult the live reader for gain.
    private bool Visible(MapPoint from,MapPoint unknown)
    {
        int x=from.X,y=from.Y,dx=Math.Abs(unknown.X-x),dy=-Math.Abs(unknown.Y-y);
        int sx=Math.Sign(unknown.X-x),sy=Math.Sign(unknown.Y-y),error=dx+dy;
        while(x!=unknown.X||y!=unknown.Y)
        {
            int twice=2*error;
            if(twice>=dy){error+=dy;x+=sx;}
            if(twice<=dx){error+=dx;y+=sy;}
            if(x==unknown.X&&y==unknown.Y)return true;
            if(map.Get(x,y) is 0 or 2)return false;
        }
        return false;
    }
    internal IEnumerable<int> Evaluate(MapPoint start,MapPoint anchor,IEnumerable<Frontier> clusters,
        int remaining,Func<ExplorationLeg,bool> blocked,Func<MapPoint,bool> visited)
    {
        var home=new Paths();
        foreach(int work in Search(home,anchor,true,blocked))yield return work;
        ReturnLeg=home.Next.GetValueOrDefault(start);
        if(clusters==null)yield break;
        var outbound=new Paths();
        foreach(int work in Search(outbound,start,false,blocked))yield return work;
        var choices=new Dictionary<MapPoint,ExplorationChoice>();
        var borders=new Dictionary<MapPoint,List<MapPoint>>();
        foreach(var cluster in clusters)
        foreach(var p in cluster.Unknown)
        {
            if(!bounds.Contains(p))continue;
            if(!borders.TryGetValue(p,out var keys))borders[p]=keys=new();
            keys.Add(cluster.Key);
        }
        foreach(var (p,distance) in outbound.Cost)
        {
            yield return 1;
            if(p==start || visited(p) || !home.Cost.TryGetValue(p,out int back)||distance+back+45>remaining)continue;
            var eye=new MapPoint(p.X,p.Y-2);
            var gains=new Dictionary<MapPoint,int>();
            for(int y=-4;y<=4;y++)
            for(int x=-4;x<=4;x++)
            {
                var target=new MapPoint(eye.X+x,eye.Y+y);
                if(x*x+y*y>16 || !borders.TryGetValue(target,out var keys)||!Visible(eye,target))continue;
                foreach(var key in keys)gains[key]=gains.GetValueOrDefault(key)+1;
            }
            foreach(var (key,gain) in gains)
            {
                var candidate=new ExplorationChoice(key,p,gain,distance,back,outbound.Next[p]);
                if(!choices.TryGetValue(key,out var old)||Compare(candidate,old)<0)choices[key]=candidate;
            }
        }
        Choices=choices.Values.OrderBy(x=>x,Comparer<ExplorationChoice>.Create(Compare)).Take(3).ToList();
    }
    private static int Compare(ExplorationChoice a,ExplorationChoice b)
    {
        int score=((long)b.Gain*(a.Outbound+a.Return+15)).CompareTo((long)a.Gain*(b.Outbound+b.Return+15));
        return score!=0?score:(a.Outbound,a.Return,a.Stance.X,a.Stance.Y,a.Boundary.X,a.Boundary.Y)
            .CompareTo((b.Outbound,b.Return,b.Stance.X,b.Stance.Y,b.Boundary.X,b.Boundary.Y));
    }
}
