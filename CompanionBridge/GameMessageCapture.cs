using System;

namespace CompanionBridge;

// ChatHelper -> Main.NewTextMultiline must produce one event; own replies/cache replay produce none.
internal static class GameMessageCapture
{
    [ThreadStatic] private static int _depth;

    internal static void Suppress(Action display)
    {
        _depth++;
        try { display(); }
        finally { _depth--; }
    }

    internal static void Run(Action display, Action capture)
    {
        bool outermost = _depth == 0;
        Suppress(display);
        if (outermost) capture();
    }
}
