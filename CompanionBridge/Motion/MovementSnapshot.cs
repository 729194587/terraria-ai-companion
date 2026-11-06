using System;
using System.Linq;

namespace CompanionBridge.Motion;

internal sealed record ExtraJumpSnapshot(string FullName,int Order,bool Enabled,bool Available,bool Active);
internal sealed record WingBaseSnapshot(int FlyTime,float AccRunSpeedOverride,float AccRunAccelerationMult,
    bool HasDownHoverStats,float DownHoverSpeedOverride,float DownHoverAccelerationMult);
internal sealed record WingSnapshot(int FunctionalSlot,float Time,int TimeMax,int RegisteredSlots,WingBaseSnapshot BaseStats);
internal sealed record RocketSnapshot(int Boots,int Time,int TimeMax);
internal sealed record MountSnapshot(bool Active,int Type,string ModFullName,int RegisteredTypes);
internal sealed record GrappleSnapshot(int Order,int ProjectileIndex,bool Valid,bool Active,int Type,int Identity,int Owner,bool RegisteredHook);
internal sealed record MovementEnvironment(bool Pulley,int PulleyDir,bool Wet,bool HoneyWet,bool LavaWet,bool ShimmerWet);

// Owns value snapshots only. Defensive array getters also protect prior frames
// and revision comparisons from callers modifying diagnostic payloads.
internal sealed record MovementSnapshot
{
    private readonly ExtraJumpSnapshot[] jumps;
    private readonly GrappleSnapshot[] hooks;
    public ExtraJumpSnapshot[] ExtraJumps=>jumps.ToArray();
    public bool BlockExtraJumps{get;}
    public WingSnapshot Wings{get;}
    public RocketSnapshot Rockets{get;}
    public MountSnapshot Mount{get;}
    public int GrappleCount{get;}
    public bool GrappleListComplete{get;}
    public GrappleSnapshot[] Grapples=>hooks.ToArray();
    public MovementEnvironment Environment{get;}
    public string Scope=>"loaded native registries and current player state; discovery is not navigation support";
    internal bool ActiveExtraJump=>jumps.Any(j=>j.Active);
    internal bool PotentialExtraJump=>!BlockExtraJumps&&jumps.Any(j=>j.Available);
    internal MovementSnapshot(ExtraJumpSnapshot[] extraJumps,bool blockExtraJumps,WingSnapshot wings,RocketSnapshot rockets,
        MountSnapshot mount,int grappleCount,bool grappleListComplete,GrappleSnapshot[] grapples,MovementEnvironment environment)
    {
        jumps=extraJumps.ToArray();hooks=grapples.ToArray();BlockExtraJumps=blockExtraJumps;
        Wings=wings;Rockets=rockets;Mount=mount;GrappleCount=grappleCount;GrappleListComplete=grappleListComplete;Environment=environment;
    }
    internal bool SamePlanningAssumptions(MovementSnapshot other)=>other!=null
        &&jumps.SequenceEqual(other.jumps)&&BlockExtraJumps==other.BlockExtraJumps
        &&Wings.FunctionalSlot==other.Wings.FunctionalSlot&&Wings.RegisteredSlots==other.Wings.RegisteredSlots&&Wings.BaseStats==other.Wings.BaseStats
        &&Rockets.Boots==other.Rockets.Boots&&Mount==other.Mount
        &&GrappleCount==other.GrappleCount&&GrappleListComplete==other.GrappleListComplete&&hooks.SequenceEqual(other.hooks)
        &&Environment==other.Environment;
}
