using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Mono.Cecil;
using Mono.Cecil.Cil;
using VirtualPlayerEngineProbe;

public static class ServerAiToolPatchTests
{
    public static int Main(string[] args)
    {
        var files=Directory.GetFiles(Path.Combine(args[0],"Libraries"),"*.dll",SearchOption.AllDirectories)
            .GroupBy(Path.GetFileNameWithoutExtension).ToDictionary(g=>g.Key,g=>g.First(),StringComparer.OrdinalIgnoreCase);
        AssemblyLoadContext.Default.Resolving+=(_,name)=>files.TryGetValue(name.Name,out var path)
            ?AssemblyLoadContext.Default.LoadFromAssemblyPath(path):null;
        try{Run(args[0]);return 0;}catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run(string engine)
    {
        Console.WriteLine("Offline tool patch checks; CLR "+Environment.Version+"; no world/model/client.");
        ItemUseOfflineTests.Run(engine);
        MiningOfflineTests.Run(engine);
        PlacementPatchOfflineTests.Run(engine);
        CraftingPatchOfflineTests.Run(engine);
        using var module=ModuleDefinition.ReadModule(Path.Combine(engine,"tModLoader.dll"));
        var player=module.GetType("Terraria.Player");
        var pose=player.Methods.Single(m=>m.Name=="ItemCheck_ApplyUseStyle_Inner");
        var predicate=new MethodReference("OwnedCombat",module.TypeSystem.Boolean,player);
        predicate.Parameters.Add(new ParameterDefinition(player));
        var original=pose.Body.Instructions.ToArray();
        ItemUsePatches.Patch(pose,"combat-pose",predicate);
        if(!pose.Body.Instructions.Contains(original[2])||original[2].OpCode!=OpCodes.Ret
            ||!pose.Body.Instructions.Any(i=>i.OpCode==OpCodes.Brtrue&&ReferenceEquals(i.Operand,original[3])))
            throw new InvalidOperationException("Combat pose must preserve the native dedicated-server return outside the owned scope.");
        try{ItemUsePatches.Patch(pose,"combat-pose",predicate);}
        catch(InvalidOperationException){Console.WriteLine("Combat pose guard: pinned IL and duplicate rejection passed.");return;}
        throw new InvalidOperationException("Duplicate combat pose patch was admitted.");
    }
}
