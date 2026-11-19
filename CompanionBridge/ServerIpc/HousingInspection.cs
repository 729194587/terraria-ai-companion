using System;
using System.Reflection;
using Terraria;
using Terraria.ID;
using Terraria.Enums;

namespace CompanionBridge.ServerIpc;

internal static class HousingInspection
{
    // These private flags are written by the supported engine's housing routines.
    private static bool Flag(string name) =>
        (bool)(typeof(WorldGen).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)
            ?.GetValue(null) ?? throw new MissingFieldException(typeof(WorldGen).FullName, name));

    internal static object Read(int x, int y)
    {
        if (!WorldGen.InWorld(x, y, 0)) throw new FormatException("Housing query tile outside world.");
        const int npcType = NPCID.Guide;
        bool structureValid = WorldGen.StartRoomCheck(x, y);
        var nativeReason = WorldGen.roomCheckFailureReason;
        var bounds = !structureValid ? null : new { minX = WorldGen.roomX1, maxX = WorldGen.roomX2, minY = WorldGen.roomY1, maxY = WorldGen.roomY2 };
        bool? stinkbug = structureValid ? Flag("roomHasStinkbug") : null;
        bool? echoStinkbug = structureValid ? Flag("roomHasEchoStinkbug") : null;
        object furniture = null, homeTile = null;
        bool? furnitureValid = null, occupied = null, evil = null;
        int? score = null;
        object Result(string status, string failureStage, string reason) => new {
            checkVersion = "server_ai_housing_inspect_v1", queryTile = new { x, y }, npcType, status, failureStage, reason,
            nativeReason = nativeReason.ToString(), stinkbug, echoStinkbug, structureValid, bounds, furnitureValid, furniture,
            occupied, evil, score, homeTile
        };
        if (!structureValid)
        {
            string reason = nativeReason switch {
                TownNPCRoomCheckFailureReason.TooCloseToWorldEdge => "too_close_to_world_edge",
                TownNPCRoomCheckFailureReason.RoomIsTooBig => "room_too_big",
                TownNPCRoomCheckFailureReason.RoomIsTooSmall => "room_too_small",
                TownNPCRoomCheckFailureReason.HoleInWallIsTooBig => "wall_gap_too_big",
                TownNPCRoomCheckFailureReason.RoomCheckStartedInASolidTile => "solid_query_tile",
                _ => null
            };
            return Result(reason == null ? "unknown" : "invalid", "structure", reason ?? "unclassified_structure_failure");
        }
        furnitureValid = WorldGen.RoomNeeds(npcType);
        furniture = new { light = Flag("roomTorch"), door = Flag("roomDoor"), table = Flag("roomTable"), chair = Flag("roomChair") };
        if (furnitureValid == false) return Result("invalid", "furniture", "room_needs_failed");
        if (stinkbug == true || echoStinkbug == true) return Result("invalid", "guide_requirements", stinkbug == true ? "stinkbug" : "echo_stinkbug");

        WorldGen.ScoreRoom(-1, npcType);
        score = WorldGen.hiScore;
        occupied = Flag("roomOccupied");
        evil = occupied == true ? null : Flag("roomEvil");
        if (score <= 0) return Result("invalid", "score", occupied == true ? "occupied" : evil == true ? "evil" : "no_home_tile");
        homeTile = new { x = WorldGen.bestX, y = WorldGen.bestY };
        return Result("valid", null, null);
    }
}
