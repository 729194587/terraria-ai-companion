using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;

namespace VirtualPlayerEngineProbe;

public static class ItemOwnershipPatches
{
    private static Instruction At(MethodDefinition m,int offset)=>m.Body.Instructions.SingleOrDefault(i=>i.Offset==offset);
    private static Instruction Target(Instruction i)=>i?.Operand is ILLabel l?l.Target:i?.Operand as Instruction;
    private static bool Field(Instruction i,string name)=>i?.OpCode==OpCodes.Ldfld&&i.Operand is FieldReference f&&f.Name==name;
    private static bool Call(Instruction i,string name)=>(i?.OpCode==OpCodes.Call||i?.OpCode==OpCodes.Callvirt)&&i.Operand is MethodReference r&&r.Name==name;
    private static void Require(bool valid){if(!valid)throw new InvalidOperationException("Unsupported native item ownership IL; no pickup fix installed.");}
    public static void Patch(MethodDefinition m,bool server,MethodReference predicate)
    {
        Require(m.Body.ExceptionHandlers.Count==0&&predicate.Parameters.Count==1&&predicate.Parameters[0].ParameterType.MetadataType==MetadataType.Int32&&predicate.ReturnType.MetadataType==MetadataType.Boolean&&!predicate.HasThis);
        Require(!m.Body.Instructions.Any(i=>i.Operand is MethodReference r&&r.FullName==predicate.FullName));
        int entryOffset=server?0x0121:0x017c,targetOffset=server?0x0143:0x01b2;
        var entry=At(m,entryOffset);var target=At(m,targetOffset);
        VariableDefinition local;
        if(server)
        {
            Require(m.Name.Contains("UpdateServer")&&entry?.OpCode==OpCodes.Ldloc_S&&entry.Operand is VariableDefinition
                &&Field(At(m,0x0123),"playerIndexTheItemIsReservedFor")&&At(m,0x012d)?.OpCode==OpCodes.Beq_S&&Target(At(m,0x012d))==target
                &&Field(At(m,0x013c),"active")&&At(m,0x0141)?.OpCode==OpCodes.Brtrue_S&&Target(At(m,0x0141))==At(m,0x014c)&&Call(At(m,0x0147),"FindOwner"));
            local=(VariableDefinition)entry.Operand;
        }
        else
        {
            Require(m.Name.Contains("FindOwner")&&entry?.OpCode==OpCodes.Ldloc_0&&At(m,0x017d)?.OpCode==OpCodes.Ldsfld&&At(m,0x017d)?.Operand is FieldReference f&&f.FullName=="System.Int32 Terraria.Main::myPlayer"
                &&At(m,0x017a)?.OpCode==OpCodes.Beq_S&&Target(At(m,0x017a))==At(m,0x01f4)&&Field(At(m,0x0027),"playerIndexTheItemIsReservedFor")
                &&At(m,0x002c)?.OpCode==OpCodes.Stloc_0&&At(m,0x01b2)?.OpCode==OpCodes.Ldc_I4_S
                &&Convert.ToInt32(At(m,0x01b2).Operand)==21&&Call(At(m,0x01ca),"SendData")
                &&At(m,0x01d7)?.OpCode==OpCodes.Ldc_I4_S&&Convert.ToInt32(At(m,0x01d7)?.Operand)==22&&Call(At(m,0x01ef),"SendData"));
            local=m.Body.Variables[0];
        }
        Require(target!=null&&!m.Body.ExceptionHandlers.Any(e=>e.TryStart==entry||e.HandlerStart==entry));
        var il=m.Body.GetILProcessor();var guard=Instruction.Create(OpCodes.Ldloc,local);
        foreach(var i in m.Body.Instructions.ToArray())
        {
            if(i.Operand==entry)i.Operand=guard;
            else if(i.Operand is ILLabel l&&l.Target==entry)l.Target=guard;
            else if(i.Operand is Instruction[] branches)for(int n=0;n<branches.Length;n++)if(branches[n]==entry)branches[n]=guard;
        }
        il.InsertBefore(entry,guard);
        if(server)il.InsertBefore(entry,Instruction.Create(OpCodes.Ldfld,(FieldReference)At(m,0x0123).Operand));
        il.InsertBefore(entry,Instruction.Create(OpCodes.Call,predicate));il.InsertBefore(entry,Instruction.Create(OpCodes.Brtrue,target));
    }
}
