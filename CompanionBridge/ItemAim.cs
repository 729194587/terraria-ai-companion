using System;
using Microsoft.Xna.Framework;

namespace CompanionBridge;

internal static class ItemAim
{
    // At PreItemCheck, SetZoom_World/SetZoom_MouseInWorld have already converted
    // physical screen input into unzoomed world-relative mouse coordinates.
    // Main.MouseWorld adds screenPosition (normal gravity). Do not transform
    // these values through the render matrix: that would double-apply zoom.
    internal static bool TryGetItemCheckMouse(Vector2 world, Vector2 screenPosition, out int x, out int y)
    {
        float rx = world.X - screenPosition.X, ry = world.Y - screenPosition.Y;
        x = y = 0;
        if (!float.IsFinite(rx) || !float.IsFinite(ry) || rx <= int.MinValue || rx >= int.MaxValue
            || ry <= int.MinValue || ry >= int.MaxValue) return false;
        x = (int)rx; y = (int)ry; // Same integer truncation used by PlayerInput.
        return true;
    }
}
