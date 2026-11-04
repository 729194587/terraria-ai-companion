using System;

namespace CompanionBridge.Motion;

// World pixels, Y increases downward. X bounds constrain the player's LEFT
// collision edge, not its centre; SurfaceY constrains Py + PlayerH (the feet).
internal sealed record LandingRegion
{
    public float MinPlayerLeftX { get; }
    public float MaxPlayerLeftX { get; }
    public float SurfaceY { get; }
    internal const float FeetTolerance = 0.5f;

    internal LandingRegion(float minPlayerLeftX, float maxPlayerLeftX, float surfaceY)
    {
        if (!float.IsFinite(minPlayerLeftX) || !float.IsFinite(maxPlayerLeftX)
            || !float.IsFinite(surfaceY) || minPlayerLeftX > maxPlayerLeftX)
            throw new ArgumentException("Landing region requires finite ordered coordinates");
        MinPlayerLeftX = minPlayerLeftX; MaxPlayerLeftX = maxPlayerLeftX; SurfaceY = surfaceY;
    }

    internal float Distance(PhysicsState s) => Math.Max(0, Math.Max(MinPlayerLeftX - s.Px, s.Px - MaxPlayerLeftX))
        + Math.Max(0, Math.Abs(s.Py + PhysicsSimulator.PlayerH - SurfaceY) - FeetTolerance);
    internal bool Contains(PhysicsState s) => Distance(s) == 0;
    internal bool Arrived(PhysicsState s, IMotionWorld world) => Contains(s) && s.Grounded
        && Math.Abs(s.Vx) <= 0.1f && Math.Abs(s.Vy) <= 0.1f
        && s.ReleaseJump && s.JumpFramesLeft == 0 && world.SafeLanding(s) && world.SafeFlatLanding(s);
}

// Shared by native navigation and offline terrain fixtures, so support checks
// are tested against real tile coordinates rather than collision-world guesses.
internal static class MotionSupport
{
    internal const float MinimumSupportPixels = 8;

    // Region goals accept horizontal contact surfaces, including the top of a
    // half block. Sloped poses use SafeLanding but cannot define a flat region.
    internal static bool SafeFlatLanding(PhysicsState state, Func<int, int, char> readCell)
    {
        if (!SafeLanding(state, readCell)) return false;
        float bottom = state.Py + PhysicsSimulator.PlayerH;
        int surface = (int)Math.Round(bottom / 8) * 8;
        if (Math.Abs(bottom - surface) > LandingRegion.FeetTolerance) return false;
        int row = surface / 16;
        bool half = surface % 16 != 0;
        float support = 0;
        for (int x = (int)Math.Floor(state.Px / 16); x < (int)Math.Ceiling((state.Px + PhysicsSimulator.PlayerW) / 16); x++)
        {
            char cell=readCell(x,row);
            if (half ? cell=='h' : cell is '#' or '=')
                support += Math.Min(state.Px + PhysicsSimulator.PlayerW, (x + 1) * 16) - Math.Max(state.Px, x * 16);
        }
        return support >= MinimumSupportPixels;
    }

    internal static bool SafeLanding(PhysicsState state, Func<int, int, char> readCell)
        => LandingRejection(state, readCell) == null;

    internal sealed record Assessment(float? SupportPixels, string Rejection);

    internal static string LandingRejection(PhysicsState state, Func<int, int, char> readCell)
        => CheckLanding(state, readCell, out _);

    internal static Assessment Assess(PhysicsState state, Func<int, int, char> readCell)
    {
        string rejection=CheckLanding(state,readCell,out float? pixels);
        return new(pixels,rejection);
    }

    // Navigation shares the scan without allocating an assessment for every candidate.
    private static string CheckLanding(PhysicsState state, Func<int, int, char> readCell, out float? supportPixels)
    {
        supportPixels=null;
        if (!state.Grounded) return "not grounded";
        float left = state.Px, right = left + PhysicsSimulator.PlayerW;
        float bottom = state.Py + PhysicsSimulator.PlayerH;
        float supportedWidth = 0;
        for (int x = (int)Math.Floor(left / 16); x < (int)Math.Ceiling(right / 16); x++)
        {
            float localLeft = Math.Max(0, left - x * 16), localRight = Math.Min(16, right - x * 16);
            for (int y = (int)Math.Floor(state.Py / 16); y < (int)Math.Ceiling((bottom - 4) / 16); y++)
            {
                char cell = readCell(x, y);
                if (cell is '?' or '!') return $"unknown/hazard body cell at ({x},{y}): {cell}";
                if (TerrainClassifier.IntersectsSolid(cell, localLeft, localRight,
                    state.Py - y * 16, bottom - 4 - y * 16)) return $"body intersects solid at ({x},{y}): {cell}";
            }
            bool supported = false;
            for (int y = (int)Math.Floor((bottom - 16) / 16); y <= (int)Math.Floor((bottom + 4) / 16); y++)
            {
                char cell = readCell(x, y);
                if (cell is '?' or '!') return $"unknown/hazard support cell at ({x},{y}): {cell}";
                float surface = y * 16 + TerrainClassifier.SurfaceOffset(cell, localLeft, localRight);
                supported |= Math.Abs(surface - bottom) <= 4;
            }
            // Terraria can stand with part of its body over an edge. Count only
            // actual foot contact, not empty tiles under a padded bounding box.
            if (supported) supportedWidth += localRight - localLeft;
        }
        supportPixels=supportedWidth;
        return supportedWidth >= MinimumSupportPixels ? null
            : $"insufficient foot support: {supportedWidth:F3}px < {MinimumSupportPixels}px";
    }
}
