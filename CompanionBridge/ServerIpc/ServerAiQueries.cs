using System;
using System.Linq;
using System.Text.Json;
using Terraria;
using Terraria.ModLoader;
using CompanionBridge.Motion;
using CompanionBridge.Common.Systems;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

// Called only under the reservation Gate on the original simulation thread.
// Reuses native-player projections without entering the human Query/LocalPlayer path.
internal static class ServerAiQueries
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
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
        if (operation is not ("get_player_state" or "inspect_inventory" or "observe_terrain" or "scan_tiles" or "scan_entities" or "inspect_tile" or "resolve_content" or "observe_scene" or "query_recipes" or "query_map")) throw new FormatException("Unsupported server AI query.");
        AtomicParameters.Validate(operation, arguments, true);
        Require(expected);
        var p = (Player)expected.Actor;
        object data;
        if(operation=="query_map") data=(AiMapRuntime.Map??throw new InvalidOperationException("Map unavailable")).Query(arguments,(int)(p.Center.X/16),(int)(p.Center.Y/16));
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
                legend=". clear/nonblocking/actuated; # solid; h halfblock; 1..4 slopes; = platform; a/b platform slopes; ! liquid or hazard; ? unknown collision or world boundary",
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
            n => expected.World + ":" + expected.Generation + ":npc:" + n.whoAmI + ":" + n.GetGlobalNPC<AtomicNpcIdentity>().Token, true);
        else
        {
            // No human Controls/WorldSession/Main.myPlayer-based token may leak into this backend.
            data = AtomicOperations.ReadInventory(p, arguments, (item, slot) => ActorInventory.Token(expected,item,slot), () => Snapshot(p));
        }
        var projected = JsonSerializer.SerializeToElement(data, Json).EnumerateObject().ToDictionary(field => field.Name, field => (object)field.Value);
        if(operation=="inspect_tile")
        {
            var terrain=TerrainReader.Read(p);
            var pose=new PhysicsState(p.position.X,p.position.Y,p.velocity.X,p.velocity.Y,terrain.Grounded,p.jump,p.releaseJump);
            string unsupported=p.width!=20||p.height!=42?"unsupported_body_dimensions":p.gravDir!=1?"unsupported_gravity":
                !terrain.Supported?"unsupported_player_profile":null;
            projected["miningSupportImpact"]=JsonSerializer.SerializeToElement(MiningSupportImpact.Read(pose,
                arguments.GetProperty("x").GetInt32(),arguments.GetProperty("y").GetInt32(),TerrainReader.ReadCell,unsupported),Json);
        }
        projected["source"] = "server_ai_player";
        projected["customState"] = "unknown; custom Mod player/item/buff data and Mod accessory slots not read";
        projected["inventoryTokenStatus"] = "server_ai_inventory_v1; use mode slots/selected/hotbar for fresh slot tokens; basic fields and instance identity only; all custom Mod state unknown; no save hooks";
        Require(expected); // Reject hooks/identity changes during projection before publishing the result.
        return new { available = true, worldSessionId = expected.World, gameUpdate = Main.GameUpdateCount,
            actorGeneration = expected.Generation, actorSlot = expected.Slot,
            observedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), clock = "server_utc_at_projection", data = projected };
    }
}
