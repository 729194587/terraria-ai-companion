using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Xna.Framework;
using MonoMod.Cil;
using Terraria;
using Terraria.ModLoader;
using Terraria.Net.Sockets;

namespace VirtualPlayerEngineProbe;

public sealed class VirtualPlayerEngineProbe : Mod
{
    internal const string EngineHash = "FCC6A9624B12191BE4A15D714A3675440911FEB8C1D374642AB1FD686F0DA0A5";
    internal const string HooksHash = "595D26FB3BBE27548D030943B5ED2437F690D1C002129BAD21FAFEAEB8118F7A";
    internal const string Commit = "c9545cf27d56c456321e1896274c02adf67d0140";
    private readonly Stack<Action> rollback = new();
    private bool allocatorPatched, cleanupPatched;
    internal static VirtualPlayerEngineProbe Instance;
    internal static bool Ready;
    internal static StartupReservation Reservation = new();
    internal static long NetworkLoops, CleanupSkips, SyncSkips, SectionSkips, Accepts;
    private static int scopeThread;
    private static string fault;
    internal static string Fault => Volatile.Read(ref fault);

    public override void Load()
    {
        Instance = this;
        Ready = false;
        Reservation = new();
        fault = null;
        NetworkLoops = CleanupSkips = SyncSkips = SectionSkips = Accepts = 0;
        scopeThread = 0;
        string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
        try
        {
            if (typeof(Main).Assembly.GetName().Version?.ToString() != "1.4.4.9"
                || BuildInfo.tMLVersion.ToString() != "2026.8.3.0" || BuildInfo.CommitSHA != Commit
                || Hash(typeof(Main).Assembly.Location) != EngineHash
                || Hash(typeof(On_Netplay).Assembly.Location) != HooksHash)
                throw new InvalidOperationException("Unsupported engine/hooks fingerprint; no hooks installed.");
            Logger.Info($"Engine fingerprint confirmed: Terraria 1.4.4.9 / tML {BuildInfo.tMLVersion}; {EngineHash}");
            if (!Main.dedServ) return; // Host graphical window is a normal client.
            if (ModLoader.Mods.Any(m => m != this && m.Name != "ModLoader"))
                throw new InvalidOperationException("G1 isolation requires this probe and tModLoader only.");
            Install(() => IL_Netplay.FindNextOpenClientSlot += PatchAllocator, () => IL_Netplay.FindNextOpenClientSlot -= PatchAllocator);
            Install(() => IL_Netplay.UpdateConnectedClients += PatchCleanup, () => IL_Netplay.UpdateConnectedClients -= PatchCleanup);
            Install(() => On_Netplay.StartServer += StartServer, () => On_Netplay.StartServer -= StartServer);
            Install(() => On_Netplay.UpdateConnectedClients += NetworkTick, () => On_Netplay.UpdateConnectedClients -= NetworkTick);
            Install(() => On_Netplay.OnConnectionAccepted += Accepted, () => On_Netplay.OnConnectionAccepted -= Accepted);
            Install(() => On_NetMessage.SyncOnePlayer += SyncOne, () => On_NetMessage.SyncOnePlayer -= SyncOne);
            Install(() => On_RemoteClient.CheckSection += Sections, () => On_RemoteClient.CheckSection -= Sections);
            Install(() => On_Main.DedServ_PostModLoad += ServerScope, () => On_Main.DedServ_PostModLoad -= ServerScope);
            Install(() => On_WorldGen.clearWorld += ClearWorld, () => On_WorldGen.clearWorld -= ClearWorld);
            Ready = allocatorPatched && cleanupPatched;
            if (!Ready) throw new InvalidOperationException("Incomplete IL hook installation.");
            Logger.Info("G1 hooks ready; no actor exists. One reservation will arm before the first native StartServer.");
        }
        catch (Exception e)
        {
            Ready = false;
            SignalFault(e.Message);
            Undo(); // No server/actor has been started during Mod.Load.
            Logger.Error("G0 blocked; spawn disabled.", e);
        }
    }

    private void Install(Action add, Action remove)
    {
        try { add(); rollback.Push(remove); }
        catch { remove(); throw; }
    }
    private void Undo() { while (rollback.TryPop(out var remove)) remove(); }

    public override void Unload()
    {
        // tML normally calls RemoveAll before this method. clearWorld and the
        // simulation-scope finally must have removed the actor BEFORE that.
        if (ProbeSession.Actor != null || Reservation.Snapshot != null)
            throw new InvalidOperationException("STOP: native unload reached before G1 teardown. Do not retry live spawn.");
        Ready = false;
        Undo();
        Logger.Info("G1 teardown: no actor/reservation; hook subscriptions removed (or already removed by tML).");
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

    // Allocator readers consult metadata only. The view exists before ANY native
    // listener/accept/network loop starts, and can only shrink after actor teardown.
    public static bool IsReserved(int slot) => Reservation.IsReserved(slot);
    private static bool OwnedWithoutConnection(int slot)
    {
        if (!IsReserved(slot)) return false;
        var c = Netplay.Clients[slot];
        if (c != null && Reservation.MaySkipDisconnectedSlot(slot, c.IsConnected(), c.IsActive, c.State, c.PendingTermination)) return true;
        SignalFault("Reserved slot unexpectedly acquired connection state; native human paths retained.");
        return false;
    }
    public static bool SkipCleanup(int slot)
    {
        if (!OwnedWithoutConnection(slot)) return false;
        Interlocked.Increment(ref CleanupSkips);
        return true;
    }
    internal static void SignalFault(string reason) => Interlocked.CompareExchange(ref fault, reason, null);

    private void StartServer(On_Netplay.orig_StartServer orig)
    {
        if (Reservation.Snapshot != null)
        {
            ProbeSession.Finish("unexpected second StartServer");
            SignalFault("Server restart in same process is unsupported.");
        }
        bool armed = Ready && Fault == null && Volatile.Read(ref scopeThread) == Environment.CurrentManagedThreadId
            && Reservation.ArmBeforeListening(Netplay.IsListening, Main.maxNetPlayers);
        if (!armed) SignalFault("Reservation could not arm before listening; fresh Host & Play server process required.");
        ProbeSession.Log("pre-listen-reservation", new { armed, Main.maxNetPlayers });
        try { orig(); }
        catch { ProbeSession.Finish("StartServer exception"); throw; }
        if (armed) ProbeSession.CheckInitialSlot();
    }
    private void NetworkTick(On_Netplay.orig_UpdateConnectedClients orig)
    {
        orig();
        Interlocked.Increment(ref NetworkLoops);
    }
    private void Accepted(On_Netplay.orig_OnConnectionAccepted orig, ISocket socket)
    {
        orig(socket); // Observation only; filtering is in the original allocator.
        Interlocked.Increment(ref Accepts);
        int slot = Array.FindIndex(Netplay.Clients, c => c != null && ReferenceEquals(c.Socket, socket));
        if (IsReserved(slot)) SignalFault("STOP: native accept assigned reserved slot.");
        ProbeSession.Log("native-accept", new { slot, reserved = IsReserved(slot), thread = Environment.CurrentManagedThreadId });
    }
    private void SyncOne(On_NetMessage.orig_SyncOnePlayer orig, int slot, int toWho, int fromWho)
    {
        if (OwnedWithoutConnection(slot)) { Interlocked.Increment(ref SyncSkips); return; }
        orig(slot, toWho, fromWho);
    }
    private void Sections(On_RemoteClient.orig_CheckSection orig, int slot, Vector2 position, int fluff)
    {
        if (OwnedWithoutConnection(slot)) { Interlocked.Increment(ref SectionSkips); return; }
        orig(slot, position, fluff);
    }
    private void ServerScope(On_Main.orig_DedServ_PostModLoad orig, Main self, out bool reload)
    {
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
