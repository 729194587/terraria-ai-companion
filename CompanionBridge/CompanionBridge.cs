using Terraria.ModLoader;

namespace CompanionBridge;

public sealed class CompanionBridge : Mod
{
    internal static ModKeybind ToggleAutoMoveKeybind { get; private set; }
    internal static bool AutoMoveEnabled { get; set; }

    public override void Load()
    {
        AutoMoveEnabled = false;
        ToggleAutoMoveKeybind = KeybindLoader.RegisterKeybind(this, "ToggleAutoMove", "P");
        Logger.Info("CompanionBridge M1.0 loaded; auto move is off.");
    }

    public override void Unload()
    {
        AutoMoveEnabled = false;
        ToggleAutoMoveKeybind = null;
    }
}
