using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using VirtualPlayerEngineProbe;

public static class FrontierReplayTests
{
    internal static AiMap Grid(params string[] rows)
    {
        var map=new AiMap(Guid.NewGuid(),"fixture",rows[0].Length,rows.Length);
        for(int y=0;y<rows.Length;y++)
        for(int x=0;x<map.Width;x++)
        {
            int value="? #=~LHS".IndexOf(rows[y][x]);
            if(value<0)throw new Exception("Unknown fixture symbol");
            if(value>0)map.Remember(x,y,(byte)value);
        }
        return map;
    }
    internal static void Check(bool value,string reason)
    {
        if(!value)throw new Exception(reason);
    }
    public static void Run()
    {
        var map=Grid("    ??","#?###?");
        var clusters=ExplorationFrontiers.Find(map,new(0,0,2,0));
        Check(clusters.Count==1 && clusters[0].Unknown.SetEquals(new[]{new MapPoint(1,1)}),
            "clipped window, wall or world edge created a frontier");
        var seam=Grid(new string(' ',34),new string('?',34));
        clusters=ExplorationFrontiers.Find(seam,new(0,0,33,0));
        Check(clusters.Count==1 && clusters[0].Unknown.Count==34,"chunk seam split frontier");
        var wall=Grid(" #?"," #?");
        Check(ExplorationFrontiers.Find(wall,new(-1,-1,3,2)).Count==0,"wall revealed hidden space");
        Check(wall.Get(2,0)==0,"read-only detection revealed cells");
        Console.WriteLine("Frontier replay passed: window halo, bounds, wall, unknown, chunk seam");
        CostAndReturn();
        BoundedWork();
    }
    private static void CostAndReturn()
    {
        var map=Grid("??###########??","?             ?","?             ?","?             ?","###############");
        var anchor=new MapPoint(7,4);
        var trip=new ExplorationTrip(map,anchor,12,8);
        MapPoint? blocked=null;
        TravelCell Inspect(MapPoint p)=>p==blocked?new('!'):new("? #=~LHS"[map.Get(p.X,p.Y)]==' '?'.':"? #=~LHS"[map.Get(p.X,p.Y)]);
        ExplorationCostmap Costs()=>new(map,trip.Window,new(1,3,16),Inspect);
        var at=anchor;
        ExplorationLeg Decide(long now)
        {
            var costs=Costs();
            foreach(var work in trip.Plan(costs,at,now,1000,_=>1))Check(work==1,"invalid work slice");
            return trip.Next;
        }
        var first=Decide(0);
        Check(first!=null,"visible branches lost");
        Check(trip.Choices.Select(c=>c.Boundary).Distinct().Count()>=2,"fixture needs two distinct openings");
        var nearest=trip.Choices.OrderBy(c=>c.Outbound).First();
        int travel=0;
        trip.Reject(first,1,1);
        var other=Decide(2);
        Check(other!=null && other.To.X-at.X==at.X-first.To.X,"failed branch was repeated");
        blocked=new(other.To.X+(other.To.X>at.X?1:0),at.Y-1);
        Check(Costs().Edge(at,other.To)==null,"multiplayer obstacle retained cached route");
        blocked=null;
        var route=new HashSet<(MapPoint,MapPoint)>();
        var chosen=trip.Choices[0];
        while(at!=chosen.Stance)
        {
            Check(other!=null && route.Add((at,other.To)),"repeated outbound entry");
            trip.Attempt();travel+=other.Updates;at=other.To;
            if(at!=chosen.Stance)other=Decide(travel);
        }
        trip.Observe();
        Check(trip.NewTiles==0,"inspection inflated progress");
        map.Remember(at.X<anchor.X?1:13,0,1);
        while(at!=anchor)
        {
            var home=Decide(travel);
            Check(trip.NewTiles==1 && trip.Returning && home!=null,"return path missing");
            travel+=home.Updates;at=home.To;
        }
        trip.Observe();Check(trip.NewTiles==1,"repeated observation counted twice");
        var shaft=Grid("   ????","    ???","     ??","#    ??","     ??"," ######");
        var window=new MapWindow(0,0,6,5);
        TravelCell Shaft(MapPoint p)=>new(shaft.Get(p.X,p.Y)==1?'.':'#');
        var limited=new ExplorationCostmap(shaft,window,new(0,3,16),Shaft);
        var rim=new MapPoint(0,3);var bottom=new MapPoint(1,5);
        foreach(var _ in limited.Evaluate(bottom,rim,null,500,_=>false,_=>false)){}
        Check(limited.Edge(rim,bottom)!=null && limited.ReturnLeg==null,"one-way shaft classified reversible");
        foreach(var _ in limited.Evaluate(rim,rim,ExplorationFrontiers.Find(shaft,window),500,_=>false,_=>false)){}
        Check(limited.Choices.Count==0,"unsafe descent offered");
        var climber=new ExplorationCostmap(shaft,window,new(2,3,16),Shaft);
        foreach(var _ in climber.Evaluate(bottom,rim,null,500,_=>false,_=>false)){}
        Check(climber.ReturnLeg?.To==rim,"injected rise capability did not change return estimate");
        var passage=Grid("      ","      ","   #  ","######");
        var flat=new MapWindow(0,0,5,3);
        ExplorationLeg Cross(char obstruction,int quote)=>new ExplorationCostmap(passage,flat,new(1,2,16),
            p=>p==new MapPoint(3,2)?new(obstruction,quote):new(p.Y==3?'#':'.')).Edge(new(1,3),new(2,3));
        Check(Cross('#',0)==null && Cross('#',50).Updates>Cross('.',0).Updates,"unpriced or cheap solid shortcut");
        Check(Cross('~',0).Updates>Cross('.',0).Updates && Cross('!',0)==null,"water/lava costs");
        Console.WriteLine($"Fork replay: nearest first stance={nearest.Stance.X}, cost first step={first.To.X}, alternate stance={chosen.Stance.X}, repeated outbound edges=0, returned=1/1, new tiles/100 travel updates={100d/travel:F2}");
    }
    private static void BoundedWork()
    {
        var big=new AiMap(Guid.NewGuid(),"large",8400,2400);
        for(int x=4186;x<=4214;x++)
        for(int y=1197;y<=1200;y++)big.Remember(x,y,(byte)(y==1200?2:1));
        var anchor=new MapPoint(4200,1200);
        var trip=new ExplorationTrip(big,anchor,12,8);
        int reads=0;
        TravelCell Inspect(MapPoint p)
        {
            reads++;
            Check(trip.Window.Contains(p)&&big.Get(p.X,p.Y)!=0,"out of window or unknown live read");
            return new(p.Y==1200?'#':'.');
        }
        (ExplorationLeg Next,int Slices,int Reads) Run(int limit)
        {
            reads=0;int slices=0;
            var costs=new ExplorationCostmap(big,trip.Window,new(1,3,24),Inspect);
            using var plan=trip.Plan(costs,anchor,0,1000,_=>1).GetEnumerator();
            bool more=true;
            while(more)
            {
                slices++;
                for(int i=0;i<limit;i++)if(!plan.MoveNext()){more=false;break;}
            }
            Check(costs.InspectedCells<=625 && costs.InspectedCells==reads,"costmap cache exceeded window or duplicate reads");
            return (trip.Next,slices,reads);
        }
        var clock=Stopwatch.StartNew();
        var cold=Run(10000);
        big.Remember(1,1,1);
        var hot=Run(8);
        Check(cold.Next==hot.Next && cold.Next!=null && hot.Slices>1 && hot.Reads==cold.Reads,"sliced planning differed");
        Console.WriteLine($"8400x2400 replay: {hot.Reads} local cells, {hot.Slices} slices of 8, two decisions {clock.Elapsed.TotalMilliseconds:F1} ms");
    }
}
