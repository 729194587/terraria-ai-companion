using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

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
    }
}
