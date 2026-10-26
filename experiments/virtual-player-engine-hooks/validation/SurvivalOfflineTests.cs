using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;
using VirtualPlayerEngineProbe;

public static class SurvivalOfflineTests
{
    private static int checks;
    public static bool Owns(object value)=>true;
    public static int Slot()=>254;
    private static void Check(bool condition,string label){if(!condition)throw new Exception(label);checks++;Console.WriteLine("PASS survival: "+label);}
    private static MethodDefinition Method(AssemblyDefinition a,string op)
    {
        var player=a.MainModule.Types.Single(t=>t.FullName=="Terraria.Player");
        return op switch {
            "pickup"=>player.Methods.Single(m=>m.Name=="GrabItems"),
            "lethal"=>player.Methods.Single(m=>m.Name=="Hurt"&&m.Parameters.Count==2),
            "dodge"=>player.Methods.Single(m=>m.Name=="Hurt"&&m.Parameters.Count==11),
            "death-drops"=>player.Methods.Single(m=>m.Name=="KillMe"),
            "spawn"=>player.Methods.Single(m=>m.Name=="Spawn"),
            _=>player.Methods.Single(m=>m.Name=="DropCoins")
        };
    }
    public static void Run(string path)
    {
        string dll=Path.Combine(path,"tModLoader.dll");
        var runtime=AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
        foreach(var op in new[]{"pickup","lethal","dodge","death-drops","spawn","coin-mouse"})
        {
            using var a=AssemblyDefinition.ReadAssembly(dll);var m=Method(a,op);
            var owns=a.MainModule.ImportReference(typeof(SurvivalOfflineTests).GetMethod(nameof(Owns)));
            var old=m.Body.Instructions.ToArray();var before=old.Select(i=>(i.OpCode,i.Operand)).ToArray();
            SurvivalPatches.Patch(m,op,owns);
            Check(m.Body.Instructions.Count>old.Length&&old.Select((i,n)=>i.OpCode==before[n].OpCode).All(v=>v),"scoped patch preserves original human instruction opcodes: "+op);
            Check(!m.Body.Instructions.Any(i=>i.OpCode==OpCodes.Stsfld&&i.Operand is FieldReference f&&f.Name=="myPlayer"),"patch never changes global identity: "+op);
            int count=m.Body.Instructions.Count;bool rejected=false;
            try{SurvivalPatches.Patch(m,op,owns);}catch(InvalidOperationException){rejected=true;}
            Check(rejected&&count==m.Body.Instructions.Count,"duplicate safely rejected before mutation: "+op);
            foreach(var kind in new[]{"boundary","opcode"})
            {
                using var bad=AssemblyDefinition.ReadAssembly(dll);var method=Method(bad,op);
                int offset=op switch{"pickup"=>0xa5,"lethal"=>0x984,"dodge"=>0x1a1,"death-drops"=>0x127,"spawn"=>0,"coin-mouse"=>0x1dc,_=>0};
                var target=method.Body.Instructions.Single(i=>i.Offset==offset);
                if(kind=="boundary")target.Offset=0x7ffff;else target.OpCode=OpCodes.Nop;
                int original=method.Body.Instructions.Count;rejected=false;
                try{SurvivalPatches.Patch(method,op,owns);}catch(InvalidOperationException){rejected=true;}
                Check(rejected&&method.Body.Instructions.Count==original,"unsupported shape rejected without partial patch: "+op+"/"+kind);
            }
            var actual=runtime.GetType("Terraria.Player").GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)
                .Single(info=>info.Name==m.Name&&info.GetParameters().Length==m.Parameters.Count);
            using var dmd=new DynamicMethodDefinition(actual);
            using var il=new ILContext(dmd.Definition);
            il.Invoke(c=>SurvivalPatches.Patch(c.Method,op,c.Module.ImportReference(typeof(SurvivalOfflineTests).GetMethod(nameof(Owns)))));
            Check(true,"actual DMD/ILContext labels and signatures supported: "+op);
        }
        using(var a=AssemblyDefinition.ReadAssembly(dll))
        {
            var m=Method(a,"pickup");var entry=m.Body.Instructions.Single(i=>i.Offset==0x9f);
            var allowed=m.Body.Instructions.Single(i=>i.Offset==0xaa);var skipped=m.Body.Instructions.Single(i=>i.Offset==0x2a8);
            SurvivalPatches.Patch(m,"pickup",a.MainModule.ImportReference(typeof(SurvivalOfflineTests).GetMethod(nameof(Owns))));
            bool Gate(object actor,object selected,int id,int local)
            {
                var pc=entry.Previous.Previous.Previous;var stack=new Stack<object>();
                while(pc!=allowed&&pc!=skipped)
                {
                    var next=pc.Next;
                    if(pc.OpCode==OpCodes.Ldarg_0)stack.Push(actor);
                    else if(pc.OpCode==OpCodes.Ldarg_1)stack.Push(id);
                    else if(pc.OpCode==OpCodes.Ldsfld)stack.Push(local);
                    else if(pc.OpCode==OpCodes.Call)stack.Push(ReferenceEquals(stack.Pop(),selected));
                    else if(pc.OpCode==OpCodes.Brtrue){if((bool)stack.Pop())next=(Instruction)pc.Operand;}
                    else if(pc.OpCode==OpCodes.Bne_Un){var b=stack.Pop();var v=stack.Pop();if(!Equals(v,b))next=(Instruction)pc.Operand;}
                    else throw new Exception("Unexpected gate instruction "+pc);
                    pc=next;
                }
                return pc==allowed;
            }
            var ai=new object();var human=new object();
            Check(Gate(ai,ai,254,0),"only exact scoped AI reference gains pickup authority");
            Check(Gate(human,ai,0,0),"local human pickup condition still passes unchanged");
            Check(!Gate(human,ai,1,0)&&!Gate(ai,new object(),254,0),"unowned remote/reference replacement gains no pickup authority");
        }
        using(var a=AssemblyDefinition.ReadAssembly(dll))
        {
            var m=a.MainModule.Types.Single(t=>t.FullName=="Terraria.Projectile").Methods.Single(m=>m.Name=="Damage");
            var original=m.Body.Instructions.ToArray();
            SurvivalPatches.HostileClone(m,a.MainModule.ImportReference(typeof(SurvivalOfflineTests).GetMethod(nameof(Slot))));
            Check(m.Body.Instructions.All(i=>i.Offset<0x6c0||i.Offset>=0x4e43),"private hostile clone excludes all native NPC/friendly/PvP middle paths");
            Check(!m.Body.Instructions.Any(i=>i.Operand is FieldReference f&&f.Name=="myPlayer"),"private hostile clone uses explicit validated actor slot, never global identity");
            Check(m.Body.Instructions.Any(i=>i.Operand is MethodReference r&&r.Name=="CanDamage")
                &&m.Body.Instructions.Any(i=>i.Operand is MethodReference r&&r.Name=="Colliding")
                &&m.Body.Instructions.Any(i=>i.Operand is MethodReference r&&r.Name=="Hurt")
                &&m.Body.Instructions.Any(i=>i.Operand is MethodReference r&&r.Name=="StatusPlayer"),"native damage gate, hitbox, damage calculation, Hurt and status hooks retained");
        }
        foreach(var mutation in new[]{0x6ba,0x4e53,0x5098})
        {
            using var a=AssemblyDefinition.ReadAssembly(dll);
            var m=a.MainModule.Types.Single(t=>t.FullName=="Terraria.Projectile").Methods.Single(m=>m.Name=="Damage");
            m.Body.Instructions.Single(i=>i.Offset==mutation).Operand=null;
            int count=m.Body.Instructions.Count;bool rejected=false;
            try{SurvivalPatches.HostileClone(m,a.MainModule.ImportReference(typeof(SurvivalOfflineTests).GetMethod(nameof(Slot))));}catch(InvalidOperationException){rejected=true;}
            Check(rejected&&m.Body.Instructions.Count==count,"malformed hostile slice fails without mutation: "+mutation);
        }
        Console.WriteLine($"Survival offline checks passed: {checks}; native API and live client validation are separate.");
    }
}
