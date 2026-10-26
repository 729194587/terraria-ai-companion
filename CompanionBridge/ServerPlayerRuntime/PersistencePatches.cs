using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;

namespace VirtualPlayerEngineProbe;

public static class PersistencePatches
{
    public static void SavePlayer(MethodDefinition method, MethodReference ownsFile)
    {
        var body = method.Body;
        var code = body.Instructions;
        Instruction At(int offset) => code.SingleOrDefault(i => i.Offset == offset);
        Instruction Target(int offset) => At(offset)?.Operand is ILLabel label ? label.Target : At(offset)?.Operand as Instruction;
        bool Call(int offset, string type, string name) => (At(offset)?.OpCode == OpCodes.Call || At(offset)?.OpCode == OpCodes.Callvirt)
            && At(offset)?.Operand is MethodReference m
            && m.DeclaringType.FullName == type && m.Name == name;
        if (!method.Name.Contains("SavePlayer", StringComparison.Ordinal) || !method.IsStatic
            || method.Parameters.Count != 2 || method.Parameters[0].ParameterType.FullName != "Terraria.IO.PlayerFileData"
            || method.Parameters[1].ParameterType.MetadataType != MetadataType.Boolean
            || body.ExceptionHandlers.Count != 1 || body.ExceptionHandlers[0].HandlerType != ExceptionHandlerType.Catch
            || body.ExceptionHandlers[0].TryStart != At(0xd) || body.ExceptionHandlers[0].HandlerStart != At(0x41)
            || body.ExceptionHandlers[0].TryEnd != At(0x41) || body.ExceptionHandlers[0].HandlerEnd != At(0xa2)
            || body.ExceptionHandlers[0].CatchType?.FullName != "System.Exception"
            || !Call(0xd,"Terraria.Main","get_Achievements") || !Call(0x12,"Terraria.Achievements.AchievementManager","Save")
            || !Call(0x22,"Terraria.Player","InternalSaveMap") || At(0x27)?.OpCode != OpCodes.Ldsfld
            || At(0x27)?.Operand is not FieldReference field || field.FullName != "System.Boolean Terraria.Main::ServerSideCharacter"
            || At(0x2c)?.OpCode != OpCodes.Brtrue_S || Target(0x2c) != At(0x3f)
            || !Call(0x3a,"Terraria.Utilities.FileUtilities","ProtectedInvoke")
            || At(0x41)?.OpCode != OpCodes.Stloc_1 || !Call(0x4e,"Terraria.UI.FancyErrorPrinter","ShowFileSavingFailError")
            || !Call(0x5b,"Terraria.WorldGen","SaveAndQuit") || At(0xa0)?.OpCode != OpCodes.Rethrow
            || code.Any(i => i.Operand is MethodReference m && m.Name == ownsFile.Name))
            throw new InvalidOperationException("Unsupported native SavePlayer error/global-effects IL; character IO disabled.");
        var il = body.GetILProcessor();
        var entry = At(0xd); var globalsSkipped = At(0x27); var originalCatch = At(0x42);
        var guard = Instruction.Create(OpCodes.Ldarg_0);
        il.InsertBefore(entry, guard);
        il.InsertBefore(entry, Instruction.Create(OpCodes.Call, ownsFile));
        il.InsertBefore(entry, Instruction.Create(OpCodes.Brtrue, globalsSkipped));
        body.ExceptionHandlers[0].TryStart = guard;
        il.InsertBefore(originalCatch, Instruction.Create(OpCodes.Ldarg_0));
        il.InsertBefore(originalCatch, Instruction.Create(OpCodes.Call, ownsFile));
        il.InsertBefore(originalCatch, Instruction.Create(OpCodes.Brfalse, originalCatch));
        il.InsertBefore(originalCatch, Instruction.Create(OpCodes.Rethrow));
    }
}
