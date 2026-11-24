using System;
using System.Collections.Generic;

namespace CompanionBridge.Motion;

// Only a verified shore-to-shore action may use this world's submerged sweeps.
internal interface IPoolMotionWorld
{
    int Breath { get; }
    IMotionWorld PoolWorld(PhysicsState start);
    bool WaterAhead(PhysicsState start,int direction);
}

internal static class PoolCrossing
{
    internal const int Reserve=20;
    internal const string Kind="pool_crossing";

    internal static Route TryPlan(PhysicsState start,LandingRegion goal,PhysicsParameters physics,IMotionWorld ordinary)
    {
        float targetX=Math.Clamp(start.Px,goal.MinPlayerLeftX,goal.MaxPlayerLeftX);
        int side=Math.Sign(targetX-start.Px);
        if(side==0||!physics.CanWade||!start.Grounded||ordinary is not IPoolMotionWorld pool
            ||pool.Breath<=Reserve||!pool.WaterAhead(start,side))return null;
        var world=pool.PoolWorld(start);
        // First follow the bottom. Also try jumping from the first submerged foothold.
        // Charge one breath point per predicted update, including the dry approach.
        // Native ordinary-water consumption can only be slower than this bound.
        foreach(bool jumpOnEntry in new[]{false,true})
        {
            var frames=new List<PredictedFrame>();
            var pose=start;
            bool touched=false,braking=false,entryJump=false;
            int hold=0,stuck=0,fallStart=FallSafety.Row(start.Py);
            try
            {
                for(int frame=0;frame<Math.Min(360,pool.Breath-Reserve);frame++)
                {
                    bool wet=world.WaterContact(pose);
                    if(touched&&!wet&&pose.Grounded&&side*(pose.Px-start.Px)>16)braking=true;
                    if(!braking&&wet&&pose.Grounded&&pose.ReleaseJump&&(stuck>=2||jumpOnEntry&&!entryJump))
                    {hold=PhysicsParameters.WaterJumpHeight;entryJump=true;stuck=0;}
                    var input=new PhysicsInput(braking?0:side,hold>0);
                    if(hold>0)hold--;
                    var next=PhysicsSimulator.Step(pose,input,physics,world);
                    if(!world.SafeSweep(pose,next,out _))break;
                    if(pose.Grounded||next.Py<pose.Py)fallStart=FallSafety.Row(next.Py);
                    if(next.Grounded&&!physics.Fall.Safe(fallStart,next.Py)&&!world.NegatesFallDamage(next))break;
                    frames.Add(new(input,next));
                    touched|=wet;
                    stuck=Math.Abs(next.Px-pose.Px)<.1f?stuck+1:0;
                    pose=next;
                    if(braking&&!world.WaterContact(pose)&&PhysicsSimulator.Settled(pose,ordinary)
                        &&Math.Abs(targetX-pose.Px)
                            <Math.Abs(targetX-start.Px))
                    {
                        var edge=new Prediction(true,Kind,frames,pose,frames.Count,false);
                        return new(new(){edge},0,0,ReachedGoal:goal.Contains(pose),Kind:Kind);
                    }
                    if(stuck>36&&!braking)break;
                }
            }
            catch(UnsafeMotionException){ }
        }
        return null;
    }
}
