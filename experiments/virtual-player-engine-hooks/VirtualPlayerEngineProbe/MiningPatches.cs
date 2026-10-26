using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;

namespace VirtualPlayerEngineProbe;

// Pinned-engine checks, shared by runtime installation and offline mutation tests.
public static class MiningPatches
{
    private static Instruction At(MethodDefinition m,int offset)=>m.Body.Instructions.SingleOrDefault(i=>i.Offset==offset);
    private static bool Call(MethodDefinition m,int offset,string name)=>At(m,offset)?.Operand is MethodReference r&&r.Name==name;
    private static void Require(bool ok){if(!ok)throw new InvalidOperationException("Unsupported G7 mining IL; installation refused.");}
    public static void Validate(TypeDefinition player)
    {
        MethodDefinition Method(string name)=>player.Methods.Single(m=>m.Name==name);
        var tool=Method("ItemCheck_UseMiningTools_ActuallyUseMiningTool");var pick=Method("PickTile");
        var range=Method("IsTargetTileInItemRange");var outer=Method("ItemCheck_UseMiningTools");
        Require(tool.Parameters.Count==4&&tool.Parameters[1].ParameterType.IsByReference&&tool.Body.ExceptionHandlers.Count==0
            &&Call(tool,0x0473,"PickTile")&&Call(tool,0x0493,"ApplyItemTime")&&Call(tool,0x049f,"ItemCheck_UseMiningTools_TryPoundingTile")
            &&Call(outer,0x0060,"IsTargetTileInItemRange")&&Call(outer,0x0152,tool.Name)
            &&At(outer,0x010c)?.Operand is FieldReference f&&f.Name=="toolTime"
            &&Call(pick,0x0032,"GetPickaxeDamage")&&Call(pick,0x003a,"CanKillTile")
            &&Call(pick,0x005b,"DoesPickTargetTransformOnKill")&&Call(pick,0x006d,"AddDamage")
            &&Call(pick,0x02c3,"KillTile")&&Call(pick,0x031c,"KillTile")&&Call(pick,0x0361,"Prune")
            &&At(pick,0x0072)?.OpCode==OpCodes.Ldc_I4_S&&Convert.ToInt32(At(pick,0x0072).Operand)==100
            &&Call(pick,0x030a,"SendData")&&Call(pick,0x033b,"SendData")&&Call(pick,0x0353,"SendData"));
        Require(range.Body.ExceptionHandlers.Count==0&&range.Body.Instructions.Count==77
            &&range.Body.Instructions.Count(i=>i.Operand is FieldReference r&&r.Name=="tileTargetX")==2
            &&range.Body.Instructions.Count(i=>i.Operand is FieldReference r&&r.Name=="tileTargetY")==2);
    }
    // Clone only: reads a checked private intent scope, never the human cursor.
    public static void RangeClone(MethodDefinition m,MethodReference getX,MethodReference getY)
    {
        Require(!m.HasThis&&m.Parameters.Count==2&&m.Parameters[0].ParameterType.FullName=="Terraria.Player"
            &&m.Parameters[1].ParameterType.FullName=="Terraria.Item"&&m.Body.ExceptionHandlers.Count==0
            &&m.Body.Instructions.Count(i=>i.Operand is FieldReference f&&f.Name=="tileTargetX")==2
            &&m.Body.Instructions.Count(i=>i.Operand is FieldReference f&&f.Name=="tileTargetY")==2);
        foreach(var i in m.Body.Instructions)
            if(i.OpCode==OpCodes.Ldsfld&&i.Operand is FieldReference f&&f.DeclaringType.FullName=="Terraria.Player"&&f.Name is "tileTargetX" or "tileTargetY")
            {i.OpCode=OpCodes.Call;i.Operand=f.Name=="tileTargetX"?getX:getY;}
    }
}
