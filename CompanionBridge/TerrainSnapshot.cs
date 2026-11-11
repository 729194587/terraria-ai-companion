namespace CompanionBridge;

// Tile indices touched by [left,right) x [top,bottom); maxima are inclusive.
// No epsilon: a right/bottom edge exactly on a tile boundary excludes that tile.
internal sealed record BodyTileBounds(int MinX,int MinY,int MaxX,int MaxY)
{
    internal static BodyTileBounds FromPixels(double left,double top,double width,double height)
    {
        if(!double.IsFinite(left)||!double.IsFinite(top)||!double.IsFinite(width)||!double.IsFinite(height)||width<=0||height<=0)
            throw new System.ArgumentException("Finite positive body bounds required.");
        return new((int)System.Math.Floor(left/16),(int)System.Math.Floor(top/16),
            (int)System.Math.Ceiling((left+width)/16)-1,(int)System.Math.Ceiling((top+height)/16)-1);
    }
}

// Compact collision categories, not tile IDs. No Terraria objects cross the thread boundary.
internal sealed record TerrainSnapshot(int OriginX, int OriginY, int TileSize, string[] Rows,
    int PlayerWidth, int PlayerHeight, bool Grounded, bool Supported, TerrainLiquidCell[] Liquids=null);

// Liquid entries use absolute tile coordinates. Only nonempty, observed cells
// are emitted, and unknown coverage remains represented by the shape grid.
internal sealed record TerrainLiquidCell(int X,int Y,string Type,byte Amount);

internal enum LiquidKind { None, Water, Lava, Honey, Shimmer, Unknown }

internal readonly record struct TileLiquid
{
    internal byte Amount { get; }
    internal LiquidKind Kind { get; }
    private int NativeType { get; }
    internal TileLiquid(int nativeType,byte amount)
    {
        Amount=amount;
        NativeType=amount==0?0:nativeType;
        Kind=amount==0?LiquidKind.None:nativeType switch
        {
            0=>LiquidKind.Water,1=>LiquidKind.Lava,2=>LiquidKind.Honey,3=>LiquidKind.Shimmer,_=>LiquidKind.Unknown
        };
    }
    internal long Stamp => NativeType*256L+Amount;
    internal string Name => Kind switch
    {
        LiquidKind.None=>"none",LiquidKind.Water=>"water",LiquidKind.Lava=>"lava",
        LiquidKind.Honey=>"honey",LiquidKind.Shimmer=>"shimmer",_=>"unknown"
    };

    // The pinned WetCollision/LavaCollision IL computes a fractional
    // liquid top, then subtracts the truncated offset from a height of 16.
    internal bool OverlapsVertical(int tileY,float top,float bottom)
    {
        float offset=(256-Amount)/16f;
        float surface=tileY*16+offset;
        return Amount>0&&bottom>surface&&top<surface+16-(int)offset;
    }
}

// Internal observation. Collision air can still contain a real tile.
internal readonly record struct TerrainCell(char Shape,TileLiquid Liquid,int? TileType=null,bool Actuated=false)
{
    internal bool HasTile=>TileType.HasValue;
    // Legacy structural-only callers cannot certify liquid trajectories.
    internal char DryShape => Shape=='?'||Liquid.Amount==0?Shape:'!';
}

internal static class TerrainClassifier
{
    // Shape-aware clearance for a padded standing area. SolidCollision treats
    // slopes as full blocks in 1.4.4.9; this is the existing Python View.clear
    // interval test, shared here rather than reintroducing that false positive.
    internal static bool IntersectsSolid(char cell, float left, float right, float top, float bottom)
    {
        if (cell is not ('#' or 'h' or '1' or '2' or '3' or '4')) return false;
        float low = cell == '1' ? left : cell == '2' ? 16 - right : cell == 'h' ? 8 : 0;
        float high = cell == '3' ? 16 - left : cell == '4' ? right : 16;
        return left < right && low < bottom && high > top;
    }

    internal static char Classify(bool loaded, bool active, bool actuated, bool solid,
        bool solidTop, int frameY, bool halfBlock, int slope, bool harmful)
    {
        if (!loaded) return '?';
        if (!active || actuated) return '.';
        if (harmful) return '!';
        // One-way top surfaces never become horizontal walls. Non-top furniture frames do not support.
        if (solidTop)
        {
            // TileCollision accepts tileSolid OR (tileSolidTop && frameY == 0).
            // Actual platforms can have nonzero frameY for their style and tileSolid=true;
            // furniture with tileSolid=false only supports its top frame.
            if (!solid && frameY != 0) return '.';
            return slope switch { 0 => '=', 1 => 'a', 2 => 'b', _ => '?' };
        }
        if (!solid) return '.';
        if (slope != 0) return slope is >= 1 and <= 4 ? (char)('0' + slope) : '?';
        return halfBlock ? 'h' : '#';
    }

    // Same footprint-edge offsets used by Collision.SlopeCollision for ordinary floor slopes.
    // 1/a descends to the right; 2/b rises to the right. 3/4 have a flat top, sloped underside.
    internal static float SurfaceOffset(char cell, float left, float right) => cell switch
    {
        '1' or 'a' => System.Math.Clamp(left, 0, 16),
        '2' or 'b' => 16 - System.Math.Clamp(right, 0, 16),
        'h' => 8,
        '#' or '=' or '3' or '4' => 0,
        _ => float.NaN
    };
}
