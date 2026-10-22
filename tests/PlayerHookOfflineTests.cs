using System.Linq;
using System;
using CompanionBridge.Common.Players;
using CompanionBridge.Motion;

// Minimal Terraria surface for compiling the actual ModPlayer hook offline.
// These stubs do not claim to reproduce Terraria physics or multiplayer timing.
namespace Terraria
{
    public static class Main
    {
        public static int myPlayer, netMode = 1, maxTilesX = 100, maxTilesY = 100, mouseX, mouseY;
        public static bool dedServ, gameMenu, hasFocus;
        public static ulong GameUpdateCount;
        public static Player[] player = { new(), new() };
        public static Player LocalPlayer => player[myPlayer];
        public static NPC[] npc = { new(), new() };
        public static Item[] item = { new(), new() };
        public static Tile[,] tile = new Tile[100, 100];
        public static bool[] tileFrameImportant = new bool[1000];
        public static TestSections sectionManager = new();
        public static TestView GameViewMatrix = new();
        public static Microsoft.Xna.Framework.Vector2 screenPosition;
        public static double worldSurface = 20, rockLayer = 40;
        public static void NewText(string text) { }
    }
    public sealed class TestSections { public bool Loaded = true; public bool TileLoaded(int x, int y) => Loaded; }
    public sealed class TestView { public Microsoft.Xna.Framework.Vector2 Zoom = Microsoft.Xna.Framework.Vector2.One; }
    public static class Netplay { public static bool Disconnect; }
    public class Entity
    {
        public int whoAmI;
        public bool active = true;
        public Microsoft.Xna.Framework.Vector2 position, velocity;
        public Microsoft.Xna.Framework.Vector2 Center => position;
    }
    public sealed class Player : Entity
    {
        public bool dead, ghost;
        public bool controlLeft, controlRight, controlJump, controlDown, controlUp, controlUseItem, releaseUseItem;
        public float gravDir = 1;
        public int direction = 1, selectedItem, itemAnimation, itemTime, statLife = 100, statLifeMax2 = 100, team;
        public bool ZoneUnderworldHeight, ZoneRockLayerHeight, ZoneDirtLayerHeight, ZoneSkyHeight;
        public string name = "test-player";
        public static int tileTargetX, tileTargetY;
        public Item[] inventory = System.Linq.Enumerable.Range(0, 10).Select(_ => new Item()).ToArray();
        public Item HeldItem => inventory[selectedItem];
    }
    public sealed class NPC : Entity
    {
        public int type, life = 100, lifeMax = 100;
        public bool friendly, dontTakeDamage;
        public string GivenOrTypeName => "test-npc";
        private readonly CompanionBridge.Common.Systems.AtomicNpcIdentity _identity = new();
        public T GetGlobalNPC<T>() where T : Terraria.ModLoader.GlobalNPC => (T)(object)_identity;
        public bool CanBeChasedBy() => active && !friendly && !dontTakeDamage;
    }
    public sealed class Item : Entity
    {
        public int type = 1, stack = 10, pick, createTile = -1, createWall = -1, damage = 10;
        public bool consumable;
        public object ModItem;
        public string Name => "test-item";
        public bool IsAir => type == 0 || stack <= 0;
    }
    public struct Tile { public bool HasTile; public ushort TileType; }
    public static class WorldGen
    {
        public static bool InWorld(int x, int y, int margin) => x >= margin && y >= margin && x < Main.maxTilesX-margin && y < Main.maxTilesY-margin;
        public static bool OpenDoor(int x, int y, int direction) { Main.tile[x,y].TileType = 11; return true; }
        public static bool CloseDoor(int x, int y) { Main.tile[x,y].TileType = 10; return true; }
    }
}
namespace Terraria.ID
{
    public static class TileID { public const int ClosedDoor = 10, OpenDoor = 11; }
    public static class NetmodeID { public const int SinglePlayer = 0, MultiplayerClient = 1; }
}
namespace Terraria.GameInput { public sealed class TriggersSet { } }
namespace Terraria.ModLoader
{
    public class GlobalNPC { public virtual bool InstancePerEntity => false; public virtual GlobalNPC Clone(Terraria.NPC from, Terraria.NPC to) => (GlobalNPC)MemberwiseClone(); }
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
        public virtual bool PreItemCheck() => true;
        public virtual void PostItemCheck() { }
    }
    public sealed class TestMod { public TestLogger Logger = new(); }
    public sealed class TestLogger { public void Info(string text) { } }
    public sealed class ModKeybind { public bool JustPressed; }
}
namespace CompanionBridge.Common.Systems
{
    internal static class ObservationReader
    {
        internal static string WorldSessionId = "hook-test";
        internal static object ReadPlayer(Terraria.Player p) => new { p.statLife };
        internal static object ReadEnvironment(Terraria.Player p) => new { dayTime = true };
        internal static TileCell ReadCell(int x, int y, System.Collections.Generic.Dictionary<(int,int), object> objects) =>
            new(Terraria.WorldGen.InWorld(x,y,0) ? "read" : "out_of_world");
    }
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
        internal static NavigationSnapshot Snapshot => Task.Snapshot;
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

namespace Microsoft.Xna.Framework
{
    public struct Vector2
    {
        public float X, Y;
        public Vector2(float x, float y) { X = x; Y = y; }
        public static Vector2 One => new(1,1);
        public static Vector2 operator +(Vector2 a, Vector2 b) => new(a.X+b.X, a.Y+b.Y);
        public static float Distance(Vector2 a, Vector2 b) => (float)Math.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Y-b.Y)*(a.Y-b.Y));
        public static bool operator ==(Vector2 a, Vector2 b) => a.X == b.X && a.Y == b.Y;
        public static bool operator !=(Vector2 a, Vector2 b) => !(a==b);
        public override bool Equals(object other) => other is Vector2 v && this==v;
        public override int GetHashCode() => HashCode.Combine(X,Y);
    }
}
