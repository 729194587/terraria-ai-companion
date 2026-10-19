using System.Diagnostics;
using Terraria.ModLoader;

namespace CompanionBridge;

public sealed class CompanionBridge : Mod
{
    internal static ModKeybind ToggleAutoMoveKeybind { get; private set; }
    private static bool _autoMoveEnabled;
    private static long _autoMoveStartedAt;

    internal static bool AutoMoveEnabled
    {
        get => _autoMoveEnabled;
        set
        {
            _autoMoveEnabled = value;
            _autoMoveStartedAt = value ? Stopwatch.GetTimestamp() : 0;
        }
    }

    internal static bool ExpireAutoMove()
    {
        if (!AutoMoveEnabled || Stopwatch.GetElapsedTime(_autoMoveStartedAt).TotalSeconds < 10)
            return false;

        AutoMoveEnabled = false;
        return true;
    }

    public override void Load()
    {
        AutoMoveEnabled = false;
        ToggleAutoMoveKeybind = KeybindLoader.RegisterKeybind(this, "ToggleAutoMove", "P");
        Logger.Info("CompanionBridge M1.1 focus experiment loaded; PostUpdateRunSpeeds, 10-second limit; auto move is off.");
    }

    public override void Unload()
    {
        AutoMoveEnabled = false;
        ToggleAutoMoveKeybind = null;
    }
}
