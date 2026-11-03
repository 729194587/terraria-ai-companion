using System;
using System.Collections.Generic;
using System.Linq;

namespace CompanionBridge.Motion;

// Action-local quarantine, not a world map or a permanent obstacle assertion.
// Updates are owned by one NavigationTask. No wall-clock/network dependency.
internal sealed class NavigationFailureMemory
{
    internal const int LifetimeUpdates = 120;
    private sealed record Edge(PhysicsState At, int Direction, int Hold, int Stride, int Expires);
    private readonly List<Edge> edges = new();
    internal int Count => edges.Count;
    internal void Clear() => edges.Clear();
    internal void Expire(int update) => edges.RemoveAll(e => update >= e.Expires);
    private static (int Direction, int Hold, int Stride) Signature(Prediction p)
        => (p.Frames[0].Input.Direction, p.Frames.Count(f => f.Input.Jump),
            p.Jump ? 0 : p.Frames.Count(f => f.Input.Direction != 0));
    internal void Remember(PhysicsState at, Prediction p, int update)
    {
        var key = Signature(p);
        edges.Add(new(at, key.Direction, key.Hold, key.Stride, update + LifetimeUpdates));
    }
    internal bool Reject(PhysicsState start, Prediction p, int update)
    {
        Expire(update);
        if (edges.Count==0) return false;
        var key = Signature(p);
        return edges.Any(e => e.Direction == key.Direction && e.Hold == key.Hold && e.Stride == key.Stride
            && Math.Abs(start.Px - e.At.Px) <= 32 && Math.Abs(start.Py - e.At.Py) <= 8
            && Math.Abs(start.Vx - e.At.Vx) <= .5f);
    }
}
