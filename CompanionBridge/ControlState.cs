using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;

namespace CompanionBridge;

internal sealed record ControlRequest(int Id, string Cmd);
internal sealed record ControlReply(int V, int Id, bool Ok, string Status, string Mode);
internal enum ControlMode { None, Manual, External }

// No Terraria references: the socket side submits requests; the game thread applies them.
internal sealed class ControlState
{
    internal const int LeaseMilliseconds = 750;
    internal const int Capacity = 32;
    private readonly object _gate = new();
    private readonly Queue<Pending> _pending = new();
    private int _nextSession, _session, _lastId;
    private long _deadline;
    private ControlMode _mode;
    private sealed record Pending(int Session, ControlRequest Request, long ReceivedAt,
        TaskCompletionSource<ControlReply> Completion);

    internal ControlMode Mode { get { lock (_gate) return _mode; } }
    internal bool IsMoving => Mode != ControlMode.None;

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
            if (_mode == ControlMode.External) _mode = ControlMode.None;
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

    internal void Update(bool canControl, long now)
    {
        lock (_gate)
        {
            if (!canControl)
            {
                _mode = ControlMode.None;
                ClearPending("not_ready");
                return;
            }
            if (_mode != ControlMode.None && now >= _deadline) _mode = ControlMode.None;
            while (_pending.TryDequeue(out var pending))
            {
                var request = pending.Request;
                string status = "applied";
                if (pending.Session != _session) status = "disconnected";
                else if (request.Cmd == "stop") _mode = ControlMode.None;
                else if (_mode == ControlMode.Manual) status = "manual_active";
                else if (now >= pending.ReceivedAt + LeaseTicks) status = "expired";
                else
                {
                    _mode = ControlMode.External;
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
            _mode = _mode == ControlMode.None ? ControlMode.Manual : ControlMode.None;
            _deadline = now + Stopwatch.Frequency * 10;
        }
    }

    internal void Reset()
    {
        lock (_gate)
        {
            _mode = ControlMode.None;
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
            || cmd.GetString() is not ("move_right" or "stop"))
            throw new FormatException("Expected v=1, positive id, cmd=move_right or stop.");
        return new ControlRequest(n, cmd.GetString());
    }
}
