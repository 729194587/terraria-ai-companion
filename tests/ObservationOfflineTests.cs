using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using CompanionBridge;

public static class ObservationOfflineTests
{
    public static string SampleJson { get; private set; }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception e) when (e is FormatException or ArgumentException) { return; }
        throw new Exception("Expected invalid observation data to be rejected");
    }
    public static void Run()
    {
        // Real trace: two different dropped items had whoAmI=0 and both became item:0.
        // Exercise the producer's slot enumeration with duplicate/stale IDs and inactive holes.
        var itemSlots = new (int WhoAmI, bool Active, int Type)[32];
        itemSlots[5] = (0, true, 23); itemSlots[9] = (0, true, 321);
        for (int i = 12; i < 32; i++) itemSlots[i] = (0, true, 23);
        var slottedItems = ObservationEntitySlots.Enumerate(itemSlots).Where(entry => entry.Entity.Active).ToArray();
        var itemKeys = slottedItems.Select(entry => ObservationEntitySlots.InstanceKey("world:1", "item", entry.Slot)).ToArray();
        Check(slottedItems.Length == 22 && slottedItems[0].Slot == 5 && slottedItems[1].Slot == 9,
            "Actual sparse array slots retained, not filtered ordinals or duplicate whoAmI");
        Check(itemKeys[0] == "world:1:item:5" && itemKeys[1] == "world:1:item:9"
            && itemKeys.Distinct().Count() == 22, "Different client items keep unique instance keys without dropping any");
        Check(ObservationEntitySlots.InstanceKey("world:1", "npc", 5) != itemKeys[0]
            && ObservationEntitySlots.InstanceKey("world:2", "item", 5) != itemKeys[0],
            "Instance namespace separates entity kinds and observations");

        var camera = ObservationGeometry.Camera(1, 42, "fixture", 10, new(100, 200),
            new(13, 27, 1920, 1080), new(2, 2), new(2, 0, 0, 2, -960, -540));
        Check(camera.WorldBounds == new WorldRect(580, 470, 960, 540), "Inverse centered zoom, not screenPosition");
        Check(camera.Viewport.X == 13 && camera.TileBounds == new TileBounds(36, 29, 61, 35),
            "Device viewport offset retained; local sprite coordinates; partial edge tiles included");
        var flipped = ObservationGeometry.Camera(2, 43, "fixture", 10, new(-1, -1),
            new(0, 0, 1280, 720), new(1, 1), new(1, 0, 0, -1, 0, 720));
        Check(flipped.WorldBounds == new WorldRect(-1, -1, 1280, 720), "Gravity flip and negative world edge");
        Check(flipped.TileBounds == new TileBounds(-1, -1, 81, 46), "Floor negative coordinates, ceil exclusive far edge");
        Check(ObservationGeometry.Intersects(new(570, 500, 20, 20), camera.WorldCorners), "Center outside but AABB intersects");
        Check(!ObservationGeometry.Intersects(new(560, 500, 20, 20), camera.WorldCorners), "Touching edge has no area intersection");
        var diamond = new[] { new StateVector(0, 5), new(5, 0), new(10, 5), new(5, 10) };
        Check(!ObservationGeometry.Intersects(new(0, 0, 1, 1), diamond), "Polygon intersection excludes rotated AABB corner");
        Reject(() => ObservationGeometry.Camera(1, 1, "", 0, new(0, 0), new(0, 0, 100, 100), new(1, 1), new(0, 0, 0, 0, 0, 0)));
        Check(ObservationGeometry.CameraStatus(251, 42, 42) == "stale"
            && ObservationGeometry.CameraStatus(0, 45, 42) == "stale"
            && ObservationGeometry.CameraStatus(16, 43, 42) == "recent_render", "Camera age and update gaps");

        TileCell Cell(int x, int y) => (x, y) switch {
            (0, 0) => new("out_of_world"), (1, 0) => new("unloaded"), (2, 0) => new("read"),
            _ => new("read", true, 1, 2, (x % 16) * 18, (y % 8) * 18,
                HalfBlock: x % 2 == 0, Slope: x % 5, Solid: true, SolidTop: x % 3 == 0,
                Actuated: x % 2 == 1, Actuator: true, WireMask: x % 16,
                LiquidType: x % 4, LiquidAmount: 127, CollisionKnowledge: x % 7 == 0 ? "unknown_mod_hooks" : "known",
                TouchDamage: 10, Hot: true)
        };
        long started = Stopwatch.GetTimestamp();
        var terrain = ObservationEncoding.Encode(new(0, 0, 120, 68), Cell,
            (kind, id) => new(kind, id, "Fixture/" + id, false));
        double sampleMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        for (int y = 0; y < 68; y++)
        {
            var decoded = ObservationEncoding.Decode(terrain.Rows[y], terrain.Palette, 120);
            for (int x = 0; x < 120; x++) Check(decoded[x] == Cell(x, y), "Lossless full tile state round trip");
        }
        Check(terrain.UnknownCount == 2 && terrain.UninterpretedCount > 0, "Unknown cells and raw uninterpreted cells separate");
        var uniform = ObservationEncoding.Encode(new(0, 0, 120, 1), (_, _) => new("read"), (_, _) => null);
        Check(uniform.Rows[0].Encoding == "rle" && terrain.Rows[1].Encoding == "indices", "Mixed row encodings");
        Reject(() => ObservationEncoding.Decode(new("rle", new[] { 121, 0 }), uniform.Palette, 120));
        Reject(() => ObservationEncoding.Decode(new("rle", new[] { 0, 0 }), uniform.Palette, 120));
        Reject(() => ObservationEncoding.Decode(new("rle", new[] { 120 }), uniform.Palette, 120));
        Reject(() => ObservationEncoding.Decode(new("indices", new[] { 1 }), uniform.Palette, 1));
        Reject(() => ObservationEncoding.Decode(new("indices", new[] { 0 }), uniform.Palette, 2));
        Reject(() => ObservationEncoding.Encode(new(0, 0, 1000, 1000), (_, _) => null, (_, _) => null));

        var entities = Enumerable.Range(0, 25).Select(i => new ObservedEntity("npc", "world:1:npc:" + i,
            i, 1, "Fixture/NPC", "fixture", new(590, 500, 20, 20), new(0, 0), 10, 10,
            "unknown", "friendly=false alone insufficient", true, 42, null)).ToArray();
        var observation = new ObservationSnapshot(1, "fixture-world", "fixture-world:1", 42, "fixture-utc", "pre_DoUpdate",
            "partial", new(camera, 0, "recent_render"), new { id = 1 }, terrain,
            new(entities, new(25, 25, 0, "no cap")), new { source = "fixture" }, null,
            new(false, new[] { "unloaded cells" }, new[] { "offline fixture, not game measurement" }), sampleMs);
        var state = new ControlState(); int session = state.BeginSession(); int reads = 0;
        ObservationSnapshot Read() { reads++; return observation; }
        state.Update(true, 0, readObservation: Read);
        Check(reads == 0, "No observation scan without request");
        Check(ControlState.Parse("{\"v\":1,\"id\":1,\"cmd\":\"get_observation\"}").Cmd == "get_observation", "Command parser");
        state.Enqueue(session, new(1, "move_right"), 0); state.Update(true, 0);
        int generation = state.Generation;
        var a = state.Enqueue(session, new(2, "get_observation"), 1);
        var b = state.Enqueue(session, new(3, "get_observation"), 1);
        state.Update(true, 1, readObservation: Read);
        Check(reads == 1 && ReferenceEquals(a.Result.Observation, b.Result.Observation), "One detached snapshot per servicing update");
        Check(a.Result.Observation.GameUpdate == 42 && state.Generation == generation, "Metadata and control generation preserved");
        Check(a.Result.ControlGeneration == generation && b.Result.ControlGeneration == generation,
            "Observation replies carry the game-thread control generation without acquiring/renewing control");
        state.Update(true, Stopwatch.Frequency * 750 / 1000);
        Check(!state.IsMoving, "Observation cannot extend control lease");
        var missing = state.Enqueue(session, new(4, "get_observation"), 2);
        state.Update(false, 2);
        Check(!missing.Result.Ok && missing.Result.Status == "not_ready", "Unavailable reader cannot fall through to movement");
        var failed = state.Enqueue(session, new(5, "get_observation"), 2);
        state.Update(false, 2, readObservation: () => throw new InvalidOperationException("fixture mod error"));
        Check(!failed.Result.Ok && failed.Result.Status == "observation_failed" && !state.IsMoving,
            "Reader failure returns explicit error without escaping to game update or acquiring control");

        byte[] wire = TcpControlServer.SerializeFrame(a.Result);
        SampleJson = Encoding.UTF8.GetString(wire);
        using var json = JsonDocument.Parse(wire);
        Check(json.RootElement.GetProperty("controlGeneration").GetInt32() == generation,
            "Additive control generation serialized outside the unchanged observation schema");
        Check(wire.Length > 65536 && wire.Length < TcpControlServer.MaxResponseBytes, "Large observation fits new bounded framing");
        Check(json.RootElement.GetProperty("observation").GetProperty("entities").GetProperty("items").GetArrayLength() == 25,
            "More than twelve entities survive serialization");
        var metrics = json.RootElement.GetProperty("transportMetrics");
        Console.WriteLine($"MEASURE offline generated fixture: tiles=8160 entities=25 palette={terrain.Palette.Length} wireBytes={wire.Length} encodeMs={sampleMs:F3} serializeMs={metrics.GetProperty("serializationMilliseconds").GetDouble():F3}");
        byte[] oversized = TcpControlServer.SerializeFrame(new ControlReply(1, 5, true, "observation", "none",
            Observation: observation with { Player = new string('x', TcpControlServer.MaxResponseBytes) }));
        using var error = JsonDocument.Parse(oversized);
        Check(error.RootElement.GetProperty("status").GetString() == "response_too_large"
            && !error.RootElement.GetProperty("ok").GetBoolean(), "Explicit oversize error, no partial snapshot");
        Console.WriteLine("PASS: viewport transforms, bounds/polygon, lossless tile encoding, metadata, no-cap entities, on-demand reads, leases and large/oversized framing");
    }
}
