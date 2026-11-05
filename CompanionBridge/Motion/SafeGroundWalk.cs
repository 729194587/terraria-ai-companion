using System;
using System.Diagnostics;
using System.Linq;

namespace CompanionBridge.Motion;

// A conservative one-edge producer, not another executor or a replacement search.
internal static class SafeGroundWalk
{
    internal static Route TryPlan(PhysicsState start,float targetX,int direction,PhysicsParameters ph,IMotionWorld world,LandingRegion region=null)
    {
        var watch=Stopwatch.StartNew();
        if(region!=null&&Math.Abs(start.Py+PhysicsSimulator.PlayerH-region.SurfaceY)>LandingRegion.FeetTolerance)return null;
        if(!ph.IsSupportedGroundPhysics||direction is not (-1 or 1)||!float.IsFinite(targetX)
            ||(region?.Contains(start)??(direction*(targetX-start.Px)<=12))||!PhysicsSimulator.SupportedCheckpoint(start,world)
            ||!world.SafeFlatLanding(start))return null;
        var strides=region!=null&&region.Distance(start)<=48
            ?Enumerable.Range(1,24).Reverse():new[]{24,6}.AsEnumerable();
        foreach(int stride in strides)
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
