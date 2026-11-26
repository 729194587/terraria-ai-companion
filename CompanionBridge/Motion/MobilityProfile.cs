using System;
using System.Linq;

namespace CompanionBridge.Motion;

// Values are captured at PostUpdateRunSpeeds, not inferred from buff names.
// Session/generation scope the revision; outer action evidence carries Actor/world.
internal sealed record MobilityProfile(long Revision,ulong SourceTick,int Session,int Generation,
    string Variant,PhysicsParameters EffectivePhysics,int Width,int Height,
    string[] AvailableModes,string[] UnsupportedEffects,bool SwiftnessExpiryTail=false,MovementSnapshot Movement=null,PhysicsParameters ObservedPhysics=null)
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
        string[] unsupported,ulong tick,int session,int generation,int swiftnessTime=0,MovementSnapshot movement=null,bool expectedExtraJump=false,bool unexpectedAirResource=false,PhysicsParameters observedPhysics=null)
    {
        bool expiryTail=IsSwiftnessExpiryTail(ph,swiftness,tick,session,generation);
        unsupported=unsupported.Length==0?Array.Empty<string>():unsupported.ToArray();
        string variant=ph.IsBasic?"basic":ph.Matches(PhysicsParameters.Swiftness)?"vanilla_swiftness":"measured_ground";
        if(!ph.IsSupportedGroundPhysics)unsupported=unsupported.Append("unsupported_effective_physics: "+ph).ToArray();
        if(width!=PhysicsSimulator.PlayerW||height!=PhysicsSimulator.PlayerH)unsupported=unsupported.Append("unsupported_body_size").ToArray();
        // The adapter grants this exemption only against the executed plan's
        // prior frame and actual native state. Registry/enabled/physics changes
        // still invalidate; the immutable snapshot always keeps the real bits.
        if(previous==null||unexpectedAirResource||previous.Session!=session||previous.Generation!=generation
            ||previous.Variant!=variant||previous.EffectivePhysics!=ph||previous.Width!=width||previous.Height!=height
            ||ph.Air!=null&&previous.Movement!=null&&movement!=null
                &&(previous.Movement.Wings.TimeMax!=movement.Wings.TimeMax||previous.Movement.Rockets.TimeMax!=movement.Rockets.TimeMax)
            ||!previous.UnsupportedEffects.SequenceEqual(unsupported)
            ||(movement==null?previous.Movement!=null:!movement.SamePlanningAssumptions(previous.Movement,expectedExtraJump,ph.CanWade)))revision++;
        var modes = new System.Collections.Generic.List<string>();
        if (unsupported.Length == 0)
        {
            modes.Add("ground_walk");
            if (!ph.GroundOnly) modes.Add("ground_jump");
            if (ph.ExtraJump != null) modes.Add("extra_jump");
            if (ph.Air != null) modes.Add(ph.Air.Kind==AirKind.Wings ? "wings" : "rocket_pulse");
            for (var action=ph.Impulse;action!=null;action=action.Next) modes.Add(action.Key);
        }
        previous=new(revision,tick,session,generation,variant,ph,width,height,
            modes.ToArray(),unsupported,expiryTail,movement,observedPhysics);
        previousSwiftnessTime=swiftness?swiftnessTime:0;
        return previous;
    }
}
