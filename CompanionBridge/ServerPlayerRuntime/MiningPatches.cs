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
    private static bool ClientOnly(MethodDefinition m,int offset,int resume)
    {
        var load=At(m,offset);
        return load?.OpCode==OpCodes.Ldsfld&&load.Operand is FieldReference f&&f.Name=="netMode"
            &&load.Next?.OpCode==OpCodes.Ldc_I4_1
            &&load.Next.Next?.OpCode.Code is Code.Bne_Un or Code.Bne_Un_S
            &&ReferenceEquals(load.Next.Next.Operand,At(m,resume));
    }
    private static void Require(bool ok){if(!ok)throw new InvalidOperationException("Unsupported G7 mining IL; installation refused.");}
    public static void Validate(TypeDefinition player)
    {
        MethodDefinition Method(string name)=>player.Methods.Single(m=>m.Name==name);
        var tool=Method("ItemCheck_UseMiningTools_ActuallyUseMiningTool");var pick=Method("PickTile");
        var range=Method("IsTargetTileInItemRange");var outer=Method("ItemCheck_UseMiningTools");
        var transform=Method("DoesPickTargetTransformOnKill");
        var kill=player.Module.Types.Single(t=>t.FullName=="Terraria.WorldGen").Methods.Single(m=>m.Name=="KillTile");
        Require(tool.Parameters.Count==4&&tool.Parameters[1].ParameterType.IsByReference&&tool.Body.ExceptionHandlers.Count==0
            &&Call(tool,0x0473,"PickTile")&&Call(tool,0x0493,"ApplyItemTime")&&Call(tool,0x049f,"ItemCheck_UseMiningTools_TryPoundingTile")
            &&Call(outer,0x0060,"IsTargetTileInItemRange")&&Call(outer,0x0152,tool.Name)
            &&At(outer,0x010c)?.Operand is FieldReference f&&f.Name=="toolTime"
            &&Call(pick,0x0032,"GetPickaxeDamage")&&Call(pick,0x003a,"CanKillTile")
            &&Call(pick,0x005b,"DoesPickTargetTransformOnKill")&&Call(pick,0x006d,"AddDamage")
            &&Call(pick,0x02c3,"KillTile")&&Call(pick,0x031c,"KillTile")&&Call(pick,0x0361,"Prune")
            &&At(pick,0x0072)?.OpCode==OpCodes.Ldc_I4_S&&Convert.ToInt32(At(pick,0x0072).Operand)==100
            &&Call(pick,0x030a,"SendData")&&Call(pick,0x033b,"SendData")&&Call(pick,0x0353,"SendData"));
        // The same explicit-coordinate native tool entry owns the axe branch.
        // Keep protection, accumulated damage and native tree destruction intact.
        Require(At(tool,0x01f4)?.Operand is FieldReference axe&&axe.Name=="tileAxe"
            &&Call(tool,0x028a,"CanKillTile")&&Call(tool,0x02e9,"AddDamage")
            &&At(tool,0x02ee)?.OpCode==OpCodes.Ldc_I4_S&&Convert.ToInt32(At(tool,0x02ee).Operand)==100
            &&Call(tool,0x03ba,"IsBottomOfTreeTrunkNoRoots")&&Call(tool,0x03c7,"KillTile")
            &&Call(tool,0x040f,"KillTile")&&Call(tool,0x043d,"Prune")&&Call(tool,0x0453,"ApplyItemTime"));
        // Server breaks tiles directly. These client requests are not needed
        // alongside our observed SyncTilePicking/TileSquare publication.
        Require(ClientOnly(tool,0x0178,0x01c5)&&ClientOnly(tool,0x01a5,0x01c5)
            &&ClientOnly(tool,0x03cc,0x03ec)&&ClientOnly(tool,0x0414,0x0434)
            &&ClientOnly(pick,0x0088,0x02a8)&&ClientOnly(pick,0x02f0,0x030f)&&ClientOnly(pick,0x0321,0x0358));
        Require(transform.Parameters.Count==7&&transform.Body.ExceptionHandlers.Count==0
            &&Call(transform,0x0005,"AddDamage")&&At(transform,0x0004)?.OpCode==OpCodes.Ldc_I4_0
            &&At(transform,0x0019)?.OpCode==OpCodes.Ldc_I4_2&&At(transform,0x00d2)?.OpCode==OpCodes.Ldc_I4_1
            &&At(kill,0x0623)?.OpCode==OpCodes.Ldarg_2&&At(kill,0x0650)?.OpCode==OpCodes.Ldc_I4_2
            &&At(kill,0x0651)?.Operand is Instruction grass&&grass==At(kill,0x0698)
            &&At(kill,0x069f)?.OpCode==OpCodes.Ldc_I4_0&&At(kill,0x06a0)?.OpCode==OpCodes.Stind_I2
            &&Call(kill,0x0730,"SquareTileFrame"));
        Require(range.Body.Instructions.All(i=>i.OpCode!=OpCodes.Stfld&&i.OpCode!=OpCodes.Stsfld&&i.OpCode!=OpCodes.Call&&i.OpCode!=OpCodes.Callvirt)
            &&range.Body.ExceptionHandlers.Count==0&&range.Body.Instructions.Count==77
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
