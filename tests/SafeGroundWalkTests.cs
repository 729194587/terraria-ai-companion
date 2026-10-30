using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using CompanionBridge.Motion;

public static class SafeGroundWalkTests
{
    private sealed class World:IMotionWorld
    {
        internal readonly MotionOfflineTests.World Collision=new();
        internal bool FlatAllowed=true;
        internal int Moves;
        internal float? UnknownX,HazardX;
        internal Func<PhysicsState,PhysicsState> Correction;
        internal char Cell(int x,int y)
        {
            if(UnknownX.HasValue&&x==(int)(UnknownX.Value/16))return '?';
            if(HazardX.HasValue&&x==(int)(HazardX.Value/16))return '!';
            float px=x*16;
            if(px+16>Collision.WallX&&px<Collision.WallX+Collision.WallWidth&&y*16>=1600-Collision.WallHeight)return '#';
            return y>=100&&!(px+16>Collision.GapStart&&px<Collision.GapEnd)?'#':'.';
        }
        public PhysicsState Move(PhysicsState s,float vx,float vy,float gravity)
        {Moves++;var next=Collision.Move(s,vx,vy,gravity);return Correction?.Invoke(next)??next;}
        public bool SafeSweep(PhysicsState a,PhysicsState b,out string reason)
        {
            if(!Collision.SafeSweep(a,b,out reason))return false;
            for(int x=(int)Math.Floor(Math.Min(a.Px,b.Px)/16);x<=(int)Math.Floor((Math.Max(a.Px,b.Px)+20)/16);x++)
                if((UnknownX.HasValue&&x==(int)(UnknownX.Value/16))||(HazardX.HasValue&&x==(int)(HazardX.Value/16)))
                {reason="unknown/hazard";return false;}
            return true;
        }
        public bool SafeLanding(PhysicsState s)=>Collision.SafeLanding(s)&&MotionSupport.SafeLanding(s,Cell);
        public bool SafeFlatLanding(PhysicsState s)=>FlatAllowed&&SafeLanding(s)&&MotionSupport.SafeFlatLanding(s,Cell);
    }
    private sealed record Measurement(bool Optimized,string Scenario,string Status,float StartX,float EndX,int Frames,
        int Plans,int Searches,int FastWalks,int Moves,double ElapsedMs,double PlanningMs);
    private static Measurement Measure(bool enabled,string scenario,int direction,int segments,int distance,bool flatProof=true)
    {
        var world=new World{FlatAllowed=flatProof};var state=new PhysicsState(0,1558,0,0,true);
        int frames=0,plans=0,searches=0,fast=0;double planningMs=0;
        var watch=Stopwatch.StartNew();string status="idle";
        for(int segment=0;segment<segments;segment++)
        {
            var task=new NavigationTask(enabled);float target=state.Px+direction*distance;int previous=0;
            for(int f=0;f<1200&&!task.Terminal;f++)
            {
                var input=task.Before(segment+1,1,target,state,PhysicsParameters.Basic,world);
                if(task.Snapshot.PlanNumber!=previous){planningMs+=task.Snapshot.PlanMilliseconds;previous=task.Snapshot.PlanNumber;}
                if(task.Terminal)break;
                state=PhysicsSimulator.Step(state,input,PhysicsParameters.Basic,world);frames++;task.After(state,world);
            }
            status=task.Snapshot.Status;plans+=task.Snapshot.PlanNumber;searches+=task.Snapshot.SearchNumber;fast+=task.Snapshot.FastWalkPlans;
            if(status!="arrived")break;
        }
        watch.Stop();return new(enabled,scenario,status,0,state.Px,frames,plans,searches,fast,world.Moves,watch.Elapsed.TotalMilliseconds,planningMs);
    }
    public static void Run()
    {
        int checks=0;void Check(bool b,string why){if(!b)throw new Exception(why);checks++;}
        var start=new PhysicsState(0,1558,0,0,true);var ph=PhysicsParameters.Basic;
        var flat=new World();var route=SafeGroundWalk.TryPlan(start,512,1,ph,flat);
        Check(route?.Kind=="safe_ground_walk"&&route.Expansions==0&&route.Actions.Count==1&&!route.Actions[0].Jump,"one safe edge without A* expansions");
        Check(route.Actions[0].Frames.Count>route.Actions[0].ExecuteFrames,"complete braking tail retained beyond moving prefix");
        foreach(string kind in new[]{"wall","edge","slope","unknown","hazard"})
        {
            var world=new World();
            if(kind=="wall")world.Collision.WallX=32;
            if(kind=="edge"){world.Collision.GapStart=32;world.Collision.GapEnd=128;}
            if(kind=="slope"){world.Correction=s=>s with{Py=s.Py-0.5f};}
            if(kind=="unknown")world.UnknownX=32;
            if(kind=="hazard")world.HazardX=32;
            var candidate=SafeGroundWalk.TryPlan(start,512,1,ph,world);
            Check(candidate==null,"uncertain short walk falls back: "+kind);
            var task=new NavigationTask(true);task.Before(30,1,512,start,ph,world);
            Check(task.Snapshot.FastWalkPlans==0&&task.Snapshot.SearchNumber==1,"original planner invoked: "+kind);
        }
        Check(SafeGroundWalk.TryPlan(start,512,1,ph,new World{FlatAllowed=false})==null,"world without flat-proof capability opts out");
        Check(SafeGroundWalk.TryPlan(start,512,1,ph with{Gravity=.5f},new World())==null,"unsupported motion profile opts out");
        Check(SafeGroundWalk.TryPlan(start with{Grounded=false},512,1,ph,new World())==null,"airborne opts out");
        var region=new NavigationTask(true);region.Before(35,1,new LandingRegion(100,150,1600),start,ph,new World());
        Check(region.Snapshot.SearchNumber==1&&region.Snapshot.FastWalkPlans==0,"2D region path unchanged");
        var changed=new World();var running=new NavigationTask(true);var input0=running.Before(40,1,512,start,ph,changed);
        var actual=PhysicsSimulator.Step(start,input0,ph,changed);running.After(actual,changed);changed.Collision.Hazard=true;
        Check(running.Before(40,1,512,actual,ph,changed)==default&&running.Terminal,"live hazard detected by existing executor before next input");
        var divergence=new NavigationTask(true);divergence.Before(41,1,512,start,ph,new World());
        divergence.After(start with{Px=40},new World());Check(divergence.Terminal,"position divergence still fails");
        divergence.Cancel();Check(divergence.Snapshot.Status=="idle"&&divergence.Snapshot.SearchNumber==0&&divergence.Snapshot.PlanKind==null,"cancel clears fast-path diagnostics");
        divergence.Before(42,2,512,start,ph,new World());Check(divergence.Snapshot.Generation==2&&divergence.Snapshot.FastWalkPlans==1,"new generation starts fresh");
        // A valid fast prediction does not turn an unconfirmed real checkpoint into success.
        var unsettled=new NavigationTask(true);var uw=new World();var us=start;
        unsettled.Before(47,1,512,us,ph,uw);
        var planned=(Route)typeof(NavigationTask).GetField("_route",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(unsettled);
        int endpoint=planned.Actions[0].ExecuteFrames;
        for(int f=0;f<endpoint;f++)
        {
            var keys=unsettled.Before(47,1,512,us,ph,uw);us=PhysicsSimulator.Step(us,keys,ph,uw);
            if(f==endpoint-1)us=us with{Grounded=false,Vy=.331f};
            unsettled.After(us,uw);
        }
        Check(unsettled.Snapshot.Status=="confirming_landing"&&unsettled.Snapshot.CompletedActions==0,"fast walk still requires actual supported checkpoint");
        for(int f=1;f<NavigationTask.LandingConfirmationLimit;f++)
        {Check(unsettled.Before(47,1,512,us,ph,uw)==default,"confirmation releases intent");unsettled.After(us,uw);}
        Check(unsettled.Snapshot.Status=="unknown"&&unsettled.Snapshot.Reason=="landing_confirmation_deadline"
            &&unsettled.Snapshot.FastWalkPlans==1&&unsettled.Snapshot.SearchNumber==0,"unconfirmed fast checkpoint stays unknown without replan/replay");
        // Warm both production paths, then publish paired workloads. No wall-clock speed threshold.
        Measure(false,"warm",1,1,128);Measure(true,"warm",1,1,128);
        var rows=new List<Measurement>();
        foreach(int direction in new[]{1,-1})foreach(var scenario in new[]{(Name:"continuous512",Segments:1,Distance:512),(Name:"five128",Segments:5,Distance:128)})
        {
            for(int repetition=0;repetition<5;repetition++)
            {
                var before=Measure(false,scenario.Name,direction,scenario.Segments,scenario.Distance);
                var after=Measure(true,scenario.Name,direction,scenario.Segments,scenario.Distance);
                Check(before.Status=="arrived"&&after.Status=="arrived","both arrive: "+scenario.Name);
                Check(after.Searches<before.Searches&&after.Moves<before.Moves&&after.FastWalks>0,"less search and simulation work: "+scenario.Name);
                Check(Math.Abs(before.EndX-after.EndX)<0.01f&&before.Frames==after.Frames,"same flat-ground endpoint and execution frames");
                Check(after.Frames<1200,"bounded execution");rows.Add(before);rows.Add(after);
            }
        }
        var fallbackBefore=Measure(false,"flat_without_proof",1,1,128,false);
        var fallbackAfter=Measure(true,"flat_without_proof",1,1,128,false);
        Check(fallbackBefore.Status=="arrived"&&fallbackAfter.Status=="arrived"&&fallbackAfter.FastWalks==0
            &&fallbackBefore.Searches==fallbackAfter.Searches,"no flat proof: original search, no claimed improvement");
        rows.Add(fallbackBefore);rows.Add(fallbackAfter);
        Console.WriteLine($"Safe ground walk: {checks} checks passed; offline collision world, no graphical/native acceptance.");
        Console.WriteLine(JsonSerializer.Serialize(rows,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase}));
    }
}
