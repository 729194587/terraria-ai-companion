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
        Console.WriteLine("PASS: bounded left/right input, absolute duration, lease, cancellation/ownership, death/respawn, manual and stale-start safety");
    }
}
