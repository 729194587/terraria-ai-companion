using System;
using System.Collections.Generic;

namespace CompanionBridge.Motion;

internal sealed record LandingFeedback(int ActionNumber, bool Jump, StateVector Predicted,
    StateVector Actual, float MaxError, StateVector PredictedVelocity = null,
    StateVector ActualVelocity = null, bool Settled = true);

internal sealed record NavigationSnapshot(int Session, string Status, string Action, string Reason,
    int Frame, int HoldFrames, StateVector PredictedLanding, StateVector ActualLanding, float MaxError,
    StateVector ExpectedPosition = null, StateVector ObservedPosition = null,
    StateVector ExpectedVelocity = null, StateVector ObservedVelocity = null, int Generation = 0,
    float? TargetX = null, int CompletedActions = 0, int CompletedJumps = 0,
    int Expansions = 0, int PlannedActions = 0, double PlanMilliseconds = 0,
    LandingFeedback LastLanding = null, bool? ReachedGoal = null, bool? TimeBudgetReached = null,
    int PlanNumber = 0, LandingRegion TargetRegion = null, int LandingConfirmationFrames = 0,
    bool? ObservedGrounded = null, bool? SafeSupport = null,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    NavigationBlockingTile[] BlockingTiles=null, PhysicsState? PlanStart=null, string SearchFailureReason=null, string PlanKind=null, int SearchNumber=0, int FastWalkPlans=0);

// One rolling walk or one jump; no graph, digging, placement, or autonomous retries.
// Candidate simulation and landing feedback follow TerraBlind JumpExecutor's pattern.
internal sealed class LocalMotionController
{
    private Prediction _plan;
    private int _session, _direction, _generation, _frame, _hold, _badFrames, _supportedLandingFrames;
    private float _maxError;
    private bool _airborne;
    internal NavigationSnapshot Snapshot { get; private set; } = new(0, "idle", "none", "not started", 0, 0, null, null, 0);
    internal bool Terminal => Snapshot.Status is "failed" or "landed";
    internal bool NeedsPlan => _plan == null;
    internal bool NeedsLandingConfirmation { get; private set; }

    internal void LoadPlan(Prediction plan)
    {
        _plan = plan;
        NeedsLandingConfirmation=false;
        _frame = 0; _badFrames = 0; _supportedLandingFrames = 0; _maxError = 0; _airborne = false;
        _hold = plan.Frames.FindAll(f => f.Input.Jump).Count;
        Snapshot = Snapshot with { Status = "running" };
    }

    internal void Begin(int session, int direction, int generation = 0)
    {
        if (_session == session && _direction == direction && _generation == generation) return;
        Cancel(); _session = session; _direction = direction; _generation = generation;
    }

    internal void Cancel()
    {
        _plan = null;
        NeedsLandingConfirmation=false;
        _session = 0;
        Snapshot = new(0, "idle", "none", "control released", 0, 0, null, null, 0);
    }

    internal PhysicsInput Before(int session, int direction, PhysicsState state,
        PhysicsParameters parameters, IMotionWorld world, int generation = 0)
    {
        Begin(session, direction, generation);
        if (Terminal) return default;
        if (!world.SafeSweep(state, state, out string danger)) return Fail(danger);
        if (_plan == null)
        {
            if (!state.Grounded) return Fail("start/replan requires ground support");
            if (!state.ReleaseJump || state.JumpFramesLeft != 0)
            {
                Snapshot = new(session, "running", "release", "release jump before planning", 0, 0, null, null, 0,
                    Generation: _generation);
                return default;
            }
            _plan = PhysicsSimulator.Predict(state, direction, 0, parameters, world);
            _hold = 0;
            string walkingFailure = _plan.Reason;
            if (!_plan.Safe)
            {
                // Upstream's enumeration of hold lengths is retained, without its
                // binary search, target-cell planner, replay singleton, or fake Vx seed.
                foreach (int hold in new[] { 1, 4, 8, 12, 16 })
                {
                    _plan = PhysicsSimulator.Predict(state, direction, hold, parameters, world);
                    if (_plan.Safe) { _hold = hold; break; }
                }
                if (!_plan.Safe) return Fail($"no safe local action; walk={walkingFailure}; jump={_plan.Reason}");
            }
            _frame = 0; _badFrames = 0; _supportedLandingFrames = 0; _maxError = 0; _airborne = false;
        }
        var next = _plan.Frames[_frame];
        // Recheck the immediate step against the current world and actual velocity.
        try
        {
            var actualNext = PhysicsSimulator.Step(state, next.Input, parameters, world);
            if (!world.SafeSweep(state, actualNext, out danger)) return Fail(danger);
        }
        catch (UnsafeMotionException error) { return Fail(error.Message); }
        Snapshot = new(session, "running", _plan.Jump ? "jump" : "walk", _plan.Reason,
            _frame, _hold, new(_plan.Landing.Px, _plan.Landing.Py), null, _maxError, Generation: _generation);
        return next.Input;
    }

    internal void After(PhysicsState actual, IMotionWorld world)
    {
        if (_plan == null || Terminal) return;
        if (!world.SafeSweep(actual, actual, out string danger)) { Fail(danger); return; }
        var expected = _plan.Frames[_frame].State;
        Snapshot = Snapshot with { ExpectedPosition = new(expected.Px, expected.Py), ObservedPosition = new(actual.Px, actual.Py),
            ExpectedVelocity = new(expected.Vx, expected.Vy), ObservedVelocity = new(actual.Vx, actual.Vy) };
        float error = Math.Max(Math.Abs(expected.Px - actual.Px), Math.Abs(expected.Py - actual.Py));
        _maxError = Math.Max(_maxError, error);
        float velocityError = Math.Max(Math.Abs(expected.Vx - actual.Vx), Math.Abs(expected.Vy - actual.Vy));
        _badFrames = error > 6 || velocityError > 1.5f ? _badFrames + 1 : 0;
        _airborne |= !actual.Grounded;
        if (_plan.Jump && _plan.ExecuteFrames < _plan.Frames.Count)
            _supportedLandingFrames = actual.Grounded && world.SafeLanding(actual)
                && actual.ReleaseJump && actual.JumpFramesLeft == 0 ? _supportedLandingFrames + 1 : 0;
        _frame++;
        Snapshot = Snapshot with { Frame = _frame, MaxError = _maxError };
        if (_badFrames >= 2 || error > 16)
        {
            Fail($"prediction diverged: position={error:F2}px velocity={velocityError:F2}px/tick"); return;
        }
        if (_frame >= _plan.ExecuteFrames)
        {
            if (_plan.Jump)
            {
                bool movingLanding = _plan.ExecuteFrames < _plan.Frames.Count;
                bool safeSupport = world.SafeLanding(actual);
                bool landed = _airborne && actual.Grounded && safeSupport
                    && Math.Abs(actual.Vx - (movingLanding ? _plan.Landing.Vx : 0)) <= 0.1f
                    && Math.Abs(actual.Vy) <= 0.1f
                    && (!movingLanding || _supportedLandingFrames >= 2)
                    && Math.Abs(actual.Px - _plan.Landing.Px) <= 8 && Math.Abs(actual.Py - _plan.Landing.Py) <= 4;
                NeedsLandingConfirmation=!landed&&(!actual.Grounded||!safeSupport||Math.Abs(actual.Vy)>0.1f
                    ||movingLanding&&_supportedLandingFrames<2);
                Snapshot = new(_session, landed ? "landed" : "failed", "jump",
                    landed ? movingLanding ? "moving landing verified within prediction tolerance"
                        : "landed and settled within prediction tolerance"
                        : $"jump landing mismatch: airborne={_airborne}, grounded={actual.Grounded}, support={safeSupport}, "
                            + $"supportFrames={_supportedLandingFrames}, releaseJump={actual.ReleaseJump}, jumpCounter={actual.JumpFramesLeft}, "
                            + $"vxError={Math.Abs(actual.Vx - (movingLanding ? _plan.Landing.Vx : 0)):F3}, vy={actual.Vy:F3}",
                    _frame, _hold, new(_plan.Landing.Px, _plan.Landing.Py), !NeedsLandingConfirmation ? new(actual.Px, actual.Py) : null, _maxError,
                    Snapshot.ExpectedPosition, Snapshot.ObservedPosition, Snapshot.ExpectedVelocity, Snapshot.ObservedVelocity, _generation);
            }
            else _plan = null; // Next update replans a moving walk prefix.
        }
    }

    internal PhysicsInput Fail(string reason)
    {
        Snapshot = new(_session, "failed", _plan?.Jump == true ? "jump" : "walk", reason,
            _frame, _hold, _plan == null ? null : new(_plan.Landing.Px, _plan.Landing.Py), null, _maxError,
            Snapshot.ExpectedPosition, Snapshot.ObservedPosition, Snapshot.ExpectedVelocity, Snapshot.ObservedVelocity, _generation);
        _plan = null;
        return default;
    }
}
