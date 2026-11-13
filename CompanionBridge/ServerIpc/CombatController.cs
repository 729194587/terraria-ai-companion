using Result = CompanionBridge.ServerIpc.AiAtomicActions.NativeResult;
using Identity = CompanionBridge.ServerIpc.AiReadOnlyProtocol.Identity;

namespace CompanionBridge.ServerIpc;

internal sealed record CombatTarget(int Slot, int TypeId, string EntityRef);
internal sealed record CombatHit(int LifeBefore, int LifeAfter, int DamageDone, bool Defeated, ulong GameUpdate);
internal sealed record CombatObservation(bool SameSpawn, bool Active, bool Eligible, int Life);
internal sealed record CombatWeapon(int Slot, int TypeId, string Name);

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
    int ItemChecks { get; }
    int CombinedUpdates { get; }
    string Prepare();
    void Acquire();
    CombatObservation Observe();
    string Step(out string phase);
    bool Release();
    void Abandon();
}

internal sealed class CombatController(ICombatWorld world, string source, string threat) : CompanionTask
{
    internal const ulong Budget = 1800;
    private string phase = "acquire_target";
    private int? life;
    private bool acquired;
    internal override string Name => CombatProtocol.Operation;
    internal override string Phase => phase;
    internal override object Progress => new { source, threat, target = world.Target, weapon = world.Weapon,
        life, lastHit = world.LastHit, itemChecks = world.ItemChecks, combinedUpdates = world.CombinedUpdates };
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
        if (!target.SameSpawn) return Stop(frame, "failed", "combat_target_replaced");
        if (world.LastHit is { Defeated: true, LifeAfter: <= 0 } && target.Life <= 0)
            return Stop(frame, "completed", "combat_target_defeated");
        if (!target.Active || !target.Eligible || target.Life <= 0)
            return Stop(frame, "failed", "combat_target_invalidated");

        string failure = world.Step(out phase);
        if (failure == null) return Running();
        return Stop(frame, failure == "combat_native_unconfirmed" ? "unknown" : "failed", failure);
    }

    internal override Result Stop(Identity frame, string status, string reason)
    {
        bool released = world.Release();
        return new(status, reason, Progress, released, released && status == "completed", acquired);
    }

    internal override void Abandon() => world.Abandon();
}
