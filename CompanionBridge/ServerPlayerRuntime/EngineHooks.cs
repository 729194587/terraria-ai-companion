using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Xna.Framework;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;
using Terraria.Net.Sockets;

namespace VirtualPlayerEngineProbe;

public sealed class VirtualPlayerEngineProbe
{
    internal static EngineProfiles Engine { get; private set; }
    private readonly Stack<Action> rollback = new();
    private bool allocatorPatched, cleanupPatched, syncPatched, savePatched;
    internal static Mod Instance;
    internal static bool Ready;
    internal static StartupReservation Reservation = new();
    internal static long NetworkLoops, CleanupSkips, SyncSkips, SectionSkips, Accepts;
    private static int listenerEntries;
    private static int scopeThread;
    private static string fault;
    internal static string Fault => Volatile.Read(ref fault);

    public void Load(Mod owner)
    {
        Instance = owner;
        Ready = false;
        Reservation = new();
        fault = null;
        NetworkLoops = CleanupSkips = SyncSkips = SectionSkips = Accepts = 0;
        ProbeSession.Reset();
        scopeThread = 0;
        listenerEntries = 0;
        Engine = null;
        try
        {
            Engine = EngineProfiles.Verify(owner);
            Instance.Logger.Info($"Engine fingerprint confirmed: Terraria 1.4.4.9 / tML {Engine.Selected}; {Engine.EngineHash}");
            if (ModLoader.Mods.Any(m => m != Instance && m.Name != "ModLoader"))
                throw new InvalidOperationException("G1 isolation requires this probe and tModLoader only.");
            NetworkStateSync.Reset();
            ProbeAnnouncements.ResetNotices();
            CharacterStore.Reset();
            WorldCharacterPersistence.Reset();
            NativeSurvival.Reset();
            if (Main.dedServ) InstallServerHooks();
            else
            {
                // Current native Host & Play launches a separate -server process.
                // Observe that graphical entrypoint; never host an actor in its client.
                Install(() => On_Main.OnSubmitServerPassword += GraphicalHostLaunch, () => On_Main.OnSubmitServerPassword -= GraphicalHostLaunch);
                Instance.Logger.Info("G1 graphical host-launch hook ready; engine hooks belong to the native -server child process.");
            }
        }
        catch (Exception e)
        {
            Ready = false;
            Engine = null;
            SignalFault(e.Message);
            Undo(); // No server/actor has been started during Mod.Load.
            Instance.Logger.Error("G0 blocked; spawn disabled.", e);
        }
    }

    private void InstallServerHooks()
    {
        Install(() => IL_Netplay.FindNextOpenClientSlot += PatchAllocator, () => IL_Netplay.FindNextOpenClientSlot -= PatchAllocator);
        Install(() => IL_Netplay.UpdateConnectedClients += PatchCleanup, () => IL_Netplay.UpdateConnectedClients -= PatchCleanup);
        Install(() => IL_NetMessage.SyncOnePlayer += PatchPlayerSync, () => IL_NetMessage.SyncOnePlayer -= PatchPlayerSync);
        Install(() => IL_Player.SavePlayer += PatchCharacterSave, () => IL_Player.SavePlayer -= PatchCharacterSave);
        Install(() => On_WorldGen.SaveAndQuit += GuardCharacterExit, () => On_WorldGen.SaveAndQuit -= GuardCharacterExit);
        Install(() => On_Netplay.StartServer += StartServer, () => On_Netplay.StartServer -= StartServer);
        Install(() => On_Netplay.StartListening += Listen, () => On_Netplay.StartListening -= Listen);
        Install(() => On_Netplay.UpdateConnectedClients += NetworkTick, () => On_Netplay.UpdateConnectedClients -= NetworkTick);
        Install(() => On_Netplay.UpdateInMainThread += MainNetworkTick, () => On_Netplay.UpdateInMainThread -= MainNetworkTick);
        Install(() => On_Netplay.OnConnectionAccepted += Accepted, () => On_Netplay.OnConnectionAccepted -= Accepted);
        Install(() => On_NetMessage.SyncOnePlayer += SyncOne, () => On_NetMessage.SyncOnePlayer -= SyncOne);
        Install(() => On_RemoteClient.CheckSection += Sections, () => On_RemoteClient.CheckSection -= Sections);
        Install(() => On_Main.DedServ_PostModLoad += ServerScope, () => On_Main.DedServ_PostModLoad -= ServerScope);
        Install(() => On_WorldGen.clearWorld += ClearWorld, () => On_WorldGen.clearWorld -= ClearWorld);
        NativeSurvival.Install(Install);
        NativeItemOwnership.Install(Install);
        ActorMining.Install(Install);
        ActorPlacement.Install(Install);
        ActorItemUse.Install(Install);
        NativeItemInput.Install(Install);
        ActorGrapple.Install(Install);
        ActorSorting.Install(Install);
        ActorCrafting.Initialize();
        Install(() => On_Player.Update += NativeSurvival.Update, () => On_Player.Update -= NativeSurvival.Update);
        Ready = allocatorPatched && cleanupPatched && syncPatched && savePatched;
        if (!Ready) throw new InvalidOperationException("Incomplete IL hook installation.");
        Instance.Logger.Info("G2 network hooks ready; no actor exists. One reservation will arm before the first native StartServer.");
    }

    private void GraphicalHostLaunch(On_Main.orig_OnSubmitServerPassword orig, Main self)
    {
        if (Main.dedServ || !Program.IsMainThread)
            throw new InvalidOperationException("Unexpected graphical Host & Play launch context.");
        ProbeSession.Log("graphical-host-launch", "orig launches the native -server -autoshutdown child; no credentials logged");
        orig(self); // Includes native process launch and normal host-client connect.
    }

    private void Install(Action add, Action remove)
    {
        try { add(); rollback.Push(remove); }
        catch { remove(); throw; }
    }
    private void Undo() { while (rollback.TryPop(out var remove)) remove(); }

    public void Unload()
    {
        // tML normally calls RemoveAll before this method. clearWorld and the
        // simulation-scope finally must have removed the actor BEFORE that.
        if (ProbeSession.Actor != null || Reservation.Snapshot != null)
            throw new InvalidOperationException("STOP: native unload reached before G1 teardown. Do not retry live spawn.");
        Ready = false;
        Engine = null;
        Undo();
        Instance.Logger.Info("G1 teardown: no actor/reservation; hook subscriptions removed (or already removed by tML).");
        Instance = null;
    }

    private void PatchAllocator(ILContext il)
    {
        EnginePatches.Allocator(il.Method, il.Module.ImportReference(typeof(VirtualPlayerEngineProbe).GetMethod(nameof(IsReserved))));
        allocatorPatched = true;
    }
    private void PatchCleanup(ILContext il)
    {
        EnginePatches.Cleanup(il.Method, il.Module.ImportReference(typeof(VirtualPlayerEngineProbe).GetMethod(nameof(SkipCleanup))));
        cleanupPatched = true;
    }
    private void PatchPlayerSync(ILContext il)
    {
        EnginePatches.PlayerSync(il.Method, il.Module.ImportReference(typeof(VirtualPlayerEngineProbe).GetMethod(nameof(IsNetworkActor))));
        syncPatched = true;
    }
    private void PatchCharacterSave(ILContext il)
    {
        PersistencePatches.SavePlayer(il.Method, il.Module.ImportReference(typeof(CharacterIo).GetMethod(nameof(CharacterIo.IsCharacterFile))));
        savePatched = true;
    }
    private void GuardCharacterExit(On_WorldGen.orig_SaveAndQuit orig, Action callback)
    {
        if (CharacterIo.Active)
        {
            CharacterIo.ExitRequests++;
            throw new InvalidOperationException("Character IO cannot request WorldGen.SaveAndQuit.");
        }
        orig(callback);
    }

    public static bool IsNetworkActor(int slot)
    {
        if (!IsReserved(slot)) return false;
        lock (Reservation.Gate)
            return Main.netMode == Terraria.ID.NetmodeID.Server && Ready && Fault == null
                && !ProbeSession.Quarantined && OwnedWithoutConnection(slot)
                && ProbeSession.Actor.active && (!ProbeSession.Actor.dead || NativeSurvival.ManagedDead(ProbeSession.Actor));
    }

    // Allocator readers consult metadata only. The view exists before ANY native
    // listener/accept/network loop starts, and can only shrink after actor teardown.
    public static bool IsReserved(int slot) => Reservation.IsReserved(slot);
    internal static bool ConnectionIdle(RemoteClient c) => c != null && StartupReservation.ConnectionIdle(
        c.IsConnected(), c.IsActive, c.State, c.PendingTermination, c.PendingTerminationApproved);
    private static bool OwnedWithoutConnection(int slot)
    {
        lock (Reservation.Gate)
        {
            if (!IsReserved(slot)) return false;
            bool inRange = Main.player != null && slot >= 0 && slot < Main.player.Length
                && Netplay.Clients != null && slot < Netplay.Clients.Length;
            if (!inRange || !ConnectionIdle(Netplay.Clients[slot]))
            {
                ProbeSession.Quarantine("Reserved slot acquired unknown/transitioning connection state; native human paths retained.");
                return false;
            }
            var actor = ProbeSession.Actor;
            if (actor == null) return false; // A reservation alone never owns a Player.
            if (!ReferenceEquals(Main.player[slot], actor) || actor.whoAmI != slot)
            {
                ProbeSession.Quarantine("Reserved Player reference/identity changed; native cleanup retained.");
                return false;
            }
            return true;
        }
    }
    public static bool SkipCleanup(int slot)
    {
        if (!OwnedWithoutConnection(slot)) return false;
        Interlocked.Increment(ref CleanupSkips);
        return true;
    }
    internal static void SignalFault(string reason) => Interlocked.CompareExchange(ref fault, reason, null);
    internal static void WarnAnnouncement(Exception error)
    {
        // Chat is cosmetic: a notification failure must not undo a valid actor
        // or prevent an otherwise safe teardown. Do not retry partial broadcasts.
        try { Instance?.Logger.Warn("G2 lifecycle chat notice failed; no retry.", error); }
        catch { /* Logging failure must not change Player lifecycle either. */ }
    }

    private void StartServer(On_Netplay.orig_StartServer orig)
    {
        if (Reservation.Snapshot != null)
        {
            ProbeSession.Finish("unexpected second StartServer");
            SignalFault("Server restart in same process is unsupported.");
        }
        // Netplay.IsListening is initialized TRUE by the engine before any socket
        // exists. Reject actual startup evidence instead; never rewrite that flag.
        bool tcpListenerExists = Netplay.TcpListener != null;
        int entries = Volatile.Read(ref listenerEntries);
        bool startupObserved = tcpListenerExists || entries != 0
            || Interlocked.Read(ref Accepts) != 0 || Interlocked.Read(ref NetworkLoops) != 0;
        bool armed = Ready && Fault == null && Main.dedServ && Program.IsMainThread
            && Volatile.Read(ref scopeThread) == Environment.CurrentManagedThreadId
            && Reservation.ArmBeforeListening(startupObserved, Main.maxNetPlayers);
        if (!armed) SignalFault("Reservation could not arm before listening; fresh Host & Play server process required.");
        ProbeSession.Log("pre-listen-reservation", new { armed, Main.maxNetPlayers,
            legacyIsListening = Netplay.IsListening, tcpListenerExists, listenerEntries = entries });
        try { orig(); }
        catch { ProbeSession.Finish("StartServer exception"); throw; }
        if (armed) { ProbeSession.CheckInitialSlot(); CharacterSelfTest.Update();SurvivalSelfTest.RunIfRequested();ItemUseSelfTest.RunIfRequested();MiningSelfTest.RunIfRequested();PlacementSelfTest.RunIfRequested();PickupOwnershipSelfTest.RunIfRequested(); }
    }
    private bool Listen(On_Netplay.orig_StartListening orig)
    {
        int entry = Interlocked.Increment(ref listenerEntries);
        bool reserved = Reservation.Snapshot != null;
        if (entry == 1 && !reserved)
            SignalFault("First native listener entry had no startup reservation; live spawn disabled.");
        ProbeSession.Log("native-listener-entry", new { entry, reserved });
        return orig(); // Observation only; TCP/Steam and native retries remain intact.
    }
    private void NetworkTick(On_Netplay.orig_UpdateConnectedClients orig)
    {
        lock (Reservation.Gate)
        {
            // Pending-termination branches never reach the narrow IL guard.
            // Detect them before native handling, while leaving that handling intact.
            var view = Reservation.Snapshot;
            if (view != null) OwnedWithoutConnection(view.Slot);
            orig();
            Interlocked.Increment(ref NetworkLoops);
        }
    }
    private void Accepted(On_Netplay.orig_OnConnectionAccepted orig, ISocket socket)
    {
        lock (Reservation.Gate)
        {
            // Includes native selection, Reset, socket assignment and full-server
            // handling. Publish/release cannot interleave with this transaction.
            orig(socket);
            Interlocked.Increment(ref Accepts);
            int slot = -1;
            for (int i = 0; i < Netplay.Clients.Length; i++)
                if (Netplay.Clients[i] != null && ReferenceEquals(Netplay.Clients[i].Socket, socket)) { slot = i; break; }
            if (IsReserved(slot)) ProbeSession.Quarantine("STOP: native accept assigned reserved slot.");
            ProbeSession.Log("native-accept", new { slot, reserved = IsReserved(slot), thread = Environment.CurrentManagedThreadId });
        }
    }
    private void MainNetworkTick(On_Netplay.orig_UpdateInMainThread orig)
    {
        if (Reservation.Snapshot == null) { orig(); return; }
        Reservation.AssertOwner(Program.IsMainThread);
        lock (Reservation.Gate) orig();
    }
    private void SyncOne(On_NetMessage.orig_SyncOnePlayer orig, int slot, int toWho, int fromWho)
    {
        if (!IsReserved(slot)) { orig(slot, toWho, fromWho); return; }
        lock (Reservation.Gate)
        {
            if (!OwnedWithoutConnection(slot)) { orig(slot, toWho, fromWho); return; }
            if (!IsNetworkActor(slot)) { Interlocked.Increment(ref SyncSkips); return; }
            var expected = ProbeSession.Actor;
            try
            {
                Reservation.AssertOwner(Program.IsMainThread);
                orig(slot, toWho, fromWho);
                NativeSurvival.DeadSnapshot(expected,toWho,fromWho);
                NetworkStateSync.SnapshotCompleted(expected);
            }
            catch (Exception e) { NetworkStateSync.Fail(e); throw; }
        }
    }
    private void Sections(On_RemoteClient.orig_CheckSection orig, int slot, Vector2 position, int fluff)
    {
        if (!IsReserved(slot)) { orig(slot, position, fluff); return; }
        lock (Reservation.Gate)
        {
            if (OwnedWithoutConnection(slot)) { Interlocked.Increment(ref SectionSkips); return; }
            orig(slot, position, fluff);
        }
    }
    private void ServerScope(On_Main.orig_DedServ_PostModLoad orig, Main self, out bool reload)
    {
        if (!Main.dedServ || !Program.IsMainThread)
            throw new InvalidOperationException("Dedicated server scope must run on engine Program.MainThread.");
        if (Interlocked.CompareExchange(ref scopeThread, Environment.CurrentManagedThreadId, 0) != 0)
            throw new InvalidOperationException("Concurrent/reentrant server simulation scope is unsupported.");
        try { ProbeSession.Log("server-scope-enter"); orig(self, out reload); }
        finally
        {
            try { ProbeSession.Finish("server simulation scope exited"); }
            finally { Volatile.Write(ref scopeThread, 0); }
        }
    }
    private void ClearWorld(On_WorldGen.orig_clearWorld orig)
    {
        // This is before native clearWorld mutations AND before RemoveAll.
        // Reject unsupported concurrent/background unload rather than racing it.
        ProbeSession.Finish("before native clearWorld");
        orig();
    }
}
