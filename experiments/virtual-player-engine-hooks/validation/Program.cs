using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

public static class Program
{
    public static int Main(string[] args)
    {
        // Install resolution before JIT-compiling tests that use MonoMod/Cecil.
        var files = Directory.GetFiles(Path.Combine(args[0], "Libraries"), "*.dll", SearchOption.AllDirectories)
            .GroupBy(Path.GetFileNameWithoutExtension).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        AssemblyLoadContext.Default.Resolving += (_, name) => files.TryGetValue(name.Name, out var path)
            ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        try { Run(args); return 0; }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run(string[] args)
    {
        Console.WriteLine("Verification CLR: " + Environment.Version + "; no Terraria game process or world.");
        if(args.Length>3&&args[3] is "item-use" or "mining")
        {
            EngineHookOfflineTests.ReadNativeDefaults(args[0]);
            ItemUseOfflineTests.Run(args[0]);
            if(args[3]=="mining")MiningOfflineTests.Run(args[0]);
            if(!string.IsNullOrEmpty(args[2]))EngineHookOfflineTests.AuditPackage(args[2]);
            return;
        }
        EngineHookOfflineTests.Run(args[0], args[1]);
        ItemUseOfflineTests.Run(args[0]);
        MiningOfflineTests.Run(args[0]);
        PickupOwnershipOfflineTests.Run(args[0]);
        if (args.Length > 2 && !string.IsNullOrEmpty(args[2])) EngineHookOfflineTests.AuditPackage(args[2]);
        PersistenceOfflineTests.Run(args[0],args[1]);
        ControlOfflineTests.Run(args[0]);
        SurvivalOfflineTests.Run(args[0]);
    }
}
