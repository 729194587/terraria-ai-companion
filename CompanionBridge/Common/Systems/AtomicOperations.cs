using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
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
    private static bool _used, _aimSaved, _inputInjected;
    private static Vector2 _target;
    private static int _mouseX, _mouseY, _targetX, _targetY;
    private static NPC _npc;
    private static string Ref(NPC n) => ObservationReader.WorldSessionId + ":npc:" + n.whoAmI + ":" + n.GetGlobalNPC<AtomicNpcIdentity>().Token;
    private static void Finish(string status, string reason, object evidence = null) =>
        CompanionBridge.Controls.FinishAtomic(_action, status, reason, evidence,
            new(true, new(Main.LocalPlayer.position.X, Main.LocalPlayer.position.Y), new(Main.LocalPlayer.velocity.X, Main.LocalPlayer.velocity.Y), Main.LocalPlayer.statLife, Main.LocalPlayer.statLifeMax2));
    private static bool Loaded(int x, int y) => WorldGen.InWorld(x, y, 1)
        && (Main.netMode != NetmodeID.MultiplayerClient || Main.sectionManager?.TileLoaded(x, y) == true);
    private static bool Reach(Player p, Vector2 target) => Vector2.Distance(p.Center, target) <= 80;

    internal static object Query(ControlRequest request)
    {
        if (Main.gameMenu || !Main.LocalPlayer.active || ObservationReader.WorldSessionId == null)
            return new { available = false, reason = "not_in_world" };
        var p = Main.LocalPlayer;
        object data;
        var a = request.Arguments;
        switch (request.Operation)
        {
            case "get_player_state": case "inspect_inventory": data = ObservationReader.ReadPlayer(p); break;
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

    internal static void Before(Player p, ControlRequest request)
    {
        var action = CompanionBridge.Controls.AtomicAction;
        if (action?.Status != "running") return;
        bool first = _action != action.ActionId;
        var a = request.Arguments; string op = request.Operation;
        if (first)
        {
            _action = action.ActionId; _used = false; _npc = null;
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
                p.selectedItem = a.GetProperty("slot").GetInt32();
                Finish(p.selectedItem == a.GetProperty("slot").GetInt32() ? "completed" : "failed", "selected_slot_verified", new { p.selectedItem }); return;
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
            if (op is "mine_tile" or "place_tile" or "interact_tile")
            {
                int x = a.GetProperty("x").GetInt32(), y = a.GetProperty("y").GetInt32();
                _target = new(x * 16 + 8, y * 16 + 8);
                if (!Loaded(x, y)) { Finish("failed", "tile_unloaded_or_out_of_world"); return; }
                _tileType = Main.tile[x, y].HasTile ? Main.tile[x, y].TileType : -1;
                if (op == "mine_tile" && (_tileType < 0 || p.HeldItem.pick <= 0)) { Finish("failed", "requires_existing_tile_and_selected_pickaxe"); return; }
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
        if (!Loaded(tx, ty)) { Finish("failed", "target_unloaded_or_out_of_world"); return; }
        if (!Reach(p, _target)) { Finish("failed", "out_of_conservative_80_pixel_reach"); return; }
        if (op == "interact_tile")
        {
            if (Main.netMode != NetmodeID.SinglePlayer) { Finish("failed", "door_interaction_multiplayer_not_supported"); return; }
            var tile = Main.tile[tx, ty];
            bool changed = tile.HasTile && (tile.TileType == TileID.ClosedDoor ? WorldGen.OpenDoor(tx, ty, p.direction)
                : tile.TileType == TileID.OpenDoor && WorldGen.CloseDoor(tx, ty));
            int after = Main.tile[tx, ty].HasTile ? Main.tile[tx, ty].TileType : -1;
            Finish(changed && after != _tileType ? "completed" : "failed", changed ? "door_state_changed" : "unsupported_or_blocked_interaction", new { beforeType = _tileType, afterType = after }); return;
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
        if (first) _placeType = p.HeldItem.createTile;
        if (op == "use_item" && _used && p.itemAnimation == 0)
        { Finish("completed", "native_item_use_cycle_finished", new { effect = "item use cycle only; no target effect promised" }); return; }
        if (p.HeldItem.IsAir) { Finish("failed", "item_exhausted_before_effect_verified"); return; }
        if (p.gravDir != 1) { Finish("failed", "item_aim_requires_normal_gravity"); return; }
        if (Main.GameViewMatrix.Zoom != Vector2.One) { Finish("failed", "item_aim_requires_default_zoom"); return; }
        // One use cycle for generic use; mining/combat repeat until their effect or deadline.
        if (p.itemAnimation == 0 && p.itemTime == 0) p.releaseUseItem = true;
        p.controlUseItem = op != "use_item" || !_used;
        _inputInjected = true;
    }
    private static int _placeType;

    internal static void Aim(Player p)
    {
        var action = CompanionBridge.Controls.AtomicAction;
        if (action?.ActionId != _action || action.Status != "running" || action.Operation is "navigate_to_x" or "navigate_to_landing_region")
        {
            if (_inputInjected) p.controlUseItem = false; // Disconnect/revocation may race between player hooks.
            _inputInjected = false;
            return;
        }
        _mouseX = Main.mouseX; _mouseY = Main.mouseY; _targetX = Player.tileTargetX; _targetY = Player.tileTargetY; _aimSaved = true;
        Main.mouseX = (int)(_target.X - Main.screenPosition.X); Main.mouseY = (int)(_target.Y - Main.screenPosition.Y);
        Player.tileTargetX = (int)(_target.X / 16); Player.tileTargetY = (int)(_target.Y / 16);
    }
    internal static void RestoreAim()
    {
        if (!_aimSaved) return;
        Main.mouseX = _mouseX; Main.mouseY = _mouseY; Player.tileTargetX = _targetX; Player.tileTargetY = _targetY; _aimSaved = false;
    }
    internal static void After(Player p)
    {
        RestoreAim();
        if (_inputInjected) { p.controlUseItem = false; _inputInjected = false; }
        var action = CompanionBridge.Controls.AtomicAction;
        if (action?.Status != "running") return;
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
