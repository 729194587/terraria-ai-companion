using System;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Identity = CompanionBridge.ServerIpc.AiReadOnlyProtocol.Identity;
using Result = CompanionBridge.ServerIpc.AiAtomicActions.NativeResult;

namespace CompanionBridge.ServerIpc;

internal static class NpcHousing
{
    private sealed record TilePosition(int x, int y);
    private sealed record State(string entityRef, int type, int slot, bool active, bool townNPC,
        bool homeless, int homeTileX, int homeTileY, bool managerHasRoom, TilePosition managerHomeTile);

    private static State Snapshot(NPC npc, string reference)
    {
        bool registered = WorldGen.TownManager.HasRoom(npc.type, out Point room);
        return new(reference, npc.type, npc.whoAmI, npc.active, npc.townNPC, npc.homeless,
            npc.homeTileX, npc.homeTileY, registered, registered ? new(room.X, room.Y) : null);
    }

    internal static object Read(Identity identity, string reference) =>
        Snapshot(NpcDialogue.Resolve(identity, reference) ?? throw new StaleNpcReferenceException(), reference);

    // Existing vanilla residents with ordinary housing rules. Truffle has an extra
    // biome check; pets use separate sharing rules and modded residents can veto housing.
    private static bool OrdinaryResident(NPC npc) => npc.townNPC && npc.life > 0 &&
        npc.type is NPCID.Guide or NPCID.Merchant or NPCID.Nurse or NPCID.Demolitionist
            or NPCID.DyeTrader or NPCID.Angler or NPCID.Dryad or NPCID.Painter
            or NPCID.ArmsDealer or NPCID.GoblinTinkerer or NPCID.WitchDoctor or NPCID.Clothier
            or NPCID.Mechanic or NPCID.PartyGirl or NPCID.Wizard or NPCID.Steampunker
            or NPCID.Cyborg or NPCID.Pirate or NPCID.Stylist or NPCID.Golfer;

    internal static Result Assign(Identity identity, JsonElement args)
    {
        ServerAiNativeActions.Require(identity);
        string entityRef = args.GetProperty("entityRef").GetString();
        int x = args.GetProperty("x").GetInt32(), y = args.GetProperty("y").GetInt32();
        var queryTile = new TilePosition(x, y);
        var npc = NpcDialogue.Resolve(identity, entityRef);
        State before = npc == null ? null : Snapshot(npc, entityRef);
        Result Reject(string reason) => new("failed", reason, new { entityRef, queryTile, before, mutationAttempted = false },
            true, InputsAcquired: false);
        if (npc == null) return Reject("stale_entity_ref");
        if (!OrdinaryResident(npc)) return Reject("unsupported_npc");
        if (!WorldGen.InWorld(x, y, 0)) return Reject("invalid_room");
        HousingInspection.Read(x, y, npc.type, out var candidate, out bool occupied);
        if (occupied) return Reject("room_occupied");
        if (candidate == null || !(Main.wallHouse[Main.tile[x, y].WallType] || WorldGen.canSpawn)) return Reject("invalid_room");

        // FindAHomelessNPC filters by prioritizedTownNPCType. SetRoom keys by type,
        // so two live instances of the same type cannot have independently verified records.
        foreach (var other in Main.ActiveNPCs)
            if (other != npc && other.type == npc.type)
                return Reject("ambiguous_housing_target");

        var homeTile = new TilePosition(candidate.Value.X, candidate.Value.Y);
        State after = null;
        object Evidence() => new { entityRef, queryTile, homeTile, before, after };
        try
        {
            WorldGen.moveRoom(x, y, npc.whoAmI);
            if (ServerAiNativeActions.Owned(identity) && ReferenceEquals(npc, NpcDialogue.Resolve(identity, entityRef)))
                after = Snapshot(npc, entityRef);
        }
        catch (Exception)
        {
            return new("unknown", "npc_home_unconfirmed", Evidence(), true, InputsAcquired: false);
        }
        bool verified = after is { active: true, homeless: false, managerHasRoom: true } &&
            after.type == before.type && after.homeTileX == homeTile.x && after.homeTileY == homeTile.y &&
            after.managerHomeTile == homeTile;
        return new(verified ? "completed" : "unknown", verified ? "npc_home_verified" : "npc_home_unconfirmed",
            Evidence(), true, verified, InputsAcquired: false);
    }
}
