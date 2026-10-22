using System;
using CompanionBridge.Common.Players;
using CompanionBridge.Motion;

// Minimal Terraria surface for compiling the actual ModPlayer hook offline.
// These stubs do not claim to reproduce Terraria physics or multiplayer timing.
namespace Terraria
{
    public static class Main
    {
        public static int myPlayer, netMode = 1;
        public static bool dedServ, gameMenu, hasFocus;
        public static ulong GameUpdateCount;
        public static void NewText(string text) { }
    }
    public static class Netplay { public static bool Disconnect; }
    public sealed class Player
    {
        public int whoAmI;
        public bool active = true, dead, ghost;
        public bool controlLeft, controlRight, controlJump, controlDown, controlUp;
        public PhysicsTestPosition position = new();
    }
    public sealed class PhysicsTestPosition { public float X; }
}
namespace Terraria.ID
{
    public static class NetmodeID { public const int SinglePlayer = 0, MultiplayerClient = 1; }
}
namespace Terraria.GameInput { public sealed class TriggersSet { } }
namespace Terraria.ModLoader
{
    public class ModPlayer
    {
        public Terraria.Player Player = new();
        public TestMod Mod = new();
        public virtual void OnEnterWorld() { }
        public virtual void PreUpdate() { }
        public virtual void UpdateDead() { }
        public virtual void ProcessTriggers(Terraria.GameInput.TriggersSet triggersSet) { }
        public virtual void PostUpdateRunSpeeds() { }
        public virtual void PostUpdate() { }
    }
    public sealed class TestMod { public TestLogger Logger = new(); }
    public sealed class TestLogger { public void Info(string text) { } }
    public sealed class ModKeybind { public bool JustPressed; }
}
namespace CompanionBridge.Common.Systems
{
    internal static class ObservationReader { internal static string WorldSessionId = "hook-test"; }
}
namespace CompanionBridge
{
    internal static class CompanionBridge
    {
        internal static ControlState Controls { get; } = new();
        internal static bool AutoMoveEnabled => Controls.IsMoving;
        internal static Terraria.ModLoader.ModKeybind ToggleAutoMoveKeybind = null;
        internal static void RevokeControl() { GameNavigation.Cancel(); Controls.RevokeControl(); }
        internal static void StopControl() { GameNavigation.Cancel(); Controls.Reset(); }
        internal static void ToggleManual() { StopControl(); }
    }
}
namespace CompanionBridge.Motion
{
    // GameNavigation's Terraria adapter is replaced; the task, executor and
    // planner below are production classes. Count cancellation at the hook boundary.
    internal static class GameNavigation
    {
        internal static NavigationTask Task = new();
        internal static IMotionWorld World = null;
        internal static PhysicsState State;
        internal static int Cancellations;
        private static bool _paired;
        internal static PhysicsInput Before(Terraria.Player player, int session, int direction,
            int generation, float? targetX = null)
        {
            _paired = true;
            return Task.Before(session, generation, targetX.Value, State, PhysicsParameters.Basic, World);
        }
        internal static void After(Terraria.Player player)
        {
            if (_paired) { _paired = false; Task.After(State, World); }
        }
        internal static void Cancel() { Cancellations++; _paired = false; Task.Cancel(); }
    }
}

public static class PlayerHookOfflineTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static void Run()
    {
        var local = new CompanionPlayer { Player = new Terraria.Player { whoAmI = 0 } };
        var remote = new CompanionPlayer { Player = new Terraria.Player { whoAmI = 1,
            controlLeft = true, controlJump = true, controlDown = true } };
        Terraria.Main.myPlayer = 0;
        var controls = CompanionBridge.CompanionBridge.Controls;
        controls.Reset();
        int session = controls.BeginSession();
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        var ack = controls.Enqueue(session, new CompanionBridge.ControlRequest(1, "navigate", TargetX: 1840), now);
        controls.Update(true, now, new CompanionBridge.PlayerSnapshot(true, new(1600, 1558), new(0, 0), 100, 100));
        Check(ack.Result.Ok && controls.IsMoving, "Hook fixture acquires real external navigation lease");
        GameNavigation.World = new MotionOfflineTests.World { WallX = 1664, WallHeight = 16 };
        GameNavigation.State = new(1600, 1558, 0, 0, true);
        GameNavigation.Task = new();
        GameNavigation.Cancellations = 0;
        bool advancedFrame = false;
        for (int f = 0; f < 600 && !GameNavigation.Task.Terminal; f++)
        {
            // Test remote hooks both before and between the local Before/After pair.
            remote.PostUpdateRunSpeeds();
            local.PostUpdateRunSpeeds();
            remote.PostUpdateRunSpeeds();
            Check(GameNavigation.Cancellations == 0, "Remote player must not cancel the shared local task");
            var input = new PhysicsInput(local.Player.controlRight ? 1 : local.Player.controlLeft ? -1 : 0,
                local.Player.controlJump);
            GameNavigation.State = PhysicsSimulator.Step(GameNavigation.State, input,
                PhysicsParameters.Basic, GameNavigation.World);
            remote.PostUpdate();
            local.PostUpdate();
            advancedFrame |= GameNavigation.Task.Snapshot.Frame > 1;
        }
        Check(advancedFrame && GameNavigation.Task.Snapshot.CompletedActions > 0,
            "Interleaved player hooks retain frames and verified action checkpoints");
        Check(GameNavigation.Task.Snapshot.Status == "arrived" && Math.Abs(GameNavigation.State.Vx) <= 0.1f,
            "Local task crosses a synthetic single-block obstacle and settles with remote hooks interleaved: "
            + GameNavigation.Task.Snapshot.Reason);
        remote.PostUpdateRunSpeeds();
        Check(GameNavigation.Task.Snapshot.Status == "arrived", "Remote hook preserves confirmed terminal result");
        Check(remote.Player.controlLeft && remote.Player.controlJump && remote.Player.controlDown,
            "Local navigation never changes remote player's input");

        local.Player.dead = true;
        local.UpdateDead();
        Check(!controls.IsMoving && GameNavigation.Task.Snapshot.Status == "idle", "Local death still revokes navigation");
        int cancelled = GameNavigation.Cancellations;
        remote.PostUpdateRunSpeeds();
        Check(GameNavigation.Cancellations == cancelled, "Remote hook cannot mutate navigation after local death");
        local.Player.dead = false;
        local.PostUpdateRunSpeeds();
        Check(GameNavigation.Cancellations == cancelled + 1, "Local inactive control still clears navigation");
        controls.ToggleManual(now);
        local.PostUpdateRunSpeeds();
        Check(GameNavigation.Cancellations == cancelled + 2 && local.Player.controlRight
            && controls.Mode == CompanionBridge.ControlMode.Manual, "Manual input still cancels predictive navigation");
        controls.Reset();
        Console.WriteLine("PASS: actual player hook isolates remote updates; local obstacle task progresses; terminal, death and manual control preserved (offline stubs)");
    }
}
