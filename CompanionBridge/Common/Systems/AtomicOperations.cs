using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.GameContent;
using Terraria.ObjectData;
using CompanionBridge.Motion;

namespace CompanionBridge.Common.Systems;

// A fresh GlobalNPC instance is attached on SetDefaults, including client slot reuse.
public sealed class AtomicNpcIdentity : GlobalNPC
{
    public override bool InstancePerEntity => true;
    internal Guid Token = Guid.NewGuid();
    public override GlobalNPC Clone(NPC from, NPC to)
    {
        var clone = (AtomicNpcIdentity)base.Clone(from, to);
        clone.Token = Guid.NewGuid();
        return clone;
    }
}

internal static class AtomicOperations
{
    private static int _action, _slot, _itemType, _stack, _tileType, _npcLife;
    private static int _wallType, _slope, _frameX, _frameY;
    private static bool _half;
    private static bool _used, _aimSaved, _inputInjected, _explicitUseTarget;
    private static Vector2 _target;
    private static int _mouseX, _mouseY, _targetX, _targetY;
    private static NPC _npc;
    private static PlatformDropAction _drop;
    private static bool _downInjected;
    private static string Ref(NPC n) => ObservationReader.WorldSessionId + ":npc:" + n.whoAmI + ":" + n.GetGlobalNPC<AtomicNpcIdentity>().Token;
    private static void Finish(string status, string reason, object evidence = null) =>
        CompanionBridge.Controls.FinishAtomic(_action, status, reason, evidence,
            new(true, new(Main.LocalPlayer.position.X, Main.LocalPlayer.position.Y), new(Main.LocalPlayer.velocity.X, Main.LocalPlayer.velocity.Y), Main.LocalPlayer.statLife, Main.LocalPlayer.statLifeMax2));
    private static bool Loaded(int x, int y) => WorldGen.InWorld(x, y, 1)
        && (Main.netMode != NetmodeID.MultiplayerClient || Main.sectionManager?.TileLoaded(x, y) == true);
    private static bool Reach(Player p, Vector2 target) => Vector2.Distance(p.Center, target) <= 80;

    internal static object Query(ControlRequest request)
    {
        AtomicParameters.Validate(request.Operation, request.Arguments, true);
        if (Main.gameMenu || !Main.LocalPlayer.active || ObservationReader.WorldSessionId == null)
            return new { available = false, reason = "not_in_world" };
        var p = Main.LocalPlayer;
        object data;
        var a = request.Arguments;
        switch (request.Operation)
        {
            case "get_player_state": data = ObservationReader.ReadPlayer(p); break;
            case "inspect_inventory": data = Inventory(p, a); break;
            case "scan_tiles":
                int minX = a.GetProperty("minX").GetInt32(), minY = a.GetProperty("minY").GetInt32();
                int maxX = a.GetProperty("maxX").GetInt32(), maxY = a.GetProperty("maxY").GetInt32();
                if (maxX >= Main.maxTilesX || maxY >= Main.maxTilesY)
                    return new { available = false, reason = "scan_out_of_world" };
                var types = a.GetProperty("tileTypes").EnumerateArray().Select(t => t.GetInt32()).ToHashSet();
                if (types.Any(t => t >= TileLoader.TileCount)) return new { available = false, reason = "invalid_tile_type" };
                int limit = a.TryGetProperty("maxResults", out var cap) ? cap.GetInt32() : 128;
                var tiles = new List<object>(); int matches = 0, unread = 0;
                var objects = new Dictionary<(int, int), object>();
                for (int sy = minY; sy <= maxY; sy++) for (int sx = minX; sx <= maxX; sx++)
                {
                    var cell = ObservationReader.ReadCell(sx, sy, objects);
                    if (cell.Knowledge != "read") { unread++; continue; }
                    if (!cell.HasTile || !cell.TileType.HasValue || !types.Contains(cell.TileType.Value)) continue;
                    matches++;
                    if (tiles.Count < limit) tiles.Add(new { x = sx, y = sy, typeId = cell.TileType.Value });
                }
                data = new { items = tiles, matchedCount = matches, returnedCount = tiles.Count, truncated = matches > tiles.Count,
                    unknownCount = unread, complete = unread == 0,
                    status = unread > 0 ? "partial_unloaded" : matches == 0 ? "not_found" : "ok",
                    bounds = new { minX, minY, maxX, maxY }, scannedCount = (maxX-minX+1)*(maxY-minY+1),
                    coordinates = "tile cells; inclusive bounds; 16 pixels per cell; +X right,+Y down" }; break;
            case "get_world_state": data = new { environment = ObservationReader.ReadEnvironment(p),
                depth = new { tileY = p.Center.Y / 16, Main.worldSurface, Main.rockLayer, Main.maxTilesX, Main.maxTilesY },
                depthLayer = p.ZoneUnderworldHeight ? "underworld" : p.ZoneRockLayerHeight ? "cavern" : p.ZoneDirtLayerHeight ? "underground" : p.ZoneSkyHeight ? "space" : "surface" }; break;
            case "inspect_tile":
                int x = a.GetProperty("x").GetInt32(), y = a.GetProperty("y").GetInt32();
                data = new { x, y, cell = ObservationReader.ReadCell(x, y, new()) }; break;
            case "scan_entities":
                string category = a.GetProperty("category").GetString(); float radius = a.GetProperty("radius").GetSingle();
                var list = new List<object>(); int matched = 0;
                void Add(Entity e, string kind, int slot, string name, object details)
                {
                    float distance = Vector2.Distance(p.Center, e.Center);
                    if (distance > radius) return;
                    matched++;
                    if (list.Count < 128) list.Add(new { kind, slot, name, centerWorldPixels = new StateVector(e.Center.X, e.Center.Y), distancePixels = distance, details });
                }
                if (category is "all" or "npc") foreach (var n in Main.npc) if (n.active)
                    Add(n, "npc", n.whoAmI, n.GivenOrTypeName, new { entityRef = Ref(n), n.type, n.life, n.lifeMax, n.friendly, n.dontTakeDamage, chaseable = n.CanBeChasedBy() });
                if (category is "all" or "player") foreach (var other in Main.player) if (other.active && other.whoAmI != Main.myPlayer)
                    Add(other, "player", other.whoAmI, other.name, new { other.dead, other.statLife, other.team });
                if (category is "all" or "item") for (int i = 0; i < Main.item.Length; i++) { var item = Main.item[i]; if (item.active && !item.IsAir) Add(item, "item", i, item.Name, new { item.type, item.stack }); }
                data = new { items = list, matchedCount = matched, truncated = matched > list.Count, radiusPixels = radius, scope = "client-known entities; synchronization age/visibility unknown" }; break;
            default: throw new ArgumentException("Unsupported query");
        }
        return new { available = true, worldSessionId = ObservationReader.WorldSessionId, gameUpdate = Main.GameUpdateCount, data };
    }

    private static object CompactItem(Item item, int slot) => new { slot, hotbarKey = HotbarSlots.KeyForInventorySlot(slot),
        typeId = item.type, contentKey = item.ModItem?.FullName ?? "Terraria/" + ItemID.Search.GetName(item.type),
        name = item.Name, stack = item.IsAir ? 0 : item.stack,
        use = new { item.pick, item.axe, item.hammer, item.createTile, item.createWall } };

    // Project only this tool's response; the shared full snapshot stays unchanged.
    internal static object FullInventory(object snapshot)
    {
        var source = System.Text.Json.JsonSerializer.SerializeToElement(snapshot);
        var result = source.EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value);
        foreach (string name in new[] { "inventory", "equipment", "dyes", "miscEquipment", "miscDyes" })
            result[name] = source.GetProperty(name).EnumerateArray().Select(item =>
                item.GetProperty("empty").GetBoolean()
                    ? (object)new { slot = item.GetProperty("slot").GetInt32(), empty = true }
                    : item).ToArray();
        result["buffs"] = source.GetProperty("buffs").EnumerateArray()
            .Where(buff => buff.GetProperty("typeId").GetInt32() > 0
                        && buff.GetProperty("timeTicks").GetInt32() > 0).ToArray();
        result["buffScope"] = "Complete active buffs in player.buffType/buffTime; empty/inactive slots omitted; slot indices retained";
        result["scope"] = "All slots of player.inventory, armor, dye, miscEquips, miscDyes; includes vanilla social slots; excludes banks, chests, mouse item and Mod accessory slots";
        result["customState"] = "unknown; Mod custom player/item/buff state and Mod accessory slots not read";
        return result;
    }

    private static object Inventory(Player p, System.Text.Json.JsonElement args)
    {
        string mode = args.TryGetProperty("mode", out var value) ? value.GetString() : "selected";
        if (mode == "full") return FullInventory(ObservationReader.ReadPlayer(p));
        if (mode == "item")
        {
            string wanted = args.GetProperty("item").GetString();
            var candidates = ContentSamples.ItemsByType.Values.Where(i => !i.IsAir &&
                (string.Equals(i.ModItem?.FullName ?? "Terraria/" + ItemID.Search.GetName(i.type), wanted, StringComparison.OrdinalIgnoreCase)
                || string.Equals(i.Name, wanted, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (candidates.Length != 1) return new { status = candidates.Length == 0 ? "unknown_item" : "ambiguous_item",
                candidates = candidates.Take(32).Select(i => new { typeId = i.type, contentKey = i.ModItem?.FullName ?? "Terraria/" + ItemID.Search.GetName(i.type), name = i.Name }).ToArray(),
                truncated = candidates.Length > 32 };
            int type = candidates[0].type;
            var slots = p.inventory.Select((i, slot) => (Item: i, Slot: slot)).Where(s => !s.Item.IsAir && s.Item.type == type).ToArray();
            return new { status = "ok", typeId = type,
                contentKey = candidates[0].ModItem?.FullName ?? "Terraria/" + ItemID.Search.GetName(type), name = candidates[0].Name,
                count = slots.Sum(s => s.Item.stack),
                slots = slots.Select(s => new { slot = s.Slot, hotbarKey = HotbarSlots.KeyForInventorySlot(s.Slot), stack = s.Item.stack }).ToArray(),
                scope = "player.inventory including hotbar, ammo, coins; excludes equipment, banks, chests, mouse item" };
        }
        var indices = mode == "hotbar" ? Enumerable.Range(0, Math.Min(10, p.inventory.Length)) : new[] { p.selectedItem };
        return new { status = "ok", selectedSlot = p.selectedItem, selectedHotbarKey = HotbarSlots.KeyForInventorySlot(p.selectedItem),
            inventory = indices.Select(i => CompactItem(p.inventory[i], i)).ToArray() };
    }

    internal static void Before(Player p, ControlRequest request)
    {
        var action = CompanionBridge.Controls.AtomicAction;
        if (action?.Status != "running") return;
        if (action.WorldSessionId != ObservationReader.WorldSessionId)
        {
            p.controlUseItem = false;
            CompanionBridge.Controls.FinishAtomic(action.ActionId, "cancelled", "world_session_changed", null); return;
        }
        bool first = _action != action.ActionId;
        var a = request.Arguments; string op = request.Operation;
        if (op == "drop_through_platform")
        {
            var terrain = TerrainReader.Read(p);
            var state = new PhysicsState(p.position.X,p.position.Y,p.velocity.X,p.velocity.Y,terrain.Grounded,p.jump,p.releaseJump);
            if(first) { _action=action.ActionId; _drop=new(); _drop.Begin(state,p.statLife,terrain.Supported,TerrainReader.ReadCell); }
            _drop.Before(state,p.statLife,TerrainReader.ReadCell);
            p.controlLeft=p.controlRight=p.controlJump=p.controlUp=p.controlUseItem=false;
            p.controlDown=_drop.Down; _downInjected=true;
            if(_drop.Status!="running") Finish(_drop.Status,_drop.Reason);
            return;
        }
        if (first)
        {
            _action = action.ActionId; _used = false; _npc = null;
            try { AtomicParameters.Validate(op, a, false); }
            catch (Exception e) when (e is FormatException or InvalidOperationException) { Finish("failed", "invalid_atomic_arguments"); return; }
            _explicitUseTarget = op == "use_item" && a.TryGetProperty("target", out _);
            _slot = p.selectedItem; _itemType = p.HeldItem.type; _stack = p.HeldItem.stack;
            _target = p.Center + new Vector2(p.direction * 32, 0);
            if (op is "navigate_to_x" or "navigate_to_landing_region")
            {
                if (op == "navigate_to_landing_region")
                {
                    var region = request.TargetRegion;
                    if (region == null || region.MinPlayerLeftX < 16 || region.MaxPlayerLeftX > (Main.maxTilesX - 1) * 16 - p.width
                        || region.SurfaceY < p.height + 16 || region.SurfaceY > (Main.maxTilesY - 1) * 16)
                        Finish("failed", "landing_region_out_of_world");
                    return;
                }
                if (request.TargetX < 16 || request.TargetX > (Main.maxTilesX - 2) * 16) Finish("failed", "navigation_target_out_of_world");
                return;
            }
            if (p.itemAnimation != 0 || p.itemTime != 0) { Finish("failed", "item_busy"); return; }
            if (op == "select_hotbar_slot")
            {
                int key=a.GetProperty("slot").GetInt32();
                p.selectedItem=HotbarSlots.ToInventorySlot(key);
                Finish(HotbarSlots.KeyForInventorySlot(p.selectedItem)==key ? "completed" : "failed", "selected_hotbar_key_verified",
                    new { selectedHotbarKey=HotbarSlots.KeyForInventorySlot(p.selectedItem), inventorySlot=p.selectedItem }); return;
            }
            if (op == "attack_entity")
            {
                if (p.HeldItem.damage <= 0 || p.HeldItem.createTile >= 0 || p.HeldItem.createWall >= 0)
                { Finish("failed", "selected_item_not_a_weapon"); return; }
                string reference = a.GetProperty("entityRef").GetString();
                _npc = Main.npc.FirstOrDefault(n => n.active && Ref(n) == reference);
                if (_npc == null || !_npc.CanBeChasedBy()) { Finish("failed", "invalid_or_stale_entity_reference"); return; }
                _npcLife = _npc.life;
            }
            if (op == "use_item" && a.TryGetProperty("target", out var target))
                _target = new(target.GetProperty("x").GetSingle(), target.GetProperty("y").GetSingle());
            if (op is "mine_tile" or "place_tile" or "interact_tile" or "mine_wall" or "place_wall" or "hammer_tile")
            {
                int x = a.GetProperty("x").GetInt32(), y = a.GetProperty("y").GetInt32();
                _target = new(x * 16 + 8, y * 16 + 8);
                if (!Loaded(x, y)) { Finish("failed", "tile_unloaded_or_out_of_world"); return; }
                _tileType = Main.tile[x, y].HasTile ? Main.tile[x, y].TileType : -1;
                var initial = Main.tile[x, y];
                _wallType = initial.WallType; _half = initial.IsHalfBlock; _slope = (int)initial.Slope;
                _frameX = initial.TileFrameX; _frameY = initial.TileFrameY;
                if (op is "mine_wall" or "place_wall" or "hammer_tile")
                {
                    if (p.HeldItem.ModItem != null) { Finish("failed", "unsupported_mod_item"); return; }
                    if (op != "place_wall" && (p.HeldItem.hammer <= 0 || p.HeldItem.pick > 0 || p.HeldItem.axe > 0))
                    { Finish("failed", "selected_item_requires_pure_hammer"); return; }
                    if (op != "place_wall" && (p.HeldItem.createTile >= 0 || p.HeldItem.createWall >= 0))
                    { Finish("failed", "unsupported_hammer_with_placement_effect"); return; }
                    if (op == "mine_wall" && _wallType == 0) { Finish("failed", "target_wall_missing"); return; }
                    if (op == "mine_wall" && _tileType >= 0) { Finish("failed", "unsupported_wall_behind_foreground_tile"); return; }
                    if (op == "place_wall" && (_wallType != 0 || p.HeldItem.createWall <= 0 || !p.HeldItem.consumable || p.HeldItem.createTile >= 0))
                    { Finish("failed", "requires_empty_wall_and_consumable_wall_item"); return; }
                    if (op == "place_wall") _placeType = p.HeldItem.createWall;
                    if (op == "hammer_tile" && (_tileType < 0 || _wallType != 0 || initial.IsActuated
                        || TileLoader.GetTile(_tileType) != null || Main.tileHammer[_tileType]
                        || (!Main.tileSolid[_tileType] && !TileID.Sets.Platforms[_tileType])
                        || (Main.tileFrameImportant[_tileType] && !TileID.Sets.Platforms[_tileType])))
                    { Finish("failed", "unsupported_hammer_target_requires_vanilla_block_or_platform_without_wall"); return; }
                }
                if (op == "mine_tile" && _tileType < 0)
                { Finish("failed", "target_tile_missing", new { targetTileX=x, targetTileY=y }); return; }
                if (op == "mine_tile" && p.HeldItem.pick <= 0)
                { Finish("failed", "selected_item_not_pickaxe", new { selectedHotbarKey=HotbarSlots.KeyForInventorySlot(p.selectedItem),
                    inventorySlot=p.selectedItem, selectedItemType=p.HeldItem.type, pickPower=p.HeldItem.pick, targetTileX=x, targetTileY=y, targetTileType=_tileType }); return; }
                if (op == "place_tile" && (_tileType >= 0 || p.HeldItem.createTile < 0 || !p.HeldItem.consumable
                    || Main.tileFrameImportant[p.HeldItem.createTile] || p.HeldItem.ModItem != null))
                { Finish("failed", "only_empty_cell_and_consumable_single_block_supported"); return; }
            }
            if (p.HeldItem.IsAir && op != "interact_tile") { Finish("failed", "selected_item_empty"); return; }
            p.releaseUseItem = true;
        }
        if (op is "navigate_to_x" or "navigate_to_landing_region") return;
        p.controlLeft = p.controlRight = p.controlJump = false;
        p.controlUseItem = false;
        if (p.selectedItem != _slot || p.HeldItem.type != _itemType && !p.HeldItem.IsAir)
        { Finish("failed", "selected_item_changed"); return; }
        if (op == "attack_entity")
        {
            if (_npc == null || _npc.whoAmI < 0 || _npc.whoAmI >= Main.npc.Length
                || !ReferenceEquals(Main.npc[_npc.whoAmI], _npc) || Ref(_npc) != a.GetProperty("entityRef").GetString()) { Finish("failed", "entity_slot_reused"); return; }
            if (_npc.life < _npcLife) { Finish("completed", "target_life_decreased", new { beforeLife = _npcLife, afterLife = _npc.life, _npc.active, attribution = "observed_effect; other_damage_sources_possible" }); return; }
            if (!_npc.active) { Finish("failed", "target_disappeared_without_damage_evidence"); return; }
            if (!_npc.CanBeChasedBy()) { Finish("failed", "target_no_longer_attackable"); return; }
            _target = _npc.Center;
        }
        int tx = (int)(_target.X / 16), ty = (int)(_target.Y / 16);
        if ((op != "use_item" || _explicitUseTarget) && !Loaded(tx, ty)) { Finish("failed", "target_unloaded_or_out_of_world"); return; }
        if ((op != "use_item" || _explicitUseTarget) && !Reach(p, _target))
        { Finish("failed", "out_of_conservative_80_pixel_reach", new { targetTileX = tx, targetTileY = ty,
            targetTileType = _tileType, maximumDistancePixels = 80 }); return; }
        if (AtomicMultiplayer.Before(p, action, request)) { p.controlUseItem = false; return; }
        if (op == "interact_tile")
        {
            Interact(p, tx, ty); return;
        }
        if (op is "mine_wall" or "place_wall" or "hammer_tile")
        {
            var tile = Main.tile[tx, ty];
            if (op == "mine_wall")
            {
                if (tile.WallType == 0 && !first) { Finish("completed", "wall_removed", new { tx, ty, beforeWallType = _wallType, afterWallType = 0, attribution = "observed_effect" }); return; }
                if (tile.WallType != _wallType) { Finish("failed", "target_wall_replaced"); return; }
                if (tile.HasTile) { Finish("failed", "foreground_tile_appeared"); return; }
                // Native wall targeting may choose a neighbour for enclosed unsafe walls.
                // CanPlayerSmashWall uses the same neighbourhood. Refuse before input.
                for (int x = tx-1; x <= tx+1; x++) for (int y = ty-1; y <= ty+1; y++)
                    if (!Loaded(x,y)) { Finish("failed", "wall_neighbourhood_unloaded"); return; }
                if (!Player.CanPlayerSmashWall(tx, ty)) { Finish("failed", "wall_not_smashable_by_native_rules"); return; }
            }
            if (op == "place_wall" && tile.WallType != 0)
            {
                int remaining = p.inventory[_slot].IsAir ? 0 : p.inventory[_slot].stack;
                bool placed = !first && tile.WallType == _placeType && remaining == _stack - 1;
                Finish(placed ? "completed" : "failed", placed ? "placed_wall_and_consumption_verified" : "wall_placement_not_verified",
                    new { tx, ty, beforeWallType = _wallType, afterWallType = (int)tile.WallType, beforeStack = _stack, remainingStack = remaining }); return;
            }
            if (op == "place_wall")
            {
                if (p.autoPaint) { Finish("failed", "unsupported_wall_auto_paint"); return; }
                // Vanilla also fills adjacent one-cell holes in the same ItemCheck.
                // Refuse those layouts rather than spending extra items off target.
                if (_stack > 1)
                {
                    for (int x=tx-2;x<=tx+2;x++) for(int y=ty-2;y<=ty+2;y++)
                        if (!Loaded(x,y)) { Finish("failed", "wall_placement_neighbourhood_unloaded"); return; }
                    var offsets = new[] { (-1,0),(1,0),(0,-1),(0,1) };
                    foreach(var (dx,dy) in offsets)
                    {
                        int x=tx+dx,y=ty+dy;
                        if (Main.tile[x,y].WallType!=0) continue;
                        if(offsets.All(d => x+d.Item1==tx && y+d.Item2==ty || Main.tile[x+d.Item1,y+d.Item2].WallType==_placeType))
                        { Finish("failed", "unsupported_native_neighbour_wall_autofill"); return; }
                    }
                }
            }
            if (op == "hammer_tile")
            {
                if (!tile.HasTile || tile.TileType != _tileType || tile.IsActuated || tile.WallType != 0)
                { Finish("failed", "hammer_target_invalidated"); return; }
                if (!first && (tile.IsHalfBlock != _half || (int)tile.Slope != _slope))
                { Finish("completed", "tile_shape_changed", new { tx, ty, typeId = _tileType,
                    before = new { halfBlock = _half, slope = _slope, frameX = _frameX, frameY = _frameY },
                    after = new { halfBlock = tile.IsHalfBlock, slope = (int)tile.Slope, frameX = (int)tile.TileFrameX, frameY = (int)tile.TileFrameY } }); return; }
            }
        }
        if (op == "mine_tile")
        {
            if (!Main.tile[tx, ty].HasTile) { Finish("completed", "tile_removed", new { tx, ty, beforeType = _tileType, attribution = "observed_effect" }); return; }
            if (Main.tile[tx, ty].TileType != _tileType) { Finish("failed", "target_tile_replaced"); return; }
        }
        if (op == "place_tile" && Main.tile[tx, ty].HasTile)
        {
            bool placed = Main.tile[tx, ty].TileType == _placeType && p.inventory[_slot].stack < _stack;
            Finish(placed ? "completed" : "failed", placed ? "placed_tile_and_consumption_verified" : "target_occupied", new { tx, ty, type = (int)Main.tile[tx, ty].TileType, remainingStack = p.inventory[_slot].stack }); return;
        }
        if (first && op != "place_wall") _placeType = p.HeldItem.createTile;
        if (op == "use_item" && _used && p.itemAnimation == 0)
        { Finish("completed", "native_item_use_cycle_finished", new { execution = "native_use_cycle_completed", effectStatus = "unconfirmed",
            effect = "item use cycle only; summon/heal/hit effects require separate observation" }); return; }
        if (p.HeldItem.IsAir) { Finish("failed", "item_exhausted_before_effect_verified"); return; }
        if ((op != "use_item" || _explicitUseTarget) && p.gravDir != 1) { Finish("failed", "item_aim_requires_normal_gravity"); return; }
        if (op is not ("use_item" or "mine_tile" or "place_tile" or "mine_wall" or "place_wall" or "hammer_tile") && Main.GameViewMatrix.Zoom != Vector2.One) { Finish("failed", "item_aim_requires_default_zoom"); return; }
        // One use cycle for generic use; mining/combat repeat until their effect or deadline.
        if (p.itemAnimation == 0 && p.itemTime == 0) p.releaseUseItem = true;
        p.controlUseItem = op != "use_item" || !_used;
        _inputInjected = true;
    }
    private static int _placeType;

    private static void Interact(Player p, int x, int y)
    {
        if (Main.netMode == NetmodeID.MultiplayerClient) { NativeInteraction(p,x,y); return; }
        if (Main.netMode != NetmodeID.SinglePlayer) { Finish("failed", "unsupported_interaction_net_mode"); return; }
        var tile = Main.tile[x,y];
        if (!tile.HasTile || tile.TileType != _tileType || tile.IsActuated)
        { Finish("failed", "interaction_target_invalidated"); return; }
        int type = tile.TileType;
        if (type is not (TileID.ClosedDoor or TileID.OpenDoor or TileID.Containers or TileID.Containers2 or TileID.Switches or TileID.Lever))
        { Finish("failed", "unsupported_interaction_type"); return; }
        var anchor = TileObjectData.TopLeft(x,y);
        int width = type == TileID.Switches || type == TileID.ClosedDoor ? 1 : 2;
        int height = type is TileID.ClosedDoor or TileID.OpenDoor ? 3 : type == TileID.Switches ? 1 : 2;
        for (int dx=0; dx<width; dx++) for (int dy=0; dy<height; dy++)
        {
            int cx=anchor.X+dx, cy=anchor.Y+dy;
            if (!Loaded(cx,cy)) { Finish("failed", "interaction_footprint_unloaded"); return; }
            var part = Main.tile[cx,cy];
            if (!part.HasTile || part.TileType != type || part.IsActuated)
            { Finish("failed", "invalid_interaction_footprint"); return; }
        }
        if (!p.InInteractionRange(x,y,Terraria.DataStructures.TileReachCheckSettings.Simple)) { Finish("failed", "out_of_native_interaction_range"); return; }
        object footprint = new { x = anchor.X, y = anchor.Y, width, height, interactionX = x, interactionY = y };
        if (type is TileID.ClosedDoor or TileID.OpenDoor)
        {
            bool changed = type == TileID.ClosedDoor ? WorldGen.OpenDoor(x,y,p.direction) : WorldGen.CloseDoor(x,y);
            int after = Main.tile[x,y].HasTile ? Main.tile[x,y].TileType : -1;
            bool verified = changed && after != type;
            Finish(verified ? "completed" : "failed", verified ? "door_state_changed" : "door_blocked_or_unchanged",
                new { beforeType = type, afterType = after, footprint }); return;
        }
        if (type is TileID.Containers or TileID.Containers2)
        {
            if (Chest.IsLocked(anchor.X,anchor.Y)) { Finish("failed", "locked_chest_unsupported"); return; }
            int index = Chest.FindChest(anchor.X,anchor.Y);
            if (index < 0) { Finish("failed", "chest_record_missing"); return; }
            int before = p.chest, expected = before == index ? -1 : index;
            NativeInteraction(p,x,y);
            bool verified = p.chest == expected && p.chestX == anchor.X && p.chestY == anchor.Y;
            Finish(verified ? "completed" : "failed", verified ? "chest_open_state_changed" : "chest_interaction_not_verified",
                new { beforeChest = before, afterChest = p.chest, chestIndex = index, open = p.chest == index, footprint }); return;
        }
        // Switches are 1x1 toggles in Terraria (TileID.Switches=136), not doors.
        // Levers are 2x2 and HitSwitch changes all four frames before TripWire.
        var frames = new List<(int X,int Y,int FrameX,int FrameY)>();
        for (int dx=0; dx<width; dx++) for (int dy=0; dy<height; dy++)
        {
            var part=Main.tile[anchor.X+dx,anchor.Y+dy];
            frames.Add((anchor.X+dx,anchor.Y+dy,part.TileFrameX,part.TileFrameY));
        }
        NativeInteraction(p,x,y);
        bool toggled = frames.All(f => Main.tile[f.X,f.Y].HasTile && Main.tile[f.X,f.Y].TileType == type
            && (Main.tile[f.X,f.Y].TileFrameX != f.FrameX || Main.tile[f.X,f.Y].TileFrameY != f.FrameY));
        Finish(toggled ? "completed" : "failed", toggled ? (type == TileID.Switches ? "switch_frame_toggled" : "lever_frames_toggled") : "wiring_interaction_not_verified",
            new { footprint, beforeFrames = frames.Select(f => new { x=f.X,y=f.Y,frameX=f.FrameX,frameY=f.FrameY }).ToArray(),
                afterFrames = frames.Select(f => new { x=f.X,y=f.Y,frameX=(int)Main.tile[f.X,f.Y].TileFrameX,frameY=(int)Main.tile[f.X,f.Y].TileFrameY }).ToArray(),
                scope = "native switch frame change; downstream circuit effects not verified" });
    }

    private static void NativeInteraction(Player p,int x,int y)
    {
        bool release=p.releaseUseTile, attempted=p.tileInteractAttempted;
        try { AtomicMultiplayer.BeginInput(); p.releaseUseTile=true; p.tileInteractAttempted=true; p.TileInteractionsCheck(x,y); }
        finally { AtomicMultiplayer.EndInput(); p.releaseUseTile=release; p.tileInteractAttempted=attempted; }
    }

    internal static void Aim(Player p)
    {
        var action = CompanionBridge.Controls.AtomicAction;
        if (action?.ActionId != _action || action.Status != "running" || action.WorldSessionId != ObservationReader.WorldSessionId || action.Operation is "navigate_to_x" or "navigate_to_landing_region" or "drop_through_platform")
        {
            if (_inputInjected) p.controlUseItem = false; // Disconnect/revocation may race between player hooks.
            _inputInjected = false;
            return;
        }
        // ItemCheck already runs in PlayerInput.SetZoom_World coordinates. Injecting
        // rendered pixels (or multiplying by Zoom) here would apply zoom twice.
        // Untargeted use preserves the native cursor, including inverted gravity.
        if (action.Operation == "use_item" && !_explicitUseTarget) return;
        AtomicMultiplayer.BeginInput();
        if (!ItemAim.TryGetItemCheckMouse(_target, Main.screenPosition, out int x, out int y))
        { p.controlUseItem = false; _inputInjected = false; Finish("failed", "invalid_item_aim_coordinates"); return; }
        _mouseX = Main.mouseX; _mouseY = Main.mouseY; _targetX = Player.tileTargetX; _targetY = Player.tileTargetY; _aimSaved = true;
        Main.mouseX = x; Main.mouseY = y;
        Player.tileTargetX = (int)(_target.X / 16); Player.tileTargetY = (int)(_target.Y / 16);
    }
    internal static void RestoreAim()
    {
        AtomicMultiplayer.EndInput();
        if (!_aimSaved) return;
        Main.mouseX = _mouseX; Main.mouseY = _mouseY; Player.tileTargetX = _targetX; Player.tileTargetY = _targetY; _aimSaved = false;
    }
    internal static void After(Player p)
    {
        RestoreAim();
        if (_downInjected) { p.controlDown=false; _downInjected=false; }
        if (_inputInjected) { p.controlUseItem = false; _inputInjected = false; }
        AtomicMultiplayer.After(p);
        var action = CompanionBridge.Controls.AtomicAction;
        if (action?.Status != "running") return;
        if(action.Operation=="drop_through_platform" && action.ActionId==_action)
        {
            var terrain=TerrainReader.Read(p);
            var state=new PhysicsState(p.position.X,p.position.Y,p.velocity.X,p.velocity.Y,terrain.Grounded,p.jump,p.releaseJump);
            _drop.After(state,p.statLife,TerrainReader.ReadCell);
            if(_drop.Status!="running") Finish(_drop.Status,_drop.Reason,new {
                startFeetY=_drop.StartFeetY,targetSurfaceY=_drop.TargetSurfaceY,
                actualPosition=new StateVector(p.position.X,p.position.Y), actualFeetY=p.position.Y+p.height,
                velocity=new StateVector(p.velocity.X,p.velocity.Y),terrain.Grounded });
            return;
        }
        if (action.Operation is "navigate_to_x" or "navigate_to_landing_region")
        {
            var nav = GameNavigation.Snapshot;
            if (nav.Generation == action.Generation && nav.Session == action.Session && nav.TargetRegion == action.TargetRegion
                && nav.Status is "arrived" or "failed")
                CompanionBridge.Controls.FinishAtomic(action.ActionId, nav.Status == "arrived" ? "completed" : "failed", nav.Reason, nav);
            return;
        }
        if (action.ActionId == _action) _used |= p.itemAnimation > 0 || p.itemTime > 0;
        p.controlUseItem = false; // Agent inputs never survive their update boundary.
    }
}
