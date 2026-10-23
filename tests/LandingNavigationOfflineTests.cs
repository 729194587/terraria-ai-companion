using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using CompanionBridge;
using CompanionBridge.Motion;
using CompanionBridge.Common.Players;
using CompanionBridge.Common.Systems;
using Terraria;

// Actual TCP, control state, player hooks, atomic completion, planner and executor.
// Only the native game world/player adapter is replaced with deterministic physics.
public static class LandingNavigationOfflineTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly LandingRegion Goal = new(1664, 1744, 1552);
    private static CompanionPlayer _hook;
    private static ControlState Controls => CompanionBridge.CompanionBridge.Controls;
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static void Setup()
    {
        if (Controls.SessionId != 0) Controls.EndSession(Controls.SessionId);
        Controls.Reset(); GameNavigation.Cancel();
        Main.myPlayer = 0; Main.netMode = 0; Main.gameMenu = false; Main.maxTilesX = Main.maxTilesY = 1000;
        Main.GameUpdateCount = 1;
        Main.player[0] = new Player { whoAmI = 0, position = new(1600,1558) };
        _hook = new CompanionPlayer { Player = Main.player[0] };
        GameNavigation.World = new MotionOfflineTests.RegionWorld();
        GameNavigation.State = new(1600,1558,0,0,true);
        // Isolate protocol tests from JIT cost; cold-budget behavior has its own audit.
        StateSpacePlanner.Plan(GameNavigation.State, Goal, PhysicsParameters.Basic, GameNavigation.World, budgetMs: 500);
    }
    private static void Tick(Action<string> chat = null)
    {
        Main.GameUpdateCount++;
        var p = Main.LocalPlayer;
        var state = GameNavigation.State;
        Controls.Update(true, Stopwatch.GetTimestamp(), new(true, new(state.Px,state.Py), new(state.Vx,state.Vy),100,100),
            navigation: GameNavigation.Snapshot, sendChat: chat, worldSessionId: ObservationReader.WorldSessionId,
            queryAtomic: AtomicOperations.Query);
        p.controlLeft = p.controlRight = p.controlJump = p.controlUseItem = false;
        _hook.PostUpdateRunSpeeds();
        var input = new PhysicsInput(p.controlRight ? 1 : p.controlLeft ? -1 : 0, p.controlJump);
        GameNavigation.State = PhysicsSimulator.Step(GameNavigation.State, input, PhysicsParameters.Basic, GameNavigation.World);
        state = GameNavigation.State;
        p.position = new(state.Px,state.Py); p.velocity = new(state.Vx,state.Vy);
        _hook.PostUpdate();
    }
    private static ControlRequest Start(int id, LandingRegion region) => ControlState.Parse(JsonSerializer.Serialize(new {
        v=1, id, cmd="start_atomic", operation="navigate_to_landing_region", expectedGeneration=Controls.Generation,
        arguments=region }, Json));
    private static void LeaseAndDeadline()
    {
        var controls = new ControlState(); int session = controls.BeginSession(), id = 0;
        long now = Stopwatch.GetTimestamp();
        var snapshot = new PlayerSnapshot(true,new(1600,1558),new(0,0),100,100);
        ControlReply Apply(ControlRequest request, long time)
        {
            var pending=controls.Enqueue(session,request,time);
            controls.Update(true,time,snapshot,worldSessionId:"lease-world"); return pending.Result;
        }
        ControlRequest Begin() => new(++id,"start_atomic",ExpectedGeneration:controls.Generation,
            Operation:"navigate_to_landing_region",TargetRegion:Goal);
        Apply(Begin(),now); var action=controls.AtomicAction;
        var changed=Apply(new(++id,"renew_move",ActionId:action.ActionId,ExpectedGeneration:action.Generation,
            TargetRegion:new(1664,1744,1504)),now+Stopwatch.Frequency/2);
        Check(!changed.Ok && changed.Status == "target_changed", "Wrong goal renewal rejected explicitly");
        controls.Update(true,now+Stopwatch.Frequency,snapshot);
        Check(controls.AtomicAction.Status == "cancelled" && controls.AtomicAction.Reason == "lease_expired",
            "Rejected renewal cannot extend region lease");
        controls.ConfirmMovementInputReleased("lease-world",1,"PostUpdateRunSpeeds_before_bridge_input",true);
        now+=2*Stopwatch.Frequency; Apply(Begin(),now); action=controls.AtomicAction;
        for(int half=1;half<=60;half++)
            Apply(new(++id,"renew_move",ActionId:action.ActionId,ExpectedGeneration:action.Generation,TargetRegion:Goal),now+half*Stopwatch.Frequency/2);
        Check(controls.AtomicAction.Status == "timeout" && !controls.IsMoving, "Region absolute deadline is never extended by renewal");
        controls.EndSession(session);
    }

    public static void Run()
    {
        foreach (var args in new[] {
            "{\"minPlayerLeftX\":10,\"maxPlayerLeftX\":1,\"surfaceY\":100}",
            "{\"minPlayerLeftX\":true,\"maxPlayerLeftX\":10,\"surfaceY\":100}",
            "{\"minPlayerLeftX\":0,\"maxPlayerLeftX\":10,\"surfaceY\":1e999}",
            "{\"minPlayerLeftX\":0,\"maxPlayerLeftX\":10}",
            "{\"minPlayerLeftX\":0,\"maxPlayerLeftX\":10,\"surfaceY\":100,\"extra\":1}" })
        {
            bool rejected = false;
            try { AtomicParameters.ReadLandingRegion(JsonDocument.Parse(args).RootElement); }
            catch (Exception e) when (e is FormatException or InvalidOperationException) { rejected = true; }
            Check(rejected, "Invalid landing region must be rejected");
        }
        LeaseAndDeadline();
        Setup(); int session = Controls.BeginSession(), id = 0;
        ControlReply Apply(ControlRequest request)
        {
            var pending = Controls.Enqueue(session, request, Stopwatch.GetTimestamp()); Tick(); return pending.Result;
        }
        var start = Start(++id, Goal);
        Check(start.TargetRegion == Goal && start.TargetX == null, "Protocol carries typed two-dimensional goal");
        var reply = Apply(start);
        var action = Controls.AtomicAction;
        Check(reply.Ok && reply.Movement.Action.TargetRegion == Goal && action.DurationMs == 30000, "Start receipt binds fixed region and absolute deadline");
        Check(GameNavigation.Snapshot.TargetRegion == Goal && GameNavigation.Snapshot.PlannedActions > 0,
            "Actual player hook passes region into actual task/planner");
        var changed = Goal with { }; // Immutable value equality is intentional.
        Check(Apply(new(++id,"renew_move", ActionId: action.ActionId, TargetRegion: changed, ExpectedGeneration: action.Generation)).Ok,
            "Same goal renewal keeps the action");
        Check(!Apply(new(++id,"renew_move", ActionId: action.ActionId, TargetRegion: new(1664,1744,1504))).Ok,
            "Renewal cannot change height");
        Check(!Apply(new(++id,"renew_move", ActionId: action.ActionId)).Ok, "Region renewal requires fixed goal guard");
        Check(!Apply(new(++id,"renew_move", ActionId: action.ActionId, TargetRegion: Goal, ExpectedGeneration: action.Generation+1)).Ok,
            "Renewal rejects stale generation");
        Check(!Apply(Start(++id, new(1680,1720,1536))).Ok, "Second action cannot replace active landing goal");
        for (int f=0; f<900 && Controls.AtomicAction.Status == "running"; f++)
        {
            if (f % 10 == 0) Apply(new(++id,"renew_move", ActionId: action.ActionId, TargetRegion: Goal, ExpectedGeneration: action.Generation));
            else Tick();
        }
        action = Controls.AtomicAction;
        Check(action.Completed && Goal.Arrived(GameNavigation.State, GameNavigation.World), "Completion comes from settled two-dimensional landing");
        Check(action.Evidence is NavigationSnapshot nav && nav.TargetRegion == Goal && nav.LastLanding.Settled,
            "Terminal evidence retains target, actual landing and settled feedback");
        Check(action.EndEvidenceStatus == "pending", "Physics completion still needs input-release hook");
        Tick();
        Check(Controls.AtomicAction.EndEvidence != null, "Next actual player hook seals release receipt");
        // Cancellation before another physics step cannot resume the old region task.
        reply = Apply(Start(++id, new(1800,1840,1600))); action = Controls.AtomicAction;
        Check(reply.Ok, "New task after release accepted");
        Apply(new(++id,"cancel_move",ActionId:action.ActionId)); Tick();
        Check(Controls.AtomicAction.Status == "cancelled" && Controls.AtomicAction.EndEvidence != null && !Controls.IsMoving,
            "Exact action cancellation releases input and cannot resume");
        // A same-X goal with the wrong feet Y cannot arrive on the current platform.
        Setup(); session = Controls.BeginSession(); id = 0;
        GameNavigation.State = new(1680,1558,0,0,true);
        Main.LocalPlayer.position = new(1680,1558);
        Apply(Start(++id, Goal));
        Check(Controls.AtomicAction.Status != "completed", "Correct horizontal coordinate does not prove landing height");
        Apply(new(++id,"cancel_move",ActionId:Controls.AtomicAction.ActionId)); Tick();
        Tick();
        reply = Apply(Start(++id, new(16000,16032,1552)));
        Check(reply.Ok && Controls.AtomicAction.Status == "failed" && Controls.AtomicAction.Reason == "landing_region_out_of_world",
            "Actual game dimensions reject out-of-world region before planning");
        Controls.EndSession(session); Controls.Reset();
        Console.WriteLine("PASS: landing target protocol/bounds, immutable renewal, generation, actual player hook -> planner -> executor -> settled result/release; cancellation and wrong-height rejection (synthetic game world)");
    }

    // Launched by Python's real Agent/Bridge integration test, with an ephemeral port.
    public static void RunProtocolHost()
    {
        Setup();
        Main.tile[5,5] = new Tile { HasTile=true, TileType=2 };
        Main.LocalPlayer.inventory=System.Linq.Enumerable.Range(0,59).Select(_=>new Item {type=0,stack=0}).ToArray();
        Main.LocalPlayer.inventory[10]=new Item {type=2,stack=10,pick=50};
        Terraria.GameContent.ContentSamples.ItemsByType[2]=new Item {type=2,stack=1,pick=50,DisplayName="Fixture Pick"};
        using var server = new TcpControlServer(Controls, 0);
        server.Start(); Console.WriteLine("READY " + server.Port); Console.Out.Flush();
        bool received = false;
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed.TotalSeconds < 25)
        {
            if (Controls.SessionId != 0) { received = true; Controls.ObserveChat("landing-e2e"); }
            Tick(text => { Console.WriteLine("CHAT " + text); Console.Out.Flush(); });
            if (received && Controls.SessionId == 0) break;
            Thread.Sleep(10);
        }
    }
}
