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
    internal static string WorldSession = "none";
    internal static string Phase = "not armed";
    internal static int Pending;
    internal static StartupReservation Reservation => VirtualPlayerEngineProbe.Reservation;

    internal static void CheckInitialSlot()
    {
        Reservation.AssertOwner();
        var view = Reservation.Snapshot;
        if (view == null) return;
        var c = Netplay.Clients[view.Slot];
        if (c == null || c.IsConnected() || c.IsActive || c.State != 0 || c.PendingTermination || Main.player[view.Slot].active)
        {
            VirtualPlayerEngineProbe.SignalFault("Initial dedicated slot is not empty after native initialization.");
            Finish("initial slot validation failed");
            return;
        }
        slot = view.Slot;
        Phase = "reserved; no actor";
        Log("reservation-ready");
    }

    internal static void Spawn()
    {
        Reservation.AssertOwner();
        if (Actor != null) { Log("spawn-idempotent"); return; }
        var view = Reservation.Snapshot ?? throw new InvalidOperationException("No reservation. Restart Host & Play; re-arming is disabled.");
        if (!VirtualPlayerEngineProbe.Ready || VirtualPlayerEngineProbe.Fault != null
            || Main.netMode != NetmodeID.Server || Main.maxNetPlayers != view.Capacity)
            throw new InvalidOperationException("G0 contract or server capacity changed; spawn disabled.");
        CheckInitialSlot();
        if (Reservation.Snapshot == null) throw new InvalidOperationException("Slot validation failed.");
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
        previous = Main.player[view.Slot];
        Actor = new Player { whoAmI = view.Slot, name = "G1_NoClient_Probe", width = 20, height = 42,
            statLife = 100, statLifeMax = 100, statLifeMax2 = 100,
            position = new Vector2(x, y), SpawnX = -1, SpawnY = -1 };
        slot = view.Slot;
        Main.player[slot] = Actor;
        Actor.active = true; // Exactly once; never repaired by update/network hooks.
        spawnedAt = Interlocked.Read(ref VirtualPlayerEngineProbe.NetworkLoops);
        Phase = "actor exists; G1 live result requires user verification";
        try
        {
            connectedHook = true;
            Player.Hooks.PlayerConnect(slot); // Includes native pressure-plate reset.
            Log("spawn");
        }
        catch { Finish("connect hook failed"); throw; }
    }

    internal static void Finish(string reason)
    {
        if (Reservation.Snapshot == null && Actor == null) return;
        // The pre-clearWorld fuse aborts a foreign-thread unload BEFORE native
        // world clear / hook removal. No Player is touched from that thread.
        Reservation.AssertOwner();
        var owned = Actor;
        bool same = owned != null && slot >= 0 && ReferenceEquals(Main.player[slot], owned);
        bool human = slot >= 0 && Netplay.Clients[slot]?.IsConnected() == true;
        try
        {
            if (owned != null)
            {
                if (!human) owned.active = false;
                if (same && !human && connectedHook)
                {
                    connectedHook = false;
                    Player.Hooks.PlayerDisconnect(slot);
                }
                if (!same || human) VirtualPlayerEngineProbe.SignalFault("STOP: actor slot ownership lost; human slot was not restored/overwritten.");
            }
        }
        finally
        {
            if (same && !human) Main.player[slot] = previous;
            Actor = previous = null;
            connectedHook = false;
            Phase = "released; fresh server process required to re-arm";
            try { Log("teardown", new { reason, same, human }); }
            finally
            {
                // Native allocator may reuse the slot only AFTER actor cleanup.
                Reservation.Release();
                slot = -1;
            }
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
        bool owner = Reservation.OwnerThread == Environment.CurrentManagedThreadId;
        object actor = null;
        var humans = new List<object>();
        if (owner && Main.netMode == NetmodeID.Server)
        {
            if (Actor is { } p)
                actor = new { p.whoAmI, p.active, p.dead, x = p.position.X, y = p.position.Y,
                    vx = p.velocity.X, vy = p.velocity.Y,
                    preUpdates = p.GetModPlayer<ProbePlayer>().PreUpdates,
                    postUpdates = p.GetModPlayer<ProbePlayer>().PostUpdates,
                    sameReference = ReferenceEquals(Main.player[p.whoAmI], p),
                    connected = Netplay.Clients[p.whoAmI].IsConnected(),
                    socketType = Netplay.Clients[p.whoAmI].Socket?.GetType().FullName,
                    remoteState = Netplay.Clients[p.whoAmI].State, remoteActive = Netplay.Clients[p.whoAmI].IsActive };
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
            process = Environment.ProcessId, worldSnapshot = owner, gameUpdate = owner ? Main.GameUpdateCount : 0,
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
        if (ProbeSession.Actor == null) return;
        VirtualPlayerEngineProbe.Reservation.AssertOwner();
        var p = ProbeSession.Actor;
        if (!ReferenceEquals(Main.player[p.whoAmI], p) || !p.active || p.dead)
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
