using System;
using System.Collections.Generic;
using System.Linq;
using CompanionBridge;
using CompanionBridge.Motion;

public static class SurfaceGraphTests
{
    private static int checks;
    private static void Check(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
    // Shape-aware collision DOUBLE. Native collision evidence is collected by
    // verify_server_ai_native.ps1 -LandingRegions, not inferred from this model.
    internal sealed class Terrain : IMotionWorld,ISurfaceTerrain
    {
        internal readonly Dictionary<(int,int),char> Tiles=new();
        internal bool Floor,Unknown;
        internal Func<int,int,char> ReadOverride;
        internal int Reads,Probes;
        internal char Cell(int x,int y)=>ReadOverride!=null?ReadOverride(x,y):Tiles.TryGetValue((x,y),out char c)?c:Unknown?'?':Floor&&y>=100?'#':'.';
        public SurfaceCell ReadSurfaceCell(int x,int y){Reads++;return new(Cell(x,y),Cell(x,y));}
        public IMotionWorld MotionAt(PhysicsState s){Probes++;return this;}
        public long TerrainSignature(PhysicsState s,Prediction e=null)
        {long h=Floor?1:0;foreach(var t in Tiles)h=unchecked(h*31+t.Key.Item1*7+t.Key.Item2*11+t.Value);return h;}
        public bool SafeLanding(PhysicsState s)=>MotionSupport.SafeLanding(s,Cell);
        public bool SafeSweep(PhysicsState a,PhysicsState b,out string why)
        {
            for(int x=(int)Math.Floor(Math.Min(a.Px,b.Px)/16);x<Math.Ceiling((Math.Max(a.Px,b.Px)+20)/16);x++)
                for(int y=(int)Math.Floor(Math.Min(a.Py,b.Py)/16);y<Math.Ceiling((Math.Max(a.Py,b.Py)+42)/16);y++)
                    if(Cell(x,y) is '?' or '!'){why="unknown/hazard";return false;}
            why=null;return true;
        }
        public PhysicsState Move(PhysicsState s,float vx,float vy,float g)
        {
            float x=s.Px+vx,y=s.Py+vy;
            // Full/half side faces and ceilings, plus footprint-dependent floor
            // heights. One-way platforms do not obstruct rising motion.
            for(int tx=(int)Math.Floor(Math.Min(x,s.Px)/16)-1;tx<Math.Ceiling((Math.Max(x,s.Px)+20)/16)+1;tx++)
                for(int ty=(int)Math.Floor(Math.Min(y,s.Py)/16)-1;ty<Math.Ceiling((Math.Max(y,s.Py)+42)/16)+1;ty++)
                {
                    char c=Cell(tx,ty);float top=ty*16+(c=='h'?8:0),bottom=ty*16+16;
                    if(c is not ('#' or 'h'))continue;
                    if(s.Py+42>top+.01f&&s.Py<bottom&&x+20>tx*16&&x<tx*16+16)
                    {x=vx>0?tx*16-20:vx<0?tx*16+16:x;vx=0;}
                    if(vy<0&&s.Py>=bottom&&y<bottom&&x+20>tx*16&&x<tx*16+16){y=bottom;vy=0;}
                }
            bool grounded=false;
            if(vy>=0)
                for(int tx=(int)Math.Floor(x/16);tx<Math.Ceiling((x+20)/16);tx++)
                    for(int ty=(int)Math.Floor((s.Py+42-16)/16);ty<=Math.Floor((y+42+16)/16);ty++)
                    {
                        float surface=ty*16+TerrainClassifier.SurfaceOffset(Cell(tx,ty),Math.Max(0,x-tx*16),Math.Min(16,x+20-tx*16));
                        if(float.IsFinite(surface)&&s.Py+42<=surface+.01f&&y+42>=surface)
                        {y=surface-42;vy=0;grounded=true;}
                    }
            return new(x,y,vx,vy,grounded);
        }
    }
    private static void Refresh(SurfaceGraph graph,Terrain t,int radius=8)
    {foreach(int _ in graph.Refresh(t,100,100,radius)){} }
    private static Route Search(SurfaceGraph graph,Terrain w,PhysicsState s,LandingRegion goal,out SurfaceGraphSnapshot snapshot,int work=512)
    {
        var learning=new NavigationSearchMemory();learning.Scope(w.TerrainSignature(s),PhysicsParameters.Basic,1);
        using var nav=new SurfaceNavigator(graph,s,goal,PhysicsParameters.Basic,w,w,learning,null);
        Route route=null;int slices=0;
        do {route=nav.Advance(work,10);Check(nav.Snapshot.SliceWork<=work,"bounded graph work per slice");}
        while(route.Actions.Count==0&&!nav.Finished&&++slices<10000);
        snapshot=nav.Snapshot;
        Check(slices<10000,"search makes bounded-slice progress");return route;
    }
    public static void Run()
    {
        CheckStepCandidates();
        CheckNodeBudget();
        {
            var t=new Terrain{Floor=true};var pose=new PhysicsState(1600,1558,0,0,true);
            using var pending=new SurfaceNavigator(new(),pose,LandingRegion.AtDestination(2400,1600),PhysicsParameters.Basic,t,t,new(),null);
            Check(pending.Advance(1,0).TimeBudgetReached&&t.Reads==0,"zero slice budget does not touch world or fail journey");
            for(int i=0;i<10;i++)
            {
                var waiting=pending.Advance(1,10);
                Check(waiting.Actions.Count==0&&!pending.Finished&&pending.Snapshot.SliceWork==1,"single-work slices retain scan cursor without unsafe partial input");
            }
            Check(t.Reads==10,"resumed scanning reads new cells instead of repeating the first cell");
        }
        foreach(char shape in new[]{'#','=','h','1','2','3','4','a','b'})
        {
            var t=new Terrain();t.Tiles[(100,100)]=shape;
            var graph=new SurfaceGraph();Refresh(graph,t);
            var nodes=graph.Nodes.ToArray();
            Check(nodes.Length>0,$"native-style shape {shape} produces physically probed stances");
            Check(nodes.All(n=>n.PhysicallyVerified&&n.Clearance&&n.SupportWidth==16),"node records support and clearance proof");
            Check(nodes.Any(n=>n.Region.MinPlayerLeftX<1600),"one tile accepts actual partial overhang");
            if(shape is '1' or '2' or 'a' or 'b')
            {
                Check(nodes.Select(n=>n.Region.SurfaceY).Distinct().Count()>1,"slope heights depend on horizontal position");
                var precise=LandingRegion.AtDestination(shape is '1' or 'a'?1617.25f:1598.75f,1607.25f);
                Check(graph.GoalStances(t,precise,PhysicsParameters.Basic).Any(n=>n!=null&&n.Region.SurfaceY==1607.25f),
                    "fractional slope destination survives graph sampling");
            }
            else if(shape is '#' or '=' or 'h')Check(nodes.Any(n=>n.Region.MaxPlayerLeftX>n.Region.MinPlayerLeftX),"flat stance retains position interval");
            int id=nodes[0].Id,probes=t.Probes;
            Refresh(graph,t);
            Check(graph.Nodes.Any(n=>n.Id==id)&&t.Probes==probes,"unchanged nodes retained without repeated physical probes");
            t.Tiles[(100,98)]='#';Refresh(graph,t);
            Check(graph.Nodes.Count(n=>n.TileY==100)<nodes.Length,"placed overhead obstacle updates body clearance");
            t.Tiles.Remove((100,98));t.Tiles.Remove((100,100));Refresh(graph,t);
            Check(!graph.Nodes.Any(),"removed solid/platform/shape invalidates dependent nodes");
        }
        {
            var t=new Terrain();t.Tiles[(100,100)]='#';t.Tiles[(101,100)]='#';t.Tiles[(107,100)]='#';
            var graph=new SurfaceGraph();Refresh(graph,t);
            Check(graph.Nodes.Any(n=>n.TileX==100)&&graph.Nodes.Any(n=>n.TileX==101),"two tile floor works");
            var unaffected=graph.Nodes.First(n=>n.TileX==107);
            t.Tiles[(100,100)]='h';Refresh(graph,t);
            Check(graph.Nodes.Any(n=>n.Kind==SurfaceKind.Half)&&graph.Nodes.Any(n=>ReferenceEquals(n,unaffected)),"shape update preserves unaffected graph region");
            t.Tiles[(100,100)]='1';Refresh(graph,t);
            Check(graph.Nodes.Any(n=>n.Kind==SurfaceKind.Slope),"slope replacement changes node geometry");
            t.Tiles[(103,99)]='#';Refresh(graph,t);
            Check(graph.Nodes.Any(n=>n.TileX==103),"new support creates new stances");
        }
        {
            var t=new Terrain{Floor=true};var graph=new SurfaceGraph();Refresh(graph,t);
            var a=graph.Nodes.First(n=>n.TileX==100);var b=graph.Nodes.First(n=>n.TileX==104);
            Check(graph.Candidate(a,b,PhysicsParameters.Basic).Motion==SurfaceMotion.Walk,"continuous floor walk candidate");
            var high=b with{Id=999,Region=new(1664,1668,1552)};
            Check(graph.Candidate(a,high,PhysicsParameters.Basic).Motion==SurfaceMotion.Jump,"rise candidate is jump");
            Check(graph.Candidate(high,a,PhysicsParameters.Basic).Motion==SurfaceMotion.Drop,"safe height drop candidate");
            Check(graph.Candidate(high,a with{Region=new(1600,1604,1700)},PhysicsParameters.Basic)==null,"drop exceeds actual model bound");
            Check(graph.Candidate(a,high with{Region=new(1664,1668,1400)},PhysicsParameters.Basic)==null,"jump beyond current profile is not a candidate");
            var edge=graph.Candidate(a,high,PhysicsParameters.Basic);
            t.Tiles[(102,94)]='#';Refresh(graph,t);
            Check(graph.Candidate(a,high,PhysicsParameters.Basic).Signature!=edge.Signature,"jump signature includes intermediate arc obstacles");
            var nodes=graph.Nodes.ToArray();var start=new PhysicsState(1600,1558,0,0,true);var goal=LandingRegion.AtDestination(1740,1600);
            Check(nodes.Count(n=>graph.KeySample(n,start,LandingRegion.AtDestination(3000,1600)))<nodes.Length,"flat interior graph sampling reduces redundant nodes");
            var route=Search(graph,t,start,goal,out var snap);
            Check(route.Actions.Count>0&&snap.CandidateEdges>0&&route.Kind=="surface_graph","A* hands physics-verified route to executor");
            var actual=start;
            foreach(var prediction in route.Actions)foreach(var f in prediction.Frames.Take(prediction.ExecuteFrames))actual=PhysicsSimulator.Step(actual,f.Input,PhysicsParameters.Basic,t);
            Check(PhysicsSimulator.SupportedCheckpoint(actual,t),"partial handoff is a real supported physics checkpoint");
            Check(PhysicsSimulator.Settled(actual,t),"graph handover executes its full safe braking tail");
            if(route.ReachedGoal)
            {
                Check(graph.LastVerifiedEdge?.Verification=="verified"&&graph.LastVerifiedEdge.VerifiedStart==start,"edge validation binds the exact full physics state");
                t.Tiles[(103,95)]='#';Refresh(graph,t);
                Check(graph.LastVerifiedEdge.Verification=="stale","changed trajectory dependencies invalidate verified edge");
            }
            var far=Search(new(),new Terrain{Floor=true},start,LandingRegion.AtDestination(4000,1600),out snap);
            Check(far.Actions.Count>0&&snap.Exit=="safe_partial","outside initial coverage supplies safe partial route");
            // Same journey crosses several original collision/search windows.
            t=new(){Floor=true};var task=new NavigationTask(enableSafeGroundWalk:true){Continuous=true};actual=start;
            goal=LandingRegion.AtDestination(2700,1600);
            for(int frame=0;frame<2400&&!task.Terminal;frame++)
            {var input=task.Before(910,1,goal,actual,PhysicsParameters.Basic,t);actual=PhysicsSimulator.Step(actual,input,PhysicsParameters.Basic,t);task.After(actual,t,input==default);}
            Check(task.Snapshot.Status=="arrived"&&goal.Arrived(actual,t),"one continuous graph journey crosses multiple local ranges and verifies final arrival");
            Check(task.Snapshot.SurfaceGraph!=null&&task.Snapshot.SearchWaitUpdates>0,"graph telemetry and search-wait watchdog are integrated");
        }
        {
            var t=new Terrain{Floor=true};var s=new PhysicsState(1600,1558,2.4f,0,true);
            var route=Search(new(),t,s,LandingRegion.AtDestination(1710,1600),out _);
            var actual=s;
            foreach(var p in route.Actions)foreach(var f in p.Frames.Take(p.ExecuteFrames))
            {actual=PhysicsSimulator.Step(actual,f.Input,PhysicsParameters.Basic,t);Check(actual==f.State,"graph physical replay retains actual start momentum");}
            Check(PhysicsSimulator.Settled(actual,t),"moving launch brakes at verified graph handover");
            var memory=new NavigationFailureMemory();
            var edge=PhysicsSimulator.Predict(s,1,0,PhysicsParameters.Basic,t,walkFrames:24);
            memory.Remember(s,edge,1,t,PhysicsParameters.Basic,"test execution failure",s);
            Check(memory.Reject(s,edge,2,t,PhysicsParameters.Basic),"graph uses existing exact action failure memory");
            t.Tiles[(102,96)]='#';
            Check(!memory.Reject(s,edge,3,t,PhysicsParameters.Basic),"changed terrain reopens failed graph action through Phase 1 memory");
            t=new(){Floor=true};
            var baseline=Search(new(),t,s,LandingRegion.AtDestination(1720,1600),out _);
            memory=new();memory.Remember(s,baseline.Actions[0],1,t,PhysicsParameters.Basic,"known executed action",s);
            int rejects=0;
            using(var nav=new SurfaceNavigator(new(),s,LandingRegion.AtDestination(1720,1600),PhysicsParameters.Basic,t,t,new(),(at,p)=>
                {bool denied=memory.RejectKnown(at,p,2);if(denied)rejects++;return denied;}))
            {
                Route alternative;
                do{alternative=nav.Advance(512,10);}while(alternative.Actions.Count==0&&!nav.Finished);
                Check(rejects>0&&alternative.Actions.Count>0&&!memory.RejectKnown(s,alternative.Actions[0],2),"physical graph validation honors existing failed action bans and finds an alternative");
            }
            var pending=new NavigationTask(enableSafeGroundWalk:true){Continuous=true};
            var goal=LandingRegion.AtDestination(3200,1600);
            pending.Before(912,1,goal,s with{Vx=0},PhysicsParameters.Basic,t);pending.Cancel();
            Check(pending.Before(912,1,goal,s with{Vx=0},PhysicsParameters.Basic,t)==default&&pending.Snapshot.Status=="idle","cancel retires cross-frame graph search without replay");
            var knowledge=new NavigationKnowledge();knowledge.Bind(new object(),"world",1,2);Refresh(knowledge.Surfaces,t);
            Check(knowledge.Surfaces.Nodes.Any(),"root knowledge owns reusable surface cache");
            knowledge.Bind(new object(),"world",1,2);
            Check(!knowledge.Surfaces.Nodes.Any(),"actor replacement invalidates surface knowledge even at same slot/generation");
            using var unsupported=new SurfaceNavigator(new(),s,LandingRegion.AtDestination(1800,1600),PhysicsParameters.Basic with{Gravity=0},t,t,new(),null);
            var unavailable=unsupported.Advance();
            Check(unavailable.SearchExit=="unsupported_model"&&unavailable.Actions.Count==0,"unsupported movement is distinct from no route");
        }
        {
            var t=new Terrain{Floor=true};
            for(int x=99;x<=105;x++)t.Tiles[(x,96)]='#';
            for(int y=96;y<100;y++)t.Tiles[(105,y)]='#';
            var task=new NavigationTask(enableSafeGroundWalk:true){Continuous=true};
            var actual=new PhysicsState(1600,1558,0,0,true);float min=actual.Px;
            var goal=LandingRegion.AtDestination(1832,1600);
            for(int frame=0;frame<2400&&!task.Terminal;frame++)
            {var input=task.Before(911,1,goal,actual,PhysicsParameters.Basic,t);actual=PhysicsSimulator.Step(actual,input,PhysicsParameters.Basic,t);task.After(actual,t,input==default);min=Math.Min(min,actual.Px);}
            Console.WriteLine($"Graph detour: {task.Snapshot.Status}, minX={min}, x={actual.Px}, reason={task.Snapshot.Reason}");
            Check(task.Snapshot.Status=="arrived"&&min<1580,"graph takes a necessary retreat around a low roof to the final goal");
        }
        {
            // The only first stair is outside the initial 24-tile radius.
            // Higher stairs inside it cannot be reached from the ground.
            var t=new Terrain{Floor=true};
            for(int step=0;step<6;step++)for(int dx=0;dx<3;dx++)t.Tiles[(70+step*6+dx,95-step*5)]='=';
            var route=Search(new(),t,new(1600,1558,0,0,true),LandingRegion.AtDestination(1610,1120),out var snap);
            Check(route.Actions.Count>0&&snap.Radius>SurfaceGraph.InitialRadius&&snap.RangeExpansions>0,
                "larger coverage discovers the distant beginning of a platform detour before safe handover");
        }
        {
            var t=new Terrain{Floor=true};
            for(int x=104;x<=108;x++)t.Tiles[(x,96)]='=';
            var task=new NavigationTask(enableSafeGroundWalk:true){Continuous=true};var actual=new PhysicsState(1600,1558,0,0,true);
            var goal=LandingRegion.AtDestination(1685,1536);
            for(int frame=0;frame<1000&&!task.Terminal;frame++)
            {var input=task.Before(913,1,goal,actual,PhysicsParameters.Basic,t);actual=PhysicsSimulator.Step(actual,input,PhysicsParameters.Basic,t);task.After(actual,t,input==default);}
            Check(task.Snapshot.Status=="arrived"&&goal.Arrived(actual,t)&&task.Snapshot.CompletedJumps>0,"graph physically executes solid-to-platform jump and settles at true height");
            task=new(enableSafeGroundWalk:true){Continuous=true};goal=LandingRegion.AtDestination(1820,1600);
            for(int frame=0;frame<1000&&!task.Terminal;frame++)
            {var input=task.Before(914,1,goal,actual,PhysicsParameters.Basic,t);actual=PhysicsSimulator.Step(actual,input,PhysicsParameters.Basic,t);task.After(actual,t,input==default);}
            Check(task.Snapshot.Status=="arrived"&&goal.Arrived(actual,t),"graph physically executes bounded platform-to-solid drop");
        }
        foreach(bool unknown in new[]{false,true})
        {
            var w=new Terrain{Unknown=unknown};var start=new PhysicsState(1600,1558,0,0,true);
            var route=Search(new(),w,start,LandingRegion.AtDestination(1900,1600),out var s);
            Check(route.Actions.Count==0&&s.Radius==SurfaceGraph.MaximumRadius&&s.RangeExpansions>0,"no local path grows coverage incrementally");
            Check(s.Exit==(unknown?"unknown_terrain":"no_route_in_coverage"),"unknown terrain is distinct from exhausted covered graph");
        }
        {
            var t=new Terrain();t.Tiles[(100,100)]='#';var start=new PhysicsState(1600,1558,0,0,true);
            Check(PhysicsSimulator.Settled(start,t),"disconnected island has a genuine stable starting stance");
            var route=Search(new(),t,start,LandingRegion.AtDestination(2200,1600),out var snap);
            Check(route.Actions.Count==0&&snap.Exit=="no_route_in_coverage","known isolated support has no connection in current model/coverage");
        }
        Console.WriteLine($"Surface graph: {checks} checks passed");
    }

    private static void CheckStepCandidates()
    {
        foreach(char shape in new[]{'h','#'})foreach(bool roof in new[]{false,true})
        {
            var terrain=new Terrain{Floor=true};
            for(int x=103;x<=106;x++)terrain.Tiles[(x,99)]=shape;
            if(roof)for(int x=95;x<=110;x++)terrain.Tiles[(x,95)]='#';
            var graph=new SurfaceGraph();Refresh(graph,terrain,16);
            float height=shape=='h'?1592:1584;
            var low=new SurfaceNode(-1,100,100,new(1600,1600,1600),1600,1616,SurfaceKind.Solid,0,true,true);
            var high=graph.Nodes.First(n=>n.TileX==105&&n.Region.SurfaceY==height);
            Check(graph.Candidate(low,high,PhysicsParameters.Basic).Motion==SurfaceMotion.Walk,"half/full tile StepUp remains walk candidate, including low roof");
            Check(graph.Candidate(high,low,PhysicsParameters.Basic).Motion==SurfaceMotion.Walk,"half/full tile StepDown remains walk candidate, including low roof");
            var beyond=low with{Id=-2,TileX=108,Region=new(1728,1728,1600)};
            // Same-height endpoints over a short raised surface still use the
            // height envelope, rather than demanding a flat stance throughout.
            Check(graph.Candidate(low,beyond,PhysicsParameters.Basic).Motion==SurfaceMotion.Walk,"same-height endpoints can walk over a raised step");
        }
        {
            var terrain=new Terrain{Floor=true};
            for(int x=102;x<=105;x++)for(int y=100;y<=105;y++)terrain.Tiles[(x,y)]='.';
            var graph=new SurfaceGraph();Refresh(graph,terrain);
            var left=graph.Nodes.First(n=>n.TileX==100);var right=graph.Nodes.First(n=>n.TileX==107&&n.Region.SurfaceY==1600);
            Check(graph.Candidate(left,right,PhysicsParameters.Basic).Motion==SurfaceMotion.Jump,"genuine unsupported gap still requires jump candidate");
        }
    }

    private static void CheckNodeBudget()
    {
        // Repeated slopes give >8192 valid raw stance samples within the FIRST
        // radius. Exercise real scanning/probing, not injected cache entries.
        Terrain Dense()=>new(){ReadOverride=(x,y)=>y%4==0?'1':'.'};
        var terrain=Dense();var graph=new SurfaceGraph();
        Refresh(graph,terrain,SurfaceGraph.InitialRadius);
        Check(graph.Truncated&&graph.Nodes.Count()==SurfaceGraph.MaximumNodes,"additional valid node beyond 8192 truncates construction at the hard cap");
        Refresh(graph,terrain,SurfaceGraph.InitialRadius);
        Check(graph.Truncated&&graph.Nodes.Count()==SurfaceGraph.MaximumNodes,"explicit later refresh cannot treat partially sampled tile as complete cached coverage");
        var start=new PhysicsState(1600,1558,0,0,true);var goal=LandingRegion.AtDestination(2000,1600);
        terrain=Dense();graph=new();
        using(var nav=new SurfaceNavigator(graph,start,goal,PhysicsParameters.Basic,terrain,terrain,new(),null))
        {
            Route route;int slices=0;
            do
            {
                route=nav.Advance(4096,100);
                if(!nav.Finished)Check(route.TimeBudgetReached&&route.SearchExit=="surface_budget","frame slice retains dense graph scan");
                Check(++slices<1000,"dense graph terminates within bounded scan slices");
            }while(!nav.Finished);
            Check(route.SearchExit=="surface_node_budget"&&!route.TimeBudgetReached&&route.Actions.Count==0,"graph total-node exhaustion is a terminal resource result, not a time slice");
            Check(nav.Snapshot is {Exit:"surface_node_budget",Radius:SurfaceGraph.InitialRadius,RangeExpansions:0,Expansions:0,Nodes:SurfaceGraph.MaximumNodes},"node-budget diagnostic survives without searching or expanding incomplete graph");
            bool refused=false;try{nav.Advance();}catch(InvalidOperationException){refused=true;}
            Check(refused,"completed resource-limited search cannot restart on Advance");
        }
        terrain=Dense();var task=new NavigationTask(){Continuous=true};int updates=0;
        while(!task.Terminal&&updates++<2000)
        {
            var input=task.Before(915,1,goal,start,PhysicsParameters.Basic,terrain);
            Check(input==default,"node-budget scan never dispatches candidate input");task.After(start,terrain,true);
        }
        Check(task.Snapshot is {Status:"failed",Reason:"navigation_surface_node_budget",SearchExit:"surface_node_budget",TimeBudgetReached:false},"navigation ends exactly once with explicit graph resource reason");
        Check(task.Snapshot.SurfaceGraph?.Nodes==SurfaceGraph.MaximumNodes,"navigation retains node-limit telemetry after cleanup");
        int reads=terrain.Reads,probes=terrain.Probes,searches=task.Snapshot.SearchNumber;
        for(int i=0;i<100;i++)Check(task.Before(915,1,goal,start,PhysicsParameters.Basic,terrain)==default,"terminal budget result keeps input released");
        Check(terrain.Reads==reads&&terrain.Probes==probes&&task.Snapshot.SearchNumber==searches,"100 later updates cannot rebuild the same exhausted search");
        Console.WriteLine($"Graph node budget: nodes={task.Snapshot.SurfaceGraph.Nodes}, updates={updates}, terminal={task.Snapshot.Reason}, repeatedReads={terrain.Reads-reads}");
    }
}
