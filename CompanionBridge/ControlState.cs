using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using CompanionBridge.Motion;

namespace CompanionBridge;

internal sealed record ControlRequest(int Id, string Cmd, int Direction = 1, bool Jump = false);
internal sealed record ControlInput(int Direction, bool Jump, bool OverrideJump, bool Predictive = false);
internal sealed record StateVector(float X, float Y);
internal sealed record PlayerSnapshot(bool InWorld, StateVector Position, StateVector Velocity,
    int? Life, int? LifeMax)
{
    internal static readonly PlayerSnapshot NotInWorld = new(false, null, null, null, null);
}
internal sealed record ControlReply(int V, int Id, bool Ok, string Status, string Mode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PlayerSnapshot State = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TerrainSnapshot Terrain = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NavigationSnapshot Navigation = null);
internal enum ControlMode { None, Manual, External }

// No Terraria references: the socket side submits requests; the game thread applies them.
internal sealed class ControlState
{
    internal const int LeaseMilliseconds = 750;
    internal const int Capacity = 32;
    private readonly object _gate = new();
    private readonly Queue<Pending> _pending = new();
    private int _nextSession, _session, _lastId;
    private int _generation;
    private long _deadline;
    private ControlMode _mode;
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
            return _session;
        }
    }

    internal void EndSession(int session)
    {
        lock (_gate)
        {
            if (_session != session) return;
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
            if (_pending.Count >= Capacity) throw new InvalidOperationException("Command queue full.");
            var completion = new TaskCompletionSource<ControlReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Enqueue(new Pending(session, request, receivedAt, completion));
            return completion.Task;
        }
    }

    internal void Update(bool canControl, long now, PlayerSnapshot snapshot = null,
        TerrainSnapshot terrain = null, NavigationSnapshot navigation = null)
    {
        lock (_gate)
        {
            if (!canControl)
            {
                if (_mode != ControlMode.None) _generation++;
                _mode = ControlMode.None;
            }
            if (_mode != ControlMode.None && now >= _deadline) { _mode = ControlMode.None; _generation++; }
            while (_pending.TryDequeue(out var pending))
            {
                var request = pending.Request;
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
                          Terrain = request.Cmd == "get_terrain" ? terrain : null });
                    continue;
                }
                string status = "applied";
                if (pending.Session != _session) status = "disconnected";
                else if (!canControl) status = "not_ready";
                else if (request.Cmd == "stop") { _mode = ControlMode.None; _generation++; }
                else if (_mode == ControlMode.Manual) status = "manual_active";
                else if (now >= pending.ReceivedAt + LeaseTicks) status = "expired";
                else
                {
                    if (_mode != ControlMode.External || _input.Predictive != (request.Cmd == "terrain_step"))
                        _generation++;
                    _mode = ControlMode.External;
                    _input = new ControlInput(request.Direction, request.Jump,
                        request.Cmd is "input" or "terrain_step", request.Cmd == "terrain_step");
                    _deadline = pending.ReceivedAt + LeaseTicks;
                }
                pending.Completion.TrySetResult(Reply(request.Id, status == "applied", status));
            }
        }
    }

    internal void ToggleManual(long now)
    {
        lock (_gate)
        {
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
            _mode = ControlMode.None;
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
                or "terrain_step" or "get_navigation"))
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
        return new ControlRequest(n, cmd.GetString(), direction, jump);
    }
}
