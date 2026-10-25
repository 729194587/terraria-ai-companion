using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;
using VirtualPlayerEngineProbe;

// These are isolated storage/effect fixtures, NOT game Players, fake connections
// in Terraria, or a live multiplayer test. Installed complete IL drives them.
public static class EngineHookOfflineTests
{
    public const string EngineHash = "FCC6A9624B12191BE4A15D714A3675440911FEB8C1D374642AB1FD686F0DA0A5";
    public const string HooksHash = "595D26FB3BBE27548D030943B5ED2437F690D1C002129BAD21FAFEAEB8118F7A";
    public class EntityState
    {
        public bool active;
        public int whoAmI, width, height;
        public VectorState position, velocity;
    }
    public struct VectorState
    {
        public float X, Y;
        public static VectorState Zero => new(0,0);
        public VectorState(float x, float y) { X = x; Y = y; }
    }
    public class PlayerState : EntityState
    {
        public static Action OnConstruct;
        public int statLife, statLifeMax, statLifeMax2, SpawnX, SpawnY;
        public string name;
        public bool dead, controlLeft, controlRight, controlUp, controlDown, controlJump, controlUseItem, controlUseTile;
        public int respawnTimer, fallStart;
        public PlayerState() => OnConstruct?.Invoke();
    }
    public static class MainState
    {
        public static PlayerState[] player;
        public static int maxNetPlayers;
        public static int netMode, spawnTileX, spawnTileY;
    }
    public class AddressState { public string GetFriendlyName() => "fixture"; }
    public class SocketState
    {
        public bool Connected = true;
        public AddressState GetRemoteAddress() => new();
    }
    public class RemoteState
    {
        public int Id, State, Updates, Resets;
        public bool IsActive, PendingTerminationApproved;
        public bool PendingTermination { get; set; }
        public SocketState Socket;
        public string StatusText2;
        public bool IsConnected() => Socket?.Connected == true;
        public void Update() => Updates++;
        public void SetPendingTermination(string _) => PendingTermination = true;
        public void Reset()
        {
            Resets++; State = 0; IsActive = false; PendingTermination = PendingTerminationApproved = false;
            MainState.player[Id] = new PlayerState();
            Socket = null;
        }
    }
    public static class NetState
    {
        public static RemoteState[] Clients;
        public static bool HasClients, IsListening;
        public static object fullBuffer = new();
        public static int Stops, Kicks;
        public static Func<int> Allocator;
        public static int FindNextOpenClientSlot() => Allocator();
        public static void StopListening() => Stops++;
        public static void KickClient(SocketState _, TextState __) => Kicks++;
    }
    public static class MessageState
    {
        public static List<int> Disconnected = new();
        public static void SyncDisconnectedPlayer(int slot) => Disconnected.Add(slot);
    }
    public static class HookState
    {
        public static List<int> Disconnected = new();
        public static void PlayerDisconnect(int slot) => Disconnected.Add(slot);
    }
    public static class LanguageState { public static string GetTextValue(string key, object value) => key; }
    public static class LoggingState { public static void ServerConsoleLine(string _) { } }
    public class TextState
    {
        public string Key;
        public object[] Args;
        public static TextState FromKey(string key, object[] args) => new() { Key=key, Args=args };
    }
    public static StartupReservation Reservation = new();
    public static PlayerState FixtureActor;
    public static bool Reserved(int slot) => Reservation.IsReserved(slot);
    public static bool CleanupEligible(int slot)
    {
        var c = NetState.Clients[slot];
        return Reservation.IsReserved(slot) && FixtureActor != null && ReferenceEquals(MainState.player[slot], FixtureActor)
            && StartupReservation.ConnectionIdle(c.IsConnected(), c.IsActive, c.State, c.PendingTermination, c.PendingTerminationApproved);
    }
    private static int checks;
    private static string gameDirectory;
    private static bool nativeInitialIsListening;
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    public static int DetourSmokeTarget() => 7;
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL: " + label);
        checks++;
        Console.WriteLine("PASS: " + label);
    }

    public static void Run(string gamePath, string output)
    {
        gameDirectory = gamePath;
        string dll = Path.Combine(gamePath, "tModLoader.dll");
        Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll))) == EngineHash, "exact installed engine fingerprint");
        string hooks = Path.Combine(gamePath, "Libraries/TerrariaHooks/0.0.0.0/TerrariaHooks.dll");
        Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(hooks))) == HooksHash, "exact generated hooks fingerprint");
        using var game = AssemblyDefinition.ReadAssembly(dll);
        using var hookAssembly = AssemblyDefinition.ReadAssembly(hooks);
        var smoke = typeof(EngineHookOfflineTests).GetMethod(nameof(DetourSmokeTarget));
        string runtimeName = null;
        Check((int)smoke.Invoke(null, null) == 7, "RuntimeDetour fixture begins unmodified");
        using (var hook = new ILHook(smoke, il => {
            runtimeName = il.Method.Name;
            Console.WriteLine("RuntimeDetour IL identity: " + runtimeName);
            var value = il.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldc_I4_7);
            value.OpCode = OpCodes.Ldc_I4_8;
        }))
        {
            Check(runtimeName.Contains(nameof(DetourSmokeTarget), StringComparison.Ordinal), "real installed RuntimeDetour embeds target identity in DMD name");
            Check((int)smoke.Invoke(null, null) == 8, "real installed RuntimeDetour installs fixture IL hook");
        }
        Check((int)smoke.Invoke(null, null) == 7, "real installed RuntimeDetour disposal restores original fixture method");
        // Reflect method bodies only: no native method is invoked or detoured,
        // no Terraria Main instance/static initializer/world is started.
        var runtimeEngine = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
        foreach (string name in new[] { "FindNextOpenClientSlot", "UpdateConnectedClients" })
        {
            var target = runtimeEngine.GetType("Terraria.Netplay").GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
            using var dmd = new DynamicMethodDefinition(target);
            using var context = new ILContext(dmd.Definition);
            int before = dmd.Definition.Body.Instructions.Count;
            context.Invoke(il => {
                var test = il.Module.ImportReference(typeof(EngineHookOfflineTests).GetMethod(nameof(Reserved)));
                if (name == "FindNextOpenClientSlot") EnginePatches.Allocator(il.Method, test);
                else EnginePatches.Cleanup(il.Method, test);
            });
            Check(dmd.Definition.Body.Instructions.Count == before + 3, "actual native reflection/DMD/ILContext handler shape: " + name);
        }
        var types = game.MainModule.Types.ToDictionary(t => t.FullName);
        MethodDefinition Method(string type, string name) => types[type].Methods.Single(m => m.Name == name);
        var allocate = Method("Terraria.Netplay", "FindNextOpenClientSlot");
        var cleanup = Method("Terraria.Netplay", "UpdateConnectedClients");
        var accept = Method("Terraria.Netplay", "OnConnectionAccepted");
        var start = Method("Terraria.Netplay", "StartServer");
        var initialize = Method("Terraria.Netplay", "InitializeServer");
        var netDefaults = Method("Terraria.Netplay", ".cctor");
        var listeningDefault = netDefaults.Body.Instructions.Single(i => i.OpCode == OpCodes.Stsfld
            && i.Operand is FieldReference f && f.Name == "IsListening");
        nativeInitialIsListening = listeningDefault.Previous.OpCode == OpCodes.Ldc_I4_1;
        Check(nativeInitialIsListening, "installed Netplay static initializer sets IsListening=true before any listener exists");
        Check(!netDefaults.Body.Instructions.Any(i => i.OpCode == OpCodes.Stsfld
                && i.Operand is FieldReference f && f.Name == "TcpListener")
            && game.MainModule.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody)
                .Where(m => m.Body.Instructions.Any(i => i.OpCode == OpCodes.Stsfld
                    && i.Operand is FieldReference f && f.FullName == "Terraria.Net.Sockets.ISocket Terraria.Netplay::TcpListener"))
                .Select(m => m.FullName).SequenceEqual(new[] { initialize.FullName }),
            "native TcpListener starts null and only InitializeServer assigns it");
        var sync = Method("Terraria.NetMessage", "SyncOnePlayer");
        var unload = Method("Terraria.ModLoader.ModContent", "UnloadModContent");
        var server = Method("Terraria.Main", "DedServ_PostModLoad");
        var hostLaunch = types["Terraria.Main"].Methods.Single(m => m.Name == "OnSubmitServerPassword" && m.Parameters.Count == 0);
        var steamLaunch = Method("Terraria.Social.Steam.NetClientSocialModule", "LaunchLocalServer");
        var processArgs = Method("Terraria.Program", "ProcessLaunchArgs");
        var launch = Method("Terraria.Program", "LaunchGame");
        var launchInner = Method("Terraria.Program", "LaunchGame_");
        var runGame = Method("Terraria.Program", "RunGame");
        var dedicated = Method("Terraria.Main", "DedServ");
        var reload = Method("Terraria.ModLoader.ModLoader", "Reload");
        var update = Method("Terraria.Main", "Update");
        var consume = Method("Terraria.Main", "ConsumeAllMainThreadActions");
        var allMethods = game.MainModule.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody).ToArray();
        string[] Callers(string type, string name) => allMethods.SelectMany(m => m.Body.Instructions
            .Where(i => i.Operand is MethodReference r && r.DeclaringType.FullName == type && r.Name == name)
            .Select(_ => m.DeclaringType.FullName + "::" + m.Name)).OrderBy(n => n).ToArray();
        Check(Callers("Terraria.Netplay", "FindNextOpenClientSlot").SequenceEqual(new[] {
            "Terraria.Netplay::OnConnectionAccepted", "Terraria.Netplay::OnConnectionAccepted" }),
            "whole installed module has no allocator bypass call sites");
        Check(Callers("Terraria.RemoteClient", "Reset").SequenceEqual(new[] {
            "Terraria.Netplay::InitializeServer", "Terraria.Netplay::OnConnectionAccepted", "Terraria.Netplay::UpdateConnectedClients" }),
            "whole installed module has only the three investigated RemoteClient.Reset call sites");
        Check(Callers("Terraria.Netplay", "StartListening").SequenceEqual(new[] {
            "Terraria.Netplay::InitializeServer", "Terraria.Netplay::StartListeningIfNeeded" }),
            "whole installed module has only startup/network-loop listener entry points");
        foreach (var m in new[] { allocate, cleanup, accept, start, initialize, sync, unload, server,
            netDefaults, Method("Terraria.Netplay", "StartListening"), Method("Terraria.Netplay", "StartListeningIfNeeded"),
            hostLaunch, steamLaunch, processArgs, launch, launchInner, runGame, dedicated, reload, update, consume,
            Method("Terraria.Program", "get_IsMainThread"),
            Method("Terraria.Netplay", "ServerLoop"), Method("Terraria.RemoteClient", "Reset"),
            Method("Terraria.RemoteClient", "CheckSection"), Method("Terraria.Main", "UpdateServer"),
            Method("Terraria.NetMessage", "SyncConnectedPlayer"), Method("Terraria.NetMessage", "SyncDisconnectedPlayer"),
            Method("Terraria.WorldGen", "SaveAndQuit"), Method("Terraria.WorldGen", "SaveAndQuitCallBack"),
            Method("Terraria.WorldGen", "clearWorld"), Method("Terraria.ModLoader.ModLoader", "Mods_Unload") })
            File.WriteAllLines(Path.Combine(output, m.DeclaringType.Name + "-" + m.Name + ".il.txt"),
                new[] { m.FullName }.Concat(m.Body.Instructions.Select(i => i.ToString())));
        int CallOffset(MethodDefinition m, string name) => m.Body.Instructions.First(i => i.Operand is MethodReference r && r.Name == name).Offset;
        bool Literal(MethodDefinition m, string text) => m.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr
            && i.Operand is string value && value.Contains(text, StringComparison.Ordinal));
        Check(Literal(hostLaunch, "-autoshutdown") && Literal(hostLaunch, "tModLoader.dll -server ")
            && hostLaunch.Body.Instructions.Any(i => i.OpCode == OpCodes.Newobj && i.Operand is MethodReference r
                && r.DeclaringType.FullName == "System.Diagnostics.Process")
            && CallOffset(hostLaunch, "LaunchLocalServer") < CallOffset(hostLaunch, "StartTcpClient")
            && CallOffset(hostLaunch, "Start") < CallOffset(hostLaunch, "StartTcpClient"),
            "graphical Host & Play launches native -server/-autoshutdown child before connecting host client");
        Check(Literal(hostLaunch, "-modpath") && Literal(hostLaunch, "-tmlsavedirectory"),
            "native Host & Play propagates mod path and isolated save profile to child");
        Check(Literal(steamLaunch, " -steam -localsteamid ")
            && CallOffset(steamLaunch, "get_Arguments") < CallOffset(steamLaunch, "set_Arguments")
            && CallOffset(steamLaunch, "set_Arguments") < CallOffset(steamLaunch, "Start"),
            "Steam Host & Play appends its flags to existing child arguments then starts that Process");
        Check(Literal(processArgs, "-server") && processArgs.Body.Instructions.Any(i => i.OpCode == OpCodes.Stind_I1)
            && launchInner.Body.Instructions.Take(3).Any(i => i.Operand is FieldReference f && f.Name == "dedServ"),
            "child -server argument feeds Main.dedServ before RunGame");
        var dedBranch = runGame.Body.Instructions.Single(i => i.Offset == 0x83);
        Check(dedBranch.Operand is FieldReference df && df.Name == "dedServ"
            && dedBranch.Next.OpCode == OpCodes.Brfalse_S && ((Instruction)dedBranch.Next.Operand).Offset == 0x92
            && CallOffset(runGame, "DedServ") == 0x8b,
            "RunGame dispatches server child to DedServ, graphical process to Game.Run");
        Check(CallOffset(launch, "get_CurrentThread") < CallOffset(launch, "set_MainThread")
            && CallOffset(launch, "set_MainThread") < CallOffset(launch, "LaunchGame_"),
            "engine captures Program.MainThread before game/server launch");
        Check(dedicated.Body.Instructions.Single(i => i.Offset == 0xa6).Operand is FieldReference d && d.Name == "dedServ"
            && CallOffset(dedicated, "Reload") < CallOffset(dedicated, "DedServ_PostModLoad")
            && reload.Body.Instructions.Take(8).Any(i => i.Operand is MethodReference r && r.Name == "Load"),
            "server child sets dedServ then synchronously loads mods before entering hooked simulation scope");
        Check(server.Body.Instructions.Any(i => i.Operand is MethodReference r && r.Name == "Update"
                && r.DeclaringType.FullName == "Microsoft.Xna.Framework.Game")
            && CallOffset(update, "DoUpdate") < CallOffset(update, "ConsumeAllMainThreadActions")
            && consume.Body.Instructions.Any(i => i.Operand is MethodReference r && r.Name == "Invoke")
            && !consume.Body.Instructions.Any(i => i.Operand is MethodReference r && r.DeclaringType.FullName == "System.Threading.Tasks.Task"),
            "server scope calls Game.Update synchronously and drains queued commands on that same thread");
        Check(CallOffset(initialize, "StartListening") >= 0 && CallOffset(start, "InitializeServer") < CallOffset(start, "Start"),
            "reservation must precede orig StartServer: InitializeServer already starts listening");
        Check(accept.Body.Instructions.Count(i => i.Operand is MethodReference m && m.Name == allocate.Name) == 2,
            "accept selects slot and rechecks availability through same allocator");
        Check(CallOffset(unload, "RemoveAll") < CallOffset(unload, "UnloadContent"),
            "native detours are removed before Mod.UnloadContent: Unload alone is too late");
        Check(CallOffset(Method("Terraria.ModLoader.ModLoader", "Mods_Unload"), "clearWorld")
            < CallOffset(Method("Terraria.ModLoader.ModLoader", "Mods_Unload"), "UnloadModContent"),
            "ClearWorld hook precedes native hook removal");
        Check(CallOffset(server, "StartServer") < CallOffset(server, "OnWorldUnload"), "server simulation scope includes normal world teardown");
        Check(CallOffset(Method("Terraria.Main", "UpdateServer"), "CheckSection") >= 0, "active Player also enters native client section delivery");
        foreach (var pair in new[] { ("Terraria.On_Netplay", "StartServer"), ("Terraria.IL_Netplay", allocate.Name),
            ("Terraria.IL_Netplay", cleanup.Name), ("Terraria.On_NetMessage", sync.Name),
            ("Terraria.On_RemoteClient", "CheckSection"), ("Terraria.On_Main", "DedServ_PostModLoad"),
            ("Terraria.On_Main", "OnSubmitServerPassword") })
            Check(hookAssembly.MainModule.Types.Single(t => t.FullName == pair.Item1).Events.Any(e => e.Name == pair.Item2),
                "generated interception exists: " + pair.Item1 + "." + pair.Item2);
        Check(hookAssembly.MainModule.Types.Single(t => t.FullName == "Terraria.On_Netplay").Events.Any(e => e.Name == "StartListening"),
            "generated interception exists: Terraria.On_Netplay.StartListening");

        // Apply the SAME transformations used by the live IL handlers to complete
        // installed methods, before remapping their external storage/effects.
        var predicate = game.MainModule.ImportReference(typeof(EngineHookOfflineTests).GetMethod(nameof(Reserved)));
        int allocatorCount = allocate.Body.Instructions.Count, cleanupCount = cleanup.Body.Instructions.Count;
        // Real MonoMod ILContext.Invoke changes branch operands to ILLabel before
        // invoking handlers. Testing raw Cecil alone would miss that runtime shape.
        using (var context = new ILContext(allocate)) context.Invoke(il => {
            Check(il.Body.Instructions.Any(i => i.Operand is ILLabel), "allocator tested inside real MonoMod label pipeline");
            EnginePatches.Allocator(il.Method, predicate);
        });
        using (var context = new ILContext(cleanup)) context.Invoke(il => {
            Check(il.Body.Instructions.Any(i => i.Operand is ILLabel), "cleanup tested inside real MonoMod label pipeline");
            EnginePatches.Cleanup(il.Method, il.Module.ImportReference(typeof(EngineHookOfflineTests).GetMethod(nameof(CleanupEligible))));
        });
        Check(allocate.Body.Instructions.Count == allocatorCount + 3 && cleanup.Body.Instructions.Count == cleanupCount + 3,
            "only three per-slot eligibility instructions added to each native method");
        foreach (var m in new[] { allocate, cleanup })
            File.WriteAllLines(Path.Combine(output, m.Name + "-patched.il.txt"), m.Body.Instructions.Select(i => i.ToString()));

        using var fixture = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("EngineHookFixture", new Version(1, 0)), "EngineHookFixture", ModuleKind.Dll);
        var module = fixture.MainModule;
        var map = new Dictionary<string, Type> {
            ["Terraria.Main"] = typeof(MainState), ["Terraria.Entity"] = typeof(EntityState), ["Terraria.Player"] = typeof(PlayerState),
            ["Terraria.RemoteClient"] = typeof(RemoteState), ["Terraria.Netplay"] = typeof(NetState), ["Terraria.NetMessage"] = typeof(MessageState),
            ["Terraria.Player/Hooks"] = typeof(HookState), ["Terraria.Net.Sockets.ISocket"] = typeof(SocketState),
            ["Terraria.Net.RemoteAddress"] = typeof(AddressState), ["Terraria.Localization.Language"] = typeof(LanguageState),
            ["Terraria.ModLoader.Logging"] = typeof(LoggingState), ["Terraria.Localization.NetworkText"] = typeof(TextState),
            ["Terraria.MessageBuffer"] = typeof(object)
        };
        var runner = new TypeDefinition("Fixture", "Runner", Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Abstract
            | Mono.Cecil.TypeAttributes.Sealed, module.TypeSystem.Object);
        module.Types.Add(runner);
        TypeReference ImportType(TypeReference t) => t is ArrayType a ? new ArrayType(ImportType(a.ElementType))
            : map.TryGetValue(t.FullName, out var mapped) ? module.ImportReference(mapped) : module.ImportReference(t);
        foreach (var src in new[] { allocate, cleanup, accept })
        {
            var dst = new MethodDefinition(src.Name, Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, ImportType(src.ReturnType));
            foreach (var p in src.Parameters) dst.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, ImportType(p.ParameterType)));
            runner.Methods.Add(dst);
            dst.Body.InitLocals = src.Body.InitLocals; dst.Body.MaxStackSize = src.Body.MaxStackSize;
            foreach (var v in src.Body.Variables) dst.Body.Variables.Add(new VariableDefinition(ImportType(v.VariableType)));
            var ins = src.Body.Instructions.ToDictionary(i => i, _ => Instruction.Create(OpCodes.Nop));
            object Import(object operand) => operand switch {
                Instruction i => ins[i], Instruction[] branches => branches.Select(i => ins[i]).ToArray(),
                VariableDefinition v => dst.Body.Variables[v.Index], ParameterDefinition p => dst.Parameters[p.Index],
                FieldReference f when map.TryGetValue(f.DeclaringType.FullName, out var t) => module.ImportReference(t.GetField(f.Name) ?? throw new InvalidOperationException("Fixture field missing: " + f.FullName)),
                MethodReference m when map.TryGetValue(m.DeclaringType.FullName, out var t) => module.ImportReference(t.GetMethods()
                    .Single(x => x.Name == m.Name && x.GetParameters().Length == m.Parameters.Count)),
                FieldReference f => module.ImportReference(f), MethodReference m => module.ImportReference(m), TypeReference t => ImportType(t), _ => operand
            };
            foreach (var i in src.Body.Instructions) { ins[i].OpCode = i.OpCode; ins[i].Operand = Import(i.Operand); dst.Body.Instructions.Add(ins[i]); }
            foreach (var e in src.Body.ExceptionHandlers) dst.Body.ExceptionHandlers.Add(new ExceptionHandler(e.HandlerType) {
                TryStart = ins[e.TryStart], TryEnd = e.TryEnd == null ? null : ins[e.TryEnd], HandlerStart = ins[e.HandlerStart],
                HandlerEnd = e.HandlerEnd == null ? null : ins[e.HandlerEnd], CatchType = e.CatchType == null ? null : ImportType(e.CatchType)
            });
        }
        using var bytes = new MemoryStream(); fixture.Write(bytes); bytes.Position = 0;
        var loaded = AssemblyLoadContext.Default.LoadFromStream(bytes).GetType("Fixture.Runner");
        var tick = (Action)loaded.GetMethod(cleanup.Name).CreateDelegate(typeof(Action));
        var next = (Func<int>)loaded.GetMethod(allocate.Name).CreateDelegate(typeof(Func<int>));
        var accepted = (Action<SocketState>)loaded.GetMethod(accept.Name).CreateDelegate(typeof(Action<SocketState>));
        void Reset()
        {
            MainState.maxNetPlayers = 4;
            MainState.player = Enumerable.Range(0, 256).Select(_ => new PlayerState()).ToArray();
            NetState.Clients = Enumerable.Range(0, 256).Select(i => new RemoteState { Id = i }).ToArray();
            NetState.Allocator = next; NetState.Stops = NetState.Kicks = 0; NetState.IsListening = true;
            MessageState.Disconnected.Clear(); HookState.Disconnected.Clear();
            Reservation = new StartupReservation();
            FixtureActor = null;
        }
        Reset();
        Check(next() == 0, "empty reservation leaves native first-free allocation unchanged");
        MainState.player[1].active = true; tick();
        Check(!MainState.player[1].active && HookState.Disconnected.SequenceEqual(new[] { 1 }), "unreserved disconnected human cleanup remains native");
        Reset();
        Check(Reservation.ArmBeforeListening(false, 4), "one slot armed before listener callback");
        Check(Reservation.Snapshot.Slot == 3 && !Reservation.IsReserved(255), "reservation stays in configured range and excludes server sentinel");
        // Model an immediate accept inside InitializeServer, before StartServer returns.
        accepted(new SocketState());
        MainState.player[0].active = true; NetState.Clients[0].State = 10; NetState.Clients[0].IsActive = true;
        MainState.player[3].active = true;
        var actor = MainState.player[3];
        FixtureActor = actor;
        accepted(new SocketState()); accepted(new SocketState()); accepted(new SocketState());
        Check(NetState.Clients.Take(3).All(c => c.IsConnected()) && !NetState.Clients[3].IsConnected(), "complete native accept path never assigns reserved slot");
        Check(next() == -1 && NetState.Kicks == 1 && NetState.Stops > 0, "reserved slot reduces capacity; full-server native rejection preserved");
        tick();
        Check(actor.active && ReferenceEquals(MainState.player[3], actor) && NetState.Clients[3].Resets == 0, "patched native cleanup retains reserved actor without active rewrites");
        Check(NetState.Clients[3].StatusText2 == "", "synthetic guard runs after native disconnected status handling");
        Check(MainState.player[0].active && NetState.Clients[0].Updates == 1 && NetState.HasClients, "connected human updates and HasClients remain native");
        NetState.Clients[1].IsActive = true; NetState.Clients[1].Socket.Connected = false;
        tick(); tick();
        Check(NetState.Clients[1].Resets == 2 && MessageState.Disconnected.SequenceEqual(new[] { 1 }), "human connection loss still synchronizes and resets");
        Exception fault = null;
        var network = new Thread(() => { try { for (int i = 0; i < 10000; i++) { tick(); if (next() == 3) throw new Exception("reserved allocator leak"); } } catch (Exception e) { fault = e; } });
        network.Start();
        for (int i = 0; i < 10000; i++) if (!Reservation.IsReserved(3)) throw new Exception("reservation changed");
        Check(network.Join(10000) && fault == null && actor.active, "10,000 concurrent native network loops preserve immutable reservation and actor");
        Exception wrongThread = null;
        var worker = new Thread(() => { try { Reservation.Release(); } catch (Exception e) { wrongThread = e; } });
        worker.Start(); worker.Join();
        Check(wrongThread is InvalidOperationException && Reservation.IsReserved(3), "off-thread release rejected without changing reservation");
        // Cleanup engine object FIRST; release allocator visibility LAST.
        MainState.player[3].active = false; MainState.player[3] = new PlayerState();
        Reservation.Release(); Reservation.Release();
        Check(!Reservation.IsReserved(3) && !Reservation.ArmBeforeListening(false, 4), "release is idempotent and this process cannot dynamically re-arm");
        foreach (var c in NetState.Clients.Take(3)) c.Socket = new SocketState();
        Check(next() == 3, "despawn release returns slot to native human allocator");
        accepted(new SocketState()); MainState.player[3].active = true; tick();
        Check(MainState.player[3].active && NetState.Clients[3].IsConnected(), "released slot can subsequently host a genuine connection fixture");
        var late = new StartupReservation();
        Check(!late.ArmBeforeListening(true, 4) && late.Snapshot == null, "already-listening startup fails closed");
        Check(!new StartupReservation().ArmBeforeListening(false, 1) && !new StartupReservation().ArmBeforeListening(false, 256), "unsupported capacity fails closed");
        Reset(); Reservation.ArmBeforeListening(false, 4);
        MainState.player[3].active = true; NetState.Clients[3].Socket = new SocketState();
        NetState.Clients[3].IsActive = true; NetState.Clients[3].State = 10;
        tick();
        Check(MainState.player[3].active && NetState.Clients[3].Updates == 1 && NetState.HasClients,
            "unexpected real connection in reserved slot falls through to native human update");
        NetState.Clients[3].Socket.Connected = false; tick(); tick();
        Check(NetState.Clients[3].Resets == 1 && MessageState.Disconnected.SequenceEqual(new[] { 3 }),
            "reservation never suppresses native disconnect/reset for an unexpected human");
        Reset(); Reservation.ArmBeforeListening(false, 4);
        MainState.player[3].active = true; tick();
        Check(!MainState.player[3].active && HookState.Disconnected.SequenceEqual(new[] { 3 }),
            "reservation alone never suppresses native cleanup for an unknown Player");
        Reset(); Reservation.ArmBeforeListening(false, 4);
        FixtureActor = MainState.player[3]; FixtureActor.active = true;
        NetState.Clients[3].PendingTermination = NetState.Clients[3].PendingTerminationApproved = true;
        tick();
        Check(NetState.Clients[3].Resets == 1 && !ReferenceEquals(MainState.player[3], FixtureActor)
            && MessageState.Disconnected.SequenceEqual(new[] { 3 }),
            "narrow synthetic guard cannot bypass native termination approval/Reset branch");
        Console.WriteLine($"EngineHookOfflineTests: {checks} checks passed; actual IL fixtures only; G1 LIVE NOT RUN.");
    }

    public static void AuditPackage(string package)
    {
        if (!File.Exists(package)) throw new FileNotFoundException("Build isolated probe before package audit.", package);
        using var file = File.OpenRead(package);
        using var reader = new BinaryReader(file);
        Check(System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4)) == "TMOD", "actual build has TMOD header");
        string version = reader.ReadString(); reader.ReadBytes(276); reader.ReadInt32();
        Check(reader.ReadString() == "VirtualPlayerEngineProbe" && reader.ReadString() == "0.4.0", "isolated G4 package identity");
        int count = reader.ReadInt32(), offset = 0, dllOffset = -1, size = 0, packed = 0;
        for (int i = 0; i < count; i++)
        {
            string name = reader.ReadString(); int length = reader.ReadInt32(), compressed = reader.ReadInt32();
            if (name == "VirtualPlayerEngineProbe.dll") { dllOffset = offset; size = length; packed = compressed; }
            offset += compressed;
        }
        Check(dllOffset >= 0, "compiled probe DLL is packaged");
        file.Position += dllOffset;
        using var payload = new MemoryStream(reader.ReadBytes(packed));
        using var bytes = new MemoryStream();
        if (packed == size) payload.CopyTo(bytes);
        else { using var inflate = new System.IO.Compression.DeflateStream(payload, System.IO.Compression.CompressionMode.Decompress); inflate.CopyTo(bytes); }
        Check(bytes.Length == size, "packaged DLL size verified");
        bytes.Position = 0;
        using var probe = AssemblyDefinition.ReadAssembly(bytes);
        IEnumerable<TypeDefinition> Walk(TypeDefinition t) => new[] { t }.Concat(t.NestedTypes.SelectMany(Walk));
        var types = probe.MainModule.Types.SelectMany(Walk).ToArray();
        var methods = types.SelectMany(t => t.Methods).Where(m => m.HasBody).ToArray();
        var ins = methods.SelectMany(m => m.Body.Instructions).ToArray();
        var mod = types.Single(t => t.Name == "VirtualPlayerEngineProbe");
        var session = types.Single(t => t.Name == "ProbeSession");
        Check(mod.Fields.Single(f => f.Name == "EngineHash").Constant as string == EngineHash
            && mod.Fields.Single(f => f.Name == "HooksHash").Constant as string == HooksHash, "runtime pins both investigated assemblies");
        Check(!probe.MainModule.AssemblyReferences.Any(a => a.Name.Contains("CompanionBridge")), "no production Bridge dependency");
        var constructions = ins.Where(i => i.OpCode == OpCodes.Newobj).Select(i => (MethodReference)i.Operand).ToArray();
        Check(constructions.Count(m => m.DeclaringType.FullName == "Terraria.Player") == 1,
            "one genuine Player construction site, confined to spawn");
        Check(!constructions.Any(m => m.DeclaringType.FullName is "Terraria.RemoteClient" or "Terraria.NPC"
            || m.DeclaringType.FullName.Contains("Socket") || m.DeclaringType.FullName.Contains("Process")), "no fake RemoteClient, socket, NPC or process construction");
        Check(!ins.Any(i => (i.OpCode == OpCodes.Stfld || i.OpCode == OpCodes.Stsfld) && i.Operand is FieldReference f
            && (f.DeclaringType.FullName == "Terraria.RemoteClient" || f.FullName == "System.Int32 Terraria.Main::myPlayer"
                || f.FullName == "System.Boolean Terraria.Netplay::IsListening" || f.FullName == "Terraria.Net.Sockets.ISocket Terraria.Netplay::TcpListener")),
            "no connection state, native listener state or Main.myPlayer writes");
        Check(ins.Count(i => i.OpCode == OpCodes.Stfld && i.Operand is FieldReference f && f.Name == "active"
            && i.Previous.OpCode == OpCodes.Ldc_I4_1) == 1, "active=true has exactly one initialization site, no repair loop");
        Check(!ins.Any(i => i.Operand is MethodReference m
            && m.DeclaringType.FullName == "Terraria.Player" && m.Name is "Update" or "Spawn" or "Teleport"),
            "no manual update, native Spawn side effects or movement shortcut");
        Check(methods.Where(m => m.Body.Instructions.Any(i => i.Operand is MethodReference r && r.Name == "SendData"))
            .All(m => m.DeclaringType.Name == "NetworkStateSync"), "G2 native packet sends confined to ownership-checked adapter");
        var refs = ins.Where(i => i.Operand is MethodReference).Select(i => (MethodReference)i.Operand).ToArray();
        Check(!refs.Any(m=>m.Name=="SetAsActive"),"G3 never calls PlayerFileData.SetAsActive");
        Check(refs.Any(m=>m.DeclaringType.FullName=="Terraria.Player" && m.Name=="SavePlayer")
            && refs.Any(m=>m.DeclaringType.FullName=="Terraria.Player" && m.Name=="LoadPlayer"),"G3 uses native player save/load APIs");
        foreach (var hook in new[] { "FindNextOpenClientSlot", "UpdateConnectedClients", "StartServer", "StartListening", "OnConnectionAccepted", "SyncOnePlayer", "CheckSection", "DedServ_PostModLoad", "clearWorld", "OnSubmitServerPassword" })
            Check(refs.Any(m => m.Name == "add_" + hook) && refs.Any(m => m.Name == "remove_" + hook), "hook subscription has rollback pair: " + hook);
        var start = mod.Methods.Single(m => m.Name == "StartServer");
        int CallOffset(MethodDefinition m, string name) => m.Body.Instructions.First(i => i.Operand is MethodReference r && r.Name == name).Offset;
        Check(CallOffset(start, "ArmBeforeListening") < CallOffset(start, "Invoke"), "compiled live reservation publishes BEFORE orig StartServer");
        var listen = mod.Methods.Single(m => m.Name == "Listen");
        Check(CallOffset(listen, "Increment") < CallOffset(listen, "Invoke")
            && CallOffset(listen, "Log") < CallOffset(listen, "Invoke"),
            "compiled native listener observation records entry/reservation before original TCP/Steam listen");
        var scope = mod.Methods.Single(m => m.Name == "ServerScope");
        Check(scope.Body.ExceptionHandlers.Any(e => e.HandlerType == ExceptionHandlerType.Finally)
            && scope.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "Finish")
            && start.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "scopeThread")
            && scope.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "get_IsMainThread")
            && start.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "get_IsMainThread"),
            "compiled startup requires server scope AND engine MainThread identity with finally teardown");
        var graphical = mod.Methods.Single(m => m.Name == "GraphicalHostLaunch");
        Check(graphical.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "Invoke")
            && graphical.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "dedServ")
            && !graphical.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name is "Spawn" or "StartServer" or "ArmBeforeListening"),
            "graphical hook observes native launch without arming or spawning actor in human client");
        var idle = mod.Methods.Single(m => m.Name == "ConnectionIdle");
        Check(idle.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "ConnectionIdle")
            && idle.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "PendingTerminationApproved")
            && new[] { mod.Methods.Single(m => m.Name == "OwnedWithoutConnection"),
                session.Methods.Single(m => m.Name == "CheckInitialSlot"), session.Methods.Single(m => m.Name == "Finish") }
                .All(m => m.Body.Instructions.Any(i => i.Operand is MethodReference r && r.Name == "ConnectionIdle")),
            "spawn validation, network eligibility and teardown share full connection/transition policy");
        var clearWorld = mod.Methods.Single(m => m.Name == "ClearWorld");
        Check(CallOffset(clearWorld, "Finish") < CallOffset(clearWorld, "Invoke"), "compiled pre-clearWorld fuse precedes native world mutation/unhook");
        Check(refs.Any(m => m.Name == "QueueMainThreadAction") && refs.Any(m => m.Name == "DoesPlayerSlotCountAsAHost"), "compiled command queue retains native host authorization");
        Check(session.Methods.Single(m => m.Name == "Spawn").Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "AssertOwner"), "spawn checks simulation-thread ownership");
        Check(session.Methods.Single(m => m.Name == "CheckInitialSlot").Body.Instructions.Any(i => i.OpCode == OpCodes.Stsfld
                && i.Operand is FieldReference f && f.Name == "previous")
            && !session.Methods.Single(m => m.Name == "Spawn").Body.Instructions.Any(i => i.OpCode == OpCodes.Stsfld
                && i.Operand is FieldReference f && f.Name == "previous"),
            "native placeholder baseline is established before spawn and never replaced by spawn");
        RunCompiledStartup(start, listen);
        RunCompiledLifecycle(probe, session, mod);
        int networkChecks = NetworkSyncOfflineTests.Run(gameDirectory, probe);
        ControlOfflineTests.AuditPackage(probe);
        Console.WriteLine($"Engine probe package: loader={version}; bytes={file.Length}; DLL={size}; {checks} prerequisite/package + {networkChecks} G2 = {checks + networkChecks} total targeted checks; SINGLE-WINDOW LIVE NOT RUN; REMOTE MULTIPLAYER LIVE NOT RUN.");
    }

    public sealed class StartupDetail
    {
        public readonly bool Armed, LegacyListening, TcpExists;
        public readonly int Entries;
        public StartupDetail(bool armed, int capacity, bool legacyListening, bool tcpExists, int entries)
        { Armed = armed; LegacyListening = legacyListening; TcpExists = tcpExists; Entries = entries; }
    }
    public sealed class ListenerDetail { public ListenerDetail(int entry, bool reserved) { } }
    public static class StartupState
    {
        public static StartupReservation Reservation;
        public static bool Ready, dedServ, EngineMainThread, IsListening;
        public static object TcpListener;
        public static int scopeThread, listenerEntries, maxNetPlayers, InitialChecks, NativeCalls;
        public static long Accepts, NetworkLoops;
        public static string Fault;
        public static StartupDetail PreListen;
        public static string get_Fault() => Fault;
        public static bool get_IsMainThread() => EngineMainThread;
        public static void SignalFault(string reason) => Fault ??= reason;
        public static void Finish(string _, bool keep) => throw new Exception("unexpected teardown in startup fixture");
        public static void Update() { }
        public static void CheckInitialSlot()
        {
            if (!Reservation.IsReserved(254)) throw new Exception("initial validation lost reservation");
            InitialChecks++;
        }
        public static void Log(string name, object detail)
        { if (name == "pre-listen-reservation") PreListen = (StartupDetail)detail; }
    }

    private static void RunCompiledStartup(MethodDefinition start, MethodDefinition listen)
    {
        // Execute the packaged handlers, substituting only engine storage and
        // orig effects. The native flag default comes from installed .cctor IL.
        using var fixture = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("CompiledStartupFixture", new Version(1, 0)), "CompiledStartupFixture", ModuleKind.Dll);
        var module = fixture.MainModule;
        var map = new Dictionary<string, Type> {
            ["VirtualPlayerEngineProbe.VirtualPlayerEngineProbe"] = typeof(StartupState),
            ["VirtualPlayerEngineProbe.ProbeSession"] = typeof(StartupState),
            ["VirtualPlayerEngineProbe.CharacterSelfTest"] = typeof(StartupState),
            ["VirtualPlayerEngineProbe.StartupReservation"] = typeof(StartupReservation),
            ["VirtualPlayerEngineProbe.StartupReservation/View"] = typeof(StartupReservation.View),
            ["Terraria.Main"] = typeof(StartupState), ["Terraria.Netplay"] = typeof(StartupState),
            ["Terraria.Program"] = typeof(StartupState), ["Terraria.Net.Sockets.ISocket"] = typeof(object),
            ["Terraria.On_Netplay/orig_StartServer"] = typeof(Action),
            ["Terraria.On_Netplay/orig_StartListening"] = typeof(Func<bool>)
        };
        var runner = new TypeDefinition("Fixture", "Startup", Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Abstract
            | Mono.Cecil.TypeAttributes.Sealed, module.TypeSystem.Object); module.Types.Add(runner);
        TypeReference Type(TypeReference t) => map.TryGetValue(t.FullName, out var mapped) ? module.ImportReference(mapped) : module.ImportReference(t);
        foreach (var src in new[] { start, listen })
        {
            var dst = new MethodDefinition(src.Name, Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, Type(src.ReturnType));
            // Preserve instance-handler argument indices; the handlers do not read this.
            dst.Parameters.Add(new ParameterDefinition("unusedThis", Mono.Cecil.ParameterAttributes.None, module.TypeSystem.Object));
            foreach (var p in src.Parameters) dst.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, Type(p.ParameterType)));
            runner.Methods.Add(dst); dst.Body.InitLocals = src.Body.InitLocals; dst.Body.MaxStackSize = src.Body.MaxStackSize;
            foreach (var v in src.Body.Variables) dst.Body.Variables.Add(new VariableDefinition(Type(v.VariableType)));
            var instructions = src.Body.Instructions.ToDictionary(i => i, _ => Instruction.Create(OpCodes.Nop));
            object Import(object operand) => operand switch {
                Instruction i => instructions[i], Instruction[] branches => branches.Select(i => instructions[i]).ToArray(),
                VariableDefinition v => dst.Body.Variables[v.Index], ParameterDefinition p => dst.Parameters[p.Index + 1],
                FieldReference f when map.TryGetValue(f.DeclaringType.FullName, out var t) => module.ImportReference(t.GetField(f.Name) ?? throw new InvalidOperationException("Fixture field missing: " + f.FullName)),
                MethodReference m when m.DeclaringType.Name.StartsWith("<>f__AnonymousType") => module.ImportReference(
                    (m.Parameters.Count == 5 ? typeof(StartupDetail) : typeof(ListenerDetail)).GetConstructors().Single()),
                MethodReference m when map.TryGetValue(m.DeclaringType.FullName, out var t) => module.ImportReference(t.GetMethods()
                    .Single(x => x.Name == m.Name && x.GetParameters().Length == m.Parameters.Count)),
                FieldReference f => module.ImportReference(f), MethodReference m => module.ImportReference(m), TypeReference t => Type(t), _ => operand
            };
            foreach (var i in src.Body.Instructions) { instructions[i].OpCode = i.OpCode; instructions[i].Operand = Import(i.Operand); dst.Body.Instructions.Add(instructions[i]); }
            foreach (var e in src.Body.ExceptionHandlers) dst.Body.ExceptionHandlers.Add(new ExceptionHandler(e.HandlerType) {
                TryStart = instructions[e.TryStart], TryEnd = e.TryEnd == null ? null : instructions[e.TryEnd], HandlerStart = instructions[e.HandlerStart],
                HandlerEnd = e.HandlerEnd == null ? null : instructions[e.HandlerEnd], CatchType = e.CatchType == null ? null : Type(e.CatchType)
            });
        }
        using var bytes = new MemoryStream(); fixture.Write(bytes); bytes.Position = 0;
        var loaded = AssemblyLoadContext.Default.LoadFromStream(bytes).GetType("Fixture.Startup");
        var begin = (Action<object, Action>)loaded.GetMethod(start.Name).CreateDelegate(typeof(Action<object, Action>));
        var listening = (Func<object, Func<bool>, bool>)loaded.GetMethod(listen.Name).CreateDelegate(typeof(Func<object, Func<bool>, bool>));
        void Reset()
        {
            StartupState.Reservation = new(); StartupState.Ready = StartupState.dedServ = StartupState.EngineMainThread = true;
            StartupState.IsListening = nativeInitialIsListening; StartupState.TcpListener = null;
            StartupState.scopeThread = Environment.CurrentManagedThreadId; StartupState.maxNetPlayers = 255;
            StartupState.listenerEntries = StartupState.InitialChecks = StartupState.NativeCalls = 0;
            StartupState.Accepts = StartupState.NetworkLoops = 0; StartupState.Fault = null; StartupState.PreListen = null;
        }
        Reset(); bool reservedAtListen = false;
        begin(null, () => {
            StartupState.TcpListener = new object();
            listening(null, () => { StartupState.NativeCalls++; reservedAtListen = StartupState.Reservation.IsReserved(254); return true; });
        });
        Check(StartupState.IsListening && StartupState.PreListen.Armed && StartupState.PreListen.LegacyListening
            && !StartupState.PreListen.TcpExists && StartupState.PreListen.Entries == 0
            && StartupState.Reservation.IsReserved(254) && reservedAtListen && StartupState.InitialChecks == 1
            && StartupState.NativeCalls == 1 && StartupState.Fault == null,
            "actual packaged startup/listener IL arms with installed IsListening=true default BEFORE native listen");
        StartupState.Reservation.Release();
        Check(!listening(null, () => { StartupState.NativeCalls++; return false; })
            && StartupState.listenerEntries == 2 && StartupState.NativeCalls == 2 && StartupState.Fault == null,
            "actual listener hook preserves native retry/result after normal reservation release");
        foreach (string evidence in new[] { "TCP listener object", "prior listener entry", "prior accept", "prior network loop" })
        {
            Reset();
            if (evidence == "TCP listener object") StartupState.TcpListener = new object();
            else if (evidence == "prior listener entry") StartupState.listenerEntries = 1;
            else if (evidence == "prior accept") StartupState.Accepts = 1;
            else StartupState.NetworkLoops = 1;
            begin(null, () => StartupState.NativeCalls++);
            Check(StartupState.Reservation.Snapshot == null && !StartupState.PreListen.Armed && StartupState.Fault != null
                && StartupState.NativeCalls == 1 && StartupState.InitialChecks == 0 && StartupState.IsListening,
                "actual startup rejects late arm without changing human orig/flag: " + evidence);
        }
        Reset(); StartupState.EngineMainThread = false;
        begin(null, () => StartupState.NativeCalls++);
        Check(StartupState.Reservation.Snapshot == null && StartupState.Fault != null && StartupState.NativeCalls == 1,
            "actual startup still rejects non-engine-main-thread context");
        Reset(); begin(null, () => StartupState.NativeCalls++); StartupState.Reservation.Release();
        begin(null, () => StartupState.NativeCalls++);
        Check(StartupState.Reservation.Snapshot == null && StartupState.Fault != null && StartupState.InitialChecks == 1,
            "actual startup cannot re-arm after release even without remaining native evidence");
        Reset(); bool result = listening(null, () => { StartupState.NativeCalls++; return true; });
        Check(result && StartupState.Fault != null && StartupState.NativeCalls == 1 && StartupState.Reservation.Snapshot == null,
            "first listener entry without reservation disables spawn and retains native listen");
    }

    public sealed class TeardownDetail { public TeardownDetail(string reason, bool same, bool human) { } }
    public sealed class AcceptDetail { public AcceptDetail(int slot, bool reserved, int thread) { } }
    public static class WorldState
    {
        public static bool Clear = true;
        public static bool InWorld(int x, int y, int fluff) => true;
        public static bool SolidCollision(VectorState position, int width, int height) => !Clear;
    }
    public static class LifecycleNetworkState
    {
        public static void Reset() { }
        public static bool ThrowFull, ThrowLeave;
        public static void Full(PlayerState _) { if (ThrowFull) throw new IOException("fixture native full sync failed"); }
        public static void Leave(PlayerState _)
        {
            if (ThrowLeave)
            {
                TeardownState.Quarantined = true;
                throw new IOException("fixture native departure failed");
            }
        }
    }
    public static class LifecycleAnnouncements
    {
        public static void ResetNotices() { }
        public static int Joins, Leaves;
        public static void Join(PlayerState _) => Joins++;
        public static void Left(PlayerState _) { if (Joins>Leaves) Leaves++; }
    }
    public static class LifecycleCharacterStore
    {
        public static PlayerState Candidate;
        public static bool FailedSave;
        public static PlayerState AcquireCandidate() { var p=Candidate; Candidate=null; return p; }
        public static void AssertMayRetire(PlayerState p) { if(p!=null && FailedSave) throw new IOException("unsaved state"); }
        public static void Clear() { }
        public static void Update() { }
        public static void Retire(PlayerState p) { }
    }
    public static class TeardownState
    {
        public static StartupReservation Reservation;
        public static PlayerState Actor, previous;
        public static int slot;
        public static bool connectedHook, creating, finishing, Quarantined, EngineMainThread = true, Ready = true;
        public static long spawnedAt, NetworkLoops, Accepts, CleanupSkips, ActorGeneration;
        public static string Phase, Fault;
        public static bool ThrowLog, ThrowDisconnect;
        public static int Disconnects, Logs;
        public static int Connects;
        public static Action OnConnect, OnDisconnect;
        public static StartupReservation get_Reservation() => Reservation;
        public static bool get_IsMainThread() => EngineMainThread;
        public static string get_Fault() => Fault;
        public static bool IsReserved(int id) => Reservation.IsReserved(id);
        public static bool ConnectionIdle(RemoteState c) => c != null && StartupReservation.ConnectionIdle(
            c.IsConnected(), c.IsActive, c.State, c.PendingTermination, c.PendingTerminationApproved);
        public static void SignalFault(string reason) => Fault = reason;
        public static void Log(string name, object detail)
        {
            Logs++;
            if (name == "teardown" && !Reservation.IsReserved(3)) throw new Exception("released before teardown log/restoration");
            if (ThrowLog) throw new IOException("fixture log failure");
        }
        public static void PlayerDisconnect(int id)
        {
            Disconnects++;
            OnDisconnect?.Invoke();
            if (ThrowDisconnect) throw new InvalidOperationException("fixture disconnect hook failure");
        }
        public static void PlayerConnect(int id) { Connects++; OnConnect?.Invoke(); }
    }

    private static void RunCompiledLifecycle(AssemblyDefinition probe, TypeDefinition session, TypeDefinition mod)
    {
        using var fixture = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("CompiledTeardownFixture", new Version(1, 0)), "CompiledTeardownFixture", ModuleKind.Dll);
        var module = fixture.MainModule;
        var map = new Dictionary<string, Type> {
            ["VirtualPlayerEngineProbe.ProbeSession"] = typeof(TeardownState),
            ["VirtualPlayerEngineProbe.NetworkStateSync"] = typeof(LifecycleNetworkState),
            ["VirtualPlayerEngineProbe.ProbeAnnouncements"] = typeof(LifecycleAnnouncements),
            ["VirtualPlayerEngineProbe.CharacterStore"] = typeof(LifecycleCharacterStore),
            ["VirtualPlayerEngineProbe.CharacterSelfTest"] = typeof(LifecycleCharacterStore),
            ["VirtualPlayerEngineProbe.ProbeDebugAppearance"] = typeof(LifecycleCharacterStore),
            ["VirtualPlayerEngineProbe.ActorControl"] = typeof(LifecycleCharacterStore),
            ["VirtualPlayerEngineProbe.VirtualPlayerEngineProbe"] = typeof(TeardownState),
            ["VirtualPlayerEngineProbe.StartupReservation"] = typeof(StartupReservation),
            ["VirtualPlayerEngineProbe.StartupReservation/View"] = typeof(StartupReservation.View),
            ["Terraria.Player"] = typeof(PlayerState), ["Terraria.Entity"] = typeof(EntityState),
            ["Terraria.Main"] = typeof(MainState), ["Terraria.Netplay"] = typeof(NetState),
            ["Terraria.Program"] = typeof(TeardownState),
            ["Terraria.RemoteClient"] = typeof(RemoteState), ["Terraria.Player/Hooks"] = typeof(TeardownState),
            ["Microsoft.Xna.Framework.Vector2"] = typeof(VectorState),
            ["Terraria.WorldGen"] = typeof(WorldState), ["Terraria.Collision"] = typeof(WorldState),
            ["Terraria.Net.Sockets.ISocket"] = typeof(SocketState),
            ["Terraria.On_Netplay/orig_OnConnectionAccepted"] = typeof(Action<SocketState>),
            ["Terraria.On_Netplay/orig_UpdateConnectedClients"] = typeof(Action)
        };
        var runner = new TypeDefinition("Fixture", "Teardown", Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Abstract
            | Mono.Cecil.TypeAttributes.Sealed, module.TypeSystem.Object); module.Types.Add(runner);
        TypeReference Type(TypeReference t) => t is ArrayType a ? new ArrayType(Type(a.ElementType))
            : map.TryGetValue(t.FullName, out var mapped) ? module.ImportReference(mapped) : module.ImportReference(t);
        var sources = session.Methods.Where(m => m.Name is "Finish" or "Spawn" or "CheckInitialSlot" or "Quarantine")
            .Concat(mod.Methods.Where(m => m.Name is "OwnedWithoutConnection" or "SkipCleanup" or "Accepted" or "NetworkTick")).ToArray();
        var copies = sources.ToDictionary(m => m.FullName, m => new MethodDefinition(m.Name,
            Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, Type(m.ReturnType)));
        foreach (var src in sources)
        {
            var dst = copies[src.FullName];
            if (src.HasThis) dst.Parameters.Add(new ParameterDefinition("unusedThis", Mono.Cecil.ParameterAttributes.None, module.TypeSystem.Object));
            foreach (var p in src.Parameters) dst.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, Type(p.ParameterType)));
            runner.Methods.Add(dst); dst.Body.InitLocals = src.Body.InitLocals; dst.Body.MaxStackSize = src.Body.MaxStackSize;
            foreach (var v in src.Body.Variables) dst.Body.Variables.Add(new VariableDefinition(Type(v.VariableType)));
            var instructions = src.Body.Instructions.ToDictionary(i => i, _ => Instruction.Create(OpCodes.Nop));
            object Import(object operand) => operand switch {
                Instruction i => instructions[i], Instruction[] branches => branches.Select(i => instructions[i]).ToArray(),
                VariableDefinition v => dst.Body.Variables[v.Index], ParameterDefinition p => dst.Parameters[p.Index + (src.HasThis ? 1 : 0)],
                FieldReference f when map.TryGetValue(f.DeclaringType.FullName, out var t) => module.ImportReference(t.GetField(f.Name) ?? throw new InvalidOperationException("Fixture field missing: " + f.FullName)),
                MethodReference m when copies.TryGetValue(m.FullName, out var copy) => copy,
                MethodReference m when m.DeclaringType.Name.StartsWith("<>f__AnonymousType") => module.ImportReference(
                    (((GenericInstanceType)m.DeclaringType).GenericArguments[0].MetadataType == MetadataType.String
                        ? typeof(TeardownDetail) : typeof(AcceptDetail)).GetConstructors().Single()),
                MethodReference m when m.Name == ".ctor" && map.TryGetValue(m.DeclaringType.FullName, out var t) => module.ImportReference(
                    t.GetConstructors().Single(x => x.GetParameters().Length == m.Parameters.Count)),
                MethodReference m when map.TryGetValue(m.DeclaringType.FullName, out var t) => module.ImportReference(t.GetMethods()
                    .Single(x => x.Name == m.Name && x.GetParameters().Length == m.Parameters.Count)),
                FieldReference f => module.ImportReference(f), MethodReference m => module.ImportReference(m), TypeReference t => Type(t), _ => operand
            };
            foreach (var i in src.Body.Instructions) { instructions[i].OpCode = i.OpCode; instructions[i].Operand = Import(i.Operand); dst.Body.Instructions.Add(instructions[i]); }
            foreach (var e in src.Body.ExceptionHandlers) dst.Body.ExceptionHandlers.Add(new ExceptionHandler(e.HandlerType) {
                TryStart = instructions[e.TryStart], TryEnd = e.TryEnd == null ? null : instructions[e.TryEnd], HandlerStart = instructions[e.HandlerStart],
                HandlerEnd = e.HandlerEnd == null ? null : instructions[e.HandlerEnd], CatchType = e.CatchType == null ? null : Type(e.CatchType)
            });
        }
        using var bytes = new MemoryStream(); fixture.Write(bytes); bytes.Position = 0;
        var loaded = AssemblyLoadContext.Default.LoadFromStream(bytes).GetType("Fixture.Teardown");
        var finishWithLease = (Action<string,bool>)loaded.GetMethod("Finish").CreateDelegate(typeof(Action<string,bool>));
        Action<string> finish = reason => finishWithLease(reason,false);
        var spawn = (Action)loaded.GetMethod("Spawn").CreateDelegate(typeof(Action));
        var initial = (Action)loaded.GetMethod("CheckInitialSlot").CreateDelegate(typeof(Action));
        var eligible = (Func<int, bool>)loaded.GetMethod("OwnedWithoutConnection").CreateDelegate(typeof(Func<int, bool>));
        var accepted = (Action<object, Action<SocketState>, SocketState>)loaded.GetMethod("Accepted")
            .CreateDelegate(typeof(Action<object, Action<SocketState>, SocketState>));
        var networkTick = (Action<object, Action>)loaded.GetMethod("NetworkTick").CreateDelegate(typeof(Action<object, Action>));
        void Reset()
        {
            PlayerState.OnConstruct = null;
            MainState.player = Enumerable.Range(0, 256).Select(_ => new PlayerState()).ToArray();
            MainState.maxNetPlayers = 4; MainState.netMode = 2;
            NetState.Clients = Enumerable.Range(0, 256).Select(i => new RemoteState { Id = i }).ToArray();
            TeardownState.Reservation = new(); TeardownState.Reservation.ArmBeforeListening(false, 4);
            TeardownState.slot = 3; TeardownState.previous = MainState.player[3];
            TeardownState.Actor = MainState.player[3] = new PlayerState { active = true, whoAmI = 3 };
            TeardownState.connectedHook = true; TeardownState.Fault = null;
            TeardownState.Quarantined = false; TeardownState.EngineMainThread = true;
            TeardownState.ThrowLog = TeardownState.ThrowDisconnect = false; TeardownState.Disconnects = TeardownState.Logs = 0;
            TeardownState.creating = TeardownState.finishing = false;
            TeardownState.OnConnect = TeardownState.OnDisconnect = null; TeardownState.Connects = 0;
            TeardownState.Ready = WorldState.Clear = true;
            LifecycleNetworkState.ThrowFull = LifecycleNetworkState.ThrowLeave = false;
            LifecycleAnnouncements.Joins = LifecycleAnnouncements.Leaves = 0;
            LifecycleCharacterStore.Candidate=null; LifecycleCharacterStore.FailedSave=false;
        }
        Reset(); var original = TeardownState.previous;
        finish("despawn"); finish("duplicate");
        Check(TeardownState.Actor == null && ReferenceEquals(MainState.player[3], original)
            && TeardownState.Reservation.Snapshot == null && TeardownState.Disconnects == 1 && TeardownState.Logs == 1,
            "actual packaged teardown IL restores placeholder, disconnects once, then releases; idempotent");
        Reset(); TeardownState.ThrowDisconnect = true;
        try { finish("hook throws"); throw new Exception("exception lost"); } catch (InvalidOperationException) { }
        Check(TeardownState.Actor == null && TeardownState.Reservation.Snapshot == null, "actual teardown finally releases after disconnect-hook exception");
        Reset(); TeardownState.ThrowLog = true;
        try { finish("log throws"); throw new Exception("exception lost"); } catch (IOException) { }
        Check(TeardownState.Actor == null && TeardownState.Reservation.Snapshot == null, "actual teardown releases even after log failure");
        Reset(); Exception wrong = null;
        var thread = new Thread(() => { try { finish("foreign-thread clearWorld"); } catch (Exception e) { wrong = e; } });
        thread.Start(); thread.Join();
        Check(wrong is InvalidOperationException && TeardownState.Actor.active && TeardownState.Reservation.IsReserved(3)
            && TeardownState.Disconnects == 0, "actual teardown rejects foreign-thread unload before Player mutation");
        finish("owner cleanup");
        Reset(); TeardownState.EngineMainThread = false;
        try { finish("same thread but not engine main thread"); throw new Exception("engine thread check missing"); } catch (InvalidOperationException) { }
        Check(TeardownState.Actor.active && TeardownState.Reservation.IsReserved(3) && TeardownState.Disconnects == 0,
            "actual teardown requires Program.IsMainThread even when captured thread ID matches");
        void ExpectQuarantine(string label, Action prepare)
        {
            Reset(); prepare(); var player = MainState.player[3]; var actor = TeardownState.Actor; var placeholder = TeardownState.previous;
            var playerActive = player?.active; var actorActive = actor?.active; var placeholderActive = placeholder?.active;
            int originalSlot = TeardownState.slot; bool originalConnectedHook = TeardownState.connectedHook;
            bool Unchanged() => ReferenceEquals(MainState.player[3], player) && player?.active == playerActive
                && ReferenceEquals(TeardownState.Actor, actor) && actor?.active == actorActive
                && ReferenceEquals(TeardownState.previous, placeholder) && placeholder?.active == placeholderActive
                && TeardownState.slot == originalSlot && TeardownState.connectedHook == originalConnectedHook
                && TeardownState.Reservation.IsReserved(3) && TeardownState.Disconnects == 0;
            try { finish(label); throw new Exception("quarantine missing"); } catch (InvalidOperationException) { }
            Check(Unchanged() && TeardownState.Fault != null && TeardownState.Quarantined,
                "actual teardown quarantines without Player writes/release: " + label);
            try { finish("repeat quarantined cleanup"); throw new Exception("quarantine retried"); } catch (InvalidOperationException) { }
            Check(Unchanged() && TeardownState.Logs == 1, "quarantined cleanup cannot retry/release: " + label);
        }
        ExpectQuarantine("connected human", () => { MainState.player[3] = new PlayerState { active = true }; NetState.Clients[3].Socket = new SocketState(); });
        ExpectQuarantine("disconnected but IsActive", () => { NetState.Clients[3].Socket = new SocketState { Connected = false }; NetState.Clients[3].IsActive = true; });
        ExpectQuarantine("handshake State=1", () => NetState.Clients[3].State = 1);
        ExpectQuarantine("negative State=-1", () => NetState.Clients[3].State = -1);
        ExpectQuarantine("spawn State=3", () => NetState.Clients[3].State = 3);
        ExpectQuarantine("State=10 with no socket", () => NetState.Clients[3].State = 10);
        ExpectQuarantine("PendingTermination", () => NetState.Clients[3].PendingTermination = true);
        ExpectQuarantine("PendingTerminationApproved", () => NetState.Clients[3].PendingTerminationApproved = true);
        ExpectQuarantine("unknown RemoteClient", () => NetState.Clients[3] = null);
        ExpectQuarantine("unknown Player reference despite idle connection", () => MainState.player[3] = new PlayerState { active = true });
        void BeforeSpawn()
        {
            TeardownState.Actor = null; TeardownState.connectedHook = false;
            MainState.player[3] = TeardownState.previous;
        }
        ExpectQuarantine("pre-spawn active original placeholder with idle RemoteClient", () => {
            BeforeSpawn(); MainState.player[3].active = true;
        });
        ExpectQuarantine("initial active Player before placeholder baseline exists", () => {
            BeforeSpawn(); TeardownState.previous = null; TeardownState.slot = -1; MainState.player[3].active = true;
        });
        ExpectQuarantine("pre-spawn inactive replacement Player with idle RemoteClient", () => {
            BeforeSpawn(); MainState.player[3] = new PlayerState();
        });
        ExpectQuarantine("pre-spawn null Player with idle RemoteClient", () => {
            BeforeSpawn(); MainState.player[3] = null;
        });
        ExpectQuarantine("pre-spawn unestablished placeholder baseline", () => {
            BeforeSpawn(); TeardownState.previous = null; TeardownState.slot = -1;
        });
        ExpectQuarantine("active restoration placeholder while actor exists", () => TeardownState.previous.active = true);
        Reset(); BeforeSpawn(); NetState.Clients[3].State = 1;
        var beforeSpawnPlayer = MainState.player[3];
        try { finish("transition before actor creation"); throw new Exception("unused reservation released early"); } catch (InvalidOperationException) { }
        Check(TeardownState.Reservation.IsReserved(3) && ReferenceEquals(MainState.player[3], beforeSpawnPlayer)
            && TeardownState.Disconnects == 0 && TeardownState.Quarantined,
            "connection transition before spawn also quarantines startup reservation without release");
        Reset(); BeforeSpawn(); var unusedPlaceholder = MainState.player[3];
        finish("world exit before spawn");
        Check(TeardownState.Reservation.Snapshot == null && TeardownState.Disconnects == 0
            && ReferenceEquals(MainState.player[3], unusedPlaceholder) && !unusedPlaceholder.active,
            "actual teardown releases unused reservation only with known unchanged inactive placeholder");

        foreach (string mutation in new[] { "replacement", "restore activated", "pending termination", "identity", "reactivated" })
        {
            Reset(); var owned = TeardownState.Actor; var replacement = new PlayerState { active = true };
            TeardownState.OnDisconnect = () => {
                if (mutation == "replacement") MainState.player[3] = replacement;
                else if (mutation == "restore activated") TeardownState.previous.active = true;
                else if (mutation == "pending termination") NetState.Clients[3].PendingTermination = true;
                else if (mutation == "identity") owned.whoAmI = 2;
                else owned.active = true;
            };
            try { finish(mutation); throw new Exception("callback ownership check missing"); } catch (InvalidOperationException) { }
            Check(TeardownState.Quarantined && TeardownState.Reservation.IsReserved(3)
                && ReferenceEquals(TeardownState.Actor, owned) && TeardownState.Disconnects == 1
                && (mutation != "replacement" || ReferenceEquals(MainState.player[3], replacement) && replacement.active),
                "actual teardown revalidates AFTER disconnect callback and retains unknown slot: " + mutation);
        }
        Reset(); bool reentryRejected = false;
        TeardownState.OnDisconnect = () => {
            try { finish("nested"); } catch (InvalidOperationException) { reentryRejected = true; }
        };
        finish("outer");
        Check(reentryRejected && TeardownState.Disconnects == 1 && TeardownState.Reservation.Snapshot == null,
            "actual teardown rejects callback reentry and outer cleanup remains safe");

        Reset(); Check(eligible(3) && !eligible(2), "actual packaged predicate protects only reference-confirmed reserved actor");
        Reset(); TeardownState.Actor = null;
        Check(!eligible(3), "actual packaged predicate never treats reservation alone as Player ownership");
        Reset(); var unknown = MainState.player[3] = new PlayerState { active = true };
        Check(!eligible(3) && TeardownState.Quarantined && unknown.active && TeardownState.Reservation.IsReserved(3),
            "actual predicate quarantines replacement without writes/release and leaves native cleanup available");
        foreach (string state in new[] { "connected", "active", "state", "pending", "approved", "unknown" })
        {
            Reset();
            if (state == "connected") NetState.Clients[3].Socket = new SocketState();
            else if (state == "active") NetState.Clients[3].IsActive = true;
            else if (state == "state") NetState.Clients[3].State = 1;
            else if (state == "pending") NetState.Clients[3].PendingTermination = true;
            else if (state == "approved") NetState.Clients[3].PendingTerminationApproved = true;
            else NetState.Clients[3] = null;
            Check(!eligible(3) && TeardownState.Quarantined && TeardownState.Actor.active && TeardownState.Reservation.IsReserved(3),
                "actual predicate quarantines connection transition while retaining native paths: " + state);
        }
        Reset(); NetState.Clients[3].PendingTermination = true;
        bool nativeTransitionCalled = false;
        networkTick(null, () => nativeTransitionCalled = true);
        Check(nativeTransitionCalled && TeardownState.Quarantined && TeardownState.Actor.active
            && TeardownState.Reservation.IsReserved(3),
            "actual network wrapper quarantines waiting termination before native branch bypasses IL guard");

        void BeforeCreate()
        {
            Reset(); BeforeSpawn(); TeardownState.previous = null; TeardownState.slot = -1;
            initial();
        }
        BeforeCreate(); var baseline = MainState.player[3];
        spawn(); var created = TeardownState.Actor; spawn();
        Check(created != null && !ReferenceEquals(created, baseline) && ReferenceEquals(MainState.player[3], created)
            && created.whoAmI == 3 && created.active && TeardownState.Connects == 1 && eligible(3)
            && LifecycleAnnouncements.Joins == 1,
            "actual packaged Spawn privately constructs, publishes once and duplicate spawn is idempotent");
        finish("spawn lifecycle");
        Check(ReferenceEquals(MainState.player[3], baseline) && TeardownState.Reservation.Snapshot == null && LifecycleAnnouncements.Leaves == 1,
            "actual packaged Spawn/Finish restore the original baseline in one lifecycle");
        BeforeCreate(); baseline=MainState.player[3]; spawn();
        finishWithLease("G3 normal despawn",true);
        Check(TeardownState.Actor==null && ReferenceEquals(MainState.player[3],baseline) && TeardownState.Reservation.IsReserved(3),
            "G3 despawn retains original lease and idle baseline");
        var restored=new PlayerState{statLife=117,name="restored profile"}; LifecycleCharacterStore.Candidate=restored;
        spawn();
        Check(ReferenceEquals(TeardownState.Actor,restored) && restored.statLife==117 && restored.name=="restored profile",
            "G3 publishes private loaded candidate without resetting character data");
        LifecycleCharacterStore.FailedSave=true;
        try{finishWithLease("save failed",true);throw new Exception("live data lost");}catch(IOException){}
        Check(ReferenceEquals(MainState.player[3],restored) && restored.active && TeardownState.Reservation.IsReserved(3),
            "G3 failed save prevents retirement of live actor");
        LifecycleCharacterStore.FailedSave=false; finish("G3 recovered");
        BeforeCreate(); baseline = MainState.player[3];
        PlayerState.OnConstruct = () => throw new IOException("constructor failure");
        try { spawn(); throw new Exception("constructor exception lost"); } catch (IOException) { }
        PlayerState.OnConstruct = null;
        Check(TeardownState.Actor == null && ReferenceEquals(MainState.player[3], baseline) && !baseline.active
            && TeardownState.Reservation.IsReserved(3) && TeardownState.Connects == 0 && TeardownState.Disconnects == 0,
            "actual constructor failure rolls back known unused slot without publishing or disconnecting");
        BeforeCreate(); unknown = new PlayerState();
        PlayerState.OnConstruct = () => MainState.player[3] = unknown;
        try { spawn(); throw new Exception("publication validation missing"); } catch (InvalidOperationException) { }
        PlayerState.OnConstruct = null;
        Check(TeardownState.Actor == null && ReferenceEquals(MainState.player[3], unknown) && !unknown.active
            && TeardownState.Quarantined && TeardownState.Reservation.IsReserved(3) && TeardownState.Connects == 0,
            "actual post-constructor validation refuses replacement baseline without overwriting/releasing");
        BeforeCreate(); unknown = new PlayerState { active = true };
        PlayerState.OnConstruct = () => { MainState.player[3] = unknown; throw new IOException("constructor mutated then failed"); };
        try { spawn(); throw new Exception("unsafe constructor rollback accepted"); } catch (InvalidOperationException) { }
        PlayerState.OnConstruct = null;
        Check(TeardownState.Quarantined && ReferenceEquals(MainState.player[3], unknown) && unknown.active
            && TeardownState.Actor == null && TeardownState.Reservation.IsReserved(3) && TeardownState.Disconnects == 0,
            "actual throwing constructor retains unknown Player/reservation instead of unsafe rollback");
        BeforeCreate(); baseline = MainState.player[3];
        TeardownState.OnConnect = () => throw new IOException("connect failure");
        try { spawn(); throw new Exception("connect exception lost"); } catch (IOException) { }
        Check(TeardownState.Actor == null && ReferenceEquals(MainState.player[3], baseline)
            && TeardownState.Reservation.IsReserved(3) && TeardownState.Disconnects == 1,
            "actual connect failure rolls back published actor and disconnects exactly once");
        BeforeCreate(); unknown = new PlayerState { active = true };
        TeardownState.OnConnect = () => MainState.player[3] = unknown;
        try { spawn(); throw new Exception("connect mutation lost"); } catch (InvalidOperationException) { }
        Check(TeardownState.Quarantined && ReferenceEquals(MainState.player[3], unknown) && unknown.active
            && TeardownState.Reservation.IsReserved(3) && TeardownState.Disconnects == 0,
            "actual connect replacement quarantines without touching unknown Player or releasing");
        BeforeCreate(); WorldState.Clear = false;
        try { spawn(); throw new Exception("blocked location accepted"); } catch (InvalidOperationException) { }
        Check(TeardownState.Actor == null && TeardownState.Reservation.IsReserved(3) && TeardownState.Connects == 0,
            "actual no-clear-position failure retains the verified unused baseline and lease");
        BeforeCreate(); Exception spawnThread = null;
        var wrongCreator = new Thread(() => { try { spawn(); } catch (Exception e) { spawnThread = e; } });
        wrongCreator.Start(); wrongCreator.Join();
        Check(spawnThread is InvalidOperationException && TeardownState.Actor == null && TeardownState.Reservation.IsReserved(3),
            "actual Spawn rejects foreign thread before construction/publication");

        // Execute the packaged accept/network wrappers, including their locks.
        BeforeCreate(); bool acceptHeld = false, networkHeld = false;
        accepted(null, _ => acceptHeld = Monitor.IsEntered(TeardownState.Reservation.Gate), new SocketState());
        networkTick(null, () => networkHeld = Monitor.IsEntered(TeardownState.Reservation.Gate));
        Check(acceptHeld && networkHeld, "actual accept and complete native network pass share lifecycle Gate");
        using var entered = new ManualResetEventSlim(); using var completed = new ManualResetEventSlim();
        Exception acceptFailure = null;
        var concurrentAccept = new Thread(() => {
            entered.Set();
            try { accepted(null, _ => { }, new SocketState()); } catch (Exception e) { acceptFailure = e; }
            finally { completed.Set(); }
        });
        bool excluded = false;
        PlayerState.OnConstruct = () => {
            concurrentAccept.Start();
            if (!entered.Wait(2000)) throw new Exception("accept worker not started");
            excluded = !completed.Wait(100);
        };
        spawn(); PlayerState.OnConstruct = null;
        Check(concurrentAccept.Join(2000) && excluded && acceptFailure == null && TeardownState.Actor.active,
            "actual Spawn holds Gate across construction/publication and excludes concurrent native accept");
        finish("concurrent lifecycle");
        BeforeCreate(); var failedSyncBaseline = MainState.player[3]; LifecycleNetworkState.ThrowFull = true;
        try { spawn(); throw new Exception("full send failure lost"); } catch (IOException) { }
        Check(TeardownState.Actor == null && ReferenceEquals(MainState.player[3], failedSyncBaseline)
            && TeardownState.Reservation.IsReserved(3) && TeardownState.Disconnects == 1
            && LifecycleAnnouncements.Joins == 0 && LifecycleAnnouncements.Leaves == 0,
            "actual G2 Spawn rolls back published actor after native initial-sync failure");
        Reset(); var retainedActor = TeardownState.Actor; LifecycleNetworkState.ThrowLeave = true;
        try { finish("departure fails"); throw new Exception("unsafe departure release"); } catch (InvalidOperationException) { }
        Check(TeardownState.Quarantined && retainedActor.active && ReferenceEquals(MainState.player[3], retainedActor)
            && TeardownState.Reservation.IsReserved(3) && TeardownState.Disconnects == 0,
            "actual G2 teardown retains actor/reservation when departure cannot be synchronized");
    }
}
