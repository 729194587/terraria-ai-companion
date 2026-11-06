using System;
using System.Linq;

namespace CompanionBridge.Motion;

// Explicit, pinned native model. Discovery stays registry-based; unmodelled
// jumps are never inferred from item IDs or probed by invoking extension hooks.
internal sealed record ExtraJumpModel(string FullName,float DurationMultiplier)
{
    internal static readonly ExtraJumpModel Cloud=new("Terraria/CloudInABottleJump",.75f);
    internal int HoldFrames(PhysicsParameters ph)=>(int)(ph.JumpHeight*DurationMultiplier);
    internal static ExtraJumpModel Select(MovementSnapshot movement)
    {
        if(movement==null||movement.BlockExtraJumps)return null;
        var enabled=movement.ExtraJumps.Where(j=>j.Enabled).ToArray();
        return enabled.Length==1&&enabled[0].FullName==Cloud.FullName?Cloud:null;
    }
    internal static bool MatchesResource(PhysicsState expected,PhysicsState actual,bool boundaryRefresh=false)
        =>expected.ExtraActive==actual.ExtraActive
        &&(expected.ExtraAvailable==actual.ExtraAvailable
            ||boundaryRefresh&&expected.Grounded&&actual.Grounded&&actual.ExtraAvailable&&!actual.ExtraActive)
        &&expected.JumpFramesLeft==actual.JumpFramesLeft&&expected.ReleaseJump==actual.ReleaseJump;
    internal static bool ExpectedBoundary(PhysicsState? expected,PhysicsState actual)
        =>expected is {} s&&Math.Abs(s.Px-actual.Px)<=6&&Math.Abs(s.Py-actual.Py)<=6
        &&Math.Abs(s.Vx-actual.Vx)<=1.5f&&Math.Abs(s.Vy-actual.Vy)<=1.5f&&MatchesResource(s,actual,true);
}
