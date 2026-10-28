using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Utils;
using VirtualPlayerEngineProbe;

public static class CraftingPatchOfflineTests
{
    public static object Actor()=>null;
    public static object Buffer;
    public static void Consume(object recipe){}
    public static void Run(string engine)
    {
        string dll=System.IO.Path.Combine(engine,"tModLoader.dll");int checks=0;
        var runtime=AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
        void Check(bool ok,string why){if(!ok)throw new Exception(why);checks++;}
        foreach(var entry in new[]{("Terraria.Recipe","Create","consume"),("Terraria.Main","CraftItem","craft"),("Terraria.Player","AdjTiles","adjacency"),("Terraria.Recipe","CollectItemsToCraftWithFrom","collect")})
        {
            var method=runtime.GetType(entry.Item1).GetMethods(BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Single(m=>m.Name==entry.Item2&&(entry.Item3!="consume"||m.GetParameters().Length==0));
            using var d=new DynamicMethodDefinition(method);var m=d.Definition;
            CraftingPatches.Patch(m,entry.Item3,m.Module.ImportReference(typeof(CraftingPatchOfflineTests).GetMethod(nameof(Actor))),m.Module.ImportReference(typeof(CraftingPatchOfflineTests).GetField(nameof(Buffer))),m.Module.ImportReference(typeof(CraftingPatchOfflineTests).GetMethod(nameof(Consume))));
            Check(!m.Body.Instructions.Any(i=>i.Operand is FieldReference f&&f.DeclaringType.FullName=="Terraria.Main"&&f.Name is "myPlayer" or "mouseItem"),"no shared UI/player field remains: "+entry.Item3);
            Check(!m.Body.Instructions.Any(i=>i.Operand is MethodReference r&&r.FullName=="Terraria.Player Terraria.Main::get_LocalPlayer()"),"all local actor reads bound");
            if(entry.Item3=="consume")Check(m.Body.Instructions.Any(i=>i.Operand is MethodReference r&&r.Name=="ConsumeForCraft")&&m.Body.Instructions.Single(i=>i.Offset==0x0300).OpCode==OpCodes.Ret,"native ingredient engine retained, UI tail excluded");
            if(entry.Item3=="craft")Check(m.Body.Instructions.Any(i=>i.Operand is MethodReference r&&r.Name=="OnCraft")&&m.Body.Instructions.Single(i=>i.Offset==0xfe).OpCode==OpCodes.Ret,"native craft callbacks/prefix retained, popup tail excluded");
        }
        using(var a=AssemblyDefinition.ReadAssembly(dll))
        {
            var m=a.MainModule.Types.Single(t=>t.FullName=="Terraria.Recipe").Methods.Single(m=>m.Name=="Create"&&!m.HasParameters);
            var read=m.Body.Instructions.First(i=>i.Operand is FieldReference f&&f.Name=="myPlayer");read.OpCode=OpCodes.Ldc_I4_0;read.Operand=null;
            bool refused=false;try{CraftingPatches.Patch(m,"consume",null,null,null);}catch(InvalidOperationException){refused=true;}
            Check(refused,"unrecognized actor identity pattern refused");
        }
        Console.WriteLine($"Crafting private native IL: {checks} checks; no world, UI or model.");
    }
}
