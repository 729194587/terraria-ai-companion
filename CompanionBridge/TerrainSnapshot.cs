namespace CompanionBridge;

// Compact collision categories, not tile IDs. No Terraria objects cross the thread boundary.
internal sealed record TerrainSnapshot(int OriginX, int OriginY, int TileSize, string[] Rows,
    int PlayerWidth, int PlayerHeight, bool Grounded, bool Supported);

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
        bool solidTop, int frameY, bool halfBlock, int slope, bool liquid, bool harmful)
    {
        if (!loaded) return '?';
        if (liquid) return '!';
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
