using System;
using System.Numerics;

namespace CompanionBridge.ServerIpc;

internal static class ReflexContact
{
    internal const int Horizon = 18;
    // Pixels beyond the combined hitboxes. Five tiles leave time to draw a
    // weapon; three extra tiles on exit absorb a short knockback.
    internal const int EnterGap = 80, LeaveGap = 128;
    // Centers in pixels. A 24-tile firing lane fits the ordinary bow profile.
    internal const int RangedEnter = 384, RangedLeave = 480;
    internal const int QuietUpdates = 24; // 0.4 s at 60 game updates/s.

    internal static bool Near(Vector2 offset, Vector2 half, Vector2 velocity, bool retaining)
    {
        var gap = Vector2.Max(Vector2.Abs(offset) - half, Vector2.Zero);
        int margin = retaining ? LeaveGap : EnterGap;
        return gap.LengthSquared() <= margin * margin
            && (retaining || Vector2.Dot(offset, velocity) <= 0);
    }

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
