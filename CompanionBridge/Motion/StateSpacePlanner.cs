// Adapted from TerraBlind Nav/StateSpacePlanner.cs (MIT), pinned in NOTICE.md.
// Retains physics-state A*, velocity labels, came-from action frames and grounded
// partial routes. World-changing edges and the maze field are deliberately omitted.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace CompanionBridge.Motion;

// ReachedGoal includes feasible incumbents at budget exhaustion, not a claim of
// optimality. TimeBudgetReached records a checked deadline exit (not preemption).
internal sealed record Route(List<Prediction> Actions, int Expansions, double Milliseconds, string FailureReason = null,
    bool ReachedGoal = false, bool TimeBudgetReached = false,NavigationBlockingTile[] BlockingTiles=null,string Kind="state_space");

internal static class StateSpacePlanner
{
    internal const float MaxDropPixels = 64;
    private readonly record struct CellKey(int X, int Y, bool Grounded);
    private readonly record struct Label(float G, float Vx, float Vy);
    private static CellKey Cell(PhysicsState s) => new(
        (int)MathF.Floor((s.Px + 10) / 16), (int)MathF.Floor((s.Py + 41) / 16), s.Grounded);

    // Upstream Dominated: cheaper labels with at least the same signed speed bucket
    // dominate; retaining velocity variants matters when traversing uneven support.
    private static bool Dominated(List<Label> labels, float g, PhysicsState s)
    {
        int bucket = (int)(MathF.Abs(s.Vx) / 0.5f);
        foreach (var l in labels)
            if (l.G <= g + 0.01f && MathF.Sign(l.Vx) == MathF.Sign(s.Vx)
                && (int)(MathF.Abs(l.Vx) / 0.5f) >= bucket && MathF.Abs(l.Vy - s.Vy) < 0.5f)
                return true;
        return false;
    }

    // A horizontal crossing goal, within a rolling local horizon. Candidates prove
    // a safe braking tail, but can hand off on supported ground with residual speed.
    internal static Route Plan(PhysicsState start, float targetX, PhysicsParameters ph,
        IMotionWorld world, int maxExpansions = 32, double budgetMs = 25, int? goalDirection = null)
        => PlanCore(start, targetX, null, ph, world, maxExpansions, budgetMs, goalDirection);

    internal static Route Plan(PhysicsState start, LandingRegion region, PhysicsParameters ph,
        IMotionWorld world, int maxExpansions = 32, double budgetMs = 25)
        => PlanCore(start, Math.Clamp(start.Px, region.MinPlayerLeftX, region.MaxPlayerLeftX),
            region, ph, world, maxExpansions, budgetMs, null);

    private static Route PlanCore(PhysicsState start, float targetX, LandingRegion region, PhysicsParameters ph,
        IMotionWorld world, int maxExpansions, double budgetMs, int? goalDirection)
    {
        var watch = Stopwatch.StartNew();
        var rejected = new Dictionary<string, int>();
        NavigationBlockingTile[] rootBlocks=null;
        void Reject(string reason) => rejected[reason] = rejected.GetValueOrDefault(reason) + 1;
        float goal = start.Px + Math.Clamp(targetX - start.Px, -128, 128);
        bool finalGoal = goal == targetX;
        // Preserve the task's crossing direction after reaching/passing targetX;
        // recomputing it from the current position would reverse an overshot goal.
        int sign = goalDirection ?? (targetX >= start.Px ? 1 : -1);
        // Region H is the indicator of geometric non-membership (X OR feet Y).
        // Every edge costs >= one executed frame, jumps add 20. Any path from
        // outside the geometry needs at least one edge, so H <= remaining G.
        // Inside geometry H=0 even if settling/support is still needed. This also
        // stays consistent across native StepUp/Down positional corrections;
        // no unproved horizontal/vertical speed bound is used for region goals.
        float H(PhysicsState s) => region != null ? (region.Contains(s) ? 0 : 1)
            : Math.Max(0, sign * (goal - s.Px)) / ph.MaxRun;
        float Distance(PhysicsState s) => region?.Distance(s) ?? H(s);
        bool Reached(PhysicsState s) => region != null ? region.Arrived(s, world)
            : H(s) <= 4 && (!finalGoal || Math.Abs(s.Vx) <= 0.1f)
                && s.Grounded && world.SafeLanding(s);
        if (region != null ? region.Contains(start) : sign * (targetX - start.Px) <= 12)
        {
            // Distance alone is not a terminal physical state. Use the same
            // bounded simulation, sweep/drop/landing checks and stable tail as
            // other actions, but release direction rather than accelerate again.
            var brake = PhysicsSimulator.Predict(start, 0, 0, ph, world,
                walkFrames: 0, maxDropPixels: MaxDropPixels);
            if (brake.Safe && (region != null ? region.Arrived(brake.Landing, world)
                    : sign * (targetX - brake.Landing.Px) <= 12)
                && Math.Abs(brake.Landing.Vx) <= 0.1f)
                return new(new() { brake }, 0, watch.Elapsed.TotalMilliseconds, ReachedGoal: true);
            if (!brake.Safe) Reject("braking: " + brake.Reason);
        }
        var labels = new Dictionary<CellKey, List<Label>>();
        var came = new Dictionary<PhysicsState, (PhysicsState Prev, Prediction Edge, float G)>();
        var open = new PriorityQueue<PhysicsState, float>();
        labels[Cell(start)] = new() { new(0, start.Vx, start.Vy) };
        came[start] = (start, null, 0);
        open.Enqueue(start, H(start));
        PhysicsState best = start;
        float bestDistance = Distance(start);
        float bestScore = float.MaxValue;
        PhysicsState? complete = null;
        float completeCost = float.PositiveInfinity;
        bool reachedGoal = false;
        int expansions = 0;
        bool timedOut = false;
        while (open.Count > 0 && expansions < maxExpansions && !timedOut)
        {
            var cur = open.Dequeue();
            if (Reached(cur))
            { best = cur; reachedGoal = true; break; }
            expansions++;
            // Region goals also need an in-place ascent, e.g. a platform above
            // the player in a narrow shaft. Horizontal crossing keeps its existing
            // candidates; all variants use the same sweep, drop and braking proof.
            bool neutralAscent = region != null && cur.Px >= region.MinPlayerLeftX && cur.Px <= region.MaxPlayerLeftX
                && cur.Py + PhysicsSimulator.PlayerH - region.SurfaceY >= 4;
            foreach (int dir in neutralAscent ? new[] { 0, sign, -sign } : new[] { sign, -sign })
            {
                // BuildHoldOptions from upstream: walk then 2-frame hold increments.
                for (int option = 0; option <= (ph.JumpHeight + 1) / 2; option++)
                {
                    if (dir == 0 && option == 0) continue;
                    if (watch.Elapsed.TotalMilliseconds >= budgetMs) { timedOut = true; break; }
                    int hold = Math.Min(option * 2, ph.JumpHeight);
                    foreach (int stride in hold == 0 ? new[] { 24, 6 } : new[] { 24 })
                    {
                        if (watch.Elapsed.TotalMilliseconds >= budgetMs) { timedOut = true; break; }
                        var edge = PhysicsSimulator.Predict(cur, dir, hold, ph, world,
                            walkFrames: stride, rollingJump: true, maxDropPixels: MaxDropPixels);
                        if (!edge.Safe)
                        {
                            if(cur.Equals(start)&&dir==sign&&hold==0&&rootBlocks==null)rootBlocks=edge.BlockingTiles;
                            Reject(edge.Reason); continue;
                        }
                        // Predict still checks the complete braking/landing trajectory.
                        // Carry the observed prefix velocity into the next search node;
                        // The final approach or an unsafe handoff executes the tail.
                        bool settle = (region != null ? region.Contains(edge.Landing)
                                : sign * (targetX - edge.Landing.Px) <= 12)
                            || !PhysicsSimulator.SupportedCheckpoint(edge.Frames[edge.ExecuteFrames - 1].State,world);
                        int execute = settle ? edge.Frames.Count : edge.ExecuteFrames;
                        edge = edge with { ExecuteFrames = execute, Landing = edge.Frames[execute - 1].State,
                            Reason = settle ? edge.Reason : edge.Jump
                                ? "safe moving jump landing; braking tail validated"
                                : "safe walk prefix; braking tail validated" };
                        var next = edge.Landing;
                        if (Math.Abs(next.Px - start.Px) > 192 || Math.Abs(next.Py - start.Py) > 128)
                        { Reject("outside route span"); continue; }
                        // Prefer walking when it makes comparable progress. Braking at
                        // every walk boundary previously made flat-ground jumps cheaper.
                        float ng = came[cur].G + edge.ExecuteFrames + (edge.Jump ? 20 : 0);
                        // A bounded search may generate a complete landing long
                        // before its cost lets it reach the front of the queue.
                        // Preserve that feasible incumbent even if velocity/cell
                        // dominance would prune it or a cheaper partial route wins
                        // the fallback score. Continue A* normally; only the budget
                        // fallback uses an unproven-optimal complete incumbent.
                        if (region != null && ng < completeCost && Reached(next))
                        {
                            complete = next; completeCost = ng;
                            came[next] = (cur, edge, ng);
                        }
                        var key = Cell(next);
                        if (!labels.TryGetValue(key, out var list)) labels[key] = list = new();
                        if (Dominated(list, ng, next)) continue;
                        list.RemoveAll(l => l.G >= ng - 0.01f && MathF.Abs(l.Vx) <= MathF.Abs(next.Vx) + 0.01f
                            && MathF.Sign(l.Vx) == MathF.Sign(next.Vx) && MathF.Abs(l.Vy - next.Vy) < 0.5f);
                        list.Add(new(ng, next.Vx, next.Vy));
                        came[next] = (cur, edge, ng);
                        open.Enqueue(next, ng + H(next));
                        // Cost-aware partial fallback: choosing only the farthest landing
                        // discards the walk/jump costs whenever the budget is exhausted.
                        // Region pixel distance only ranks safe partial checkpoints;
                        // it is NOT the A* heuristic (the queue uses ng + H above).
                        float distance = Distance(next), score = ng + distance;
                        if (next.Grounded && world.SafeLanding(next) && distance < Distance(start) - 0.01f
                            && (score < bestScore - 0.01f || (Math.Abs(score - bestScore) <= 0.01f && distance < bestDistance)))
                        { best = next; bestDistance = distance; bestScore = score; }
                    }
                    if (timedOut) break;
                }
                if (timedOut) break;
            }
        }
        if (!reachedGoal && complete.HasValue) { best = complete.Value; reachedGoal = true; }
        var actions = new List<Prediction>();
        for (var k = best; came.TryGetValue(k, out var e) && !e.Prev.Equals(k); k = e.Prev)
            actions.Add(e.Edge);
        actions.Reverse();
        string failure = actions.Count == 0 ? string.Join("; ", rejected.OrderByDescending(p => p.Value)
            .Take(3).Select(p => $"{p.Key} ({p.Value} candidates)")) : null;
        if (actions.Count == 0 && string.IsNullOrEmpty(failure)) failure = "no forward grounded route within expansion/span budget";
        if (actions.Count == 0 && timedOut) failure = "search time budget reached; " + failure;
        return new(actions, expansions, watch.Elapsed.TotalMilliseconds, failure, reachedGoal, timedOut,
            actions.Count==0&&!timedOut?rootBlocks:null);
    }
}
