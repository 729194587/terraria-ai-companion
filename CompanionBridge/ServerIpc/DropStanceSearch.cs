using System;
using Microsoft.Xna.Framework;

namespace CompanionBridge.ServerIpc;

// Pure geometry for the drop-collection stance search. Kept free of Terraria types
// so the native grab rectangle can be checked without a world or a running game.
internal static class DropStanceSearch
{
    // World-pixel bounds of the stance search for one drop: the item hitbox grown by
    // the full Player.GetItemGrabRange on every side, then clipped to the world.
    // The range is never reduced to keep the search cheap; Approach bounds the work
    // by probe budget and 8-pixel grid alignment instead.
    internal static Rectangle Bounds(Rectangle itemHitbox, int playerWidth, int playerHeight,
        int grabRange, int maxTilesX, int maxTilesY)
    {
        int range = Math.Max(0, grabRange);
        int left = (int)Math.Max(32, (long)itemHitbox.Left - playerWidth - range + 1);
        int right = (int)Math.Min((maxTilesX - 2) * 16 - playerWidth, (long)itemHitbox.Right + range - 1);
        int top = (int)Math.Max(32, (long)itemHitbox.Top - range + 1);
        int bottom = (int)Math.Min((maxTilesY - 2) * 16 - 1, (long)itemHitbox.Bottom + range + playerHeight - 1);
        return new Rectangle(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }
}
