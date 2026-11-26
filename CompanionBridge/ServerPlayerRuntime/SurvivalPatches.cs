using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;

namespace VirtualPlayerEngineProbe;

// Fixed installed engine only. Validate each whole plan before changing its IL.
public static class SurvivalPatches
{
    private static Instruction At(MethodDefinition m,int offset)=>m.Body.Instructions.SingleOrDefault(i=>i.Offset==offset);
    private static Instruction Target(Instruction i)=>i?.Operand is ILLabel label?label.Target:i?.Operand as Instruction;
    private static bool Call(Instruction i,string name)=>(i?.OpCode==OpCodes.Call||i?.OpCode==OpCodes.Callvirt)&&i.Operand is MethodReference method && method.Name==name;
    private static bool NotEqual(Instruction i)=>i?.OpCode==OpCodes.Bne_Un||i?.OpCode==OpCodes.Bne_Un_S;
    private static bool Local(Instruction i)=>i?.OpCode==OpCodes.Ldsfld&&i.Operand is FieldReference f&&f.FullName=="System.Int32 Terraria.Main::myPlayer";
    private static void Require(bool valid,string name) { if(!valid)throw new InvalidOperationException("Unsupported native survival IL: "+name); }
    private static void Guard(MethodDefinition m,int entryOffset,int targetOffset,MethodReference owns)
    {
        Guard(m, At(m, entryOffset), At(m, targetOffset), owns);
    }
    private static void Guard(MethodDefinition m, Instruction entry, Instruction target, MethodReference owns)
    {
        var il=m.Body.GetILProcessor();
        var guard=Instruction.Create(OpCodes.Ldarg_0);
        foreach(var i in m.Body.Instructions.ToArray())
        {
            if(i.Operand==entry)i.Operand=guard;
            else if(i.Operand is ILLabel label&&label.Target==entry)label.Target=guard;
            else if(i.Operand is Instruction[] branches)for(int n=0;n<branches.Length;n++)if(branches[n]==entry)branches[n]=guard;
        }
        foreach(var e in m.Body.ExceptionHandlers)if(e.TryStart==entry)e.TryStart=guard;
        il.InsertBefore(entry,guard);il.InsertBefore(entry,Instruction.Create(OpCodes.Call,owns));
        il.InsertBefore(entry,Instruction.Create(OpCodes.Brtrue,target));
    }
    internal static void PickupFilter(MethodDefinition m, MethodReference allows)
    {
        var entry = At(m, 0x1a);
        var skip = At(m, 0x2a8);
        Require(entry?.OpCode == OpCodes.Ldloc_1 && skip != null && At(m, 0x15)?.OpCode.Code == Code.Brfalse, "pickup filter");
        var il = m.Body.GetILProcessor();
        il.InsertBefore(entry, Instruction.Create(OpCodes.Ldarg_0));
        il.InsertBefore(entry, Instruction.Create(OpCodes.Ldloc_1));
        il.InsertBefore(entry, Instruction.Create(OpCodes.Call, allows));
        il.InsertBefore(entry, Instruction.Create(OpCodes.Brfalse, skip));
    }

    // Enable just the native countdown or expired-slot sweep for the bound actor.
    // All other local-player branches in Player.Update/UpdateBuffs remain native.
    internal static void BuffClock(MethodDefinition m, MethodReference owns, bool countdown)
    {
        var guards = m.Body.Instructions.Where(i => NotEqual(i) && Local(i.Previous)
            && i.Previous.Previous?.Operand is FieldReference f && f.FullName == "System.Int32 Terraria.Entity::whoAmI"
            && i.Previous.Previous.Previous?.OpCode == OpCodes.Ldarg_0);
        bool Matches(Instruction branch)
        {
            if (countdown)
                return branch.Next?.Operand is FieldReference f && f.Name == "TimeLeftDoesNotDecrease"
                    && Target(branch)?.Previous?.OpCode == OpCodes.Stind_I4;
            if (branch.Next?.OpCode != OpCodes.Ldc_I4_0 || Target(branch) is not {} end) return false;
            var block = m.Body.Instructions.Skip(m.Body.Instructions.IndexOf(branch) + 1)
                .Take(36).TakeWhile(i => i != end).ToArray();
            return block.LastOrDefault()?.Next == end && block.Any(i => Call(i,"DelBuff"))
                && block.Any(i => i.Operand is FieldReference field && field.Name == "buffTime")
                && block.Any(i => Call(i,"get_maxBuffs"));
        }
        var gate = guards.Single(Matches);
        Guard(m, gate.Previous.Previous.Previous, gate.Next, owns);
    }

    public static void Patch(MethodDefinition m,string operation,MethodReference owns)
    {
        Require(!m.Body.Instructions.Any(i=>i.Operand is MethodReference r&&r.FullName==owns.FullName),operation+" duplicate");
        if(operation=="pickup")
        {
            Require(m.Name.Contains("GrabItems")&&At(m,0x9f)?.OpCode==OpCodes.Ldarg_1&&Local(At(m,0xa0))&&NotEqual(At(m,0xa5))&&Target(At(m,0xa5))==At(m,0x2a8)
                &&Call(At(m,0x116),"PickupItem")&&At(m,0xaa)!=null,operation);
            Guard(m,0x9f,0xaa,owns);
        }
        else if(operation=="breath")
        {
            var read=m.Body.Instructions.SingleOrDefault(Local);
            var branch=read?.Next?.Next?.Next;
            Require(m.Name.Contains("CheckDrowning")&&read?.Next?.OpCode==OpCodes.Ldarg_0
                &&read.Next.Next.Operand is FieldReference field&&field.FullName=="System.Int32 Terraria.Entity::whoAmI"
                &&NotEqual(branch)&&Target(branch)!=null&&m.Body.Instructions.Any(i=>Call(i,"get_breathCDMax")),operation);
            Guard(m,read,branch.Next,owns);
        }
        else if(operation=="lethal")
        {
            Require(m.Name.Contains("Hurt")&&Local(At(m,0x97f))&&NotEqual(At(m,0x984))&&Target(At(m,0x984))==At(m,0x991)&&Call(At(m,0x98c),"KillMe")&&At(m,0x979)?.OpCode==OpCodes.Ldarg_0,operation);
            Guard(m,0x979,0x986,owns);
        }
        else if(operation=="dodge")
        {
            // Other IL adapters can move these blocks. Identify the guarded call in its
            // branch region, then bypass only the validated local-player comparison.
            string[] names = ["FreeDodge", "NinjaDodge", "BrainOfConfusionDodge", "ShadowDodge", "ConsumableDodge"];
            var instructions = m.Body.Instructions;
            var guards = instructions.Where(i => NotEqual(i) && Local(i.Previous)
                && i.Previous.Previous?.Operand is FieldReference field && field.Name == "whoAmI"
                && i.Previous.Previous.OpCode == OpCodes.Ldfld && i.Previous.Previous.Previous?.OpCode == OpCodes.Ldarg_0
                && Target(i) is {} end && instructions.IndexOf(end) > instructions.IndexOf(i)).ToArray();
            var plan = names.Select(name => guards.Where(branch =>
            {
                for (var i = branch.Next; i != null && i != Target(branch); i = i.Next)
                    if (Call(i, name)) return true;
                return false;
            }).ToArray()).ToArray();
            Require(m.Name.Contains("Hurt") && plan.All(matches => matches.Length == 1)
                && plan.Select(matches => matches[0]).Distinct().Count() == names.Length, operation);
            foreach (var matches in plan)
            {
                var branch = matches[0];
                Guard(m, branch.Previous.Previous.Previous, branch.Next, owns);
            }
        }
        else if(operation=="death-drops")
        {
            Require(m.Name.Contains("KillMe")&&NotEqual(At(m,0x127))&&NotEqual(At(m,0x9f4))&&Local(At(m,0x11c))&&Local(At(m,0x9ef))&&Target(At(m,0x127))==At(m,0x324)&&Target(At(m,0x9f4))==At(m,0xa2d)
                &&Call(At(m,0x308),"DropItems")&&Call(At(m,0xa0c),"DropCoins")&&Call(At(m,0xa46),"saveToonWhilePlaying"),operation);
            Guard(m,0x11c,0x12c,owns);Guard(m,0x9e9,0x9f6,owns);
        }
        else if(operation=="spawn")
        {
            Require(Call(At(m,0),"get_LocalPlayer")&&At(m,6)?.Operand is FieldReference f&&f.Name=="creativeInterface"
                &&At(m,0xb)?.OpCode==OpCodes.Ldarg_0,operation);
            Guard(m,0,0xb,owns); // Do not touch Main.player[Main.myPlayer].creativeInterface.
        }
        else if(operation=="coin-mouse")
        {
            Require(At(m,0x1dc)?.OpCode==OpCodes.Ldloc_2&&At(m,0x1ee)?.Operand is FieldReference f&&f.Name=="mouseItem"
                &&At(m,0x1f3)?.OpCode==OpCodes.Ldloc_2,operation);
            Guard(m,0x1dc,0x1f3,owns);
        }
        else throw new ArgumentException(operation);
    }

    // Private DMD clone: retain the exact native CanDamage prefix and hostile
    // player-damage tail, excluding NPC/PvP/friendly damage. Never patch the
    // public projectile method or any global identity/netMode value.
    public static void HostileClone(MethodDefinition m,MethodReference slot)
    {
        var code=m.Body.Instructions;
        Require(Call(At(m,0x6a7),"CanDamage")&&Call(At(m,0x6ba),"Damage_GetHitbox")
            &&At(m,0x6bf)?.OpCode==OpCodes.Stloc_1&&At(m,0x4e43)?.OpCode==OpCodes.Ldsfld
            &&Call(At(m,0x5098),"Hurt")&&m.Body.ExceptionHandlers.Count==0&&code.Last().Offset==0x515f,"hostile clone");
        var removed=code.Where(i=>i.Offset>=0x6c0&&i.Offset<0x4e43).ToHashSet();
        var retained=code.Where(i=>!removed.Contains(i)).ToArray();
        Require(retained.All(i=>!(Target(i) is {} t)||!removed.Contains(t))
            &&retained.All(i=>i.Operand is not Instruction[] targets||targets.All(t=>!removed.Contains(t))),"hostile branch boundary");
        var identity=retained.Where(i=>i.Operand is FieldReference f&&f.DeclaringType.FullName=="Terraria.Main"&&f.Name=="myPlayer").ToArray();
        Require(identity.Length==2&&identity[0].Offset==0x4e53&&identity[1].Offset==0x4ed6,"hostile identity reads");
        foreach(var i in removed)code.Remove(i);
        At(m,0x4e43).OpCode=OpCodes.Ldc_I4_0;At(m,0x4e43).Operand=null; // Private clone enters its existing hostile branch.
        foreach(var i in identity){i.OpCode=OpCodes.Call;i.Operand=slot;}
        m.Body.InitLocals=true;
    }
}
