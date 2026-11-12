using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CompanionBridge.Motion;

// Axis-aligned tile collision double; native Collision/Player.Update are verified
// separately by native_vertical_navigation.py. Safety uses production support.
public static class VerticalNavigationTests
{
    private sealed class World : IMotionWorld
    {
        internal Func<int,int,char> Tile;
        public PhysicsState Move(PhysicsState s,float vx,float vy,float gravity)
        {
            float x=s.Px,y=s.Py;
            bool Solid(int tx,int ty)=>Tile(tx,ty)=='#';
            for(int tx=(int)MathF.Floor(Math.Min(x,x+vx)/16);tx<(int)MathF.Ceiling((Math.Max(x,x+vx)+20)/16);tx++)
                for(int ty=(int)MathF.Floor(y/16);ty<(int)MathF.Ceiling((y+42)/16);ty++)
                    if(Solid(tx,ty))
                    {
                        if(vx>0&&x+20<=tx*16)vx=Math.Min(vx,tx*16-x-20);
                        if(vx<0&&x>=(tx+1)*16)vx=Math.Max(vx,(tx+1)*16-x);
                    }
            x+=vx;
            bool grounded=false;
            for(int tx=(int)MathF.Floor(x/16);tx<(int)MathF.Ceiling((x+20)/16);tx++)
                for(int ty=(int)MathF.Floor(Math.Min(y,y+vy)/16);ty<(int)MathF.Ceiling((Math.Max(y,y+vy)+42)/16);ty++)
                {
                    char tile=Tile(tx,ty);
                    if(vy>=0&&tile is '#' or '='&&y+42<=ty*16+.001f&&y+42+vy>=ty*16)
                    {vy=ty*16-y-42;grounded=true;}
                    if(vy<0&&tile=='#'&&y>=(ty+1)*16&&y+vy<(ty+1)*16)vy=(ty+1)*16-y;
                }
            y+=vy;
            return new(x,y,vx,grounded?0:vy,grounded);
        }
        public bool SafeSweep(PhysicsState a,PhysicsState b,out string reason)
        {
            for(int x=(int)MathF.Floor((Math.Min(a.Px,b.Px)-2)/16);x<(int)MathF.Ceiling((Math.Max(a.Px,b.Px)+22)/16);x++)
                for(int y=(int)MathF.Floor((Math.Min(a.Py,b.Py)-2)/16);y<(int)MathF.Ceiling((Math.Max(a.Py,b.Py)+44)/16);y++)
                    if(Tile(x,y) is '?' or '!') {reason="unknown/hazard";return false;}
            reason=null;return true;
        }
        public bool SafeLanding(PhysicsState s)=>SafeSweep(s,s,out _)&&MotionSupport.SafeLanding(s,Tile);
        public bool SafeFlatLanding(PhysicsState s)=>SafeLanding(s)&&MotionSupport.SafeFlatLanding(s,Tile);
    }
    private static void Check(bool ok,string message){if(!ok)throw new Exception(message);}

    private static void Scenario(string name,Func<int,int,char> tile,PhysicsState start,LandingRegion[] goals,bool expected)
    {
        var world=new World{Tile=tile};var actual=start;var rows=new List<object>();
        Check(world.SafeLanding(start),name+": valid starting support");
        foreach(var goal in goals)
        {
            // Larger time budget only removes JIT noise from the diagnostic plan;
            // task below always uses the production 32-expansion/25ms defaults.
            var plan=StateSpacePlanner.Plan(actual,goal,PhysicsParameters.Basic,world,budgetMs:500);
            var task=new NavigationTask();int frames=0;
            while(!task.Terminal&&frames++<1800)
            {
                var input=task.Before(401,1,goal,actual,PhysicsParameters.Basic,world);
                actual=PhysicsSimulator.Step(actual,input,PhysicsParameters.Basic,world);task.After(actual,world);
            }
            bool arrived=task.Snapshot.Status=="arrived"&&goal.Arrived(actual,world);
            rows.Add(new{goal,plan=new{plan.ReachedGoal,plan.Expansions,plan.TimeBudgetReached,plan.FailureReason,
                actions=plan.Actions.Count},status=task.Snapshot.Status,task.Snapshot.Reason,frames,
                task.Snapshot.CompletedJumps,task.Snapshot.CompletedActions,x=actual.Px,y=actual.Py});
            Console.WriteLine(JsonSerializer.Serialize(new{name,expected,arrived,rows}));
            Check(arrived==expected,name+": "+task.Snapshot.Status+" "+task.Snapshot.Reason);
            Check(task.Before(401,1,goal,actual,PhysicsParameters.Basic,world)==default,name+": terminal releases input");
            if(!expected)Check(task.Snapshot.Status=="failed",name+": bounded known rejection");
        }
    }

    public static void Run()
    {
        var ground=new PhysicsState(1606,1558,0,0,true);
        char Floor(int x,int y)=>y>=100?'#':'.';
        foreach(int height in new[]{1,2})
            Scenario("obstacle-"+height,(x,y)=>x is >=104 and <=105&&y>=100-height?'#':Floor(x,y),
                ground,new[]{new LandingRegion(1744,1776,1600)},true);
        foreach(int height in new[]{3,5})
            Scenario("solid-up-"+height,(x,y)=>x>=103&&y>=100-height?'#':Floor(x,y),
                ground,new[]{new LandingRegion(1664,1744,(100-height)*16)},true);
        Scenario("platform-up-64",(x,y)=>x>=106&&y==96?'=':Floor(x,y),ground,
            new[]{new LandingRegion(1696,1792,1536)},true);
        Scenario("descent-64",(x,y)=>x<=102&&y==96?'=':Floor(x,y),ground with{Py=1494},
            new[]{new LandingRegion(1696,1760,1600)},true);
        Scenario("alternating-heights",(x,y)=>x is >=106 and <=110&&y==96||x is >=115 and <=121&&y==98?'=':Floor(x,y),ground,
            new[]{new LandingRegion(1696,1744,1536),new LandingRegion(1840,1872,1568),
                new LandingRegion(1696,1744,1536),new LandingRegion(1600,1632,1600)},true);
        char Shaft(int x,int y)=>x is 99 or 102||y>=100?'#':x is 100 or 101&&y==96?'=':'.';
        var shaftWorld=new World{Tile=Shaft};
        var neutral=PhysicsSimulator.Predict(ground,0,12,PhysicsParameters.Basic,shaftWorld,
            rollingJump:true);
        Check(neutral.Safe&&neutral.Frames.All(f=>f.Input.Direction==0)
            &&neutral.Landing.Px==ground.Px&&neutral.Landing.Py==1494,
            "Neutral ascent proves safe support and braking at a different height");
        Check(!PhysicsSimulator.Predict(ground,0,12,PhysicsParameters.Basic,new World{Tile=Floor}).Safe,"Neutral same-floor hop cannot create a search cycle");
        Scenario("same-x-shaft-up",Shaft,ground,new[]{new LandingRegion(1604,1608,1536)},true);
        Scenario("shaft-low-ceiling",(x,y)=>y==93?'#':Shaft(x,y),ground,
            new[]{new LandingRegion(1604,1608,1536)},false);
        Scenario("beyond-jump-height",(x,y)=>x>=106&&y==88?'=':Floor(x,y),ground,
            new[]{new LandingRegion(1696,1792,1408)},false);
        Scenario("no-target-support",Floor,ground,new[]{new LandingRegion(1664,1744,1536)},false);
        Scenario("descent-208",(x,y)=>x<=102&&y==87?'=':Floor(x,y),ground with{Py=1350},
            new[]{new LandingRegion(1696,1760,1600)},true);
        Check(new FallSafety(0,false).Safe(100,125*16+15)&&!new FallSafety(0,false).Safe(100,126*16),
            "Native fall threshold rounds the player's top to a tile row");
        Check(new FallSafety(10,false).Safe(100,135*16)&&!new FallSafety(10,false).Safe(100,136*16)
            &&new FallSafety(0,true).Safe(100,148*16),"Fall protection and extraFall are distinct");
        var deepStart=ground with{Py=73*16-42};
        var deepWorld=new World{Tile=(x,y)=>x<=102&&y==73?'=':Floor(x,y)};
        var protectedProfile=PhysicsParameters.Basic with{Fall=new(10,false)};
        var ordinary=PhysicsSimulator.Predict(deepStart,1,0,PhysicsParameters.Basic,deepWorld,walkFrames:36);
        Check(!ordinary.Safe&&ordinary.Reason.Contains("fall damage"),"A 27-tile landing exceeds ordinary fall tolerance");
        var descent=PhysicsSimulator.Predict(deepStart,1,0,protectedProfile,deepWorld,walkFrames:36);
        Check(descent.Safe&&descent.Landing.Py==ground.Py,"extraFall permits a planned 27-tile descent");
        var executor=new LocalMotionController{LiveFallStart=FallSafety.Row(deepStart.Py)};
        executor.Begin(402,1,1);executor.LoadPlan(descent);
        Check(executor.Before(402,1,deepStart,PhysicsParameters.Basic,deepWorld,1)==default
            &&executor.FailureKind==MotionFailureKind.RouteDeviation,
            "Losing fall protection before departure releases input instead of replaying the descent");
        foreach(char danger in new[]{'!','?'})
            Scenario("target-"+danger,(x,y)=>x>=104&&y>=96?danger:Floor(x,y),ground,
                new[]{new LandingRegion(1696,1744,1536)},false);
    }
}
