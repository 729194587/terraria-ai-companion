using System;
using System.Collections.Generic;

namespace CompanionBridge.Motion;

// One root journey owns this knowledge. Child movement contexts may share it.
internal sealed class NavigationKnowledge
{
    private PhysicsParameters physics;
    private object actor;
    private string world;
    private long generation;
    private int slot;
    internal NavigationFailureMemory Failures { get; }=new();
    internal NavigationSearchMemory Search { get; }=new();
    internal SurfaceGraph Surfaces { get; }=new();
    internal bool GrappleAttempted { get; set; }
    internal void Clear(){Failures.Clear();Search.Clear();Surfaces.Clear();physics=null;}
    internal void ObservePhysics(PhysicsParameters profile)
    {
        if(profile==null||physics!=profile)Clear();
        physics=profile;
    }
    internal void Bind(object boundActor,string boundWorld,long boundGeneration,int boundSlot)
    {
        if(!ReferenceEquals(actor,boundActor)||world!=boundWorld||generation!=boundGeneration||slot!=boundSlot)Clear();
        actor=boundActor;world=boundWorld;generation=boundGeneration;slot=boundSlot;
    }
}

// Predictions rejected during search are not execution failures. Only exact
// physical states in an unchanged terrain/profile scope reuse a rejection.
internal sealed class NavigationSearchMemory
{
    internal const int Capacity=256,LifetimeUpdates=120;
    private readonly record struct Key(PhysicsState State,int Direction,int Hold,int Stride,int ExtraFrame,int AirStart,int AirHold);
    private sealed record Entry(Prediction Prediction,long Expires);
    private readonly Dictionary<Key,Entry> rejected=new();
    private readonly Queue<Key> order=new();
    private readonly Dictionary<PhysicsState,(int Count,long Expires)> expanded=new();
    private long terrain;
    private PhysicsParameters physics;
    private long update;
    internal int CacheHits { get; private set; }
    internal int Count=>rejected.Count;
    internal int ExpansionHits { get; private set; }
    internal void Clear(){rejected.Clear();order.Clear();expanded.Clear();physics=null;}
    internal void Expanded(PhysicsState state)
    {
        if(expanded.Count>=Capacity&&!expanded.ContainsKey(state))
        {
            using var keys=expanded.Keys.GetEnumerator();keys.MoveNext();expanded.Remove(keys.Current);
        }
        expanded.TryGetValue(state,out var previous);
        int count=update<previous.Expires?previous.Count:0;
        expanded[state]=(Math.Min(8,count+1),update+LifetimeUpdates);
    }
    internal int Preference(PhysicsState state)
    {
        if(!expanded.TryGetValue(state,out var value)||update>=value.Expires)return 0;
        ExpansionHits++;
        return value.Count;
    }
    internal void Scope(long stamp,PhysicsParameters profile,long now)
    {
        if(terrain!=stamp||physics!=profile)Clear();
        terrain=stamp;physics=profile;update=now;
    }
    internal Prediction Predict(PhysicsState state,int direction,int hold,int stride,
        PhysicsParameters profile,IMotionWorld world,int extraFrame=-1,int airStart=-1,int airHold=0)
    {
        var key=new Key(state,direction,hold,stride,extraFrame,airStart,airHold);
        if(rejected.TryGetValue(key,out var entry))
        {
            if(update<entry.Expires){CacheHits++;return entry.Prediction;}
            rejected.Remove(key);
        }
        var prediction=PhysicsSimulator.Predict(state,direction,hold,profile,world,
            walkFrames:stride,rollingJump:true,maxDropPixels:StateSpacePlanner.MaxDropPixels,extraJumpFrame:extraFrame,airStartFrame:airStart,airHoldFrames:airHold);
        if(!prediction.Safe)
        {
            while(order.Count>0&&(rejected.Count>=Capacity||order.Count>=Capacity))
                rejected.Remove(order.Dequeue());
            rejected[key]=new(prediction,update+LifetimeUpdates);
            order.Enqueue(key);
        }
        return prediction;
    }
}
