using System;
using System.Collections.Generic;
using System.Linq;
using CompanionBridge.Motion;

public static class PlannerCorrectnessTests
{
    private static int checks;
    private static void Check(bool ok, string why) { if (!ok) throw new Exception(why); checks++; }
    private static readonly PhysicsParameters Ph = PhysicsParameters.Basic;
    private static readonly PhysicsState Start = new(1600, 1558, 0, 0, true);

    // Independent bounded Dijkstra oracle: retain exact physical states, or apply
    // the pinned pre-M4.1 cell/signed-speed policy for a reproducible comparison.
    // Same action vocabulary and safety predicates; no production planner calls.
    private static bool Reference(PhysicsState start, LandingRegion goal, IMotionWorld world, bool legacy)
    {
        var open = new PriorityQueue<(PhysicsState S, int G), int>();
        var seen = new List<(PhysicsState S, int G)> { (start, 0) };
        open.Enqueue((start, 0), 0);
        for (int expansions = 0; open.Count > 0 && expansions < 512; expansions++)
        {
            var cur = open.Dequeue();
            foreach (int dir in new[] { 1, -1 })
                for (int option = 0; option <= 8; option++)
                    foreach (int stride in option == 0 ? new[] { 24, 6 } : new[] { 24 })
                    {
                        var e = PhysicsSimulator.Predict(cur.S, dir, Math.Min(option * 2, Ph.JumpHeight), Ph,
                            world, walkFrames: stride, rollingJump: true);
                        if (!e.Safe) continue;
                        int execute = goal.Contains(e.Landing) || !PhysicsSimulator.SupportedCheckpoint(
                            e.Frames[e.ExecuteFrames - 1].State, world) ? e.Frames.Count : e.ExecuteFrames;
                        var next = e.Frames[execute - 1].State;
                        if (Math.Abs(next.Px - start.Px) > 192 || Math.Abs(next.Py - start.Py) > 128) continue;
                        int g = cur.G + execute + (e.Jump ? 20 : 0);
                        if (goal.Arrived(next, world)) return true; // Includes pruned feasible incumbents.
                        bool SameCell(PhysicsState a, PhysicsState b) => MathF.Floor((a.Px + 10) / 16) == MathF.Floor((b.Px + 10) / 16)
                            && MathF.Floor((a.Py + 41) / 16) == MathF.Floor((b.Py + 41) / 16) && a.Grounded == b.Grounded;
                        bool Dominates((PhysicsState S, int G) l) => l.G <= g && (legacy
                            ? SameCell(l.S, next) && MathF.Sign(l.S.Vx) == MathF.Sign(next.Vx)
                                && (int)(MathF.Abs(l.S.Vx) / .5f) >= (int)(MathF.Abs(next.Vx) / .5f)
                                && MathF.Abs(l.S.Vy - next.Vy) < .5f
                            : l.S == next);
                        if (seen.Any(Dominates)) continue;
                        if (legacy) seen.RemoveAll(l => SameCell(l.S, next) && l.G >= g
                            && Math.Abs(l.S.Vx) <= Math.Abs(next.Vx) + .01f
                            && Math.Sign(l.S.Vx) == Math.Sign(next.Vx) && Math.Abs(l.S.Vy - next.Vy) < .5f);
                        seen.Add((next, g)); open.Enqueue((next, g), g);
                    }
        }
        return false;
    }

    private static void Replay(Route route, PhysicsState start, IMotionWorld world)
    {
        var s = start;
        foreach (var edge in route.Actions)
        {
            foreach (var f in edge.Frames.Take(edge.ExecuteFrames))
            {
                var next = PhysicsSimulator.Step(s, f.Input, Ph, world);
                Check(next == f.State && world.SafeSweep(s, next, out _), "exact replay and safe sweep");
                s = next;
            }
            Check(s == edge.Landing && PhysicsSimulator.SupportedCheckpoint(s, world), "supported released checkpoint");
            // A moving checkpoint also carries a proved safe braking tail.
            Check(PhysicsSimulator.SupportedCheckpoint(edge.Frames[^1].State, world)
                && Math.Abs(edge.Frames[^1].State.Vx) < .01f, "safe stable tail");
        }
    }

    public static void Run()
    {
        var w = new MotionOfflineTests.RegionWorld();
        w.Collision.WallX = 1632; w.Collision.WallHeight = 32;
        var goal = new LandingRegion(1645, 1645.5f, 1568);
        Check(!Reference(Start, goal, w, true), "legacy cell policy loses the narrow higher landing");
        Check(Reference(Start, goal, w, false), "exact-state reference finds the same physical goal");
        var route = StateSpacePlanner.Plan(Start, goal, Ph, w, maxExpansions: 256, budgetMs: 5000);
        Check(route.ReachedGoal && goal.Arrived(route.Actions[^1].Landing, w), "production preserves feasible incumbent");
        var bounded = StateSpacePlanner.Plan(Start, goal, Ph, w, budgetMs: 5000);
        Check(!bounded.ReachedGoal && bounded.SearchExit == "expansion_budget", "default 32 expansions still cannot prove this narrow goal");
        Replay(route, Start, w);
        Console.WriteLine($"M4.1 narrow higher landing: expansions={route.Expansions}, ms={route.Milliseconds:F3}, exit={route.SearchExit}, edges={route.Actions.Count}");

        // Same cell and velocity bucket, distinct reachable jump checkpoints.
        var a = PhysicsSimulator.Predict(Start, 1, 8, Ph, w, walkFrames: 24, rollingJump: true);
        var b = PhysicsSimulator.Predict(Start, 1, 10, Ph, w, walkFrames: 24, rollingJump: true);
        var sa = a.Frames[a.ExecuteFrames - 1].State; var sb = b.Frames[b.ExecuteFrames - 1].State;
        Check(a.Safe && b.Safe && MathF.Floor((sa.Px + 10) / 16) == MathF.Floor((sb.Px + 10) / 16)
            && (int)(sa.Vx / .5f) == (int)(sb.Vx / .5f) && sa != sb, "reachable same-cell states are not equivalent");
        var flat = new MotionOfflineTests.World();
        var arrived = new NavigationTask(); arrived.Before(1, 1, 1612, Start, Ph, flat);
        Check(arrived.Snapshot.Status == "arrived" && StateSpacePlanner.SearchEstimate(Start, 1612, 1, null, Ph) > 0,
            "horizontal guidance is provably non-admissible at the accepted arrival band");
        Check(PhysicsSimulator.Step(Start with { Vx = 2.99f }, new(1, false), Ph, flat).Vx > Ph.MaxRun,
            "MaxRun is not a strict speed cap even before native step corrections");
        Check(StateSpacePlanner.SearchEstimate(Start, 1645, 1, goal, Ph) == 1
            && StateSpacePlanner.SearchEstimate(route.Actions[^1].Landing, 1645, 1, goal, Ph) == 0,
            "region indicator lower bound: outside needs >=1 positive-cost edge, inside zero");
        var brakeA = PhysicsSimulator.Predict(sa, 0, 0, Ph, flat, walkFrames: 0);
        var brakeB = PhysicsSimulator.Predict(sb, 0, 0, Ph, flat, walkFrames: 0);
        Check(brakeA.Safe && brakeB.Safe && Math.Abs(brakeA.Landing.Px - brakeB.Landing.Px) > 8, "different braking successors");
        var wall = new MotionOfflineTests.World { WallX = 1632, WallHeight = 200 };
        var slow = PhysicsSimulator.Predict(Start with { Vx = .8f }, 1, 0, Ph, wall, walkFrames: 6);
        var fast = PhysicsSimulator.Predict(Start with { Vx = 3 }, 1, 0, Ph, wall, walkFrames: 6);
        Check(slow.Safe && !fast.Safe, "faster state cannot dominate safe slow approach before wall");

        var zero = StateSpacePlanner.Plan(Start, 1800, Ph, flat, maxExpansions: 0, budgetMs: 5000);
        Check(zero.SearchExit == "expansion_budget" && !zero.ReachedGoal && !zero.TimeBudgetReached
            && zero.BlockingTiles == null && zero.Actions.Count == 0, "zero expansion is budget, not wall/no-route");
        var time = StateSpacePlanner.Plan(Start, 1800, Ph, flat, budgetMs: 0);
        Check(time.SearchExit == "time_budget" && time.TimeBudgetReached && time.Actions.Count == 0, "zero deadline");
        var partial = StateSpacePlanner.Plan(Start, 1800, Ph, flat, maxExpansions: 1, budgetMs: 5000);
        Check(partial.SearchExit == "expansion_budget" && partial.Actions.Count > 0 && !partial.ReachedGoal, "budget partial remains partial");
        Replay(partial, Start, flat);
        foreach (bool unknown in new[] { false, true })
        {
            var unsafeWorld = new MotionOfflineTests.World { Unknown = unknown, Hazard = !unknown };
            var rejected = StateSpacePlanner.Plan(Start, 1800, Ph, unsafeWorld, budgetMs: 5000);
            Check(rejected.SearchExit == "local_frontier_exhausted" && rejected.Actions.Count == 0
                && rejected.Rejections.Keys.Any(k => k.Contains(unknown ? "unknown" : "hazard")), "preserve unsafe rejection facts");
        }
        foreach (var invalid in new[] { Start with { Grounded = false }, Start with { Vy = .2f },
            Start with { ReleaseJump = false }, Start with { JumpFramesLeft = 1 } })
            Check(!PhysicsSimulator.SupportedCheckpoint(invalid, flat), "checkpoint flags cannot be discarded");
        Console.WriteLine($"M4.1 planner correctness: {checks} checks (collision fixtures, not native execution).");
    }
}
