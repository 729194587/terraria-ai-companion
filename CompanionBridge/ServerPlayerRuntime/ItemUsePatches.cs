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
    public static void Patch(MethodDefinition m,string operation,MethodReference predicate,MethodReference miningDispatch=null,
        MethodReference miningOnly=null,MethodReference combatOnly=null)
    {
        Require(!m.Body.Instructions.Any(i=>i.Operand is MethodReference r&&r.FullName==predicate.FullName));
        if(operation=="combat-cooldown")
        {
            Require(m.Name.Contains("UpdateMeleeHitCooldowns")&&m.Body.ExceptionHandlers.Count==0
                &&Field(At(m,0),"myPlayer")&&Field(At(m,0x0006),"whoAmI")
                &&Target(At(m,0x000b))==At(m,0x002e)&&Field(At(m,0x0012),"meleeNPCHitCooldown"));
            Guard(m,0,0x000d,predicate);
        }
        else if(operation=="combat-strike")
        {
            var strike=At(m,0x0529);
            Require(m.Name.Contains("ProcessHitAgainstNPC")&&m.Body.ExceptionHandlers.Count==0
                &&Call(At(m,0x0501),"ModifyPlayerHitNPCWithItem")
                &&strike?.OpCode==OpCodes.Callvirt&&Call(strike,"StrikeNPC")
                &&At(m,0x052e)?.OpCode==OpCodes.Stloc_S&&Call(At(m,0x0537),"OnPlayerHitNPCWithItem")
                &&!m.Body.Instructions.Any(i=>Target(i)==strike));
            m.Body.GetILProcessor().InsertBefore(strike,Instruction.Create(OpCodes.Ldarg_0));
            strike.OpCode=OpCodes.Call;
            strike.Operand=predicate;
        }
        else if(operation=="combat-pose")
        {
            Require(m.Name.Contains("ItemCheck_ApplyUseStyle_Inner")&&m.Body.ExceptionHandlers.Count==0
                &&Field(At(m,0x0000),"dedServ")&&Target(At(m,0x0005))==At(m,0x0008)
                &&At(m,0x0007)?.OpCode==OpCodes.Ret&&Field(At(m,0x0009),"useStyle"));
            Guard(m,0x0000,0x0008,predicate);
        }
        else if(operation=="inner")
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
            if(miningOnly!=null)Require(Call(At(m,0x0543),"ItemCheck_CutTiles")&&At(m,0x0570)?.OpCode==OpCodes.Ret);
            // Air-swing experiment: retain native melee; skip cursor-driven tools,
            // placement and accessory projectiles. No global tile target substitution.
            Guard(m,0x017d,0x01a7,predicate);
            Guard(m,0x01e8,0x044d,miningDispatch??predicate);
            if(miningOnly!=null)Guard(m,0x044d,0x0570,miningOnly); // G7 cannot cut neighboring tiles or attack.
            if(combatOnly!=null)
            {
                Require(Call(At(m,0x0511),"ItemCheck_CutTiles")&&Call(At(m,0x0543),"ItemCheck_CutTiles")
                    &&Call(At(m,0x055b),"ItemCheck_MeleeHitPVP")&&Call(At(m,0x056b),"ItemCheck_EmitHammushProjectiles"));
                Guard(m,0x04e8,0x0516,combatOnly);
                Guard(m,0x0534,0x0548,combatOnly);
                Guard(m,0x0554,0x0570,combatOnly);
            }
        }
        else throw new ArgumentException(operation);
    }
}
