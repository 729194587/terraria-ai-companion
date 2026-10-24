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
        public static bool dedServ, gameMenu, hasFocus, hardMode, expertMode, masterMode;
        public static ulong GameUpdateCount;
        public static Player[] player = { new(), new() };
        public static Player LocalPlayer => player[myPlayer];
        public static NPC[] npc = { new(), new() };
        public static Item[] item = { new(), new() };
        public static Tile[,] tile = new Tile[100, 100];
        public static bool[] tileFrameImportant = new bool[1000];
        public static bool[] tileSolid = new bool[1000], tileHammer = new bool[1000];
        public static TestSections sectionManager = new();
        public static TestView GameViewMatrix = new();
        public static Microsoft.Xna.Framework.Vector2 screenPosition;
        public static double worldSurface = 20, rockLayer = 40;
        public static void NewText(string text) { }
    }
    public sealed class TestSections { public bool Loaded = true; public bool TileLoaded(int x, int y) => Loaded; }
    public sealed class TestView { public Microsoft.Xna.Framework.Vector2 Zoom = Microsoft.Xna.Framework.Vector2.One; }
    public static class Netplay
    {
        public static bool Disconnect;
        public static TestClient[] Clients = Enumerable.Range(0,255).Select(_=>new TestClient()).ToArray();
        public static int GetSectionX(int x) => 0;
        public static int GetSectionY(int y) => 0;
    }
    public sealed class TestClient { public bool[,] TileSections = new bool[,] {{true}}; }
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
        public int jump; public bool releaseJump = true;
        public int width = 20, height = 42;
        public int direction = 1, selectedItem, itemAnimation, itemTime, statLife = 100, statLifeMax2 = 100, team;
        public int chest = -1, chestX, chestY;
        public bool NativeInteractionRange = true;
        public bool autoPaint, releaseUseTile, tileInteractAttempted;
        public bool noBuilding, NativeItemRange = true;
        public bool IsTargetTileInItemRange(Item item) => NativeItemRange;
        public bool InInteractionRange(int x,int y,Terraria.DataStructures.TileReachCheckSettings settings) => NativeInteractionRange;
        public void TileInteractionsCheck(int x,int y)
        {
            if (!releaseUseTile || !tileInteractAttempted) return;
            int type=Main.tile[x,y].TileType;
            if (type is Terraria.ID.TileID.Containers or Terraria.ID.TileID.Containers2)
            {
                var a=Terraria.ObjectData.TileObjectData.TopLeft(x,y);
                OpenChest(a.X,a.Y,chest==Chest.Index ? -1 : Chest.Index);
            }
            else Wiring.HitSwitch(x,y);
        }
        public void OpenChest(int x,int y,int index) { if (Chest.Blocked) return; chest=index; chestX=x; chestY=y; }
        public static bool CanPlayerSmashWall(int x,int y) => WorldGen.WallSmashable;
        public bool ZoneUnderworldHeight, ZoneRockLayerHeight, ZoneDirtLayerHeight, ZoneSkyHeight;
        public string name = "test-player";
        public static int tileTargetX, tileTargetY;
        public Item[] inventory = System.Linq.Enumerable.Range(0, 10).Select(_ => new Item()).ToArray();
        public Item HeldItem => inventory[selectedItem];
    }
    public sealed class NPC : Entity
    {
        public static bool downedBoss1, downedBoss2, downedBoss3;
        public bool townNPC;
        public int damage;
        public object ModNPC;
        public int type, life = 100, lifeMax = 100;
        public bool friendly, dontTakeDamage;
        public string GivenOrTypeName => "test-npc";
        private readonly CompanionBridge.Common.Systems.AtomicNpcIdentity _identity = new();
        public T GetGlobalNPC<T>() where T : Terraria.ModLoader.GlobalNPC => (T)(object)_identity;
        public bool CanBeChasedBy() => active && !friendly && !dontTakeDamage;
    }
    public sealed class Item : Entity
    {
        public int type = 1, stack = 10, pick, axe, hammer, createTile = -1, createWall = -1, damage = 10;
        public bool consumable;
        public Terraria.ModLoader.ModItem ModItem;
        public string DisplayName = "test-item";
        public string Name => DisplayName;
        public bool IsAir => type == 0 || stack <= 0;
        public void TurnToAir() { type=0; stack=0; }
    }
    public struct Tile { public bool HasTile, IsHalfBlock, IsActuated; public ushort TileType, WallType; public int Slope; public short TileFrameX, TileFrameY; }
    public static class Chest
    {
        public static bool Locked, Blocked;
        public static int Index = 2;
        public static int Owner = -1;
        public static int UsingChest(int index) => Owner;
        public static bool IsLocked(int x,int y) => Locked;
        public static int FindChest(int x,int y) => Index;
    }
    public static class Wiring
    {
        public static bool Blocked;
        public static void HitSwitch(int x,int y)
        {
            if (Blocked) return;
            var a=Terraria.ObjectData.TileObjectData.TopLeft(x,y);
            int size=Main.tile[x,y].TileType==Terraria.ID.TileID.Lever ? 2 : 1;
            for(int dx=0;dx<size;dx++) for(int dy=0;dy<size;dy++) Main.tile[a.X+dx,a.Y+dy].TileFrameY ^= 36;
        }
    }
    public static class WorldGen
    {
        public static bool WallSmashable = true;
        public static bool Poundable = true;
        public static bool CanPoundTile(int x,int y) => Poundable;
        public static bool InWorld(int x, int y, int margin) => x >= margin && y >= margin && x < Main.maxTilesX-margin && y < Main.maxTilesY-margin;
        public static bool OpenDoor(int x, int y, int direction) { Main.tile[x,y].TileType = 11; return true; }
        public static bool CloseDoor(int x, int y) { Main.tile[x,y].TileType = 10; return true; }
    }
    // Wire/storage substitute only. Parses the exact small native payloads
    // generated by the production adapter; it does not model Terraria behavior.
    public sealed class MessageBuffer
    {
        public static bool RejectNative;
        public static int Calls;
        public int whoAmI;
        public byte[] readBuffer = new byte[65536];
        public System.IO.BinaryReader reader;
        public System.IO.BinaryWriter writer;
        public System.IO.MemoryStream readerStream,writerStream;
        public void ResetReader() { readerStream=new(readBuffer); reader=new(readerStream); }
        public void GetData(int start,int length,out int message)
        {
            reader.BaseStream.Position=start; message=reader.ReadByte(); Calls++;
            if(RejectNative) return;
            if(message==17)
            {
                int action=reader.ReadByte(),x=reader.ReadInt16(),y=reader.ReadInt16(),data=reader.ReadInt16(); reader.ReadByte();
                if(action==3) Main.tile[x,y].WallType=(ushort)data;
                if(action==2) Main.tile[x,y].WallType=0;
                if(action==7) Main.tile[x,y].IsHalfBlock=!Main.tile[x,y].IsHalfBlock;
                if(action==14) Main.tile[x,y].Slope=data;
            }
            if(message==19)
            {
                int action=reader.ReadByte(),x=reader.ReadInt16(),y=reader.ReadInt16(); reader.ReadByte();
                var a=Terraria.ObjectData.TileObjectData.TopLeft(x,y);
                for(int dy=0;dy<3;dy++) Main.tile[a.X,a.Y+dy].TileType=(ushort)(action==0?11:10);
            }
            if(message==59) Wiring.HitSwitch(reader.ReadInt16(),reader.ReadInt16());
            if(message==31)
            {
                int x=reader.ReadInt16(),y=reader.ReadInt16();
                if(Chest.Owner==-1) Main.player[whoAmI].OpenChest(x,y,Chest.Index);
            }
            if(message==33) { reader.ReadInt16(); Main.player[whoAmI].chest=-1; }
        }
    }
    public static class NetMessage
    {
        public static int Syncs;
        public static void SendData(int message,int remoteClient=-1,int ignoreClient=-1,Terraria.Localization.NetworkText text=null,int number=0,float number2=0,float number3=0,float number4=0,int number5=0,int number6=0,int number7=0)
        {
#if ATOMIC_NETWORK_TESTS
            On_NetMessage.Invoke(Raw,message,remoteClient,ignoreClient,text,number,number2,number3,number4,number5,number6,number7);
#else
            Syncs++;
#endif
        }
        private static void Raw(int message,int remoteClient,int ignoreClient,Terraria.Localization.NetworkText text,int number,float number2,float number3,float number4,int number5,int number6,int number7) { Syncs++; }
        public static void SendTileSquare(int actor,int x,int y,int width,int height) { Syncs++; }
    }
}
namespace Terraria.ID
{
    public static class TileID { public const int ClosedDoor = 10, OpenDoor = 11, Containers = 21, Containers2 = 467, Switches = 136, Lever = 132;
        public static TestSearch Search = new();
        public static class Sets { public static bool[] Platforms = new bool[1000]; }
    }
    public sealed class TestSearch { public string GetName(int type) => "Type"+type; }
    public static class ItemID { public static TestSearch Search = new(); }
    public static class WallID { public static TestSearch Search = new(); }
    public static class NPCID { public static class Sets {
        public static bool[] ActsLikeTownNPC = new bool[1000], CountsAsCritter = new bool[1000], ProjectileNPC = new bool[1000];
    } }
    public static class NetmodeID { public const int SinglePlayer = 0, MultiplayerClient = 1, Server = 2; }
}
namespace Terraria.GameInput { public sealed class TriggersSet { } }
namespace Terraria.ModLoader
{
    public class ModItem { public string FullName = "Test/Item"; }
    public class ModType { public string FullName; }
    public static class TileLoader { public static int TileCount = 1000; public static ModType GetTile(int type) => null; }
    public static class WallLoader { public static int WallCount = 1000; public static ModType GetWall(int type) => null; }
    public static class ItemLoader
    {
        public static bool Usable = true, Consume = true;
        public static bool CanUseItem(Terraria.Item item,Terraria.Player player) => Usable;
        public static bool ConsumeItem(Terraria.Item item,Terraria.Player player) => Consume;
    }
    public class GlobalNPC { public virtual bool InstancePerEntity => false; public virtual GlobalNPC Clone(Terraria.NPC from, Terraria.NPC to) => (GlobalNPC)MemberwiseClone(); }
    public class ModPlayer
    {
        public Terraria.Player Player = new();
        public TestMod Mod = new();
        public virtual void OnEnterWorld() { }
        public virtual void PlayerDisconnect() { }
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
namespace Terraria.GameContent { public static class ContentSamples { public static System.Collections.Generic.Dictionary<int,Terraria.Item> ItemsByType = new(); } }
namespace Terraria.DataStructures { public struct TileReachCheckSettings { public static TileReachCheckSettings Simple => new(); } }
namespace Terraria.ObjectData
{
    public static class TileObjectData
    {
        public static (int X,int Y) TopLeft(int x,int y)
        {
            var t=Terraria.Main.tile[x,y];
            int width=t.TileType==Terraria.ID.TileID.Switches || t.TileType==Terraria.ID.TileID.ClosedDoor ? 1 : 2;
            int height=t.TileType is Terraria.ID.TileID.ClosedDoor or Terraria.ID.TileID.OpenDoor ? 3 : t.TileType==Terraria.ID.TileID.Switches ? 1 : 2;
            return (x-t.TileFrameX/18%width,y-t.TileFrameY/18%height);
        }
    }
}
namespace CompanionBridge.Common.Systems
{
    // Network adapter has its own pure protocol/authority and real-build/IL tests.
    // Game hook fixtures retain an explicit unavailable peer instead of predicting success.
#if !ATOMIC_NETWORK_TESTS
    internal static class AtomicMultiplayer
    {
        internal static void Disconnect(int player) { }
        internal static void BeginInput() { }
        internal static void EndInput() { }
        internal static void After(Terraria.Player player) { }
        internal static bool Before(Terraria.Player player, MovementSnapshot action, ControlRequest request)
        {
            if(Terraria.Main.netMode!=1 || request.Operation is not ("mine_wall" or "place_wall" or "hammer_tile" or "interact_tile")) return false;
            CompanionBridge.Controls.FinishAtomic(action.ActionId,"failed","multiplayer_authority_unavailable_in_hook_fixture",null);
            return true;
        }
    }
#endif
    internal static class TerrainReader
    {
        internal static Func<int,int,char> Cell = (x,y) => '.';
        internal static char ReadCell(int x,int y) => Cell(x,y);
        internal static TerrainSnapshot Read(Terraria.Player p) => new(0,0,16,Array.Empty<string>(),p.width,p.height,
            MotionSupport.SafeLanding(new(p.position.X,p.position.Y,p.velocity.X,p.velocity.Y,Math.Abs(p.velocity.Y)<0.01f),Cell),true);
    }
    internal static class ObservationReader
    {
        internal static string WorldSessionId = "hook-test";
        internal static object PlayerSnapshot;
        internal static object ReadPlayer(Terraria.Player p) => PlayerSnapshot ?? new { p.statLife };
        internal static object ReadEnvironment(Terraria.Player p) => new { dayTime = true };
        internal static TileCell ReadCell(int x, int y, System.Collections.Generic.Dictionary<(int,int), object> objects)
        {
            if (!Terraria.WorldGen.InWorld(x,y,0)) return new("out_of_world");
            if (Terraria.Main.netMode==1 && !Terraria.Main.sectionManager.Loaded) return new("unloaded");
            var t=Terraria.Main.tile[x,y]; return new("read",t.HasTile,t.TileType,t.WallType);
        }
    }
}
namespace CompanionBridge
{
    internal sealed class CompanionBridge
    {
        public Terraria.ModLoader.ModPacket GetPacket(int capacity) => new();
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
            int generation, float? targetX = null, LandingRegion region = null)
        {
            _paired = true;
            return region != null ? Task.Before(session, generation, region, State, PhysicsParameters.Basic, World)
                : Task.Before(session, generation, targetX.Value, State, PhysicsParameters.Basic, World);
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
