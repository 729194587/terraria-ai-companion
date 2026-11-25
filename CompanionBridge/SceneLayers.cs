using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace CompanionBridge;

// One additive read option; factories run only for explicitly included layers.
internal sealed class SceneLayers
{
    private static readonly string[] Names={"map","geometry","objects","hazards","landingRegions","placementCandidates","objectPlacementCandidates","resources"};
    private readonly HashSet<string> selected;
    private SceneLayers(IEnumerable<string> names){selected=new(names,StringComparer.Ordinal);}
    internal string[] Included=>Names.Where(Has).ToArray();
    internal bool Has(string name)=>selected.Contains(name);
    internal static SceneLayers Read(JsonElement args)
    {
        if(args.ValueKind!=JsonValueKind.Object)throw new FormatException("Scene arguments must be an object.");
        bool region=SceneScope.Read(args).Region;
        if(!args.TryGetProperty("layers",out var layers))return new(region?new[]{"geometry","objects","hazards"}:Names.Take(7));
        if(args.EnumerateObject().Count(p=>p.Name=="layers")!=1||layers.ValueKind!=JsonValueKind.Array
            ||layers.GetArrayLength()<1||layers.GetArrayLength()>Names.Length)
            throw new FormatException("layers must be a nonempty unique array of supported scene layer names.");
        var names=new HashSet<string>(StringComparer.Ordinal);
        foreach(var layer in layers.EnumerateArray())
            if(layer.ValueKind!=JsonValueKind.String||!Names.Contains(layer.GetString(),StringComparer.Ordinal)||!names.Add(layer.GetString()))
                throw new FormatException("Invalid or duplicate scene layer name.");
        if(region&&names.Any(n=>n!="resources"&&!Names.Take(4).Contains(n)))
            throw new FormatException("Regional scene supports map, geometry, objects, hazards and resources; action candidates require local observation.");
        return new(names);
    }
    internal void Add(Dictionary<string,object> response,string layer,Func<object> build)
    {if(Has(layer))response[layer]=build();}
}

// L0 remains wire-compatible. L1 is an explicit capability-gated read, never a
// larger navigation collision world or a second persistent map.
internal sealed record SceneScope(bool Region,int Width,int Height)
{
    internal const int MaxTiles=8192;
    internal const double SamplingBudgetMs=10;
    internal static SceneScope Read(JsonElement args)
    {
        bool region=false;
        if(args.TryGetProperty("scale",out var scale))
        {
            if(scale.ValueKind!=JsonValueKind.String||scale.GetString() is not ("local" or "region"))
                throw new FormatException("scale must be local or region.");
            region=scale.GetString()=="region";
        }
        if(!region&&(args.TryGetProperty("width",out _)||args.TryGetProperty("height",out _)))
            throw new FormatException("width/height require scale=region.");
        int Size(string name,int fallback,int min,int max)
        {
            if(!args.TryGetProperty(name,out var n))return fallback;
            if(!n.TryGetInt32(out int v)||v<min||v>max)throw new FormatException("Invalid regional "+name);
            return v;
        }
        int w=region?Size("width",120,45,128):45,h=region?Size("height",68,29,96):29;
        if(region&&(args.TryGetProperty("maxRegions",out _)||args.TryGetProperty("maxPlacements",out _)))
            throw new FormatException("Regional scene has no navigation or placement candidates.");
        if(w*h>MaxTiles)throw new FormatException("Regional scene area must be <=8192 tiles.");
        return new(region,w,h);
    }
}
