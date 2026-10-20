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
        internal bool Hazard, Unknown, LandingAllowed = true, AllowWallLanding;
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
            && (!(s.Px < WallX + WallWidth && s.Px + 20 > WallX)
                || (AllowWallLanding && s.Py <= 1558 - WallHeight + 0.01f
                    && s.Px >= WallX && s.Px + 20 <= WallX + WallWidth)) && s.Py <= 1574;
    }

    private sealed class TwoObstacles : IMotionWorld
    {
        private readonly World _first = new() { WallX = 1624, WallWidth = 32 };
        private readonly World _second = new() { WallX = 1976, WallWidth = 32 };
        internal TwoObstacles(bool allowWallLanding = false)
        {
            _first.AllowWallLanding = _second.AllowWallLanding = allowWallLanding;
        }
        private World At(PhysicsState s) => s.Px > 1700 ? _second : _first;
        public PhysicsState Move(PhysicsState s, float vx, float vy, float gravity)
            => At(s).Move(s, vx, vy, gravity);
        public bool SafeSweep(PhysicsState s, PhysicsState end, out string reason)
            => At(s).SafeSweep(s, end, out reason);
        public bool SafeLanding(PhysicsState s) => At(s).SafeLanding(s);
    }

    private static void SequentialJumps()
    {
        var world = new TwoObstacles();
        var controller = new LocalMotionController();
        var actual = new PhysicsState(1600, 1558, 0, 0, true);
        int generation = 1, jumps = 0;
        for (int f = 0; f < 600 && jumps < 2; f++)
        {
            var input = controller.Before(1, 1, actual, PhysicsParameters.Basic, world, generation);
            actual = PhysicsSimulator.Step(actual, input, PhysicsParameters.Basic, world);
            controller.After(actual, world);
            Check(controller.Snapshot.Status != "failed", "Sequential obstacle must not fail: " + controller.Snapshot.Reason);
            if (controller.Snapshot.Status == "landed")
            {
                jumps++;
                Check(actual.Grounded && Math.Abs(actual.Vx) <= 0.1f, "Verify settled landing before restart");
                Check(controller.Before(1, 1, actual, PhysicsParameters.Basic, world, generation) == default
                    && controller.Snapshot.Status == "landed", "Renewal cannot restart terminal action");
                controller.Cancel();
                generation += 2; // Guarded stop, then fresh start in the same connection.
            }
        }
        Check(jumps == 2 && actual.Px > 2008, "Two separated 2x2 obstacles execute in successive generations");
    }

    private static void NavigationRegression()
    {
        var ph = PhysicsParameters.Basic;
        var start = new PhysicsState(1600, 1558, 0, 0, true);
        var world = new TwoObstacles();
        var route = StateSpacePlanner.Plan(start, 2080, ph, world, budgetMs: 500);
        Check(route.Actions.Count >= 2 && route.Actions.Any(a => a.Jump),
            "State-space route must carry multiple simulated actions including a jump");
        for (int dir = -1; dir <= 1; dir += 2)
        {
            IMotionWorld scene = dir == 1 ? world : new World { WallX = 1544, WallWidth = 32 };
            var task = new NavigationTask();
            var actual = start;
            float goal = dir == 1 ? 2080 : 1400;
            int maxPlanned = 0;
            for (int f = 0; f < 1800 && !task.Terminal; f++)
            {
                var input = task.Before(80, 5, goal, actual, ph, scene);
                actual = PhysicsSimulator.Step(actual, input, ph, scene);
                task.After(actual, scene);
                maxPlanned = Math.Max(maxPlanned, task.Snapshot.PlannedActions);
                Check(task.Snapshot.Status != "failed", "Continuous navigation: " + task.Snapshot.Reason);
            }
            Check(task.Snapshot.Status == "arrived" && dir * (actual.Px - goal) >= -12,
                "Task must reach horizontal goal without Python action restarts");
            Check(task.Snapshot.CompletedJumps >= (dir == 1 ? 2 : 1) && task.Snapshot.CompletedActions >= 2,
                "One generation executes and verifies consecutive actions/obstacles");
            Check(task.Snapshot.LastLanding?.ActionNumber == task.Snapshot.CompletedActions
                && Math.Abs(task.Snapshot.LastLanding.Actual.X - task.Snapshot.LastLanding.Predicted.X) <= 8,
                "Last verified landing remains available after the following replan/arrival");
            Check(System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(task.Snapshot)) < 3500,
                "Task feedback including persistent landing fits the IPC response limit");
            Check(dir != 1 || maxPlanned >= 2, "Search looks beyond its first executable edge");
            Check(task.Before(80, 5, goal, actual, ph, scene) == default,
                "Lease renewals cannot restart an arrived task");
            task.Cancel();
            Check(task.Snapshot.Status == "idle", "Cancellation drops autonomous task");
        }
        var hazard = new World { Hazard = true };
        var failed = new NavigationTask();
        failed.Before(81, 6, 1800, start, ph, hazard);
        Check(failed.Snapshot.Status == "failed", "Hazards do not produce a route");
        Check(failed.Snapshot.Expansions > 0 && failed.Snapshot.Reason.Contains("hazard"),
            "Failed planning reports actual search counters and candidate rejection reasons");
        hazard.Hazard = false;
        Check(failed.Before(81, 6, 1800, start, ph, hazard) == default && failed.Terminal,
            "Failed task stays terminal until explicitly restarted");
        var deviated = new NavigationTask();
        deviated.Before(82, 7, 1800, start, ph, hazard);
        deviated.After(start with { Px = 1500 }, hazard);
        Check(deviated.Snapshot.Status == "failed", "Real-state deviation aborts autonomous task");
        var protocol = new ControlState();
        int session = protocol.BeginSession();
        var request = ControlState.Parse("{\"v\":1,\"id\":1,\"cmd\":\"navigate\",\"targetX\":2080,\"expectedGeneration\":0}");
        var ack = protocol.Enqueue(session, request, 0); protocol.Update(true, 0);
        Check(ack.Result.Ok && protocol.Input.TargetX == 2080 && protocol.Input.Predictive,
            "Navigation enters the existing guarded predictive lease");
        int generation = protocol.Generation;
        protocol.Enqueue(session, request with { Id = 2, ExpectedGeneration = generation }, 0);
        protocol.Update(true, 0);
        Check(protocol.Generation == generation, "Identical target renewal preserves task identity");
        protocol.Enqueue(session, request with { Id = 3, ExpectedGeneration = generation, TargetX = 2200 }, 0);
        protocol.Update(true, 0);
        Check(protocol.Generation != generation, "Changed target invalidates previous route");
        var stale = protocol.Enqueue(session, request with { Id = 4, ExpectedGeneration = generation }, 0);
        protocol.Update(true, 0);
        Check(!stale.Result.Ok, "Stale task cannot resume after target change");
        protocol.Update(true, Stopwatch.Frequency);
        Check(!protocol.IsMoving, "Autonomous navigation retains the 750ms deadman lease");
        foreach (string invalid in new[] { "null", "true", "-1", "1000001", "1e100" })
        {
            bool rejected = false;
            try { ControlState.Parse("{\"v\":1,\"id\":8,\"cmd\":\"navigate\",\"targetX\":" + invalid + "}"); }
            catch (FormatException) { rejected = true; }
            Check(rejected, "Invalid navigation target rejected before game-thread dispatch");
        }
        Console.WriteLine("PASS: TerraBlind state-space route, continuous multi-obstacle navigation, arrival, cancellation, divergence and guarded lease");
    }

    private static void SmoothWalkRegression()
    {
        var ph = PhysicsParameters.Basic;
        var start = new PhysicsState(1600, 1558, 0, 0, true);
        foreach (int dir in new[] { -1, 1 })
        {
            var world = new World();
            var task = new NavigationTask();
            var actual = start;
            float goal = start.Px + dir * 640;
            int movingBoundaries = 0, previousActions = 0;
            for (int f = 0; f < 1000 && !task.Terminal; f++)
            {
                var input = task.Before(90, 1, goal, actual, ph, world);
                if (dir * (goal - actual.Px) > 80)
                    Check(input == new PhysicsInput(dir, false), "Open flat approach walks without neutral or jump frames");
                actual = PhysicsSimulator.Step(actual, input, ph, world);
                task.After(actual, world);
                Check(task.Snapshot.Status != "failed", "Smooth flat navigation: " + task.Snapshot.Reason);
                if (task.Snapshot.CompletedActions != previousActions)
                {
                    if (dir * (goal - actual.Px) > 80)
                    {
                        Check(dir * actual.Vx > 1, "Walk checkpoint must retain forward speed");
                        Check(task.Snapshot.LastLanding.Settled == false
                            && Math.Abs(task.Snapshot.LastLanding.ActualVelocity.X - actual.Vx) < 0.001f,
                            "Feedback distinguishes moving checkpoints from settled landings");
                        movingBoundaries++;
                    }
                    previousActions = task.Snapshot.CompletedActions;
                }
            }
            Check(task.Snapshot.Status == "arrived" && Math.Abs(actual.Vx) <= 0.1f
                && task.Snapshot.CompletedJumps == 0 && movingBoundaries >= 3,
                "Flat route crosses several moving checkpoints without jumps, then settles at arrival");
        }
        var shallow = new World { GapStart = 1712, GapEnd = 1760, GapFloor = 1616 };
        var pitTask = new NavigationTask();
        var pitState = start;
        for (int f = 0; f < 800 && !pitTask.Terminal; f++)
        {
            var input = pitTask.Before(91, 1, 1940, pitState, ph, shallow);
            Check(!input.Jump, "Walkable three-tile-wide, one-tile-deep depression should not trigger a jump");
            pitState = PhysicsSimulator.Step(pitState, input, ph, shallow);
            pitTask.After(pitState, shallow);
        }
        Check(pitTask.Snapshot.Status == "arrived" && pitTask.Snapshot.CompletedJumps == 0,
            "Shallow depression remains traversable without jumping (collision test double)");
        var partial = StateSpacePlanner.Plan(start, 2300, ph, new World(), maxExpansions: 1, budgetMs: 500);
        Check(partial.Actions.Count > 0 && !partial.Actions[0].Jump,
            "Budget-limited partial fallback must retain walk preference, not select the farthest jump");
        var blocked = new World { WallX = 1690, WallWidth = 32 };
        var approach = StateSpacePlanner.Plan(start with { Vx = 2.8f }, 2000, ph, blocked, budgetMs: 500);
        Check(approach.Actions.Count > 0 && !approach.Actions[0].Jump,
            "A safe short walking approach is available before jumping a distant obstacle");
        Console.WriteLine("PASS: bidirectional moving walk checkpoints, settled arrival, walkable shallow pit, cost-aware partial route and obstacle approach");
    }

    private sealed class SettledOnlyWorld : IMotionWorld
    {
        private readonly World _world = new();
        public PhysicsState Move(PhysicsState s, float vx, float vy, float gravity) => _world.Move(s, vx, vy, gravity);
        public bool SafeSweep(PhysicsState s, PhysicsState end, out string reason) => _world.SafeSweep(s, end, out reason);
        public bool SafeLanding(PhysicsState s) => _world.SafeLanding(s) && Math.Abs(s.Vx) <= 0.1f;
    }

    private static void RollingJumpRegression()
    {
        var ph = PhysicsParameters.Basic;
        var start = new PhysicsState(1600, 1558, 0, 0, true);
        foreach (int dir in new[] { -1, 1 })
        {
            IMotionWorld world = dir == 1 ? new TwoObstacles() : new World { WallX = 1544, WallWidth = 32 };
            var task = new NavigationTask();
            var actual = start;
            float goal = dir == 1 ? 2250 : 1200;
            int previousActions = 0, movingJumps = 0;
            for (int f = 0; f < 1500 && !task.Terminal; f++)
            {
                var input = task.Before(95, 1, goal, actual, ph, world);
                if (dir * (goal - actual.Px) > 180)
                    Check(input.Direction == dir, "Walk/jump transitions must retain forward input away from the goal");
                actual = PhysicsSimulator.Step(actual, input, ph, world);
                task.After(actual, world);
                Check(task.Snapshot.Status != "failed", "Rolling jump task: " + task.Snapshot.Reason);
                if (task.Snapshot.CompletedActions != previousActions)
                {
                    var landing = task.Snapshot.LastLanding;
                    if (landing.Jump && dir * (goal - actual.Px) > 180)
                    {
                        Check(!landing.Settled && dir * landing.ActualVelocity.X > 1
                            && actual.ReleaseJump && actual.JumpFramesLeft == 0,
                            "Moving jump handoff verifies speed, support and jump release before replan");
                        movingJumps++;
                    }
                    previousActions = task.Snapshot.CompletedActions;
                }
            }
            Check(task.Snapshot.Status == "arrived" && Math.Abs(actual.Vx) <= 0.1f
                && movingJumps >= (dir == 1 ? 2 : 1), "Consecutive moving jumps still end in a settled arrival");
        }

        var flat = new World();
        var rolling = PhysicsSimulator.Predict(start, 1, 12, ph, flat, rollingJump: true);
        Check(rolling.Safe && rolling.ExecuteFrames < rolling.Frames.Count,
            "Moving landing retains its separately validated braking tail");
        Check(rolling.Frames[rolling.ExecuteFrames - 1].State.Grounded
            && rolling.Frames[rolling.ExecuteFrames - 2].State.Grounded,
            "Handoff includes two consecutive supported landing frames");
        var legacy = PhysicsSimulator.Predict(start, 1, 12, ph, flat);
        Check(legacy.Safe && legacy.ExecuteFrames == legacy.Frames.Count && Math.Abs(legacy.Landing.Vx) <= 0.1f,
            "Single-jump experiment preserves its settled landing contract");
        var narrow = PhysicsSimulator.Predict(start, 1, 12, ph, new SettledOnlyWorld(), rollingJump: true);
        Check(narrow.Safe && narrow.ExecuteFrames == narrow.Frames.Count,
            "Unsupported moving handoff falls back to the full settled trajectory");

        var jumpWorld = new World { WallX = 1624, WallWidth = 32 };
        var route = StateSpacePlanner.Plan(start, 2200, ph, jumpWorld, budgetMs: 500);
        var edge = route.Actions[0];
        Check(edge.Jump && edge.ExecuteFrames < edge.Frames.Count, "Close obstacle dispatches a moving jump");
        for (int mismatch = 0; mismatch < 3; mismatch++)
        {
            var executor = new LocalMotionController();
            executor.Begin(96, 1); executor.LoadPlan(edge);
            var actual = start;
            for (int f = 0; f < edge.ExecuteFrames && !executor.Terminal; f++)
            {
                var input = executor.Before(96, 1, actual, ph, jumpWorld);
                actual = PhysicsSimulator.Step(actual, input, ph, jumpWorld);
                if (mismatch == 2 && f == edge.ExecuteFrames - 2)
                    actual = actual with { Grounded = false };
                if (f == edge.ExecuteFrames - 1)
                    actual = mismatch == 1 ? actual with { ReleaseJump = false }
                        : mismatch == 0 ? actual with { Vx = actual.Vx + 0.5f } : actual;
                executor.After(actual, jumpWorld);
            }
            Check(executor.Snapshot.Status == "failed", "Moving landing rejects mismatched speed, unreleased jump or transient support");
        }
        Console.WriteLine("PASS: bidirectional consecutive moving jumps, continuous forward input, braking fallback, legacy settled jump and handoff mismatch rejection");
    }

    private static void RaisedDepartureRegression()
    {
        var ph = PhysicsParameters.Basic;
        var top = new PhysicsState(1606, 1526, 0, 0, true);
        foreach (int dir in new[] { -1, 1 })
        {
            var world = new World { WallX = 1600, WallWidth = 32, AllowWallLanding = true };
            Check(world.SafeLanding(top), "Player may stand safely on a two-tile-high obstacle");
            Check(!PhysicsSimulator.Predict(top, dir, 0, ph, world).Safe,
                "Legacy one-tile descent restriction reproduces obstacle-top departure rejection");
            var task = new NavigationTask();
            var actual = top;
            float goal = top.Px + dir * 200;
            for (int f = 0; f < 800 && !task.Terminal; f++)
            {
                var input = task.Before(100, 1, goal, actual, ph, world);
                actual = PhysicsSimulator.Step(actual, input, ph, world);
                task.After(actual, world);
                Check(task.Snapshot.Status != "failed", "Obstacle-top departure: " + task.Snapshot.Reason);
            }
            Check(task.Snapshot.Status == "arrived" && Math.Abs(actual.Py - 1558) < 0.01f,
                "Navigation leaves the raised obstacle and reaches ordinary lower ground");
        }
        var chained = new TwoObstacles(allowWallLanding: true);
        var chainTask = new NavigationTask();
        var chainState = new PhysicsState(1600, 1558, 0, 0, true);
        bool landedOnTop = false;
        for (int f = 0; f < 1400 && !chainTask.Terminal; f++)
        {
            var input = chainTask.Before(101, 1, 2250, chainState, ph, chained);
            chainState = PhysicsSimulator.Step(chainState, input, ph, chained);
            chainTask.After(chainState, chained);
            landedOnTop |= chainState.Grounded && Math.Abs(chainState.Py - 1526) < 0.01f;
            Check(chainTask.Snapshot.Status != "failed", "Raised landing continuation: " + chainTask.Snapshot.Reason);
        }
        Check(landedOnTop && chainTask.Snapshot.CompletedJumps >= 2 && chainTask.Snapshot.Status == "arrived",
            "Route may land on an obstacle, descend and clear a subsequent obstacle without restarting");
        var deep = new World { GapStart = 1624, GapEnd = 2000, GapFloor = 1680 };
        var rejected = PhysicsSimulator.Predict(new PhysicsState(1600, 1558, 0, 0, true), 1, 15,
            ph, deep, maxDropPixels: StateSpacePlanner.MaxDropPixels);
        Check(!rejected.Safe && rejected.Reason.Contains("64px"),
            "Navigation descent remains bounded; an eighty-pixel fall is rejected");
        Console.WriteLine("PASS: bidirectional obstacle-top departure, raised landing then another obstacle, bounded descent and rejection diagnostics");
    }

    private static void NativeSupportRegression()
    {
        // Actual get_terrain capture: ground row 274, two 2x2 blocks at
        // columns 2120..2121 and 2132..2133; player Y=4310 on their tops.
        char Tile(int x, int y) => y >= 274 ? '#'
            : y is 272 or 273 && (x is 2120 or 2121 or 2132 or 2133) ? '#' : '.';
        foreach (float px in new[] { 33923.72f, 34108.727f })
        {
            var actual = new PhysicsState(px, 4310, 0, 0, true);
            Check(Tile((int)Math.Floor((px - 4) / 16), 272) == '.',
                "Real reported stand position intersects an unsupported padded column");
            Check(MotionSupport.SafeLanding(actual, Tile),
                "Shared native support checker accepts both reported obstacle-top positions");
        }
        Check(MotionSupport.SafeLanding(new(34112, 4310, 0, 0, true), Tile), "Full support still accepted");
        Check(MotionSupport.SafeLanding(new(34136, 4310, 0, 0, true), Tile), "Eight pixels of real contact accepted");
        Check(!MotionSupport.SafeLanding(new(34143, 4310, 0, 0, true), Tile), "One-pixel corner support rejected");
        Check(!MotionSupport.SafeLanding(new(34145, 4310, 0, 0, true), Tile), "Air with a stale grounded flag rejected");
        Check(!MotionSupport.SafeLanding(new(34112, 4310, 0, 0, false), Tile), "Airborne state rejected");
        Check(!MotionSupport.SafeLanding(new(34112, 4326, 0, 0, true), Tile), "Body embedded in block rejected");
        foreach (char danger in new[] { '?', '!' })
            Check(!MotionSupport.SafeLanding(new(34112, 4310, 0, 0, true),
                (x, y) => x == 2132 && y == 272 ? danger : Tile(x, y)), "Unknown or harmful support rejected");
        Check(MotionSupport.SafeLanding(new(1600, 1550, 0, 0, true), (_, y) => y == 99 ? 'h' : '.'),
            "Half-block support retains shape-aware height");
        Console.WriteLine("PASS: actual reported obstacle-edge coordinates through shared native support logic; corner, air, collision, hazard and half-block rejection/acceptance");
    }

    private sealed class ClippedContactWorld : IMotionWorld
    {
        private readonly World _world = new();
        public PhysicsState Move(PhysicsState s, float vx, float vy, float gravity)
        {
            var next = _world.Move(s, vx, vy, gravity);
            // Native collision may shorten downward movement without zeroing it.
            // The simulator's contact flag can precede TerrainReader's grounded
            // observation, which requires zero Vy on ordinary flat ground.
            if (next.Grounded && s.Py < 1558 && next.Py > s.Py)
                next = next with { Vy = next.Py - s.Py };
            return next;
        }
        public bool SafeSweep(PhysicsState s, PhysicsState end, out string reason) => _world.SafeSweep(s, end, out reason);
        public bool SafeLanding(PhysicsState s) => _world.SafeLanding(s);
    }

    private static void LandingContactTimingRegression()
    {
        var world = new ClippedContactWorld();
        var ph = PhysicsParameters.Basic;
        var start = new PhysicsState(1600, 1526, 0, 0, true);
        var raw = PhysicsSimulator.Predict(start, 1, 2, ph, world, rollingJump: true, maxDropPixels: 64);
        Check(raw.Safe && raw.ExecuteFrames < raw.Frames.Count, "Raised jump retains a rolling handoff");
        var edge = raw with { Landing = raw.Frames[raw.ExecuteFrames - 1].State };
        var controller = new LocalMotionController();
        controller.Begin(110, 1); controller.LoadPlan(edge);
        var actual = start;
        for (int f = 0; f < edge.ExecuteFrames && !controller.Terminal; f++)
        {
            var input = controller.Before(110, 1, actual, ph, world);
            actual = PhysicsSimulator.Step(actual, input, ph, world);
            actual = actual with { Grounded = actual.Grounded && Math.Abs(actual.Vy) < 0.01f };
            controller.After(actual, world);
        }
        Check(controller.Snapshot.MaxError < 0.001f, "Contact flags may differ despite matching real position and velocity");
        Check(controller.Snapshot.Status == "landed", "Partial contact must not prematurely exhaust two-frame landing handoff");
        Check(raw.Frames.Take(raw.ExecuteFrames).All(f => f.Input.Direction == 1),
            "Forward input remains held through partial contact and full landing");
        Console.WriteLine("PASS: clipped downward contact before true grounding, two-frame landing handoff and uninterrupted input");
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
        SequentialJumps();
        NavigationRegression();
        SmoothWalkRegression();
        RollingJumpRegression();
        RaisedDepartureRegression();
        NativeSupportRegression();
        LandingContactTimingRegression();
        Console.WriteLine("PASS: adapted source physics, local candidates, single/sequential low-obstacle landing, divergence and safety (collision test double)");
    }
}
