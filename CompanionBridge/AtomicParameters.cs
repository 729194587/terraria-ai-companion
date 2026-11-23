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
        try { Validate(op == "interact_tile" ? "activate_observed_tile" : op, args, false); return op == "use_item" && args.TryGetProperty("target", out _) ? "targeted_use_not_supported" : null; }
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
        void EntityRef()
        {
            var value = args.GetProperty("entityRef");
            if (value.ValueKind != JsonValueKind.String || CharacterCount(value) is < 1 or > 150) throw new FormatException("Invalid entityRef.");
        }
        void Token(string key)
        {
            var token = args.GetProperty(key);
            if (token.ValueKind != JsonValueKind.String || token.GetString().Length != 64
                || token.GetString().Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
                throw new FormatException("Invalid item token.");
        }
        void Number(JsonElement v, double min, double max) { if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double n) || !double.IsFinite(n) || n < min || n > max) throw new FormatException("Invalid finite coordinate/radius."); }
        if (query)
        {
            switch (op)
            {
                case "list_pylons": case "list_teammates": case "discover_bosses": case "query_combat_loadout": case "inspect_container": case "observe_terrain": case "get_player_state": case "get_world_state": case "get_player_status": Fields(); break;
                case "observe_scene":
                    if(keys.Except(new[]{"maxObjects","maxRegions","maxPlacements","maxHazards","layers","scale","width","height"}).Any())throw new FormatException("Unexpected scene parameter.");
                    foreach(string key in keys.Where(k=>k is "maxObjects" or "maxRegions" or "maxPlacements" or "maxHazards"))Integer(key,1,key=="maxHazards"?64:key=="maxRegions"?24:32);
                    SceneLayers.Read(args);
                    break;
                case "query_map":
                    if(keys.Except(new[]{"mode","centerX","centerY","width","height","cellSize","includeChunks"}).Any()||!keys.Contains("mode"))throw new FormatException("Invalid map parameters.");
                    if(keys.Contains("includeChunks"))args.GetProperty("includeChunks").GetBoolean();
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
                case "plan_materials": Fields("typeId","count"); Integer("typeId",1,int.MaxValue); Integer("count",1,int.MaxValue); break;
                case "query_item_sources":
                case "query_item_info": Fields("typeId"); Integer("typeId", 1, int.MaxValue); break;
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
                case "quote_reforge":
                case "quote_npc_sale":
                    Fields("entityRef","sourceSlot","sourceToken"); EntityRef(); Integer("sourceSlot",0,49); Token("sourceToken"); break;
                case "quote_npc_purchase":
                    if (keys.Contains("quantity")) { Fields("entityRef","itemType","quantity"); Integer("quantity",1,50); }
                    else Fields("entityRef","itemType");
                    EntityRef(); Integer("itemType",1,int.MaxValue); break;
                case "quote_nurse_heal":
                case "inspect_npc_shop":
                case "inspect_npc_housing": Fields("entityRef"); EntityRef(); break;
                case "inspect_housing":
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
            case "open_container": Fields("x", "y"); Integer("x", 0, 100000); Integer("y", 0, 100000); break;
            case "deposit_container_item": case "withdraw_container_item":
                Fields("inventorySlot", "containerSlot", "count", "inventoryToken", "containerToken", "slotToken");
                Integer("inventorySlot", 0, 49); Integer("containerSlot", 0, 39); Integer("count", 1, 9999);
                Token("inventoryToken"); Token("containerToken"); Token("slotToken"); break;
            case "sort_inventory": case "quick_stack_nearby": Fields(); break;
            case "sort_container": case "quick_stack_container": case "loot_all": case "deposit_all": case "restock":
                Fields("containerToken"); Token("containerToken"); break;
            case "close_container": Fields("containerToken"); Token("containerToken"); break;
            case "configure_combat_loadout":
                if(keys.Count==0)break;
                foreach(var role in args.EnumerateObject())
                {
                    if(role.Name is not ("primary" or "secondary" or "summon"))throw new FormatException("Unknown weapon role");
                    Validate("combat_primary_role",role.Value,false);
                }
                break;
            case "combat_primary_role":
                Fields("hotbarKey","expectedTypeId","inventoryToken");
                Integer("hotbarKey",0,9); Integer("expectedTypeId",1,int.MaxValue); Token("inventoryToken");
                break;
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
                Token("sourceToken"); Token("destinationToken");
                break;
            case "equip_item": case "unequip_item":
                if (op == "equip_item")
                {
                    Fields("sourceSlot", "sourceToken", "targetSlotId", "targetToken");
                    Integer("sourceSlot", 0, 49); Token("sourceToken");
                }
                else if (keys.Contains("destinationSlot"))
                {
                    Fields("targetSlotId", "targetToken", "destinationSlot");
                    Integer("destinationSlot", 0, 49);
                }
                else Fields("targetSlotId", "targetToken");
                var targetSlot = args.GetProperty("targetSlotId");
                if (targetSlot.ValueKind != JsonValueKind.String || CharacterCount(targetSlot) is < 1 or > 256)
                    throw new FormatException("Invalid equipment slot ID.");
                Token("targetToken");
                break;
            case "move_input":
                Fields("horizontal", "vertical", "jumpHoldUpdates", "maxUpdates");
                if(args.GetProperty("horizontal").GetString() is not ("none" or "left" or "right")
                    ||args.GetProperty("vertical").GetString() is not ("none" or "up" or "down"))throw new FormatException("Invalid input direction.");
                Integer("maxUpdates",1,600);Integer("jumpHoldUpdates",0,args.GetProperty("maxUpdates").GetInt32());
                break;
            case "move_on_rope":
                Fields("ropeX","targetFeetY","finish","maxUpdates");
                Integer("ropeX",0,100000);Number(args.GetProperty("targetFeetY"),0,1000000);
                Integer("maxUpdates",1,360);
                if(args.GetProperty("finish").GetString() is not ("hold" or "land"))
                    throw new FormatException("Invalid rope finish.");
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
            case "manage_trash":
                Fields("mode","slot","slotToken","trashToken");Integer("slot",0,49);Token("slotToken");Token("trashToken");
                if(args.GetProperty("mode").GetString() is not ("discard" or "retrieve"))throw new FormatException("Invalid trash action.");
                break;
            case "switch_equipment_loadout":
                Fields("expectedLoadout","loadout");Integer("expectedLoadout",1,3);Integer("loadout",1,3);break;
            case "set_inventory_favorite":
                Fields("sourceSlot","sourceToken","favorited"); Integer("sourceSlot",0,49); Token("sourceToken");
                args.GetProperty("favorited").GetBoolean(); break;
            case "drop_inventory_item":
                if(keys.Contains("teammateRef"))
                {
                    Fields("sourceSlot","sourceToken","count","teammateRef");
                    if(!Guid.TryParseExact(args.GetProperty("teammateRef").GetString(),"N",out _))throw new FormatException("Invalid teammate reference.");
                }
                else Fields("sourceSlot","sourceToken","count");
                Integer("sourceSlot",0,49);Token("sourceToken");Integer("count",1,9999);break;
            case "open_inventory_item": Fields("sourceSlot","sourceToken"); Integer("sourceSlot",0,49); Token("sourceToken"); break;
            case "dismiss_summon": Fields("itemType"); Integer("itemType",1,int.MaxValue); break;
            case "reforge_item":
            case "sell_npc_item":
                Fields("entityRef","sourceSlot","sourceToken","quoteToken"); EntityRef(); Integer("sourceSlot",0,49); Token("sourceToken"); Token("quoteToken"); break;
            case "heal_at_nurse": Fields("entityRef","quoteToken"); EntityRef(); Token("quoteToken"); break;
            case "open_npc_dialogue": Fields("entityRef"); EntityRef(); break;
            case "close_npc_dialogue": Fields(); break;
            case "buy_npc_item":
                if (keys.Contains("quantity")) { Fields("entityRef","quoteToken","quantity"); Integer("quantity",1,1); }
                else Fields("entityRef","quoteToken");
                EntityRef(); Token("quoteToken"); break;
            case "assign_npc_home": Fields("entityRef","x","y"); EntityRef(); Integer("x",0,100000); Integer("y",0,100000); break;
            case "set_team": Fields("team"); Integer("team",0,5); break;
            case "teleport_to_pylon": Fields("pylonRef"); if(!Guid.TryParseExact(args.GetProperty("pylonRef").GetString(),"N",out _))throw new FormatException("Invalid pylon reference."); break;
            case "teleport_to_teammate": Fields("teammateRef"); if(!Guid.TryParseExact(args.GetProperty("teammateRef").GetString(),"N",out _))throw new FormatException("Invalid teammate reference."); break;
            case "open_door": case "close_door": case "place_item": case "place_object": case "place_rope": goto case "place_tile";
            case "explore_frontier":
                if(keys.Except(new[]{"radiusTiles","maxSegments","allowDig"}).Any())throw new FormatException("Unexpected exploration parameter.");
                Integer("radiusTiles",4,64);Integer("maxSegments",1,64);
                if(keys.Contains("allowDig"))args.GetProperty("allowDig").GetBoolean();break;
            case "collect_drops":
                if (keys.Except(new[]{"radiusPixels","typeIds","entityRef"}).Any()) throw new FormatException("Unexpected collect_drops parameter.");
                if (keys.Contains("radiusPixels")) Integer("radiusPixels", 16, 960);
                if (keys.Contains("entityRef"))
                {
                    var reference = args.GetProperty("entityRef");
                    if (reference.ValueKind != JsonValueKind.String || CharacterCount(reference) is < 1 or > 128 || keys.Contains("typeIds"))
                        throw new FormatException("Invalid drop reference.");
                }
                if (keys.Contains("typeIds"))
                {
                    var types = args.GetProperty("typeIds"); var unique = new HashSet<int>();
                    if (types.ValueKind != JsonValueKind.Array || types.GetArrayLength() is < 1 or > 16)
                        throw new FormatException("Invalid drop types.");
                    foreach (var type in types.EnumerateArray())
                        if (!type.TryGetInt32(out int value) || value is < 1 or > 65535 || !unique.Add(value))
                            throw new FormatException("Invalid drop type.");
                }
                break;
            case "activate_observed_tile":
                Fields("target");
                goto case "mine_target";
            case "mine_target":
            {
                bool hasCluster=args.TryGetProperty("cluster",out var cluster);
                if(hasCluster&&cluster.ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw new FormatException("Expected boolean cluster.");
                if(!keys.Contains("target"))
                {
                    Fields(hasCluster?new[]{"x","y","cluster"}:new[]{"x","y"});
                    Integer("x",0,100000);Integer("y",0,100000);break;
                }
                Fields(hasCluster?new[]{"target","cluster"}:new[]{"target"});
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
            case "boss_encounter": case "combat_attack": case "attack_entity":
                if(op=="combat_attack" && keys.Remove("button") && args.GetProperty("button").GetString() is not ("left" or "right"))throw new FormatException("Invalid weapon button.");
                if(op=="boss_encounter" && keys.Remove("regroupTeammateRef") && !Guid.TryParseExact(args.GetProperty("regroupTeammateRef").GetString(),"N",out _))throw new FormatException("Invalid regroup reference.");
                if(op=="boss_encounter" && keys.Contains("durationSeconds"))
                { Fields("entityRef","durationSeconds"); Integer("durationSeconds",1,600); }
                else Fields("entityRef");
                if (args.GetProperty("entityRef").ValueKind != JsonValueKind.String || CharacterCount(args.GetProperty("entityRef")) is < 1 or > 150) throw new FormatException("Invalid entityRef."); break;
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
