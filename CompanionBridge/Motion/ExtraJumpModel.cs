using System;
using System.Linq;

namespace CompanionBridge.Motion;

// Explicit, pinned native model. Discovery stays registry-based; unmodelled
// jumps are never inferred from item IDs or probed by invoking extension hooks.
internal sealed record ExtraJumpModel(string FullName,float DurationMultiplier,int Bit=1,float Acceleration=1,float Speed=1,ExtraJumpModel Next=null)
{
    internal System.Collections.Generic.IEnumerable<LaunchInput> Launches(int hold)
    {
        int count=0;
        for(var jump=this;jump!=null;jump=jump.Next)
        {
            count++;
            for(int delay=1;delay<=25;delay+=8)
            {
                yield return new(hold,ExtraFrame:hold+delay,ExtraCount:count);
                if(jump.DurationMultiplier>1)
                {
                    yield return new(hold,ExtraFrame:hold+delay,ExtraCount:count,ExtraHold:4);
                    yield return new(hold,ExtraFrame:hold+delay,ExtraCount:count,ExtraHold:12);
                }
            }
        }
    }
    internal static readonly ExtraJumpModel Cloud=new("Terraria/CloudInABottleJump",.75f);
    private static readonly ExtraJumpModel[] Kinds = {Cloud,
        new("Terraria/SandstormInABottleJump",3,2,1.5f,2),
        new("Terraria/BlizzardInABottleJump",1.5f,4,3,1.5f),
        new("Terraria/FartInAJarJump",2,8,3,1.75f),
        new("Terraria/TsunamiInABottleJump",1.25f,16,1.5f,1.25f)};
    private static readonly System.Collections.Generic.Dictionary<string,ExtraJumpModel> extensions=new(StringComparer.Ordinal);
    internal static bool Register(string fullName,float duration,float acceleration,float speed)
    {
        if(string.IsNullOrWhiteSpace(fullName)||!fullName.Contains('/')||fullName.StartsWith("Terraria/",StringComparison.Ordinal)
            || !float.IsFinite(duration)||duration<=0||!float.IsFinite(acceleration)||acceleration<=0
            || !float.IsFinite(speed)||speed<=0) return false;
        return extensions.TryAdd(fullName,new(fullName,duration,0,acceleration,speed));
    }
    internal static void ResetExtensions()=>extensions.Clear();
    internal static ExtraJumpModel Kind(string name)=>name==null?null:Array.Find(Kinds,j=>j.FullName==name)
        ?? (extensions.TryGetValue(name,out var model)?model:null);
    internal ExtraJumpModel Find(string name)=>name==FullName?this:Next?.Find(name);
    internal int Mask => Bit | (Next?.Mask??0);
    internal ExtraJumpModel First(int mask)=>(Bit&mask)!=0?this:Next?.First(mask);
    internal int HoldFrames(PhysicsParameters ph)=>(int)(ph.JumpHeight*DurationMultiplier);
    internal static ExtraJumpModel Select(MovementSnapshot movement)
    {
        if(movement==null||movement.BlockExtraJumps)return null;
        ExtraJumpModel chain=null;
        int nextBit=5; // Extension bits belong to the equipped set, independent of installed mod count.
        foreach(var jump in movement.ExtraJumps.Reverse())
        {
            if(!jump.Enabled)continue;
            var model=Kind(jump.FullName);
            if(model==null)return null;
            if(model.Bit==0)
            {
                if(nextBit>=31)return null;
                model=model with{Bit=1<<nextBit++};
            }
            chain=chain==null?model:model with{Next=chain};
        }
        return chain;
    }
    internal static bool MatchesResource(PhysicsState expected,PhysicsState actual,bool boundaryRefresh=false)
        =>expected.ExtraActive==actual.ExtraActive && expected.ActiveJump==actual.ActiveJump
        && (expected.RemainingJumps==actual.RemainingJumps || boundaryRefresh&&expected.Grounded&&actual.Grounded&&actual.ExtraAvailable)
        &&(expected.ExtraAvailable==actual.ExtraAvailable
            ||boundaryRefresh&&expected.Grounded&&actual.Grounded&&actual.ExtraAvailable&&!actual.ExtraActive)
        &&expected.JumpFramesLeft==actual.JumpFramesLeft&&expected.ReleaseJump==actual.ReleaseJump;
    internal static bool ExpectedBoundary(PhysicsState? expected,PhysicsState actual)
        =>expected is {} s&&Math.Abs(s.Px-actual.Px)<=6&&Math.Abs(s.Py-actual.Py)<=6
        &&Math.Abs(s.Vx-actual.Vx)<=1.5f&&Math.Abs(s.Vy-actual.Vy)<=1.5f&&MatchesResource(s,actual,true);
}
