using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Linq;

namespace CompanionBridge.ServerIpc;

// A bounded server execution adapter, not a scheduler. No Terraria access here.
internal sealed class AiAtomicActions
{
    private static readonly string[] basicCapabilities = { "server_ai_atomic_v1", "atomic_operations_v1", "hotbar_keys_v1" };
    internal string[] Capabilities => executor == null ? basicCapabilities : basicCapabilities.Concat(new[]{"server_ai_actions_v1","server_ai_general_operations_v1","server_ai_direct_input_v1","landing_region_navigation_v1","server_ai_place_tile_v1","server_ai_place_object_workbench_v1","server_ai_crafting_v1","server_ai_inventory_v1","server_ai_tasks_v1","server_ai_program_tasks_v1"}).ToArray();
    internal sealed record NativeResult(string Status, string Reason, object Evidence, bool InputsReleased, bool EffectVerified = false, bool InputsAcquired = true, string ReasonDetail = null);
    internal interface IExecutor
    {
        string Validate(string operation, JsonElement arguments);
        object Start(AiReadOnlyProtocol.Identity identity, string operation, JsonElement arguments, string actionId);
        NativeResult Poll(AiReadOnlyProtocol.Identity identity, object handle);
        NativeResult Cancel(AiReadOnlyProtocol.Identity identity, object handle);
        NativeResult CancelWithReason(AiReadOnlyProtocol.Identity identity,object handle,string status,string reason)=>Cancel(identity,handle);
        NativeResult BindingLost(object handle)=>new("unknown","actor_binding_changed",null,false);
        void Shutdown();
    }
    private readonly IExecutor executor;
    private readonly Func<long> clock;
    internal AiAtomicActions(IExecutor executor = null, Func<long> clock = null) { this.executor = executor; this.clock = clock ?? (() => Environment.TickCount64); }
    internal sealed record Reply(object Data = null, string Error = null, string DispatchState = null);
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
        internal string Operation;
        internal JsonElement Arguments;
        internal int Duration;
        internal object Handle;
        internal NativeResult Result;
        internal string ReasonDetail;
        internal string StopStatus, StopReason;
    }
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    // Retain up to 1024 identities globally, in terminal completion order.
    // Only terminal entries are eligible for eviction when admitting a fresh GUID.
    // Retained IDs reject duplicates; evicted IDs query as unknown_action.
    private const int RegistryCapacity = 1024;
    private readonly Queue<string> terminalEntries = new();
    private Entry active;
    private long sequence;
    internal void Clear() { executor?.Shutdown(); entries.Clear(); terminalEntries.Clear(); active = null; }
    internal void Forget(Guid connection) { if (active?.Owner == connection) active.Cancel = true; }
    private static bool Matches(Entry e, AiReadOnlyProtocol.Identity current) => current != null
        && ReferenceEquals(e.Actor, current.Actor) && e.World == current.World && e.Generation == current.Generation && e.Slot == current.Slot;
    private static object TaskState(Entry e)
    {
        if(e.Operation!="mine_target")return null;
        if(e.Result?.Evidence!=null)
        {
            var evidence=JsonSerializer.SerializeToElement(e.Result.Evidence);
            if(evidence.ValueKind==JsonValueKind.Object&&evidence.TryGetProperty("task",out var task))return task;
        }
        // Accepted requests and native admission refusals do not yet have a task slot.
        return new{taskId=e.Id,taskName="mine_target",phase=e.Phase,status=e.Status,currentChild=(object)null,
            progress=new{completedChildren=0},lastResult=e.Result==null?null:(object)new{status=e.Result.Status,reason=e.Result.Reason,evidence=e.Result.Evidence},reason=e.Reason};
    }
    private static object Snapshot(Entry e) => new {
        actionId = e.Id, operation = e.Operation, session = e.Owner.ToString("N"), generation = e.Generation,
        worldSessionId = e.World, actorId = e.ActorId, actorSlot = e.Slot, durationMs = e.Duration,
        targetTile = e.Operation is "place_tile" or "place_object" ? (JsonElement?)e.Arguments : null,
        targetInventory = e.Operation is "transfer_inventory_item" or "swap_inventory_slots" or "hold_inventory_item" ? (JsonElement?)e.Arguments : null,
        targetTask = e.Operation=="mine_target" ? (JsonElement?)e.Arguments : null,
        targetRecipe = e.Operation == "craft_recipe" ? (JsonElement?)e.Arguments : null,
        requestedInputs = e.Operation == "move_input" ? (JsonElement?)e.Arguments : null,
        targetRegion = e.Operation == "navigate_to_landing_region" ? (JsonElement?)e.Arguments : null,
        task = TaskState(e),
        status = e.Status, executionState = e.Phase, reason = e.Reason, reasonDetail = e.ReasonDetail,
        stopRequest = e.StopStatus == null ? null : new { status = e.StopStatus, reason = e.StopReason },
        evidence = e.Operation == "select_hotbar_slot" ? (object)new { requestedHotbarKey = e.Key, targetSlot = e.Target, beforeSlot = e.Before,
            mutationAttempted = e.Attempted, selectionApplied = e.Applied } : e.Result?.Evidence,
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
            if (op == "cancel" && known.Status == "running") Stop(known, "cancelled", "explicit_cancel", current);
            return new(new { action = Snapshot(known) });
        }
        // A retained ID may already have effects. Reject replay without claiming no admission.
        if (entries.ContainsKey(id)) return new(Error: "duplicate_action", DispatchState: "unknown");
        if (active != null) return new(Error: "action_busy");
        if (!current.CanAct) return new(Error: "actor_not_alive");
        string operation = root.GetProperty("operation").GetString();
        bool selecting = operation == "select_hotbar_slot";
        if (!selecting && (executor == null || operation is not ("move_input" or "navigate_to_x" or "navigate_to_landing_region" or "use_item" or "mine_tile" or "place_tile" or "place_object" or "craft_recipe" or "transfer_inventory_item" or "swap_inventory_slots" or "hold_inventory_item" or "mine_target"))) return new(Error: "unsupported_operation");
        var arguments = root.GetProperty("arguments");
        int key = 0;
        if (selecting)
        {
            try { AtomicParameters.Validate(operation, arguments, false); }
            catch (AtomicParameterException) { return new(Error: "invalid_arguments"); }
            key = arguments.GetProperty("slot").GetInt32();
            if (key < 0 || key > 9) return new(Error: "invalid_arguments");
        }
        else
        {
            string invalid = executor.Validate(operation, arguments);
            if (invalid != null) return new(Error: invalid);
        }
        // Match the native planner's float coordinates before publishing the
        // immutable receipt. Start, polling and effect evidence use one region.
        if (operation == "navigate_to_landing_region") arguments = JsonSerializer.SerializeToElement(new {
            minPlayerLeftX = arguments.GetProperty("minPlayerLeftX").GetSingle(),
            maxPlayerLeftX = arguments.GetProperty("maxPlayerLeftX").GetSingle(),
            surfaceY = arguments.GetProperty("surfaceY").GetSingle()
        });
        long remaining = root.GetProperty("expiresAtUnixMs").GetInt64() - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (remaining <= 0) return new(Error: "request_expired");
        if (remaining > 2000) return new(Error: "invalid_deadline");
        int duration = operation switch { "mine_target"=>55000, "move_input" or "navigate_to_x" or "navigate_to_landing_region" => 30000, "use_item" => 3000, "mine_tile" or "place_tile" or "place_object" => 10000, _ => 2000 };
        while (entries.Count >= RegistryCapacity && terminalEntries.TryDequeue(out var oldest))
            if (entries.TryGetValue(oldest, out var terminal) && terminal.Status != "running") entries.Remove(oldest);
        if (entries.Count >= RegistryCapacity) return new(Error: "action_registry_full");
        active = new Entry { Owner = owner, Id = id, World = current.World, Actor = current.Actor, ActorId = binding.actor,
            Generation = current.Generation, Slot = current.Slot, Before = current.SelectedSlot,
            Operation = operation, Arguments = arguments.Clone(), Duration = duration,
            Key = key, Target = HotbarSlots.ToInventorySlot(key), Deadline = clock() + (selecting ? remaining : duration), Disconnected = disconnected };
        entries.Add(id, active);
        return new(new { action = Snapshot(active) });
    }
    private void End(Entry e, string status, string reason, AiReadOnlyProtocol.Identity current, bool known)
    {
        e.Status = status; e.Reason = reason; e.Phase = "terminal";
        e.ReasonDetail = e.Result?.ReasonDetail;
        if(status=="unknown"&&e.Result!=null&&e.Result.Reason!=reason)
            e.ReasonDetail=string.IsNullOrWhiteSpace(e.ReasonDetail)?e.Result.Reason:e.Result.Reason+": "+e.ReasonDetail;
        if (known) e.End = new { phase = "ServerAI_game_thread_no_input", gameUpdate = current.GameUpdate,
            worldSessionId = e.World, actorId = e.ActorId, actorGeneration = e.Generation, actorSlot = e.Slot,
            inputsAcquired = false, selectedSlot = current.SelectedSlot, selectedHotbarKey = HotbarSlots.KeyForInventorySlot(current.SelectedSlot),
            selectionVerified = e.Applied && current.SelectedSlot == e.Target, observationSequence = sequence };
        if (known && e.Operation != "select_hotbar_slot") e.End = new { phase = "ServerAI_game_thread_inputs_released", gameUpdate = current.GameUpdate,
            worldSessionId = e.World, actorId = e.ActorId, actorGeneration = e.Generation, actorSlot = e.Slot,
            inputsAcquired = e.Handle != null && e.Result?.InputsAcquired != false, inputsReleased = e.Handle == null || e.Result?.InputsReleased == true,
            effectVerified = e.Result?.EffectVerified == true, operation = e.Operation, observationSequence = sequence };
        if (ReferenceEquals(active, e)) active = null;
        terminalEntries.Enqueue(e.Id);
    }
    private void Stop(Entry e, string status, string reason, AiReadOnlyProtocol.Identity current)
    {
        e.StopStatus = status; e.StopReason = reason;
        if (e.Handle != null)
        {
            try { e.Result = e.Operation=="mine_target"?executor.CancelWithReason(current,e.Handle,status,reason):executor.Cancel(current,e.Handle); }
            catch { End(e, "unknown", "native_cleanup_unconfirmed", current, false); return; }
            if (!e.Result.InputsReleased) { End(e, "unknown", "native_inputs_not_released", current, false); return; }
            // Confirmed release does not resolve uncertain native observation/effect state.
            if(e.Result.Status=="unknown")
            {End(e,"unknown",e.Result.Reason,current,true);return;}
            if (e.Result.Status is not ("completed" or "failed" or "timeout" or "cancelled"))
            { End(e, "unknown", "native_cancel_unconfirmed", current, true); return; }
            if (e.Result.Status == "completed" && !e.Result.EffectVerified)
            { End(e, "unknown", "native_effect_unconfirmed", current, true); return; }
            // The stop trigger is metadata. A reliable native terminal wins all races.
            status = e.Result.Status; reason = e.Result.Reason;
        }
        End(e, status, reason, current, true);
    }
    internal void Tick(Func<AiReadOnlyProtocol.Identity> capture, Action<AiReadOnlyProtocol.Identity, int> select)
    {
        sequence++;
        var e = active; if (e == null) return;
        AiReadOnlyProtocol.Identity current;
        try { current = capture(); } catch { current = null; }
        if (!Matches(e, current))
        {
            if(e.Operation=="mine_target"&&e.Handle!=null){try{e.Result=executor.BindingLost(e.Handle);}catch{}}
            End(e,"unknown","actor_binding_changed",current,false);return;
        }
        if (!current.CanAct) { Stop(e, "cancelled", "actor_not_alive", current); return; }
        if (e.Cancel || e.Disconnected.IsCancellationRequested) { Stop(e, "cancelled", "connection_cancelled", current); return; }
        if (clock() >= e.Deadline) { Stop(e, "timeout", "execution_deadline", current); return; }
        if (e.Phase == "accepted") { e.Phase = "running"; e.Reason = "awaiting_native_execution"; return; }
        if (e.Operation != "select_hotbar_slot")
        {
            try
            {
                if (e.Handle == null) { e.Attempted = true; e.Handle = executor.Start(current, e.Operation, e.Arguments, e.Id); return; }
                e.Result = executor.Poll(current, e.Handle);
                if (e.Result.Status == "running") return;
                bool verified = e.Result.Status != "completed" || e.Result.EffectVerified;
                string reason = !e.Result.InputsReleased ? "native_inputs_not_released"
                    : !verified ? "native_effect_unconfirmed" : e.Result.Reason;
                End(e, verified && e.Result.InputsReleased ? e.Result.Status : "unknown", reason, current, e.Result.InputsReleased);
            }
            catch
            {
                if (e.Handle == null) End(e, "unknown", "native_start_unconfirmed", current, false);
                else Stop(e, "unknown", "native_action_unconfirmed", current);
            }
            return;
        }
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
