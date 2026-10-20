using System;
using System.Linq;
using System.Diagnostics;
using System.Text.Json;
using CompanionBridge;
using CompanionBridge.Motion;

public static class MotionOfflineTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    // Rectangular collision test double, NOT a substitute for Terraria collision integration.
    private sealed class World : IMotionWorld
    {
        internal float WallX = float.PositiveInfinity, WallWidth = 16, WallHeight = 32;
        internal bool Hazard, Unknown, LandingAllowed = true;
        internal float GapStart = float.PositiveInfinity, GapEnd = float.PositiveInfinity;
        internal float GapFloor = 1800;
        public PhysicsState Move(PhysicsState s, float vx, float vy, float gravity)
        {
            float x = s.Px + vx, y = s.Py + vy;
            bool overWall = x + 20 > WallX && x < WallX + WallWidth;
            if (overWall && s.Py + 42 > 1600 - WallHeight && s.Py < 1600)
            {
                x = vx >= 0 ? WallX - 20 : WallX + WallWidth;
                vx = 0;
            }
            float floor = x + 20 > GapStart && x < GapEnd ? GapFloor : 1600;
            overWall = x + 20 > WallX && x < WallX + WallWidth;
            if (overWall && s.Py + 42 <= 1600 - WallHeight + 0.01f) floor = 1600 - WallHeight;
            bool grounded = vy >= 0 && y + 42 >= floor;
            if (grounded) { y = floor - 42; vy = 0; }
            return new(x, y, vx, vy, grounded);
        }
        public bool SafeSweep(PhysicsState s, PhysicsState end, out string reason)
        {
            reason = Hazard ? "hazard" : Unknown ? "unknown" : "safe";
            return !Hazard && !Unknown;
        }
        public bool SafeLanding(PhysicsState s) => LandingAllowed && s.Grounded
            && !(s.Px < WallX + WallWidth && s.Px + 20 > WallX) && s.Py <= 1574;
    }

    public static void Run()
    {
        var ph = PhysicsParameters.Basic;
        var reported = new PhysicsParameters(0.08f, 3, 3, 0.2f, 0.4f, 10.01f, 5.01f, 15);
        Check(reported.IsBasic, "Real-game reported 10.01 fall cap passes the same eligibility check used in-game");
        Check(!(reported with { MaxFall = 10 }).IsBasic
            && !(reported with { MaxFall = 5.01f, Gravity = 0.2f }).IsBasic
            && !(reported with { MaxFall = 11 }).IsBasic, "Effective cap correction must not broaden parameter tolerance");
        var start = new PhysicsState(1600, 1558, 0, 0, true);
        var world = new World();
        var falling = PhysicsSimulator.Step(start with { Py = 1300, Vy = 9.9f, Grounded = false }, new(0, false), ph, world);
        Check(Math.Abs(falling.Vy - 10.01f) < 0.0001f, "Prediction clamps to the effective 10.01 cap");
        Check(!TerrainClassifier.IntersectsSolid('h', 0, 16, 0, 8)
            && TerrainClassifier.IntersectsSolid('h', 0, 16, 9, 16), "Half-block clearance retains empty upper half");
        Check(!TerrainClassifier.IntersectsSolid('2', 0, 4, 0, 10)
            && TerrainClassifier.IntersectsSolid('2', 0, 4, 13, 16), "Floor slope clearance uses triangle, not whole tile");
        Check(!TerrainClassifier.IntersectsSolid('3', 12, 16, 8, 16)
            && !TerrainClassifier.IntersectsSolid('=', 0, 16, 0, 16), "Ceiling empty corner and one-way platform clearance");
        var first = PhysicsSimulator.Step(start, new(1, true), ph, world);
        Check(Math.Abs(first.Vx - 0.08f) < 0.0001f, "1.4.4 primary acceleration bypasses weak acceleration/friction");
        Check(Math.Abs(first.Vy + 4.61f) < 0.0001f && first.JumpFramesLeft == 15,
            "Launch applies gravity and sets full 15-tick hold counter");
        var held = PhysicsSimulator.Step(first, new(1, true), ph, world);
        Check(Math.Abs(held.Vy + 4.61f) < 0.0001f && held.JumpFramesLeft == 14, "Hold maintains launch speed");
        var released = PhysicsSimulator.Step(first, new(1, false), ph, world);
        Check(Math.Abs(released.Vy + 4.21f) < 0.0001f && released.JumpFramesLeft == 0, "Release permits gravity arc");
        Check(Math.Abs(held.Vx - first.Vx - 0.08f) < 0.0001f, "Air acceleration has no weak ground increment");
        Check(PhysicsSimulator.Step(start with { ReleaseJump = false }, new(1, true), ph, world).Grounded,
            "Holding jump without release must not relaunch");
        float shortMin = 0, longMin = 0;
        foreach (int hold in new[] { 1, 16 })
        {
            var s = start;
            float min = start.Py;
            for (int f = 0; f < 80; f++) { s = PhysicsSimulator.Step(s, new(0, f < hold), ph, world); min = Math.Min(min, s.Py); }
            if (hold == 1) shortMin = min; else longMin = min;
        }
        Check(longMin < shortMin - 50, "Hold duration changes height substantially");
        Check(Math.Abs((start.Py - longMin) - 98.07f) < 0.1f, "Dry full-hold free-flight source-derived height");
        var walk = PhysicsSimulator.Predict(start, 1, 0, ph, world);
        Check(walk.Safe && !walk.Jump && walk.ExecuteFrames == 12, "Flat walk with safe braking");
        var slow = PhysicsSimulator.Predict(start, 1, 16, ph, world);
        var fast = PhysicsSimulator.Predict(start with { Vx = 2.8f }, 1, 16, ph, world);
        Check(fast.Safe && fast.Landing.Px > slow.Landing.Px, "Initial horizontal velocity affects landing");

        var controller = new LocalMotionController();
        world.WallX = 1624; // four pixels in front of the body, two tiles tall.
        Check(!PhysicsSimulator.Predict(start, 1, 0, ph, world).Safe, "Close two-tile wall blocks walking");
        var actual = start;
        for (int frame = 0; frame < 150 && !controller.Terminal; frame++)
        {
            var input = controller.Before(1, 1, actual, ph, world);
            actual = PhysicsSimulator.Step(actual, input, ph, world);
            controller.After(actual, world);
        }
        Check(controller.Snapshot.Status == "landed" && actual.Px > world.WallX + world.WallWidth,
            $"Predicted jump executes and lands beyond low obstacle: {controller.Snapshot}");
        Check(controller.Snapshot.ActualLanding != null && controller.Snapshot.MaxError < 0.001f,
            "Feedback reports actual landing and per-frame maximum error");
        var mirrored = new World { WallX = 1580 };
        controller = new(); actual = start;
        for (int frame = 0; frame < 150 && !controller.Terminal; frame++)
        {
            var input = controller.Before(10, -1, actual, ph, mirrored);
            actual = PhysicsSimulator.Step(actual, input, ph, mirrored);
            controller.After(actual, mirrored);
        }
        Check(controller.Snapshot.Status == "landed" && actual.Px + 20 <= mirrored.WallX,
            "Leftward low-obstacle prediction/execution is symmetric");
        controller = new(); actual = start;
        bool seenAir = false;
        int settled = 0;
        for (int frame = 0; frame < 150 && !controller.Terminal; frame++)
        {
            var input = controller.Before(11, 1, actual, ph, world);
            actual = PhysicsSimulator.Step(actual, input, ph, world);
            seenAir |= !actual.Grounded;
            settled = seenAir && actual.Grounded && Math.Abs(actual.Vx) < 0.01f ? settled + 1 : 0;
            controller.After(settled >= 2 ? actual with { Vx = 0.5f } : actual, world);
        }
        Check(controller.Snapshot.Status == "failed" && controller.Snapshot.Reason.Contains("landing mismatch"),
            "Residual real velocity cannot be reported as a settled successful landing");

        controller = new(); actual = start;
        controller.Before(2, 1, actual, ph, world);
        controller.After(actual with { Px = actual.Px - 40 }, world);
        Check(controller.Snapshot.Status == "failed" && controller.Snapshot.Reason.Contains("diverged"),
            "Large prediction error stops without retries");
        Check(controller.Snapshot.ObservedPosition != null && controller.Snapshot.ExpectedPosition != null,
            "Divergence feedback retains expected and observed frame state");
        controller = new(); world = new(); actual = start;
        for (int f = 0; f < 2; f++)
        {
            var input = controller.Before(20, 1, actual, ph, world);
            actual = PhysicsSimulator.Step(actual, input, ph, world);
            controller.After(actual with { Px = actual.Px + 8 }, world);
            Check(f != 0 || !controller.Terminal, "One moderate discrepancy can be transient");
        }
        Check(controller.Terminal, "Two moderate discrepancies stop execution");
        controller = new(); world.Hazard = true;
        Check(controller.Before(3, 1, start, ph, world) == default && controller.Terminal, "Immediate hazard stops");
        world.Hazard = false; world.Unknown = true; controller = new();
        Check(controller.Before(4, 1, start, ph, world) == default && controller.Terminal, "Unknown terrain stops");
        world.Unknown = false; world.LandingAllowed = false; controller = new();
        controller.Before(5, 1, start, ph, world);
        Check(controller.Terminal, "No safe landing stops");
        world = new World { GapStart = 1628, GapEnd = 2000 };
        controller = new(); controller.Before(6, 1, start, ph, world);
        Check(controller.Terminal, "Deep/wide pit cannot be accepted as a distant lower landing");
        world = new World(); controller = new(); actual = start;
        for (int f = 0; f < 60; f++)
        {
            var input = controller.Before(7, 1, actual, ph, world);
            Check(!input.Jump && input.Direction == 1, "Flat walk remains continuous across replans");
            actual = PhysicsSimulator.Step(actual, input, ph, world); controller.After(actual, world);
        }
        controller.Cancel();
        Check(controller.Snapshot.Status == "idle", "Cancellation discards plan and result");
        world = new World { GapStart = 1624, GapEnd = 1800, GapFloor = 1616 };
        var shallow = PhysicsSimulator.Predict(start, 1, 0, ph, world);
        Check(shallow.Safe && shallow.Frames[shallow.ExecuteFrames - 1].State.Grounded,
            "Walk prefix extends to supported phase instead of replanning mid-fall");
        world = new World();
        Check(Math.Abs(PhysicsSimulator.Step(start with { Vx = 3.04f }, new(1, false), ph, world).Vx - 2.84f) < 0.001f,
            "Ordinary cruise applies friction without an invented maxRun clamp");
        var state = new ControlState();
        int session = state.BeginSession();
        var request = ControlState.Parse("{\"v\":1,\"id\":1,\"cmd\":\"terrain_step\",\"direction\":1}");
        var applied = state.Enqueue(session, request, 0);
        state.Update(true, 0);
        Check(applied.Result.Ok && state.Input.Predictive, "Predictive request uses existing external lease");
        var snapshot = new PlayerSnapshot(true, new(1600, 1558), new(0, 0), 100, 100);
        var report = new NavigationSnapshot(session, "landed", "jump", "safe", 60, 8, new(1700, 1558), new(1701, 1558), 1,
            Generation: state.Generation);
        var query = state.Enqueue(session, new ControlRequest(2, "get_navigation"), 0);
        state.Update(true, Stopwatch.Frequency / 2, snapshot, navigation: report);
        Check(query.Result.Navigation == report && query.Result.State == snapshot, "Feedback is a game-thread DTO");
        Check(System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(query.Result)) < 4096,
            "Navigation result fits existing Python response limit");
        state.Update(true, Stopwatch.Frequency * 750 / 1000, snapshot, navigation: report);
        Check(!state.IsMoving, "Feedback query must not renew the 750ms lease");
        int expiredGeneration = state.Generation;
        state.Enqueue(session, new ControlRequest(3, "terrain_step", 1), Stopwatch.Frequency);
        state.Update(true, Stopwatch.Frequency, snapshot, navigation: report);
        Check(state.Generation != expiredGeneration, "New action after expiry gets a new generation");
        int renewedGeneration = state.Generation;
        // Expire and accept a fresh request in the SAME game update: no visible
        // None-mode hook occurs between them, but the old action must be invalidated.
        long later = Stopwatch.Frequency * 2;
        state.Enqueue(session, new ControlRequest(4, "terrain_step", 1), later);
        state.Update(true, later, snapshot, navigation: report);
        Check(state.Generation != renewedGeneration && state.Input.Predictive,
            "Expiry plus renewal in one tick cannot retain the old replay generation");
        var staleQuery = state.Enqueue(session, new ControlRequest(5, "get_navigation"), later);
        state.Update(true, later, snapshot, navigation: report);
        Check(staleQuery.Result.Navigation.Status == "idle", "Stale-generation landing feedback cannot report success for a new action");
        controller = new(); world = new();
        controller.Before(session, 1, start, ph, world, renewedGeneration);
        controller.Before(session, 1, start with { Grounded = false, Vy = -3 }, ph, world, state.Generation);
        Check(controller.Terminal && controller.Snapshot.Reason.Contains("requires ground"),
            "Expired airborne action is discarded instead of resuming its next frame");
        state.Reset();
        long manualNow = Stopwatch.Frequency * 3;
        state.ToggleManual(manualNow);
        var denied = state.Enqueue(session, new ControlRequest(6, "terrain_step", 1), manualNow);
        state.Update(true, manualNow, snapshot, navigation: report);
        Check(!denied.Result.Ok && state.Mode == ControlMode.Manual && !state.Input.Predictive,
            "Predictive requests cannot steal manual control");
        foreach (string invalid in new[] { "0", "2", "true", "1.5" })
        {
            bool rejected = false;
            try { ControlState.Parse("{\"v\":1,\"id\":4,\"cmd\":\"terrain_step\",\"direction\":" + invalid + "}"); }
            catch (FormatException) { rejected = true; }
            Check(rejected, "Invalid predictive direction rejected");
        }
        Console.WriteLine("PASS: adapted source physics, local candidates, low-obstacle execution/landing, divergence and safety (collision test double)");
    }
}
