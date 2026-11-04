using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.GameContent;

namespace CompanionBridge.Common.Systems;

// Game-thread read-only projections. These checks never authorize or start an action.
internal static class AtomicQueries
{
    internal static string Key(string kind, int type) => kind switch {
        "tile" => TileLoader.GetTile(type)?.FullName ?? "Terraria/" + TileID.Search.GetName(type),
        "wall" => WallLoader.GetWall(type)?.FullName ?? "Terraria/" + WallID.Search.GetName(type),
        _ => ContentSamples.ItemsByType.TryGetValue(type, out var i)
            ? i.ModItem?.FullName ?? "Terraria/" + ItemID.Search.GetName(type) : null
    };

    internal static object Resolve(JsonElement args)
    {
        string kind = args.GetProperty("kind").GetString(), query = args.GetProperty("query").GetString();
        bool search = args.TryGetProperty("mode", out var mode) && mode.GetString() == "search";
        int limit = args.TryGetProperty("maxResults", out var cap) ? cap.GetInt32() : 16;
        bool Match(string value) => value != null && (search ? value.Contains(query, StringComparison.OrdinalIgnoreCase)
            : string.Equals(value, query, StringComparison.OrdinalIgnoreCase));
        var matches = new List<(int Id, string Key, string Name)>();
        var ids = kind == "item" ? ContentSamples.ItemsByType.Keys.OrderBy(i => i)
            : Enumerable.Range(0, kind == "tile" ? TileLoader.TileCount : WallLoader.WallCount);
        foreach (int id in ids)
        {
            if (kind == "item" && ContentSamples.ItemsByType[id].IsAir) continue;
            string key = Key(kind, id), name = kind == "item" ? ContentSamples.ItemsByType[id].Name : key?.Split('/').Last();
            if (Match(key) || Match(name)) matches.Add((id, key, name));
        }
        return new { status = matches.Count == 0 ? "not_found" : !search && matches.Count > 1 ? "ambiguous" : "ok",
            kind, mode = search ? "search" : "exact", matchedCount = matches.Count,
            returnedCount = Math.Min(matches.Count, limit), truncated = matches.Count > limit,
            candidates = matches.Take(limit).Select(m => new { typeId = m.Id, contentKey = m.Key, name = m.Name,
                nameSource = kind == "item" ? "localized_item_name" : "content_symbol",
                creates = kind == "item" ? (object)new { tileType = ContentSamples.ItemsByType[m.Id].createTile,
                    wallType = ContentSamples.ItemsByType[m.Id].createWall } : null }).ToArray(),
            scope = "Loaded content registry, not presence in world or inventory; numeric IDs valid only for this loaded content set" };
    }

    internal static object PlayerStatus(object snapshot)
    {
        var source = JsonSerializer.SerializeToElement(snapshot);
        var result = source.EnumerateObject().Where(p => p.Name is not ("inventory" or "equipment" or "dyes"
            or "miscEquipment" or "miscDyes" or "buffs" or "hotbarKeyBindings" or "hotbarInventorySlots"))
            .ToDictionary(p => p.Name, p => (object)p.Value);
        var inventory = source.GetProperty("inventory").EnumerateArray().ToArray();
        int selected = source.GetProperty("selectedSlot").GetInt32();
        result["selectedItem"] = inventory.FirstOrDefault(i => i.GetProperty("slot").GetInt32() == selected);
        result["inventorySummary"] = new { totalSlots = inventory.Length,
            occupiedSlots = inventory.Count(i => !i.GetProperty("empty").GetBoolean()),
            emptySlots = inventory.Count(i => i.GetProperty("empty").GetBoolean()),
            scope = "player.inventory; excludes equipment, banks, chest and mouse item" };
        foreach (string name in new[] { "equipment", "miscEquipment" })
            result[name] = source.GetProperty(name).EnumerateArray().Where(i => !i.GetProperty("empty").GetBoolean()).ToArray();
        result["buffs"] = source.GetProperty("buffs").EnumerateArray().Where(b =>
            b.GetProperty("typeId").GetInt32() > 0 && b.GetProperty("timeTicks").GetInt32() > 0).ToArray();
        result["scope"] = "Compact local player; nonempty vanilla equipment/social/misc slots and all active buffs; dyes and other inventory items omitted";
        result["customState"] = "unknown; Mod accessory slots and custom player/item/buff state not read";
        return result;
    }

    internal static object InspectTile(Player p, int x, int y, Func<Item,int,string> token = null)
    {
        var cell = ObservationReader.ReadCell(x, y, new());
        if (cell.Knowledge != "read") return new { x, y, cell, preflight = (object)null,
            preflightStatus = "unknown_target" };
        int type = cell.HasTile ? cell.TileType ?? -1 : -1;
        var target = new Vector2(x * 16 + 8, y * 16 + 8);
        var held = p.HeldItem;
        return new { x, y, cell,
            content = type >= 0 ? Key("tile", type) : null,
            wallContent = cell.WallType > 0 ? Key("wall", cell.WallType.Value) : null,
            preflight = new {
                distancePixels = Vector2.Distance(p.Center, target),
                withinConservativeReach = Vector2.Distance(p.Center, target) <= 80,
                reachPixels = 80, normalGravity = p.gravDir == 1,
                selectedItem = token == null ? AtomicOperations.CompactItem(held, p.selectedItem) : AtomicOperations.CompactItem(held, p.selectedItem, token),
                checks = new { tilePresent = cell.HasTile, wallPresent = cell.WallType > 0,
                    pickSelected = !held.IsAir && held.pick > 0,
                    pureHammerSelected = !held.IsAir && held.hammer > 0 && held.pick == 0 && held.axe == 0,
                    consumableTileSelected = !held.IsAir && held.consumable && held.createTile >= 0,
                    consumableWallSelected = !held.IsAir && held.consumable && held.createWall > 0,
                    supportedInteractionType = type is TileID.ClosedDoor or TileID.OpenDoor or TileID.Containers
                        or TileID.Containers2 or TileID.Switches or TileID.Lever,
                    vanillaNonFrameImportantTile = type >= 0 && TileLoader.GetTile(type) == null && !Main.tileFrameImportant[type] },
                limitations = "Preflight only: not proof of path, safety, line of sight, pick power, native permissions, object footprint, placement adjacency or server acceptance. Action validates again; re-observe effects." },
            preflightStatus = "partial_checks_only" };
    }

    internal static object ScanTiles(Player p, JsonElement args)
    {
        int minX = args.GetProperty("minX").GetInt32(), minY = args.GetProperty("minY").GetInt32();
        int maxX = args.GetProperty("maxX").GetInt32(), maxY = args.GetProperty("maxY").GetInt32();
        var types = args.GetProperty("tileTypes").EnumerateArray().Select(t => t.GetInt32()).ToHashSet();
        int limit = args.TryGetProperty("maxResults", out var cap) ? cap.GetInt32() : 128;
        var tiles = new List<(int X, int Y, int Type, float Distance)>(); int unread = 0;
        var objects = new Dictionary<(int, int), object>();
        for (int y = minY; y <= maxY; y++) for (int x = minX; x <= maxX; x++)
        {
            var cell = ObservationReader.ReadCell(x, y, objects);
            if (cell.Knowledge != "read") { unread++; continue; }
            if (cell.HasTile && cell.TileType.HasValue && types.Contains(cell.TileType.Value))
                tiles.Add((x, y, cell.TileType.Value, Vector2.Distance(p.Center, new(x*16+8, y*16+8))));
        }
        return new { items = tiles.OrderBy(t => t.Distance).ThenBy(t => t.Y).ThenBy(t => t.X).Take(limit)
                .Select(t => new { x=t.X, y=t.Y, typeId=t.Type, contentKey=Key("tile",t.Type), distancePixels=t.Distance }).ToArray(),
            matchedCount = tiles.Count, returnedCount = Math.Min(tiles.Count, limit), truncated = tiles.Count > limit,
            unknownCount = unread, complete = unread == 0,
            status = unread > 0 ? "partial_unloaded" : tiles.Count == 0 ? "not_found" : "ok",
            bounds = new { minX, minY, maxX, maxY }, scannedCount = (maxX-minX+1)*(maxY-minY+1),
            sort = "distance_from_player_center_then_y_x; nearest before truncation",
            coordinates = "tile cells; inclusive bounds; 16 pixels per cell; +X right,+Y down" };
    }

    internal static object ScanEntities(Player p, JsonElement args, Func<NPC,string> entityRef, bool authoritative = false)
    {
        string category = args.GetProperty("category").GetString();
        float radius = args.GetProperty("radius").GetSingle();
        int limit = args.TryGetProperty("maxResults", out var cap) ? cap.GetInt32() : 128;
        string filter = args.TryGetProperty("npcFilter", out var f) ? f.GetString() : "all";
        var types = args.TryGetProperty("typeIds", out var ts) ? ts.EnumerateArray().Select(t => t.GetInt32()).ToHashSet() : null;
        var list = new List<(float Distance, string Kind, int Slot, object Value)>();
        void Add(Entity e, string kind, int slot, int? typeId, string name, string classification, string basis, object details)
        {
            float distance = Vector2.Distance(p.Center, e.Center);
            if (distance > radius || types != null && (!typeId.HasValue || !types.Contains(typeId.Value))) return;
            if (kind == "npc" && filter != "all" && filter != classification) return;
            list.Add((distance,kind,slot,new { kind, slot, typeId, name, classification, classificationBasis=basis,
                centerWorldPixels = new StateVector(e.Center.X,e.Center.Y),
                velocityPixelsPerTick = new StateVector(e.velocity.X,e.velocity.Y), distancePixels=distance, details }));
        }
        if (category is "all" or "npc") foreach (var n in Main.npc) if (n.active)
        {
            var evidence = new NpcEvidence(n.friendly, n.townNPC || NPCID.Sets.ActsLikeTownNPC[n.type],
                NPCID.Sets.CountsAsCritter[n.type], n.damage, n.CanBeChasedBy(), n.ModNPC != null, NPCID.Sets.ProjectileNPC[n.type]);
            var c = EntityCollector.Classify(evidence);
            Add(n,"npc",n.whoAmI,n.type,n.GivenOrTypeName,c.Kind,c.Basis,
                new { entityRef=entityRef(n),n.type,n.life,n.lifeMax,n.friendly,n.dontTakeDamage,chaseable=n.CanBeChasedBy(),evidence });
        }
        if (category is "all" or "player") foreach (var other in Main.player) if (other.active && other.whoAmI != p.whoAmI)
            Add(other,"player",other.whoAmI,null,other.name,"other_player","PvP hostility not evaluated",new {other.dead,other.statLife,other.team});
        if (category is "all" or "item") for (int i=0;i<Main.item.Length;i++)
        {
            var item=Main.item[i]; if (item.active && !item.IsAir)
                Add(item,"item",i,item.type,item.Name,"dropped_item","Active item; not proof of pickup eligibility",
                    new {item.type,item.stack,contentKey=Key("item",item.type)});
        }
        return new { items=list.OrderBy(e=>e.Distance).ThenBy(e=>e.Kind,StringComparer.Ordinal).ThenBy(e=>e.Slot).Take(limit).Select(e=>e.Value).ToArray(),
            matchedCount=list.Count, returnedCount=Math.Min(list.Count,limit),truncated=list.Count>limit,radiusPixels=radius,
            sort="distance_from_player_center_then_kind_slot; filters before truncation",
            complete=true, status=list.Count == 0 ? "not_found" : "ok",
            originWorldPixels=new StateVector(p.Center.X,p.Center.Y),
            scope=authoritative ? "Server active entities within radius; no line-of-sight test; hostility is inference, not current threat"
                : "Client-known entities only; synchronization age/visibility unknown; hostility is conservative inference, not current threat" };
    }
}
