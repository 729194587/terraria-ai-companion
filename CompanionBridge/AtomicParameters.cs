using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CompanionBridge.Motion;

namespace CompanionBridge;

internal static class AtomicParameters
{
    internal static LandingRegion ReadLandingRegion(JsonElement args)
    {
        Validate("navigate_to_landing_region", args, false);
        return new(args.GetProperty("minPlayerLeftX").GetSingle(), args.GetProperty("maxPlayerLeftX").GetSingle(),
            args.GetProperty("surfaceY").GetSingle());
    }

    internal static void Validate(string op, JsonElement args, bool query)
    {
        if (args.ValueKind != JsonValueKind.Object) throw new FormatException("Arguments must be an object.");
        var keys = new HashSet<string>();
        foreach (var p in args.EnumerateObject()) if (!keys.Add(p.Name)) throw new FormatException("Duplicate parameter.");
        void Fields(params string[] names) { if (!keys.SetEquals(names)) throw new FormatException("Unexpected/missing parameters for " + op); }
        void Integer(string key, int min, int max) { if (!args.GetProperty(key).TryGetInt32(out int v) || v < min || v > max) throw new FormatException("Invalid " + key); }
        void Number(JsonElement v, double min, double max) { if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double n) || !double.IsFinite(n) || n < min || n > max) throw new FormatException("Invalid finite coordinate/radius."); }
        if (query)
        {
            switch (op)
            {
                case "get_player_state": case "get_world_state": Fields(); break;
                case "inspect_inventory":
                    if (keys.Except(new[] { "mode", "item" }).Any()) throw new FormatException("Unexpected inventory parameters.");
                    string mode = args.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : keys.Contains("mode") ? null : "selected";
                    if (mode is not ("selected" or "hotbar" or "item" or "full")) throw new FormatException("Invalid inventory mode.");
                    if (keys.Contains("item") != (mode == "item")) throw new FormatException("item required only in item mode.");
                    if (mode == "item" && (args.GetProperty("item").ValueKind != JsonValueKind.String || args.GetProperty("item").GetString().Length is < 1 or > 150)) throw new FormatException("Invalid item identifier.");
                    break;
                case "scan_tiles":
                    if (keys.Contains("maxResults")) { Integer("maxResults", 1, 256); keys.Remove("maxResults"); }
                    Fields("minX", "minY", "maxX", "maxY", "tileTypes");
                    foreach (string key in new[] { "minX", "minY", "maxX", "maxY" }) Integer(key, 0, 100000);
                    long width = (long)args.GetProperty("maxX").GetInt32() - args.GetProperty("minX").GetInt32() + 1;
                    long height = (long)args.GetProperty("maxY").GetInt32() - args.GetProperty("minY").GetInt32() + 1;
                    if (width <= 0 || height <= 0 || width * height > 4096) throw new FormatException("Scan area must be 1..4096 tiles.");
                    var types = args.GetProperty("tileTypes"); var seen = new HashSet<int>();
                    if (types.ValueKind != JsonValueKind.Array || types.GetArrayLength() is < 1 or > 32) throw new FormatException("Invalid tileTypes.");
                    foreach (var type in types.EnumerateArray())
                        if (!type.TryGetInt32(out int id) || id < 0 || id > 65535 || !seen.Add(id)) throw new FormatException("Invalid/duplicate tile type.");
                    break;
                case "inspect_tile": Fields("x", "y"); Integer("x", 0, 100000); Integer("y", 0, 100000); break;
                case "scan_entities":
                    Fields("category", "radius"); Number(args.GetProperty("radius"), 1, 1600);
                    if (args.GetProperty("category").GetString() is not ("all" or "npc" or "item" or "player")) throw new FormatException("Invalid category."); break;
                default: throw new FormatException("Unsupported query.");
            }
            return;
        }
        switch (op)
        {
            case "navigate_to_landing_region":
                Fields("minPlayerLeftX", "maxPlayerLeftX", "surfaceY");
                foreach (string key in keys) Number(args.GetProperty(key), 0, 1000000);
                if (args.GetProperty("minPlayerLeftX").GetDouble() > args.GetProperty("maxPlayerLeftX").GetDouble())
                    throw new FormatException("Landing X bounds must be ordered.");
                break;
            case "navigate_to_x": Fields("targetX"); Number(args.GetProperty("targetX"), 0, 1000000); break;
            case "drop_through_platform": Fields(); break;
            case "select_hotbar_slot": Fields("slot"); Integer("slot", 0, 9); break;
            case "mine_tile": case "place_tile": case "interact_tile": case "mine_wall": case "place_wall": case "hammer_tile": Fields("x", "y"); Integer("x", 0, 100000); Integer("y", 0, 100000); break;
            case "attack_entity": Fields("entityRef"); if (args.GetProperty("entityRef").ValueKind != JsonValueKind.String || args.GetProperty("entityRef").GetString().Length is < 1 or > 150) throw new FormatException("Invalid entityRef."); break;
            case "use_item":
                if (keys.Count == 0) break;
                Fields("target"); var target = args.GetProperty("target");
                if (target.ValueKind != JsonValueKind.Object || target.GetRawText().Length > 150) throw new FormatException("Invalid target.");
                var targetKeys = new HashSet<string>(); foreach (var p in target.EnumerateObject()) targetKeys.Add(p.Name);
                if (!targetKeys.SetEquals(new[] { "x", "y" })) throw new FormatException("Target requires x,y world pixels.");
                Number(target.GetProperty("x"), 0, 1000000); Number(target.GetProperty("y"), 0, 1000000); break;
            default: throw new FormatException("Unsupported action.");
        }
    }
}
