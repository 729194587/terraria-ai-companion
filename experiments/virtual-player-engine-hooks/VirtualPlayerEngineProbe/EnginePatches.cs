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
    public static void Allocator(MethodDefinition method, MethodReference reserved)
        => Filter(method, reserved, "FindNextOpenClientSlot", 0x0004, 0x0014, 0,
            "Terraria.RemoteClient", "IsConnected");

    public static void Cleanup(MethodDefinition method, MethodReference reserved)
        => Filter(method, reserved, "UpdateConnectedClients", 0x0009, 0x00d4, 1,
            "Terraria.RemoteClient", "get_PendingTermination");

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
        if (entry?.OpCode != OpCodes.Ldsfld || entry.Operand is not FieldReference f
            || f.FullName != "Terraria.RemoteClient[] Terraria.Netplay::Clients"
            || next == null || next.OpCode != (local == 0 ? OpCodes.Ldloc_0 : OpCodes.Ldloc_1)
            || !instructions.Any(i => i.Operand is MethodReference m
                && m.DeclaringType.FullName == declaringType && m.Name == callName)
            || instructions.Any(i => i.Operand is MethodReference m && m.Name == reserved.Name))
            throw new InvalidOperationException("Unsupported installed IL shape: " + name);
        var incoming = instructions.Where(i => ReferenceEquals(i.Operand, entry)
            || i.Operand is ILLabel label && ReferenceEquals(label.Target, entry)).ToArray();
        if (incoming.Length != 1) throw new InvalidOperationException("Unexpected loop edges: " + name);
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
