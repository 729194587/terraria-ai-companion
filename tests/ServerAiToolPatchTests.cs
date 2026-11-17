using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Runtime.Loader;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;
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
    public static bool SkipClientUi(object player)=>false;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run(string engine)
    {
        Console.WriteLine("Offline tool patch checks; CLR "+Environment.Version+"; no world/model/client.");
        ItemUseOfflineTests.Run(engine);
        MiningOfflineTests.Run(engine);
        CraftingPatchOfflineTests.Run(engine);
        using var module=ModuleDefinition.ReadModule(Path.Combine(engine,"tModLoader.dll"));
        var player=module.GetType("Terraria.Player");
        var wrapped=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(engine,"tModLoader.dll"))
            .GetType("Terraria.Player").GetMethod("ItemCheckWrapped",BindingFlags.Instance|BindingFlags.NonPublic,
                null,new[]{typeof(int)},null);
        using(var dmd=new DynamicMethodDefinition(wrapped))
        using(var il=new ILContext(dmd.Definition))
        {
            il.Invoke(c=> {
                var skip=c.Module.ImportReference(typeof(ServerAiToolPatchTests).GetMethod(nameof(SkipClientUi)));
                var nativeCalls=c.Body.Instructions.Where(i=>i.Operand is MethodReference r&&r.Name=="ItemCheck").ToArray();
                ItemUsePatches.Patch(c.Method,"wrapper",skip);
                if(nativeCalls.Length!=2||nativeCalls.Any(i=>i.OpCode!=OpCodes.Call||!c.Body.Instructions.Contains(i)))
                    throw new InvalidOperationException("Wrapper must retain both exclusive native ItemCheck branches.");
                RequireRejected(()=>ItemUsePatches.Patch(c.Method,"wrapper",skip));
            });
            dmd.Generate();
        }
        Console.WriteLine("ItemCheckWrapped: live IL clone retains native calls and accepts only one UI guard patch.");
        var mining=player.Methods.Single(m=>m.Name=="ItemCheck_UseMiningTools");
        var owner=player.Methods.Single(m=>m.Name=="ItemCheck_OwnerOnlyCode");
        var miningEntry=owner.Body.Instructions.Single(i=>i.Offset==0x01ef);
        var cursorEnd=owner.Body.Instructions.Single(i=>i.Offset==0x044d);
        var nativeMiningCall=owner.Body.Instructions.Single(i=>i.Offset==0x01f1);
        var ui=module.ImportReference(typeof(ServerAiToolPatchTests).GetMethod(nameof(SkipClientUi)));
        var scope=new MethodReference("MiningScope",module.TypeSystem.Boolean,player);
        scope.Parameters.Add(new ParameterDefinition(player));
        ItemUsePatches.Patch(owner,"melee",ui,miningOnly:ui,miningScope:scope);
        if(nativeMiningCall.Operand is not MethodReference miningCall||miningCall.FullName!=mining.FullName
            ||!owner.Body.Instructions.Any(i=>i.Operand==scope&&i.Next.Operand==miningEntry)
            ||!owner.Body.Instructions.Any(i=>i.Operand==scope&&i.Next.Operand is Instruction target&&target.Next?.Operand==ui&&ReferenceEquals(target.Next.Next.Next,cursorEnd)))
            throw new InvalidOperationException("AI mining must branch into the existing native call and skip the rest of the cursor block.");
        var strikeGates=mining.Body.Instructions.Single(i=>i.Offset==0x0109);
        ItemUsePatches.Patch(mining,"mining-ui",ui);
        if(!mining.Body.Instructions.Any(i=>i.Operand==ui&&i.Next.Operand==strikeGates))
            throw new InvalidOperationException("Dedicated AI must skip only the native cursor icon block before the tool timing gates.");
        Console.WriteLine("Native tool routing and cursor icon guards passed.");
        var projectile=module.GetType("Terraria.Projectile");
        bool Calls(MethodDefinition m,int offset,string name)=>m.Body.Instructions.Any(i=>i.Offset==offset
            &&i.Operand is MethodReference call&&call.Name==name);
        var shoot=player.Methods.Single(m=>m.Name=="ItemCheck_Shoot");
        var payMana=player.Methods.Single(m=>m.Name=="ItemCheck_PayMana");
        var update=projectile.Methods.Single(m=>m.Name=="Update");
        var damage=projectile.Methods.Single(m=>m.Name=="Damage");
        var spawn=projectile.Methods.Single(m=>m.Name=="NewProjectile"&&m.Parameters.Count==12);
        var movement=projectile.Methods.Single(m=>m.Name=="HandleMovement");
        if(!Calls(movement,0x0b54,"TileCollideStyle")||!Calls(movement,0x1408,"TileCollision")
            ||!Calls(movement,0x1435,"SlopeCollision"))throw new InvalidOperationException("Native projectile collision path changed.");
        if(!Calls(shoot,0x0098,"PickAmmo")||!Calls(shoot,0x02e8,"get_MouseWorld")
            ||!Calls(update,0x0499,"AI")||!Calls(update,0x1575,"Damage")||!Calls(update,0x1d18,"Kill")
            ||!Calls(spawn,0x0710,"OnSpawn")||!Calls(spawn,0x073d,"SendData")
            ||!Calls(damage,0x0b74,"CanHitNPCWithProj")||!Calls(damage,0x3210,"StrikeNPC")||!Calls(damage,0x421b,"OnHitNPCWithProj"))
            throw new InvalidOperationException("Native ranged shooting, update or damage contract changed.");
        if(!payMana.Body.Instructions.Any(i=>i.Operand is MethodReference c
            &&c.DeclaringType.FullName=="Terraria.Player"&&c.Name=="CheckMana"))
            throw new InvalidOperationException("Native magic mana payment changed; ItemCheck_PayMana must still call CheckMana.");
        Console.WriteLine("Native ranged shoot/ammo, projectile update and hit callback IL passed.");
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
        throw new InvalidOperationException("Duplicate IL patch was admitted.");
    }
}
