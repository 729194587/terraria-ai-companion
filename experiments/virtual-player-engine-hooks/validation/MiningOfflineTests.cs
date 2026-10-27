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

public static class MiningOfflineTests
{
    private static int checks;
    private static void Check(bool ok,string message){if(!ok)throw new Exception(message);checks++;Console.WriteLine("PASS G7: "+message);}
    public static bool Allowed(object p)=>false;
    public static int Target()=>0;
    public static void Run(string path)
    {
        string dll=Path.Combine(path,"tModLoader.dll");
        using(var a=AssemblyDefinition.ReadAssembly(dll))
        {MiningPatches.Validate(a.MainModule.Types.Single(t=>t.FullName=="Terraria.Player"));Check(true,"actual pinned mining/range IL validated");}
        foreach(var pair in new[]{("PickTile",0x0032),("PickTile",0x003a),("PickTile",0x005b),("PickTile",0x006d),("PickTile",0x02c3),
            ("PickTile",0x0072),("PickTile",0x030a),("PickTile",0x0353),("ItemCheck_UseMiningTools",0x0060),
            ("ItemCheck_UseMiningTools_ActuallyUseMiningTool",0x0473),("ItemCheck_UseMiningTools_ActuallyUseMiningTool",0x0493),("IsTargetTileInItemRange",0x0020),
            ("ItemCheck_UseMiningTools_ActuallyUseMiningTool",0x01f4),("ItemCheck_UseMiningTools_ActuallyUseMiningTool",0x028a),
            ("ItemCheck_UseMiningTools_ActuallyUseMiningTool",0x02e9),("ItemCheck_UseMiningTools_ActuallyUseMiningTool",0x02ee),
            ("ItemCheck_UseMiningTools_ActuallyUseMiningTool",0x03ba),("ItemCheck_UseMiningTools_ActuallyUseMiningTool",0x03c7),
            ("ItemCheck_UseMiningTools_ActuallyUseMiningTool",0x040f),("ItemCheck_UseMiningTools_ActuallyUseMiningTool",0x0453)})
        {
            using var a=AssemblyDefinition.ReadAssembly(dll);var p=a.MainModule.Types.Single(t=>t.FullName=="Terraria.Player");
            var m=p.Methods.Single(m=>m.Name==pair.Item1);var i=m.Body.Instructions.Single(i=>i.Offset==pair.Item2);i.OpCode=OpCodes.Nop;i.Operand=null;
            string before=string.Join("\n",m.Body.Instructions);bool refused=false;
            try{MiningPatches.Validate(p);}catch(InvalidOperationException){refused=true;}
            Check(refused&&before==string.Join("\n",m.Body.Instructions),"unknown mining structure refused without mutation "+pair);
        }
        foreach(var pair in new[]{("Terraria.Player","DoesPickTargetTransformOnKill",0x0005),
            ("Terraria.Player","DoesPickTargetTransformOnKill",0x0019),("Terraria.WorldGen","KillTile",0x069f)})
        {
            using var a=AssemblyDefinition.ReadAssembly(dll);var p=a.MainModule.Types.Single(t=>t.FullName=="Terraria.Player");
            var m=a.MainModule.Types.Single(t=>t.FullName==pair.Item1).Methods.Single(m=>m.Name==pair.Item2);
            var i=m.Body.Instructions.Single(i=>i.Offset==pair.Item3);i.OpCode=OpCodes.Nop;i.Operand=null;
            bool refused=false;try{MiningPatches.Validate(p);}catch(InvalidOperationException){refused=true;}
            Check(refused,"unknown grass transformation structure refused "+pair);
        }
        var runtime=AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);var player=runtime.GetType("Terraria.Player");
        using(var dmd=new DynamicMethodDefinition(player.GetMethod("IsTargetTileInItemRange",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)))
        {
            var get=dmd.Definition.Module.ImportReference(typeof(MiningOfflineTests).GetMethod(nameof(Target)));
            MiningPatches.RangeClone(dmd.Definition,get,get);
            Check(dmd.Definition.Parameters.Count==2&&!dmd.Definition.Body.Instructions.Any(i=>i.Operand is FieldReference f&&f.Name.StartsWith("tileTarget")),"range clone reads private x/y intent without any global target access");
            Check(!dmd.Definition.Body.Instructions.Any(i=>i.OpCode==OpCodes.Stsfld),"range clone has no global writes");
            Check(dmd.Generate()!=null,"actual native range clone generates executable IL");
            bool duplicate=false;try{MiningPatches.RangeClone(dmd.Definition,get,get);}catch(InvalidOperationException){duplicate=true;}Check(duplicate,"range clone cannot be patched twice");
        }
        using(var dmd=new DynamicMethodDefinition(player.GetMethod("ItemCheck_OwnerOnlyCode",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)))
        using(var il=new ILContext(dmd.Definition))
        {
            il.Invoke(c=>{var predicate=c.Module.ImportReference(typeof(MiningOfflineTests).GetMethod(nameof(Allowed)));ItemUsePatches.Patch(c.Method,"melee",predicate,predicate,predicate);});
            Check(dmd.Definition.Body.Instructions.Count(i=>i.Operand is MethodReference r&&r.DeclaringType.Name==nameof(MiningOfflineTests))==3,"actual G6/G7 owner IL includes identity, dispatch and no-cut gates");
            Check(!dmd.Definition.Body.Instructions.Any(i=>i.OpCode==OpCodes.Stsfld&&i.Operand is FieldReference f&&f.Name=="myPlayer"),"owner IL never impersonates Main.myPlayer");
        }
        Console.WriteLine($"G7 native IL checks passed: {checks}");
    }
    public static void Audit(AssemblyDefinition probe)
    {
        var type=probe.MainModule.Types.Single(t=>t.Name=="ActorMining");var methods=type.Methods.Where(m=>m.HasBody).ToArray();
        var c=type.NestedTypes.Single(t=>t.Name=="Context");
        bool Calls(MethodDefinition m,string name)=>m.Body.Instructions.Any(i=>i.Operand is MethodReference r&&r.Name==name);
        Check(new[]{"Id","Actor","World","Generation","Item","Slot","X","Y","Destroyed","PeakDamage","Reason"}.All(n=>c.Fields.Any(f=>f.Name==n)),"packaged executor retains identity, binding, target and separate destruction/progress evidence");
        Check(!methods.Any(m=>m.Body.Instructions.Any(i=>i.Operand is MethodReference r&&r.DeclaringType.FullName=="Terraria.WorldGen"&&r.Name=="KillTile")),"executor never directly calls WorldGen.KillTile");
        Check(!methods.Any(m=>m.Body.Instructions.Any(i=>i.OpCode==OpCodes.Stsfld&&i.Operand is FieldReference f&&f.DeclaringType.FullName=="Terraria.Player"&&f.Name.StartsWith("tileTarget"))),"executor never writes global human tile targets");
        var update=methods.Single(m=>m.Name=="Update");Check(update.Body.Instructions.Count(i=>i.Operand is MethodReference r&&r.Name=="ItemCheck")==1&&update.Body.ExceptionHandlers.Any(e=>e.HandlerType==ExceptionHandlerType.Finally),"one native ItemCheck with unconditional cleanup");
        Check(Calls(methods.Single(m=>m.Name=="Start"),"Enter")&&Calls(methods.Single(m=>m.Name=="Stop"),"Enter"),"commands retain lifecycle Gate");
        var survival=probe.MainModule.Types.Single(t=>t.Name=="NativeSurvival");
        Check(survival.Methods.Single(m=>m.Name=="Kill").Body.Instructions.Any(i=>i.Operand is MethodReference r&&r.DeclaringType.Name=="ActorMining"&&r.Name=="Retire"),"native death retires G7");
        Check(probe.MainModule.Types.Single(t=>t.Name=="ProbeSession").Methods.Single(m=>m.Name=="Finish").Body.Instructions.Any(i=>i.Operand is MethodReference r&&r.DeclaringType.Name=="ActorMining"&&r.Name=="Retire"),"G1 teardown retires G7");
        Console.WriteLine($"G7 combined targeted checks passed: {checks}");
    }
}
