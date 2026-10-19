using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CompanionBridge;

public static class BridgeOfflineTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    private static long Ms(int milliseconds) => Stopwatch.Frequency * milliseconds / 1000;

    public static void Run()
    {
        var state = new ControlState();
        int session = state.BeginSession();
        var reply = state.Enqueue(session, new ControlRequest(1, "move_right"), 0);
        Check(!state.IsMoving && !reply.IsCompleted, "Network enqueue must not apply input");
        state.Update(true, Ms(100));
        Check(reply.Result.Ok && state.Mode == ControlMode.External, "Game tick applies input");
        state.Update(true, Ms(749));
        Check(state.IsMoving, "Lease must last until deadline");
        state.Update(true, Ms(750));
        Check(!state.IsMoving, "Lease must expire without heartbeat");
        reply = state.Enqueue(session, new ControlRequest(2, "move_right"), Ms(800));
        state.Update(true, Ms(1600));
        Check(!reply.Result.Ok && !state.IsMoving, "Queued old input must not get a fresh lease");
        state.Enqueue(session, new ControlRequest(3, "move_right"), Ms(1700));
        state.Update(true, Ms(1700));
        Check(!state.Enqueue(session, new ControlRequest(3, "move_right"), Ms(1800)).Result.Ok,
            "Repeated ids must not renew lease");
        state.Enqueue(session, new ControlRequest(4, "stop"), Ms(1800));
        state.Update(true, Ms(1800));
        Check(!state.IsMoving, "Stop releases control");
        state.ToggleManual(Ms(2000));
        reply = state.Enqueue(session, new ControlRequest(5, "move_right"), Ms(2000));
        state.Update(true, Ms(2000));
        Check(!reply.Result.Ok && state.Mode == ControlMode.Manual, "Manual priority");
        state.Update(true, Ms(12000));
        Check(!state.IsMoving, "Manual 10 second protection");
        state.Enqueue(session, new ControlRequest(6, "move_right"), Ms(13000));
        state.EndSession(session);
        state.Update(true, Ms(13001));
        Check(!state.IsMoving, "Disconnect invalidates pending input");
        int newer = state.BeginSession();
        state.Enqueue(newer, new ControlRequest(1, "move_right"), Ms(14000));
        state.EndSession(session);
        state.Update(true, Ms(14000));
        Check(state.IsMoving, "Old EOF must not cancel a new session");
        state.Update(false, Ms(14001));
        Check(!state.IsMoving, "Invalid player must release input");
        state.EndSession(newer);
        var renewal = new ControlState();
        int renewalSession = renewal.BeginSession();
        renewal.Enqueue(renewalSession, new ControlRequest(1, "move_right"), 0);
        renewal.Update(true, 0);
        renewal.Enqueue(renewalSession, new ControlRequest(2, "move_right"), Ms(500));
        renewal.Update(true, Ms(500));
        renewal.Update(true, Ms(800));
        Check(renewal.IsMoving, "Valid heartbeat extends the lease");
        renewal.Update(true, Ms(1250));
        Check(!renewal.IsMoving, "Renewed lease still has a finite deadline");
        foreach (string invalid in new[] { "{}", "[]", "{\"v\":2,\"id\":1,\"cmd\":\"move_right\"}",
            "{\"v\":1,\"id\":1,\"cmd\":\"attack\"}" })
        {
            bool rejected = false;
            try { ControlState.Parse(invalid); } catch (Exception) { rejected = true; }
            Check(rejected, "Invalid protocol must be rejected");
        }
        NetworkTests();
        Console.WriteLine("PASS: control lease, ownership, lifecycle, protocol, real TCP framing/disconnect/unload");
    }

    private static void NetworkTests()
    {
        var state = new ControlState();
        using var server = new TcpControlServer(state, 0);
        server.Start();
        using var client = new TcpClient();
        client.Connect("127.0.0.1", server.Port);
        client.ReceiveTimeout = 2000;
        using var reader = new StreamReader(client.GetStream(), Encoding.UTF8, false, 1024, true);
        Check(reader.ReadLine().Contains("hello"), "Real TCP hello");
        // Fragment one command, then coalesce its end and the next command in one write.
        byte[] first = Encoding.UTF8.GetBytes("{\"v\":1,\"id\":1,\"cmd\":\"move_");
        client.GetStream().Write(first);
        state.Update(true, Stopwatch.GetTimestamp());
        Check(!state.IsMoving, "Partial frame must not apply");
        byte[] tail = Encoding.UTF8.GetBytes("right\"}\n{\"v\":1,\"id\":2,\"cmd\":\"stop\"}\n");
        client.GetStream().Write(tail);
        Task<string> ack = reader.ReadLineAsync();
        PumpUntil(() => ack.IsCompleted, state);
        Check(ack.Result.Contains("applied"), "Game thread acknowledgement");
        Task<string> stop = reader.ReadLineAsync();
        PumpUntil(() => stop.IsCompleted, state);
        Check(stop.Result.Contains("applied") && !state.IsMoving, "Coalesced stop");
        byte[] move = Encoding.UTF8.GetBytes("{\"v\":1,\"id\":3,\"cmd\":\"move_right\"}\n");
        client.GetStream().Write(move);
        PumpUntil(() => state.IsMoving, state);
        client.Close();
        PumpUntil(() => !state.IsMoving, state);
        // Disposal must release the listening port.
        int port = server.Port;
        server.Dispose();
        using var replacement = new TcpControlServer(new ControlState(), port);
        replacement.Start();
    }

    private static void PumpUntil(Func<bool> condition, ControlState state)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.ElapsedMilliseconds < 2000)
        {
            state.Update(true, Stopwatch.GetTimestamp());
            Thread.Sleep(5);
        }
        Check(condition(), "Timed out waiting for TCP/game-thread boundary");
    }
}
