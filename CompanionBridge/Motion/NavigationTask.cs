using System;

namespace CompanionBridge.Motion;

// RecedingNav's plan -> dispatch first edge -> verify -> replan lifecycle,
// hosted on the existing game thread and bound to one control generation.
internal sealed class NavigationTask
{
    private readonly LocalMotionController _executor = new();
    private readonly bool _enableSafeGroundWalk;
    private int _searches,_fastWalks;
    internal const int RecoveryLimit = 3;
    private readonly NavigationFailureMemory _failures = new();
    private int _updates, _recoveries, _recoveryReplans, _recoveryFrames, _recoverySettled;
    private int _cancelledSession, _cancelledGeneration;
    private bool _recovering, _neutralIssued, _resumePending;
    private Prediction _failedEdge;
    private string _recoveryReason;
    private PhysicsState? _recoveryPosition;
    private bool? _recoveryInputsReleased;
    internal NavigationTask(bool enableSafeGroundWalk=false)=>_enableSafeGroundWalk=enableSafeGroundWalk;
    private int _session, _generation, _direction, _actions, _jumps, _plans;
    private float _targetX, _furthest;
    private LandingRegion _region;
    private int _stalled;
    private Route _route;
    private bool _executing;
    internal const int LandingConfirmationLimit=60;
    private bool _confirming;
    private int _confirmationFrames,_settledFrames;
    private LandingFeedback _lastLanding;
    internal NavigationSnapshot Snapshot { get; private set; } = new(0, "idle", "none", "not started", 0, 0, null, null, 0);
    internal bool Terminal => Snapshot.Status is "failed" or "arrived" or "unknown";

    internal void Cancel(bool retire=true)
    {
        if (retire && _session != 0) { _cancelledSession=_session; _cancelledGeneration=_generation; }
        _recovering=_neutralIssued=_resumePending=false;
        _failures.Clear(); _failedEdge=null;
        _recoveries=_recoveryReplans=_updates=0; _recoveryReason=null; _recoveryPosition=null; _recoveryInputsReleased=null;
        _executor.Cancel(); _session = 0; _executing = false; _lastLanding = null; _route = null;
        _confirming=false;_confirmationFrames=_settledFrames=0;
        Snapshot = new(0, "idle", "none", "control released", 0, 0, null, null, 0);
    }

    internal PhysicsInput Fail(string reason,NavigationBlockingTile[] blockingTiles=null)
    {
        Snapshot = Snapshot with { Status = "failed", Reason = reason,BlockingTiles=blockingTiles };
        _executing = false; _recovering=_resumePending=false;
        _executor.Cancel();
        return default;
    }

    internal void Begin(int session, int generation, float targetX, PhysicsState state)
        => BeginCore(session, generation, targetX, null, state);

    internal void Begin(int session, int generation, LandingRegion region, PhysicsState state)
        => BeginCore(session, generation, region.MinPlayerLeftX, region, state);

    private void BeginCore(int session, int generation, float targetX, LandingRegion region, PhysicsState state)
    {
        if (session == _cancelledSession && generation == _cancelledGeneration) return;
        if (_session == session && _generation == generation && Terminal) return;
        if (_session != session || _generation != generation || _targetX != targetX || _region != region)
        {
            Cancel(retire:false); _session = session; _generation = generation; _targetX = targetX;
            _region = region;
            _direction = targetX >= state.Px ? 1 : -1;
            _actions = _jumps = _stalled = _plans = _searches = _fastWalks = 0; _furthest = Progress(state);
            Snapshot = new(session, "running", "plan", "navigation started", 0, 0, null, null, 0,
                Generation: generation, TargetX: targetX, TargetRegion: region);
        }
    }

    internal PhysicsInput Before(int session, int generation, float targetX, PhysicsState state,
        PhysicsParameters ph, IMotionWorld world)
        => BeforeCore(session, generation, targetX, null, state, ph, world);

    internal PhysicsInput Before(int session, int generation, LandingRegion region, PhysicsState state,
        PhysicsParameters ph, IMotionWorld world)
        => BeforeCore(session, generation, region.MinPlayerLeftX, region, state, ph, world);

    private PhysicsInput BeforeCore(int session, int generation, float targetX, LandingRegion region, PhysicsState state,
        PhysicsParameters ph, IMotionWorld world)
    {
        BeginCore(session, generation, targetX, region, state);
        if (_session != session || _generation != generation || Terminal) return default;
        _updates++; _failures.Expire(_updates);
        if (_recovering) { _neutralIssued=true; return default; }
        if (_confirming) return default; // Observe natural braking/fall, never replay or replan in air.
        if (region != null && !ph.IsBasic) return Fail("unsupported landing-region physics");
        if (!_executing)
        {
            bool recoveryReplan=_resumePending;
            if (_resumePending)
            {
                if (!PhysicsSimulator.SupportedCheckpoint(state, world) || Math.Abs(state.Vx) > .1f
                    || !world.SafeSweep(state, state, out _))
                { Unconfirmed("recovery_checkpoint_changed"); return default; }
                _resumePending=false;
            }
            if (!state.Grounded) return Fail("replan requires grounded player");
            string landingRejection = world.LandingRejection(state);
            if (landingRejection != null)
                return Fail($"replan requires safe foot support and body clearance: {landingRejection}; "
                    + $"position=({state.Px},{state.Py}), velocity=({state.Vx},{state.Vy})");
            if (region != null ? region.Arrived(state, world)
                : _direction * (targetX - state.Px) <= 12 && Math.Abs(state.Vx) <= 0.1f
                    && Math.Abs(state.Vy) <= 0.1f && state.ReleaseJump && state.JumpFramesLeft == 0)
            { Snapshot = Snapshot with { Status = "arrived", Reason = region != null
                ? "landing region verified on safe settled ground" : "horizontal goal crossed on safe settled ground" }; return default; }
            if (!state.ReleaseJump || state.JumpFramesLeft != 0) return default;
            if (recoveryReplan) { _recoveryReplans++; Snapshot=Snapshot with {RecoveryReplans=_recoveryReplans}; }
            _route = _enableSafeGroundWalk&&region==null?SafeGroundWalk.TryPlan(state,targetX,_direction,ph,world):null;
            if (_route != null && RejectRemembered(state, _route.Actions[0])) _route=null;
            if(_route==null)
            {
                _route = region != null ? StateSpacePlanner.Plan(state, region, ph, world, rejectEdge: RejectRemembered)
                    : StateSpacePlanner.Plan(state, targetX, ph, world, goalDirection: _direction, rejectEdge: RejectRemembered);
                _searches++;
            }
            else _fastWalks++;
            _plans++;
            Snapshot = Snapshot with { Action = "plan", Expansions = _route.Expansions,
                PlannedActions = _route.Actions.Count, PlanMilliseconds = _route.Milliseconds,
                PlanNumber = _plans, ReachedGoal = _route.ReachedGoal, TimeBudgetReached = _route.TimeBudgetReached, PlanStart=state, SearchFailureReason=_route.FailureReason, PlanKind=_route.Kind, SearchNumber=_searches, FastWalkPlans=_fastWalks, SearchExit=_route.SearchExit, SearchRejections=_route.Rejections, DominatedStates=_route.DominatedStates };
            if (_route.Actions.Count == 0)
            {
                return Fail(_route.TimeBudgetReached ? "navigation_search_timeout"
                    : _route.SearchExit == "expansion_budget" ? "navigation_search_expansion_budget"
                    : _route.Rejections?.ContainsKey("recently failed local edge") == true ? "navigation_recovery_blocked: " + _route.FailureReason
                    : "no safe local route: " + _route.FailureReason,_route.BlockingTiles);
            }
            var edge = _route.Actions[0];
            _executor.Cancel();
            _executor.Begin(session, edge.Frames[0].Input.Direction, generation);
            _executor.LoadPlan(edge);
            _executing = true;
        }
        var input = _executor.Before(session, _route.Actions[0].Frames[0].Input.Direction,
            state, ph, world, generation);
        Publish();
        if (_executor.FailureKind == MotionFailureKind.RouteDeviation)
        { BeginRecovery(state, _executor.Snapshot.Reason); _neutralIssued=_recovering; return default; }
        return input;
    }

    internal void After(PhysicsState state, IMotionWorld world, bool inputsReleased=false)
    {
        if (Terminal) return;
        if (_recovering) { ObserveRecovery(state, world, inputsReleased); return; }
        if (!_executing) return;
        if(_confirming){ConfirmLanding(state,world);return;}
        _executor.After(state, world);
        Publish();
        if (Snapshot.Status == "failed")
        {
            if(_executor.NeedsLandingConfirmation){BeginConfirmation(state,world);return;}
            if (_executor.FailureKind == MotionFailureKind.RouteDeviation)
            { BeginRecovery(state, _executor.Snapshot.Reason); return; }
            _executing = false; return;
        }
        if (_executor.Snapshot.Status == "landed" || _executor.NeedsPlan)
        {
            // Moving walks/jumps validate their predicted checkpoint velocity;
            // final approaches have a zero-velocity endpoint in the same check.
            var landing = _route.Actions[0].Landing;
            if(!PhysicsSimulator.SupportedCheckpoint(state,world))
            {
                BeginConfirmation(state,world);return;
            }
            if (!state.Grounded || !world.SafeLanding(state) || Math.Abs(state.Vx - landing.Vx) > 0.1f
                || Math.Abs(state.Vy - landing.Vy) > 0.1f || Math.Abs(state.Px - landing.Px) > 8 || Math.Abs(state.Py - landing.Py) > 4)
            {
                Snapshot=Snapshot with{ActualLanding=new(state.Px,state.Py),ObservedGrounded=state.Grounded,SafeSupport=true};
                BeginRecovery(state, "navigation edge landing mismatch"); return;
            }
            _actions++;
            if (_route.Actions[0].Jump) _jumps++;
            _lastLanding = new(_actions, _route.Actions[0].Jump, new(landing.Px, landing.Py),
                new(state.Px, state.Py), _executor.Snapshot.MaxError,
                new(landing.Vx, landing.Vy), new(state.Vx, state.Vy),
                Math.Abs(state.Vx) <= 0.1f && Math.Abs(state.Vy) <= 0.1f);
            float forward = Progress(state);
            _stalled = forward >= _furthest + 4 ? 0 : _stalled + 1;
            _furthest = Math.Max(_furthest, forward);
            _executing = false;
            Snapshot = Snapshot with { Status = "running", Reason = "edge checkpoint verified; replan from observed state",
                ActualLanding = new(state.Px, state.Py), CompletedActions = _actions, CompletedJumps = _jumps,
                LastLanding = _lastLanding };
            CheckBudgets();
        }
    }

    private void CheckBudgets()
    {
        if (_stalled >= 4) Fail("navigation_stalled");
        else if (_actions >= 256) Fail("navigation_action_limit");
    }

    private void BeginConfirmation(PhysicsState state,IMotionWorld world)
    {
        _confirming=true;_confirmationFrames=_settledFrames=0;
        Snapshot=Snapshot with{Status="confirming_landing",Reason="awaiting_safe_landing",ActualLanding=null};
        ConfirmLanding(state,world);
    }

    private void ConfirmLanding(PhysicsState state,IMotionWorld world)
    {
        _confirmationFrames++;
        bool support=world.SafeLanding(state);
        Snapshot=Snapshot with{Status="confirming_landing",Reason="awaiting_safe_landing",
            ObservedPosition=new(state.Px,state.Py),ObservedVelocity=new(state.Vx,state.Vy),
            ObservedGrounded=state.Grounded,SafeSupport=support,LandingConfirmationFrames=_confirmationFrames};
        if(!world.SafeSweep(state,state,out string danger))
        {Unconfirmed("landing observation unsafe: "+danger);return;}
        bool settled=PhysicsSimulator.SupportedCheckpoint(state,world)&&Math.Abs(state.Vx)<=0.1f;
        _settledFrames=settled?_settledFrames+1:0;
        if(_settledFrames>=2)
        {
            var predicted=_route.Actions[0].Landing;
            Snapshot=Snapshot with{ActualLanding=new(state.Px,state.Py)};
            _confirming=false;_executing=false;
            // A different verified landing is a route deviation, not goal arrival.
            if(Math.Abs(state.Px-predicted.Px)>8||Math.Abs(state.Py-predicted.Py)>4)
            {BeginRecovery(state,"confirmed landing differs from predicted checkpoint");return;}
            _actions++;if(_route.Actions[0].Jump)_jumps++;
            _lastLanding=new(_actions,_route.Actions[0].Jump,new(predicted.Px,predicted.Py),new(state.Px,state.Py),
                _executor.Snapshot.MaxError,new(predicted.Vx,predicted.Vy),new(state.Vx,state.Vy),true);
            float forward=Progress(state);_stalled=forward>=_furthest+4?0:_stalled+1;_furthest=Math.Max(_furthest,forward);
            Snapshot=Snapshot with{Status="running",Reason="safe landing confirmed; replan from observed state",
                CompletedActions=_actions,CompletedJumps=_jumps,LastLanding=_lastLanding};
            CheckBudgets();
            return;
        }
        if(_confirmationFrames>=LandingConfirmationLimit)Unconfirmed("landing_confirmation_deadline");
    }

    private void Unconfirmed(string reason)
    {
        Snapshot=Snapshot with{Status="unknown",Reason=reason,ActualLanding=null};
        _confirming=false;_executing=false;_recovering=_resumePending=false;
        _executor.Cancel();
    }

    private bool RejectRemembered(PhysicsState start, Prediction edge) => _failures.Reject(start,edge,_updates);

    private void BeginRecovery(PhysicsState state, string reason)
    {
        if (_recoveries >= RecoveryLimit) { Fail("navigation_recovery_limit"); return; }
        _recoveries++;
        _failedEdge=_route.Actions[0]; _executor.Cancel();
        _recovering=true; _executing=_confirming=_neutralIssued=false;
        _recoveryFrames=_recoverySettled=0;
        _recoveryReason=reason; _recoveryPosition=state; _recoveryInputsReleased=null;
        Snapshot=Snapshot with {Status="recovering",Reason="release and verify actual checkpoint",ActualLanding=null,
            RecoveryCount=_recoveries,RecoveryReason=reason,RecoveryPosition=state,RecoveryInputsReleased=null};
    }

    private void ObserveRecovery(PhysicsState state, IMotionWorld world, bool inputsReleased)
    {
        // An After-only observation cannot prove the old input was replaced.
        if (!_neutralIssued) return;
        _neutralIssued=false; _recoveryFrames++;
        _recoveryInputsReleased=inputsReleased; _recoveryPosition=state;
        Snapshot=Snapshot with {ObservedPosition=new(state.Px,state.Py),ObservedVelocity=new(state.Vx,state.Vy),
            ObservedGrounded=state.Grounded,SafeSupport=world.SafeLanding(state),RecoveryPosition=state,
            RecoveryInputsReleased=inputsReleased,LandingConfirmationFrames=_recoveryFrames};
        if (!inputsReleased) { Unconfirmed("recovery_input_release_unconfirmed"); return; }
        if (!world.SafeSweep(state,state,out string danger)) { Unconfirmed("recovery observation unsafe: "+danger); return; }
        bool settled=PhysicsSimulator.SupportedCheckpoint(state,world) && Math.Abs(state.Vx)<=.1f;
        _recoverySettled=settled?_recoverySettled+1:0;
        if (_recoverySettled>=2)
        {
            _failures.Remember(state,_failedEdge,_updates); _failedEdge=null;
            _recovering=false; _resumePending=true;
            float forward=Progress(state); _stalled=forward>=_furthest+4?0:_stalled+1;
            _furthest=Math.Max(_furthest,forward);
            Snapshot=Snapshot with {Status="running",Reason="safe recovery checkpoint; replan from actual state",
                ActualLanding=new(state.Px,state.Py),RecoveryReplans=_recoveryReplans,FailedEdgeCount=_failures.Count};
            // Abandoned edges never count as completed actions or jumps.
            CheckBudgets(); return;
        }
        if (_recoveryFrames>=LandingConfirmationLimit) Unconfirmed("recovery_landing_confirmation_deadline");
    }

    // Only called at initialization and after the executor verifies actual support,
    // velocity and position. Best-ever distance prevents ascent/descent cycles
    // from resetting the four-action stall guard. The 256-action cap is unchanged.
    private float Progress(PhysicsState state) => _region != null ? -_region.Distance(state) : _direction * state.Px;

    private void Publish() => Snapshot = _executor.Snapshot with
    {
        Status = _executor.Snapshot.Status == "failed" ? "failed" : "running",
        TargetX = _targetX, CompletedActions = _actions, CompletedJumps = _jumps,
        Expansions = _route.Expansions, PlannedActions = _route.Actions.Count, PlanMilliseconds = _route.Milliseconds,
        LastLanding = _lastLanding, PlanNumber = _plans, ReachedGoal = _route.ReachedGoal,
        TimeBudgetReached = _route.TimeBudgetReached, TargetRegion = _region,
        PlanStart=Snapshot.PlanStart, SearchFailureReason=_route.FailureReason,
        PlanKind=_route.Kind, SearchNumber=_searches, FastWalkPlans=_fastWalks,
        SearchExit=_route.SearchExit, SearchRejections=_route.Rejections, DominatedStates=_route.DominatedStates,
        RecoveryCount=_recoveries,RecoveryReplans=_recoveryReplans,RecoveryReason=_recoveryReason,
        RecoveryPosition=_recoveryPosition,RecoveryInputsReleased=_recoveryInputsReleased,FailedEdgeCount=_failures.Count
    };
}
