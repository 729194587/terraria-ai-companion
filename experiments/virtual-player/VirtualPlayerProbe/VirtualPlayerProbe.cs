using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace VirtualPlayerProbe;

public sealed class VirtualPlayerProbe : Mod
{
    internal const string EngineVersion = "2026.8.3.0";
    internal const string EngineCommit = "c9545cf27d56c456321e1896274c02adf67d0140";
    internal const string EngineHash = "FCC6A9624B12191BE4A15D714A3675440911FEB8C1D374642AB1FD686F0DA0A5";
    internal const string Blocker = "G1 blocked: UpdateConnectedClients deactivates unconnected Players; " +
        "FindNextOpenClientSlot does not reserve active Player slots; SyncOnePlayer requires State=10. " +
        "No safe no-client actor is admitted. See docs/VIRTUAL_PLAYER_SPIKE.md.";

    internal static bool InvestigatedBuild { get; private set; }

    public override void Load()
    {
        string hash = "unavailable";
        try
        {
            using var stream = File.OpenRead(typeof(Main).Assembly.Location);
            hash = Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception e)
        {
            Logger.Warn($"Engine fingerprint unavailable: {e.GetType().Name}");
        }
        InvestigatedBuild = BuildInfo.tMLVersion.ToString() == EngineVersion
            && BuildInfo.CommitSHA == EngineCommit && hash == EngineHash;
        Logger.Info(JsonSerializer.Serialize(new {
            probe = "VirtualPlayerProbe", eventName = "engine", version = BuildInfo.tMLVersion.ToString(),
            commit = BuildInfo.CommitSHA, sha256 = hash, investigatedBuild = InvestigatedBuild,
            actorCapability = "blocked", detail = Blocker
        }));
    }

    public override void Unload() => InvestigatedBuild = false;
}

// Supported hooks only. No Player/RemoteClient allocation, slot write, network-state
// spoofing, IL detour, socket, file-save, position write or LocalPlayer substitution.
public sealed class ProbeSystem : ModSystem
{
    private string session = "none";
    private long ticks;
    private int candidate = -1;
    private int packetAttempts;
    private int pending;
    private string outcome = "not attempted";

    public override void OnWorldLoad()
    {
        session = Guid.NewGuid().ToString("N");
        ticks = 0;
        candidate = -1;
        outcome = "not attempted";
        packetAttempts = 0;
        Log("world-load");
    }

    public override void OnWorldUnload()
    {
        Log("teardown", "world unload; actorSlot=-1, no engine state to restore");
        session = "none";
        candidate = -1;
        outcome = "not attempted";
    }

    public override void Unload() => Mod.Logger.Info("VirtualPlayerProbe teardown: mod unload; actorSlot=-1");

    public override void PostUpdatePlayers()
    {
        ticks++;
        if (candidate >= 0 && (ticks <= 10 || ticks % 60 == 0))
            Log("sample", "candidate is observational, not reserved or owned");
    }

    // Observes entry to SendData only; return false preserves native serialization.
    // Counts are NOT delivery acknowledgements or evidence of remote visibility.
    public override bool HijackSendData(int whoAmI, int msgType, int remoteClient, int ignoreClient,
        Terraria.Localization.NetworkText text, int number, float number2, float number3,
        float number4, int number5, int number6, int number7)
    {
        if (candidate >= 0 && number == candidate && msgType is MessageID.SyncPlayer
            or MessageID.PlayerActive or MessageID.PlayerControls or MessageID.PlayerSpawn)
        {
            Interlocked.Increment(ref packetAttempts);
            if (msgType != MessageID.PlayerControls)
                Mod.Logger.Info(JsonSerializer.Serialize(new { probe = "VirtualPlayerProbe",
                    eventName = "packet-attempt", session, netMode = Main.netMode, slot = number,
                    msgType, remoteClient, ignoreClient, number2,
                    thread = Environment.CurrentManagedThreadId, delivered = "unknown" }));
        }
        return false;
    }

    internal void Enqueue(CommandCaller caller, string[] arguments)
    {
        if (Interlocked.CompareExchange(ref pending, 1, 0) != 0)
        {
            caller.Reply("VirtualPlayerProbe: command already pending.");
            return;
        }
        string requestedSession = session;
        Player requester = caller.Player;
        string[] args = (string[])arguments.Clone();
        Main.QueueMainThreadAction(() => {
            try
            {
                if (session == "none" || session != requestedSession || Main.gameMenu || Netplay.Disconnect)
                {
                    caller.Reply("VirtualPlayerProbe: no current world session (or session changed).");
                    return;
                }
                // Console has physical server access. Chat uses the native server-side
                // host predicate, never a caller-supplied slot/name or client flag.
                bool console = caller.CommandType == CommandType.Console;
                int slot = requester?.whoAmI ?? -1;
                bool host = Main.netMode == NetmodeID.Server && slot >= 0 && slot < Main.maxPlayers
                    && ReferenceEquals(Main.player[slot], requester) && requester.active
                    && Netplay.Clients[slot].IsConnected() && NetMessage.DoesPlayerSlotCountAsAHost(slot);
                string action = args.Length > 0 ? args[0] : "status";
                if (action == "status" && args.Length <= 1)
                {
                    // A remote human may collect a local read-only snapshot for G3.
                    Log("status", $"requester={slot}");
                    caller.Reply($"VirtualPlayerProbe: session={session}, netMode={Main.netMode}, " +
                        $"ticks={ticks}, actorSlot=-1, candidate={candidate}, G1={outcome}; " +
                        "G2/G3=not attempted. Full snapshot in this process's tML log.");
                    return;
                }
                if (Main.netMode != NetmodeID.Server || !(console || host))
                {
                    Log("denied", $"action={action}, requester={slot}");
                    caller.Reply("VirtualPlayerProbe: mutations require server console or native Host & Play host.");
                    return;
                }
                if (!VirtualPlayerProbe.InvestigatedBuild)
                {
                    caller.Reply("VirtualPlayerProbe: unsupported engine fingerprint; fail closed. Re-run G0.");
                    Log("unsupported-engine");
                    return;
                }
                switch (action)
                {
                    case "spawn" when args.Length == 1:
                        candidate = FindCandidate();
                        outcome = "blocked";
                        Log("spawn-refused", candidate < 0 ? "no free candidate" : VirtualPlayerProbe.Blocker);
                        caller.Reply(candidate < 0 ? "VirtualPlayerProbe: no free candidate slot."
                            : $"VirtualPlayerProbe: candidate={candidate}; {VirtualPlayerProbe.Blocker}");
                        break;
                    case "move" when args.Length == 2 && args[1] is "left" or "right" or "jump" or "stop":
                        Log("move-refused", "G1 has not passed; no actor exists");
                        caller.Reply("VirtualPlayerProbe: G1 blocked; movement not attempted.");
                        break;
                    case "despawn" when args.Length == 1:
                        Log("teardown", "idempotent despawn; actorSlot=-1, no slot was changed");
                        candidate = -1;
                        caller.Reply("VirtualPlayerProbe: no actor to despawn; observation cleared.");
                        break;
                    default:
                        caller.Reply("Usage: /vplayer status|spawn|move left|right|jump|stop|despawn");
                        break;
                }
            }
            catch (Exception e)
            {
                Mod.Logger.Error("VirtualPlayerProbe command failed; actorSlot=-1", e);
                caller.Reply("VirtualPlayerProbe: command failed; see log. No actor allocated.");
            }
            finally { Interlocked.Exchange(ref pending, 0); }
        });
    }

    private static int FindCandidate()
    {
        // This is a snapshot, NOT a reservation. The network thread can claim it.
        for (int i = Math.Min(Main.maxPlayers, Main.maxNetPlayers) - 1; i >= 0; i--)
        {
            RemoteClient c = Netplay.Clients[i];
            if (i != Main.myPlayer && !Main.player[i].active && c != null
                && !c.IsConnected() && !c.IsActive && c.State == 0 && !c.PendingTermination)
                return i;
        }
        return -1;
    }

    private void Log(string eventName, string detail = "")
    {
        var players = new List<object>();
        var connected = new List<int>();
        if (Main.netMode == NetmodeID.Server)
            for (int i = 0; i < Main.maxPlayers; i++)
                if (Netplay.Clients[i]?.IsConnected() == true) connected.Add(i);
        for (int i = 0; i < Main.maxPlayers; i++)
        {
            Player p = Main.player[i];
            if (p == null || (!p.active && i != candidate)) continue;
            RemoteClient c = Main.netMode == NetmodeID.Server ? Netplay.Clients[i] : null;
            players.Add(new { slot = i, p.name, p.active, p.dead, p.whoAmI,
                x = p.position.X, y = p.position.Y, vx = p.velocity.X, vy = p.velocity.Y,
                p.controlLeft, p.controlRight, p.controlJump, p.direction,
                bodyFrame = p.bodyFrame.Y, legFrame = p.legFrame.Y,
                preUpdates = p.GetModPlayer<ProbePlayer>().PreUpdates,
                postUpdates = p.GetModPlayer<ProbePlayer>().PostUpdates,
                connected = c?.IsConnected(), state = c?.State, remoteActive = c?.IsActive });
        }
        Mod.Logger.Info(JsonSerializer.Serialize(new { probe = "VirtualPlayerProbe", eventName,
            utc = DateTime.UtcNow.ToString("O"), session, world = Main.worldID,
            netMode = Main.netMode, Main.dedServ, Main.myPlayer,
            thread = Environment.CurrentManagedThreadId, ticks, actorSlot = -1, candidate,
            g1 = outcome, g2 = "not attempted", g3 = "not attempted",
            packetAttempts = Volatile.Read(ref packetAttempts), connectedSlots = connected,
            connectedMeaning = Main.netMode == NetmodeID.Server ? "server socket snapshot" : "unavailable on client",
            players, detail }));
    }
}

public sealed class ProbePlayer : ModPlayer
{
    internal long PreUpdates;
    internal long PostUpdates;
    public override void PreUpdate() => PreUpdates++;
    public override void PostUpdate() => PostUpdates++;
}

public sealed class ProbeCommand : ModCommand
{
    public override string Command => "vplayer";
    public override CommandType Type => CommandType.World | CommandType.Console;
    public override string Description => "Issue #8: no-client Player diagnostics; spawn fails closed at G1.";
    public override string Usage => "/vplayer status|spawn|move left|right|jump|stop|despawn";
    public override void Action(CommandCaller caller, string input, string[] args)
        => ModContent.GetInstance<ProbeSystem>().Enqueue(caller, args);
}

// World commands are forwarded to the server by native command routing. A separate
// Chat command is necessary to capture the observer's actual local player array.
public sealed class LocalSnapshotCommand : ModCommand
{
    public override string Command => "vplayerlocal";
    public override CommandType Type => CommandType.Chat;
    public override string Description => "Log this human client's local Player snapshot (read only).";
    public override void Action(CommandCaller caller, string input, string[] args)
        => ModContent.GetInstance<ProbeSystem>().Enqueue(caller, new[] { "status" });
}
