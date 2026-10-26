using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;

namespace CompanionBridge.ServerIpc;

// A bounded server execution adapter, not a scheduler. No Terraria access here.
internal sealed class AiAtomicActions
{
    internal static readonly string[] Capabilities = { "server_ai_atomic_v1", "atomic_operations_v1", "hotbar_keys_v1" };
    internal sealed record Reply(object Data = null, string Error = null);
    private sealed class Entry
    {
        internal Guid Owner;
        internal string Id, World, ActorId, Phase = "accepted", Status = "running", Reason = "accepted";
        internal object Actor;
        internal long Generation, Deadline;
        internal int Slot, Key, Target, Before;
        internal bool Cancel, Applied, Attempted;
        internal CancellationToken Disconnected;
        internal object End;
    }
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private Entry active;
    private long sequence;
    internal void Clear() { entries.Clear(); active = null; }
    internal void Forget(Guid connection) { if (active?.Owner == connection) active.Cancel = true; }
    private static bool Matches(Entry e, AiReadOnlyProtocol.Identity current) => current != null
        && ReferenceEquals(e.Actor, current.Actor) && e.World == current.World && e.Generation == current.Generation && e.Slot == current.Slot;
    private static object Snapshot(Entry e) => new {
        actionId = e.Id, operation = "select_hotbar_slot", session = e.Owner.ToString("N"), generation = e.Generation,
        worldSessionId = e.World, actorId = e.ActorId, actorSlot = e.Slot, durationMs = 2000,
        status = e.Status, executionState = e.Phase, reason = e.Reason,
        evidence = new { requestedHotbarKey = e.Key, targetSlot = e.Target, beforeSlot = e.Before,
            mutationAttempted = e.Attempted, selectionApplied = e.Applied },
        endEvidenceStatus = e.Phase == "terminal" ? e.End == null ? "unknown" : "known" : "not_ended", endEvidence = e.End
    };
    internal Reply Handle(Guid owner, string op, JsonElement root, AiReadOnlyProtocol.Identity current,
        AiReadOnlyProtocol.Binding binding, CancellationToken disconnected)
    {
        string id = root.GetProperty("actionId").GetString();
        if (!Guid.TryParseExact(id, "N", out _)) return new(Error: "invalid_action_id");
        if (op != "start")
        {
            if (!entries.TryGetValue(id, out var known) || known.Owner != owner) return new(Error: "unknown_action");
            if (op == "cancel" && known.Status == "running") End(known, "cancelled", "cancelled_no_inputs_acquired", current, true);
            return new(new { action = Snapshot(known) });
        }
        if (entries.ContainsKey(id)) return new(Error: "duplicate_action");
        if (entries.Count >= 1024) return new(Error: "action_registry_full");
        if (active != null) return new(Error: "action_busy");
        if (!current.CanAct) return new(Error: "actor_not_alive");
        if (root.GetProperty("operation").GetString() != "select_hotbar_slot") return new(Error: "unsupported_operation");
        var arguments = root.GetProperty("arguments");
        int count = 0;
        foreach (var field in arguments.EnumerateObject()) { if (field.Name != "slot" || ++count > 1) return new(Error: "invalid_arguments"); }
        int key = arguments.GetProperty("slot").GetInt32();
        if (key < 0 || key > 9) return new(Error: "invalid_arguments");
        long remaining = root.GetProperty("expiresAtUnixMs").GetInt64() - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (remaining <= 0) return new(Error: "request_expired");
        if (remaining > 2000) return new(Error: "invalid_deadline");
        active = new Entry { Owner = owner, Id = id, World = current.World, Actor = current.Actor, ActorId = binding.actor,
            Generation = current.Generation, Slot = current.Slot, Before = current.SelectedSlot,
            Key = key, Target = HotbarSlots.ToInventorySlot(key), Deadline = Environment.TickCount64 + remaining, Disconnected = disconnected };
        entries.Add(id, active);
        return new(new { action = Snapshot(active) });
    }
    private void End(Entry e, string status, string reason, AiReadOnlyProtocol.Identity current, bool known)
    {
        e.Status = status; e.Reason = reason; e.Phase = "terminal";
        if (known) e.End = new { phase = "ServerAI_game_thread_no_input", gameUpdate = current.GameUpdate,
            worldSessionId = e.World, actorId = e.ActorId, actorGeneration = e.Generation, actorSlot = e.Slot,
            inputsAcquired = false, selectedSlot = current.SelectedSlot, selectedHotbarKey = HotbarSlots.KeyForInventorySlot(current.SelectedSlot),
            selectionVerified = e.Applied && current.SelectedSlot == e.Target, observationSequence = sequence };
        if (ReferenceEquals(active, e)) active = null;
    }
    internal void Tick(Func<AiReadOnlyProtocol.Identity> capture, Action<AiReadOnlyProtocol.Identity, int> select)
    {
        sequence++;
        var e = active; if (e == null) return;
        AiReadOnlyProtocol.Identity current;
        try { current = capture(); } catch { current = null; }
        if (!Matches(e, current)) { End(e, "unknown", "actor_binding_changed", current, false); return; }
        if (!current.CanAct) { End(e, "cancelled", "actor_not_alive_no_inputs_acquired", current, true); return; }
        if (e.Cancel || e.Disconnected.IsCancellationRequested) { End(e, "cancelled", "cancelled_no_inputs_acquired", current, true); return; }
        if (Environment.TickCount64 >= e.Deadline) { End(e, "timeout", "execution_deadline", current, true); return; }
        if (e.Phase == "accepted") { e.Phase = "running"; e.Reason = "awaiting_native_selection"; return; }
        if (!e.Applied)
        {
            // One indivisible native selection; never retry an ambiguous mutation/send failure.
            e.Attempted = true;
            try { select(current, e.Target); e.Applied = true; e.Reason = "awaiting_selection_evidence"; }
            catch { End(e, "unknown", "native_selection_unconfirmed", current, false); }
            return;
        }
        End(e, current.SelectedSlot == e.Target ? "completed" : "unknown",
            current.SelectedSlot == e.Target ? "native_selection_verified_next_pump" : "selection_changed_before_confirmation", current, true);
    }
}
