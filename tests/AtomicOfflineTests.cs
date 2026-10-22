using System;
using System.Diagnostics;
using System.Text.Json;
using CompanionBridge;

public static class AtomicOfflineTests
{
    static void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); }
    static ControlRequest Parse(int id, string op, string args = "{}", bool query = false, string extra = "") =>
        ControlState.Parse("{\"v\":1,\"id\":" + id + ",\"cmd\":\"" + (query ? "query_atomic" : "start_atomic") + "\",\"operation\":\"" + op + "\",\"arguments\":" + args + (query ? "" : ",\"expectedGeneration\":0") + extra + "}");
    static void Invalid(string op, string args, bool query = false)
    {
        bool rejected = false;
        try { Parse(1, op, args, query); } catch (Exception e) when (e is FormatException or InvalidOperationException) { rejected = true; }
        Check(rejected, "Expected rejection: " + op + args);
    }
    public static void Run()
    {
        foreach (var op in new[] { "mine_tile", "place_tile", "interact_tile" })
        {
            Parse(1, op, "{\"x\":1,\"y\":2}");
            Invalid(op, "{\"x\":-1,\"y\":2}"); Invalid(op, "{\"x\":1.5,\"y\":2}");
            Invalid(op, "{\"x\":true,\"y\":2}"); Invalid(op, "{\"x\":1,\"y\":2,\"extra\":3}");
        }
        Invalid("select_hotbar_slot", "{\"slot\":10}"); Invalid("select_hotbar_slot", "{\"slot\":true}");
        Invalid("attack_entity", "{\"entityRef\":\"\"}"); Invalid("navigate_to_x", "{\"targetX\":1e99}");
        Invalid("use_item", "{\"target\":{\"x\":1,\"y\":2,\"z\":3}}");
        Invalid("scan_entities", "{\"category\":\"bad\",\"radius\":100}", true);
        Invalid("scan_entities", "{\"category\":\"npc\",\"radius\":1601}", true);
        var controls = new ControlState(); int session = controls.BeginSession(); long now = Stopwatch.GetTimestamp();
        var state = new PlayerSnapshot(true, new(100, 100), new(0, 0), 100, 100);
        int id = 1;
        ControlReply Apply(ControlRequest request, long? time = null) {
            var pending = controls.Enqueue(session, request, time ?? now);
            controls.Update(true, time ?? now, state, worldSessionId: "atomic-test", queryAtomic: r => new { op = r.Operation });
            return pending.Result;
        }
        var start = Parse(id++, "mine_tile", "{\"x\":7,\"y\":7}") with { ExpectedGeneration = controls.Generation };
        var reply = Apply(start);
        Check(reply.Ok && controls.Input.Atomic.Operation == "mine_tile" && controls.Input.Direction == 0, "Atomic owns existing lease without walking");
        var action = controls.AtomicAction;
        Check(action.Status == "running" && action.EndEvidence == null && !action.Completed, "ACK never means completion");
        int generation = controls.Generation;
        Check(!Apply(Parse(id++, "use_item") with { ExpectedGeneration = generation }).Ok, "Atomic actions mutually exclusive");
        var query = Apply(Parse(id++, "inspect_inventory", query: true));
        Check(query.Ok && query.Data != null && controls.Generation == generation, "Queries use callback on update and never acquire control");
        controls.FinishAtomic(action.ActionId + 1, "completed", "bad", null);
        Check(controls.AtomicAction.Status == "running", "Stale completion cannot finish another action");
        controls.FinishAtomic(action.ActionId, "completed", "tile_removed", new { removed = true }, state);
        Check(!controls.IsMoving && controls.AtomicAction.Completed && controls.AtomicAction.EndEvidenceStatus == "pending", "Effect verified; release boundary still pending");
        Check(!Apply(Parse(id++, "use_item") with { ExpectedGeneration = controls.Generation }).Ok, "Cannot start before old release boundary");
        controls.ConfirmMovementInputReleased("wrong-world", 2, "PostUpdateRunSpeeds_before_bridge_input", true);
        Check(controls.AtomicAction.EndEvidence == null, "Cross-world receipt rejected");
        controls.ConfirmMovementInputReleased("atomic-test", 3, "PostUpdateRunSpeeds_before_bridge_input", true);
        Check(controls.AtomicAction.EndEvidence.GameUpdate == 3, "Actual hook boundary seals atomic receipt");
        Apply(Parse(id++, "use_item") with { ExpectedGeneration = controls.Generation });
        action = controls.AtomicAction;
        Apply(new(id++, "cancel_move", ActionId: action.ActionId));
        Check(controls.AtomicAction.Status == "cancelled" && !controls.IsMoving, "Cancellation releases atomic control");
        controls.ConfirmMovementInputReleased("atomic-test", 4, "PostUpdateRunSpeeds_before_bridge_input", true);
        Apply(Parse(id++, "navigate_to_x", "{\"targetX\":200}") with { ExpectedGeneration = controls.Generation });
        Check(controls.Input.Predictive && controls.Input.TargetX == 200, "Atomic navigation uses existing native controller");
        action = controls.AtomicAction;
        // Keep renewing the lease without extending the absolute action deadline.
        for (int half = 1; half <= 60; half++)
            Apply(new(id++, "renew_move", ActionId: action.ActionId), now + half * Stopwatch.Frequency / 2);
        Check(controls.AtomicAction.Status == "timeout" && !controls.IsMoving, "Absolute deadline yields timeout despite heartbeats");
        controls.ConfirmMovementInputReleased("atomic-test", 5, "PostUpdateRunSpeeds_before_bridge_input", true);
        now += 31 * Stopwatch.Frequency;
        Apply(Parse(id++, "use_item") with { ExpectedGeneration = controls.Generation });
        controls.Update(true, now + Stopwatch.Frequency, state);
        Check(controls.AtomicAction.Status == "cancelled" && controls.AtomicAction.Reason == "lease_expired", "Lease expiry cancels atomic input");
        controls.EndSession(session);
        Check(!controls.IsMoving, "Disconnect clears control");
        Console.WriteLine("PASS: atomic protocol bounds, query callback, shared lease/generation, mutual exclusion, effect/release evidence, stale completion, cancel and absolute timeout");
    }
}
