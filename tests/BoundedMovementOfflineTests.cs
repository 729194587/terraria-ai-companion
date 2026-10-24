using System;
using System.Diagnostics;
using System.Text.Json;
using CompanionBridge;

public static class BoundedMovementOfflineTests
{
    private static long Ms(int ms) => Stopwatch.Frequency * ms / 1000;
    private static PlayerSnapshot Player(float x = 100, int life = 100) => new(true, new(x, 20), new(0, 0), life, 100);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static ControlReply Apply(ControlState state, int session, ControlRequest request, int ms,
        PlayerSnapshot snapshot = null, bool alive = true)
    {
        var pending = state.Enqueue(session, request, Ms(ms));
        state.Update(alive, Ms(ms), snapshot ?? Player());
        return pending.Result;
    }
    public static void Run()
    {
        EndEvidenceRegression();
        var parsed = ControlState.Parse("{\"v\":1,\"id\":1,\"cmd\":\"move_for\",\"direction\":-1,\"durationMs\":200,\"expectedGeneration\":0}");
        Check(parsed.Direction == -1 && parsed.DurationMs == 200, "Bounded typed movement command");
        foreach (string fields in new[] { "\"direction\":0,\"durationMs\":200,\"expectedGeneration\":0",
            "\"direction\":1,\"durationMs\":49,\"expectedGeneration\":0", "\"direction\":1,\"durationMs\":2001,\"expectedGeneration\":0",
            "\"direction\":1,\"durationMs\":200", "\"direction\":1,\"durationMs\":true,\"expectedGeneration\":0" })
        {
            bool rejected = false;
            try { ControlState.Parse("{\"v\":1,\"id\":1,\"cmd\":\"move_for\"," + fields + "}"); }
            catch (Exception e) when (e is FormatException or InvalidOperationException) { rejected = true; }
            Check(rejected, "Invalid direction/duration/guard rejected before input");
        }
        foreach (string cmd in new[] { "renew_move", "cancel_move" })
        {
            bool rejected = false;
            try { ControlState.Parse("{\"v\":1,\"id\":1,\"cmd\":\"" + cmd + "\",\"actionId\":0}"); }
            catch (FormatException) { rejected = true; }
            Check(rejected, "Action identity required");
        }

        var state = new ControlState(); int session = state.BeginSession(); int id = 0;
        state.ObserveChat("current");
        var pending = state.Enqueue(session, new(++id, "move_for", -1, ExpectedGeneration: 0,
            ChatId: "current", DurationMs: 300), 0);
        Check(!pending.IsCompleted && !state.IsMoving, "Socket enqueue cannot execute movement");
        state.Update(true, 0, Player());
        var start = pending.Result; int actionId = start.Movement.Action.ActionId;
        Check(start.Ok && state.Input.Direction == -1 && !state.Input.Jump && state.Input.OverrideJump
            && !state.Input.Predictive, "Left input reuses executor without jump/navigation");
        var running = Apply(state, session, new(++id, "get_movement"), 100, Player(95));
        Check(running.Movement.Action.Status == "running" && state.IsMoving, "Read does not interrupt bounded action");
        Check(Apply(state, session, new(++id, "move_for", 1, ExpectedGeneration: state.Generation, DurationMs: 500), 110).Status == "control_busy"
            && state.Input.Direction == -1, "Second action cannot reset running action duration");
        state.Update(true, Ms(300), Player(80));
        var complete = Apply(state, session, new(++id, "get_movement"), 301, Player(80));
        Check(!state.IsMoving && complete.Movement.Action.Completed && !complete.Movement.Action.Cancelled
            && complete.Movement.Action.Before.Position.X == 100 && complete.Movement.Action.After.Position.X == 80,
            "Game deadline releases input and returns before/after snapshots without Python stop");
        Check(complete.Movement.Action.ElapsedMilliseconds == 300, "Elapsed movement duration reported");
        using (var json = JsonDocument.Parse(TcpControlServer.SerializeFrame(complete)))
        {
            var serialized = json.RootElement.GetProperty("movement").GetProperty("action");
            Check(serialized.GetProperty("completed").GetBoolean()
                && serialized.GetProperty("after").GetProperty("position").GetProperty("x").GetSingle() == 80,
                "Movement result uses the existing camelCase JSON framing with terminal flags/positions");
        }

        var right = Apply(state, session, new(++id, "move_for", 1, ExpectedGeneration: state.Generation, DurationMs: 2000), 400);
        int rightId = right.Movement.Action.ActionId;
        Check(state.Input.Direction == 1 && rightId > actionId, "Right input and unique action IDs");
        foreach (int ms in new[] { 600, 1000, 1400, 1800, 2200 })
            Check(Apply(state, session, new(++id, "renew_move", ActionId: rightId), ms).Ok, "Lease renewal accepted only for running action");
        state.Update(true, Ms(2400), Player(130));
        Check(Apply(state, session, new(++id, "get_movement"), 2401).Movement.Action.Completed && !state.IsMoving,
            "Heartbeats cannot extend absolute 2-second deadline");

        var lease = Apply(state, session, new(++id, "move_for", 1, ExpectedGeneration: state.Generation, DurationMs: 1500), 2500);
        int leaseId = lease.Movement.Action.ActionId;
        state.Update(true, Ms(3250), Player(140));
        var expired = Apply(state, session, new(++id, "get_movement"), 3251);
        Check(expired.Movement.Action.Cancelled && expired.Movement.Action.Reason == "lease_expired" && !state.IsMoving,
            "750ms lease cancels stalled Python");
        Check(!Apply(state, session, new(++id, "renew_move", ActionId: leaseId), 3300).Ok && !state.IsMoving,
            "Late heartbeat cannot restart expired action");

        var deathStart = Apply(state, session, new(++id, "move_for", 1, ExpectedGeneration: state.Generation, DurationMs: 1500), 3400);
        int deathId = deathStart.Movement.Action.ActionId, beforeDeathGeneration = state.Generation;
        state.Update(false, Ms(3450), Player(141, 0));
        var dead = Apply(state, session, new(++id, "get_movement"), 3451, Player(141, 0), false);
        Check(dead.Movement.Action.Cancelled && dead.Movement.Action.Reason == "control_unavailable"
            && dead.Movement.Action.After.Life == 0 && state.SessionId == session && state.Generation > beforeDeathGeneration,
            "Death cancels action while retaining observation/chat TCP ownership");
        state.Update(true, Ms(3500), Player());
        Check(!Apply(state, session, new(++id, "renew_move", ActionId: deathId), 3501).Ok && !state.IsMoving,
            "Revival never resumes old action");

        var manualStart = Apply(state, session, new(++id, "move_for", 1, ExpectedGeneration: state.Generation, DurationMs: 1500), 3600);
        int manualId = manualStart.Movement.Action.ActionId;
        state.ToggleManual(Ms(3650)); state.ToggleManual(Ms(3651));
        var manual = Apply(state, session, new(++id, "cancel_move", ActionId: manualId), 3652);
        Check(manual.Movement.Action.Cancelled && manual.Movement.Action.Reason == "manual_override"
            && state.Mode == ControlMode.Manual, "Targeted cancellation cannot stop manual takeover");
        Check(!Apply(state, session, new(++id, "move_for", 1, ExpectedGeneration: state.Generation, DurationMs: 100), 3653).Ok,
            "Movement cannot acquire manual control");
        state.ToggleManual(Ms(3660));

        var stopStart = Apply(state, session, new(++id, "move_for", 1, ExpectedGeneration: state.Generation, DurationMs: 1000), 3700);
        int stopId = stopStart.Movement.Action.ActionId;
        Check(!Apply(state, session, new(++id, "cancel_move", ActionId: stopId - 1), 3710).Ok && state.IsMoving,
            "Old cancellation cannot stop newer action");
        Check(!Apply(state, session, new(++id, "input", -1), 3711).Ok && state.Input.Direction == 1,
            "Legacy input cannot turn bounded movement into indefinite input");
        var stopped = Apply(state, session, new(++id, "cancel_move", ActionId: stopId), 3720, Player(142));
        Check(stopped.Movement.Action.Cancelled && stopped.Movement.Action.Reason == "stopped" && !state.IsMoving,
            "Explicit stop releases only owned action");
        int stoppedGeneration = state.Generation;
        Apply(state, session, new(++id, "cancel_move", ActionId: stopId), 3721);
        Check(state.Generation == stoppedGeneration, "Repeated stop idempotent");

        state.ObserveChat("newer");
        Check(Apply(state, session, new(++id, "move_for", 1, ExpectedGeneration: state.Generation,
            ChatId: "current", DurationMs: 100), 3800).Status == "superseded", "Late superseded Agent start cannot move");
        var late = state.Enqueue(session, new(++id, "move_for", 1, ExpectedGeneration: state.Generation, DurationMs: 100), Ms(3800));
        state.Update(true, Ms(4550), Player());
        Check(late.Result.Status == "expired" && !state.IsMoving, "Stale queued action cannot start");
        Apply(state, session, new(++id, "move_for", 1, ExpectedGeneration: state.Generation, DurationMs: 100), 4600);
        state.EndSession(session);
        Check(!state.IsMoving, "Disconnect cancels movement");
        int next = state.BeginSession();
        Check(Apply(state, next, new(1, "get_movement"), 4601).Movement.Action == null, "New session cannot see/use old action");
        // Death or takeover can happen while Python awaits the next model decision between actions.
        var between = new ControlState(); int betweenSession = between.BeginSession();
        between.Update(true, 0, Player()); int observedGeneration = between.Generation;
        between.RevokeControl(Player(life: 0), Ms(10)); between.Update(true, Ms(20), Player());
        Check(!Apply(between, betweenSession, new(1, "move_for", 1, ExpectedGeneration: observedGeneration,
            DurationMs: 100), 21).Ok && !between.IsMoving,
            "Pre-death observation cannot authorize a new action after revival between model turns");
        observedGeneration = between.Generation;
        between.ToggleManual(Ms(30)); between.ToggleManual(Ms(31));
        Check(!Apply(between, betweenSession, new(2, "move_for", 1, ExpectedGeneration: observedGeneration,
            DurationMs: 100), 32).Ok && !between.IsMoving,
            "Takeover and release between decisions invalidate the old observation generation");
        Console.WriteLine("PASS: bounded left/right input, absolute duration, lease, cancellation/ownership, death/respawn, manual and stale-start safety");
    }

    private static void EndEvidenceRegression()
    {
        const string phase = "PostUpdateRunSpeeds_before_bridge_input";
        foreach (string termination in new[] { "duration", "cancel", "death", "manual", "lease" })
        {
            var state = new ControlState(); int session = state.BeginSession();
            var start = state.Enqueue(session, new(1, "move_for", 1, ExpectedGeneration: 0, DurationMs: 100), 0);
            state.Update(true, 0, Player(), worldSessionId: "world");
            int actionId = start.Result.Movement.Action.ActionId;
            state.ConfirmMovementInputReleased("world", 50, phase, true);
            Check(Apply(state, session, new(2, "get_movement"), 1).Movement.Action.EndEvidence == null,
                "A running action cannot acquire release evidence");
            switch (termination)
            {
                case "duration": state.Update(true, Ms(100), Player(110)); break;
                case "cancel": Apply(state, session, new(3, "cancel_move", ActionId: actionId), 10); break;
                case "death": state.RevokeControl(Player(life: 0), Ms(10)); break;
                case "manual": state.ToggleManual(Ms(10)); break;
                case "lease": state.Update(true, Ms(750), Player(110)); break;
            }
            var terminal = Apply(state, session, new(4, "get_movement"), 751,
                Player(life: termination == "death" ? 0 : 100), termination != "death");
            Check(terminal.Movement.Action.Status != "running" && terminal.Movement.Action.EndEvidence == null
                && terminal.Movement.Action.EndEvidenceStatus == "pending",
                termination + ": terminal request/deadline/revoke is not a player input boundary");
            state.ConfirmMovementInputReleased("other-world", 77, phase, true);
            state.ConfirmMovementInputReleased("world", 77, "request_received", true);
            Check(Apply(state, session, new(5, "get_movement"), 752).Movement.Action.EndEvidence == null,
                "Wrong world/request-time stamps rejected");
            state.ConfirmMovementInputReleased("world", 79, phase, false);
            Check(Apply(state, session, new(51, "get_movement"), 752).Movement.Action.EndEvidence == null,
                "Lingering/human-held controls or paused player without release proof cannot acquire E");
            string boundary = termination == "death" ? "UpdateDead_after_revoke" : phase;
            state.ConfirmMovementInputReleased("world", 80, boundary, true);
            var evidenced = Apply(state, session, new(52, "get_movement"), 753);
            Check(evidenced.Movement.Action.EndEvidence == new MovementEndEvidence("world", 80, boundary)
                && evidenced.Movement.Action.EndEvidenceStatus == "known",
                termination + ": actual player boundary supplies E, independent of request wall time");
            state.ConfirmMovementInputReleased("world", 999, phase, true);
            var repeated = Apply(state, session, new(53, "cancel_move", ActionId: actionId), 754);
            Check(repeated.Movement.Action.EndEvidence.GameUpdate == 80,
                "Repeated stops and later player hooks cannot rewrite E");
            using var json = JsonDocument.Parse(TcpControlServer.SerializeFrame(evidenced));
            var wire = json.RootElement.GetProperty("movement").GetProperty("action");
            Check(wire.GetProperty("endEvidence").GetProperty("gameUpdate").GetUInt64() == 80
                && wire.GetProperty("endEvidence").GetProperty("worldSessionId").GetString() == "world"
                && wire.GetProperty("endEvidenceStatus").GetString() == "known",
                "Additive camelCase release evidence survives existing wire serializer");
        }
        var legacy = new ControlState(); int legacySession = legacy.BeginSession();
        Apply(legacy, legacySession, new(1, "move_for", 1, ExpectedGeneration: 0, DurationMs: 100), 0);
        legacy.Update(true, Ms(100), Player());
        legacy.ConfirmMovementInputReleased("world", 80, phase, true);
        Check(Apply(legacy, legacySession, new(2, "get_movement"), 101).Movement.Action.EndEvidenceStatus == "unknown",
            "Missing start-world context cannot gain fabricated release evidence");
        var reset = new ControlState(); int resetSession = reset.BeginSession();
        var queued = reset.Enqueue(resetSession, new(1, "move_for", 1, ExpectedGeneration: 0, DurationMs: 100), 0);
        reset.Update(true, 0, Player(), worldSessionId: "world");
        reset.EndSession(resetSession); reset.Reset();
        reset.ConfirmMovementInputReleased("world", 80, phase, true);
        int next = reset.BeginSession();
        Check(Apply(reset, next, new(1, "get_movement"), 1).Movement.Action == null,
            "Production manual/disconnect/reset teardown never fabricates or leaks old release receipts");
        Console.WriteLine("PASS: action release receipts at player boundaries, completion/cancel/death/manual/lease, pause, world and immutable E");
    }
}
