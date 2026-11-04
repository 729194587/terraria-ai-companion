namespace CompanionBridge.Motion;

// Audited against the installed Terraria 1.4.4.9: MonsterBanner (147) affects
// banner combat bonuses; BabyBird (216) maintains the finch minion. Neither
// changes player movement. Swiftness (3) is admitted only alongside an exact
// measured effective-physics check in MobilityTracker. Other buffs remain excluded.
internal static class NavigationBuffPolicy
{
    internal static string Rejection(int type, int timeTicks, int slot, string contentKey)
    {
        if (timeTicks <= 0 || type is 3 or 147 or 216) return null;
        return $"unsupported_buff: slot={slot}, type={type}, contentKey={contentKey}, timeTicks={timeTicks}";
    }
}
