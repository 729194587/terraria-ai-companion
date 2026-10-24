using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CompanionBridge;

public static class DeathLifecycleOfflineTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static void Run()
    {
        var state = new ControlState();
        using var server = new TcpControlServer(state, 0);
        server.Start();
        using var client = new TcpClient("127.0.0.1", server.Port);
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream);
        using var writer = new StreamWriter(stream) { AutoFlush = true };
        using var hello = JsonDocument.Parse(reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult());
        int session = state.SessionId;
        int id = 0;
        bool alive = true;
        ulong tick = 10;
        var chat = new List<string>();
        var terrain = new TerrainSnapshot(0, 0, 16, new[] { "." }, 20, 42, false, false);
        ObservationSnapshot Read() => new(1, "world", "world:" + tick, tick, "fixture-utc", "pre_DoUpdate",
            "partial", null, new { dead = !alive }, null, null, null, null,
            new(false, alive ? new[] { "camera_unavailable_in_fixture" } : new[] { "local_player_dead_or_ghost" }, Array.Empty<string>()), 0);

        JsonDocument Send(string cmd, int? expected = null, string chatId = null)
        {
            writer.WriteLine(JsonSerializer.Serialize(new { v = 1, id = ++id, cmd, direction = 1, jump = true,
                targetX = 100, expectedGeneration = expected, text = "fixture reply", chatId } ,
                new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
            var response = reader.ReadLineAsync();
            var wait = Stopwatch.StartNew();
            while (!response.IsCompleted && wait.Elapsed < TimeSpan.FromSeconds(2))
            {
                state.Update(alive, Stopwatch.GetTimestamp(), new(true, new(0, 0), new(0, 0), alive ? 100 : 0, 100),
                    terrain, sendChat: chat.Add, readObservation: Read);
                Thread.Sleep(1);
            }
            return JsonDocument.Parse(response.WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult());
        }

        using (var applied = Send("input")) Check(applied.RootElement.GetProperty("ok").GetBoolean(), "Initial movement accepted");
        int generation = state.Generation;
        alive = false;
        using (var denied = Send("input")) Check(!denied.RootElement.GetProperty("ok").GetBoolean(), "Movement during death rejected");
        Check(state.Mode == ControlMode.None && state.Generation == generation + 1 && state.SessionId == session,
            "Death revokes external control once and preserves TCP session");
        using (var dead = Send("get_observation"))
        {
            var obs = dead.RootElement.GetProperty("observation");
            Check(obs.GetProperty("status").GetString() == "partial"
                && obs.GetProperty("completeness").GetProperty("reasons")[0].GetString() == "local_player_dead_or_ghost",
                "Death returns current partial observation with reason");
        }
        using (var deadState = Send("get_state")) Check(deadState.RootElement.GetProperty("state").GetProperty("life").GetInt32() == 0,
            "State remains readable while dead");
        state.ObserveChat("dead-chat");
        using (var sent = Send("send_chat", chatId: "dead-chat")) Check(sent.RootElement.GetProperty("status").GetString() == "sent", "Chat while dead");
        Check(chat.Count == 1, "Death did not clear current chat identity");

        alive = true; tick++;
        using (var revived = Send("get_observation"))
        {
            var obs = revived.RootElement.GetProperty("observation");
            Check(obs.GetProperty("gameUpdate").GetUInt64() == 11 && !obs.GetProperty("player").GetProperty("dead").GetBoolean(),
                "Respawn reads current data on same connection");
        }
        using (var stale = Send("navigate", expected: generation)) Check(stale.RootElement.GetProperty("status").GetString() == "generation_changed",
            "Pre-death guarded command invalid after respawn");
        state.ObserveChat("revived-chat");
        using (var sent = Send("send_chat", chatId: "revived-chat")) Check(sent.RootElement.GetProperty("ok").GetBoolean(), "Chat after respawn");
        Check(chat.Count == 2 && state.SessionId == session && !state.IsMoving, "No reconnect and no old movement resumed");

        // Mid-update death/ghost hook can revoke before the next ControlState.Update.
        var oldInput = state.Enqueue(session, new(++id, "input", Jump: true), Stopwatch.GetTimestamp());
        var queuedRead = state.Enqueue(session, new(++id, "get_observation"), Stopwatch.GetTimestamp());
        state.RevokeControl();
        generation = state.Generation;
        state.RevokeControl();
        Check(state.Generation == generation, "Repeated game hooks for one death do not invalidate generation twice");
        state.Update(true, Stopwatch.GetTimestamp(), readObservation: Read);
        Check(!oldInput.Result.Ok && queuedRead.Result.Ok && !state.IsMoving && state.SessionId == session,
            "Revocation discards queued actions across fast respawn but preserves queued reads");
        generation = state.Generation;
        state.Update(false, Stopwatch.GetTimestamp()); // Also invalidate a transition with no active movement.
        Check(state.Generation == generation + 1, "Death/ghost invalidates generation even while idle");
        var lateDeadInput = state.Enqueue(session, new(++id, "move_right"), Stopwatch.GetTimestamp());
        Check(lateDeadInput.IsCompleted && !lateDeadInput.Result.Ok,
            "Input arriving between last dead update and first respawn update rejected at enqueue");
        state.Update(false, Stopwatch.GetTimestamp());
        Check(state.Generation == generation + 1, "Repeated dead updates do not repeatedly increment generation");
        state.Update(true, Stopwatch.GetTimestamp());
        Check(!state.IsMoving, "Late dead input cannot revive movement");

        server.Disconnect();
        Check(reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult() == null,
            "Real session teardown still closes TCP");
        Console.WriteLine("PASS: same TCP session across death/respawn, partial observations, chat, revoked queued actions, generations and teardown");
    }
}
