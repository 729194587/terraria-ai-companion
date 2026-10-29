using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace CompanionBridge;

// One additive read option; factories run only for explicitly included layers.
internal sealed class SceneLayers
{
    private static readonly string[] Names={"map","geometry","objects","hazards","landingRegions","placementCandidates","objectPlacementCandidates"};
    private readonly HashSet<string> selected;
    private SceneLayers(IEnumerable<string> names){selected=new(names,StringComparer.Ordinal);}
    internal string[] Included=>Names.Where(Has).ToArray();
    internal bool Has(string name)=>selected.Contains(name);
    internal static SceneLayers Read(JsonElement args)
    {
        if(args.ValueKind!=JsonValueKind.Object)throw new FormatException("Scene arguments must be an object.");
        if(!args.TryGetProperty("layers",out var layers))return new(Names);
        if(args.EnumerateObject().Count(p=>p.Name=="layers")!=1||layers.ValueKind!=JsonValueKind.Array
            ||layers.GetArrayLength()<1||layers.GetArrayLength()>Names.Length)
            throw new FormatException("layers must be a nonempty unique array of supported scene layer names.");
        var names=new HashSet<string>(StringComparer.Ordinal);
        foreach(var layer in layers.EnumerateArray())
            if(layer.ValueKind!=JsonValueKind.String||!Names.Contains(layer.GetString(),StringComparer.Ordinal)||!names.Add(layer.GetString()))
                throw new FormatException("Invalid or duplicate scene layer name.");
        return new(names);
    }
    internal void Add(Dictionary<string,object> response,string layer,Func<object> build)
    {if(Has(layer))response[layer]=build();}
}
