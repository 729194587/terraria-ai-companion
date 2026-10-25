using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;

namespace VirtualPlayerEngineProbe;

// Shared by the live IL handlers and the actual-installed-IL offline fixture.
// Only the per-slot eligibility edge changes. The native human branches stay intact.
public static class EnginePatches
{
    // Keep the engine's payload and human branch intact. Cache the ownership
    // decision per invocation; never write RemoteClient.State or announcements.
    public static void PlayerSync(MethodDefinition method, MethodReference owns)
    {
        var body = method.Body;
        var code = body.Instructions;
        bool Call(Instruction i, string type, string name) => i?.Operand is MethodReference m
            && m.DeclaringType.FullName == type && m.Name == name;
        bool Field(Instruction i, string type, string name) => i?.Operand is FieldReference f
            && f.DeclaringType.FullName == type && f.Name == name;
        Instruction At(int offset) => code.SingleOrDefault(i => i.Offset == offset);
        Instruction Target(Instruction branch) => branch?.Operand is ILLabel l ? l.Target : branch?.Operand as Instruction;
        var guard = At(0x12);
        var payload = At(0x25);
        var boundary = At(0x36f);
        var offline = At(0x3f7);
        if (!method.Name.Contains("SyncOnePlayer", StringComparison.Ordinal) || !method.IsStatic
            || method.ReturnType.MetadataType != MetadataType.Void || method.Parameters.Count != 3
            || method.Parameters.Any(p => p.ParameterType.MetadataType != MetadataType.Int32)
            || body.ExceptionHandlers.Count != 0 || body.Variables.Count != 4
            || At(0)?.OpCode != OpCodes.Ldc_I4_0 || At(1)?.OpCode != OpCodes.Stloc_0
            || guard?.OpCode != OpCodes.Ldsfld || !Field(guard, "Terraria.Netplay", "Clients")
            || At(0x17)?.OpCode != OpCodes.Ldarg_0 || At(0x18)?.OpCode != OpCodes.Ldelem_Ref
            || At(0x19)?.OpCode != OpCodes.Ldfld || !Field(At(0x19), "Terraria.RemoteClient", "State")
            || At(0x1e)?.OpCode != OpCodes.Ldc_I4_S || (sbyte)At(0x1e).Operand != 10
            || At(0x20)?.OpCode != OpCodes.Bne_Un || Target(At(0x20)) != offline
            || payload?.OpCode != OpCodes.Ldc_I4_S || (sbyte)payload.Operand != 14
            || boundary?.OpCode != OpCodes.Ldsfld || !Field(boundary, "Terraria.Netplay", "Clients")
            || !Call(boundary.Previous, "Terraria.ModLoader.PlayerLoader", "SyncPlayer")
            || At(0x376)?.OpCode != OpCodes.Ldfld || !Field(At(0x376), "Terraria.RemoteClient", "IsAnnouncementCompleted")
            || offline?.OpCode != OpCodes.Ldc_I4_0
            || !Call(At(0x4af), "Terraria.Player/Hooks", "PlayerDisconnect") || code.Last().OpCode != OpCodes.Ret
            || code.Count(i => Field(i, "Terraria.RemoteClient", "State")) != 1
            || code.Count(i => Call(i, "Terraria.ModLoader.PlayerLoader", "SyncPlayer")) != 1
            || code.Any(i => i.Operand is MethodReference m && m.Name == owns.Name))
            throw new InvalidOperationException("Unsupported SyncOnePlayer IL; G2 visibility patch not installed.");
        // Validate everything before mutating anything, including under ILContext.
        var owned = new VariableDefinition(method.Module.TypeSystem.Boolean);
        body.Variables.Add(owned);
        var il = body.GetILProcessor();
        var first = code[0];
        il.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_0));
        il.InsertBefore(first, Instruction.Create(OpCodes.Call, owns));
        il.InsertBefore(first, Instruction.Create(OpCodes.Stloc, owned));
        il.InsertBefore(guard, Instruction.Create(OpCodes.Ldloc, owned));
        il.InsertBefore(guard, Instruction.Create(OpCodes.Brtrue, payload));
        il.InsertBefore(boundary, Instruction.Create(OpCodes.Ldloc, owned));
        il.InsertBefore(boundary, Instruction.Create(OpCodes.Brfalse, boundary));
        il.InsertBefore(boundary, Instruction.Create(OpCodes.Ret));
    }

    public static void Allocator(MethodDefinition method, MethodReference reserved)
        => Filter(method, reserved, "FindNextOpenClientSlot", 0x0004, 0x0014, 0,
            "Terraria.RemoteClient", "IsConnected");

    public static void Cleanup(MethodDefinition method, MethodReference reserved)
        => Filter(method, reserved, "UpdateConnectedClients", 0x00ab, 0x00d4, 1,
            "Terraria.Player/Hooks", "PlayerDisconnect");

    private static void Filter(MethodDefinition method, MethodReference reserved, string name,
        int entryOffset, int nextOffset, int local, string declaringType, string callName)
    {
        // RuntimeDetour gives the IL clone a DMD wrapper name, not the bare name.
        if (!method.Name.Contains(name, StringComparison.Ordinal) || method.Parameters.Count != 0 || !method.IsStatic
            || method.Body.ExceptionHandlers.Count != 0)
            throw new InvalidOperationException("Unsupported method contract: " + name);
        var instructions = method.Body.Instructions;
        var entry = instructions.SingleOrDefault(i => i.Offset == entryOffset);
        var next = instructions.SingleOrDefault(i => i.Offset == nextOffset);
        bool entryMatches = local == 0
            ? entry?.OpCode == OpCodes.Ldsfld && entry.Operand is FieldReference f
                && f.FullName == "Terraria.RemoteClient[] Terraria.Netplay::Clients"
            : entry?.OpCode == OpCodes.Ldloc_1 && entry.Previous?.OpCode == OpCodes.Stfld
                && entry.Previous.Operand is FieldReference status
                && status.FullName == "System.String Terraria.RemoteClient::StatusText2";
        if (!entryMatches
            || next == null || next.OpCode != (local == 0 ? OpCodes.Ldloc_0 : OpCodes.Ldloc_1)
            || !instructions.Any(i => i.Operand is MethodReference m
                && m.DeclaringType.FullName == declaringType && m.Name == callName)
            || instructions.Any(i => i.Operand is MethodReference m && m.Name == reserved.Name))
            throw new InvalidOperationException("Unsupported installed IL shape: " + name);
        var incoming = instructions.Where(i => ReferenceEquals(i.Operand, entry)
            || i.Operand is ILLabel label && ReferenceEquals(label.Target, entry)).ToArray();
        if (incoming.Length != (local == 0 ? 1 : 0)) throw new InvalidOperationException("Unexpected entry edges: " + name);
        var processor = method.Body.GetILProcessor();
        var load = Instruction.Create(local == 0 ? OpCodes.Ldloc_0 : OpCodes.Ldloc_1);
        processor.InsertBefore(entry, load);
        processor.InsertBefore(entry, Instruction.Create(OpCodes.Call, reserved));
        processor.InsertBefore(entry, Instruction.Create(OpCodes.Brtrue, next));
        foreach (var edge in incoming)
            if (edge.Operand is ILLabel label) label.Target = load;
            else edge.Operand = load;
    }
}
