using System.Text.Json;
using Terraria;
using Terraria.ID;

namespace CompanionBridge.ServerIpc;

// Checked with the selected native item and current target on the simulation thread,
// including tool use by a navigation or construction child.
internal static class AutonomyPolicy
{
    internal static bool ProtectedTile(int x, int y)
    {
        if (!WorldGen.InWorld(x,y)) return false;
        var tile = Main.tile[x,y];
        return tile.HasTile && tile.TileType is TileID.ShadowOrbs or TileID.Larva or TileID.PlanteraBulb
            or TileID.DemonAltar or TileID.LihzahrdAltar;
    }

    internal static bool Refuses(AiReadOnlyProtocol.Identity identity, Player player, string operation, JsonElement args)
    {
        if (operation == "use_item")
        {
            var item = player.HeldItem;
            return item.ModItem != null || item.type is ItemID.SuspiciousLookingEye or ItemID.WormFood
                or ItemID.BloodySpine or ItemID.SlimeCrown or ItemID.Abeemination or ItemID.MechanicalEye
                or ItemID.MechanicalWorm or ItemID.MechanicalSkull or ItemID.MechdusaSummon or ItemID.DeerThing
                or ItemID.QueenSlimeCrystal or ItemID.CelestialSigil or ItemID.LihzahrdPowerCell
                or ItemID.DD2ElderCrystal or ItemID.GoblinBattleStandard or ItemID.PirateMap or ItemID.SnowGlobe
                or ItemID.PumpkinMoonMedallion or ItemID.NaughtyPresent or ItemID.SolarTablet or ItemID.BloodMoonStarter
                or ItemID.CombatBook or ItemID.CombatBookVolumeTwo or ItemID.PeddlersSatchel;
        }
        if (operation == "drop_inventory_item" && args.TryGetProperty("sourceSlot", out var source)
            && source.TryGetInt32(out int slot) && slot >= 0 && slot < player.inventory.Length)
            return player.inventory[slot].type == ItemID.GuideVoodooDoll;
        if (operation == CombatProtocol.Operation && args.TryGetProperty("entityRef", out var target))
            foreach (var n in Main.ActiveNPCs)
                if (n.type is NPCID.CultistArcherBlue or NPCID.CultistDevote or NPCID.EmpressButterfly or NPCID.MartianProbe
                    && NativeCombatWorld.Reference(identity,n) == target.GetString()) return true;
        if (args.TryGetProperty("target", out var nested) && nested.ValueKind == JsonValueKind.Object) args = nested;
        if (args.TryGetProperty("x", out var tx) && args.TryGetProperty("y", out var ty)
            && tx.TryGetInt32(out int x) && ty.TryGetInt32(out int y) && WorldGen.InWorld(x,y))
        {
            if (operation is "mine_tile" or "hammer_tile" or "mine_target") return ProtectedTile(x,y);
            var tile = Main.tile[x,y];
            if (operation is ("open_container" or "interact_tile") && tile.HasTile && tile.TileType is TileID.Containers or TileID.Containers2)
            {
                var anchor = Terraria.ObjectData.TileObjectData.TopLeft(x,y);
                if (WorldGen.IsChestRigged(anchor.X,anchor.Y)) return true;
            }
            if (operation == "interact_tile" && tile.HasTile && tile.TileType is TileID.Switches or TileID.Lever)
                return true; // The wire network's effects are outside the interaction proof.
        }
        return false;
    }
}
