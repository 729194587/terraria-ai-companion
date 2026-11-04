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
    public static void Run()
    {
        foreach(int direction in new[]{1,-1})
        {
            var task=new NavigationTask();var w=new MotionOfflineTests.World();var s=new PhysicsState(1600,1558,0,0,true);
            float target=s.Px+direction*1536;int frames=0;
            while(!task.Terminal&&frames++<=NavigationTripProgress.UpdateLimit)
            {var input=task.Before(801,3,target,s,Ph,w);s=PhysicsSimulator.Step(s,input,Ph,w);task.After(s,w,input==default);}
            Check(task.Snapshot.Status=="arrived"&&direction*(target-s.Px)<=12&&Math.Abs(s.Vx)<=.1f,"one 1536px goal arrives safely");
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
        Console.WriteLine($"M4.3 trip: {checks} checks; collision fixtures, not native execution.");
    }
}
