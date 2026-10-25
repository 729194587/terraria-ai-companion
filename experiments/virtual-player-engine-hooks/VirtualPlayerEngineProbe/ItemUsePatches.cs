using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;

namespace VirtualPlayerEngineProbe;

// Offsets are an additional structural check on the pinned engine, never a fallback.
public static class ItemUsePatches
{
    private static Instruction At(MethodDefinition m,int offset)=>m.Body.Instructions.SingleOrDefault(i=>i.Offset==offset);
    private static Instruction Target(Instruction i)=>i?.Operand is ILLabel l?l.Target:i?.Operand as Instruction;
    private static bool Field(Instruction i,string name)=>i?.Operand is FieldReference f&&f.Name==name;
    private static bool Call(Instruction i,string name)=>i?.Operand is MethodReference r&&r.Name==name;
    private static void Require(bool ok){if(!ok)throw new InvalidOperationException("Unsupported G6 ItemCheck IL; item use disabled.");}
    private static void Guard(MethodDefinition m,int start,int end,MethodReference predicate)
    {
        var entry=At(m,start);var target=At(m,end);var il=m.Body.GetILProcessor();var guard=Instruction.Create(OpCodes.Ldarg_0);
        foreach(var i in m.Body.Instructions.ToArray())
        {
            if(i.Operand==entry)i.Operand=guard;
            else if(i.Operand is ILLabel label&&label.Target==entry)label.Target=guard;
            else if(i.Operand is Instruction[] branches)for(int n=0;n<branches.Length;n++)if(branches[n]==entry)branches[n]=guard;
        }
        Require(!m.Body.ExceptionHandlers.Any(e=>e.TryStart==entry||e.HandlerStart==entry));
        il.InsertBefore(entry,guard);il.InsertBefore(entry,Instruction.Create(OpCodes.Call,predicate));
        il.InsertBefore(entry,Instruction.Create(OpCodes.Brtrue,target));
    }
    public static void Patch(MethodDefinition m,string operation,MethodReference predicate)
    {
        Require(!m.Body.Instructions.Any(i=>i.Operand is MethodReference r&&r.FullName==predicate.FullName));
        if(operation=="inner")
        {
            Require(m.Name.Contains("ItemCheck_Inner")&&m.Body.ExceptionHandlers.Count==0
                &&At(m,0x071d)?.OpCode==OpCodes.Ldarg_0&&Field(At(m,0x0723),"myPlayer")&&Target(At(m,0x0728))==At(m,0x0752)
                &&Call(At(m,0x074d),"AddBuff")&&At(m,0x072a)!=null
                &&At(m,0x166c)?.OpCode==OpCodes.Ldarg_0&&Field(At(m,0x1672),"myPlayer")&&Target(At(m,0x1677))==At(m,0x18c2)
                &&Call(At(m,0x1849),"ConsumeItem")&&At(m,0x167c)!=null
                &&At(m,0x187c)?.OpCode==OpCodes.Ldc_I4_1&&At(m,0x187d)?.OpCode==OpCodes.Stsfld&&Field(At(m,0x187d),"blockMouse")
                &&At(m,0x1882)!=null&&Field(At(m,0x18bd),"mouseItem"));
            Guard(m,0x071d,0x072a,predicate);Guard(m,0x166c,0x167c,predicate);
            Guard(m,0x187c,0x1882,predicate); // Last consumable must not change a human mouse flag.
        }
        else if(operation=="melee")
        {
            Require(m.Name.Contains("ItemCheck_OwnerOnlyCode")&&m.Body.ExceptionHandlers.Count==0
                &&At(m,0x017d)?.OpCode==OpCodes.Ldarg_0&&Field(At(m,0x0183),"myPlayer")&&Target(At(m,0x0188))==At(m,0x018b)
                &&At(m,0x018a)?.OpCode==OpCodes.Ret&&At(m,0x044d)?.OpCode==OpCodes.Ldarg_2
                &&Call(At(m,0x054f),"ItemCheck_MeleeHitNPCs")
                &&At(m,0x01a7)?.OpCode==OpCodes.Ldarg_0&&At(m,0x01e8)?.OpCode==OpCodes.Ldarg_0
                &&Field(At(m,0x01b6),"toolTime"));
            // Air-swing experiment: retain native melee; skip cursor-driven tools,
            // placement and accessory projectiles. No global tile target substitution.
            Guard(m,0x017d,0x01a7,predicate);
            Guard(m,0x01e8,0x044d,predicate);
        }
        else throw new ArgumentException(operation);
    }
}
