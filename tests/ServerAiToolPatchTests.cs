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
        RequireRejected(()=>ItemUsePatches.Patch(pose,"combat-pose",predicate));

        var hit=player.Methods.Single(m=>m.Name=="ProcessHitAgainstNPC");
        var strike=hit.Body.Instructions.Single(i=>i.Offset==0x0529);
        var native=(MethodReference)strike.Operand;
        var wrapper=new MethodReference("StrikeWithEvidence",native.ReturnType,player);
        wrapper.Parameters.Add(new ParameterDefinition(native.DeclaringType));
        foreach(var parameter in native.Parameters)wrapper.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
        wrapper.Parameters.Add(new ParameterDefinition(player));
        var originalNext=strike.Next;
        ItemUsePatches.Patch(hit,"combat-strike",wrapper);
        if(strike.Previous.OpCode!=OpCodes.Ldarg_0||strike.OpCode!=OpCodes.Call||!ReferenceEquals(strike.Operand,wrapper)
            ||!ReferenceEquals(strike.Next,originalNext))
            throw new InvalidOperationException("Combat strike must preserve the native result stack before post-hit hooks.");
        RequireRejected(()=>ItemUsePatches.Patch(hit,"combat-strike",wrapper));
        var cooldown=player.Methods.Single(m=>m.Name=="UpdateMeleeHitCooldowns");
        var loop=cooldown.Body.Instructions.Single(i=>i.Offset==0x000d);
        ItemUsePatches.Patch(cooldown,"combat-cooldown",predicate);
        if(!cooldown.Body.Instructions.Any(i=>i.OpCode==OpCodes.Brtrue&&ReferenceEquals(i.Operand,loop)))
            throw new InvalidOperationException("Owned combat must reach native melee cooldown decrement.");
        RequireRejected(()=>ItemUsePatches.Patch(cooldown,"combat-cooldown",predicate));
        Console.WriteLine("Combat pose, strike and cooldown guards: pinned IL and duplicate rejection passed.");
    }

    private static void RequireRejected(Action patch)
    {
        try{patch();}
        catch(InvalidOperationException){return;}
        throw new InvalidOperationException("Duplicate combat patch was admitted.");
    }
}
