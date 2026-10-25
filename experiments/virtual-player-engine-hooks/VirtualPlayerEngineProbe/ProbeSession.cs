using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace VirtualPlayerEngineProbe;

internal static class ProbeSession
{
    internal static Player Actor;
    private static Player previous;
    private static int slot = -1;
    private static long spawnedAt;
    private static bool connectedHook;
    private static bool creating, finishing;
    internal static string WorldSession = "none";
    internal static string Phase = "not armed";
    internal static int Pending;
    internal static volatile bool Quarantined;
    internal static StartupReservation Reservation => VirtualPlayerEngineProbe.Reservation;

    internal static void Reset()
    {
        Actor = previous = null;
        slot = -1;
        spawnedAt = 0;
        connectedHook = creating = finishing = Quarantined = false;
        WorldSession = "none";
        Phase = "not armed";
        Pending = 0;
    }

    // Also callable under Gate from native network callbacks. Never writes Player.
    internal static void Quarantine(string reason)
    {
        Quarantined = true;
        Phase = "quarantined; no Player/slot changes or reservation release";
        VirtualPlayerEngineProbe.SignalFault(reason);
    }

    internal static void CheckInitialSlot()
    {
        Reservation.AssertOwner(Program.IsMainThread);
        lock (Reservation.Gate)
        {
            var view = Reservation.Snapshot;
            if (view == null) return;
            bool inRange = Main.player != null && view.Slot < Main.player.Length
                && Netplay.Clients != null && view.Slot < Netplay.Clients.Length;
            var c = inRange ? Netplay.Clients[view.Slot] : null;
            var candidate = inRange ? Main.player[view.Slot] : null;
            bool firstValidation = previous == null && slot == -1;
            if (!VirtualPlayerEngineProbe.ConnectionIdle(c) || candidate == null || candidate.active
                || !firstValidation && (slot != view.Slot || !ReferenceEquals(candidate, previous)))
            {
                Quarantine("Initial dedicated slot is occupied or its Player ownership is unknown.");
                throw new InvalidOperationException("Initial slot validation failed; reservation quarantined.");
            }
            // Capture the native idle placeholder once, before any spawn. Revalidation
            // must never adopt a replacement object as a new ownership baseline.
            if (firstValidation) { previous = candidate; slot = view.Slot; }
            Phase = "reserved; no actor";
            Log("reservation-ready");
        }
    }

    internal static void Spawn()
    {
        Reservation.AssertOwner(Program.IsMainThread);
        lock (Reservation.Gate)
        {
            if (creating || finishing) throw new InvalidOperationException("Reentrant Player lifecycle rejected.");
            if (Quarantined || VirtualPlayerEngineProbe.Fault != null)
                throw new InvalidOperationException("G1 fault/quarantine; new server process required.");
            var view = Reservation.Snapshot ?? throw new InvalidOperationException("No reservation. Restart Host & Play; re-arming is disabled.");
            if (!VirtualPlayerEngineProbe.Ready || VirtualPlayerEngineProbe.Fault != null
                || Main.netMode != NetmodeID.Server || Main.maxNetPlayers != view.Capacity)
                throw new InvalidOperationException("G0 contract or server capacity changed; spawn disabled.");
            if (Actor != null)
            {
                if (Main.player == null || view.Slot >= Main.player.Length
                    || Netplay.Clients == null || view.Slot >= Netplay.Clients.Length
                    || !ReferenceEquals(Main.player[view.Slot], Actor) || Actor.whoAmI != view.Slot
                    || !VirtualPlayerEngineProbe.ConnectionIdle(Netplay.Clients[view.Slot]))
                {
                    Quarantine("Duplicate spawn found changed actor/connection ownership.");
                    throw new InvalidOperationException("Actor ownership changed; reservation quarantined.");
                }
                if (!Actor.active || Actor.dead)
                {
                    VirtualPlayerEngineProbe.SignalFault("Actor activity/death changed; no automatic respawn.");
                    Finish("inactive actor");
                    throw new InvalidOperationException("Actor stopped; new server process required.");
                }
                Log("spawn-idempotent"); return;
            }
            CheckInitialSlot();
            creating = true;
            try
            {
                int x = Main.spawnTileX * 16, y = Main.spawnTileY * 16 - 42;
                bool clear = false;
                // Initialization only, not a teleport/movement driver. Never clear tiles.
                for (int up = 0; up <= 12; up++)
                {
                    int cy = y - up * 16;
                    if (WorldGen.InWorld(x / 16, cy / 16, 10) && !Collision.SolidCollision(new Vector2(x, cy), 20, 42))
                    { y = cy; clear = true; break; }
                }
                if (!clear) throw new InvalidOperationException("No clear test spawn position; no actor allocated.");
                // Constructors/ModPlayer initialization can throw or reenter. Keep the
                // candidate private, then revalidate the original lease and placeholder.
                var candidate = new Player { whoAmI = view.Slot, name = "G1_NoClient_Probe", width = 20, height = 42,
                    statLife = 100, statLifeMax = 100, statLifeMax2 = 100,
                    position = new Vector2(x, y), SpawnX = -1, SpawnY = -1 };
                if (!ReferenceEquals(Reservation.Snapshot, view) || Quarantined || VirtualPlayerEngineProbe.Fault != null
                    || Main.maxNetPlayers != view.Capacity || slot != view.Slot || previous == null || previous.active
                    || Main.player == null || slot >= Main.player.Length || Netplay.Clients == null || slot >= Netplay.Clients.Length
                    || !ReferenceEquals(Main.player[slot], previous) || !VirtualPlayerEngineProbe.ConnectionIdle(Netplay.Clients[slot]))
                {
                    Quarantine("Player construction changed reserved slot ownership; candidate was not published.");
                    throw new InvalidOperationException("Publication refused; reservation quarantined.");
                }
                Actor = candidate;
                Main.player[slot] = candidate;
                candidate.active = true; // Exactly once; never repaired by update/network hooks.
                spawnedAt = Interlocked.Read(ref VirtualPlayerEngineProbe.NetworkLoops);
                Phase = "actor exists; G1 live result requires user verification";
                connectedHook = true;
                Player.Hooks.PlayerConnect(slot); // Includes native pressure-plate reset.
                if (Main.player == null || slot >= Main.player.Length || Netplay.Clients == null || slot >= Netplay.Clients.Length
                    || !ReferenceEquals(Main.player[slot], candidate) || candidate.whoAmI != slot || previous.active
                    || !VirtualPlayerEngineProbe.ConnectionIdle(Netplay.Clients[slot]))
                {
                    Quarantine("PlayerConnect changed actor/connection ownership.");
                    throw new InvalidOperationException("Connect callback changed ownership; reservation quarantined.");
                }
                Log("spawn");
            }
            catch
            {
                creating = false;
                Finish("spawn failed");
                throw;
            }
            finally { creating = false; }
        }
    }

    internal static void Finish(string reason)
    {
        if (Reservation.Snapshot == null && Actor == null) return;
        // The pre-clearWorld fuse aborts a foreign-thread unload BEFORE native
        // world clear / hook removal. No Player is touched from that thread.
        Reservation.AssertOwner(Program.IsMainThread);
        lock (Reservation.Gate)
        {
            if (creating || finishing) throw new InvalidOperationException("Reentrant Player lifecycle rejected.");
            if (Quarantined) throw new InvalidOperationException("STOP: slot remains quarantined; do not retry or unload in place.");
            var view = Reservation.Snapshot;
            int target = view?.Slot ?? slot;
            var owned = Actor;
            bool inRange = view != null && target >= 0 && Main.player != null && target < Main.player.Length
                && Netplay.Clients != null && target < Netplay.Clients.Length;
            var current = inRange ? Main.player[target] : null;
            var restore = previous;
            bool baselineKnown = inRange && slot == target && previous != null && !previous.active;
            bool same = owned != null && ReferenceEquals(current, owned) && owned.whoAmI == target;
            bool playerSafe = baselineKnown && (owned != null ? same
                : current != null && !current.active && ReferenceEquals(current, previous));
            bool occupied = !inRange || !VirtualPlayerEngineProbe.ConnectionIdle(Netplay.Clients[target]);
            if (occupied || !playerSafe)
            {
                Quarantined = true;
                Phase = "quarantined; no Player/slot changes or reservation release";
                VirtualPlayerEngineProbe.SignalFault("STOP: teardown found connection transition or active/unknown Player ownership; reservation retained.");
                Log("teardown-quarantined", new { reason, same, occupied });
                throw new InvalidOperationException("STOP: occupied/transitioning slot quarantined; native clearWorld/unload must not continue.");
            }
            finishing = true;
            try
            {
                try
                {
                    if (owned != null)
                    {
                        owned.active = false;
                        if (connectedHook)
                        {
                            connectedHook = false;
                            Player.Hooks.PlayerDisconnect(slot);
                        }
                    }
                }
                finally
                {
                    // Disconnect callbacks can mutate/reenter even on the owner thread.
                    // Never restore or release based only on the pre-callback snapshot.
                    bool stillSafe = !Quarantined && ReferenceEquals(Reservation.Snapshot, view)
                        && slot == target && ReferenceEquals(Actor, owned)
                        && ReferenceEquals(previous, restore) && previous != null && !previous.active
                        && Main.player != null && target < Main.player.Length
                        && ReferenceEquals(Main.player[target], current)
                        && (owned == null ? !current.active : owned.whoAmI == target && !owned.active)
                        && Netplay.Clients != null && target < Netplay.Clients.Length
                        && VirtualPlayerEngineProbe.ConnectionIdle(Netplay.Clients[target]);
                    if (!stillSafe)
                    {
                        Quarantined = true;
                        Phase = "quarantined; no Player/slot changes or reservation release";
                        VirtualPlayerEngineProbe.SignalFault("STOP: disconnect callback changed slot ownership; reservation retained.");
                        Log("teardown-quarantined", new { reason, same, occupied });
                        throw new InvalidOperationException("STOP: callback changed ownership; no restoration/release.");
                    }
                    if (same) Main.player[slot] = previous;
                    Actor = previous = null;
                    connectedHook = false;
                    Phase = "released; fresh server process required to re-arm";
                    try { Log("teardown", new { reason, same, occupied }); }
                    finally
                    {
                        // Native allocator may reuse the slot only AFTER actor cleanup.
                        Reservation.Release();
                        slot = -1;
                    }
                }
            }
            finally { finishing = false; }
        }
    }

    internal static int Allocator()
    {
        int next = (int)typeof(Netplay).GetMethod("FindNextOpenClientSlot", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
        if (Reservation.IsReserved(next)) VirtualPlayerEngineProbe.SignalFault("STOP: allocator returned reservation.");
        Log("allocator-snapshot", new { next, reserved = Reservation.IsReserved(next) });
        return next;
    }

    internal static void Log(string eventName, object detail = null)
    {
        var mod = VirtualPlayerEngineProbe.Instance;
        if (mod == null) return;
        var view = Reservation.Snapshot;
        // Network callbacks log metadata only. World/Player snapshots stay on owner.
        bool owner = Reservation.OwnerThread == Environment.CurrentManagedThreadId && Program.IsMainThread;
        object actor = null;
        var humans = new List<object>();
        if (owner && Main.netMode == NetmodeID.Server)
        {
            if (Actor is { } p)
            {
                int id = view?.Slot ?? -1;
                var c = Netplay.Clients != null && id >= 0 && id < Netplay.Clients.Length ? Netplay.Clients[id] : null;
                actor = new { p.whoAmI, p.active, p.dead, x = p.position.X, y = p.position.Y,
                    vx = p.velocity.X, vy = p.velocity.Y,
                    preUpdates = p.GetModPlayer<ProbePlayer>().PreUpdates,
                    postUpdates = p.GetModPlayer<ProbePlayer>().PostUpdates,
                    sameReference = Main.player != null && id >= 0 && id < Main.player.Length && ReferenceEquals(Main.player[id], p),
                    connected = c?.IsConnected(), socketType = c?.Socket?.GetType().FullName,
                    remoteState = c?.State, remoteActive = c?.IsActive,
                    pendingTermination = c?.PendingTermination, terminationApproved = c?.PendingTerminationApproved };
            }
            for (int i = 0; i < Main.maxPlayers; i++)
                if (Netplay.Clients[i]?.IsConnected() == true)
                {
                    Player h = Main.player[i];
                    humans.Add(new { slot = i, h.name, h.active, x = h.position.X, y = h.position.Y,
                        vx = h.velocity.X, vy = h.velocity.Y,
                        preUpdates = h.GetModPlayer<ProbePlayer>().PreUpdates,
                        postUpdates = h.GetModPlayer<ProbePlayer>().PostUpdates });
                }
        }
        mod.Logger.Info(JsonSerializer.Serialize(new { probe = "VirtualPlayerEngineProbe", eventName,
            utc = DateTime.UtcNow.ToString("O"), WorldSession, Phase, thread = Environment.CurrentManagedThreadId,
            process = Environment.ProcessId, role = Main.dedServ ? "native-server" : "graphical-client",
            programMainThread = Program.MainThread?.ManagedThreadId, programIsMainThread = Program.IsMainThread,
            worldSnapshot = owner, gameUpdate = owner ? Main.GameUpdateCount : 0,
            ownerThread = Reservation.OwnerThread, netMode = Main.netMode, Main.dedServ, Main.myPlayer,
            reservation = view, actorSlot = owner ? Actor?.whoAmI ?? -1 : -1, actor, humans,
            networkLoops = Interlocked.Read(ref VirtualPlayerEngineProbe.NetworkLoops),
            loopsSinceSpawn = !owner || Actor == null ? 0 : Interlocked.Read(ref VirtualPlayerEngineProbe.NetworkLoops) - spawnedAt,
            cleanupSkips = Interlocked.Read(ref VirtualPlayerEngineProbe.CleanupSkips),
            syncSkips = Interlocked.Read(ref VirtualPlayerEngineProbe.SyncSkips),
            sectionSkips = Interlocked.Read(ref VirtualPlayerEngineProbe.SectionSkips),
            accepts = Interlocked.Read(ref VirtualPlayerEngineProbe.Accepts),
            fault = VirtualPlayerEngineProbe.Fault, liveGateResult = "not automatically assessed", detail }));
    }
}

public sealed class ProbeSystem : ModSystem
{
    public override void OnWorldLoad() => ProbeSession.WorldSession = Guid.NewGuid().ToString("N");
    public override void OnWorldUnload()
    {
        ProbeSession.Finish("OnWorldUnload");
        ProbeSession.WorldSession = "none";
    }
    public override void PostUpdatePlayers()
    {
        if (VirtualPlayerEngineProbe.Reservation.Snapshot == null) return;
        VirtualPlayerEngineProbe.Reservation.AssertOwner(Program.IsMainThread);
        if (ProbeSession.Quarantined)
        {
            if (Main.GameUpdateCount % 60 == 0) ProbeSession.Log("quarantine-sample");
            return;
        }
        if (ProbeSession.Actor == null) return;
        var p = ProbeSession.Actor;
        var view = VirtualPlayerEngineProbe.Reservation.Snapshot;
        if (p.whoAmI != view.Slot || !ReferenceEquals(Main.player[view.Slot], p) || !p.active || p.dead)
            VirtualPlayerEngineProbe.SignalFault("Actor reference/activity/death changed; no automatic repair or respawn.");
        if (VirtualPlayerEngineProbe.Fault != null) { ProbeSession.Finish("stop condition: " + VirtualPlayerEngineProbe.Fault); return; }
        if (Main.GameUpdateCount % 60 == 0) ProbeSession.Log("sample");
    }
}

public sealed class ProbePlayer : ModPlayer
{
    internal long PreUpdates, PostUpdates;
    public override void PreUpdate() => PreUpdates++;
    public override void PostUpdate() => PostUpdates++;
}

public sealed class ProbeCommand : ModCommand
{
    public override string Command => "vph";
    public override CommandType Type => CommandType.World | CommandType.Console;
    public override string Usage => "/vph status|allocator|spawn|despawn";
    public override string Description => "G1 engine experiment; server/host only, one spawn lifecycle per server process.";
    public override void Action(CommandCaller caller, string input, string[] args)
    {
        if (Interlocked.CompareExchange(ref ProbeSession.Pending, 1, 0) != 0) { caller.Reply("G1 command pending."); return; }
        string session = ProbeSession.WorldSession;
        Player requester = caller.Player;
        var copy = (string[])args.Clone();
        Main.QueueMainThreadAction(() => {
            try
            {
                if (session == "none" || session != ProbeSession.WorldSession || Main.gameMenu || Netplay.Disconnect)
                    throw new InvalidOperationException("No matching active world session.");
                int id = requester?.whoAmI ?? -1;
                bool host = Main.netMode == NetmodeID.Server && id >= 0 && id < Main.maxPlayers
                    && ReferenceEquals(Main.player[id], requester) && requester.active
                    && Netplay.Clients[id].IsConnected() && NetMessage.DoesPlayerSlotCountAsAHost(id);
                if (Main.netMode != NetmodeID.Server || !(host || caller.CommandType == CommandType.Console))
                    throw new InvalidOperationException("Server console or native Host & Play host authorization required.");
                if (copy.Length > 1) throw new InvalidOperationException(Usage);
                string action = copy.Length == 0 ? "status" : copy[0];
                if (action == "status") ProbeSession.Log("status");
                else if (action == "allocator") ProbeSession.Allocator();
                else if (action == "spawn") ProbeSession.Spawn();
                else if (action == "despawn") ProbeSession.Finish("authorized despawn");
                else throw new InvalidOperationException(Usage);
                caller.Reply($"G1 probe: {ProbeSession.Phase}; actorSlot={ProbeSession.Actor?.whoAmI ?? -1}; " +
                    $"fault={VirtualPlayerEngineProbe.Fault ?? "none"}. Details in server.log; no live pass is inferred.");
            }
            catch (Exception e)
            {
                ProbeSession.Log("command-refused", new { reason = e.Message });
                caller.Reply("G1 probe: " + e.Message);
            }
            finally { Interlocked.Exchange(ref ProbeSession.Pending, 0); }
        });
    }
}
