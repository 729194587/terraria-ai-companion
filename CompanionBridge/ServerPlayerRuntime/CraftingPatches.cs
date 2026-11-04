using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace VirtualPlayerEngineProbe;

// Applied to private DynamicMethodDefinition copies only, never shared engine methods.
internal static class CraftingPatches
{
    internal static void Patch(MethodDefinition m,string kind,MethodReference actor,FieldReference buffer,MethodReference consume)
    {
        void Require(bool ok){if(!ok)throw new InvalidOperationException("Unsupported pinned crafting IL: "+kind);}
        var code=m.Body.Instructions.ToArray();
        int players=0,locals=0,mouse=0;
        foreach(var i in code)
        {
            if(i.OpCode==OpCodes.Ldsfld&&i.Operand is FieldReference f&&f.FullName=="Terraria.Player[] Terraria.Main::player")
            {
                Require(i.Next?.OpCode==OpCodes.Ldsfld&&i.Next.Operand is FieldReference index&&index.FullName=="System.Int32 Terraria.Main::myPlayer"&&i.Next.Next?.OpCode==OpCodes.Ldelem_Ref);
                i.Next.OpCode=OpCodes.Nop;i.Next.Operand=null;i.Next.Next.OpCode=OpCodes.Nop;i.Next.Next.Operand=null;
                i.OpCode=OpCodes.Call;i.Operand=actor;players++;
            }
            if(i.Operand is MethodReference call&&call.FullName=="Terraria.Player Terraria.Main::get_LocalPlayer()")
            {i.Operand=actor;locals++;}
            if(kind=="craft"&&i.Operand is FieldReference field&&field.FullName=="Terraria.Item Terraria.Main::mouseItem")
            {Require(i.OpCode==OpCodes.Ldsfld||i.OpCode==OpCodes.Stsfld);i.Operand=buffer;mouse++;}
            if(kind=="craft"&&i.Operand is MethodReference create&&create.FullName=="System.Void Terraria.Recipe::Create()")
            {i.OpCode=OpCodes.Call;i.Operand=consume;}
        }
        if(kind=="craft")
        {
            Require(m.Name.Contains("CraftItem")&&players==4&&mouse==12);
            var ui=code.SingleOrDefault(i=>i.Offset==0x00fe);
            Require(ui?.OpCode==OpCodes.Ldc_I4_4&&code.Any(i=>i.Operand is MethodReference r&&r.Name=="OnCraft"));
            // Native prefix/consume/OnCraft/buffer assembly kept; popup/sound tail is UI only.
            ui.OpCode=OpCodes.Ret;ui.Operand=null;
        }
        else if(kind=="consume")
        {
            Require(m.Name.Contains("Create")&&players==18&&locals==1);
            var tail=code.SingleOrDefault(i=>i.Offset==0x0300);
            Require(tail?.OpCode==OpCodes.Ldarg_0&&code.Any(i=>i.Operand is MethodReference r&&r.Name=="ConsumeForCraft"));
            // Achievements and FindRecipes use client-global UI; no server UI refresh.
            tail.OpCode=OpCodes.Ret;tail.Operand=null;
        }
        else if(kind=="collect")Require(m.Name.Contains("CollectItemsToCraftWithFrom")&&locals==1&&players==0);
        else if(kind=="adjacency")
        {
            Require(m.Name.Contains("AdjTiles")&&players==0&&locals==0);
            var tail=code.SingleOrDefault(i=>i.Offset==0x03bc);
            Require(tail?.Operand is FieldReference f&&f.Name=="playerInventory");tail.OpCode=OpCodes.Ret;tail.Operand=null;
        }
        else if(kind=="condition")Require(players==0&&locals<=2);
        else throw new ArgumentException(kind);
        // Unknown player/global writes cannot silently enter a private native adapter.
        Require(!m.Body.Instructions.Any(i=>i.Operand is FieldReference f&&f.DeclaringType.FullName=="Terraria.Main"&&f.Name=="myPlayer"));
    }
}
