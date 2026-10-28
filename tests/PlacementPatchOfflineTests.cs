using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;
using VirtualPlayerEngineProbe;

public static class PlacementPatchOfflineTests
{
    public static int X(object player)=>0;
    public static void Run(string engine)
    {
        string dll=System.IO.Path.Combine(engine,"tModLoader.dll");
        var runtime=AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);int checks=0;
        void Check(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
        foreach(var entry in PlacementPatches.Reads)
        {
            using var a=AssemblyDefinition.ReadAssembly(dll);
            var m=a.MainModule.Types.Single(t=>t.FullName=="Terraria.Player").Methods.Single(m=>m.Name==entry.Key);
            var getter=a.MainModule.ImportReference(typeof(PlacementPatchOfflineTests).GetMethod(nameof(X)));
            var original=m.Body.Instructions.ToArray();
            PlacementPatches.Patch(m,getter,getter);
            Check(m.Body.Instructions.Count(i=>i.OpCode==OpCodes.Call&&i.Operand==getter)==entry.Value,"all target reads must be scoped: "+entry.Key);
            Check(!m.Body.Instructions.Any(i=>i.OpCode==OpCodes.Ldsfld&&i.Operand is FieldReference f&&f.Name is "tileTargetX" or "tileTargetY"),"unscoped target read remains");
            Check(original.All(m.Body.Instructions.Contains),"native control flow/rules were removed");
            Check(!m.Body.Instructions.Any(i=>i.OpCode==OpCodes.Stsfld&&i.Operand is FieldReference f&&f.Name is "tileTargetX" or "tileTargetY" or "myPlayer"),"global target/identity write introduced");
            int count=m.Body.Instructions.Count;bool rejected=false;
            try{PlacementPatches.Patch(m,getter,getter);}catch(InvalidOperationException){rejected=true;}
            Check(rejected&&m.Body.Instructions.Count==count,"duplicate placement patch not rejected before mutation");
            using var bad=AssemblyDefinition.ReadAssembly(dll);
            var unknown=bad.MainModule.Types.Single(t=>t.FullName=="Terraria.Player").Methods.Single(m=>m.Name==entry.Key);
            var field=unknown.Body.Instructions.First(i=>i.OpCode==OpCodes.Ldsfld&&i.Operand is FieldReference f&&f.Name=="tileTargetX");
            field.OpCode=OpCodes.Nop;field.Operand=null;var before=unknown.Body.Instructions.Select(i=>i.ToString()).ToArray();rejected=false;
            try{PlacementPatches.Patch(unknown,getter,getter);}catch(InvalidOperationException){rejected=true;}
            Check(rejected&&before.SequenceEqual(unknown.Body.Instructions.Select(i=>i.ToString())),"unknown placement read structure accepted");
            using var dmd=new DynamicMethodDefinition(runtime.GetType("Terraria.Player").GetMethod(entry.Key,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic));
            using var il=new ILContext(dmd.Definition);
            il.Invoke(c=>PlacementPatches.Patch(c.Method,c.Module.ImportReference(typeof(PlacementPatchOfflineTests).GetMethod(nameof(X))),c.Module.ImportReference(typeof(PlacementPatchOfflineTests).GetMethod(nameof(X)))));
            Check(true,"actual native ILContext accepted");
        }
        Console.WriteLine($"Native placement scoped-target IL: {checks} checks across {PlacementPatches.Reads.Count} pinned methods; no world/client/model.");
    }
}
