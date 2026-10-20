using System;

namespace CompanionBridge.Motion;

// Shared by native navigation and offline terrain fixtures, so support checks
// are tested against real tile coordinates rather than collision-world guesses.
internal static class MotionSupport
{
    internal const float MinimumSupportPixels = 8;

    internal static bool SafeLanding(PhysicsState state, Func<int, int, char> readCell)
    {
        if (!state.Grounded) return false;
        float left = state.Px, right = left + PhysicsSimulator.PlayerW;
        float bottom = state.Py + PhysicsSimulator.PlayerH;
        float supportedWidth = 0;
        for (int x = (int)Math.Floor(left / 16); x < (int)Math.Ceiling(right / 16); x++)
        {
            float localLeft = Math.Max(0, left - x * 16), localRight = Math.Min(16, right - x * 16);
            for (int y = (int)Math.Floor(state.Py / 16); y < (int)Math.Ceiling((bottom - 4) / 16); y++)
            {
                char cell = readCell(x, y);
                if (cell is '?' or '!' || TerrainClassifier.IntersectsSolid(cell, localLeft, localRight,
                    state.Py - y * 16, bottom - 4 - y * 16)) return false;
            }
            bool supported = false;
            for (int y = (int)Math.Floor((bottom - 16) / 16); y <= (int)Math.Floor((bottom + 4) / 16); y++)
            {
                char cell = readCell(x, y);
                if (cell is '?' or '!') return false;
                float surface = y * 16 + TerrainClassifier.SurfaceOffset(cell, localLeft, localRight);
                supported |= Math.Abs(surface - bottom) <= 4;
            }
            // Terraria can stand with part of its body over an edge. Count only
            // actual foot contact, not empty tiles under a padded bounding box.
            if (supported) supportedWidth += localRight - localLeft;
        }
        return supportedWidth >= MinimumSupportPixels;
    }
}
