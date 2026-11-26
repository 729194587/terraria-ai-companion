using System;
using System.Linq;
using System.Text.Json;
using CompanionBridge.Motion;

public static class NavigationTripTests
{
    private static int checks;
    private static readonly PhysicsParameters Ph=PhysicsParameters.Basic;
    private static void Check(bool ok,string reason){if(!ok)throw new Exception(reason);checks++;}
    internal sealed class Alcove:IMotionWorld
    {
        // Low roof prevents a forward ascent; its left edge permits a run-up
        // and a jump onto it. Collision double, not native Terraria geometry.
        private readonly (float L,float R,float T,float B)[] blocks={(1600,1664,1520,1536),(1664,1696,1536,1600)};
        public PhysicsState Move(PhysicsState s,float vx,float vy,float gravity)
        {
            float x=s.Px+vx,y=s.Py+vy;
            foreach(var b in blocks)
                if(s.Py+42>b.T&&s.Py<b.B&&x+20>b.L&&x<b.R)
                {x=vx>=0?b.L-20:b.R;vx=0;}
            bool ground=false;
            if(vy>=0&&y+42>=1600){y=1558;vy=0;ground=true;}
            foreach(var b in blocks)
                if(x+20>b.L&&x<b.R)
                {
                    if(vy>=0&&s.Py+42<=b.T+.01f&&y+42>=b.T){y=b.T-42;vy=0;ground=true;}
                    else if(vy<0&&s.Py>=b.B-.01f&&y<=b.B){y=b.B;vy=0;}
                }
            return new(x,y,vx,vy,ground);
        }
        public bool SafeSweep(PhysicsState a,PhysicsState b,out string reason){reason=null;return true;}
        public bool SafeLanding(PhysicsState s)=>s.Grounded&&!blocks.Any(b=>s.Px+20>b.L&&s.Px<b.R&&s.Py+42>b.T+.01f&&s.Py<b.B);
    }
    private sealed class ChangingWorld : IMotionWorld
    {
        internal readonly MotionOfflineTests.RegionWorld Inner=new();
        internal long Stamp;
        internal int Moves;
        public long TerrainSignature(PhysicsState state,Prediction edge=null)=>Stamp;
        public PhysicsState Move(PhysicsState state,float vx,float vy,float gravity)
        {Moves++;return Inner.Move(state,vx,vy,gravity);}
        public bool SafeSweep(PhysicsState start,PhysicsState end,out string reason)=>Inner.SafeSweep(start,end,out reason);
        public bool SafeLanding(PhysicsState state)=>Inner.SafeLanding(state);
    }
    public static void WorldJourney()
    {
        var start = new PhysicsState(1600,1558,0,0,true);
        float farX = start.Px + 6320*16;
        var region = new LandingRegion(farX,farX+20,1600);
        var flat = new MotionOfflineTests.RegionWorld();
        flat.Collision.WallX = float.PositiveInfinity;
        VerifyFlatJourney(start,region,flat,60000);
    }

    private static void VerifyFlatJourney(PhysicsState start, LandingRegion region,
        MotionOfflineTests.RegionWorld flat, int updateLimit)
    {
        var nav=new NavigationTask(enableSafeGroundWalk:true){Continuous=true};
        var actual=start;
        for(int n=0;n<updateLimit&&!nav.Terminal;n++)
        {
            var input=nav.Before(806,3,region,actual,Ph,flat);
            actual=PhysicsSimulator.Step(actual,input,Ph,flat);
            nav.After(actual,flat,input==default);
        }
        Check(nav.Snapshot.Status=="arrived"&&region.Arrived(actual,flat)
            &&nav.Snapshot.TripUpdates>NavigationTripProgress.UpdateLimit&&nav.Snapshot.FastWalkPlans>1,
            "continuous region trip uses verified rolling prefixes beyond legacy update limit");
        Console.WriteLine($"Flat journey reached {(actual.Px-start.Px)/16:F1} tiles in {nav.Snapshot.TripUpdates} updates");
    }
    public static void Run()
    {
        foreach(int direction in new[]{1,-1})
        {
            var task=new NavigationTask(searchBudgetMilliseconds:1000);var w=new MotionOfflineTests.World();var s=new PhysicsState(1600,1558,0,0,true);
            float target=s.Px+direction*1536;int frames=0;
            while(!task.Terminal&&frames++<=NavigationTripProgress.UpdateLimit)
            {var input=task.Before(801,3,target,s,Ph,w);s=PhysicsSimulator.Step(s,input,Ph,w);task.After(s,w,input==default);}
            Check(task.Snapshot.Status=="arrived"&&direction*(target-s.Px)<=12&&Math.Abs(s.Vx)<=.1f,$"one 1536px goal arrives safely: {task.Snapshot.Status} {task.Snapshot.Reason} x={s.Px} vx={s.Vx} searches={task.Snapshot.SearchNumber}");
            Check(task.Snapshot.Session==801&&task.Snapshot.Generation==3&&task.Snapshot.ContinuedEdges>0,"same identity follows revalidated prefixes");
            Check(task.Snapshot.PlanNumber<task.Snapshot.CompletedActions&&task.Snapshot.TripUpdates<=1800,"prefix retention reduces fresh planning");
            Console.WriteLine(JsonSerializer.Serialize(new{scenario="long_flat",direction,frames,x=s.Px,target,task.Snapshot.Status,
                task.Snapshot.PlanNumber,task.Snapshot.CompletedActions,task.Snapshot.ContinuedEdges,task.Snapshot.TripPlanningMilliseconds}));
        }
        var progress=new NavigationTripProgress();var start=new PhysicsState(1600,1558,0,0,true);
        progress.Begin(start,200);
        for(int i=1;i<=5;i++)Check(progress.Checkpoint(start with{Px=1600-i*8},200+i*8),"necessary novel retreat is bounded progress");
        Check(progress.DetourCheckpoints==5,"five retreats do not fail at the old four-checkpoint horizontal guard");
        Check(!progress.Checkpoint(start,200),"return to familiar state is not new progress");
        Check(progress.Reject(start),"repeated checkpoint is quarantined");
        Check(!progress.Reject(start with{Vx=2.5f}),"different checkpoint momentum is not the same repeated state");
        Check(!progress.Detour(start with{Px=1400}),"detour does not enlarge search span");
        var journey=new NavigationTripProgress();
        journey.Begin(start,200);
        for(int n=1;n<=12;n++)
            Check(journey.Checkpoint(start with{Px=start.Px-n*16},200+n*16,continuous:true),
                "verified new terrain is journey progress even while temporarily retreating");
        Check(journey.Detour(start with{Px=start.Px-13*16}),
            "continuous progress rolls the local detour anchor instead of spending a cumulative detour count");
        var alcove=new Alcove();var nav=new NavigationTask();var actual=new PhysicsState(1620,1558,0,0,true);
        float minX=actual.Px;
        for(int n=0;n<=1800&&!nav.Terminal;n++)
        {var input=nav.Before(802,3,1820,actual,Ph,alcove);actual=PhysicsSimulator.Step(actual,input,Ph,alcove);nav.After(actual,alcove,input==default);minX=Math.Min(minX,actual.Px);}
        Console.WriteLine(JsonSerializer.Serialize(new{scenario="alcove",nav.Snapshot.Status,nav.Snapshot.Reason,minX,x=actual.Px,
            nav.Snapshot.DetourCheckpoints,nav.Snapshot.PlanNumber}));
        Check(nav.Snapshot.Status=="arrived"&&minX<1580,"necessary retreat clears the low roof and reaches the original goal");
        // New obstacle at the boundary of a retained multi-edge route: stale
        // suffix must be rejected before its inputs, without replaying old frames.
        var world=new MotionOfflineTests.World();nav=new();actual=start;bool changed=false;
        for(int n=0;n<500&&!nav.Terminal;n++)
        {
            if(!changed&&nav.Snapshot.CompletedActions==1)
            {world.WallX=actual.Px+24;world.WallWidth=32;world.WallHeight=200;changed=true;}
            var input=nav.Before(803,3,2200,actual,Ph,world);
            actual=PhysicsSimulator.Step(actual,input,Ph,world);nav.After(actual,world,input==default);
        }
        Check(changed&&nav.Snapshot.InvalidatedRoutes>0,"current world invalidates retained suffix");
        nav.Cancel();var cancelled=nav.Snapshot;
        nav.Before(803,3,2200,actual,Ph,world);nav.After(actual,world,true);
        Check(nav.Snapshot==cancelled,"cancelled trip cannot follow a retained suffix");
        var field=typeof(NavigationTask).GetField("_updates",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
        nav=new();nav.Begin(804,3,5000,start);field.SetValue(nav,1800);
        Check(nav.Before(804,3,5000,start,Ph,new MotionOfflineTests.World())==default&&nav.Snapshot.Reason=="navigation_trip_update_budget",
            "whole-trip updates do not reset at prefix handoffs");
        foreach(string counter in new[]{"_searches","_planningMilliseconds"})
        {
            nav=new();nav.Begin(805,3,5000,start);
            var budget=typeof(NavigationTask).GetField(counter,System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
            budget.SetValue(nav,counter=="_searches"?(object)NavigationTripProgress.SearchLimit:NavigationTripProgress.PlanningLimitMilliseconds);
            Check(nav.Before(805,3,5000,start,Ph,new MotionOfflineTests.World())==default
                &&nav.Snapshot.Reason=="navigation_trip_search_budget","cumulative search budget cannot issue another edge");
        }
        var hazard=new MotionOfflineTests.World{Hazard=true};int priorReads=0;
        var refused=StateSpacePlanner.Plan(start,1800,Ph,hazard,budgetMs:5000,detourCheckpoint:_=>true,
            historicalPreference:_=>{priorReads++;return int.MaxValue;});
        Check(refused.Actions.Count==0&&priorReads==0,"historical hints cannot approve unsafe or unobserved terrain");
        var region=new LandingRegion(7500,7520,1600);
        var flat=new MotionOfflineTests.RegionWorld();
        flat.Collision.WallX=float.PositiveInfinity;
        VerifyFlatJourney(start,region,flat,2500);
        var narrow=new LandingRegion(1614,1618,1600);
        var approach=SafeGroundWalk.TryPlan(start,1616,1,Ph,flat,narrow);
        Check(approach is {ReachedGoal:true}&&narrow.Arrived(approach.Actions[0].Landing,flat),
            "short flat approach selects a stride that brakes inside the narrow goal");
        nav=new(searchBudgetMilliseconds:0){Continuous=true};
        for(int n=1;n<=5;n++)
        {
            Check(nav.Before(807,3,region,start,Ph,flat)==default,"empty budget slice emits no input");
            Check(!nav.Terminal,"empty search slices stay inside the root journey");
        }
        Check(nav.Snapshot.DeferredSearches==5&&nav.Snapshot.Reason=="local search deferred"
            &&nav.Snapshot.NoProgressUpdates==0,"search waiting does not count as motion stall");
        nav.Cancel();
        var cancelledSearch=nav.Snapshot;
        nav.Before(807,3,region,start,Ph,flat);
        Check(nav.Snapshot==cancelledSearch,"cancellation retires the suspended search without resuming its frontier");
        nav=new(enableSafeGroundWalk:true,searchBudgetMilliseconds:0){Continuous=true};
        flat.Collision.WallX=start.Px+20;
        Check(nav.Before(808,3,region,start,Ph,flat)==default&&!nav.Terminal,"budget miss keeps trip running");
        flat.Collision.WallX=float.PositiveInfinity;
        Check(nav.Before(808,3,region,start,Ph,flat).Direction==1&&nav.Snapshot.DeferredSearches==1,
            "same trip continues when new observation yields a safe prefix");
        var recoveryGoal=new LandingRegion(1840,1860,1600);
        nav=new(enableSafeGroundWalk:true){Continuous=true};
        actual=start;
        bool displaced=false;
        for(int n=0;n<900&&!nav.Terminal;n++)
        {
            var input=nav.Before(809,3,recoveryGoal,actual,Ph,flat);
            actual=PhysicsSimulator.Step(actual,input,Ph,flat);
            if(!displaced&&input.Direction!=0)
            {
                actual=actual with{Px=actual.Px-24};
                displaced=true;
            }
            nav.After(actual,flat,input==default);
        }
        Check(displaced&&nav.Snapshot.RecoveryReplans==1&&nav.Snapshot.Status=="arrived"
            &&recoveryGoal.Arrived(actual,flat),"continuous goal recovers from actual displacement and arrives");
        var changing=new ChangingWorld();
        var upper=new LandingRegion(1664,1744,1552);
        using(var search=new StateSpacePlanner.Search(start,1664,upper,Ph,changing))
        {
            var firstSlice=search.Advance(maxExpansions:1,budgetMs:1000);
            Check(!search.Finished&&firstSlice.SearchExit=="expansion_budget"&&search.TotalExpansions==1,
                "local expansion budget yields a resumable frontier");
            search.Advance(maxExpansions:1,budgetMs:1000);
            Check(search.TotalExpansions>=2,"continuation expands the next physical state");
        }
        var learning=new NavigationSearchMemory();
        changing.Inner.Collision.WallX=start.Px+20;
        learning.Scope(changing.Stamp,Ph,0);
        var rejected=learning.Predict(start,1,0,24,Ph,changing);
        int moves=changing.Moves;
        learning.Predict(start,1,0,24,Ph,changing);
        Check(!rejected.Safe&&changing.Moves==moves&&learning.CacheHits==1,
            "exact rejected search candidate is learned without executing it");
        learning.Predict(start with{Vx=.2f},1,0,24,Ph,changing);
        learning.Predict(start with{ReleaseJump=false},1,0,24,Ph,changing);
        Check(learning.Count==3,"velocity and jump state remain distinct search keys");
        changing.Inner.Collision.WallX=float.PositiveInfinity;changing.Stamp++;
        learning.Scope(changing.Stamp,Ph,1);
        var viable=learning.Predict(start,1,0,24,Ph,changing);
        Check(viable.Safe&&learning.Count==0,"terrain change invalidates learned rejection");
        var memory=new NavigationFailureMemory();
        memory.Remember(start,viable,0,changing,Ph,"fixture_collision",start with{Px=1580});
        Check(memory.Reject(start,viable,1,changing,Ph)&&memory.Records[0].Reason=="fixture_collision"
            &&memory.Records[0].ActualLanding.Px==1580,"execution memory keeps movement and observed failure evidence");
        Check(!memory.Reject(start with{Vx=.2f},viable,1,changing,Ph),"failed edge cannot collapse distinct momentum");
        changing.Stamp++;
        Check(!memory.Reject(start,viable,2,changing,Ph)&&memory.Count==0,
            "changed terrain permits reevaluating an actually failed edge");
        memory.Remember(start,viable,2,changing,Ph,"fixture",start);
        Check(!memory.Reject(start,viable,3,changing,PhysicsParameters.Swiftness)&&memory.Count==0,
            "changed motion capability invalidates the old failed edge");
        var knowledge=new NavigationKnowledge();
        var actor=new object();
        knowledge.Bind(actor,"world",3,254);
        knowledge.Failures.Remember(start,viable,0);
        knowledge.Bind(actor,"other-world",3,254);
        Check(knowledge.Failures.Count==0,"world session change clears root journey memory");
        knowledge.Failures.Remember(start,viable,0);
        knowledge.Bind(actor,"other-world",4,254);
        Check(knowledge.Failures.Count==0,"actor generation change clears root journey memory");
        for(int n=0;n<NavigationFailureMemory.Capacity+8;n++)memory.Remember(start,viable,n);
        Check(memory.Count==NavigationFailureMemory.Capacity,"execution memory has bounded capacity");
        var watchdog=new NavigationProgressWatchdog();
        for(int n=0;n<500;n++)watchdog.Observe(NavigationActivity.Search);
        Check(!watchdog.Stalled&&watchdog.SearchUpdates==500,"normal search waiting cannot trip motion watchdog");
        for(int n=0;n<NavigationProgressWatchdog.ActiveUpdateLimit;n++)watchdog.Observe(NavigationActivity.Movement);
        Check(watchdog.Stalled,"active movement without verified progress is bounded");
        watchdog.Observe(NavigationActivity.VerifiedEffect);
        Check(!watchdog.Stalled&&watchdog.VerifiedEffects==1,"verified terrain work is meaningful progress");
        Console.WriteLine($"M4.3 trip: {checks} checks; collision fixtures, not native execution.");
    }
}
