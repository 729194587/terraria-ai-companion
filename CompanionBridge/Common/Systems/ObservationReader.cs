using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ObjectData;

namespace CompanionBridge.Common.Systems;

// All methods run on the game thread. Only detached records/arrays cross to TCP.
internal static class ObservationReader
{
    private static string _worldSession;
    private static long _observation, _render;
    private static CameraSnapshot _camera;
    private static string _cameraError;
    private static readonly System.Reflection.PropertyInfo[] Biomes = typeof(Player).GetProperties()
        .Where(p => p.Name.StartsWith("Zone", StringComparison.Ordinal) && p.PropertyType == typeof(bool)
            && p.GetIndexParameters().Length == 0).ToArray();

    internal static void EnterWorld()
    {
        _worldSession = Guid.NewGuid().ToString("N"); _observation = 0; _render = 0;
        _camera = null; _cameraError = null;
    }
    internal static void LeaveWorld() { _worldSession = null; _camera = null; _cameraError = null; }

    internal static void CaptureCamera()
    {
        if (_worldSession == null || Main.gameMenu || Main.dedServ) return;
        try
        {
            var m = Main.GameViewMatrix.TransformationMatrix;
            // This implementation supports the affine 2D transform used by SpriteBatch.
            if (m.M14 != 0 || m.M24 != 0 || m.M44 != 1)
                throw new ArgumentException("Unsupported projective camera matrix");
            var viewport = Main.instance.GraphicsDevice.Viewport;
            _camera = ObservationGeometry.Camera(++_render, Main.GameUpdateCount,
                DateTimeOffset.UtcNow.ToString("O"), Stopwatch.GetTimestamp(),
                new(Main.screenPosition.X, Main.screenPosition.Y),
                new(viewport.X, viewport.Y, viewport.Width, viewport.Height),
                new(Main.GameViewMatrix.Zoom.X, Main.GameViewMatrix.Zoom.Y),
                new(m.M11, m.M12, m.M21, m.M22, m.M41, m.M42));
            _cameraError = null;
        }
        catch (ArgumentException e) { _camera = null; _cameraError = e.Message; }
    }

    internal static ObservationSnapshot Read()
    {
        long started = Stopwatch.GetTimestamp();
        ulong tick = Main.GameUpdateCount;
        string utc = DateTimeOffset.UtcNow.ToString("O");
        string id = _worldSession == null ? null : _worldSession + ":" + ++_observation;
        var reasons = new List<string>();
        var limits = new[] {
            "Client-known data only; unsynced or offscreen entities are unknown, not absent. Server synchronization age is unknown.",
            "Camera is from PostDrawTiles; world data is copied at pre_DoUpdate. Different phases are not claimed to be the same frame.",
            "Tile bounds conservatively cover the camera quadrilateral; corner cells may be outside it. Geometry is not visual visibility; lighting, occlusion, UI and mod drawing are not evaluated.",
            "Mod collision/interaction hooks and mod accessory slots/biomes are not interpreted. Raw tile properties remain available.",
            "Entity instance keys are observation-scoped; slot reuse between snapshots is not tracked.",
            "Chest contents, signs' text and custom TileEntity state are not queried. Interactable candidates are not a complete interaction model.",
            "Engine tile data may include hidden terrain and wires not visually exposed to a player. Map exploration is not included." };
        CameraObservation camera = null;
        object player = null, environment = null, interactables = null;
        ObservationTerrain terrain = null;
        ObservedEntities entities = null;
        bool inWorld = _worldSession != null && !Main.dedServ && !Main.gameMenu
            && Main.netMode is NetmodeID.SinglePlayer or NetmodeID.MultiplayerClient
            && !(Main.netMode == NetmodeID.MultiplayerClient && Netplay.Disconnect) && Main.LocalPlayer.active;
        if (!inWorld) reasons.Add("not_in_world");
        else
        {
            var p = Main.LocalPlayer;
            if (p.dead || p.ghost) reasons.Add("local_player_dead_or_ghost");
            if (Main.gamePaused) reasons.Add("game_paused; simulation freshness not guaranteed");
            if (!Main.hasFocus) reasons.Add("unfocused; rendering/simulation may be suspended");
            player = ReadPlayer(p);
            environment = new {
                source = "client_known; server synchronization age unknown", Main.dayTime, Main.time,
                timeUnit = "Terraria day/night time units, not UTC", Main.raining, Main.rainTime,
                Main.maxRaining, Main.windSpeedCurrent, Main.cloudAlpha, Main.bloodMoon, Main.eclipse,
                Main.snowMoon, Main.pumpkinMoon,
                sandstorm = Terraria.GameContent.Events.Sandstorm.Happening,
                invasionTypeClientKnown = Main.invasionType,
                serverEventProgress = "unknown; no authoritative event progress requested",
                localPlayerBiomes = Biomes.ToDictionary(prop => prop.Name, prop => (bool)prop.GetValue(p)),
                biomeScope = "local player's vanilla flags, not per-tile viewport biome or all mod biomes"
            };
            if (_camera == null) reasons.Add("camera_unavailable: " + (_cameraError ?? "no render in this world"));
            else
            {
                double age = Stopwatch.GetElapsedTime(_camera.CapturedMonotonic, started).TotalMilliseconds;
                string status = ObservationGeometry.CameraStatus(age, tick, _camera.SourceGameUpdate);
                camera = new(_camera, age, status);
                if (status == "stale") reasons.Add("camera_stale");
                var objects = new Dictionary<(int, int), object>();
                try
                {
                    terrain = ObservationEncoding.Encode(_camera.TileBounds, (x, y) => ReadCell(x, y, objects), Describe);
                    if (terrain.UnknownCount > 0) reasons.Add("terrain_contains_out_of_world_or_unloaded_cells");
                    if (terrain.UninterpretedCount > 0) reasons.Add("mod_tile_collision_uninterpreted");
                    entities = ReadEntities(_camera, tick, id);
                    interactables = new { status = "partial_capability", items = objects.Values.ToArray(),
                        contentsStatus = "unknown; no chest opening or network requests performed" };
                }
                catch (ArgumentException e) { reasons.Add(e.Message); }
            }
        }
        string resultStatus = !inWorld ? "unavailable" : camera?.Status == "stale" ? "stale"
            : reasons.Count > 0 ? "partial" : "ok";
        return new(1, _worldSession, id, tick, utc, "pre_DoUpdate", resultStatus, camera,
            player, terrain, entities, environment, interactables,
            new(reasons.Count == 0, reasons.ToArray(), limits),
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private static object ReadPlayer(Player p) => new {
        id = p.whoAmI, source = "local_player", positionWorldPixels = new StateVector(p.position.X, p.position.Y),
        boundsWorldPixels = Bounds(p), velocityPixelsPerTick = new StateVector(p.velocity.X, p.velocity.Y),
        life = p.statLife, lifeMax = p.statLifeMax2, mana = p.statMana, manaMax = p.statManaMax2,
        p.direction, p.dead, p.ghost, selectedSlot = p.selectedItem,
        hotbarInventorySlots = Enumerable.Range(0, 10).ToArray(),
        inventory = p.inventory.Select((item, slot) => ItemData(item, slot)).ToArray(),
        equipment = p.armor.Select((item, slot) => ItemData(item, slot)).ToArray(),
        dyes = p.dye.Select((item, slot) => ItemData(item, slot)).ToArray(),
        miscEquipment = p.miscEquips.Select((item, slot) => ItemData(item, slot)).ToArray(),
        miscDyes = p.miscDyes.Select((item, slot) => ItemData(item, slot)).ToArray(),
        equipmentScope = "vanilla player arrays, including vanilla social slots; mod accessory slots not included",
        buffs = p.buffType.Select((type, slot) => new { slot, typeId = type, timeTicks = p.buffTime[slot],
            contentKey = type <= 0 ? null : BuffLoader.GetBuff(type)?.FullName ?? "Terraria/" + BuffID.Search.GetName(type) }).ToArray(),
        mount = new { active = p.mount.Active, typeId = p.mount.Active ? (int?)p.mount.Type : null },
        movement = new { p.gravDir, p.sloping, p.stairFall, p.wet, p.lavaWet, p.honeyWet, p.shimmerWet,
            p.pulley, p.grapCount, p.jump, p.controlDown, p.controlJump, p.frozen, p.webbed, p.stoned,
            p.runAcceleration, p.maxRunSpeed, p.gravity, p.maxFallSpeed,
            contactStatus = "unknown; no universal player collideX/Y or grounded flag inferred" }
    };

    private static object ItemData(Item item, int slot) => new { slot, empty = item.IsAir,
        typeId = item.type, contentKey = item.ModItem?.FullName ?? "Terraria/" + ItemID.Search.GetName(item.type),
        name = item.Name, stack = item.stack, prefix = (int)item.prefix, item.favorited,
        customState = item.ModItem == null ? "not_applicable" : "unknown; custom ModItem data not serialized" };

    private static ContentType Describe(string kind, int type)
    {
        var content = kind == "tile" ? (ModType)TileLoader.GetTile(type) : WallLoader.GetWall(type);
        return new(kind, type, content?.FullName ?? "Terraria/" +
            (kind == "tile" ? TileID.Search.GetName(type) : WallID.Search.GetName(type)), content != null);
    }

    private static TileCell ReadCell(int x, int y, Dictionary<(int, int), object> objects)
    {
        if (x < 0 || y < 0 || x >= Main.maxTilesX || y >= Main.maxTilesY) return new("out_of_world");
        if (Main.netMode == NetmodeID.MultiplayerClient && (Main.sectionManager == null || !Main.sectionManager.TileLoaded(x, y)))
            return new("unloaded");
        var t = Main.tile[x, y]; int type = t.TileType;
        if (t.HasTile && Main.tileFrameImportant[type])
        {
            var anchor = TileObjectData.TopLeft(x, y);
            if (!objects.ContainsKey((anchor.X, anchor.Y)))
            {
                string category = type is TileID.Containers or TileID.Containers2 or TileID.Dressers ? "chest"
                    : type is TileID.OpenDoor or TileID.ClosedDoor ? "door"
                    : type is TileID.Signs or TileID.Tombstones ? "sign" : "frame_important_candidate";
                objects.Add((anchor.X, anchor.Y), new { category, anchorTile = new { x = anchor.X, y = anchor.Y },
                    typeId = type, contentKey = Describe("tile", type).ContentKey,
                    interactionKnowledge = category == "frame_important_candidate" ? "unknown" : "vanilla_type_inference",
                    contents = "unknown_or_not_applicable; not read" });
            }
        }
        bool modded = t.HasTile && TileLoader.GetTile(type) != null;
        return new("read", t.HasTile, t.HasTile ? type : null, t.WallType,
            t.TileFrameX, t.TileFrameY, t.IsHalfBlock, (int)t.Slope,
            t.HasTile && Main.tileSolid[type], t.HasTile && Main.tileSolidTop[type], t.IsActuated, t.HasActuator,
            (t.RedWire ? 1 : 0) | (t.BlueWire ? 2 : 0) | (t.GreenWire ? 4 : 0) | (t.YellowWire ? 8 : 0),
            t.LiquidType, t.LiquidAmount, t.TileColor, t.WallColor,
            t.IsTileInvisible, t.IsWallInvisible, t.IsTileFullbright, t.IsWallFullbright,
            modded ? "unknown_mod_hooks" : "known", t.HasTile ? TileID.Sets.TouchDamageImmediate[type] : 0,
            t.HasTile && TileID.Sets.TouchDamageHot[type], t.HasTile && TileID.Sets.TouchDamageBleeding[type],
            t.HasTile && TileID.Sets.TouchDamageDestroyTile[type], t.HasTile && TileID.Sets.Boulders[type]);
    }

    private static WorldRect Bounds(Entity e) => new(e.position.X, e.position.Y, e.width, e.height);
    private static ObservedEntities ReadEntities(CameraSnapshot camera, ulong tick, string observationId)
    {
        var items = new List<ObservedEntity>();
        void Add(Entity entity, string kind, int slot, int? type, string key, string name,
            int? life, int? max, string classification, string basis, object details)
        {
            var bounds = Bounds(entity);
            if (!ObservationGeometry.Intersects(bounds, camera.WorldCorners)) return;
            items.Add(new(kind, ObservationEntitySlots.InstanceKey(observationId, kind, slot), slot, type, key, name,
                bounds, new(entity.velocity.X, entity.velocity.Y), life, max, classification, basis, true, tick, details));
        }
        foreach (var (slot, p) in ObservationEntitySlots.Enumerate(Main.player))
            if (p.active) Add(p, "player", slot, null, "Terraria/Player", p.name, p.statLife, p.statLifeMax2,
                slot == Main.myPlayer ? "self" : "other_player", "Player identity; current PvP hostility not inferred",
                new { p.dead, p.ghost, p.hostile, p.team, isLocal = slot == Main.myPlayer });
        foreach (var (slot, n) in ObservationEntitySlots.Enumerate(Main.npc))
            if (n.active)
            {
                var evidence = new NpcEvidence(n.friendly, n.townNPC || NPCID.Sets.ActsLikeTownNPC[n.type],
                    NPCID.Sets.CountsAsCritter[n.type], n.damage, n.CanBeChasedBy(), n.ModNPC != null,
                    NPCID.Sets.ProjectileNPC[n.type]);
                var classification = EntityCollector.Classify(evidence);
                Add(n, "npc", slot, n.type, n.ModNPC?.FullName ?? "Terraria/" + NPCID.Search.GetName(n.type),
                    n.GivenOrTypeName, n.life, n.lifeMax, classification.Kind, classification.Basis,
                    new { evidence, n.hide, n.dontTakeDamage, n.realLife });
            }
        foreach (var (slot, item) in ObservationEntitySlots.Enumerate(Main.item))
            if (item.active && !item.IsAir) Add(item, "item", slot, item.type,
                item.ModItem?.FullName ?? "Terraria/" + ItemID.Search.GetName(item.type), item.Name,
                null, null, "dropped_item", "Active item; life not applicable", ItemData(item, slot));
        foreach (var (slot, projectile) in ObservationEntitySlots.Enumerate(Main.projectile))
            if (projectile.active) Add(projectile, "projectile", slot, projectile.type,
                projectile.ModProjectile?.FullName ?? "Terraria/" + ProjectileID.Search.GetName(projectile.type), projectile.Name,
                null, null, "projectile", "Projectile flags are evidence, not NPC allegiance or current threat",
                new { projectile.owner, projectile.identity, projectile.friendly, projectile.hostile,
                    projectile.damage, projectile.timeLeft, projectile.hide, lifeStatus = "not_applicable" });
        return new(items.ToArray(), new(items.Count, items.Count, 0, "all client-known active entities intersecting camera; no 12-entity cap"));
    }
}
