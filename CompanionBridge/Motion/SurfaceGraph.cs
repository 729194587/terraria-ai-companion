using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace CompanionBridge.Motion;

// All methods run on the simulation thread. This is a bounded terrain reader,
// not an extension of the 45x29 collision window or a background world snapshot.
internal interface ISurfaceTerrain
{
    SurfaceCell ReadSurfaceCell(int x,int y);
    IMotionWorld MotionAt(PhysicsState pose);
}

internal readonly record struct SurfaceCell(char Shape,long Signature,TileLiquid Liquid=default,bool NegatesFallDamage=false);
internal enum SurfaceKind { Solid, Platform, Half, Slope, PlatformSlope }
internal enum SurfaceMotion { Walk, Jump, Drop }
internal sealed record SurfaceNode(int Id,int TileX,int TileY,LandingRegion Region,
    float SupportLeft,float SupportRight,SurfaceKind Kind,long Signature,
    bool Clearance,bool PhysicallyVerified)
{
    internal PhysicsState Rest(float x)=>new(x,Region.SurfaceY-PhysicsSimulator.PlayerH,0,0,true);
    internal float X=>(Region.MinPlayerLeftX+Region.MaxPlayerLeftX)/2;
    internal float SupportWidth=>SupportRight-SupportLeft;
}
internal sealed record SurfaceEdge(SurfaceNode From,SurfaceNode To,SurfaceMotion Motion,
    LandingRegion Launch,LandingRegion Landing,float Cost,long Signature,
    string Verification="candidate",PhysicsState? VerifiedStart=null,
    PhysicsState? VerifiedLanding=null,PhysicsParameters Physics=null);
internal sealed record SurfaceGraphSnapshot(string Exit,int Radius,int TileReads,int Nodes,
    int CandidateEdges,int VerifiedEdges,int Expansions,int SliceWork,double SliceMilliseconds,
    int ChangedTiles,int RetainedNodes,int RangeExpansions,LandingRegion Waypoint=null,
    float? EstimatedRouteUpdates=null,bool CandidateReachedGoal=false);

// Per-root cache. Raw stance intervals are kept even when the graph uses fewer
// samples on ordinary flat spans. A tile alone never certifies a stance.
internal sealed class SurfaceGraph
{
    internal const int InitialRadius=24,MaximumRadius=128,MaximumNodes=40000;
    private readonly Dictionary<(int X,int Y),SurfaceCell> cells=new();
    private readonly Dictionary<(int X,int Y),List<SurfaceNode>> surfaces=new();
    private int nextId;
    private (int X,int Y)? incompleteTile;
    internal SurfaceEdge LastVerifiedEdge {get;private set;}
    private IMotionWorld verifiedWorld;
    private Prediction verifiedFootprint;
    internal int ChangedTiles {get;private set;}
    internal int RetainedNodes {get;private set;}
    internal bool Truncated {get;private set;}
    internal IEnumerable<SurfaceNode> Nodes=>surfaces.Values.SelectMany(x=>x);
    internal void Clear(){cells.Clear();surfaces.Clear();nextId=0;incompleteTile=null;Truncated=false;LastVerifiedEdge=null;verifiedWorld=null;verifiedFootprint=null;}
    internal void RecordVerified(SurfaceEdge edge,PhysicsState start,Route route,PhysicsParameters ph,IMotionWorld world)
    {
        // Cover the complete simulated trajectory, including every braking tail,
        // rather than only the support tiles or the graph's geometric envelope.
        verifiedFootprint=new(true,"verified graph connection",route.Actions.SelectMany(a=>a.Frames).ToList(),route.Actions[^1].Landing,0,false);
        verifiedWorld=world;
        LastVerifiedEdge=edge with{Verification="verified",VerifiedStart=start,VerifiedLanding=route.Actions[^1].Landing,
            Physics=ph,Signature=world.TerrainSignature(start,verifiedFootprint)};
    }
    internal char Shape(int x,int y)=>cells.TryGetValue((x,y),out var c)?c.Shape:'?';
    internal long Stamp(int left,int top,int right,int bottom)
    {
        unchecked
        {
            long h=1469598103934665603;
            for(int x=left;x<=right;x++)for(int y=top;y<=bottom;y++)
                h=(h^(cells.TryGetValue((x,y),out var c)?c.Signature:long.MinValue))*1099511628211;
            return h;
        }
    }

    // Each yield is one bounded piece of work (one tile read or stance probe).
    // Re-read coverage on a new plan; preserve nodes with unchanged dependencies.
    internal IEnumerable<int> Refresh(ISurfaceTerrain source,int cx,int cy,int radius,PhysicsParameters physics=null,int downwardRows=0)
    {
        physics??=PhysicsParameters.Basic;
        if(physics.Air!=null)physics=physics with{Air=null}; // Static stances have no Actor resources.
        int height=Math.Min(64,Math.Max(16,radius/2));
        int l=cx-radius,r=cx+radius,t=cy-height,b=cy+Math.Max(height,downwardRows);
        var dirty=new HashSet<(int X,int Y)>();
        ChangedTiles=0;RetainedNodes=0;Truncated=false;
        // A later, explicitly requested refresh must not mistake the last
        // partially sampled tile for complete cached coverage.
        if(incompleteTile is {} unfinished){surfaces.Remove(unfinished);incompleteTile=null;}
        // Evict before reading the new window and accounting for its budget.
        // Otherwise expired nodes can exhaust the cap before the old end-of-
        // refresh cleanup is reached, even when the new region is sparse.
        foreach(var key in cells.Keys.Where(k=>Math.Abs(k.X-cx)>MaximumRadius+8||Math.Abs(k.Y-cy)>80).ToArray())
        {cells.Remove(key);yield return 0;}
        foreach(var key in surfaces.Keys.Where(k=>Math.Abs(k.X-cx)>MaximumRadius+4||Math.Abs(k.Y-cy)>72).ToArray())
        {surfaces.Remove(key);yield return 0;}
        if(LastVerifiedEdge?.Verification=="verified"&&verifiedWorld.TerrainSignature(LastVerifiedEdge.VerifiedStart.Value,verifiedFootprint)!=LastVerifiedEdge.Signature)
            LastVerifiedEdge=LastVerifiedEdge with{Verification="stale"};
        // The extra rim covers body clearance and native step/collision probes.
        for(int x=l-3;x<=r+3;x++)for(int y=t-5;y<=b+2;y++)
        {
            var cell=source.ReadSurfaceCell(x,y);
            if(!cells.TryGetValue((x,y),out var previous)||previous!=cell)
            {
                cells[(x,y)]=cell;ChangedTiles++;
                // A body may overhang a support and touches three rows above it.
                for(int sx=x-3;sx<=x+3;sx++)for(int sy=y-2;sy<=y+5;sy++)dirty.Add((sx,sy));
            }
            yield return 1;
        }
        foreach(var key in dirty){surfaces.Remove(key);yield return 0;}
        RetainedNodes=surfaces.Values.Sum(v=>v.Count);
        if(RetainedNodes>=MaximumNodes*3/4)
        {
            // Under pressure, give the next local window room before keeping
            // distant samples from a previously expanded window.
            foreach(var key in surfaces.Keys.Where(k=>k.X<l||k.X>r||k.Y<t||k.Y>b).ToArray())
            {RetainedNodes-=surfaces[key].Count;surfaces.Remove(key);yield return 0;}
        }
        int count=RetainedNodes;
        // Keep the origin represented even when a dense outer rim fills the cache.
        foreach(int x in Enumerable.Range(l,r-l+1).OrderBy(x=>Math.Abs(x-cx)))
        for(int y=t;y<=b;y++)
        {
            if(surfaces.ContainsKey((x,y)))continue;
            char shape=Shape(x,y);
            if(shape is not ('#' or '=' or 'a' or 'b' or 'h' or '1' or '2' or '3' or '4'))continue;
            if(Shape(x,y-1) is '#' or 'h')
            {surfaces[(x,y)]=new();yield return 0;continue;}
            var kind=shape=='='?SurfaceKind.Platform:shape=='h'?SurfaceKind.Half:
                shape is 'a' or 'b'?SurfaceKind.PlatformSlope:
                shape is '1' or '2' or '3' or '4'?SurfaceKind.Slope:SurfaceKind.Solid;
            var found=new List<SurfaceNode>();
            float? lo=null;float hi=0,feet=0;
            // Include partial overhangs on BOTH sides. Slopes keep position-
            // dependent heights; never combine them into a horizontal region.
            for(int offset=-19;offset<=15;offset++)
            {
                float px=x*16+offset;
                float localLeft=Math.Max(0,px-x*16),localRight=Math.Min(16,px+20-x*16);
                float fy=y*16+TerrainClassifier.SurfaceOffset(shape,localLeft,localRight);
                var pose=new PhysicsState(px,fy-42,0,0,true);
                bool valid=float.IsFinite(fy)&&MotionSupport.ClearanceRejection(pose,Shape)==null;
                if(valid)
                {
                    var w=source.MotionAt(pose);
                    // Same native contact and settling rules as execution.
                    valid=StableStance(pose,physics,w);
                }
                if(valid)
                {
                    if(lo.HasValue&&(Math.Abs(feet-fy)>.01f||kind is SurfaceKind.Slope or SurfaceKind.PlatformSlope))Flush();
                    lo??=px;hi=px;feet=fy;
                }
                else Flush();
                if(Truncated){surfaces[(x,y)]=found;incompleteTile=(x,y);yield break;}
                yield return 0;
            }
            Flush();surfaces[(x,y)]=found;
            if(Truncated){incompleteTile=(x,y);yield break;}
            count+=found.Count;
            void Flush()
            {
                if(!lo.HasValue)return;
                // This is a total graph resource limit, not a resumable frame
                // slice. Keep the cache bounded even inside one dense tile.
                if(count+found.Count>=MaximumNodes){Truncated=true;lo=null;return;}
                found.Add(new(++nextId,x,y,new(lo.Value,hi,feet),x*16,x*16+16,kind,
                    Stamp(x-3,y-5,x+3,y+2),true,true));
                lo=null;
            }
        }
    }

    // An observed destination can have a fractional slope height. Integer graph
    // samples must not quantize away its physically valid stance. Add bounded,
    // native-verified samples at the exact goal height, including slope inverses.
    internal IEnumerable<SurfaceNode> GoalStances(ISurfaceTerrain source,LandingRegion goal,PhysicsParameters ph)
    {
        if(ph.Air!=null)ph=ph with{Air=null};
        for(int x=(int)Math.Floor(goal.MinPlayerLeftX/16);x<=Math.Floor((goal.MaxPlayerLeftX+20)/16);x++)
            for(int y=(int)Math.Floor(goal.SurfaceY/16)-1;y<=Math.Floor(goal.SurfaceY/16);y++)
            {
                char shape=Shape(x,y);
                if(shape is not ('#' or '=' or 'a' or 'b' or 'h' or '1' or '2' or '3' or '4'))continue;
                var xs=new HashSet<float>{goal.MinPlayerLeftX,goal.MaxPlayerLeftX,(goal.MinPlayerLeftX+goal.MaxPlayerLeftX)/2,
                    x*16+goal.SurfaceY-y*16,x*16+16-(goal.SurfaceY-y*16)-20};
                foreach(float px in xs)
                {
                    SurfaceNode node=null;
                    if(px>=goal.MinPlayerLeftX&&px<=goal.MaxPlayerLeftX)
                    {
                        var pose=new PhysicsState(px,goal.SurfaceY-42,0,0,true);
                        var w=source.MotionAt(pose);
                        if(StableStance(pose,ph,w))node=new(++nextId,x,y,new(px,px,goal.SurfaceY),x*16,x*16+16,
                            shape=='='?SurfaceKind.Platform:shape=='h'?SurfaceKind.Half:shape is 'a' or 'b'?SurfaceKind.PlatformSlope:
                            shape is '1' or '2' or '3' or '4'?SurfaceKind.Slope:SurfaceKind.Solid,Stamp(x-3,y-5,x+3,y+2),true,true);
                    }
                    yield return node;
                }
            }
    }

    private static bool StableStance(PhysicsState pose,PhysicsParameters ph,IMotionWorld world)
    {
        if(!PhysicsSimulator.Settled(pose,world))return false;
        try
        {
            var next=PhysicsSimulator.Step(pose,default,ph,world);
            return PhysicsSimulator.Settled(next,world)&&Math.Abs(next.Px-pose.Px)<.01f&&Math.Abs(next.Py-pose.Py)<.01f;
        }
        catch(UnsafeMotionException){return false;}
    }

    internal bool KeySample(SurfaceNode n,PhysicsState start,LandingRegion goal)
    {
#if NAVIGATION_EXPERIMENT
        if(NavigationExperiment.AllStances)return true;
#endif
        if(n.Kind!=SurfaceKind.Solid||Math.Abs(n.X-start.Px)<48||Math.Abs(n.X-goal.MinPlayerLeftX)<64)return true;
        // Flat interior reduction only. Keep every narrow support, boundary,
        // height transition and obstacle-adjacent tile.
        for(int dx=-1;dx<=1;dx+=2)
        {
            if(Shape(n.TileX+dx,n.TileY)!='#')return true;
            for(int dy=1;dy<=3;dy++)if(Shape(n.TileX+dx,n.TileY-dy)!='.')return true;
        }
        return n.TileX%4==0;
    }

    internal static int HorizontalRange(PhysicsParameters ph)
    {
#if NAVIGATION_EXPERIMENT
        if(NavigationExperiment.HorizontalRange is int range)return range;
#endif
        return ph.ExtraJump==null&&ph.Air==null?144:256;
    }

    internal SurfaceEdge Candidate(SurfaceNode a,SurfaceNode b,PhysicsParameters ph)
    {
        float dx=Math.Abs(a.X-b.X),dy=b.Region.SurfaceY-a.Region.SurfaceY;
        float extraRise=ph.ExtraJump==null?0:ph.JumpSpeed*ph.ExtraJump.HoldFrames(ph)+ph.JumpSpeed*ph.JumpSpeed/(2*ph.Gravity);
        if(ph.Air!=null)extraRise+=ph.JumpSpeed*16;
        if(a.Id==b.Id||dx>HorizontalRange(ph)||dy>FallSafety.LookaheadPixels
            ||dy<-(ph.JumpSpeed*ph.JumpHeight+ph.JumpSpeed*ph.JumpSpeed/(2*ph.Gravity)+16+extraRise))return null;
        if(dy>16&&!ph.Fall.Safe(a.Region.SurfaceY-PhysicsSimulator.PlayerH,b.Region.SurfaceY-PhysicsSimulator.PlayerH))
        {
            int row=FallSafety.Row(b.Region.SurfaceY+1);
            if(!Enumerable.Range(FallSafety.Row(b.Region.MinPlayerLeftX),
                FallSafety.Row(b.Region.MaxPlayerLeftX+PhysicsSimulator.PlayerW)-FallSafety.Row(b.Region.MinPlayerLeftX)+1)
                .Any(x=>cells.TryGetValue((x,row),out var cell)&&cell.NegatesFallDamage))return null;
        }
        if(dx<2&&Math.Abs(dy)<1)return null;
        SurfaceMotion kind=dy>16?SurfaceMotion.Drop:Math.Abs(dy)<=16?SurfaceMotion.Walk:SurfaceMotion.Jump;
        if(kind==SurfaceMotion.Walk)
        {
            // Candidate support can change height as native StepUp/StepDown
            // moves the body. Testing every X at a.Rest's fixed Y incorrectly
            // turns half/full-brick steps into mandatory jumps under low roofs.
            // This envelope only preserves walking candidates; it does not prove
            // clearance through the transition or implement native step physics.
            for(float px=Math.Min(a.X,b.X);px<=Math.Max(a.X,b.X);px+=8)
            {
                float minFeet=Math.Min(a.Region.SurfaceY,b.Region.SurfaceY)-16;
                float maxFeet=Math.Max(a.Region.SurfaceY,b.Region.SurfaceY)+16;
                bool supported=false;
                for(int x=(int)Math.Floor(px/16);x<Math.Ceiling((px+20)/16)&&!supported;x++)
                    for(int y=(int)Math.Floor(minFeet/16);y<=Math.Floor(maxFeet/16)&&!supported;y++)
                    {
                        float feet=y*16+TerrainClassifier.SurfaceOffset(Shape(x,y),Math.Max(0,px-x*16),Math.Min(16,px+20-x*16));
                        supported=feet>=minFeet&&feet<=maxFeet&&MotionSupport.SafeLanding(new(px,feet-42,0,0,true),Shape);
                    }
                if(!supported){kind=SurfaceMotion.Jump;break;}
            }
        }
        int l=(int)Math.Floor(Math.Min(a.X,b.X)/16)-3,r=(int)Math.Ceiling((Math.Max(a.X,b.X)+20)/16)+3;
        int t=(int)Math.Floor((Math.Min(a.Region.SurfaceY,b.Region.SurfaceY)-42-160)/16),bottom=(int)Math.Ceiling(Math.Max(a.Region.SurfaceY,b.Region.SurfaceY)/16)+2;
        float wetDistance=0;
        if(ph.CanWade&&kind==SurfaceMotion.Walk)
        {
            // Rank crossings by estimated updates. Native simulation verifies
            // the connection and prices it from its actual input frame count.
            float from=Math.Min(a.X,b.X),to=Math.Max(a.X,b.X);
            for(float px=from;px<to;px+=8)
            {
                float feet=a.Region.SurfaceY+(b.Region.SurfaceY-a.Region.SurfaceY)*Math.Abs(px-a.X)/Math.Max(1,dx);
                int x=FallSafety.Row(px+10),y=FallSafety.Row(feet-8);
                if(cells.TryGetValue((x,y),out var cell)&&cell.Liquid.Kind==LiquidKind.Water)
                    wetDistance+=Math.Min(8,to-px);
            }
        }
        return new(a,b,kind,a.Region,b.Region,Math.Max(1,(dx+wetDistance)/ph.MaxRun)
            +Math.Abs(dy)/ph.MaxRun+(kind==SurfaceMotion.Jump?20:0),Stamp(l,t,r,bottom));
    }
}

// Graph search supplies subgoals. Only existing full-state physical routes are
// returned to NavigationTask; candidate edges can never produce player inputs.
internal sealed class SurfaceNavigator : IDisposable
{
    private readonly IEnumerator<Route> steps;
    private readonly Stopwatch timer=new();
    private int work,limit,reads,candidates,verified,expanded,radius,rangeExpansions;
    private double milliseconds;
    private string exit="budget";
    private LandingRegion waypoint;
    private float? estimatedRouteUpdates;
    private bool candidateReachedGoal;
    private readonly SurfaceGraph graph;
    private readonly Func<PhysicsState,bool> detour;
    private readonly Func<PhysicsState,int> preference;
    internal SurfaceGraphSnapshot Snapshot=>new(exit,radius,reads,graph.Nodes.Count(),candidates,verified,expanded,
        work,timer.Elapsed.TotalMilliseconds,graph.ChangedTiles,graph.RetainedNodes,rangeExpansions,waypoint,estimatedRouteUpdates,candidateReachedGoal);
    internal bool Finished {get;private set;}
    internal SurfaceNavigator(SurfaceGraph graph,PhysicsState start,LandingRegion goal,PhysicsParameters ph,
        IMotionWorld world,ISurfaceTerrain terrain,NavigationSearchMemory learning,
        Func<PhysicsState,Prediction,bool> reject,Func<PhysicsState,bool> detour=null,Func<PhysicsState,int> preference=null)
    {
        this.graph=graph;
        this.detour=detour;this.preference=preference;
        steps=Run(start,goal,ph,world,terrain,learning,reject).GetEnumerator();
    }
    internal Route Advance(int maxWork=512,double budgetMs=4)
    {
        if(Finished)throw new InvalidOperationException("Surface search already finished");
        work=0;limit=maxWork;milliseconds=budgetMs;timer.Restart();
        if(maxWork<=0||budgetMs<=0)return Waiting();
        if(!steps.MoveNext())throw new InvalidOperationException("Surface search yielded no result");
        return steps.Current;
    }
    private bool YieldWork(){work++;return work>=limit||timer.Elapsed.TotalMilliseconds>=milliseconds;}
    private Route Waiting()=>new(new(),0,timer.Elapsed.TotalMilliseconds,TimeBudgetReached:true,Kind:"surface_graph",SearchExit:"surface_budget");
    public void Dispose()=>steps.Dispose();

    private IEnumerable<Route> Run(PhysicsState start,LandingRegion goal,PhysicsParameters ph,IMotionWorld world,
        ISurfaceTerrain terrain,NavigationSearchMemory learning,Func<PhysicsState,Prediction,bool> reject)
    {
        if(!ph.IsSupportedGroundPhysics)
        {
            exit="unsupported_model";Finished=true;
            yield return new(new(),0,0,FailureReason:exit,Kind:"surface_graph",SearchExit:exit);
            yield break;
        }
        int cx=(int)Math.Floor((start.Px+10)/16),cy=(int)Math.Floor((start.Py+42)/16);
        var denied=new HashSet<int>(); // search-local exclusions, NOT execution failure memory
        var uncertain=new HashSet<int>();
        bool localBudget=false;
        int topRows=0,bottomRows=0;
        for(radius=SurfaceGraph.InitialRadius;radius<=SurfaceGraph.MaximumRadius;radius=Math.Min(SurfaceGraph.MaximumRadius,radius*2))
        {
            topRows=Math.Min(64,Math.Max(16,radius/2));
            bool goalInColumns=Math.Abs((goal.MinPlayerLeftX+goal.MaxPlayerLeftX)/32-cx)<=radius;
            // A nearby X with a low destination needs vertical coverage before
            // accepting an upper ledge prefix merely because it is closer in X.
            int towardGoal=goalInColumns?Math.Clamp((int)Math.Ceiling(goal.SurfaceY/16)-cy,0,FallSafety.LookaheadPixels/16):0;
            bottomRows=Math.Max(topRows,towardGoal);
            using(var refresh=graph.Refresh(terrain,cx,cy,radius,ph,bottomRows).GetEnumerator())
                while(refresh.MoveNext())
                {
                    reads+=refresh.Current;
                    if(YieldWork())yield return Waiting();
                }
            var selected=graph.Nodes.Where(n=>Math.Abs(n.TileX-cx)<=radius&&n.TileY>=cy-topRows&&n.TileY<=cy+bottomRows
                &&graph.KeySample(n,start,goal)).ToList();
            if(goalInColumns&&goal.SurfaceY/16>=cy-topRows&&goal.SurfaceY/16<=cy+bottomRows)
                foreach(var stance in graph.GoalStances(terrain,goal,ph))
                {if(stance!=null)selected.Add(stance);if(YieldWork())yield return Waiting();}
            var nodes=selected.ToArray();
            var origin=new SurfaceNode(-1,cx,cy,new(start.Px,start.Px,start.Py+42),start.Px,start.Px+20,SurfaceKind.Solid,world.TerrainSignature(start),true,true);
            var launches=new Dictionary<int,LandingRegion>();
            // Re-run structural A* when physical validation rejects its first
            // connection. Rejections apply only to this exact observed start.
            while(true)
            {
                var open=new PriorityQueue<SurfaceNode,float>();
                var costs=new Dictionary<int,float>{{-1,0}};
                var came=new Dictionary<int,SurfaceEdge>();
                var closed=new HashSet<int>();
                var byColumn=nodes.GroupBy(n=>n.TileX).ToDictionary(g=>g.Key,g=>g.ToArray());
                open.Enqueue(origin,goal.Distance(start)/ph.MaxRun);
                SurfaceNode end=null,best=origin;
                float bestDistance=goal.Distance(start);
                while(open.TryDequeue(out var a,out _))
                {
                    if(!closed.Add(a.Id))continue;
                    expanded++;
                    float d=goal.Distance(a.Rest(Math.Clamp((goal.MinPlayerLeftX+goal.MaxPlayerLeftX)/2,a.Region.MinPlayerLeftX,a.Region.MaxPlayerLeftX)));
                    if(d<bestDistance){bestDistance=d;best=a;}
                    if(d==0){end=a;break;}
                    int columns=(SurfaceGraph.HorizontalRange(ph)+15)/16+1;
                    for(int column=a.TileX-columns;column<=a.TileX+columns;column++)
                    {
                        if(!byColumn.TryGetValue(column,out var neighbors))continue;
                        foreach(var b in neighbors)
                        {
                            if(closed.Contains(b.Id)||(a.Id==-1||a.Region.Contains(start))&&denied.Contains(b.Id))continue;
                            var edge=graph.Candidate(a,b,ph);candidates++;
                            if(edge!=null)
                            {
                                float launchCost=0;
                                if(edge.Motion==SurfaceMotion.Jump)
                                {
                                    if(!launches.TryGetValue(a.Id,out var launch))
                                    {launch=JumpLaunch(a,start,terrain,ph);launches[a.Id]=launch;}
                                    if(launch==null)launchCost=256; // uncertain, never a hard infeasibility ban
                                    else edge=edge with{Launch=launch};
                                }
                                float cost=costs[a.Id]+edge.Cost+launchCost+(a.Id==-1&&uncertain.Contains(b.Id)?256:0);
                                if(!costs.TryGetValue(b.Id,out float old)||cost<old)
                                {costs[b.Id]=cost;came[b.Id]=edge;open.Enqueue(b,cost+goal.Distance(b.Rest(b.X))/ph.MaxRun);}
                            }
                            if(YieldWork())yield return Waiting();
                        }
                    }
                }
                bool complete=end!=null;
                end??=best;
                if(end==origin)break;
                var path=new List<SurfaceEdge>();
                for(var n=end;n.Id!=-1;){var edge=came[n.Id];path.Add(edge);n=edge.From;}
                path.Reverse();
                estimatedRouteUpdates=path.Sum(e=>e.Cost)+(complete?0:goal.Distance(end.Rest(end.X))/ph.MaxRun);
                candidateReachedGoal=complete;
                int connection=0;
                // Overlapping stance intervals are intentional. If the actual
                // body already occupies the next launch region, validate its
                // outgoing maneuver directly rather than walking away merely
                // to visit that node's representative coordinate.
                while(connection+1<path.Count&&path[connection].Landing.Contains(start)
                    &&(path[connection+1].Launch??path[connection+1].From.Region).Contains(start))connection++;
                var next=path[connection];
                // Use the FINAL region when this node overlaps it. Otherwise
                // preserve its whole valid stance interval for braking.
                waypoint=complete&&connection==path.Count-1?goal:next.Landing;
                if(connection+1<path.Count&&path[connection+1].Motion==SurfaceMotion.Jump)
                    waypoint=path[connection+1].Launch??next.Landing;
                if(waypoint.Contains(start)){denied.Add(next.To.Id);continue;}
                using var physical=new StateSpacePlanner.Search(start,Math.Clamp(start.Px,waypoint.MinPlayerLeftX,waypoint.MaxPlayerLeftX),
                    waypoint,ph,world,rejectEdge:reject,detourCheckpoint:detour,historicalPreference:preference,learning:learning,
                    requireInitialJump:next.Motion==SurfaceMotion.Jump);
                bool reconsider=false;
                while(true)
                {
                    // The native simulator stays on this thread. Its internal
                    // candidate iterator retains exact velocities/jump state.
                    var route=physical.Advance(maxExpansions:8,budgetMs:Math.Max(.1,milliseconds-timer.Elapsed.TotalMilliseconds));
                    if(route.Actions.Count>0)
                    {
                        // A safe prefix does not verify the selected connection.
                        // First give other graph branches a chance (e.g. retreat
                        // out from a low roof), instead of repeatedly approaching
                        // the same impossible jump. This is a soft search cost,
                        // never a physical impossibility or execution-failure ban.
                        if(!route.ReachedGoal&&physical.TotalExpansions>=32&&!uncertain.Contains(next.To.Id))
                        {uncertain.Add(next.To.Id);reconsider=true;break;}
                        if(!route.ReachedGoal&&physical.TotalExpansions<32&&!physical.Finished)
                        {yield return Waiting();continue;}
                        if(route.ReachedGoal)verified++;
                        exit=complete&&connection==path.Count-1&&route.ReachedGoal?"complete":"safe_partial";
                        // A graph handover executes the already validated braking
                        // tail. Do not leave a moving body waiting for graph work.
                        var last=route.Actions[^1];
                        route.Actions[^1]=last with{ExecuteFrames=last.Frames.Count,Landing=last.Frames[^1].State};
                        if(route.ReachedGoal)graph.RecordVerified(next,start,route,ph,world);
                        Finished=true;
                        yield return route with{Kind="surface_graph",ReachedGoal=exit=="complete",SearchExit=exit};
                        yield break;
                    }
                    if(physical.Finished){localBudget|=route.SearchExit=="local_state_budget";break;}
                    yield return Waiting();
                }
                if(!reconsider)denied.Add(next.To.Id);
                if(YieldWork())yield return Waiting();
            }
            if(graph.Truncated||radius==SurfaceGraph.MaximumRadius)break;
            rangeExpansions++;
        }
        bool unknown=false;
        for(int x=cx-radius;x<=cx+radius&&!unknown;x++)
            for(int y=cy-topRows;y<=cy+bottomRows;y++)
            {if(graph.Shape(x,y)=='?')unknown=true;if(YieldWork())yield return Waiting();}
        bool goalCovered=goal.MinPlayerLeftX>=(cx-radius)*16&&goal.MaxPlayerLeftX<=(cx+radius+1)*16
            &&goal.SurfaceY>=(cy-topRows)*16&&goal.SurfaceY<=(cy+bottomRows)*16;
        exit=graph.Truncated?"surface_node_budget":localBudget?"surface_local_budget":unknown?"unknown_terrain"
            :!goalCovered?"local_coverage_exhausted":"no_route_in_coverage";Finished=true;
        yield return new(new(),0,timer.Elapsed.TotalMilliseconds,FailureReason:exit,TimeBudgetReached:localBudget||graph.Truncated,Kind:"surface_graph",SearchExit:exit);
    }

    // Locate usable take-off space inside a stance interval. A low roof can
    // make its right-hand part walkable but unable to launch. These probes only
    // guide graph costs/waypoints; the complete maneuver is still planned from
    // the actual PhysicsState before execution.
    private static LandingRegion JumpLaunch(SurfaceNode node,PhysicsState actual,ISurfaceTerrain source,PhysicsParameters ph)
    {
        if(ph.Air!=null)ph=ph with{Air=null};
        float? first=null;float last=0,bestLeft=0,bestRight=-1;
        for(float x=node.Region.MinPlayerLeftX;x<=node.Region.MaxPlayerLeftX;x+=2)
        {
            var pose=node.Id==-1?actual:node.Rest(x);
            var w=source.MotionAt(pose);var next=pose;bool clear=w.SafeLanding(pose);
            try
            {
                for(int frame=0;frame<3&&clear;frame++)
                {
                    // A hypothetical surface has no observed flight resources.
                    // These three ordinary launch frames only test headroom;
                    // the later actual-state search proves powered movement.
                    var previous=next;next=PhysicsSimulator.Step(next,new(0,true),ph,w);
                    clear=w.SafeSweep(previous,next,out _);
                }
                clear&=pose.Py-next.Py>=ph.JumpSpeed*2;
            }
            catch(UnsafeMotionException){clear=false;}
            if(clear){first??=x;last=x;}
            if(!clear||x+2>node.Region.MaxPlayerLeftX)
            {
                if(first.HasValue&&last-first.Value>bestRight-bestLeft){bestLeft=first.Value;bestRight=last;}
                first=null;
            }
        }
        return bestRight>=bestLeft?new(bestLeft,bestRight,node.Region.SurfaceY):null;
    }
}
