using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CompanionBridge.Motion;

// Compiled only by -NavigationExperimentOutput. Each trial owns fresh terrain,
// search memory and counters. Native trials consume the saved input schedules.
public static class NavigationExperiment
{
    internal static bool AllStances;
    internal static int? HorizontalRange;
    internal static int HoldStep = 2, Expanded;
    private static int predictions, extra, powered, safeExtra, safePowered;
    private static readonly Dictionary<string,int> rejections = new();
    internal static void Observe(Prediction prediction, int second, int thrust)
    {
        predictions++;
        if(second >= 0){extra++; if(prediction.Safe)safeExtra++;}
        if(thrust >= 0){powered++; if(prediction.Safe)safePowered++;}
        if(!prediction.Safe)rejections[prediction.Reason] = rejections.GetValueOrDefault(prediction.Reason)+1;
    }

    private sealed record Config(string Group, bool AllStances=false, int? Range=null, int HoldStep=2,
        bool RollY=false, int LocalBudget=32, int GraphSlices=96);
    private sealed record Fixture(string Name, string Geometry, LandingRegion Goal, string Capability="basic",
        float X=1598, float Y=1558, bool Grounded=true, float VY=0);
    private sealed record InputRun(int Direction, bool Jump, bool Down, int Frames);
    private sealed record Segment(int Hold, int ExtraFrame, int ThrustFrame, int Executed, int Validated,
        List<InputRun> Inputs);
    private sealed class Row
    {
        public string Scene {get;init;}
        public string Planner {get;init;}
        public Config Config {get;init;}
        public string RouteKind {get;set;}
        public string Exit {get;set;}
        public string FailureClass {get;set;}
        public bool FoundRoute {get;set;}
        public bool ModelArrived {get;set;}
        public bool? ActuallyArrived => null;
        public bool? FalseNegative {get;set;}
        public string Witness {get;set;}
        public int CandidateCount => GraphCandidates + MotionCandidates;
        public int GraphCandidates {get;set;}
        public int MotionCandidates {get;set;}
        public int ExpandedStates {get;set;}
        public double SearchTime {get;set;}
        public int ModelRejectionCount => Rejections.Values.Sum();
        public Dictionary<string,int> Rejections {get;set;}
        public bool SearchBudgetExit {get;set;}
        public double? ExecutionDeviation => null;
        public bool? RecoverySuccess => null;
        public List<Segment> Route {get;} = new();
        [System.Text.Json.Serialization.JsonIgnore]
        public List<float[]> Positions {get;} = new();
        public List<string> Handoffs {get;} = new();
        public string Capability {get;set;}
        public int ExtraCandidates {get;set;}
        public int SafeExtraCandidates {get;set;}
        public int WingCandidates {get;set;}
        public int SafeWingCandidates {get;set;}
        public bool? HookTemplate {get;set;}
        public PhysicsState End {get;set;}
    }

    private static char Cell(string geometry,int x,int y)
    {
        int q=x-100;
        return geometry switch {
            "flat" => y>=100?'#':'.',
            "solid_up_three" => y>=100-(q>=4?3:0)?'#':'.',
            "extra_up" => y>=100-(q>=6?8:0)?'#':'.',
            "experiment_platform" => y>=108?'#':(q<=3&&y==100||q>=6&&y==87)?'=':'.',
            "hook_shaft" => y>=100||q is >=2 and <=12&&y==90||q==0&&y==81?'#':'.',
            "unknown" => q>=2?'?':y>=100?'#':'.',
            _ => throw new ArgumentException(geometry)
        };
    }

    // Tile shapes/collisions come from the existing graph double. Mirror the
    // bounded 32px native pre-move probe, including unknown cells in its rim.
    // Rolling changes ONLY the Y anchor of this probe and the swept window.
    private sealed class Window : IMotionWorld, ISurfaceTerrain
    {
        private readonly SurfaceGraphTests.Terrain tiles;
        private readonly PhysicsState anchor;
        private readonly bool roll;
        internal Window(SurfaceGraphTests.Terrain tiles, PhysicsState anchor, bool roll)
        {this.tiles=tiles;this.anchor=anchor;this.roll=roll;}
        public SurfaceCell ReadSurfaceCell(int x,int y)=>tiles.ReadSurfaceCell(x,y);
        public IMotionWorld MotionAt(PhysicsState s)=>new Window(tiles,s,roll);
        public bool SafeLanding(PhysicsState s)=>tiles.SafeLanding(s);
        public bool SafeFlatLanding(PhysicsState s)=>tiles.SafeFlatLanding(s);
        private bool Inside(PhysicsState a,PhysicsState b,float pad)
        {
            int left=(int)MathF.Floor((anchor.Px+10)/16)-22;
            int top=(int)MathF.Floor(((roll?a:anchor).Py+42)/16)-17;
            return Math.Min(a.Px,b.Px)-pad>=16*left && Math.Max(a.Px,b.Px)+20+pad<=16*(left+45)
                && Math.Min(a.Py,b.Py)-pad>=16*top
                && Math.Max(a.Py,b.Py)+42+pad<=16*(top+17+FallSafety.LookaheadPixels/16+5);
        }
        public PhysicsState Move(PhysicsState s,float vx,float vy,float gravity)
        {
            if(!Inside(s,s,32))throw new UnsafeMotionException("outside local prediction window");
            // All fixtures except the coverage control are fully observed.
            if(tiles.Unknown)
                for(int x=(int)MathF.Floor((s.Px-32)/16);x<Math.Ceiling((s.Px+52)/16);x++)
                    for(int y=(int)MathF.Floor((s.Py-32)/16);y<Math.Ceiling((s.Py+74)/16);y++)
                        if(tiles.Cell(x,y)=='?')throw new UnsafeMotionException("unknown terrain");
            return tiles.Move(s,vx,vy,gravity);
        }
        public bool SafeSweep(PhysicsState a,PhysicsState b,out string why)
        {
            if(!Inside(a,b,2)){why="outside local prediction window";return false;}
            return tiles.SafeSweep(a,b,out why);
        }
    }

    private static (PhysicsParameters,PhysicsState) Actor(Fixture f)
    {
        bool cloud=f.Capability=="cloud", wings=f.Capability=="angel";
        var observed=new MovementSnapshot(cloud?new[]{new ExtraJumpSnapshot(ExtraJumpModel.Cloud.FullName,0,true,true,false)}:Array.Empty<ExtraJumpSnapshot>(),
            false,new(wings?2:0,wings?100:0,wings?100:0,59,wings?AirMotionModel.AngelStats:null),
            new(0,0,0),new(false,-1,null,54),0,true,Array.Empty<GrappleSnapshot>(),new(false,0,false,false,false,false));
        var ph=PhysicsParameters.Basic with{ExtraJump=ExtraJumpModel.Select(observed),Air=AirMotionModel.Select(observed)};
        if(cloud!=(ph.ExtraJump!=null)||wings!=(ph.Air!=null))throw new Exception("profile selection");
        return (ph,new(f.X,f.Y,0,f.VY,f.Grounded,ExtraAvailable:cloud,Air:wings?AirMotionModel.Resources(observed):null));
    }

    private static PhysicsState Replay(Row row, Route route, PhysicsState actual,PhysicsParameters ph,IMotionWorld world)
    {
        foreach(var edge in route.Actions)
        {
            actual=MotionOfflineTests.ReplayChecked(actual,edge,ph,world);
            var runs=new List<InputRun>();
            foreach(var frame in edge.Frames.Take(edge.ExecuteFrames))
            {
                row.Positions.Add(new[]{frame.State.Px,frame.State.Py});
                var input=new InputRun(frame.Input.Direction,frame.Input.Jump,frame.Input.Down,1);
                if(runs.Count>0 && runs[^1] with{Frames=1}==input)runs[^1]=input with{Frames=runs[^1].Frames+1};
                else runs.Add(input);
            }
            row.Route.Add(new(edge.InitialHold,edge.ExtraJumpFrame,edge.AirStartFrame,edge.ExecuteFrames,edge.Frames.Count,runs));
        }
        return actual;
    }

    private static Row Trial(Fixture f,Config c,string planner)
    {
        AllStances=c.AllStances; HorizontalRange=c.Range; HoldStep=c.HoldStep;
        predictions=Expanded=extra=powered=safeExtra=safePowered=0; rejections.Clear();
        var (ph,state)=Actor(f);
        var tiles=new SurfaceGraphTests.Terrain{ReadOverride=(x,y)=>Cell(f.Geometry,x,y),Unknown=f.Geometry=="unknown"};
        var row=new Row{Scene=f.Name,Planner=planner,Config=c,Capability=f.Capability};
        var graph=new SurfaceGraph();
        var memory=new NavigationSearchMemory();
        var clock=new Stopwatch();
        var seen=new HashSet<PhysicsState>();
        for(int leg=0;leg<8;leg++)
        {
            var world=new Window(tiles,state,c.RollY);
            Route route;
            clock.Start();
            if(planner is "surface" or "production_surface")
            {
                memory.Scope(world.ReadSurfaceCell(100,100).Signature,ph,leg);
                using var search=new SurfaceNavigator(graph,state,f.Goal,ph,world,world,memory,null);
                int slices=0;
                do
                {
                    route=planner=="production_surface"?search.Advance():search.Advance(512,double.PositiveInfinity);
                    slices++;
                }
                while(!search.Finished && slices<c.GraphSlices);
                var snap=search.Snapshot;
                row.GraphCandidates+=snap.CandidateEdges; row.ExpandedStates+=snap.Expansions;
                row.Exit=search.Finished?route.SearchExit:"experiment_slice_budget";
            }
            else if(planner=="air_task_gate")
            {
                var task=new NavigationTask();
                task.Before(1,1,f.Goal,state,ph,world);
                if(!task.Terminal||task.Snapshot.Reason!="replan requires grounded player")throw new Exception("air entry changed");
                row.Exit="unsupported_air_start";clock.Stop();break;
            }
            else
            {
                route = planner=="production_local"
                    ? StateSpacePlanner.Plan(state,f.Goal,ph,world)
                    : StateSpacePlanner.Plan(state,f.Goal,ph,world,c.LocalBudget,double.PositiveInfinity);
                row.Exit=route.SearchExit;
            }
            clock.Stop();
            row.Handoffs.Add(row.Exit);
            row.SearchBudgetExit|=row.Exit.Contains("budget");
            state=Replay(row,route,state,ph,world);
            row.ModelArrived=f.Goal.Arrived(state,world);
            if(route.ReachedGoal!=row.ModelArrived)throw new Exception("replay/goal disagreement "+f.Name);
            if(row.ModelArrived||route.Actions.Count==0||planner is not ("surface" or "production_surface"))break;
            if(!seen.Add(state)){row.Exit="repeated_handoff";break;}
            if(leg==7){row.Exit="experiment_leg_budget";row.SearchBudgetExit=true;}
        }
        if(f.Geometry=="hook_shaft" && f.Capability=="hook")
        {
            var start=Actor(f).Item2;
            row.HookTemplate=GrappleClimbPlan.Find(start,f.Goal,ph,tiles,tiles)!=null;
        }
        row.End=state;row.FoundRoute=row.ModelArrived;
        row.RouteKind=row.ModelArrived?"complete":row.Route.Count>0?"partial":"none";
        row.ExpandedStates+=Expanded;row.MotionCandidates=predictions;
        row.ExtraCandidates=extra;row.WingCandidates=powered;
        row.SafeExtraCandidates=safeExtra;row.SafeWingCandidates=safePowered;
        row.SearchTime=clock.Elapsed.TotalMilliseconds;row.Rejections=new(rejections);
        row.FailureClass=row.ModelArrived?null:row.HookTemplate==true||row.Exit=="unsupported_air_start"?"unsupported_model":
            f.Name=="buried_goal"?"physically_unreachable_fixed_geometry":
            row.Exit=="unknown_terrain"||row.Exit=="local_coverage_exhausted"||rejections.ContainsKey("unknown terrain")?"insufficient_coverage":
            row.SearchBudgetExit?"search_budget":row.ModelRejectionCount>0?"prediction_rejected":"unresolved";
        return row;
    }

    // Fork one observed deviation. The control continues its active task. The
    // alternative substitutes one immediate search and hands its checkpoint to
    // the normal task if the immediate plan is partial.
    private static object AirRecovery(Fixture f)
    {
        AllStances=false;HorizontalRange=null;HoldStep=2;
        var (ph,state)=Actor(f);
        var tiles=new SurfaceGraphTests.Terrain{ReadOverride=(x,y)=>Cell(f.Geometry,x,y)};
        var world=new Window(tiles,state,false);
        var task=new NavigationTask(searchBudgetMilliseconds:double.PositiveInfinity);
        for(int frame=0;frame<400;frame++)
        {
            var input=task.Before(1,1,f.Goal,state,ph,world);
            state=PhysicsSimulator.Step(state,input,ph,world);
            if(!state.Grounded && state.Py<f.Y-30)
            {
                state=state with{Px=state.Px-24};
                task.After(state,world,input==default);
                if(task.Snapshot.RecoveryCount!=1)throw new Exception("missing air deviation");
                var disturbed=state;
                var control=Finish(task,state);
                var reference=StateSpacePlanner.Plan(disturbed,f.Goal,ph,world,32,double.PositiveInfinity);
                var route=new Row();
                var handoff=Replay(route,reference,disturbed,ph,world);
                var alternate=Finish(new NavigationTask(searchBudgetMilliseconds:double.PositiveInfinity),handoff);
                return new{Scene=f.Name,InjectionX=-24,AtFrame=frame+1,Disturbed=disturbed,Current=control,
                    AirReplan=new{reference.SearchExit,reference.Expansions,reference.ReachedGoal,route.Route,Handoff=handoff,
                        ExecutedBeforeHandoff=route.Route.Sum(a=>a.Executed),Continuation=alternate}};
            }
            task.After(state,world,input==default);
        }
        throw new Exception("fixture never became airborne");

        object Finish(NavigationTask controller,PhysicsState actual)
        {
            int updates=0;
            while(!controller.Terminal&&updates<900)
            {
                var keys=controller.Before(1,1,f.Goal,actual,ph,world);
                var next=PhysicsSimulator.Step(actual,keys,ph,world);
                if(!world.SafeSweep(actual,next,out _))throw new Exception("unsafe recovery replay");
                actual=next;controller.After(actual,world,keys==default);updates++;
            }
            return new{Updates=updates,controller.Snapshot.Status,controller.Snapshot.Reason,controller.Snapshot.RecoveryReplans,
                End=actual,ModelRecoverySuccess=controller.Snapshot.Status=="arrived"&&f.Goal.Arrived(actual,world)};
        }
    }

    public static void Run(string output)
    {
        var configs=new[]{new Config("A0"),new("A1",AllStances:true),new("A2-192",Range:192),
            new("A2-256",Range:256),new("A3",HoldStep:1),new("A4",RollY:true)};
        var fixtures=new Fixture[]{
            new("flat_trip","flat",new(1816,1840,1600)),
            new("ledge_48","solid_up_three",new(1696,1756,1552)),
            new("precision","flat",new(1700.10f,1700.26f,1600)),
            new("wing_window","experiment_platform",new(1696,1808,1392),"angel"),
            new("ordinary_high","extra_up",new(1712,1760,1472)),
            new("cloud_high","extra_up",new(1712,1760,1472),"cloud"),
            new("wing_high","extra_up",new(1712,1760,1472),"angel"),
            new("ordinary_shaft","hook_shaft",LandingRegion.AtDestination(1680,1440)),
            new("hook_shaft","hook_shaft",LandingRegion.AtDestination(1680,1440),"hook"),
            new("air_start","flat",new(1610,1650,1600),Y:1480,Grounded:false,VY:2),
            new("unknown","unknown",new(1700,1732,1600)),
            new("buried_goal","flat",new(1616,1648,1664))
        };
        var rows=new List<Row>();
        foreach(var f in fixtures)
        {
            Console.WriteLine("Navigation experiment: "+f.Name);
            if(f.Name=="air_start")rows.Add(Trial(f,configs[0],"air_task_gate"));
            else if(f.Name is "flat_trip" or "ledge_48" or "precision" or "wing_window")
                foreach(var c in configs.Where(c=>c.Range==null||f.Capability=="basic"))rows.Add(Trial(f,c,"surface"));
            foreach(var c in configs.Where(c=>c.Group is "A0" or "A3" or "A4"))rows.Add(Trial(f,c,"local"));
            rows.Add(Trial(f,new("budget_reference",LocalBudget:256),"local"));
            if(f.Grounded)rows.Add(Trial(f,configs[0],"production_local"));
        }
        foreach(var row in rows)
        {
            var witness=rows.FirstOrDefault(r=>r.Scene==row.Scene&&r.ModelArrived);
            row.FalseNegative=row.ModelArrived?false:witness==null?null:true;
            if(row.FalseNegative==true)
            {
                row.Witness=witness.Planner+"/"+witness.Config.Group;
                if(!row.SearchBudgetExit&&row.FailureClass is "unresolved" or "prediction_rejected"
                    &&rows.Any(r=>r.Scene==row.Scene&&r.Planner==row.Planner&&r.Config.Group=="A3"&&r.ModelArrived))
                    row.FailureClass="candidate_omission";
            }
        }
        if(!rows.Any(r=>r.SafeExtraCandidates>0&&r.Route.Any(a=>a.ExtraFrame>=0))
            ||!rows.Any(r=>r.SafeWingCandidates>0&&r.Route.Any(a=>a.ThrustFrame>=0))
            ||!rows.Any(r=>r.HookTemplate==true)||!rows.Any(r=>r.FalseNegative==true))
            throw new Exception("missing capability or replay witness");
        var defaults=fixtures.Where(f=>f.Name is "flat_trip" or "ledge_48" or "precision" or "wing_window")
            .Select(f=>Trial(f,configs[0],"production_surface")).ToArray();
        File.WriteAllText(Path.ChangeExtension(output,"controls.json"),JsonSerializer.Serialize(new{ProductionSurface=defaults,AirRecovery=AirRecovery(fixtures.Single(f=>f.Name=="ledge_48"))}));
        var native=rows.Where(r=>r.Route.Count>0 && fixtures.Any(f=>f.Name==r.Scene && f.Geometry is "flat" or "solid_up_three" or "extra_up" or "experiment_platform" && f.Grounded && f.Name!="buried_goal"));
        File.WriteAllText(Path.ChangeExtension(output,"positions.json"),JsonSerializer.Serialize(native.Select(r=>new{r.Scene,r.Planner,r.Config.Group,r.Positions})));
        File.WriteAllText(output,JsonSerializer.Serialize(new{Baseline="2d2be0b",Evidence="offline_collision_double",
            SearchTimeUnit="ms",Fixtures=fixtures,Rows=rows},new JsonSerializerOptions{WriteIndented=false}));
    }
}
