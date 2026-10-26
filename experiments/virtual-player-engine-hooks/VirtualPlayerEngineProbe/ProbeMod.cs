using Terraria.ModLoader;

namespace VirtualPlayerEngineProbe;

// The experimental package and CompanionBridge compile the same runtime sources.
public sealed class ProbeMod : Mod
{
    private readonly VirtualPlayerEngineProbe runtime = new();
    public override void Load() => runtime.Load(this);
    public override void Unload() => runtime.Unload();
}
