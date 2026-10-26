// Storage and transport fixtures for directly compiling the production G4 adapter.
// These are NOT Terraria engine physics or a running multiplayer game.
using System;
using System.Collections.Generic;
using System.Linq;
namespace Terraria
{
    public struct Vector { public float X,Y; }
    public sealed class Mount { public bool Active; }
    public struct Sleeping { public bool isSleeping; }
    public struct Sitting { public bool isSitting; }
    public sealed class Player
    {
        public int whoAmI;
        public bool active=true,dead,ghost,frozen,stoned,webbed,tongued,shimmering,isControlledByFilm;
        public bool controlLeft,controlRight,controlUp,controlDown,controlJump,controlUseItem,controlUseTile;
        public Vector position,velocity;
        public Mount mount=new(); public Sleeping sleeping; public Sitting sitting;
    }
    public sealed class RemoteClient { public bool Connected,IsActive,PendingTermination,PendingTerminationApproved; public int State; }
    public static class Main { public static Player[] player; public static int netMode=2; public static bool dedServ=true,gameMenu; public static ulong GameUpdateCount; }
    public static class Program { public static bool IsMainThread=true; }
    public static class Netplay { public static RemoteClient[] Clients; public static bool Disconnect; }
    public static class On_Player { public delegate void orig_Update(Player player,int slot); }
}
namespace Terraria.ID { public static class NetmodeID { public const int Server=2; } }
namespace VirtualPlayerEngineProbe
{
    public static class VirtualPlayerEngineProbe
    {
        public static bool Ready=true;
        public static string Fault;
        public static bool ConnectionIdle(Terraria.RemoteClient c)=>c!=null && StartupReservation.ConnectionIdle(c.Connected,c.IsActive,c.State,c.PendingTermination,c.PendingTerminationApproved);
    }
    public static class ProbeSession
    {
        public static Terraria.Player Actor;
        public static long ActorGeneration;
        public static string WorldSession;
        public static StartupReservation Reservation;
        public static bool Quarantined,ThrowLog;
        public static readonly List<string> Events=new();
        public static void Log(string name,object detail) { if(ThrowLog)throw new System.IO.IOException("fixture logger");Events.Add(name); }
        public static void Quarantine(string reason) { Quarantined=true;VirtualPlayerEngineProbe.Fault=reason; }
    }
    public static class NetworkStateSync
    {
        public readonly record struct Snapshot(float X,float Vx,bool Right);
        public static readonly List<Snapshot> Packets=new();
        public static bool ThrowFull,ThrowTick;
        public static void Require(Terraria.Player p)
        {
            ProbeSession.Reservation.AssertOwner(Terraria.Program.IsMainThread);
            var view=ProbeSession.Reservation.Snapshot;
            if(view==null || !ReferenceEquals(ProbeSession.Actor,p) || p.whoAmI!=view.Slot
                || !ReferenceEquals(Terraria.Main.player[view.Slot],p) || !VirtualPlayerEngineProbe.ConnectionIdle(Terraria.Netplay.Clients[view.Slot]))
            {ProbeSession.Quarantine("fixture sync ownership");throw new InvalidOperationException("fixture sync ownership");}
            if(!p.active || p.dead || ProbeSession.Quarantined || !VirtualPlayerEngineProbe.Ready || VirtualPlayerEngineProbe.Fault!=null)
                throw new InvalidOperationException("fixture sync health");
        }
        public static void Full(Terraria.Player p) { Require(p);if(ThrowFull)throw new System.IO.IOException("fixture full send");Packets.Add(new(p.position.X,p.velocity.X,p.controlRight)); }
        public static void Tick(Terraria.Player p) { Require(p);if(ThrowTick)throw new System.IO.IOException("fixture control send");if(Terraria.Main.GameUpdateCount%6==0)Packets.Add(new(p.position.X,p.velocity.X,p.controlRight)); }
        public static void Fail(Exception e)=>VirtualPlayerEngineProbe.Fault=e.Message;
    }
}

namespace VirtualPlayerEngineProbe { internal static class ActorItemUse { internal static bool Running => false; } }

namespace VirtualPlayerEngineProbe { internal static class ActorMining { internal static bool Running => false; } }
