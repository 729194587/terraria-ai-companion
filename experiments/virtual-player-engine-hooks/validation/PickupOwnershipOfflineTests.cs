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

public static class PickupOwnershipOfflineTests
{
    public static bool Owns(int slot)=>false;
    private static int checks;
    private static void Check(bool ok,string label){if(!ok)throw new Exception(label);checks++;Console.WriteLine("PASS pickup-owner: "+label);}
    public static void Run(string path)
    {
        string dll=Path.Combine(path,"tModLoader.dll");var runtime=AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
        foreach(bool server in new[]{true,false})
        {
            string type=server?"Terraria.Main":"Terraria.Item",name=server?"UpdateServer":"FindOwner";
            using var a=AssemblyDefinition.ReadAssembly(dll);var method=a.MainModule.Types.Single(t=>t.FullName==type).Methods.Single(m=>m.Name==name);
            var old=method.Body.Instructions.ToArray();var codes=old.Select(i=>i.OpCode).ToArray();
            var predicate=a.MainModule.ImportReference(typeof(PickupOwnershipOfflineTests).GetMethod(nameof(Owns)));
            ItemOwnershipPatches.Patch(method,server,predicate);
            Check(old.Select((i,n)=>i.OpCode==codes[n]).All(v=>v),name+": human/native opcodes unchanged");
            Check(!method.Body.Instructions.Any(i=>i.OpCode==OpCodes.Stsfld&&i.Operand is FieldReference f&&f.Name=="myPlayer"),name+": no identity spoof");
            int count=method.Body.Instructions.Count;bool rejected=false;
            try{ItemOwnershipPatches.Patch(method,server,predicate);}catch(InvalidOperationException){rejected=true;}
            Check(rejected&&method.Body.Instructions.Count==count,name+": duplicate rejected before mutation");
            foreach(int offset in server?new[]{0x0121,0x0123,0x012d,0x013c,0x0141,0x0147}:new[]{0x017c,0x017d,0x017a,0x0027,0x002c,0x01b2,0x01ca,0x01d7,0x01ef})
            {
                using var bad=AssemblyDefinition.ReadAssembly(dll);var m=bad.MainModule.Types.Single(t=>t.FullName==type).Methods.Single(m=>m.Name==name);
                var i=m.Body.Instructions.Single(i=>i.Offset==offset);i.OpCode=OpCodes.Nop;
                var before=m.Body.Instructions.Select(i=>i.ToString()).ToArray();rejected=false;
                try{ItemOwnershipPatches.Patch(m,server,bad.MainModule.ImportReference(typeof(PickupOwnershipOfflineTests).GetMethod(nameof(Owns))));}catch(InvalidOperationException){rejected=true;}
                Check(rejected&&m.Body.Instructions.Select(i=>i.ToString()).SequenceEqual(before),name+": unsupported IL rejected atomically "+offset.ToString("x"));
            }
            using var dmd=new DynamicMethodDefinition(runtime.GetType(type).GetMethod(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance));
            using var il=new ILContext(dmd.Definition);
            il.Invoke(c=>ItemOwnershipPatches.Patch(c.Method,server,c.Module.ImportReference(typeof(PickupOwnershipOfflineTests).GetMethod(nameof(Owns)))));
            Check(true,name+": real DMD/ILContext supported");
        }
        Console.WriteLine($"Pickup ownership checks passed: {checks}");
    }
}
