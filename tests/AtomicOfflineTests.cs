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
        foreach (var op in new[] { "mine_tile", "place_tile", "interact_tile", "mine_wall", "place_wall", "hammer_tile" })
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
        Parse(1,"scan_tiles","{\"minX\":0,\"minY\":0,\"maxX\":63,\"maxY\":63,\"tileTypes\":[0,2],\"maxResults\":256}",true);
        foreach(string args in new[] {
            "{\"minX\":0,\"minY\":0,\"maxX\":64,\"maxY\":63,\"tileTypes\":[2]}",
            "{\"minX\":2,\"minY\":0,\"maxX\":0,\"maxY\":0,\"tileTypes\":[2]}",
            "{\"minX\":0,\"minY\":0,\"maxX\":1,\"maxY\":1,\"tileTypes\":[]}",
            "{\"minX\":0,\"minY\":0,\"maxX\":1,\"maxY\":1,\"tileTypes\":[2,2]}",
            "{\"minX\":0,\"minY\":0,\"maxX\":1,\"maxY\":1,\"tileTypes\":[true]}",
            "{\"minX\":0,\"minY\":0,\"maxX\":1,\"maxY\":1,\"tileTypes\":[2],\"maxResults\":257}",
            "{\"minX\":0,\"minY\":0,\"maxX\":1,\"maxY\":1,\"tileTypes\":[2],\"extra\":1}" }) Invalid("scan_tiles",args,true);
        foreach(string args in new[] {"{\"mode\":\"all\"}","{\"mode\":true}","{\"mode\":\"item\"}","{\"item\":\"Stone\"}","{\"mode\":\"full\",\"item\":\"Stone\"}","{\"mode\":\"item\",\"item\":\"\"}","{\"extra\":1}"}) Invalid("inspect_inventory",args,true);
        foreach(string mode in new[]{"selected","hotbar","full"}) Parse(1,"inspect_inventory","{\"mode\":\""+mode+"\"}",true);
        Parse(1,"inspect_inventory","{\"mode\":\"item\",\"item\":\"Terraria/StoneBlock\"}",true);
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
        var brokenQuery=controls.Enqueue(session,Parse(id++,"scan_tiles","{\"minX\":0,\"minY\":0,\"maxX\":0,\"maxY\":0,\"tileTypes\":[2]}",true),now);
        controls.Update(true,now,state,worldSessionId:"atomic-test",queryAtomic:r=>throw new Exception("read failed"));
        Check(!brokenQuery.Result.Ok && brokenQuery.Result.Status=="query_failed","Read failure distinct from empty scan");
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
        foreach(string op in new[]{"mine_wall","place_wall","hammer_tile","interact_tile"})
        {
            controls.Reset(); session=controls.BeginSession();
            Apply(Parse(id++,op,"{\"x\":5,\"y\":5}") with { ExpectedGeneration=controls.Generation });
            action=controls.AtomicAction;
            Check(action.DurationMs==10000 && action.Status=="running","Extension ACK remains running with bounded deadline");
            for(int half=1;half<=20;half++) Apply(new(id++,"renew_move",ActionId:action.ActionId),now+half*Stopwatch.Frequency/2);
            Check(controls.AtomicAction.Status=="timeout" && !controls.IsMoving,"Extension absolute timeout survives renewals");
            controls.ConfirmMovementInputReleased("atomic-test",6,"PostUpdateRunSpeeds_before_bridge_input",true);
            Check(controls.AtomicAction.EndEvidenceStatus=="known","Extension timeout uses normal release evidence");
            now+=11*Stopwatch.Frequency;
            Apply(Parse(id++,op,"{\"x\":5,\"y\":5}") with { ExpectedGeneration=controls.Generation });
            action=controls.AtomicAction; Apply(new(id++,"cancel_move",ActionId:action.ActionId));
            Check(controls.AtomicAction.Status=="cancelled" && !controls.IsMoving,"Extension cancellation uses normal action identity");
            controls.EndSession(session);
        }
        Console.WriteLine("PASS: atomic protocol bounds, query callback, shared lease/generation, mutual exclusion, effect/release evidence, stale completion, cancel and absolute timeout");
    }
}
