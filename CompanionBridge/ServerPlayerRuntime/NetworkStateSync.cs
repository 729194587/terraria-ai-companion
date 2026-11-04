using System;
using System.Reflection;
using Terraria;
using Terraria.ID;

namespace VirtualPlayerEngineProbe;

// One reserved actor. Native SyncOnePlayer owns all identity/equipment payload
// formats and late-join routing; this adapter only schedules bounded refreshes.
internal static class NetworkStateSync
{
    private static readonly MethodInfo nativeFull = typeof(NetMessage).GetMethod("SyncOnePlayer",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, null,
        new[] { typeof(int), typeof(int), typeof(int) }, null)
        ?? throw new MissingMethodException("Native SyncOnePlayer(int,int,int) is unavailable.");
    private static ulong controlTick, fullTick;
    private static bool possiblyVisible, leaveAttempted;
    internal static long FullSnapshots, Controls, Departures;

    internal static void Reset()
    {
        controlTick = fullTick = 0;
        possiblyVisible = leaveAttempted = false;
        FullSnapshots = Controls = Departures = 0;
    }

    internal static void Require(Player expected, bool inactiveAllowed = false)
    {
        var reservation = VirtualPlayerEngineProbe.Reservation;
        reservation.AssertOwner(Program.IsMainThread);
        var view = reservation.Snapshot;
        bool valid = Main.netMode == NetmodeID.Server && view != null && expected != null
            && expected.whoAmI == view.Slot && ReferenceEquals(ProbeSession.Actor, expected)
            && Main.player != null && view.Slot < Main.player.Length && ReferenceEquals(Main.player[view.Slot], expected)
            && Netplay.Clients != null && view.Slot < Netplay.Clients.Length
            && VirtualPlayerEngineProbe.ConnectionIdle(Netplay.Clients[view.Slot]);
        if (!valid)
        {
            ProbeSession.Quarantine("G2 sync ownership/connection changed; no further packets or slot release.");
            throw new InvalidOperationException("G2 sync ownership check failed; slot quarantined.");
        }
        if (ProbeSession.Quarantined || !VirtualPlayerEngineProbe.Ready
                || !inactiveAllowed && (VirtualPlayerEngineProbe.Fault != null || !expected.active || expected.dead && !NativeSurvival.ManagedDead(expected)))
            throw new InvalidOperationException("G2 sync stopped; lifecycle cleanup required.");
    }

    internal static void SnapshotCompleted(Player expected)
    {
        Require(expected);
        FullSnapshots++;
    }

    internal static void Full(Player expected)
    {
        VirtualPlayerEngineProbe.Reservation.AssertOwner(Program.IsMainThread);
        lock (VirtualPlayerEngineProbe.Reservation.Gate)
        {
            Require(expected);
            possiblyVisible = true; // A failed native call may already have sent a prefix.
            try
            {
                nativeFull.Invoke(null, new object[] { expected.whoAmI, -1, -1 });
                Require(expected);
                fullTick = Main.GameUpdateCount;
            }
            catch (Exception e) { Fail(e); throw; }
        }
    }

    internal static void Tick(Player expected)
    {
        VirtualPlayerEngineProbe.Reservation.AssertOwner(Program.IsMainThread);
        lock (VirtualPlayerEngineProbe.Reservation.Gate)
        {
            Require(expected);
            if(expected.dead)return; // Death is sent once; late joins get DeadSnapshot, not repeated remote Spawn/UI events.
            try
            {
                if (Main.GameUpdateCount - controlTick >= 6)
                {
                    NetMessage.SendData(MessageID.PlayerControls, -1, -1, null, expected.whoAmI);
                    Require(expected);
                    Controls++;
                    controlTick = Main.GameUpdateCount;
                }
                // Also refresh appearance and equipment changed by native code.
                // One actor only; use native full serialization every five seconds.
                if (Main.GameUpdateCount - fullTick >= 300) Full(expected);
            }
            catch (Exception e) { Fail(e); throw; }
        }
    }

    internal static void Leave(Player expected)
    {
        VirtualPlayerEngineProbe.Reservation.AssertOwner(Program.IsMainThread);
        lock (VirtualPlayerEngineProbe.Reservation.Gate)
        {
            Require(expected, inactiveAllowed: true);
            if (!possiblyVisible) return;
            if (leaveAttempted) throw new InvalidOperationException("G2 departure was already attempted.");
            leaveAttempted = true;
            try
            {
                // Keep the object/lease intact until the native active=false packet
                // has been constructed. No human SyncDisconnectedPlayer side effects.
                NetMessage.SendData(MessageID.PlayerActive, -1, -1, null, expected.whoAmI, 0);
                Require(expected, inactiveAllowed: true);
                possiblyVisible = false;
                Departures++;
            }
            catch (Exception e)
            {
                ProbeSession.Quarantine("G2 departure failed; actor and reservation retained: " + e.GetType().Name);
                throw;
            }
        }
    }

    internal static void Fail(Exception error) => VirtualPlayerEngineProbe.SignalFault(
        "G2 native synchronization failed: " + (error.InnerException ?? error).Message);
}
