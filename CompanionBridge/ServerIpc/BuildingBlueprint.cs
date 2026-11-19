using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Terraria;
using Terraria.ID;
using Terraria.GameContent;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

internal static class BuildingBlueprint
{
    internal sealed record Target(string Layer,int X,int Y,Item Material,bool Supported,ActorObjectPlacement.Shape Shape,int Left,int Top,int Width,int Height);

    // Match the client's ensure_ascii=False compact encoding, including non-BMP keys.
    private static int StringBytes(string value)
    {
        int count = 2;
        foreach (var rune in value.EnumerateRunes())
            count += rune.Value switch
            {
                34 or 92 or 8 or 9 or 10 or 12 or 13 => 2,
                < 32 => 6,
                _ => rune.Utf8SequenceLength
            };
        return count;
    }

    private static int CompactBytes(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return StringBytes(value.GetString());
        if (value.ValueKind == JsonValueKind.Object)
        {
            int count = 1;
            foreach (var property in value.EnumerateObject())
                count += StringBytes(property.Name) + 2 + CompactBytes(property.Value);
            return Math.Max(2,count);
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            int count = 1;
            foreach (var element in value.EnumerateArray()) count += 1 + CompactBytes(element);
            return Math.Max(2,count);
        }
        return value.GetRawText().Length;
    }
    private static AtomicParameterException Invalid() => new(new FormatException("Invalid blueprint"));

    private static void Fields(JsonElement node, params string[] allowed)
    {
        var seen = new HashSet<string>();
        if (node.ValueKind != JsonValueKind.Object) throw Invalid();
        foreach (var field in node.EnumerateObject())
            if (!allowed.Contains(field.Name) || !seen.Add(field.Name)) throw Invalid();
    }

    private static int Number(JsonElement node, string key)
    {
        if (!node.TryGetProperty(key, out var n) || n.ValueKind != JsonValueKind.Number || !n.TryGetInt32(out int value)) throw Invalid();
        return value;
    }

    private static Item Material(JsonElement entry)
    {
        bool numeric = entry.TryGetProperty("itemType", out _);
        if (numeric == entry.TryGetProperty("contentKey", out var key)) throw Invalid();
        int type;
        if (numeric)
        {
            type = Number(entry, "itemType");
            if (type <= 0) throw Invalid();
        }
        else
        {
            if (key.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(key.GetString())) throw Invalid();
            string name = key.GetString();
            if (!name.StartsWith("Terraria/", StringComparison.Ordinal) || !ItemID.Search.TryGetId(name[9..], out type)) return null;
        }
        return type > 0 && ContentSamples.ItemsByType.TryGetValue(type, out var item) && item.ModItem == null ? item : null;
    }

    internal static IReadOnlyList<Target> Parse(JsonElement args)
    {
        Fields(args, "blueprint");
        if (!args.TryGetProperty("blueprint", out var bp) || CompactBytes(bp) > 3072) throw Invalid();
        Fields(bp, "origin", "tiles", "walls", "objects");
        if (!bp.TryGetProperty("origin", out var origin)) throw Invalid();
        Fields(origin, "x", "y");
        int ox = Number(origin, "x"), oy = Number(origin, "y");
        if (ox is < 0 or > 100000 || oy is < 0 or > 100000) throw Invalid();
        var cells = new HashSet<(bool wall, long x, long y)>();
        var entries = new List<Target>();
        long minX = long.MaxValue, minY = long.MaxValue, maxX = long.MinValue, maxY = long.MinValue;
        foreach (string layer in new[]{"tiles","walls","objects"})
        {
            if (!bp.TryGetProperty(layer, out var list)) continue;
            if (list.ValueKind != JsonValueKind.Array || entries.Count + list.GetArrayLength() > 256) throw Invalid();
            foreach (var entry in list.EnumerateArray())
            {
                Fields(entry, "x", "y", "itemType", "contentKey");
                long x = (long)ox + Number(entry,"x"), y = (long)oy + Number(entry,"y");
                if (x is < 0 or > 100000 || y is < 0 or > 100000) throw Invalid();
                var item = Material(entry);
                string kind = item == null ? null : ActorPlacement.UnifiedKind(item);
                bool supported = layer switch { "walls" => kind == "wall", "tiles" => kind == "tile", _ => kind is "object" or "chest" };
                var shape = supported && layer == "objects" ? ActorObjectPlacement.Definition(item) : null;
                long left = x - (shape?.OriginX ?? 0), top = y - (shape?.OriginY ?? 0);
                int width = shape?.Width ?? 1, height = shape?.Height ?? 1;
                minX = Math.Min(left,minX); minY = Math.Min(top,minY);
                maxX = Math.Max(left+width-1,maxX); maxY = Math.Max(top+height-1,maxY);
                if (maxX-minX >= 64 || maxY-minY >= 64) throw Invalid();
                for (int dx=0;dx<width;dx++)
                    for (int dy=0;dy<height;dy++)
                        if (!cells.Add((layer=="walls",left+dx,top+dy))) throw Invalid();

                entries.Add(new(layer,(int)x,(int)y,item,supported,shape,(int)left,(int)top,width,height));
            }
        }
        if (entries.Count == 0) throw Invalid();
        return entries;
    }
}
