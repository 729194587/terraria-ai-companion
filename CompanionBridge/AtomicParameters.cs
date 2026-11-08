using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CompanionBridge.Motion;

namespace CompanionBridge;

internal sealed class AtomicParameterException : FormatException
{
    internal AtomicParameterException(Exception cause) : base("Invalid atomic arguments: " + cause.Message, cause) { }
}

internal static class AtomicParameters
{
    // JSON Schema maxLength counts Unicode scalars, not UTF-16 code units.
    private static int CharacterCount(JsonElement value) => value.GetString().EnumerateRunes().Count();
    internal static LandingRegion ReadLandingRegion(JsonElement args)
    {
        Validate("navigate_to_landing_region", args, false);
        return new(args.GetProperty("minPlayerLeftX").GetSingle(), args.GetProperty("maxPlayerLeftX").GetSingle(),
            args.GetProperty("surfaceY").GetSingle());
    }

    internal static string ValidateServerAction(string op, JsonElement args)
    {
        try { Validate(op, args, false); return op == "use_item" && args.TryGetProperty("target", out _) ? "targeted_use_not_supported" : null; }
        catch (AtomicParameterException) { return "invalid_arguments"; }
    }

    internal static void Validate(string op, JsonElement args, bool query)
    {
        // Only this pure parameter boundary translates JSON access/type failures.
        // Engine exceptions outside validation must retain their own classification.
        try { ValidateCore(op, args, query); }
        catch (Exception e) when (e is FormatException or InvalidOperationException or KeyNotFoundException or OverflowException)
        { throw new AtomicParameterException(e); }
    }

    private static void ValidateCore(string op, JsonElement args, bool query)
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
                case "observe_terrain": case "get_player_state": case "get_world_state": case "get_player_status": Fields(); break;
                case "observe_scene":
                    if(keys.Except(new[]{"maxObjects","maxRegions","maxPlacements","maxHazards","layers","scale","width","height"}).Any())throw new FormatException("Unexpected scene parameter.");
                    foreach(string key in keys.Where(k=>k is "maxObjects" or "maxRegions" or "maxPlacements" or "maxHazards"))Integer(key,1,key=="maxHazards"?64:key=="maxRegions"?24:32);
                    SceneLayers.Read(args);
                    break;
                case "query_map":
                    if(keys.Except(new[]{"mode","centerX","centerY","width","height","cellSize"}).Any()||!keys.Contains("mode"))throw new FormatException("Invalid map parameters.");
                    string mapMode=args.GetProperty("mode").GetString();
                    if(mapMode is not ("overview" or "region"))throw new FormatException("Invalid map mode.");
                    if(mapMode=="overview"&&keys.Count!=1)throw new FormatException("Overview takes only mode.");
                    foreach(string key in new[]{"centerX","centerY"})if(keys.Contains(key))Integer(key,0,100000);
                    if(keys.Contains("width"))Integer("width",1,128);
                    if(keys.Contains("height"))Integer("height",1,96);
                    if(keys.Contains("cellSize")){Integer("cellSize",1,4);if(args.GetProperty("cellSize").GetInt32()==3)throw new FormatException("cellSize must be 1,2,4.");}
                    break;
                case "query_recipes":
                    if(keys.Except(new[]{"resultType","availableOnly","limit","offset"}).Any())throw new FormatException("Unexpected recipe parameter.");
                    if(keys.Contains("resultType"))Integer("resultType",1,int.MaxValue);
                    if(keys.Contains("limit"))Integer("limit",1,24);
                    if(keys.Contains("offset"))Integer("offset",0,100000);
                    if(keys.Contains("availableOnly")&&args.GetProperty("availableOnly").ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw new FormatException("Invalid availableOnly.");
                    break;
                case "resolve_content":
                    if (keys.Except(new[] { "kind", "query", "mode", "maxResults" }).Any()
                        || !keys.Contains("kind") || !keys.Contains("query")) throw new FormatException("Invalid resolver parameters.");
                    if (args.GetProperty("kind").ValueKind != JsonValueKind.String
                        || args.GetProperty("kind").GetString() is not ("item" or "tile" or "wall")) throw new FormatException("Invalid content kind.");
                    if (args.GetProperty("query").ValueKind != JsonValueKind.String
                        || CharacterCount(args.GetProperty("query")) is < 1 or > 150) throw new FormatException("Invalid content query.");
                    if (keys.Contains("mode") && (args.GetProperty("mode").ValueKind != JsonValueKind.String
                        || args.GetProperty("mode").GetString() is not ("exact" or "search"))) throw new FormatException("Invalid resolver mode.");
                    if (keys.Contains("maxResults")) Integer("maxResults",1,32);
                    break;
                case "inspect_inventory":
                    if (keys.Except(new[] { "mode", "item", "slots" }).Any()) throw new FormatException("Unexpected inventory parameters.");
                    string mode = args.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                    if (mode is not ("selected" or "hotbar" or "item" or "full" or "slots")) throw new FormatException("Invalid inventory mode.");
                    if (keys.Contains("item") != (mode == "item")) throw new FormatException("item required only in item mode.");
                    if (mode == "item" && (args.GetProperty("item").ValueKind != JsonValueKind.String || CharacterCount(args.GetProperty("item")) is < 1 or > 150)) throw new FormatException("Invalid item identifier.");
                    if (keys.Contains("slots") != (mode == "slots")) throw new FormatException("slots required only in slots mode.");
                    if (mode == "slots")
                    {
                        var slots=args.GetProperty("slots"); var unique=new HashSet<int>();
                        if (slots.ValueKind != JsonValueKind.Array || slots.GetArrayLength() is < 1 or > 16) throw new FormatException("Invalid inventory slots.");
                        foreach(var slot in slots.EnumerateArray())
                            if (!slot.TryGetInt32(out int v) || v is < 0 or > 58 || !unique.Add(v)) throw new FormatException("Invalid inventory slots.");
                    }
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
                    if (keys.Contains("maxResults")) { Integer("maxResults",1,128); keys.Remove("maxResults"); }
                    if (keys.Contains("typeIds"))
                    {
                        var ids=args.GetProperty("typeIds"); var unique=new HashSet<int>();
                        if (ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() is < 1 or > 32) throw new FormatException("Invalid entity typeIds.");
                        foreach (var id in ids.EnumerateArray())
                            if (!id.TryGetInt32(out int v) || v is < 0 or > 65535 || !unique.Add(v)) throw new FormatException("Invalid entity typeIds.");
                        if (args.GetProperty("category").GetString() is not ("npc" or "item")) throw new FormatException("typeIds requires npc/item.");
                        keys.Remove("typeIds");
                    }
                    if (keys.Contains("npcFilter"))
                    {
                        if (args.GetProperty("category").GetString() != "npc" || args.GetProperty("npcFilter").ValueKind != JsonValueKind.String
                            || args.GetProperty("npcFilter").GetString() is not ("all" or "hostile_creature" or "friendly_npc" or "critter" or "unknown"))
                            throw new FormatException("Invalid NPC filter.");
                        keys.Remove("npcFilter");
                    }
                    Fields("category", "radius"); Number(args.GetProperty("radius"), 1, 1600);
                    if (args.GetProperty("category").GetString() is not ("all" or "npc" or "item" or "player")) throw new FormatException("Invalid category."); break;
                default: throw new FormatException("Unsupported query.");
            }
            return;
        }
        switch (op)
        {
            case "transfer_inventory_item": case "swap_inventory_slots": case "hold_inventory_item":
                if (op == "transfer_inventory_item") { Fields("sourceSlot","destinationSlot","sourceToken","destinationToken","count"); Integer("count",1,9999); }
                else if (op == "hold_inventory_item") { Fields("sourceSlot","hotbarKey","sourceToken","destinationToken"); Integer("hotbarKey",0,9); }
                else Fields("sourceSlot","destinationSlot","sourceToken","destinationToken");
                Integer("sourceSlot",0,49);
                if (op != "hold_inventory_item")
                {
                    Integer("destinationSlot",0,49);
                    if (args.GetProperty("sourceSlot").GetInt32() == args.GetProperty("destinationSlot").GetInt32()) throw new FormatException("Inventory slots must differ.");
                }
                foreach(string name in new[] {"sourceToken","destinationToken"})
                {
                    var token=args.GetProperty(name);
                    if (token.ValueKind != JsonValueKind.String || token.GetString().Length != 64
                        || token.GetString().Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f'))) throw new FormatException("Invalid inventory token.");
                }
                break;
            case "move_input":
                Fields("horizontal", "vertical", "jumpHoldUpdates", "maxUpdates");
                if(args.GetProperty("horizontal").GetString() is not ("none" or "left" or "right")
                    ||args.GetProperty("vertical").GetString() is not ("none" or "up" or "down"))throw new FormatException("Invalid input direction.");
                Integer("maxUpdates",1,600);Integer("jumpHoldUpdates",0,args.GetProperty("maxUpdates").GetInt32());
                break;
            case "navigate_to_landing_region":
                Fields("minPlayerLeftX", "maxPlayerLeftX", "surfaceY");
                foreach (string key in keys) Number(args.GetProperty(key), 0, 1000000);
                if (args.GetProperty("minPlayerLeftX").GetDouble() > args.GetProperty("maxPlayerLeftX").GetDouble())
                    throw new FormatException("Landing X bounds must be ordered.");
                break;
            case "navigate_to":
                Fields("x","y");
                foreach(string key in keys)Number(args.GetProperty(key),0,1000000);
                break;
            case "navigate_to_x": Fields("targetX"); Number(args.GetProperty("targetX"), 0, 1000000); break;
            case "craft_recipe":
                Fields("recipeId","recipeToken","crafts");Integer("recipeId",0,100000);Integer("crafts",1,20);
                var recipeToken=args.GetProperty("recipeToken");
                if(recipeToken.ValueKind!=JsonValueKind.String||recipeToken.GetString().Length!=64||recipeToken.GetString().Any(c=>!(c is >= '0' and <= '9' or >= 'a' and <= 'f')))throw new FormatException("Invalid recipe token.");
                break;
            case "drop_through_platform": Fields(); break;
            case "select_hotbar_slot": Fields("slot"); Integer("slot", 0, 9); break;
            case "place_object": case "place_rope": goto case "place_tile";
            case "mine_target":
            {
                if(!keys.Contains("target")){Fields("x","y");Integer("x",0,100000);Integer("y",0,100000);break;}
                Fields("target");
                var mineTarget=args.GetProperty("target");
                string[] mineTargetKeys={"kind","x","y","typeId","worldSessionId","actorGeneration","actorSlot","observedGameUpdate","observedAtUnixMs","targetToken"};
                if(mineTarget.ValueKind!=JsonValueKind.Object||!mineTarget.EnumerateObject().Select(f=>f.Name).OrderBy(k=>k).SequenceEqual(mineTargetKeys.OrderBy(k=>k)))throw new FormatException("Invalid observed mine target");
                if(new[]{"kind","worldSessionId","targetToken"}.Any(k=>mineTarget.GetProperty(k).ValueKind!=JsonValueKind.String))throw new FormatException("Invalid target string");
                if(mineTarget.GetProperty("kind").GetString()!="tile"||CharacterCount(mineTarget.GetProperty("worldSessionId")) is <1 or >128
                    ||!System.Text.RegularExpressions.Regex.IsMatch(mineTarget.GetProperty("targetToken").GetString(),"^[0-9a-f]{64}$"))throw new FormatException("Invalid target binding");
                foreach(string key in new[]{"x","y","typeId","actorSlot","actorGeneration","observedGameUpdate","observedAtUnixMs"})
                    if(!mineTarget.GetProperty(key).TryGetInt64(out long value)||value<0||value>9007199254740991L)throw new FormatException("Invalid target field");
                if(mineTarget.GetProperty("x").GetInt64()>100000||mineTarget.GetProperty("y").GetInt64()>100000||mineTarget.GetProperty("typeId").GetInt64()>65535||mineTarget.GetProperty("actorSlot").GetInt64()>254)throw new FormatException("Invalid target range");
                break;
            }
            case "mine_tile": case "place_tile": case "interact_tile": case "mine_wall": case "place_wall": case "hammer_tile": Fields("x", "y"); Integer("x", 0, 100000); Integer("y", 0, 100000); break;
            case "attack_entity": Fields("entityRef"); if (args.GetProperty("entityRef").ValueKind != JsonValueKind.String || CharacterCount(args.GetProperty("entityRef")) is < 1 or > 150) throw new FormatException("Invalid entityRef."); break;
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
