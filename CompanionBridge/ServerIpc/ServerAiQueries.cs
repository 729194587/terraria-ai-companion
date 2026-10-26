using System;
using System.Linq;
using System.Text.Json;
using Terraria;
using Terraria.ModLoader;
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
        if (operation is not ("get_player_state" or "inspect_inventory")) throw new FormatException("Unsupported server AI query.");
        AtomicParameters.Validate(operation, arguments, true);
        Require(expected);
        var p = (Player)expected.Actor;
        object data;
        if (operation == "get_player_state")
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
        else
        {
            // No human Controls/WorldSession/Main.myPlayer-based token may leak into this backend.
            data = AtomicOperations.ReadInventory(p, arguments, (_, _) => null, () => Snapshot(p));
        }
        var projected = JsonSerializer.SerializeToElement(data, Json).EnumerateObject().ToDictionary(field => field.Name, field => (object)field.Value);
        projected["source"] = "server_ai_player";
        projected["customState"] = "unknown; custom Mod player/item/buff data and Mod accessory slots not read";
        projected["inventoryTokenStatus"] = "unsupported_server_ai_inventory_mutation";
        Require(expected); // Reject hooks/identity changes during projection before publishing the result.
        return new { available = true, worldSessionId = expected.World, gameUpdate = Main.GameUpdateCount,
            actorGeneration = expected.Generation, actorSlot = expected.Slot, data = projected };
    }
}
