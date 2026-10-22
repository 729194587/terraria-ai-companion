using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using CompanionBridge.Motion;

namespace CompanionBridge;

internal sealed record ControlRequest(int Id, string Cmd, int Direction = 1, bool Jump = false,
    int? ExpectedGeneration = null, float? TargetX = null, string Text = null, string ChatId = null,
    int? DurationMs = null, int? ActionId = null, string Operation = null, JsonElement Arguments = default, LandingRegion TargetRegion = null);
internal sealed record ControlInput(int Direction, bool Jump, bool OverrideJump, bool Predictive = false, float? TargetX = null, ControlRequest Atomic = null, LandingRegion TargetRegion = null);
internal sealed record StateVector(float X, float Y);
internal sealed record PlayerSnapshot(bool InWorld, StateVector Position, StateVector Velocity,
    int? Life, int? LifeMax)
{
    internal static readonly PlayerSnapshot NotInWorld = new(false, null, null, null, null);
}
internal sealed record MovementEndEvidence(string WorldSessionId, ulong GameUpdate, string Phase);
internal sealed record MovementSnapshot(int ActionId, int Session, int Generation, int Direction, int DurationMs,
    string Status, string Reason, PlayerSnapshot Before, PlayerSnapshot After, string StartedAt, string EndedAt,
    double ElapsedMilliseconds, string WorldSessionId = null, MovementEndEvidence EndEvidence = null, string Operation = null, object Evidence = null, LandingRegion TargetRegion = null)
{
    public bool Completed => Status == "completed";
    public bool Cancelled => Status == "cancelled";
    public string EndEvidenceStatus => Status == "running" ? "not_ended"
        : EndEvidence != null ? "known" : WorldSessionId != null ? "pending" : "unknown";
}
internal sealed record MovementFeedback(int Generation, MovementSnapshot Action);
internal sealed record ControlReply(int V, int Id, bool Ok, string Status, string Mode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PlayerSnapshot State = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TerrainSnapshot Terrain = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NavigationSnapshot Navigation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] EntityPerception Entities = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ObservationSnapshot Observation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MovementFeedback Movement = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ControlGeneration = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object Data = null);
internal enum ControlMode { None, Manual, External }

// No Terraria references: the socket side submits requests; the game thread applies them.
internal sealed class ControlState
{
    internal const int LeaseMilliseconds = 750;
    internal const int Capacity = 32;
    internal const int MaxMoveMilliseconds = 2000;
    private MovementSnapshot _movement;
    private long _moveStartedAt, _moveEndsAt;
    private int _nextActionId;
    private readonly object _gate = new();
    private readonly Queue<Pending> _pending = new();
    private int _nextSession, _session, _lastId;
    private int _generation;
    private long _deadline;
    private ControlMode _mode;
    private bool _wasCanControl;
    private bool _controlBlocked;
    private static bool IsReadOrChat(string cmd) => cmd is "get_state" or "get_terrain" or "get_navigation" or "get_observation" or "send_chat" or "get_movement" or "cancel_move" or "query_atomic";
    private string _latestChatId;
    internal void ObserveChat(string id) { lock (_gate) _latestChatId = id; }
    private ControlInput _input = new(1, false, false);
    private sealed record Pending(int Session, ControlRequest Request, long ReceivedAt,
        TaskCompletionSource<ControlReply> Completion);

    internal ControlMode Mode { get { lock (_gate) return _mode; } }
    internal int SessionId { get { lock (_gate) return _session; } }
    internal int Generation { get { lock (_gate) return _generation; } }
    internal bool IsMoving => Mode != ControlMode.None;
    internal ControlInput Input { get { lock (_gate) return _mode == ControlMode.External
        ? _input : new ControlInput(1, false, false); } }

    internal int BeginSession()
    {
        lock (_gate)
        {
            if (_session != 0) throw new InvalidOperationException("Controller already connected.");
            _session = ++_nextSession;
            _lastId = 0;
            _latestChatId = null;
            return _session;
        }
    }

    internal void EndSession(int session)
    {
        lock (_gate)
        {
            if (_session != session) return;
            FinishMovement(false, "disconnected", Stopwatch.GetTimestamp());
            _session = 0;
            if (_mode == ControlMode.External) { _mode = ControlMode.None; _generation++; }
            ClearPending("disconnected");
        }
    }

    internal Task<ControlReply> Enqueue(int session, ControlRequest request, long receivedAt)
    {
        lock (_gate)
        {
            if (session != _session || session == 0)
                return Task.FromResult(Reply(request.Id, false, "disconnected"));
            if (request.Id <= _lastId)
                return Task.FromResult(Reply(request.Id, false, "invalid_id"));
            _lastId = request.Id;
            if (_controlBlocked && !IsReadOrChat(request.Cmd))
                return Task.FromResult(Reply(request.Id, false, "not_ready"));
            if (_pending.Count >= Capacity) throw new InvalidOperationException("Command queue full.");
            var completion = new TaskCompletionSource<ControlReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Enqueue(new Pending(session, request, receivedAt, completion));
            return completion.Task;
        }
    }

    internal void Update(bool canControl, long now, PlayerSnapshot snapshot = null,
        TerrainSnapshot terrain = null, NavigationSnapshot navigation = null, Action<string> sendChat = null,
        EntityPerception entities = null, Func<ObservationSnapshot> readObservation = null,
        string worldSessionId = null, Func<ControlRequest, object> queryAtomic = null)
    {
        lock (_gate)
        {
            if (!canControl && (_wasCanControl || _mode != ControlMode.None)) RevokeControl(snapshot, now);
            _wasCanControl = canControl;
            if (canControl) _controlBlocked = false;
            else if (snapshot?.InWorld == true) _controlBlocked = true;
            if (_mode != ControlMode.None && now >= _deadline)
            {
                FinishMovement(false, "lease_expired", now, snapshot);
                _mode = ControlMode.None; _generation++;
            }
            else if (_movement?.Status == "running" && now >= _moveEndsAt)
            {
                if (_movement.Operation == null) FinishMovement(true, "duration_elapsed", now, snapshot);
                else FinishAtomic(_movement.ActionId, "timeout", "action_deadline", null, snapshot);
                if (_mode != ControlMode.None) { _mode = ControlMode.None; _generation++; }
            }
            ObservationSnapshot observation = null;
            bool observationAttempted = false, observationFailed = false;
            while (_pending.TryDequeue(out var pending))
            {
                var request = pending.Request;
                if (request.Cmd is "get_movement" or "cancel_move" && pending.Session == _session)
                {
                    bool match = request.Cmd == "get_movement" || _movement != null && request.ActionId == _movement.ActionId && _movement.Session == _session;
                    if (match && request.Cmd == "cancel_move" && _movement?.Status == "running")
                    {
                        FinishMovement(false, "stopped", now, snapshot);
                        _mode = ControlMode.None; _generation++;
                    }
                    pending.Completion.TrySetResult(Reply(request.Id, match, match ? "movement" : "action_changed") with
                        { State = snapshot, Movement = Movement(now) });
                    continue;
                }
                if (request.Cmd == "query_atomic" && pending.Session == _session)
                {
                    try { pending.Completion.TrySetResult(Reply(request.Id, true, "query") with { Data = queryAtomic?.Invoke(request), ControlGeneration = _generation }); }
                    catch (Exception) { pending.Completion.TrySetResult(Reply(request.Id, false, "query_failed")); }
                    continue;
                }
                if (request.Cmd is "move_for" or "start_atomic" or "renew_move" && pending.Session == _session)
                {
                    string result = !canControl || snapshot?.InWorld != true ? "not_ready"
                        : now >= pending.ReceivedAt + LeaseTicks ? "expired"
                        : request.Cmd == "renew_move" ? RenewMovement(request, pending.ReceivedAt)
                        : StartMovement(request, pending.ReceivedAt, now, snapshot, worldSessionId);
                    pending.Completion.TrySetResult(Reply(request.Id, result == "movement_started" || result == "movement", result) with
                        { State = snapshot, Movement = Movement(now) });
                    continue;
                }
                if (request.Cmd == "get_observation" && pending.Session == _session)
                {
                    if (!observationAttempted)
                    {
                        observationAttempted = true;
                        try { observation = readObservation?.Invoke(); }
                        catch (Exception) { observationFailed = true; } // A mod read must not break the update/control loop.
                    }
                    pending.Completion.TrySetResult(Reply(request.Id, observation != null,
                        observationFailed ? "observation_failed" : observation != null ? "observation" : "not_ready") with
                        { Observation = observation, ControlGeneration = _generation });
                    continue;
                }
                if (request.Cmd == "send_chat")
                {
                    string chatStatus = pending.Session != _session ? "disconnected"
                        : snapshot?.InWorld != true || sendChat == null ? "not_ready"
                        : request.ChatId != _latestChatId ? "superseded" : "sent";
                    if (chatStatus == "sent")
                    {
                        try { sendChat(request.Text); }
                        catch (Exception) { chatStatus = "chat_send_failed"; }
                    }
                    pending.Completion.TrySetResult(Reply(request.Id, chatStatus == "sent", chatStatus));
                    continue;
                }
                if (request.Cmd == "get_navigation" && pending.Session == _session)
                {
                    bool available = snapshot?.InWorld == true;
                    pending.Completion.TrySetResult(Reply(request.Id, available,
                        available ? "navigation" : "not_ready") with
                        { State = snapshot, Navigation = available
                            ? navigation?.Session == pending.Session && navigation.Generation == _generation ? navigation
                                : new(pending.Session, "idle", "none", "not started", 0, 0, null, null, 0,
                                    Generation: _generation)
                            : null });
                    continue;
                }
                if (request.Cmd is "get_state" or "get_terrain" && pending.Session == _session)
                {
                    bool available = request.Cmd == "get_state" || terrain != null;
                    pending.Completion.TrySetResult(Reply(request.Id, available,
                        !available ? "not_ready" : request.Cmd == "get_state" ? "state" : "terrain") with
                        { State = snapshot ?? PlayerSnapshot.NotInWorld,
                          Terrain = request.Cmd == "get_terrain" ? terrain : null,
                          Entities = snapshot?.InWorld == true ? entities : null });
                    continue;
                }
                string status = "applied";
                if (pending.Session != _session) status = "disconnected";
                else if (!canControl) status = "not_ready";
                else if (request.ExpectedGeneration is int expected && expected != _generation)
                    status = "generation_changed";
                else if (request.Cmd == "stop")
                {
                    FinishMovement(false, "stopped", now, snapshot);
                    _mode = ControlMode.None; _generation++;
                }
                else if (_mode == ControlMode.Manual) status = "manual_active";
                else if (_movement?.Status == "running") status = "movement_active";
                else if (now >= pending.ReceivedAt + LeaseTicks) status = "expired";
                else
                {
                    if (_mode != ControlMode.External || _input.Predictive != (request.Cmd is "terrain_step" or "navigate")
                        || _input.TargetX != request.TargetX || _input.Direction != request.Direction)
                        _generation++;
                    _mode = ControlMode.External;
                    _input = new ControlInput(request.Direction, request.Jump,
                        request.Cmd is "input" or "terrain_step" or "navigate",
                        request.Cmd is "terrain_step" or "navigate", request.TargetX);
                    _deadline = pending.ReceivedAt + LeaseTicks;
                }
                pending.Completion.TrySetResult(Reply(request.Id, status == "applied", status));
            }
        }
    }

    private MovementFeedback Movement(long now) => new(_generation,
        _movement?.Session != _session ? null : _movement.Status == "running"
            ? _movement with { ElapsedMilliseconds = (now - _moveStartedAt) * 1000.0 / Stopwatch.Frequency } : _movement);

    private string StartMovement(ControlRequest request, long receivedAt, long now, PlayerSnapshot snapshot,
        string worldSessionId)
    {
        if (request.Cmd != "start_atomic" && (request.Direction is not (-1 or 1) || request.DurationMs is not (>= 50 and <= MaxMoveMilliseconds))
            || request.ExpectedGeneration == null) return "invalid_movement";
        if (request.ChatId != null && request.ChatId != _latestChatId) return "superseded";
        if (request.ExpectedGeneration != _generation) return "generation_changed";
        if (_mode == ControlMode.Manual) return "manual_active";
        if (_mode != ControlMode.None || _movement?.Operation != null && _movement.EndEvidenceStatus == "pending") return "control_busy";
        if (request.Operation == "navigate_to_landing_region"
            && (request.TargetRegion == null || request.TargetX != null)) return "invalid_landing_region";
        _generation++;
        _mode = ControlMode.External;
        _input = request.Cmd == "start_atomic"
            ? new(0, false, true, request.Operation is "navigate_to_x" or "navigate_to_landing_region", request.TargetX, request, request.TargetRegion)
            : new(request.Direction, false, true); // Existing executor; no jump or predictive navigation.
        _deadline = receivedAt + LeaseTicks;
        _moveStartedAt = now;
        int duration = request.Cmd == "start_atomic" ? request.Operation is "navigate_to_x" or "navigate_to_landing_region" ? 30000 : 10000 : request.DurationMs.Value;
        _moveEndsAt = now + duration * Stopwatch.Frequency / 1000;
        _movement = new(++_nextActionId, _session, _generation, request.Direction, duration,
            "running", "started", snapshot, null, DateTimeOffset.UtcNow.ToString("O"), null, 0, worldSessionId, Operation: request.Operation, TargetRegion: request.TargetRegion);
        return "movement_started";
    }

    internal MovementSnapshot AtomicAction { get { lock (_gate) return _movement?.Operation != null && _movement.Session == _session ? _movement : null; } }

    internal void FinishAtomic(int actionId, string status, string reason, object evidence, PlayerSnapshot after = null)
    {
        lock (_gate)
        {
            if (_movement?.ActionId != actionId || _movement.Status != "running" || _movement.Operation == null) return;
            FinishMovement(false, reason, Stopwatch.GetTimestamp(), after);
            _movement = _movement with { Status = status, Evidence = evidence };
            _mode = ControlMode.None; _generation++;
        }
    }

    private string RenewMovement(ControlRequest request, long receivedAt)
    {
        if (_movement?.Status != "running" || _movement.Session != _session
            || request.ActionId != _movement.ActionId || _movement.Generation != _generation || _mode != ControlMode.External)
            return "action_changed";
        if (_movement.TargetRegion != null && request.ExpectedGeneration == null) return "generation_required";
        if (request.ExpectedGeneration is int expected && expected != _movement.Generation) return "generation_changed";
        if (request.TargetRegion != _movement.TargetRegion) return "target_changed";
        _deadline = receivedAt + LeaseTicks; // The absolute _moveEndsAt never changes.
        return "movement";
    }

    private void FinishMovement(bool completed, string reason, long now, PlayerSnapshot after = null)
    {
        if (_movement?.Status != "running") return;
        _movement = _movement with { Status = completed ? "completed" : "cancelled", Reason = reason,
            After = after, EndedAt = DateTimeOffset.UtcNow.ToString("O"),
            ElapsedMilliseconds = (now - _moveStartedAt) * 1000.0 / Stopwatch.Frequency };
        _input = new(0, false, false);
    }

    // Game-thread player hook only: normal input has been processed and the terminated
    // bounded action can no longer inject controls. A TCP request/FinishMovement is NOT
    // this boundary. Dead players use UpdateDead after revocation instead.
    internal void ConfirmMovementInputReleased(string worldSessionId, ulong gameUpdate, string phase, bool inputReleased)
    {
        lock (_gate)
        {
            if (!inputReleased || _movement == null || _movement.Status == "running" || _movement.EndEvidence != null
                || _session == 0 || _movement.Session != _session
                || string.IsNullOrEmpty(worldSessionId) || _movement.WorldSessionId != worldSessionId
                || (_mode == ControlMode.External && _generation == _movement.Generation)
                || phase is not ("PostUpdateRunSpeeds_before_bridge_input" or "UpdateDead_after_revoke")) return;
            _movement = _movement with { EndEvidence = new(worldSessionId, gameUpdate, phase) };
        }
    }

    internal void RevokeControl(PlayerSnapshot snapshot = null, long? now = null)
    {
        lock (_gate)
        {
            // Both the death hook and the next DoUpdate may observe this transition.
            FinishMovement(false, "control_unavailable", now ?? Stopwatch.GetTimestamp(), snapshot);
            if (!_controlBlocked || _wasCanControl || _mode != ControlMode.None) _generation++;
            _mode = ControlMode.None;
            _input = new(0, false, false);
            _deadline = 0;
            _wasCanControl = false;
            _controlBlocked = true;
            // A command queued before death cannot be applied after a fast respawn.
            // Preserve read/chat requests and their order on the same TCP session.
            int count = _pending.Count;
            for (int i = 0; i < count; i++)
            {
                var pending = _pending.Dequeue();
                if (IsReadOrChat(pending.Request.Cmd))
                    _pending.Enqueue(pending);
                else pending.Completion.TrySetResult(Reply(pending.Request.Id, false, "not_ready"));
            }
        }
    }

    internal void ToggleManual(long now)
    {
        lock (_gate)
        {
            FinishMovement(false, "manual_override", now);
            ClearPending("manual_override");
            _generation++;
            _mode = _mode == ControlMode.None ? ControlMode.Manual : ControlMode.None;
            _deadline = now + Stopwatch.Frequency * 10;
        }
    }

    internal void Reset()
    {
        lock (_gate)
        {
            FinishMovement(false, "reset", Stopwatch.GetTimestamp());
            _movement = null;
            _mode = ControlMode.None;
            _wasCanControl = false;
            _controlBlocked = false;
            _generation++;
            _deadline = 0;
            ClearPending("reset");
        }
    }

    private static long LeaseTicks => Stopwatch.Frequency * LeaseMilliseconds / 1000;
    private ControlReply Reply(int id, bool ok, string status) =>
        new(1, id, ok, status, _mode.ToString().ToLowerInvariant());
    private void ClearPending(string status)
    {
        while (_pending.TryDequeue(out var pending))
            pending.Completion.TrySetResult(Reply(pending.Request.Id, false, status));
    }

    internal static ControlRequest Parse(string line)
    {
        using var json = JsonDocument.Parse(line);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("v", out var version) || !version.TryGetInt32(out int v) || v != 1
            || !root.TryGetProperty("id", out var id) || !id.TryGetInt32(out int n) || n <= 0
            || !root.TryGetProperty("cmd", out var cmd) || cmd.ValueKind != JsonValueKind.String
            || cmd.GetString() is not ("move_right" or "stop" or "get_state" or "get_terrain" or "input"
                or "terrain_step" or "get_navigation" or "navigate" or "send_chat" or "get_observation"
                or "move_for" or "renew_move" or "cancel_move" or "get_movement" or "start_atomic" or "query_atomic"))
            throw new FormatException("Expected v=1, positive id and a supported cmd.");
        int direction = 1;
        bool jump = false;
        if (cmd.GetString() == "terrain_step" && (!root.TryGetProperty("direction", out var navDirection)
            || navDirection.ValueKind != JsonValueKind.Number
            || !navDirection.TryGetInt32(out direction) || direction is not (-1 or 1)))
            throw new FormatException("terrain_step requires direction=-1/1.");
        if (cmd.GetString() == "input")
        {
            if (!root.TryGetProperty("direction", out var d) || !d.TryGetInt32(out direction)
                || direction < -1 || direction > 1
                || !root.TryGetProperty("jump", out var j) || j.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new FormatException("input requires direction=-1/0/1 and boolean jump.");
            jump = j.GetBoolean();
        }
        float? targetX = null;
        if (cmd.GetString() == "navigate")
        {
            if (!root.TryGetProperty("targetX", out var target) || target.ValueKind != JsonValueKind.Number
                || !target.TryGetSingle(out float x) || !float.IsFinite(x) || x < 0 || x > 1000000)
                throw new FormatException("navigate requires finite targetX in [0,1000000].");
            targetX = x;
        }
        int? expectedGeneration = null;
        if (root.TryGetProperty("expectedGeneration", out var guard))
        {
            if (cmd.GetString() is not ("terrain_step" or "navigate" or "stop" or "move_for" or "start_atomic" or "renew_move")
                || guard.ValueKind != JsonValueKind.Number || !guard.TryGetInt32(out int expected) || expected < 0)
                throw new FormatException("expectedGeneration requires a guarded command and a nonnegative integer.");
            expectedGeneration = expected;
        }
        string text = null, chatId = null;
        if (cmd.GetString() == "send_chat")
        {
            if (!root.TryGetProperty("text", out var t) || t.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(text = t.GetString()) || text.Length > 400
                || text.Contains('\n') || text.Contains('\r') || text.Contains('\0')
                || !root.TryGetProperty("chatId", out var c) || c.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(chatId = c.GetString()) || chatId.Length > 100)
                throw new FormatException("send_chat requires single-line text (1..400 chars) and chatId.");
        }
        int? durationMs = null, actionId = null;
        if (cmd.GetString() == "move_for")
        {
            if (!root.TryGetProperty("direction", out var d) || !d.TryGetInt32(out direction) || direction is not (-1 or 1)
                || !root.TryGetProperty("durationMs", out var ms) || !ms.TryGetInt32(out int duration)
                || duration is < 50 or > MaxMoveMilliseconds || expectedGeneration == null)
                throw new FormatException("move_for requires direction=-1/1, durationMs=50..2000 and expectedGeneration.");
            durationMs = duration;
            if (root.TryGetProperty("chatId", out var chat))
            {
                if (chat.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(chatId = chat.GetString()) || chatId.Length > 100)
                    throw new FormatException("Invalid movement chatId.");
            }
        }
        if (cmd.GetString() is "renew_move" or "cancel_move")
        {
            if (!root.TryGetProperty("actionId", out var a) || !a.TryGetInt32(out int action) || action <= 0)
                throw new FormatException("Movement continuation/cancellation requires positive actionId.");
            actionId = action;
        }
        LandingRegion targetRegion = null;
        if (root.TryGetProperty("targetRegion", out var region))
        {
            if (cmd.GetString() != "renew_move") throw new FormatException("targetRegion is only a renewal guard; use atomic arguments to start.");
            targetRegion = AtomicParameters.ReadLandingRegion(region);
        }
        string operation = null;
        JsonElement arguments = default;
        if (cmd.GetString() is "start_atomic" or "query_atomic")
        {
            if (!root.TryGetProperty("operation", out var op) || op.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("arguments", out var args) || args.ValueKind != JsonValueKind.Object)
                throw new FormatException("Atomic operation requires operation and arguments object.");
            operation = op.GetString(); arguments = args.Clone();
            AtomicParameters.Validate(operation, arguments, cmd.GetString() == "query_atomic");
            if (cmd.GetString() == "start_atomic" && expectedGeneration == null)
                throw new FormatException("Atomic action requires expectedGeneration.");
            if (operation == "navigate_to_landing_region") targetRegion = AtomicParameters.ReadLandingRegion(arguments);
            if (operation == "navigate_to_x") targetX = arguments.GetProperty("targetX").GetSingle();
            if (root.TryGetProperty("chatId", out var chat))
            {
                if (chat.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(chatId = chat.GetString()) || chatId.Length > 100)
                    throw new FormatException("Invalid atomic chatId.");
            }
        }
        return new ControlRequest(n, cmd.GetString(), direction, jump, expectedGeneration, targetX, text, chatId, durationMs, actionId, operation, arguments, targetRegion);
    }
}
