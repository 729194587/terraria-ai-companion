using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CompanionBridge;
using CompanionBridge.Motion;

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
        StateTests();
        TerrainTests();
        GuardedControlTests();
        NetworkTests();
        Console.WriteLine("PASS: state/terrain snapshots, tile classification, typed input, control lease, ownership, lifecycle, protocol, real TCP framing/disconnect/unload");
    }

    private static void GuardedControlTests()
    {
        var state = new ControlState();
        int session = state.BeginSession();
        var start = ControlState.Parse("{\"v\":1,\"id\":1,\"cmd\":\"terrain_step\",\"direction\":1,\"expectedGeneration\":0}");
        var result = state.Enqueue(session, start, 0);
        state.Update(true, 0);
        Check(result.Result.Ok && state.Generation == 1 && state.Input.Predictive, "Guarded start");
        result = state.Enqueue(session, new(2, "terrain_step", ExpectedGeneration: 1), Ms(100));
        state.Update(true, Ms(100));
        Check(result.Result.Ok && state.Generation == 1, "Guarded renewal retains current action");
        // Expire and receive a recent request in the SAME update: never start a new action.
        result = state.Enqueue(session, new(3, "terrain_step", ExpectedGeneration: 1), Ms(850));
        state.Update(true, Ms(850));
        Check(!result.Result.Ok && result.Result.Status == "generation_changed" && !state.IsMoving,
            "Expired guarded renewal cannot restart motion");
        int idle = state.Generation;
        result = state.Enqueue(session, new(4, "terrain_step", ExpectedGeneration: idle), Ms(900));
        state.Update(true, Ms(900));
        Check(result.Result.Ok, "Fresh explicitly observed generation can start");
        int active = state.Generation;
        state.ToggleManual(Ms(901));
        result = state.Enqueue(session, new(5, "terrain_step", ExpectedGeneration: active), Ms(902));
        var stop = state.Enqueue(session, new(6, "stop", ExpectedGeneration: active), Ms(902));
        state.Update(true, Ms(902));
        Check(!result.Result.Ok && !stop.Result.Ok && state.Generation != active,
            "P invalidates both pending continuation and stale cleanup");
        state.ToggleManual(Ms(903));
        int manual = state.Generation;
        stop = state.Enqueue(session, new(7, "stop", ExpectedGeneration: active), Ms(904));
        state.Update(true, Ms(904));
        Check(!stop.Result.Ok && state.Mode == ControlMode.Manual && state.Generation == manual,
            "Stale stop cannot steal new manual control");
        foreach (string value in new[] { "null", "true", "-1", "1.5", "\"1\"" })
        {
            bool rejected = false;
            try { ControlState.Parse("{\"v\":1,\"id\":1,\"cmd\":\"stop\",\"expectedGeneration\":" + value + "}"); }
            catch (FormatException) { rejected = true; }
            Check(rejected, "Malformed generation guard rejected");
        }
    }

    private static void TerrainTests()
    {
        char Classify(bool active = true, bool actuated = false, bool solid = true,
            bool platform = false, int frame = 0, bool half = false, int slope = 0,
            bool liquid = false, bool harmful = false, bool loaded = true) =>
            TerrainClassifier.Classify(loaded, active, actuated, solid, platform, frame, half, slope, liquid, harmful);
        Check(Classify() == '#', "Ordinary full block");
        Check(Classify(active: false) == '.' && Classify(actuated: true) == '.', "Empty/actuated never collide");
        Check(Classify(solid: false) == '.', "Decorations are not walls");
        Check(Classify(platform: true) == '=' && Classify(solid: false, platform: true) == '=', "One-way platforms");
        Check(Classify(platform: true, frame: 18) == '=', "Solid-top platform styles retain support at nonzero frameY");
        Check(Classify(solid: false, platform: true, frame: 18) == '.', "Furniture non-top frames have no support");
        Check(Classify(half: true) == 'h', "Half block remains distinct");
        for (int slope = 1; slope <= 4; slope++)
            Check(Classify(slope: slope) == (char)('0' + slope), "All four slope orientations preserved");
        Check(Classify(platform: true, slope: 1) == 'a' && Classify(platform: true, slope: 2) == 'b',
            "Sloped platforms remain one-way stairs");
        Check(Classify(slope: 5) == '?' && Classify(platform: true, slope: 3) == '?', "Unsupported slope fails closed");
        Check(TerrainClassifier.SurfaceOffset('1', 4, 12) == 4
            && TerrainClassifier.SurfaceOffset('2', 4, 12) == 4
            && TerrainClassifier.SurfaceOffset('a', 4, 12) == 4
            && TerrainClassifier.SurfaceOffset('b', 4, 12) == 4, "Footprint edges select native floor height");
        Check(TerrainClassifier.SurfaceOffset('1', -4, 20) == 0
            && TerrainClassifier.SurfaceOffset('2', -4, 20) == 0
            && TerrainClassifier.SurfaceOffset('3', 4, 12) == 0
            && TerrainClassifier.SurfaceOffset('4', 4, 12) == 0
            && float.IsNaN(TerrainClassifier.SurfaceOffset('?', 0, 16)), "Partial footprint clamps and ceiling tops");
        Check(Classify(liquid: true, active: false) == '!' && Classify(harmful: true) == '!'
            && Classify(loaded: false) == '?', "Hazards and unloaded terrain are not air");

        var request = ControlState.Parse("{\"v\":1,\"id\":1,\"cmd\":\"input\",\"direction\":-1,\"jump\":true}");
        Check(request.Direction == -1 && request.Jump, "Typed input parses");
        foreach (string extra in new[] { "", ",\"direction\":2,\"jump\":false", ",\"direction\":1,\"jump\":1",
            ",\"direction\":0", ",\"direction\":true,\"jump\":false" })
        {
            bool rejected = false;
            try { ControlState.Parse("{\"v\":1,\"id\":1,\"cmd\":\"input\"" + extra + "}"); }
            catch (Exception e) when (e is FormatException or InvalidOperationException) { rejected = true; }
            Check(rejected, "Malformed input rejected");
        }
        var state = new ControlState();
        int session = state.BeginSession();
        var applied = state.Enqueue(session, request, 0);
        Check(!applied.IsCompleted && !state.IsMoving, "Input cannot apply on socket thread");
        state.Update(true, 0);
        Check(applied.Result.Ok && state.Input == new ControlInput(-1, true, true), "Input applies direction and jump");
        var terrain = new TerrainSnapshot(10, 20, 16, new[] { ".#=hs!?" }, 20, 42, true, true);
        var query = state.Enqueue(session, new ControlRequest(2, "get_terrain"), Ms(500));
        Check(!query.IsCompleted, "Terrain waits for game update");
        state.Update(true, Ms(500), PlayerSnapshot.NotInWorld, terrain);
        Check(query.Result.Ok && query.Result.Terrain == terrain, "Terrain snapshot crosses queue as data");
        state.Update(true, Ms(750));
        Check(!state.IsMoving, "Terrain query must not renew jump/horizontal lease");
        state.Enqueue(session, new ControlRequest(3, "input", 0, false), Ms(800));
        state.Update(true, Ms(800));
        Check(state.Input == new ControlInput(0, false, true), "Explicit input releases jump and direction");
        state.Enqueue(session, new ControlRequest(4, "move_right"), Ms(900));
        state.Update(true, Ms(900));
        Check(state.Input == new ControlInput(1, false, false), "Legacy move_right preserves human jump");
        state.ToggleManual(Ms(1000)); // External -> none, then none -> manual.
        state.ToggleManual(Ms(1000));
        var denied = state.Enqueue(session, new ControlRequest(5, "input", -1, true), Ms(1000));
        state.Update(true, Ms(1000));
        Check(!denied.Result.Ok && denied.Result.Status == "manual_active"
            && !state.Input.OverrideJump, "Typed input cannot steal manual control");
        state.EndSession(session);
        state.Reset();
        session = state.BeginSession();
        state.Enqueue(session, new ControlRequest(1, "input", 1, true), Ms(1100));
        state.Update(true, Ms(1900));
        Check(!state.IsMoving, "Old queued jump must not activate after lease expiry");
        var missing = state.Enqueue(session, new ControlRequest(2, "get_terrain"), Ms(2000));
        state.Update(false, Ms(2000));
        Check(!missing.Result.Ok && missing.Result.Status == "not_ready", "No world has no terrain");
        var cleared = state.Enqueue(session, new ControlRequest(3, "get_terrain"), Ms(2000));
        state.EndSession(session);
        Check(!cleared.Result.Ok, "Disconnect clears queued terrain reads");
    }

    private static void StateTests()
    {
        Check(ControlState.Parse("{\"v\":1,\"id\":1,\"cmd\":\"get_state\"}").Cmd == "get_state",
            "State request parses");
        var state = new ControlState();
        int session = state.BeginSession();
        var pending = state.Enqueue(session, new ControlRequest(1, "get_state"), 0);
        Check(!pending.IsCompleted, "State request must wait for game thread, even while paused");
        state.Update(false, Ms(1000));
        Check(pending.Result.Ok && pending.Result.State == PlayerSnapshot.NotInWorld,
            "Menu returns successful explicit empty state even after movement lease duration");
        var snapshot = new PlayerSnapshot(true, new StateVector(10.5f, 20), new StateVector(-2, 0), 80, 120);
        state.Enqueue(session, new ControlRequest(2, "move_right"), Ms(1100));
        state.Update(true, Ms(1100), snapshot);
        pending = state.Enqueue(session, new ControlRequest(3, "get_state"), Ms(1500));
        state.Update(true, Ms(1500), snapshot);
        Check(pending.Result.State == snapshot && state.Mode == ControlMode.External,
            "Read returns supplied immutable snapshot without stopping movement");
        state.Update(true, Ms(1850), snapshot);
        Check(!state.IsMoving, "State query must not renew 750ms movement lease");
        state.ToggleManual(Ms(2000));
        pending = state.Enqueue(session, new ControlRequest(4, "get_state"), Ms(2000));
        state.Update(true, Ms(2000), snapshot);
        Check(pending.Result.Ok && state.Mode == ControlMode.Manual, "Read must preserve P mode");
        pending = state.Enqueue(session, new ControlRequest(5, "get_state"), Ms(2000));
        state.EndSession(session);
        Check(!pending.Result.Ok && pending.Result.State == null, "Disconnect clears queued reads");
        session = state.BeginSession();
        pending = state.Enqueue(session, new ControlRequest(1, "get_state"), Ms(2100));
        state.Update(false, Ms(2100), snapshot);
        Check(pending.Result.Ok && pending.Result.State.InWorld, "Dead player state does not require control eligibility");
        var move = state.Enqueue(session, new ControlRequest(2, "move_right"), Ms(2200));
        state.Update(false, Ms(2200));
        Check(!move.Result.Ok && move.Result.Status == "not_ready", "Menu still rejects movement");
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
        string hello = reader.ReadLine();
        Check(hello.Contains("hello") && hello.Contains("guarded_control"), "Real TCP hello advertises guard");
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
        Check(!ack.Result.Contains("state"), "Movement response format stays unchanged");
        Task<string> stop = reader.ReadLineAsync();
        PumpUntil(() => stop.IsCompleted, state);
        Check(stop.Result.Contains("applied") && !state.IsMoving, "Coalesced stop");
        client.GetStream().Write(Encoding.UTF8.GetBytes("{\"v\":1,\"id\":3,\"cmd\":\"get_state\"}\n"));
        Task<string> readState = reader.ReadLineAsync();
        Check(!readState.Wait(50), "Network thread must not return state without a game update");
        var snapshot = new PlayerSnapshot(true, new StateVector(123.5f, 200), new StateVector(1, -2), 75, 100);
        var timer = Stopwatch.StartNew();
        while (!readState.IsCompleted && timer.ElapsedMilliseconds < 2000)
        {
            state.Update(true, Stopwatch.GetTimestamp(), snapshot);
            Thread.Sleep(5);
        }
        Check(readState.IsCompleted, "State reply completes after game update");
        using (var json = JsonDocument.Parse(readState.Result))
        {
            var root = json.RootElement;
            Check(root.GetProperty("id").GetInt32() == 3 && root.GetProperty("ok").GetBoolean(), "State response envelope");
            var value = root.GetProperty("state");
            Check(value.GetProperty("inWorld").GetBoolean()
                && value.GetProperty("position").GetProperty("x").GetSingle() == 123.5f
                && value.GetProperty("velocity").GetProperty("y").GetSingle() == -2
                && value.GetProperty("life").GetInt32() == 75
                && value.GetProperty("lifeMax").GetInt32() == 100, "Real TCP state serialization");
        }
        client.GetStream().Write(Encoding.UTF8.GetBytes("{\"v\":1,\"id\":4,\"cmd\":\"get_state\"}\n"));
        Task<string> menuState = reader.ReadLineAsync();
        timer.Restart();
        while (!menuState.IsCompleted && timer.ElapsedMilliseconds < 2000)
        {
            state.Update(false, Stopwatch.GetTimestamp(), PlayerSnapshot.NotInWorld);
            Thread.Sleep(5);
        }
        Check(menuState.IsCompleted, "Menu state reply completes");
        using (var json = JsonDocument.Parse(menuState.Result))
        {
            var value = json.RootElement.GetProperty("state");
            Check(!value.GetProperty("inWorld").GetBoolean()
                && value.GetProperty("position").ValueKind == JsonValueKind.Null
                && value.GetProperty("velocity").ValueKind == JsonValueKind.Null
                && value.GetProperty("life").ValueKind == JsonValueKind.Null
                && value.GetProperty("lifeMax").ValueKind == JsonValueKind.Null, "Menu fields are explicit nulls");
        }
        client.GetStream().Write(Encoding.UTF8.GetBytes("{\"v\":1,\"id\":5,\"cmd\":\"get_terrain\"}\n"));
        Task<string> readTerrain = reader.ReadLineAsync();
        timer.Restart();
        var terrain = new TerrainSnapshot(100, 200, 16, new[] { ".#=hs!?" }, 20, 42, true, true);
        while (!readTerrain.IsCompleted && timer.ElapsedMilliseconds < 2000)
        {
            state.Update(true, Stopwatch.GetTimestamp(), snapshot, terrain);
            Thread.Sleep(5);
        }
        Check(readTerrain.IsCompleted, "Terrain TCP reply completes");
        using (var json = JsonDocument.Parse(readTerrain.Result))
        {
            var root = json.RootElement;
            Check(root.GetProperty("status").GetString() == "terrain"
                && root.GetProperty("terrain").GetProperty("rows")[0].GetString() == ".#=hs!?"
                && root.GetProperty("state").GetProperty("life").GetInt32() == 75,
                "Terrain and own state serialized in the same reply");
        }
        byte[] move = Encoding.UTF8.GetBytes("{\"v\":1,\"id\":6,\"cmd\":\"input\",\"direction\":-1,\"jump\":true}\n");
        client.GetStream().Write(move);
        PumpUntil(() => state.IsMoving, state);
        Check(state.Input == new ControlInput(-1, true, true), "TCP typed input applied by game pump");
        Check(reader.ReadLine().Contains("applied"), "Typed input TCP acknowledgement");
        client.GetStream().Write(Encoding.UTF8.GetBytes("{\"v\":1,\"id\":7,\"cmd\":\"terrain_step\",\"direction\":1,\"expectedGeneration\":"
            + state.Generation + "}\n"));
        var predictiveAck = reader.ReadLineAsync();
        PumpUntil(() => predictiveAck.IsCompleted, state);
        Check(predictiveAck.Result.Contains("applied") && state.Input.Predictive,
            "Predictive action enters existing lease over TCP");
        client.GetStream().Write(Encoding.UTF8.GetBytes("{\"v\":1,\"id\":8,\"cmd\":\"get_navigation\"}\n"));
        var readNavigation = reader.ReadLineAsync();
        Check(!readNavigation.Wait(30), "Socket cannot calculate/read navigation without game update");
        timer.Restart();
        var navigation = new NavigationSnapshot(state.SessionId, "landed", "jump", "fixture", 40, 8,
            new(1600, 1000), new(1601, 1000), 1, Generation: state.Generation);
        while (!readNavigation.IsCompleted && timer.ElapsedMilliseconds < 2000)
        {
            state.Update(true, Stopwatch.GetTimestamp(), snapshot, terrain, navigation);
            Thread.Sleep(5);
        }
        Check(readNavigation.IsCompleted, "Navigation reply completes after game update");
        using (var json = JsonDocument.Parse(readNavigation.Result))
        {
            var nav = json.RootElement.GetProperty("navigation");
            Check(nav.GetProperty("status").GetString() == "landed"
                && nav.GetProperty("actualLanding").GetProperty("x").GetSingle() == 1601,
                "Navigation result and actual landing serialized over TCP");
        }
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
