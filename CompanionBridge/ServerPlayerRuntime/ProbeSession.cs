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
    internal static long ActorGeneration;
    private static Player previous;
    private static int slot = -1;
    private static long spawnedAt;
    private static bool connectedHook;
    private static bool creating, finishing;
    internal static string WorldSession = "none";
    internal static string Phase = "not armed";
    internal static string StatusText()
    {
        var p=Actor;
        string health=Quarantined?"已隔离，动作停止":VirtualPlayerEngineProbe.Fault!=null?"故障，动作停止":p==null?"尚未加载 AI":p.dead?"AI 已死亡，等待原生复活":"AI 在线";
        string text=$"[AI 状态] {health}；角色档案 {CharacterStore.DisplayId}";
        if(p!=null)
        {
            text+=$"\n槽位 {p.whoAmI}；生命 {p.statLife}/{p.statLifeMax2}；位置 Tile ({(int)Math.Floor(p.position.X/16)},{(int)Math.Floor(p.position.Y/16)})";
            text+="\n"+ActorItemUse.Summary();
            text+="\n"+ActorControl.Summary();
            text+="\n"+ActorMining.Summary();
        }
        if(VirtualPlayerEngineProbe.Fault!=null)text+="\n故障："+VirtualPlayerEngineProbe.Fault;
        return text;
    }
    internal static int Pending;
    internal static volatile bool Quarantined;
    internal static StartupReservation Reservation => VirtualPlayerEngineProbe.Reservation;

    internal static void Reset()
    {
        ActorControl.Reset();
        ActorItemUse.Reset();ActorMining.Reset();
        ActorGeneration = 0;
        ProbeDebugAppearance.Clear();
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
                if (NativeSurvival.ManagedDead(Actor)) throw new InvalidOperationException("AI is waiting for native respawn; do not spawn another Player.");
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
                int x = Main.spawnTileX * 16 + 64, y = Main.spawnTileY * 16 - 42;
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
                // Loaded profiles retain their inventory; only a genuinely new
                // character receives vanilla starting items.
                var candidate = CharacterStore.AcquireCandidate() ?? CharacterStore.CreateNewCandidate();
                candidate.whoAmI = view.Slot;
                candidate.position = new Vector2(x, y);
                candidate.velocity = Vector2.Zero;
                candidate.SpawnX = candidate.SpawnY = -1;
                candidate.controlLeft = candidate.controlRight = candidate.controlUp = candidate.controlDown = false;
                candidate.controlJump = candidate.controlUseItem = candidate.controlUseTile = false;
                candidate.dead = false; candidate.respawnTimer = 0; candidate.fallStart = y / 16;
                if (!ReferenceEquals(Reservation.Snapshot, view) || Quarantined || VirtualPlayerEngineProbe.Fault != null
                    || Main.maxNetPlayers != view.Capacity || slot != view.Slot || previous == null || previous.active
                    || Main.player == null || slot >= Main.player.Length || Netplay.Clients == null || slot >= Netplay.Clients.Length
                    || !ReferenceEquals(Main.player[slot], previous) || !VirtualPlayerEngineProbe.ConnectionIdle(Netplay.Clients[slot]))
                {
                    Quarantine("Player construction changed reserved slot ownership; candidate was not published.");
                    throw new InvalidOperationException("Publication refused; reservation quarantined.");
                }
                ActorGeneration = checked(ActorGeneration + 1);
                Actor = candidate;
                NetworkStateSync.Reset();
                ProbeAnnouncements.ResetNotices();
                Main.player[slot] = candidate;
                candidate.active = true; // Exactly once; never repaired by update/network hooks.
                spawnedAt = Interlocked.Read(ref VirtualPlayerEngineProbe.NetworkLoops);
                Phase = "actor exists; G2 visibility requires client verification";
                connectedHook = true;
                Player.Hooks.PlayerConnect(slot); // Includes native pressure-plate reset.
                if (Main.player == null || slot >= Main.player.Length || Netplay.Clients == null || slot >= Netplay.Clients.Length
                    || !ReferenceEquals(Main.player[slot], candidate) || candidate.whoAmI != slot || previous.active
                    || !VirtualPlayerEngineProbe.ConnectionIdle(Netplay.Clients[slot]))
                {
                    Quarantine("PlayerConnect changed actor/connection ownership.");
                    throw new InvalidOperationException("Connect callback changed ownership; reservation quarantined.");
                }
                NetworkStateSync.Full(candidate);
                Log("spawn");
                ProbeAnnouncements.Join(candidate);
            }
            catch
            {
                creating = false;
                Finish("spawn failed", keepReservation: true);
                throw;
            }
            finally { creating = false; }
        }
    }

    internal static void Finish(string reason, bool keepReservation = false)
    {
        if (Reservation.Snapshot == null && Actor == null) return;
        // The pre-clearWorld fuse aborts a foreign-thread unload BEFORE native
        // world clear / hook removal. No Player is touched from that thread.
        Reservation.AssertOwner(Program.IsMainThread);
        lock (Reservation.Gate)
        {
            if (creating || finishing) throw new InvalidOperationException("Reentrant Player lifecycle rejected.");
            if (Quarantined) throw new InvalidOperationException("STOP: slot remains quarantined; do not retry or unload in place.");
            CharacterStore.AssertMayRetire(Actor);
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
            ActorMining.Retire(owned);ActorItemUse.Retire(owned);
            ActorControl.Retire(owned);
            WorldCharacterPersistence.BeforeRetire(owned);
            finishing = true;
            try
            {
                try
                {
                    if (owned != null)
                    {
                        NetworkStateSync.Leave(owned);
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
                    if (same)
                    {
                        ProbeAnnouncements.Left(owned);
                        Main.player[slot] = previous;
                    }
                    Actor = null;
                    WorldCharacterPersistence.AfterRetire(owned);
                    connectedHook = false;
                    ProbeDebugAppearance.Clear();
                    Phase = keepReservation ? "reserved; no actor" : "released; fresh server process required to re-arm";
                    try { Log("teardown", new { reason, same, occupied }); }
                    finally
                    {
                        // Native allocator may reuse the slot only AFTER actor cleanup.
                        if (!keepReservation) { Reservation.Release(); previous = null; slot = -1; }
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
            fullSnapshots = NetworkStateSync.FullSnapshots, controlPackets = NetworkStateSync.Controls,
            departures = NetworkStateSync.Departures,
            joinNotices = ProbeAnnouncements.JoinNotices, leaveNotices = ProbeAnnouncements.LeaveNotices,
            noticeFailures = ProbeAnnouncements.NoticeFailures,
            fault = VirtualPlayerEngineProbe.Fault, liveGateResult = "not automatically assessed", detail }));
    }
}

public sealed class ProbeSystem : ModSystem
{
    private Guid? loadedWorld;
    public override void OnWorldLoad()
    {
        Guid world=Main.ActiveWorldFileData?.UniqueId??Guid.Empty;
        if(loadedWorld.HasValue){if(loadedWorld==world)return;throw new InvalidOperationException("World changed without unloading the previous lifecycle.");}
        loadedWorld=world;ProbeTargeting.Reset();ProbeSession.WorldSession=Guid.NewGuid().ToString("N");
    }
    public override void OnWorldUnload()
    {
        ProbeSession.Finish("OnWorldUnload");
        ProbeTargeting.Reset();
        ProbeSession.WorldSession = "none";
        loadedWorld=null;
    }
    public override void PostUpdatePlayers()
    {
        CharacterSelfTest.Update();
        ActorControl.CheckBinding();
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
        if (p.whoAmI != view.Slot || !ReferenceEquals(Main.player[view.Slot], p) || !p.active || p.dead && !NativeSurvival.ManagedDead(p))
            VirtualPlayerEngineProbe.SignalFault("Actor reference/activity/death changed; no automatic repair or respawn.");
        if (VirtualPlayerEngineProbe.Fault != null) { ProbeSession.Finish("stop condition: " + VirtualPlayerEngineProbe.Fault); return; }
        try { NetworkStateSync.Tick(p); }
        catch (Exception e)
        {
            NetworkStateSync.Fail(e);
            ProbeSession.Finish("G2 synchronization failure");
            return;
        }
        if (Main.GameUpdateCount % 60 == 0) ProbeSession.Log("sample");
    }
}

public sealed class ProbePlayer : ModPlayer
{
    internal long PreUpdates, PostUpdates;
    internal string PersistentMarker = "";
    public override void PreUpdate() => PreUpdates++;
    public override void PostUpdate() => PostUpdates++;
    public override bool PreItemCheck(){if (VirtualPlayerEngineProbe.Instance?.Name == "VirtualPlayerEngineProbe") ProbeTargeting.Capture(Player);return true;}
    public override void PostItemCheck(){if (VirtualPlayerEngineProbe.Instance?.Name == "VirtualPlayerEngineProbe") ProbeTargeting.Observe(Player);}
    public override void PostUpdateRunSpeeds()
    {
        CompanionBridge.ServerIpc.MovementDiscoveryFixture.AtRunSpeeds(Player);
        ActorControl.BeforeMovement(Player);
    }
    public override void PostUpdateBuffs() => CompanionBridge.ServerIpc.ServerAiTerrainFixture.AfterBuffs(Player);
    public override void SaveData(Terraria.ModLoader.IO.TagCompound tag)
    {
        if (Mod.Name == "VirtualPlayerEngineProbe" || ReferenceEquals(Player, ProbeSession.Actor)) tag["g31Marker"] = PersistentMarker;
    }
    public override void LoadData(Terraria.ModLoader.IO.TagCompound tag) => PersistentMarker = tag.GetString("g31Marker");
}

public sealed class ProbeCommand : ModCommand
{
    public override bool IsLoadingEnabled(Mod mod) => mod.Name == "VirtualPlayerEngineProbe";
    public override string Command => "vph";
    public override CommandType Type => CommandType.World | CommandType.Console;
    public override string Usage => "/vph mine <x> <y>|mine-status|mine-stop|item-fixture|slot <1-10>|use|use-stop|item|walk|stop|motion|life|bag|test-hurt|test-death|status|spawn|save [id]|load [id]|profiles|inspect|fixture|test-save-failure|debug-style|resync|despawn";
    public override string Description => "G4 server control experiment; server/host only.";
    public override void Action(CommandCaller caller, string input, string[] args)
    {
        if (Interlocked.CompareExchange(ref ProbeSession.Pending, 1, 0) != 0) { caller.Reply("G1 command pending."); return; }
        string session = ProbeSession.WorldSession;
        Player requester = caller.Player;
        Player commandedActor = ProbeSession.Actor;
        long commandedGeneration = ProbeSession.ActorGeneration;
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
                if (copy.Length > 3) throw new InvalidOperationException(Usage);
                string action = copy.Length == 0 ? "status" : copy[0];
                if ((copy.Length == 3 && action != "mine") || (copy.Length == 2 && action is not ("save" or "load" or "slot"))) throw new InvalidOperationException(Usage);
                if(action is "mine" or "mine-status" or "mine-stop")
                {
                    ActorItemUse.Capture(commandedActor,session,commandedGeneration);
                    if(action=="mine")
                    {
                        if(copy.Length!=3||!int.TryParse(copy[1],out int x)||!int.TryParse(copy[2],out int y))
                            throw new InvalidOperationException("/vph mine <tileX> <tileY>");
                        ActorMining.Start(commandedActor,session,commandedGeneration,x,y);
                    }
                    else if(copy.Length!=1)throw new InvalidOperationException(Usage);
                    if(action=="mine-stop")ActorMining.Stop(commandedActor,session,commandedGeneration);
                    caller.Reply(ActorMining.Summary());return;
                }
                if (action is "item-fixture" or "slot" or "use" or "use-stop" or "item")
                {
                    ActorItemUse.Capture(commandedActor,session,commandedGeneration);
                    if(action=="item-fixture") {caller.Reply(ActorItemUse.Fixture(commandedActor,session,commandedGeneration));return;}
                    if(action=="slot")
                    {
                        if(copy.Length!=2||!int.TryParse(copy[1],out int hotbar))throw new InvalidOperationException("/vph slot 1–10");
                        ActorItemUse.Select(commandedActor,session,commandedGeneration,hotbar-1);
                    }
                    if(action=="use")ActorItemUse.Start(commandedActor,session,commandedGeneration);
                    if(action=="use-stop")ActorItemUse.Stop(commandedActor,session,commandedGeneration);
                    caller.Reply(ActorItemUse.Summary());return;
                }
                if (action == "walk")
                {
                    ActorControl.Start(commandedActor, commandedGeneration, session);
                    caller.Reply("AI 开始向右走约4格；/vph motion 看结果，/vph stop 停止。"); return;
                }
                if (action == "stop")
                {
                    ActorControl.Stop(commandedActor, commandedGeneration, session);
                    caller.Reply(ActorControl.Summary()); return;
                }
                if (action == "motion") { ActorControl.CheckBinding(); caller.Reply(ActorControl.Summary()); return; }
                if (action == "life") { caller.Reply(NativeSurvival.Summary());return; }
                if (action == "bag") { caller.Reply(NativeSurvival.Bag());return; }
                if (action is "test-hurt" or "test-death")
                {
                    NativeSurvival.Debug(commandedActor,session,commandedGeneration,action=="test-death");
                    caller.Reply(NativeSurvival.Summary());return;
                }
                if (action == "status")
                {
                    string status=ProbeSession.StatusText();
                    ProbeSession.Log("status",new{reply=status});
                    caller.Reply(status,Color.LightCyan);return;
                }
                else if (action == "allocator") ProbeSession.Allocator();
                else if (action == "spawn") ProbeSession.Spawn();
                else if (action == "save") CharacterStore.Save(copy.Length == 2 ? copy[1] : null);
                else if (action == "load") CharacterStore.Load(copy.Length == 2 ? copy[1] : null);
                else if (action == "fixture") CharacterSelfTest.Seed();
                else if (action == "inspect") caller.Reply(CharacterStore.Inspect());
                else if (action == "profiles") caller.Reply(CharacterStore.Profiles());
                else if (action == "test-save-failure") CharacterStore.TestSaveFailure();
                else if (action == "debug-style") ProbeDebugAppearance.Toggle(ProbeSession.Actor ?? throw new InvalidOperationException("Spawn the probe first."));
                else if (action == "resync") NetworkStateSync.Full(ProbeSession.Actor ?? throw new InvalidOperationException("Spawn the probe first."));
                else if (action == "despawn") ProbeSession.Finish("authorized despawn", keepReservation: true);
                else throw new InvalidOperationException(Usage);
                if (action == "spawn" && ProbeSession.Actor != null)
                    caller.Reply($"G3.1：已创建 Player，slot={ProbeSession.Actor.whoAmI}。请观察出生点右侧的 G31_NoClient_Probe；/vpc {ProbeSession.Actor.whoAmI} 查看客户端状态。");
                else caller.Reply($"G3.1: {ProbeSession.Phase}; actorSlot={ProbeSession.Actor?.whoAmI ?? -1}; id={CharacterStore.DisplayId}; " +
                    $"fault={VirtualPlayerEngineProbe.Fault ?? "none"}; full={NetworkStateSync.FullSnapshots}, controls={NetworkStateSync.Controls}, left={NetworkStateSync.Departures}.");
            }
            catch (Exception e)
            {
                ProbeSession.Log("command-refused", new { reason = e.Message });
                caller.Reply("G3.1 probe: " + e.Message);
            }
            finally { Interlocked.Exchange(ref ProbeSession.Pending, 0); }
        });
    }
}

// Read-only client diagnostics. The slot is supplied explicitly, never inferred
// from a name as authority. This command cannot publish or mutate any Player.
public sealed class ClientProbeCommand : ModCommand
{
    public override bool IsLoadingEnabled(Mod mod) => mod.Name == "VirtualPlayerEngineProbe";
    public override string Command => "vpc";
    public override CommandType Type => CommandType.Chat;
    public override string Usage => "/vpc <server actor slot>";
    public override string Description => "Read the local client's native Player state for a probe slot.";
    public override void Action(CommandCaller caller, string input, string[] args)
    {
        if (!Program.IsMainThread || Main.netMode != NetmodeID.MultiplayerClient || Main.gameMenu
            || args.Length != 1 || !int.TryParse(args[0], out int id) || id < 0 || id >= Main.maxPlayers)
        { caller.Reply(Usage + " (active multiplayer client required)"); return; }
        var p = Main.player[id];
        ProbeTargeting.Watch(p);
        caller.Reply($"客户端 slot={id}, active={p.active}, name={p.name}, pos=({p.position.X:F1},{p.position.Y:F1}), " +
            $"helmet={p.armor[0].type}, selected={p.selectedItem}。");
        Mod.Logger.Info(JsonSerializer.Serialize(new { probe = "VirtualPlayerEngineProbe", eventName = "client-slot-snapshot",
            utc = DateTime.UtcNow.ToString("O"), role = "graphical-client", slot = id, p.active, p.name,
            x = p.position.X, y = p.position.Y, vx = p.velocity.X, vy = p.velocity.Y,
            p.direction, p.controlLeft, p.controlRight, p.controlJump, p.selectedItem, helmet = p.armor[0].type,
            shirt = new { p.shirtColor.R, p.shirtColor.G, p.shirtColor.B }, readOnlyDiagnostic = true }));
    }
}
