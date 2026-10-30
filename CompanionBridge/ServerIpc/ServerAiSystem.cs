using System;
using System.Linq;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.Chat;
using Terraria.Localization;
using Microsoft.Xna.Framework;
using VirtualPlayerEngineProbe;
using EngineRuntime = VirtualPlayerEngineProbe.VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

public sealed class ServerAiSystem : ModSystem
{
    internal static bool Enabled => Environment.GetEnvironmentVariable("COMPANION_SERVER_AI") == "1"
        || Program.LaunchParameters.ContainsKey("-companionserverai");
    private static EngineRuntime runtime;
    private static ServerAiSystem current;
    private static bool callbacks;
    private AiReadOnlyServer server;
    private bool spawnPending;
    internal static void LoadRuntime(Mod owner)
    {
        if (!Main.dedServ || !Enabled) return;
        runtime = new();
        runtime.Load(owner); // Exact engine fingerprints, pre-listen reservation and original quarantine guards.
        if (!EngineRuntime.Ready) return;
        On_Netplay.StartServer += Started;
        Main.OnTickForThirdPartySoftwareOnly += IdleTick;
        callbacks = true;
    }
    internal static void UnloadRuntime()
    {
        if (callbacks) { On_Netplay.StartServer -= Started; Main.OnTickForThirdPartySoftwareOnly -= IdleTick; callbacks = false; }
        current?.Stop();
        runtime?.Unload(); runtime = null;
    }
    public override void Load() => current = this;
    private static void Started(On_Netplay.orig_StartServer orig)
    {
        try { orig(); current?.Begin(); current?.Pump(); }
        catch { current?.Stop(); throw; }
    }
    // Native dedicated servers pause Game.Update when no real clients exist.
    // This native simulation-thread pulse services read-only IPC while paused;
    // it never calls Player.Update or changes HasClients.
    private static void IdleTick()
    {
        if (!Netplay.HasClients) { current?.Pump(); ServerAiActionFixture.Tick(); ServerAiNaturalFixture.Tick(); }
        // Isolated native harness only; no shutdown operation exists on IPC.
        if (Program.LaunchParameters.ContainsKey("-companionaiipctest") && Program.IsMainThread
            && System.IO.File.Exists(System.IO.Path.Combine(Main.SavePath, "g81-test-stop")))
            Netplay.Disconnect = true;
    }
    public override void OnWorldLoad()
    {
        // Native world loading runs on a worker; defer listener/actor creation
        // until the audited StartServer returns on Program.MainThread.
        current = this;
    }
    private void Begin()
    {
        if (!Program.IsMainThread) throw new InvalidOperationException("AI IPC startup requires the simulation thread.");
        if (!Main.dedServ || runtime == null || !EngineRuntime.Ready || EngineRuntime.Fault != null) return;
        if(server!=null)return;
        try
        {
            Program.LaunchParameters.TryGetValue("-companionbridgeport", out var humanArg);
            Program.LaunchParameters.TryGetValue("-companionaiport", out var aiArg);
            int port = AiReadOnlyServer.ParsePort(aiArg ?? Environment.GetEnvironmentVariable("COMPANION_AI_PORT"), TcpControlServer.ParseLaunchPort(humanArg));
            server = new(port, new AiAtomicActions(new ServerAiNativeActions()));
            server.Start();
            spawnPending = true;
            Mod.Logger.Info($"AI IPC listening on 127.0.0.1:{port}; selection/navigation/use/mining enabled; awaiting server reservation.");
        }
        catch (Exception e) { server?.Dispose(); server = null; Mod.Logger.Error("AI IPC unavailable; human paths remain available.", e); }
    }
    public override void PostUpdatePlayers() => Pump();
    private void Pump()
    {
        if (server == null) return;
        if (!Program.IsMainThread) throw new InvalidOperationException("AI IPC pump must run on engine game thread.");
        lock (EngineRuntime.Reservation.Gate)
        {
            if (spawnPending && EngineRuntime.Reservation.Snapshot != null)
            {
                spawnPending = false; // One attempt; never adopt replacements or automatically re-arm.
                try { WorldCharacterPersistence.Start(); }
                catch (Exception e) { EngineRuntime.SignalFault("G8.1 initial actor creation failed: " + e.Message); Mod.Logger.Error("AI creation stopped.", e);WorldCharacterSelfTest.StartFailed(e); }
            }
            WorldCharacterSelfTest.Tick();
            if(Capture()!=null)AiMapRuntime.Tick();
            server.Tick(Capture, Select, ServerAiQueries.Read, Say);
        }
    }
    private static AiReadOnlyProtocol.Identity Capture()
    {
        if (!Main.dedServ || Main.netMode != NetmodeID.Server || Netplay.Disconnect || Main.gameMenu
            || ProbeSession.WorldSession == "none" || !EngineRuntime.Ready || EngineRuntime.Fault != null || ProbeSession.Quarantined||!WorldCharacterPersistence.Ready)
            return null;
        EngineRuntime.Reservation.AssertOwner(Program.IsMainThread);
        var p = ProbeSession.Actor;
        if (p == null) return null;
        NetworkStateSync.Require(p); // Reuses exact object/slot/idle-connection/lifecycle checks; no LocalPlayer fallback.
        var inventory = p.inventory.Select((item, slot) => new { slot, type = item.type, stack = item.stack, prefix = item.prefix, name = item.Name }).ToArray();
        object state = new {
            tick = Main.GameUpdateCount, name = p.name, active = p.active, dead = p.dead, ghost = p.ghost,
            position = new { x = p.position.X, y = p.position.Y }, velocity = new { x = p.velocity.X, y = p.velocity.Y },
            life = p.statLife, lifeMax = p.statLifeMax2, selectedSlot = p.selectedItem,
            selectedItem = p.selectedItem >= 0 && p.selectedItem < inventory.Length ? inventory[p.selectedItem] : null,
            inventory
        };
        return new(p, ProbeSession.WorldSession, ProbeSession.ActorGeneration, p.whoAmI, state, p.selectedItem, Main.GameUpdateCount, !p.dead && !p.ghost);
    }
    private static void Select(AiReadOnlyProtocol.Identity expected, int slot)
    {
        EngineRuntime.Reservation.AssertOwner(Program.IsMainThread);
        // G6 performs its own reference/session/generation, idle and native sync checks.
        var p = (Player)expected.Actor;
        int before = p.selectedItem;
        ActorItemUse.Select(p, expected.World, expected.Generation, slot);
        ProbeSession.Log("atomic-select", new { beforeSlot = before, selectedSlot = p.selectedItem, expected.Generation, expected.World });
    }
    private static object Say(AiReadOnlyProtocol.Identity expected,string text)
    {
        ServerAiNativeActions.Require(expected);
        var p=(Player)expected.Actor;
        if(!p.active||expected.Slot<0||expected.Slot>=255)throw new InvalidOperationException("AI chat author unavailable.");
        // Native NetText serialization carries this author to the name tag and
        // the actual Player.chatOverhead on clients. No client/player spoofing.
        ChatHelper.BroadcastChatMessageAs((byte)p.whoAmI,NetworkText.FromLiteral(text),Color.White);
        ServerAiNativeActions.Require(expected);
        ProbeSession.Log("ai-chat",new{authorSlot=p.whoAmI,expected.World,expected.Generation,text});
        return new{status="published",authorSlot=p.whoAmI,worldSessionId=expected.World,actorGeneration=expected.Generation,gameUpdate=Main.GameUpdateCount};
    }
    private void Stop() { spawnPending = false; server?.Dispose(); server = null; ServerAiActionFixture.Reset(); ServerAiNaturalFixture.Reset(); }
    public override void OnWorldUnload() => Stop();
    public override void Unload() { Stop(); if (ReferenceEquals(current, this)) current = null; }
}
