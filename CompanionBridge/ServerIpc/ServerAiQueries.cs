using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using Terraria;
using Terraria.ModLoader;
using CompanionBridge.Motion;
using CompanionBridge.Common.Systems;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

internal sealed class QueryUnavailableException(string reason) : Exception(reason) { }

// Called only under the reservation Gate on the original simulation thread.
// Reuses native-player projections without entering the human Query/LocalPlayer path.
internal static class ServerAiQueries
{
    private static readonly JsonSerializerOptions Json = SceneRegion.Json;
    private static void Require(AiReadOnlyProtocol.Identity expected)
    {
        ProbeSession.Reservation.AssertOwner(Program.IsMainThread);
        if (expected == null || !ReferenceEquals(expected.Actor, ProbeSession.Actor)
            || expected.World != ProbeSession.WorldSession || expected.Generation != ProbeSession.ActorGeneration
            || expected.Slot != ProbeSession.Actor?.whoAmI)
            throw new InvalidOperationException("AI query identity changed.");
        NetworkStateSync.Require((Player)expected.Actor);
    }
    private static JsonElement Snapshot(Player p) => JsonSerializer.SerializeToElement(ObservationReader.ReadPlayer(p, "server_ai_player"), Json);
    internal static object Read(AiReadOnlyProtocol.Identity expected, string operation, JsonElement arguments)
    {
        try { return ReadBound(expected, operation, arguments); }
        catch (QueryUnavailableException unavailable)
        {
            Require(expected);
            return new { available = false, reason = unavailable.Message, worldSessionId = expected.World,
                gameUpdate = Main.GameUpdateCount, actorGeneration = expected.Generation, actorSlot = expected.Slot,
                observedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), clock = "server_utc_at_projection" };
        }
    }

    private static object ReadBound(AiReadOnlyProtocol.Identity expected, string operation, JsonElement arguments)
    {
        if (operation is not ("get_player_state" or "inspect_inventory" or "observe_terrain" or "scan_tiles" or "scan_entities" or "inspect_tile" or "resolve_content" or "observe_scene" or "query_recipes" or "query_map" or "query_combat_loadout" or "list_pylons" or "list_teammates" or "discover_bosses" or "inspect_container" or "build_diff" or "inspect_housing" or "inspect_npc_housing" or "inspect_npc_shop" or "quote_npc_purchase" or "quote_npc_sale" or "quote_nurse_heal" or "quote_reforge")) throw new FormatException("Unsupported server AI query.");
        if (operation != "build_diff") AtomicParameters.Validate(operation, arguments, true);
        Require(expected);
        bool regional=operation=="observe_scene"&&arguments.TryGetProperty("scale",out var scale)&&scale.GetString()=="region";
        long sampleStarted=regional?Stopwatch.GetTimestamp():0;
        long allocated=regional?GC.GetAllocatedBytesForCurrentThread():0;
        var p = (Player)expected.Actor;
        object data;
        if (operation is "inspect_container" or "query_combat_loadout" or "list_pylons" or "list_teammates" or "discover_bosses")
        {
            data = operation=="list_pylons"?ServerPylons.List(expected):operation=="list_teammates"?ServerRegroup.Discover(expected):operation=="discover_bosses"?BossMembers.Discover(expected,p):operation=="inspect_container"?ActorContainer.Inspect(expected):NativeCombatWorld.Loadout(expected);
            Require(expected);
            return new { available = true, worldSessionId = expected.World, gameUpdate = Main.GameUpdateCount,
                actorGeneration = expected.Generation, actorSlot = expected.Slot,
                observedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), clock = "server_utc_at_projection", data };
        }
        if (operation == "build_diff") data = BuildDiff.Read(p, arguments);
        else if (operation == "quote_reforge") data = GoblinReforge.Quote(expected,arguments);
        else if (operation == "quote_nurse_heal") data = NurseService.Quote(expected,arguments);
        else if (operation == "quote_npc_sale") data = MerchantShop.QuoteSale(expected, arguments);
        else if (operation is "inspect_npc_shop" or "quote_npc_purchase") data = MerchantShop.Read(expected, arguments, operation == "quote_npc_purchase");
        else if (operation == "inspect_npc_housing") data = NpcHousing.Read(expected, arguments.GetProperty("entityRef").GetString());
        else if (operation == "inspect_housing") data = HousingInspection.Read(arguments.GetProperty("x").GetInt32(), arguments.GetProperty("y").GetInt32());
        else if(operation=="query_map") data=(AiMapRuntime.Map??throw new InvalidOperationException("Map unavailable")).Query(arguments,(int)(p.Center.X/16),(int)(p.Center.Y/16));
        else if (operation == "query_recipes") data=ActorCrafting.ReadRecipes(expected,arguments);
        else if (operation == "observe_scene") data=ServerSceneReader.Read(p,expected,arguments);
        else if (operation == "get_player_state")
        {
            var snapshot = Snapshot(p);
            var player = snapshot.EnumerateObject().Where(field => field.Name is not ("inventory" or "equipment" or "dyes" or "miscEquipment" or "miscDyes" or "buffs"))
                .ToDictionary(field => field.Name, field => (object)field.Value);
            player["inWorld"] = true;
            player["active"] = p.active;
            player["position"] = snapshot.GetProperty("positionWorldPixels");
            player["velocity"] = snapshot.GetProperty("velocityPixelsPerTick");
            var terrain = TerrainReader.Read(p);
            data = new { player, motion = new { grounded = terrain.Grounded, supported = terrain.Supported,
                playerWidth = terrain.PlayerWidth, playerHeight = terrain.PlayerHeight,
                knowledge = "existing_terrain_classifier; supported is the existing movement-profile check, not path safety" } };
        }
        else if (operation == "observe_terrain")
        {
            var terrain = TerrainReader.Read(p);
            int unknown = terrain.Rows.Sum(row => row.Count(cell => cell == '?'));
            data = new { terrain, state = new { id=p.whoAmI,
                positionWorldPixels=new StateVector(p.position.X,p.position.Y),
                centerWorldPixels=new StateVector(p.Center.X,p.Center.Y),
                velocityPixelsPerTick=new StateVector(p.velocity.X,p.velocity.Y), p.direction, p.dead },
                bounds = new { minX=terrain.OriginX, minY=terrain.OriginY,
                    maxX=terrain.OriginX+44, maxY=terrain.OriginY+28 },
                scannedCount=45*29, unknownCount=unknown, complete=unknown==0,
                coordinates="Tile cells; 16 world pixels per tile; +X right,+Y down; rows[y][x] relative to origin",
                legend=". clear/nonblocking/actuated; # solid; h halfblock; 1..4 slopes; = platform; a/b platform slopes; ! harmful tile; ? unknown collision or world boundary",
                liquidCoordinates="terrain.liquids entries use absolute Tile x/y and native amount 1..255; missing entries on unread cells are unknown",
                scope="45x29 collision categories, not tile types or visibility; unknown Mod collision hooks; no path guarantee" };
        }
        else if (operation == "resolve_content") data = AtomicQueries.Resolve(arguments);
        else if (operation == "inspect_tile") data = AtomicQueries.InspectTile(p,
            arguments.GetProperty("x").GetInt32(), arguments.GetProperty("y").GetInt32(), (_,_) => null);
        else if (operation == "scan_tiles")
        {
            if (arguments.GetProperty("tileTypes").EnumerateArray().Any(t => t.GetInt32() >= TileLoader.TileCount))
                throw new FormatException("Invalid tile type for loaded registry.");
            data = AtomicQueries.ScanTiles(p, arguments);
        }
        else if (operation == "scan_entities") data = AtomicQueries.ScanEntities(p, arguments,
            n => NativeCombatWorld.Reference(expected,n), true, (slot, item) => ServerDrops.Reference(expected,slot,item));
        else
        {
            // No human Controls/WorldSession/Main.myPlayer-based token may leak into this backend.
            data = AtomicOperations.ReadInventory(p, arguments, (item, slot) => ActorInventory.Token(expected,item,slot), () => Snapshot(p));
        }
        var projected = JsonSerializer.SerializeToElement(data, Json).EnumerateObject().ToDictionary(field => field.Name, field => (object)field.Value);
        if(operation=="inspect_tile")
        {
            if (ServerTileInteraction.Observe(expected, arguments.GetProperty("x").GetInt32(), arguments.GetProperty("y").GetInt32()) is {} interaction)
                projected["interaction"] = interaction;
            var terrain=TerrainReader.Read(p);
            var pose=new PhysicsState(p.position.X,p.position.Y,p.velocity.X,p.velocity.Y,terrain.Grounded,p.jump,p.releaseJump);
            string unsupported=p.width!=20||p.height!=42?"unsupported_body_dimensions":p.gravDir!=1?"unsupported_gravity":
                !terrain.Supported?"unsupported_player_profile":null;
            projected["miningSupportImpact"]=JsonSerializer.SerializeToElement(MiningSupportImpact.Read(pose,
                arguments.GetProperty("x").GetInt32(),arguments.GetProperty("y").GetInt32(),TerrainReader.ReadDryCell,unsupported),Json);
        }
        if(operation=="inspect_inventory" && projected.ContainsKey("inventory"))
        {
            var inspected=(JsonElement)projected["inspectedSlots"];
            projected["combatCandidates"]=inspected.EnumerateArray().Select(value=>value.GetInt32())
                .Where(slot=>slot is >=0 and <50)
                .Select(slot=>new {slot,family=NativeCombatWorld.LoadoutFamily(p.inventory[slot])})
                .Where(row=>row.family!=null).ToArray();
        }
        projected["source"] = "server_ai_player";
        if(operation=="get_player_state" || operation=="inspect_inventory" && arguments.GetProperty("mode").GetString()=="full")
            projected["functionalEquipmentSlots"] = ActorEquipment.Observe(expected);
        if(operation=="inspect_inventory" && arguments.GetProperty("mode").GetString()=="full")
        {
            projected["cosmeticEquipmentSlots"]=ActorEquipment.Observe(expected,cosmetic:true);
            projected["trash"]=ServerPersonalInventory.ObserveTrash(expected);
            projected["currentLoadout"]=p.CurrentLoadoutIndex+1;
        }
        projected["customState"] = "unknown; custom Mod player/item/buff data not read";
        projected["inventoryTokenStatus"] = "server_ai_inventory_v1; use mode slots/selected/hotbar for fresh slot tokens; basic fields and instance identity only; all custom Mod state unknown; no save hooks";
        Require(expected); // Reject hooks/identity changes during projection before publishing the result.
        if(regional)
        {
            var metrics=((JsonElement)projected["sample"]).EnumerateObject().ToDictionary(f=>f.Name,f=>(object)f.Value);
            metrics["projectionElapsedMilliseconds"]=Stopwatch.GetElapsedTime(sampleStarted).TotalMilliseconds;
            metrics["projectionAllocatedBytes"]=GC.GetAllocatedBytesForCurrentThread()-allocated;
            projected["sample"]=metrics;
        }
        return new { available = true, worldSessionId = expected.World, gameUpdate = Main.GameUpdateCount,
            actorGeneration = expected.Generation, actorSlot = expected.Slot,
            observedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), clock = "server_utc_at_projection", data = projected };
    }
}
