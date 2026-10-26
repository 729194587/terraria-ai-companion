using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;
using VirtualPlayerEngineProbe;

public static class ItemUseOfflineTests
{
    public static bool Allowed(object p)=>false;
    private static int checks;
    private static void Check(bool ok,string text){if(!ok)throw new Exception(text);checks++;Console.WriteLine("PASS G6: "+text);}
    public static void Run(string path)
    {
        string dll=Path.Combine(path,"tModLoader.dll");var runtime=AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
        foreach(var op in new[]{"inner","melee"})
        {
            string name=op=="inner"?"ItemCheck_Inner":"ItemCheck_OwnerOnlyCode";
            using var a=AssemblyDefinition.ReadAssembly(dll);var m=a.MainModule.Types.Single(t=>t.FullName=="Terraria.Player").Methods.Single(x=>x.Name==name);
            var original=m.Body.Instructions.ToArray();var opcodes=original.Select(i=>i.OpCode).ToArray();
            var predicate=a.MainModule.ImportReference(typeof(ItemUseOfflineTests).GetMethod(nameof(Allowed)));
            ItemUsePatches.Patch(m,op,predicate);
            Check(original.Select((i,n)=>i.OpCode==opcodes[n]).All(v=>v),op+": original human instruction opcodes retained");
            Check(!m.Body.Instructions.Any(i=>i.OpCode==OpCodes.Stsfld&&i.Operand is FieldReference f&&f.Name=="myPlayer"),op+": global identity never changed");
            int count=m.Body.Instructions.Count;bool rejected=false;
            try{ItemUsePatches.Patch(m,op,predicate);}catch(InvalidOperationException){rejected=true;}
            Check(rejected&&m.Body.Instructions.Count==count,op+": duplicate rejected before mutation");
            foreach(int offset in op=="inner"?new[]{0x071d,0x0728,0x074d,0x1677,0x1849,0x187d}:new[]{0x017d,0x0188,0x018a,0x044d,0x054f})
            {
                using var bad=AssemblyDefinition.ReadAssembly(dll);var method=bad.MainModule.Types.Single(t=>t.FullName=="Terraria.Player").Methods.Single(x=>x.Name==name);
                var at=method.Body.Instructions.Single(i=>i.Offset==offset);at.OpCode=OpCodes.Nop;at.Operand=null;
                var before=method.Body.Instructions.Select(i=>i.ToString()).ToArray();rejected=false;
                try{ItemUsePatches.Patch(method,op,bad.MainModule.ImportReference(typeof(ItemUseOfflineTests).GetMethod(nameof(Allowed))));}catch(InvalidOperationException){rejected=true;}
                Check(rejected&&method.Body.Instructions.Select(i=>i.ToString()).SequenceEqual(before),op+": unsupported shape safely rejected "+offset.ToString("x"));
            }
            var actual=runtime.GetType("Terraria.Player").GetMethod(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
            using var dmd=new DynamicMethodDefinition(actual);using var il=new ILContext(dmd.Definition);
            il.Invoke(c=>ItemUsePatches.Patch(c.Method,op,c.Module.ImportReference(typeof(ItemUseOfflineTests).GetMethod(nameof(Allowed)))));
            Check(true,op+": actual DMD/ILContext accepted");
        }
        Console.WriteLine($"G6 targeted IL checks passed: {checks}");
    }
    public static void Audit(AssemblyDefinition probe,string gamePath)
    {
        var type=probe.MainModule.Types.Single(t=>t.Name=="ActorItemUse");
        var methods=type.Methods.Where(m=>m.HasBody).ToArray();
        bool Calls(MethodDefinition m,string name)=>m.Body.Instructions.Any(i=>i.Operand is MethodReference r&&r.Name==name);
        var update=methods.Single(m=>m.Name=="Update");
        var context=type.NestedTypes.Single(t=>t.Name=="Context");
        Check(new[]{"Actor","World","Generation","Item","Slot"}.All(n=>context.Fields.Any(f=>f.Name==n)),"packaged action binds Player, world, incarnation and item reference");
        Check(update.Body.Instructions.Count(i=>i.Operand is MethodReference r&&r.Name=="ItemCheck")==1&&Calls(update,"Owns")&&Calls(update,"Binding"),"one native ItemCheck guarded by survival scope and action binding");
        Check(update.Body.ExceptionHandlers.Any(e=>e.HandlerType==ExceptionHandlerType.Finally)&&Calls(update,"Retire"),"native use has exception release and unconditional finally");
        Check(!methods.Any(m=>Calls(m,"ApplyItemAnimation")||Calls(m,"ApplyItemTime")||Calls(m,"SetItemAnimation")||Calls(m,"SetItemTime")),"controller never fabricates native animation/use-time results");
        Check(methods.Where(m=>Calls(m,"SetDefaults")).All(m=>m.Name=="FixtureCore"),"test items are created only by explicit fixture command");
        Check(new[]{"Select","Start","Fixture","Stop"}.All(n=>Calls(methods.Single(m=>m.Name==n),"Enter")),"all command mutations hold lifecycle Gate");
        var kill=probe.MainModule.Types.Single(t=>t.Name=="NativeSurvival").Methods.Single(m=>m.Name=="Kill");
        int retire=kill.Body.Instructions.First(i=>i.Operand is MethodReference r&&r.DeclaringType.Name=="ActorItemUse"&&r.Name=="Retire").Offset;
        Check(retire<kill.Body.Instructions.Last(i=>i.Operand is MethodReference r&&r.Name=="Invoke").Offset,"death releases use before native KillMe");
        var finish=probe.MainModule.Types.Single(t=>t.Name=="ProbeSession").Methods.Single(m=>m.Name=="Finish");
        Check(Calls(finish,"Retire")&&finish.Body.Instructions.Any(i=>i.Operand is MethodReference r&&r.DeclaringType.Name=="ActorItemUse"&&r.Name=="Retire"),"G1 despawn retires item action");
        var save=probe.MainModule.Types.Single(t=>t.Name=="CharacterStore").Methods.Single(m=>m.Name=="Save");
        Check(Calls(save,"get_Running"),"character save refuses an unfinished item action");
        using var native=AssemblyDefinition.ReadAssembly(Path.Combine(gamePath,"tModLoader.dll"));
        var p=native.MainModule.Types.Single(t=>t.FullName=="Terraria.Player");
        Check(Calls(p.Methods.Single(m=>m.Name=="Update"),"ItemCheckWrapped")
            &&Calls(p.Methods.Single(m=>m.Name=="ItemCheck"),"PreItemCheck")&&Calls(p.Methods.Single(m=>m.Name=="ItemCheck"),"PostItemCheck"),"installed update and tML pre/post item hooks match injection path");
        var receive=native.MainModule.Types.Single(t=>t.FullName=="Terraria.MessageBuffer").Methods.Single(m=>m.Name=="GetData");
        var packet41=receive.Body.Instructions.Where(i=>i.Offset>=0x4f89&&i.Offset<0x501d).ToArray();
        Check(packet41.Any(i=>i.Offset==0x4fc8&&i.OpCode==OpCodes.Pop)&&!packet41.Any(i=>i.OpCode==OpCodes.Stfld&&i.Operand is FieldReference f&&f.Name=="itemAnimation"),"native packet 41 discards animation count; controls drive remote animation");
        Console.WriteLine($"G6 combined targeted checks passed: {checks}");
    }
}
