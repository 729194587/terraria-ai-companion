using System;
using System.Linq;

namespace CompanionBridge.Motion;

// Values are captured at PostUpdateRunSpeeds, not inferred from buff names.
// Session/generation scope the revision; outer action evidence carries Actor/world.
internal sealed record MobilityProfile(long Revision,ulong SourceTick,int Session,int Generation,
    string Variant,PhysicsParameters EffectivePhysics,int Width,int Height,
    string[] AvailableModes,string[] UnsupportedEffects,bool SwiftnessExpiryTail=false,MovementSnapshot Movement=null)
{
    public bool Supported=>UnsupportedEffects.Length==0;
    public string Source=>"native_PostUpdateRunSpeeds";
}

internal sealed class MobilityTracker
{
    private MobilityProfile previous;
    private long revision;
    private int previousSwiftnessTime;
    internal void Reset()=>previous=null;
    // Local Player.Update applies the last buff tick before removing its slot.
    // Accept that one measured Swift frame only with consecutive, scoped evidence.
    internal bool IsSwiftnessExpiryTail(PhysicsParameters ph,bool swiftness,ulong tick,int session,int generation)
        =>!swiftness&&previous?.Supported==true&&previous.Variant=="vanilla_swiftness"
        &&previous.Session==session&&previous.Generation==generation&&previous.SourceTick+1==tick
        &&previousSwiftnessTime==1&&ph.Matches(PhysicsParameters.Swiftness);
    internal MobilityProfile Sample(PhysicsParameters ph,int width,int height,bool swiftness,
        string[] unsupported,ulong tick,int session,int generation,int swiftnessTime=0,MovementSnapshot movement=null)
    {
        bool expiryTail=IsSwiftnessExpiryTail(ph,swiftness,tick,session,generation);
        unsupported=unsupported.Length==0?Array.Empty<string>():unsupported.ToArray();
        string variant=ph.IsBasic?"basic":ph.Matches(PhysicsParameters.Swiftness)?"vanilla_swiftness":"measured_ground";
        if(!ph.IsSupportedGroundPhysics)unsupported=unsupported.Append("unsupported_effective_physics: "+ph).ToArray();
        if(width!=PhysicsSimulator.PlayerW||height!=PhysicsSimulator.PlayerH)unsupported=unsupported.Append("unsupported_body_size").ToArray();
        if(previous==null||previous.Session!=session||previous.Generation!=generation
            ||previous.Variant!=variant||previous.EffectivePhysics!=ph||previous.Width!=width||previous.Height!=height
            ||!previous.UnsupportedEffects.SequenceEqual(unsupported)
            ||(movement==null?previous.Movement!=null:!movement.SamePlanningAssumptions(previous.Movement)))revision++;
        previous=new(revision,tick,session,generation,variant,ph,width,height,
            unsupported.Length==0?new[]{"ground_walk","ground_jump"}:Array.Empty<string>(),unsupported,expiryTail,movement);
        previousSwiftnessTime=swiftness?swiftnessTime:0;
        return previous;
    }
}
