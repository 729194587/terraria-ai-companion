using System;

namespace CompanionBridge.Motion;

// RecedingNav's plan -> dispatch first edge -> verify -> replan lifecycle,
// hosted on the existing game thread and bound to one control generation.
internal sealed class NavigationTask
{
    private readonly LocalMotionController _executor = new();
    private int _session, _generation, _direction, _actions, _jumps;
    private float _targetX, _furthest;
    private int _stalled;
    private Route _route;
    private bool _executing;
    private LandingFeedback _lastLanding;
    internal NavigationSnapshot Snapshot { get; private set; } = new(0, "idle", "none", "not started", 0, 0, null, null, 0);
    internal bool Terminal => Snapshot.Status is "failed" or "arrived";

    internal void Cancel()
    {
        _executor.Cancel(); _session = 0; _executing = false; _lastLanding = null;
        Snapshot = new(0, "idle", "none", "control released", 0, 0, null, null, 0);
    }

    internal PhysicsInput Fail(string reason)
    {
        Snapshot = Snapshot with { Status = "failed", Reason = reason };
        _executing = false;
        return default;
    }

    internal void Begin(int session, int generation, float targetX, PhysicsState state)
    {
        if (_session != session || _generation != generation || _targetX != targetX)
        {
            Cancel(); _session = session; _generation = generation; _targetX = targetX;
            _direction = targetX >= state.Px ? 1 : -1;
            _actions = _jumps = _stalled = 0; _furthest = _direction * state.Px;
            Snapshot = new(session, "running", "plan", "navigation started", 0, 0, null, null, 0,
                Generation: generation, TargetX: targetX);
        }
    }

    internal PhysicsInput Before(int session, int generation, float targetX, PhysicsState state,
        PhysicsParameters ph, IMotionWorld world)
    {
        Begin(session, generation, targetX, state);
        if (Terminal) return default;
        if (!_executing)
        {
            if (!state.Grounded) return Fail("replan requires grounded player");
            string landingRejection = world.LandingRejection(state);
            if (landingRejection != null)
                return Fail($"replan requires safe foot support and body clearance: {landingRejection}; "
                    + $"position=({state.Px},{state.Py}), velocity=({state.Vx},{state.Vy})");
            if (_direction * (targetX - state.Px) <= 12 && Math.Abs(state.Vx) <= 0.1f)
            { Snapshot = Snapshot with { Status = "arrived", Reason = "horizontal goal crossed on safe settled ground" }; return default; }
            if (!state.ReleaseJump || state.JumpFramesLeft != 0) return default;
            _route = StateSpacePlanner.Plan(state, targetX, ph, world, goalDirection: _direction);
            Snapshot = Snapshot with { Action = "plan", Expansions = _route.Expansions,
                PlannedActions = _route.Actions.Count, PlanMilliseconds = _route.Milliseconds };
            if (_route.Actions.Count == 0) return Fail("no safe local route: " + _route.FailureReason);
            var edge = _route.Actions[0];
            _executor.Cancel();
            _executor.Begin(session, edge.Frames[0].Input.Direction, generation);
            _executor.LoadPlan(edge);
            _executing = true;
        }
        var input = _executor.Before(session, _route.Actions[0].Frames[0].Input.Direction,
            state, ph, world, generation);
        Publish();
        return input;
    }

    internal void After(PhysicsState state, IMotionWorld world)
    {
        if (!_executing || Terminal) return;
        _executor.After(state, world);
        Publish();
        if (Snapshot.Status == "failed") { _executing = false; return; }
        if (_executor.Snapshot.Status == "landed" || _executor.NeedsPlan)
        {
            // Moving walks/jumps validate their predicted checkpoint velocity;
            // final approaches have a zero-velocity endpoint in the same check.
            var landing = _route.Actions[0].Landing;
            if (!state.Grounded || !world.SafeLanding(state) || Math.Abs(state.Vx - landing.Vx) > 0.1f
                || Math.Abs(state.Vy - landing.Vy) > 0.1f || Math.Abs(state.Px - landing.Px) > 8 || Math.Abs(state.Py - landing.Py) > 4)
            { Fail("navigation edge landing mismatch"); return; }
            _actions++;
            if (_route.Actions[0].Jump) _jumps++;
            _lastLanding = new(_actions, _route.Actions[0].Jump, new(landing.Px, landing.Py),
                new(state.Px, state.Py), _executor.Snapshot.MaxError,
                new(landing.Vx, landing.Vy), new(state.Vx, state.Vy),
                Math.Abs(state.Vx) <= 0.1f && Math.Abs(state.Vy) <= 0.1f);
            float forward = _direction * state.Px;
            _stalled = forward >= _furthest + 4 ? 0 : _stalled + 1;
            _furthest = Math.Max(_furthest, forward);
            _executing = false;
            Snapshot = Snapshot with { Status = "running", Reason = "edge checkpoint verified; replan from observed state",
                ActualLanding = new(state.Px, state.Py), CompletedActions = _actions, CompletedJumps = _jumps,
                LastLanding = _lastLanding };
            if (_stalled >= 4 || _actions >= 256) Fail("navigation progress/action budget exhausted");
        }
    }

    private void Publish() => Snapshot = _executor.Snapshot with
    {
        Status = _executor.Snapshot.Status == "failed" ? "failed" : "running",
        TargetX = _targetX, CompletedActions = _actions, CompletedJumps = _jumps,
        Expansions = _route.Expansions, PlannedActions = _route.Actions.Count, PlanMilliseconds = _route.Milliseconds,
        LastLanding = _lastLanding
    };
}
