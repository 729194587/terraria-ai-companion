using Terraria.ModLoader;

namespace CompanionBridge.Common.Systems;

public sealed class CompanionSystem : ModSystem
{
    public override void OnWorldUnload() => CompanionBridge.AutoMoveEnabled = false;
}
