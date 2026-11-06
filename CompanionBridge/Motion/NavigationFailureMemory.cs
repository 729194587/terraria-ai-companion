using System;
using System.Collections.Generic;
using System.Linq;

namespace CompanionBridge.Motion;

// Action-local quarantine, not a world map or a permanent obstacle assertion.
// Updates are owned by one NavigationTask. No wall-clock/network dependency.
internal sealed class NavigationFailureMemory
{
    internal const int LifetimeUpdates = 120;
    internal const int Capacity=64;
    internal sealed record Failure(string Motion,int Direction,int Hold,int Stride,PhysicsState Start,PhysicsState ExpectedLanding,
        PhysicsState ActualLanding,string Reason,long Terrain,PhysicsParameters Physics,long Expires,int ExtraJumpFrame=-1);
    private sealed record Edge(PhysicsState At, int Direction, int Hold, int Stride, long Expires,
        Prediction Prediction,Failure Detail,int ExtraJumpFrame);
    private readonly List<Edge> edges = new();
    internal int Count => edges.Count;
    internal int Version { get; private set; }
    internal Failure[] Records=>edges.Select(e=>e.Detail).Where(e=>e!=null).ToArray();
    internal void Clear(){if(edges.Count>0)Version++;edges.Clear();}
    internal void Expire(long update){if(edges.RemoveAll(e=>update>=e.Expires)>0)Version++;}
    private static (int Direction, int Hold, int Stride) Signature(Prediction p)
        => (p.Frames[0].Input.Direction, p.Frames.Count(f => f.Input.Jump),
            p.Jump ? 0 : p.Frames.Count(f => f.Input.Direction != 0));
    internal void Remember(PhysicsState at, Prediction p, long update)
        =>Remember(at,p,update,null,null,null,at);

    internal void Remember(PhysicsState at,Prediction p,long update,IMotionWorld world,
        PhysicsParameters physics,string reason,PhysicsState actualLanding)
    {
        var key = Signature(p);
        if(world!=null&&(Math.Abs(actualLanding.Px-p.Landing.Px)>256||Math.Abs(actualLanding.Py-p.Landing.Py)>256))
            return;
        Expire(update);
        if(edges.Count>=Capacity)edges.RemoveAt(0);
        var footprint=world==null?p:p with{Frames=new(p.Frames){new(default,actualLanding)}};
        var detail=world==null?null:new Failure(p.ExtraJumpFrame>=0?"extra_jump":p.Jump?"jump":"walk",key.Direction,key.Hold,key.Stride,at,p.Landing,actualLanding,reason,
            world.TerrainSignature(at,footprint),physics,update+LifetimeUpdates,p.ExtraJumpFrame);
        edges.Add(new(at, key.Direction, key.Hold, key.Stride, update + LifetimeUpdates,footprint,detail,p.ExtraJumpFrame));
        Version++;
    }
    internal bool Reject(PhysicsState start, Prediction p, long update)
        =>Reject(start,p,update,null,null);

    internal bool Reject(PhysicsState start,Prediction p,long update,IMotionWorld world,PhysicsParameters physics)
    {
        Validate(world,physics,update);
        return RejectKnown(start,p,update);
    }
    internal void Validate(IMotionWorld world,PhysicsParameters physics,long update)
    {
        Expire(update);
        if(world!=null)
            if(edges.RemoveAll(e=>e.Detail!=null&&(e.Detail.Physics!=physics
                ||e.Detail.Terrain!=world.TerrainSignature(e.At,e.Prediction)))>0)Version++;
    }
    internal bool RejectKnown(PhysicsState start,Prediction p,long update)
    {
        if (edges.Count==0) return false;
        var key = Signature(p);
        return edges.Any(e => e.Direction == key.Direction && e.Hold == key.Hold && e.Stride == key.Stride&&e.ExtraJumpFrame==p.ExtraJumpFrame
            && (e.Detail!=null?start==e.At
                : Math.Abs(start.Px - e.At.Px) <= 32 && Math.Abs(start.Py - e.At.Py) <= 8
                    && Math.Abs(start.Vx - e.At.Vx) <= .5f));
    }
}
