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
    public static void Y(int y,object player){}
    public static bool Replacement(object player)=>false;
    public static void FillWalls(object player){}
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
        using var rope=new DynamicMethodDefinition(runtime.GetType("Terraria.Player").GetMethod(PlacementPatches.RopeMethod,
            BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic));
        var ropeGetter=rope.Definition.Module.ImportReference(typeof(PlacementPatchOfflineTests).GetMethod(nameof(X)));
        var setter=rope.Definition.Module.ImportReference(typeof(PlacementPatchOfflineTests).GetMethod(nameof(Y)));
        using var ropeIl=new ILContext(rope.Definition);
        ropeIl.Invoke(c=>PlacementPatches.PatchRope(c.Method,ropeGetter,ropeGetter,setter));
        var instructions=rope.Definition.Body.Instructions;
        Check(instructions.Count(i=>i.OpCode==OpCodes.Call&&i.Operand==ropeGetter)==7,"seven native rope target reads replaced");
        Check(instructions.Count(i=>i.OpCode==OpCodes.Call&&i.Operand==setter)==1,"native retarget store replaced with scoped setter");
        Check(!instructions.Any(i=>i.Operand is FieldReference f&&f.Name is "tileTargetX" or "tileTargetY"),"no native rope global coordinate access remains in the patched helper");
        using var source=AssemblyDefinition.ReadAssembly(dll);
        var walls=source.MainModule.Types.Single(t=>t.FullName=="Terraria.Player").Methods.Single(m=>m.Name=="PlaceThing_Walls");
        var placeCall=walls.Body.Instructions.Single(i=>i.Offset==0x02f1);
        var wallGetter=walls.Module.ImportReference(typeof(PlacementPatchOfflineTests).GetMethod(nameof(X)));
        var replacement=walls.Module.ImportReference(typeof(PlacementPatchOfflineTests).GetMethod(nameof(Replacement)));
        var fill=walls.Module.ImportReference(typeof(PlacementPatchOfflineTests).GetMethod(nameof(FillWalls)));
        PlacementPatches.Patch(walls,wallGetter,wallGetter,replacement,fill);
        Check(walls.Body.Instructions.Count(i=>i.Operand==wallGetter)==34,"wall target reads are scoped");
        Check(walls.Body.Instructions.Count(i=>i.Operand==fill)==1&&walls.Body.Instructions.Count(i=>i.Operand==replacement)==1,
            "wall auto-fill and replacement are scoped");
        Check(placeCall.Operand is MethodReference call&&call.Name=="PlaceWall","native wall placement remains in place");
        using var wallDmd=new DynamicMethodDefinition(runtime.GetType("Terraria.Player").GetMethod("PlaceThing_Walls",
            BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic));
        using var wallIl=new ILContext(wallDmd.Definition);
        wallIl.Invoke(c=>PlacementPatches.Patch(c.Method,c.Module.ImportReference(typeof(PlacementPatchOfflineTests).GetMethod(nameof(X))),
            c.Module.ImportReference(typeof(PlacementPatchOfflineTests).GetMethod(nameof(X))),
            c.Module.ImportReference(typeof(PlacementPatchOfflineTests).GetMethod(nameof(Replacement))),
            c.Module.ImportReference(typeof(PlacementPatchOfflineTests).GetMethod(nameof(FillWalls)))));
        Check(wallDmd.Generate()!=null,"actual wall IL generates an executable method");
        Console.WriteLine($"Native placement scoped-target IL: {checks} checks across {PlacementPatches.Reads.Count+2} pinned methods; no world/client/model.");
    }
}
