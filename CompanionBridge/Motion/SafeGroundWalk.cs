using System;
using System.Diagnostics;
using System.Linq;

namespace CompanionBridge.Motion;

// A conservative one-edge producer, not another executor or a replacement search.
internal static class SafeGroundWalk
{
    internal static Route TryAscend(PhysicsState start,LandingRegion goal,PhysicsParameters ph,IMotionWorld world)
    {
        if(goal.SurfaceY>=start.Py+PhysicsSimulator.PlayerH-LandingRegion.FeetTolerance ||
            start.Wet || !PhysicsSimulator.SupportedCheckpoint(start,world))return null;
        float target=Math.Clamp(start.Px,goal.MinPlayerLeftX,goal.MaxPlayerLeftX);
        if(Math.Abs(target-start.Px)<16)return null;
        int direction=Math.Sign(target-start.Px);
        var watch=Stopwatch.StartNew();
        foreach(int hold in new[]{0,8,ph.JumpHeight})
        {
            var edge=PhysicsSimulator.Predict(start,direction,hold,ph,world,walkFrames:30,walkingGoal:goal);
            if(!edge.Safe || edge.Landing.Py>start.Py-4 || edge.Frames.Any(f=>f.State.Wet)
                || direction*(edge.Landing.Px-start.Px)<4 || goal.Distance(edge.Landing)>=goal.Distance(start)
                || (direction>0?edge.Landing.Px>goal.MaxPlayerLeftX:edge.Landing.Px<goal.MinPlayerLeftX))continue;
            return new(new(){edge with{ExecuteFrames=edge.Frames.Count}},0,watch.Elapsed.TotalMilliseconds,
                ReachedGoal:goal.Arrived(edge.Landing,world),Kind:"uphill_approach");
        }
        return null;
    }

    internal static Route TryDescend(PhysicsState start,LandingRegion goal,PhysicsParameters ph,IMotionWorld world)
    {
        if(!ph.IsSupportedGroundPhysics || goal.SurfaceY<=start.Py+PhysicsSimulator.PlayerH+LandingRegion.FeetTolerance
            || !PhysicsSimulator.SupportedCheckpoint(start,world) || start.Wet)return null;
        var watch=Stopwatch.StartNew();
        int direction=Math.Sign((goal.MinPlayerLeftX+goal.MaxPlayerLeftX)/2-start.Px);
        if(direction==0)return null;
        foreach(int stride in new[]{36,24,12,6})
        {
            var edge=PhysicsSimulator.Predict(start,direction,0,ph,world,walkFrames:stride,walkingGoal:goal);
            if(!edge.Safe || edge.Landing.Py<=start.Py+1 || edge.Frames.Any(f=>f.State.Wet)
                || goal.Distance(edge.Landing)>=goal.Distance(start)
                || (direction>0?edge.Landing.Px>goal.MaxPlayerLeftX:edge.Landing.Px<goal.MinPlayerLeftX))continue;
            // Execute the braking tail before selecting the next stair.
            edge=edge with{ExecuteFrames=edge.Frames.Count};
            return new(new(){edge},0,watch.Elapsed.TotalMilliseconds,ReachedGoal:goal.Arrived(edge.Landing,world),Kind:"downhill_walk");
        }
        return null;
    }

    internal static Route TryPlan(PhysicsState start,float targetX,int direction,PhysicsParameters ph,IMotionWorld world,LandingRegion region=null)
    {
        var watch=Stopwatch.StartNew();
        if(region!=null&&Math.Abs(start.Py+PhysicsSimulator.PlayerH-region.SurfaceY)>LandingRegion.FeetTolerance)return null;
        if(!ph.IsSupportedGroundPhysics||direction is not (-1 or 1)||!float.IsFinite(targetX)
            ||(region?.Contains(start)??(direction*(targetX-start.Px)<=12))||!PhysicsSimulator.SupportedCheckpoint(start,world))return null;
        var strides=region!=null&&region.Distance(start)<=48
            ?Enumerable.Range(1,24).Reverse():new[]{24,6}.AsEnumerable();
        foreach(int stride in strides)
        {
            // Includes the original complete braking/stable-support proof.
            var edge=PhysicsSimulator.Predict(start,direction,0,ph,world,walkFrames:stride,walkingGoal:region);
            if(!edge.Safe||edge.Jump||edge.Frames.Count==0)continue;
            bool flat=true;
            foreach(var frame in edge.Frames)
                if(Math.Abs(frame.State.Py-start.Py)>0.01f
                    ||!PhysicsSimulator.SupportedCheckpoint(frame.State,world))
                {flat=false;break;}
            if(!flat||direction*(edge.Landing.Px-targetX)>12)continue;
            bool settle=region?.Arrived(edge.Landing,world)??(direction*(targetX-edge.Landing.Px)<=12);
            if(region!=null&&!settle)
            {
                // A rolling prefix must leave room to brake before the goal's
                // far edge, even when its endpoint reduces the goal distance.
                bool overshoot=direction>0?edge.Landing.Px>region.MaxPlayerLeftX:edge.Landing.Px<region.MinPlayerLeftX;
                if(overshoot||region.Distance(edge.Landing)>=region.Distance(start)-.01f)continue;
            }
            int execute=settle?edge.Frames.Count:edge.ExecuteFrames;
            var checkpoint=edge.Frames[execute-1].State;
            if(!PhysicsSimulator.SupportedCheckpoint(checkpoint,world)
                ||region!=null&&!settle&&region.Distance(checkpoint)>=region.Distance(start)-.01f)continue;
            edge=edge with{ExecuteFrames=execute,Landing=checkpoint,
                Reason=settle?"safe flat walk and braking":"safe flat walk prefix; braking tail validated"};
            return new(new(){edge},0,watch.Elapsed.TotalMilliseconds,ReachedGoal:settle,Kind:"safe_ground_walk");
        }
        return null;
    }
}
