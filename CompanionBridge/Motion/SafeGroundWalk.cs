using System;
using System.Diagnostics;

namespace CompanionBridge.Motion;

// A conservative one-edge producer, not another executor or a replacement search.
internal static class SafeGroundWalk
{
    internal static Route TryPlan(PhysicsState start,float targetX,int direction,PhysicsParameters ph,IMotionWorld world)
    {
        var watch=Stopwatch.StartNew();
        if(!ph.IsBasic||direction is not (-1 or 1)||!float.IsFinite(targetX)
            ||direction*(targetX-start.Px)<=12||!PhysicsSimulator.SupportedCheckpoint(start,world)
            ||!world.SafeFlatLanding(start))return null;
        foreach(int stride in new[]{24,6})
        {
            // Includes the original complete braking/stable-support proof.
            var edge=PhysicsSimulator.Predict(start,direction,0,ph,world,walkFrames:stride,
                maxDropPixels:StateSpacePlanner.MaxDropPixels);
            if(!edge.Safe||edge.Jump||edge.Frames.Count==0)continue;
            bool flat=true;
            foreach(var frame in edge.Frames)
                if(Math.Abs(frame.State.Py-start.Py)>0.01f
                    ||!PhysicsSimulator.SupportedCheckpoint(frame.State,world)||!world.SafeFlatLanding(frame.State))
                {flat=false;break;}
            if(!flat||direction*(edge.Landing.Px-targetX)>12)continue;
            bool settle=direction*(targetX-edge.Landing.Px)<=12;
            int execute=settle?edge.Frames.Count:edge.ExecuteFrames;
            if(!PhysicsSimulator.SupportedCheckpoint(edge.Frames[execute-1].State,world))continue;
            edge=edge with{ExecuteFrames=execute,Landing=edge.Frames[execute-1].State,
                Reason=settle?"safe flat walk and braking":"safe flat walk prefix; braking tail validated"};
            return new(new(){edge},0,watch.Elapsed.TotalMilliseconds,ReachedGoal:settle,Kind:"safe_ground_walk");
        }
        return null;
    }
}
