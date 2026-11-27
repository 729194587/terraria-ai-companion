using System;
using System.Collections.Generic;
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
        var liquids = new List<TerrainLiquidCell>();
        for (int y = 0; y < rows.Length; y++)
        {
            var row = new char[45];
            for (int x = 0; x < row.Length; x++)
            {
                var cell=ReadEnvironment(left+x,top+y);
                row[x]=cell.Shape;
                if(cell.Liquid.Amount>0)liquids.Add(new(left+x,top+y,cell.Liquid.Name,cell.Liquid.Amount));
            }
            rows[y] = new string(row);
        }
        bool grounded = Grounded(player);
        bool supported = !player.dead && !player.ghost && !player.mount.Active && !player.pulley
            && !player.wet && !player.honeyWet && !player.lavaWet && !player.shimmerWet
            && !player.webbed && !player.stoned && !player.frozen
            && player.gravDir == 1 && player.grapCount == 0 && player.width == 20 && player.height == 42;
        return new TerrainSnapshot(left, top, 16, rows, player.width, player.height, grounded, supported,liquids.ToArray());
    }

    // Lightweight contact observation shared by movement drivers; no profile or safety gate.
    internal static bool Grounded(Player player)
    {
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
        return grounded;
    }

    internal static bool Loaded(int x,int y)=>WorldGen.InWorld(x,y,1)
        &&(Main.netMode!=NetmodeID.MultiplayerClient||Main.sectionManager?.TileLoaded(x,y)==true);
    internal static char ReadCell(int x,int y)=>Loaded(x,y)?ReadShape(Main.tile[x,y]):'?';
    internal static bool UnsupportedMotionSurface(int type)=>type>=0 && (TileID.Sets.IceSkateSlippery[type] ||
        type is TileID.HoneyBlock or TileID.Cobweb or TileID.Asphalt or TileID.SlimeBlock or TileID.FrozenSlimeBlock);
    internal static char ReadDryCell(int x,int y)=>ReadEnvironment(x,y).DryShape;

    internal static bool OrdinaryOverlayCell(TerrainCell cell)
        =>!cell.Actuated && (cell.TileType==null
            || cell.Shape=='.' && cell.TileType is TileID.Plants or TileID.Trees or TileID.LargePiles or TileID.ImmatureHerbs or TileID.Pots
            || cell.TileType is
            TileID.Dirt or TileID.Stone or TileID.Grass or TileID.Mud or TileID.ClayBlock
            || cell.TileType is int type && type<TileID.Count && TileID.Sets.Ore[type]);

    internal static TerrainCell ReadEnvironment(int x, int y)
    {
        return Loaded(x,y)?ReadEnvironment(Main.tile[x,y]):new('?',default);
    }

    internal static TerrainCell ReadEnvironment(Tile tile) => new(ReadShape(tile),
        tile.LiquidAmount==0?default:new(tile.LiquidType,tile.LiquidAmount),
        tile.HasTile?tile.TileType:null,tile.HasTile&&tile.IsActuated);

    private static char ReadShape(Tile tile)
    {
        int type = tile.TileType;
        // Custom collision hooks cannot be inferred from vanilla tile flags.
        if (tile.HasUnactuatedTile && type >= TileID.Count) return '?';
        // Vanilla lists cactus in the touch-damage table, but only applies
        // its damage in worlds with The Constant's rules.
        bool harmful = tile.HasUnactuatedTile && (type != TileID.Cactus || Main.dontStarveWorld)
            && (TileID.Sets.TouchDamageImmediate[type] > 0
            || TileID.Sets.TouchDamageHot[type] || TileID.Sets.TouchDamageBleeding[type]
            || TileID.Sets.TouchDamageDestroyTile[type] || TileID.Sets.Boulders[type]);
        return TerrainClassifier.Classify(true, tile.HasTile, tile.IsActuated,
            Main.tileSolid[type], Main.tileSolidTop[type], tile.TileFrameY,
            tile.IsHalfBlock, (int)tile.Slope, harmful);
    }
}
