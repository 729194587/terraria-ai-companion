using System;
using System.Linq;

namespace CompanionBridge.Motion;

// Values are captured at PostUpdateRunSpeeds, not inferred from buff names.
// Session/generation scope the revision; outer action evidence carries Actor/world.
internal sealed record MobilityProfile(long Revision,ulong SourceTick,int Session,int Generation,
    string Variant,PhysicsParameters EffectivePhysics,int Width,int Height,
    string[] AvailableModes,string[] UnsupportedEffects)
{
    public bool Supported=>UnsupportedEffects.Length==0;
    public string Source=>"native_PostUpdateRunSpeeds";
}

internal sealed class MobilityTracker
{
    private MobilityProfile previous;
    private long revision;
    internal void Reset()=>previous=null;
    internal MobilityProfile Sample(PhysicsParameters ph,int width,int height,bool swiftness,
        string[] unsupported,ulong tick,int session,int generation)
    {
        unsupported=unsupported.Length==0?Array.Empty<string>():unsupported.ToArray();
        string variant=swiftness?"vanilla_swiftness":"basic";
        var expected=swiftness?PhysicsParameters.Swiftness:PhysicsParameters.Basic;
        if(!ph.Matches(expected))unsupported=unsupported.Append("unvalidated_effective_physics: "+ph).ToArray();
        if(width!=20||height!=42)unsupported=unsupported.Append("unsupported_body_size").ToArray();
        if(previous==null||previous.Session!=session||previous.Generation!=generation
            ||previous.Variant!=variant||previous.EffectivePhysics!=ph||previous.Width!=width||previous.Height!=height
            ||!previous.UnsupportedEffects.SequenceEqual(unsupported))revision++;
        previous=new(revision,tick,session,generation,variant,ph,width,height,
            unsupported.Length==0?new[]{"ground_walk","ground_jump"}:Array.Empty<string>(),unsupported);
        return previous;
    }
}
