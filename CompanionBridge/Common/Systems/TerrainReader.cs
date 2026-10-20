using System;
using Terraria;
using Terraria.ID;

namespace CompanionBridge.Common.Systems;

internal static class TerrainReader
{
    // Fixed, bounded local window; enough room for lookahead and a normal jump, not a world map.
    internal static TerrainSnapshot Read(Player player)
    {
        int left = (int)Math.Floor(player.Center.X / 16) - 22;
        int top = (int)Math.Floor(player.Bottom.Y / 16) - 17;
        var rows = new string[29];
        for (int y = 0; y < rows.Length; y++)
        {
            var row = new char[45];
            for (int x = 0; x < row.Length; x++) row[x] = ReadCell(left + x, top + y);
            rows[y] = new string(row);
        }
        bool grounded = false;
        float bottom = player.Bottom.Y;
        for (int x = (int)Math.Floor(player.position.X / 16);
            x <= (int)Math.Floor((player.Right.X - 0.01f) / 16); x++)
        {
            for (int y = (int)Math.Floor((bottom - 17) / 16); y <= (int)Math.Floor((bottom + 1) / 16); y++)
            {
                char cell = ReadCell(x, y);
                float surface = y * 16 + TerrainClassifier.SurfaceOffset(cell,
                    player.position.X - x * 16, player.Right.X - x * 16);
                if (Math.Abs(surface - bottom) <= 2 && player.velocity.Y >= -0.01f
                    && (Math.Abs(player.velocity.Y) < 0.01f || player.sloping)) grounded = true;
            }
        }
        bool supported = !player.dead && !player.ghost && !player.mount.Active && !player.pulley
            && !player.wet && !player.webbed && !player.stoned && !player.frozen
            && player.gravDir == 1 && player.grapCount == 0 && player.width == 20 && player.height == 42;
        return new TerrainSnapshot(left, top, 16, rows, player.width, player.height, grounded, supported);
    }

    internal static char ReadCell(int x, int y)
    {
        if (!WorldGen.InWorld(x, y, 1)
            || (Main.netMode == NetmodeID.MultiplayerClient
                && (Main.sectionManager == null || !Main.sectionManager.TileLoaded(x, y)))) return '?';
        var tile = Main.tile[x, y];
        int type = tile.TileType;
        bool harmful = tile.HasUnactuatedTile && (TileID.Sets.TouchDamageImmediate[type] > 0
            || TileID.Sets.TouchDamageHot[type] || TileID.Sets.TouchDamageBleeding[type]
            || TileID.Sets.TouchDamageDestroyTile[type] || TileID.Sets.Boulders[type]);
        // Custom collision hooks cannot be inferred from vanilla tile flags.
        if (tile.HasUnactuatedTile && type >= TileID.Count) return '?';
        return TerrainClassifier.Classify(true, tile.HasTile, tile.IsActuated,
            Main.tileSolid[type], Main.tileSolidTop[type], tile.TileFrameY,
            tile.IsHalfBlock, (int)tile.Slope, tile.LiquidAmount > 0, harmful);
    }
}
