using System.Collections.Generic;
using System.Text.Json.Serialization;
using Result = CompanionBridge.ServerIpc.AiAtomicActions.NativeResult;
using Identity = CompanionBridge.ServerIpc.AiReadOnlyProtocol.Identity;

namespace CompanionBridge.ServerIpc;

internal sealed record CombatTarget(int Slot, int TypeId, string EntityRef);
internal sealed record SummonImpact(int Owner,int ProjectileType,int ProjectileIdentity,int? ParentIdentity,ulong SpawnUpdate,int TargetEpoch)
{
    public int WeaponType {get;init;}
}
internal sealed record ProjectileImpact(int Weapon,int Ammo,int Type,int Owner,int Identity,int Epoch,ulong Launched);
internal sealed record CombatHit(int LifeBefore, int LifeAfter, int DamageDone, bool Defeated, ulong GameUpdate)
{
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public SummonImpact Summon { get; init; }
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public int? MeleeWeaponType {get;init;}
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public ProjectileImpact Projectile {get;init;}
}
internal sealed record CombatObservation(bool SameSpawn, bool Active, bool Eligible, int Life);
internal sealed record CombatEncounter(CombatTarget Target, string Reason, CombatHit LastHit,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] CombatContributions Contributions=null);
internal sealed record CombatSelection(string Role,long Version,int PreviousSlot,ulong SelectedUpdate,
    ulong AdmittedUpdate,string InterruptedAction);
internal sealed record CombatWeapon(int Slot, int TypeId, string Name)
{
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public CombatSelection Selection {get; init;}
}

internal sealed record CombatSummonObservation(bool InputAttempted, bool NativeShootObserved, int RootType,
    int? ObservedRootIdentity, bool? BuffObserved, int VerifiedChildren, int UnconfirmedChildren);

internal sealed record CombatContributions(CombatHit Melee,CombatHit Summon);
internal sealed record CombatProgress(string source,string threat,CombatTarget target,CombatWeapon weapon,int? life,
    CombatHit lastHit,int itemChecks,int combinedUpdates,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] CombatEncounter[] encounters,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] CombatSummonObservation summon,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] CombatContributions contributions)
{
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public object defense {get;init;}
}

internal static class CombatStrikeEvidence
{
    internal static CombatHit Observe(bool sameSpawn, int before, int after, int damageDone, bool active, ulong tick)
        => sameSpawn && before > 0 && after < before && damageDone > 0
            ? new(before,after,damageDone,after<=0 && !active,tick) : null;
}

internal sealed class CombatInputCycle
{
    private ulong? lastCheck, lastMovement, lastSwing, lastCombined;
    internal int ItemChecks { get; private set; }
    internal int CombinedUpdates { get; private set; }

    internal bool BeginItemCheck(ulong tick)
    {
        if (lastCheck == tick) return false;
        lastCheck = tick;
        ItemChecks++;
        return true;
    }

    internal void Movement(ulong tick) { lastMovement = tick; Combine(tick); }
    internal void Swing(ulong tick) { lastSwing = tick; Combine(tick); }

    private void Combine(ulong tick)
    {
        if (lastMovement != tick || lastSwing != tick || lastCombined == tick) return;
        lastCombined = tick;
        CombinedUpdates++;
    }
}

internal interface ICombatWorld
{
    CombatTarget Target { get; }
    CombatWeapon Weapon { get; }
    CombatHit LastHit { get; }
    CombatSummonObservation Summon => null;
    CombatContributions Contributions => null;
    object Defense => null;
    string DangerRef => null;
    int ItemChecks { get; }
    int CombinedUpdates { get; }
    string Prepare();
    void Acquire();
    CombatObservation Observe();
    string Step(out string phase);
    string Retarget(Identity frame,IReadOnlySet<string> skipped);
    bool Release();
    void Abandon();
}

internal sealed class CombatController(ICombatWorld world, string source, string threat) : CompanionTask
{
    internal const ulong Budget = 1800;
    private string phase = "acquire_target";
    private int? life;
    private bool acquired;
    private bool stopped;
    private readonly List<CombatEncounter> encounters = new();
    private readonly HashSet<string> skipped = new();
    internal string TargetRef => world.Target?.EntityRef??world.DangerRef;
    internal override string Name => CombatProtocol.Operation;
    internal override string Phase => phase;
    private CombatEncounter LastEncounter => encounters.Count>0?encounters[^1]:null;
    internal override object Progress => new CombatProgress(source,threat,world.Target??(stopped?LastEncounter?.Target:null),world.Weapon,life,
        world.Target!=null?world.LastHit:stopped?LastEncounter?.LastHit:null,
        world.ItemChecks,world.CombinedUpdates,source=="reflex" ? encounters.ToArray() : null,world.Summon,world.Contributions){defense=world.Defense};
    internal override Result Prepare(Identity frame) => world.Prepare() is {} reason ? Failed(reason) : null;
    internal override void Start(Identity frame)
    {
        world.Acquire();
        acquired = true;
    }

    internal override Result Tick(Identity frame)
    {
        if(source=="reflex" && world.Target==null)return ContinueDefense(frame,null);
        var target = world.Observe();
        life = target.SameSpawn ? target.Life : null;
        phase = "verify_result";
        string outcome = !target.SameSpawn ? "combat_target_replaced"
            : world.LastHit is { Defeated: true, LifeAfter: <= 0 } && target.Life <= 0 ? "combat_target_defeated"
            : !target.Active || !target.Eligible || target.Life <= 0 ? "combat_target_invalidated"
            : null;
        if (outcome != null)
            return source=="reflex" ? ContinueDefense(frame,outcome)
                : Stop(frame,outcome=="combat_target_defeated"?"completed":"failed",outcome);

        string failure = world.Step(out phase);
        if (failure == null) return Running();
        if (source=="reflex" && ApproachFailed(failure))
            return ContinueDefense(frame,failure);
        if (failure=="combat_firing_navigation_timeout")return Stop(frame,"timeout",failure);
        return Stop(frame, failure == "combat_native_unconfirmed" ? "unknown" : "failed", failure);
    }

    private static bool ApproachFailed(string reason) => reason is "combat_target_out_of_range" or "combat_approach_failed"
        or "combat_shot_blocked" or "combat_firing_navigation_no_route" or "combat_firing_navigation_timeout";

    private Result ContinueDefense(Identity frame,string outcome)
    {
        if(world.Target!=null)encounters.Add(new(world.Target,outcome,world.LastHit,world.Contributions));
        // Exclude only targets whose approach failed; a temporarily invalid target
        // may become a real threat again while another target is being handled.
        if(ApproachFailed(outcome))skipped.Add(TargetRef);
        var next=world.Retarget(frame,skipped);
        if(next==null)
        {
            life=null;
            phase="reassess";
            return Running();
        }
        return Stop(frame,next=="combat_threats_cleared"?"completed"
            :next=="combat_native_unconfirmed"?"unknown":"failed",next);
    }

    internal override Result Stop(Identity frame, string status, string reason)
    {
        bool released = world.Release();
        stopped=true;
        return new(status, reason, Progress, released, released && status == "completed", acquired);
    }

    internal override void Abandon() => world.Abandon();
}
