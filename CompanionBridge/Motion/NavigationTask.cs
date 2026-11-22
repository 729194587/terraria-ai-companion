using System;

namespace CompanionBridge.Motion;

// RecedingNav's plan -> dispatch first edge -> verify -> replan lifecycle,
// hosted on the existing game thread and bound to one control generation.
internal sealed class NavigationTask
{
    internal int? LiveFallStart { set=>_executor.LiveFallStart=value; }
    internal ulong ProgressVersion { get; private set; }
    internal Func<PhysicsState,PhysicsParameters,IMotionWorld,bool> TryGrapple { get; set; }
    private readonly LocalMotionController _executor = new();
    private readonly bool _enableSafeGroundWalk;
    private int _searches,_fastWalks,_deferredSearches,_consecutiveRecoveries;
    private StateSpacePlanner.Search _pendingSearch;
    private SurfaceNavigator _surfaceSearch;
    private SurfaceGraphSnapshot _surfaceSnapshot;
    private PhysicsState _searchStart;
    private PhysicsParameters _searchPhysics;
    private long _searchTerrain;
    private int _searchFailures;
    private StateSpacePlanner.Search _aheadSearch;
    private Route _aheadRoute;
    private PhysicsState _aheadStart;
    private long _aheadTerrain;
    private long _aheadWindow;
    private int _aheadSlices;
    private int _aheadEdge=-1;
    private IMotionWorld _observedWorld;
    private PhysicsState _observedTerrainPose;
    private long _observedTerrain;
    private NavigationKnowledge _knowledge=new();
    private bool _sharedKnowledge;
    private NavigationFailureMemory _failures=>_knowledge.Failures;
    private readonly NavigationProgressWatchdog _watchdog=new();
    internal long ObservationUpdate { get; set; }=-1;
    private long Clock=>ObservationUpdate>=0?ObservationUpdate:_updates;
    internal void SetKnowledge(NavigationKnowledge knowledge)
    {
        _sharedKnowledge=knowledge!=null;
        _knowledge=knowledge??new();
    }
    private void DiscardSearch(){_pendingSearch?.Dispose();_pendingSearch=null;_surfaceSearch?.Dispose();_surfaceSearch=null;}
    private void DiscardAhead(){_aheadSearch?.Dispose();_aheadSearch=null;_aheadRoute=null;_aheadEdge=-1;}

    internal bool Continuous { get; set; }
    internal bool PreferFlatRegion { get; set; }
    internal const int RecoveryLimit = 3;
    private int _updates, _recoveries, _recoveryReplans, _recoveryFrames, _recoverySettled;
    private int _cancelledSession, _cancelledGeneration;
    private bool _recovering, _neutralIssued, _resumePending;
    private Prediction _failedEdge;
    private PhysicsState _edgeStart;
    private PhysicsState _motionStart;
    private bool _motionIssued;
    private readonly double _searchBudgetMilliseconds;
    private long _mobilityRevision,_planMobilityRevision;
    private bool _rememberFailedEdge=true;
    private string _recoveryReason;
    private PhysicsState? _recoveryPosition;
    private bool? _recoveryInputsReleased;
    private readonly Func<PhysicsState,int> _historicalPreference;
    internal NavigationTask(bool enableSafeGroundWalk=false,Func<PhysicsState,int> historicalPreference=null,
        double searchBudgetMilliseconds=25)
    {
        _enableSafeGroundWalk=enableSafeGroundWalk;
        _historicalPreference=historicalPreference;
        _searchBudgetMilliseconds=searchBudgetMilliseconds;
    }
    private int _session, _generation, _direction, _actions, _jumps, _plans;
    private float _targetX, _furthest;
    private LandingRegion _region;
    private int _stalled;
    private Route _route;
    private readonly NavigationTripProgress _trip=new();
    private int _routeIndex,_continuedEdges,_invalidatedRoutes;
    private PhysicsParameters _routePhysics;
    internal PhysicsState? ExpectedState=>_executor.ExpectedState;
    private double _planningMilliseconds;
    private Prediction Edge=>_route.Actions[_routeIndex];
    private bool _executing, _waitedForWingRefill;
    internal const int LandingConfirmationLimit=60;
    private bool _confirming;
    private int _confirmationFrames,_settledFrames;
    private LandingFeedback _lastLanding;
    internal NavigationSnapshot Snapshot { get; private set; } = new(0, "idle", "none", "not started", 0, 0, null, null, 0);
    internal bool Terminal => Snapshot.Status is "failed" or "arrived" or "unknown";

    internal void Cancel(bool retire=true)
    {
        if (retire && _session != 0) { _cancelledSession=_session; _cancelledGeneration=_generation; }
        _recovering=_neutralIssued=_resumePending=_motionIssued=false;
        if(!_sharedKnowledge)_knowledge.Clear();
        DiscardSearch();DiscardAhead();_watchdog.Reset();_failedEdge=null;
        _observedWorld=null;
        _deferredSearches=_consecutiveRecoveries=0;
        _recoveries=_recoveryReplans=_updates=0; _recoveryReason=null; _recoveryPosition=null; _recoveryInputsReleased=null;
        _waitedForWingRefill=false;
        _executor.Cancel(); _session = 0; _executing = false; _lastLanding = null; _route = null;
        _surfaceSnapshot=null;
        _confirming=false;_confirmationFrames=_settledFrames=0;
        _routeIndex=_continuedEdges=_invalidatedRoutes=0;_planningMilliseconds=0;_routePhysics=null;_mobilityRevision=_planMobilityRevision=0;_rememberFailedEdge=true;
        Snapshot = new(0, "idle", "none", "control released", 0, 0, null, null, 0);
    }

    internal PhysicsInput Fail(string reason,NavigationBlockingTile[] blockingTiles=null)
    {
        Snapshot = Snapshot with { Status = "failed", Reason = reason,BlockingTiles=blockingTiles };
        _executing = false; _recovering=_resumePending=false;
        _executor.Cancel();DiscardSearch();DiscardAhead();
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
            if(_generation!=0&&_generation!=generation)_knowledge.Clear();
            Cancel(retire:false); _session = session; _generation = generation; _targetX = targetX;
            _region = region;
            _direction = targetX >= state.Px ? 1 : -1;
            _actions = _jumps = _stalled = _plans = _searches = _fastWalks = 0; _furthest = Progress(state);
            ProgressVersion=0;
            _trip.Begin(state,Distance(state));
            Snapshot = new(session, "running", "plan", "navigation started", 0, 0, null, null, 0,
                Generation: generation, TargetX: targetX, TargetRegion: region);
        }
    }

    internal PhysicsInput Before(int session, int generation, float targetX, PhysicsState state,
        PhysicsParameters ph, IMotionWorld world)
        {var input=BeforeCore(session,generation,targetX,null,state,ph,world);UpdateTripSnapshot();return input;}

    internal PhysicsInput Before(int session, int generation, LandingRegion region, PhysicsState state,
        PhysicsParameters ph, IMotionWorld world)
        {var input=BeforeCore(session,generation,region.MinPlayerLeftX,region,state,ph,world);UpdateTripSnapshot();return input;}

    private PhysicsInput BeforeCore(int session, int generation, float targetX, LandingRegion region, PhysicsState state,
        PhysicsParameters ph, IMotionWorld world)
    {
        BeginCore(session, generation, targetX, region, state);
        if (_session != session || _generation != generation || Terminal) return default;
        _updates++; _failures.Validate(world,ph,Clock);
        long terrain=!Continuous&&region==null&&world is ISurfaceTerrain?world.TerrainSignature(state):0;
        if(Continuous)
        {
            // Re-read the previous fixed window before moving the observation
            // window. Scrolling alone must not erase trip history.
            if(_observedWorld!=null&&_observedWorld.TerrainSignature(_observedTerrainPose)!=_observedTerrain)
                _trip.Begin(state,Distance(state));
            terrain=world.TerrainSignature(state);
            _observedWorld=world;_observedTerrainPose=state;_observedTerrain=terrain;
        }
        if(!Continuous&&_updates>NavigationTripProgress.UpdateLimit)return Fail("navigation_trip_update_budget");
        if (_recovering) { _watchdog.Observe(NavigationActivity.Recovery);_neutralIssued=true; return default; }
        if (_confirming) return default; // Observe natural braking/fall, never replay or replan in air.
        if (!ph.IsSupportedGroundPhysics) return Fail("unsupported landing-region physics");
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
            if ((region?.Contains(state)??(_direction * (targetX - state.Px) <= 12))&&PhysicsSimulator.Settled(state,world))
            { Snapshot = Snapshot with { Status = "arrived", Reason = region != null
                ? "landing region verified on safe settled ground" : "horizontal goal crossed on safe settled ground" }; return default; }
            if (!state.ReleaseJump || state.JumpFramesLeft != 0) return default;
            // Native wings recharge on a settled frame with jump released. Use the
            // next observed amount; a missing refill must not stall route planning.
            if(ph.Air?.Kind==AirKind.Wings && state.Air is {} resources &&
                resources.WingTime<resources.WingMax && PhysicsSimulator.Settled(state,world))
            {
                if(!_waitedForWingRefill)
                {
                    _waitedForWingRefill=true;
                    Snapshot=Snapshot with {Action="release",Reason="awaiting observed wing recharge"};
                    return default;
                }
            }
            else _waitedForWingRefill=false;
            if (!TryContinue(state,ph,world))
            {
                if (recoveryReplan) { _recoveryReplans++; Snapshot=Snapshot with {RecoveryReplans=_recoveryReplans}; }
                if(!Continuous&&(_searches>=NavigationTripProgress.SearchLimit||_planningMilliseconds>=NavigationTripProgress.PlanningLimitMilliseconds))
                    return Fail("navigation_trip_search_budget");
                _routeIndex=0;_routePhysics=ph;_planMobilityRevision=_mobilityRevision;
                int priorRepeatRejections=_trip.RepeatRejections;
                float walkTarget=region==null?targetX:(region.MinPlayerLeftX+region.MaxPlayerLeftX)/2;
                int walkDirection=walkTarget>=state.Px?1:-1;
                bool surface=Continuous&&region!=null&&world is ISurfaceTerrain;
                // Cleared edit corridors and precise final braking retain the
                // existing fully simulated shortcut. Larger/special terrain is
                // organised by the graph, not a succession of flat prefixes.
                bool shortApproach=!surface||region.Distance(state)<=64;
                _route = shortApproach&&_enableSafeGroundWalk&&(region==null||Continuous||PreferFlatRegion)
                    ?SafeGroundWalk.TryPlan(state,walkTarget,region==null?_direction:walkDirection,ph,world,region):null;
                if (_route != null && RejectRemembered(state, _route.Actions[0])) _route=null;
                if(_route==null)
                {
                    _watchdog.Observe(NavigationActivity.Search);
                    if(Continuous&&region!=null||region==null&&world is ISurfaceTerrain)
                    {
                        _knowledge.Search.Scope(terrain,ph,Clock);
                        if((_pendingSearch==null&&_surfaceSearch==null)||_searchStart!=state||_searchPhysics!=ph||_searchTerrain!=terrain
                            ||_searchFailures!=_failures.Version)
                        {
                            DiscardSearch();
                            _searchStart=state;_searchPhysics=ph;_searchTerrain=terrain;
                            _searchFailures=_failures.Version;
                            if(surface)_surfaceSearch=new(_knowledge.Surfaces,state,region,ph,world,(ISurfaceTerrain)world,_knowledge.Search,RejectRemembered,_trip.Detour,_historicalPreference);
                            else _pendingSearch=new(state,targetX,region,ph,world,goalDirection:region==null?_direction:null,rejectEdge:RejectRemembered,
                                    detourCheckpoint:_trip.Detour,historicalPreference:_historicalPreference,learning:_knowledge.Search);
                        }
                        if(_surfaceSearch!=null)
                        {
                            _route=_surfaceSearch.Advance(budgetMs:Math.Min(4,_searchBudgetMilliseconds));
                            _surfaceSnapshot=_surfaceSearch.Snapshot;
                        }
                        else _route=_pendingSearch.Advance(budgetMs:_searchBudgetMilliseconds);
                    }
                    else
                        _route = region != null ? StateSpacePlanner.Plan(state, region, ph, world, budgetMs: _searchBudgetMilliseconds, rejectEdge: RejectRemembered, detourCheckpoint: _trip.Detour, historicalPreference: _historicalPreference)
                            : StateSpacePlanner.Plan(state, targetX, ph, world, goalDirection: _direction, rejectEdge: RejectRemembered, detourCheckpoint: _trip.Detour, historicalPreference: _historicalPreference);
                    _searches++;
                }
                else _fastWalks++;
                _planningMilliseconds+=_route.Milliseconds;
                _plans++;
                Snapshot = Snapshot with { Action = "plan", Expansions = _route.Expansions,
                    PlannedActions = _route.Actions.Count, PlanMilliseconds = _route.Milliseconds,
                    PlanNumber = _plans, ReachedGoal = _route.ReachedGoal, TimeBudgetReached = _route.TimeBudgetReached, PlanStart=state, SearchFailureReason=_route.FailureReason, PlanKind=_route.Kind, SearchNumber=_searches, FastWalkPlans=_fastWalks, SearchExit=_route.SearchExit, SearchRejections=_route.Rejections, DominatedStates=_route.DominatedStates };
                bool grappleFallback=_route.SearchExit=="no_route_in_coverage"
                    ||_route.SearchExit=="safe_partial"&&_route.Actions.Count>0
                    &&_route.Actions.TrueForAll(edge=>edge.Landing.Py>=state.Py-LandingRegion.FeetTolerance);
                if(grappleFallback&&TryGrapple?.Invoke(state,ph,world)==true)
                {
                    DiscardSearch();_route=null;
                    Snapshot=Snapshot with {Status="running",Reason="bounded native grapple candidate selected"};
                    return default;
                }
                if (_route.Actions.Count == 0)
                {
                    if(_route.SearchExit=="surface_node_budget")
                        return Fail("navigation_surface_node_budget");
                    bool sliceExhausted=_route.TimeBudgetReached||_route.SearchExit is "expansion_budget" or "local_state_budget";
                    if(sliceExhausted&&(_pendingSearch is {Finished:false}||_surfaceSearch is {Finished:false}))
                    {
                        // Retain exact frontier/candidate cursor. Its start, terrain
                        // and physics are revalidated before the next game-thread slice.
                        _deferredSearches++;
                        Snapshot=Snapshot with{Status="running",Reason="local search deferred"};
                        return default;
                    }
                    DiscardSearch();
                    if(_route.SearchExit=="unknown_terrain")
                    {Unconfirmed("navigation_unknown_terrain");return default;}
                    if(_route.SearchExit=="local_coverage_exhausted")return Fail("navigation_local_coverage_exhausted");
                    return Fail(_route.TimeBudgetReached ? "navigation_search_timeout"
                        : _route.SearchExit is "expansion_budget" or "local_state_budget" ? "navigation_search_expansion_budget"
                        : _trip.RepeatRejections>priorRepeatRejections ? "navigation_trip_repeated_state"
                        : _route.Rejections?.ContainsKey("recently failed local edge") == true ? "navigation_recovery_blocked: " + _route.FailureReason
                        : "no safe local route: " + _route.FailureReason,_route.BlockingTiles);
                }

            }
            DiscardSearch();
            _edgeStart=state;
            var edge=Edge;
            _executor.Cancel();
            _executor.Begin(session, edge.Frames[0].Input.Direction, generation);
            _executor.LoadPlan(edge);
            _executing = true;
        }
        _watchdog.Observe(NavigationActivity.Movement);
        var input = _executor.Before(session, Edge.Frames[0].Input.Direction,
            state, ph, world, generation);
        Publish();
        if (_executor.FailureKind == MotionFailureKind.RouteDeviation)
        { BeginRecovery(state, _executor.Snapshot.Reason); _neutralIssued=_recovering; return default; }
        if(Continuous&&_region!=null&&_routeIndex==_route.Actions.Count-1&&!_route.ReachedGoal
            &&world is ISurfaceTerrain&&_routePhysics==ph)
            PlanAhead(ph,world);
        _motionStart=state;_motionIssued=true;
        return input;
    }

    internal void After(PhysicsState state, IMotionWorld world, bool inputsReleased=false)
    {AfterCore(state,world,inputsReleased);UpdateTripSnapshot();}
    private void AfterCore(PhysicsState state, IMotionWorld world, bool inputsReleased)
    {
        if(_motionIssued){_watchdog.MotionObserved(_motionStart.Px,_motionStart.Py,state.Px,state.Py);_motionIssued=false;}
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
            var landing = Edge.Landing;
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
            if (Edge.Jump) _jumps++;
            _lastLanding = new(_actions, Edge.Jump, new(landing.Px, landing.Py),
                new(state.Px, state.Py), _executor.Snapshot.MaxError,
                new(landing.Vx, landing.Vy), new(state.Vx, state.Vy),
                Math.Abs(state.Vx) <= 0.1f && Math.Abs(state.Vy) <= 0.1f);
            float forward = Progress(state);
            _stalled = Checkpoint(state) ? 0 : _stalled + 1;
            _furthest = Math.Max(_furthest, forward);
            if(_routeIndex>0)_continuedEdges++;
            _routeIndex++;
            _executing = false;
            Snapshot = Snapshot with { Status = "running", Reason = "edge checkpoint verified; replan from observed state",
                ActualLanding = new(state.Px, state.Py), CompletedActions = _actions, CompletedJumps = _jumps,
                LastLanding = _lastLanding };
            CheckBudgets();
        }
    }

    private bool Checkpoint(PhysicsState state,bool verifiedAction=true)
    {
        bool progress=_trip.Checkpoint(state,Distance(state),continuous:Continuous);
        if(progress)
        {
            ProgressVersion++;
            _watchdog.Observe(NavigationActivity.VerifiedCheckpoint);
            if(Continuous&&verifiedAction)_consecutiveRecoveries=0;
        }
        return progress;
    }

    private void CheckBudgets()
    {
        if (Continuous?_watchdog.Stalled:_stalled>=4) Fail("navigation_stalled");
        else if (!Continuous && _actions >= 256) Fail("navigation_action_limit");
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
        bool settled=PhysicsSimulator.Settled(state,world);
        _settledFrames=settled?_settledFrames+1:0;
        if(_settledFrames>=2)
        {
            var predicted=Edge.Landing;
            Snapshot=Snapshot with{ActualLanding=new(state.Px,state.Py)};
            _confirming=false;_executing=false;
            // A different verified landing is a route deviation, not goal arrival.
            if(Math.Abs(state.Px-predicted.Px)>8||Math.Abs(state.Py-predicted.Py)>4)
            {BeginRecovery(state,"confirmed landing differs from predicted checkpoint");return;}
            _actions++;if(Edge.Jump)_jumps++;
            _lastLanding=new(_actions,Edge.Jump,new(predicted.Px,predicted.Py),new(state.Px,state.Py),
                _executor.Snapshot.MaxError,new(predicted.Vx,predicted.Vy),new(state.Vx,state.Vy),true);
            float forward=Progress(state);_stalled=Checkpoint(state)?0:_stalled+1;_furthest=Math.Max(_furthest,forward);
            Snapshot=Snapshot with{Status="running",Reason="safe landing confirmed; replan from observed state",
                CompletedActions=_actions,CompletedJumps=_jumps,LastLanding=_lastLanding};
            if(_routeIndex>0)_continuedEdges++;
            _routeIndex++;
            CheckBudgets();
            return;
        }
        if(_confirmationFrames>=LandingConfirmationLimit)Unconfirmed("landing_confirmation_deadline");
    }

    private void Unconfirmed(string reason)
    {
        Snapshot=Snapshot with{Status="unknown",Reason=reason,ActualLanding=null};
        _confirming=false;_executing=false;_recovering=_resumePending=false;
        _executor.Cancel();DiscardSearch();DiscardAhead();
    }

    internal void DeferPlanningOverrun(PhysicsState state)
    {
        if(Terminal)return;
        _deferredSearches++;
        if(_executing)
        {
            BeginRecovery(state,"game-thread planning slice overran");
            _rememberFailedEdge=false;
        }
        else Snapshot=Snapshot with{Status="running",Reason="game-thread search slice deferred"};
    }

    private bool RejectRemembered(PhysicsState start, Prediction edge)
        => _failures.RejectKnown(start,edge,Clock)||_trip.Reject(edge.Landing);

    private bool TryContinue(PhysicsState state,PhysicsParameters ph,IMotionWorld world)
    {
        if(_route==null||_routeIndex==0)return false;
        bool ahead=_routeIndex>=_route.Actions.Count;
        if(ahead)
        {
            var route=_aheadRoute;
            bool current=route!=null&&state==_aheadStart&&ph==_routePhysics
                &&_aheadTerrain==world.TerrainSignature(state,route.Actions[0])
                &&!RejectRemembered(state,route.Actions[0]);
            DiscardAhead();
            if(!current)return false;
            _route=route;
            _routeIndex=0;
        }
        var old=Edge;
        if(_routePhysics==ph&&!RejectRemembered(state,old)&&PhysicsSimulator.SupportedCheckpoint(state,world))
        {
            int hold=old.ExtraJumpFrame>=0||old.AirStartFrame>=0?old.InitialHold:old.Frames.FindAll(f=>f.Input.Jump).Count;
            int stride=old.Jump?24:old.Frames.FindAll(f=>f.Input.Direction!=0).Count;
            var fresh=PhysicsSimulator.Predict(state,old.Frames[0].Input.Direction,hold,ph,world,
                walkFrames:stride,rollingJump:true,extraJumpFrame:old.ExtraJumpFrame,airStartFrame:old.AirStartFrame,airHoldFrames:old.AirHoldFrames);
            if(fresh.Safe&&old.ExecuteFrames<=fresh.Frames.Count)
            {
                var end=fresh.Frames[old.ExecuteFrames-1].State;
                if(PhysicsSimulator.SupportedCheckpoint(end,world)&&Math.Abs(end.Px-old.Landing.Px)<.01f
                    &&Math.Abs(end.Py-old.Landing.Py)<.01f&&Math.Abs(end.Vx-old.Landing.Vx)<.01f
                    &&Math.Abs(end.Vy-old.Landing.Vy)<.01f)
                {
                    _route.Actions[_routeIndex]=fresh with{ExecuteFrames=old.ExecuteFrames,Landing=end};
                    if(ahead)_continuedEdges++;
                    return true;
                }
            }
        }
        _invalidatedRoutes++;return false;
    }

    private void PlanAhead(PhysicsParameters ph,IMotionWorld world)
    {
        var endpoint=Edge.Landing;
        if(_region.Contains(endpoint)||!PhysicsSimulator.SupportedCheckpoint(endpoint,world))return;
        if(_aheadEdge==_routeIndex&&_aheadSearch==null)return;
        var aheadWorld=((ISurfaceTerrain)world).MotionAt(endpoint);
        long window=aheadWorld.TerrainSignature(endpoint);
        if(_aheadEdge!=_routeIndex||_aheadWindow!=window)
        {
            DiscardAhead();
            _aheadEdge=_routeIndex;
            _aheadStart=endpoint;
            _aheadWindow=window;
            _aheadSlices=0;
            _aheadSearch=new(endpoint,Math.Clamp(endpoint.Px,_region.MinPlayerLeftX,_region.MaxPlayerLeftX),
                _region,ph,aheadWorld,rejectEdge:RejectRemembered,detourCheckpoint:_trip.Detour);
        }
        if(_aheadSearch==null)return;
        var result=_aheadSearch.Advance(maxExpansions:4,budgetMs:Math.Min(1,_searchBudgetMilliseconds));
        // Keep graph-guided detours in charge of choosing intermediate checkpoints.
        // A speculative local route is useful only when it reaches the final goal.
        if(result.ReachedGoal&&result.Actions.Count>0)
        {
            _aheadTerrain=world.TerrainSignature(endpoint,result.Actions[0]);
            _aheadRoute=result;
        }
        if(_aheadSearch.Finished||result.ReachedGoal||++_aheadSlices>=8)
        {_aheadSearch.Dispose();_aheadSearch=null;}
    }
    private float Distance(PhysicsState state)=>_region?.Distance(state)??Math.Max(0,_direction*(_targetX-state.Px));

    internal void InvalidateMobility(PhysicsState state,long revision,PhysicsParameters physics=null)
    {
        if(Terminal)return;
        _mobilityRevision=revision;
        DiscardSearch();
        DiscardAhead();
        _knowledge.ObservePhysics(physics);
        if(_recovering){_rememberFailedEdge=false;return;}
        if(_route!=null&&(_executing||_routeIndex<_route.Actions.Count))
        {
            if(_routeIndex>=_route.Actions.Count)_routeIndex=_route.Actions.Count-1;
            BeginRecovery(state,"mobility profile changed; old route invalidated");
            _rememberFailedEdge=false;
        }
        else _route=null;
    }
    internal void RecoverGrapple(PhysicsState state,string reason) => BeginRecovery(state,reason,grapple:true);

    private void BeginRecovery(PhysicsState state, string reason,bool grapple=false)
    {
        DiscardAhead();
        if ((Continuous?_consecutiveRecoveries:_recoveries) >= RecoveryLimit) { Fail("navigation_recovery_limit"); return; }
        _recoveries++;_consecutiveRecoveries++;
        _rememberFailedEdge=!grapple&&(!Continuous||Snapshot.Frame>0);
        _failedEdge=grapple?null:Edge; _routeIndex=0; _executor.Cancel();
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
        bool settled=PhysicsSimulator.Settled(state,world);
        _recoverySettled=settled?_recoverySettled+1:0;
        if (_recoverySettled>=2)
        {
            if(_rememberFailedEdge)
            {
                if(Continuous)_failures.Remember(_edgeStart,_failedEdge,Clock,world,_routePhysics,_recoveryReason,state);
                else _failures.Remember(state,_failedEdge,Clock);
            }
            _failedEdge=null;
            _recovering=false; _resumePending=true; _route=null;
            float forward=Progress(state); _stalled=Checkpoint(state,verifiedAction:false)?0:_stalled+1;
            _furthest=Math.Max(_furthest,forward);
            Snapshot=Snapshot with {Status="running",Reason="safe recovery checkpoint; replan from actual state",
                ActualLanding=new(state.Px,state.Py),RecoveryReplans=_recoveryReplans,FailedEdgeCount=_failures.Count};
            // Abandoned edges never count as completed actions or jumps.
            CheckBudgets(); return;
        }
        if (_recoveryFrames>=LandingConfirmationLimit) Unconfirmed("recovery_landing_confirmation_deadline");
    }

    // The original goal and crossing direction survive every prefix/recovery.
    // Trip progress separately bounds novel detours and repeated checkpoints.
    private float Progress(PhysicsState state) => _region != null ? -_region.Distance(state) : _direction * state.Px;

    private void UpdateTripSnapshot()
    {
        if(Snapshot.Session==0)return;
        Snapshot=Snapshot with{TripUpdates=_updates,ContinuedEdges=_continuedEdges,InvalidatedRoutes=_invalidatedRoutes,
            TripPlanningMilliseconds=_planningMilliseconds,DetourCheckpoints=_trip.DetourCheckpoints,
            VisitedCheckpoints=_trip.VisitedCheckpoints,RepeatRejections=_trip.RepeatRejections,
            MobilityRevision=_mobilityRevision,PlanMobilityRevision=_planMobilityRevision,DeferredSearches=_deferredSearches,SearchCacheHits=_knowledge.Search.CacheHits,
            FailureMemory=_failures.Records,ProgressActivity=_watchdog.Activity.ToString(),
            SearchWaitUpdates=_watchdog.SearchUpdates,NoProgressUpdates=_watchdog.NoProgressUpdates,
            ActualMovingUpdates=_watchdog.ActualMovingUpdates,ActualTravelPixels=_watchdog.ActualTravelPixels,
            SearchExpansionHits=_knowledge.Search.ExpansionHits,SurfaceGraph=_surfaceSnapshot};
    }
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
