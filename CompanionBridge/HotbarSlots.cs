using System;

namespace CompanionBridge;

// Public hotbar numbers are the keyboard labels 1..9,0; array indices stay internal.
internal static class HotbarSlots
{
    internal static int ToInventorySlot(int key)
    {
        if (key is < 0 or > 9) throw new ArgumentOutOfRangeException(nameof(key));
        return (key + 9) % 10;
    }
    internal static int? KeyForInventorySlot(int slot) => slot is >= 0 and < 10 ? (slot + 1) % 10 : null;
}
