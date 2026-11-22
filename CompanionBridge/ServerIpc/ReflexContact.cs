using System;
using System.Numerics;

namespace CompanionBridge.ServerIpc;

internal static class ReflexContact
{
    internal const int Horizon = 18;

    // Sweep relative centers against the sum of both hitbox half-extents.
    internal static int? FirstTick(Vector2 offset, Vector2 relativeVelocity, Vector2 half,
        float npcGravity, int horizon)
    {
        if (Math.Abs(offset.X) <= half.X && Math.Abs(offset.Y) <= half.Y) return 0;
        for (int tick = 1; tick <= horizon; tick++)
        {
            relativeVelocity.Y += npcGravity;
            float enter = 0, exit = 1;
            if (Overlaps(offset.X, relativeVelocity.X, half.X, ref enter, ref exit)
                && Overlaps(offset.Y, relativeVelocity.Y, half.Y, ref enter, ref exit)) return tick;
            offset += relativeVelocity;
        }
        return null;
    }

    private static bool Overlaps(float position, float speed, float half, ref float enter, ref float exit)
    {
        if (speed == 0) return Math.Abs(position) <= half;
        float a = (-half - position) / speed, b = (half - position) / speed;
        enter = Math.Max(enter, Math.Min(a, b));
        exit = Math.Min(exit, Math.Max(a, b));
        return enter <= exit;
    }
}
