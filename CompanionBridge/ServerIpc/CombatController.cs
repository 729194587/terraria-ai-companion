using System.Collections.Generic;
using System.Text.Json.Serialization;
using Result = CompanionBridge.ServerIpc.AiAtomicActions.NativeResult;
using Identity = CompanionBridge.ServerIpc.AiReadOnlyProtocol.Identity;

namespace CompanionBridge.ServerIpc;

internal sealed record CombatTarget(int Slot, int TypeId, string EntityRef);
internal sealed record SummonImpact(int Owner,int ProjectileType,int ProjectileIdentity,int? ParentIdentity,ulong SpawnUpdate,int TargetEpoch);
internal sealed record CombatHit(int LifeBefore, int LifeAfter, int DamageDone, bool Defeated, ulong GameUpdate)
{
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public SummonImpact Summon { get; init; }
}
internal sealed record CombatObservation(bool SameSpawn, bool Active, bool Eligible, int Life);
internal sealed record CombatEncounter(CombatTarget Target, string Reason, CombatHit LastHit);
internal sealed record CombatWeapon(int Slot, int TypeId, string Name);

internal sealed record CombatSummonObservation(bool InputAttempted, bool NativeShootObserved, int RootType,
    int? ObservedRootIdentity, bool? BuffObserved, int VerifiedChildren, int UnconfirmedChildren);

internal sealed record CombatProgress(string source,string threat,CombatTarget target,CombatWeapon weapon,int? life,
    CombatHit lastHit,int itemChecks,int combinedUpdates,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] CombatEncounter[] encounters,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] CombatSummonObservation summon);

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
    private readonly List<CombatEncounter> encounters = new();
    private readonly HashSet<string> skipped = new();
    internal string TargetRef => world.Target?.EntityRef;
    internal override string Name => CombatProtocol.Operation;
    internal override string Phase => phase;
    internal override object Progress => new CombatProgress(source,threat,world.Target,world.Weapon,life,world.LastHit,
        world.ItemChecks,world.CombinedUpdates,source=="reflex" ? encounters.ToArray() : null,world.Summon);
    internal override Result Prepare(Identity frame) => world.Prepare() is {} reason ? Failed(reason) : null;
    internal override void Start(Identity frame)
    {
        world.Acquire();
        acquired = true;
    }

    internal override Result Tick(Identity frame)
    {
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
        if (failure=="combat_firing_navigation_timeout")return Stop(frame,"timeout",failure);
        if (source=="reflex" && failure is ("combat_target_out_of_range" or "combat_approach_failed" or "combat_shot_blocked" or "combat_firing_navigation_no_route"))
            return ContinueDefense(frame,failure);
        return Stop(frame, failure == "combat_native_unconfirmed" ? "unknown" : "failed", failure);
    }

    private Result ContinueDefense(Identity frame,string outcome)
    {
        encounters.Add(new(world.Target,outcome,world.LastHit));
        // Exclude only targets whose approach failed; a temporarily invalid target
        // may become a real threat again while another target is being handled.
        if(outcome is "combat_target_out_of_range" or "combat_approach_failed" or "combat_shot_blocked" or "combat_firing_navigation_no_route")skipped.Add(TargetRef);
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
        return new(status, reason, Progress, released, released && status == "completed", acquired);
    }

    internal override void Abandon() => world.Abandon();
}
