using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Only storage and external effects are substituted. Two COMPLETE installed IL
// method bodies execute unchanged; no Terraria static initializer or game runs.
// This proves native lifecycle behavior with fixtures, NOT live multiplayer.
public static class NativeLifecycleTests
{
    public class EntityState { public bool active; }
    public class PlayerState : EntityState { }
    public static class MainState
    {
        public static PlayerState[] player;
        public static int maxNetPlayers;
    }
    public class RemoteState
    {
        public bool Connected, IsActive, PendingTerminationApproved;
        public bool PendingTermination { get; set; }
        public int State, Updates, Resets;
        public string StatusText2;
        public bool IsConnected() => Connected;
        public void Update() => Updates++;
        public void SetPendingTermination(string reason) => PendingTermination = true;
        public void Reset() { Resets++; IsActive = false; PendingTermination = false; State = 0; }
    }
    public static class NetState
    {
        public static RemoteState[] Clients;
        public static bool HasClients;
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

    private static int checks;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        checks++;
        Console.WriteLine("PASS: " + name);
    }

    public static void Run(string gamePath, string output, string source, string package)
    {
        Directory.CreateDirectory(output);
        string dll = Path.Combine(gamePath, "tModLoader.dll");
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll)));
        Check(File.ReadAllText(source).Contains("\"" + hash + "\""), "installed DLL matches runtime fingerprint");
        using var game = AssemblyDefinition.ReadAssembly(dll);
        var types = game.MainModule.Types.ToDictionary(t => t.FullName);
        MethodDefinition Method(string type, string name) => types[type].Methods.Single(m => m.Name == name);
        var lifecycle = Method("Terraria.Netplay", "UpdateConnectedClients");
        var allocate = Method("Terraria.Netplay", "FindNextOpenClientSlot");
        var sync = Method("Terraria.NetMessage", "SyncOnePlayer");
        var accepted = Method("Terraria.Netplay", "OnConnectionAccepted");
        var reset = Method("Terraria.RemoteClient", "Reset");
        var receive = Method("Terraria.MessageBuffer", "GetData");
        var send = Method("Terraria.NetMessage", "SendData");
        var update = types["Terraria.Player"].Methods.Single(m => m.Name == "Update" && m.Parameters.Count == 1);
        foreach (var m in new[] { lifecycle, allocate, sync, accepted, reset, receive, send, update,
            Method("Terraria.Netplay", "ServerLoop"), Method("Terraria.Main", "DoUpdateInWorld"),
            Method("Terraria.NetMessage", "SyncConnectedPlayer"), Method("Terraria.Main", "TrySyncingMyPlayer"),
            Method("Terraria.NetMessage", "DoesPlayerSlotCountAsAHost"),
            Method("Terraria.Player", "Spawn"), Method("Terraria.Main", "DoUpdate"),
            Method("Terraria.RemoteClient", "IsConnected") })
            File.WriteAllLines(Path.Combine(output, m.DeclaringType.Name + "-" + m.Name + ".il.txt"),
                new[] { m.FullName }.Concat(m.Body.Instructions.Select(i => i.ToString())));

        Check(!allocate.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "player"),
            "native allocator never consults Main.player");
        Check(Calls(accepted, "FindNextOpenClientSlot") && Calls(accepted, "Reset"),
            "connection acceptance allocates and resets slot");
        Check(reset.Body.Instructions.Any(i => i.OpCode == OpCodes.Newobj && i.Operand is MethodReference m
                && m.DeclaringType.FullName == "Terraria.Player") && reset.Body.Instructions.Any(i => i.OpCode == OpCodes.Stelem_Ref),
            "RemoteClient.Reset replaces Main.player element");
        // The initial State==10 branch must bypass the connected sync path.
        var state = sync.Body.Instructions.Single(i => i.Offset == 0x19);
        Check(state.Operand is FieldReference sf && sf.FullName == "System.Int32 Terraria.RemoteClient::State"
            && state.Next.OpCode == OpCodes.Ldc_I4_S && (sbyte)state.Next.Operand == 10
            && state.Next.Next.OpCode == OpCodes.Bne_Un
            && state.Next.Next.Operand is Instruction target && target.Offset == 0x3f7,
            "SyncOnePlayer branches to disconnected path when State != 10");
        // Exact stack arguments: loc0=0, message=14, remote=-1, ignore=slot,
        // text=null, number=slot, number2=(float)loc0, followed by zero defaults.
        string[] prefix = { "ldc.i4.0", "stloc.0", "ldc.i4.s 14", "ldc.i4.m1", "ldarg.0",
            "ldnull", "ldarg.0", "ldloc.0", "conv.r4" };
        var disconnected = sync.Body.Instructions.Where(i => i.Offset >= 0x3f7 && i.Offset <= 0x400).ToArray();
        Check(disconnected.Select(i => i.ToString().Split(": ")[1]).SequenceEqual(prefix)
            && sync.Body.Instructions.Single(i => i.Offset == 0x40e).Operand is MethodReference sm && sm.Name == "SendData",
            "disconnected path broadcasts PlayerActive(14), number2=0");
        Check(Calls(Method("Terraria.NetMessage", "SyncConnectedPlayer"), "SyncOnePlayer"),
            "late-join baseline invokes the same gated SyncOnePlayer");
        Check(Calls(update, "PreUpdate") && Calls(update, "PreUpdateMovement")
            && Calls(update, "DryCollision") && Calls(update, "PlayerFrame"),
            "installed Player.Update contains hooks, collision and animation");
        var loop = Method("Terraria.Main", "DoUpdateInWorld");
        Check(Calls(loop, "PreUpdatePlayers") && Calls(loop, "Update") && Calls(loop, "PostUpdatePlayers"),
            "world loop has native Player update between supported system hooks");
        var dispatch = receive.Body.Instructions.First(i => i.OpCode == OpCodes.Switch);
        var cases = (Instruction[])dispatch.Operand;
        int start = cases[12].Offset; // dispatch indexes message ID minus one
        int end = cases[13].Offset;
        var controls = receive.Body.Instructions.Where(i => i.Offset >= start && i.Offset < end).ToArray();
        File.WriteAllLines(Path.Combine(output, "MessageBuffer-PlayerControls.il.txt"), controls.Select(i => i.ToString()));
        var owner = controls.Single(i => i.Offset == 0x273f);
        Check(owner.Operand is FieldReference of && of.FullName == "System.Int32 Terraria.MessageBuffer::whoAmI"
            && owner.Next.OpCode == OpCodes.Stloc_S && ((VariableDefinition)owner.Next.Operand).Index == 83
            && controls.Single(i => i.Offset == 0x2736).Operand is FieldReference nf && nf.Name == "netMode"
            && controls.Single(i => i.Offset == 0x273b).OpCode == OpCodes.Ldc_I4_2,
            "incoming PlayerControls replaces payload slot with connection whoAmI on server");

        using var fixture = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("NativeLifecycleFixture", new Version(1, 0)),
            "NativeLifecycleFixture", ModuleKind.Dll);
        ModuleDefinition module = fixture.MainModule;
        var map = new Dictionary<string, Type> {
            ["Terraria.Main"] = typeof(MainState), ["Terraria.Entity"] = typeof(EntityState),
            ["Terraria.Player"] = typeof(PlayerState), ["Terraria.RemoteClient"] = typeof(RemoteState),
            ["Terraria.Netplay"] = typeof(NetState), ["Terraria.NetMessage"] = typeof(MessageState),
            ["Terraria.Player/Hooks"] = typeof(HookState)
        };
        TypeDefinition runner = new("Fixture", "Runner", Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Abstract
            | Mono.Cecil.TypeAttributes.Sealed, module.TypeSystem.Object);
        module.Types.Add(runner);
        TypeReference ImportType(TypeReference t) => t is ArrayType a ? new ArrayType(ImportType(a.ElementType))
            : map.TryGetValue(t.FullName, out var mapped) ? module.ImportReference(mapped) : module.ImportReference(t);
        object ImportOperand(object operand, MethodDefinition dst, Dictionary<Instruction, Instruction> instructions) => operand switch {
            Instruction i => instructions[i], Instruction[] branch => branch.Select(i => instructions[i]).ToArray(),
            VariableDefinition v => dst.Body.Variables[v.Index], ParameterDefinition p => dst.Parameters[p.Index],
            FieldReference f when map.TryGetValue(f.DeclaringType.FullName, out var t)
                => module.ImportReference(t.GetField(f.Name) ?? throw new Exception("Unmapped field: " + f.FullName)),
            MethodReference m when map.TryGetValue(m.DeclaringType.FullName, out var t)
                => module.ImportReference(t.GetMethods().Single(x => x.Name == m.Name && x.GetParameters().Length == m.Parameters.Count)),
            TypeReference t => ImportType(t), _ => operand
        };
        foreach (var src in new[] { lifecycle, allocate })
        {
            Check(src.Body.ExceptionHandlers.Count == 0 && src.Parameters.Count == 0, "supported complete IL shape: " + src.Name);
            var dst = new MethodDefinition(src.Name, Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static,
                ImportType(src.ReturnType));
            runner.Methods.Add(dst);
            dst.Body.InitLocals = src.Body.InitLocals;
            dst.Body.MaxStackSize = src.Body.MaxStackSize;
            foreach (var v in src.Body.Variables) dst.Body.Variables.Add(new VariableDefinition(ImportType(v.VariableType)));
            var ins = src.Body.Instructions.ToDictionary(i => i, _ => Instruction.Create(OpCodes.Nop));
            foreach (var i in src.Body.Instructions)
            {
                ins[i].OpCode = i.OpCode;
                ins[i].Operand = ImportOperand(i.Operand, dst, ins);
                dst.Body.Instructions.Add(ins[i]);
            }
            Check(dst.Body.Instructions.Select(i => i.OpCode).SequenceEqual(src.Body.Instructions.Select(i => i.OpCode)),
                "all opcodes preserved: " + src.Name);
        }
        using var bytes = new MemoryStream();
        fixture.Write(bytes); bytes.Position = 0;
        var loaded = AssemblyLoadContext.Default.LoadFromStream(bytes).GetType("Fixture.Runner");
        var tick = (Action)loaded.GetMethod(lifecycle.Name).CreateDelegate(typeof(Action));
        var next = (Func<int>)loaded.GetMethod(allocate.Name).CreateDelegate(typeof(Func<int>));
        void ResetFixture()
        {
            MainState.player = Enumerable.Range(0, 256).Select(_ => new PlayerState()).ToArray();
            MainState.maxNetPlayers = 4;
            NetState.Clients = Enumerable.Range(0, 256).Select(_ => new RemoteState()).ToArray();
            NetState.HasClients = false;
            MessageState.Disconnected.Clear(); HookState.Disconnected.Clear();
            MainState.player[0].active = true;
            NetState.Clients[0].Connected = true;
            NetState.Clients[0].IsActive = true;
            NetState.Clients[0].State = 10;
        }
        ResetFixture();
        MainState.player[1].active = true;
        Check(next() == 1, "minimal repro: active virtual Player slot is still allocatable to a human");
        tick();
        Check(!MainState.player[1].active && HookState.Disconnected.SequenceEqual(new[] { 1 }),
            "minimal repro: native network tick deactivates virtual Player and calls disconnect");
        Check(MainState.player[0].active && NetState.Clients[0].Updates == 1 && NetState.HasClients,
            "connected human remains active and updates normally in lifecycle fixture");
        ResetFixture();
        MainState.player[1].active = true; NetState.Clients[1].State = 10;
        tick();
        Check(!MainState.player[1].active && next() == 1, "spoofing State=10 alone cannot retain or reserve actor");
        ResetFixture();
        MainState.player[1].active = true; NetState.Clients[1].IsActive = true;
        tick();
        Check(NetState.Clients[1].PendingTermination && NetState.Clients[1].PendingTerminationApproved,
            "spoofing IsActive schedules connection-lost termination");
        tick();
        Check(NetState.Clients[1].Resets == 1 && MessageState.Disconnected.SequenceEqual(new[] { 1 }),
            "next native network tick synchronizes disconnect and resets client");
        ResetFixture();
        foreach (var c in NetState.Clients.Take(MainState.maxNetPlayers)) c.Connected = true;
        Check(next() == -1, "native allocator reports full configured connection range");
        AuditPackage(package);
        Console.WriteLine($"NativeLifecycleTests: {checks} checks passed; SHA256={hash}; G1-G3 LIVE NOT RUN.");
    }

    private static void AuditPackage(string package)
    {
        using var input = File.OpenRead(package);
        using var reader = new BinaryReader(input);
        Check(System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4)) == "TMOD", "built artifact has TMOD header");
        string loader = reader.ReadString();
        reader.ReadBytes(276); reader.ReadInt32(); // hash, signature, data length
        Check(reader.ReadString() == "VirtualPlayerProbe" && reader.ReadString() == "0.1.0", "isolated probe package identity");
        int count = reader.ReadInt32(), offset = 0, dllOffset = -1, length = 0, compressed = 0;
        for (int i = 0; i < count; i++)
        {
            string name = reader.ReadString();
            int size = reader.ReadInt32(), packed = reader.ReadInt32();
            if (name == "VirtualPlayerProbe.dll") { dllOffset = offset; length = size; compressed = packed; }
            offset += packed;
        }
        Check(dllOffset >= 0, "package contains compiled probe DLL");
        input.Position += dllOffset;
        using var payload = new MemoryStream(reader.ReadBytes(compressed));
        using var bytes = new MemoryStream();
        if (length == compressed) payload.CopyTo(bytes);
        else
        {
            using var inflate = new System.IO.Compression.DeflateStream(payload, System.IO.Compression.CompressionMode.Decompress);
            inflate.CopyTo(bytes);
        }
        Check(bytes.Length == length, "compiled DLL extracted at declared size");
        bytes.Position = 0;
        using var probe = AssemblyDefinition.ReadAssembly(bytes);
        IEnumerable<TypeDefinition> Descend(TypeDefinition t) => new[] { t }.Concat(t.NestedTypes.SelectMany(Descend));
        var types = probe.MainModule.Types.SelectMany(Descend).ToArray();
        var instructions = types.SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).ToArray();
        Check(!probe.MainModule.AssemblyReferences.Any(a => a.Name.Contains("CompanionBridge")), "package independent of CompanionBridge");
        Check(!instructions.Any(i => (i.OpCode == OpCodes.Stfld || i.OpCode == OpCodes.Stsfld)
            && i.Operand is FieldReference f && f.DeclaringType.Namespace.StartsWith("Terraria")),
            "compiled probe writes no Terraria engine fields");
        Check(!instructions.Any(i => i.Operand is MethodReference m && m.DeclaringType.FullName == "Terraria.Player"
            && m.Name is "Update" or "Spawn" or "Teleport"), "compiled probe never manually drives, spawns or teleports a Player");
        Check(!instructions.Any(i => i.OpCode == OpCodes.Newobj && i.Operand is MethodReference m
            && m.DeclaringType.FullName is "Terraria.Player" or "Terraria.NPC" or "Terraria.RemoteClient"),
            "compiled negative probe allocates no actor, NPC substitute or RemoteClient");
        Check(!instructions.Any(i => i.Operand is MethodReference m && (m.Name == "SendData"
            || m.DeclaringType.FullName.Contains("Socket") || m.DeclaringType.FullName == "System.Diagnostics.Process")),
            "compiled probe sends no actor packets and opens no sockets/processes");
        Check(instructions.Any(i => i.Operand is MethodReference m && m.Name == "QueueMainThreadAction")
            && instructions.Any(i => i.Operand is MethodReference h && h.Name == "DoesPlayerSlotCountAsAHost"),
            "compiled commands retain game-thread queue and native host authorization");
        var command = types.Single(t => t.Name == "ProbeCommand");
        Check(command.Methods.Single(m => m.Name == "get_Type").Body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_I4_S
            && (sbyte)i.Operand == 12), "vplayer uses World|Console routing, not local multiplayer chat");
        var hook = types.Single(t => t.Name == "ProbeSystem").Methods.Single(m => m.Name == "HijackSendData");
        Check(hook.Body.Instructions.Last().OpCode == OpCodes.Ret && hook.Body.Instructions.Last().Previous.OpCode == OpCodes.Ldc_I4_0,
            "packet observation returns false to preserve native serialization");
        Console.WriteLine($"Package audit: loader={loader}; bytes={input.Length}; DLL={length}.");
    }

    private static bool Calls(MethodDefinition method, string name) => method.Body.Instructions
        .Any(i => i.Operand is MethodReference m && m.Name == name);
}
