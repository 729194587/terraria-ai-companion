using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace VirtualPlayerEngineProbe;

// Explicit G2 debugging only. Character initialization and synchronization must
// never apply this overlay. Retain the original Item instance, including mod data.
internal static class ProbeDebugAppearance
{
    private static Player owner;
    private static Item savedHelmet, debugHelmet;
    private static Color savedShirt;
    internal static bool IsApplied(Player p) => ReferenceEquals(owner, p);

    internal static void Clear()
    {
        owner = null;
        savedHelmet = debugHelmet = null;
        savedShirt = default;
    }

    internal static void Toggle(Player expected)
    {
        VirtualPlayerEngineProbe.Reservation.AssertOwner(Program.IsMainThread);
        lock (VirtualPlayerEngineProbe.Reservation.Gate)
        {
            NetworkStateSync.Require(expected);
            if (owner == null)
            {
                var helmet = new Item();
                helmet.SetDefaults(ItemID.IronHelmet);
                owner = expected;
                savedHelmet = expected.armor[0];
                savedShirt = expected.shirtColor;
                debugHelmet = helmet;
                expected.armor[0] = helmet;
                expected.shirtColor = new Color(190, 65, 170);
            }
            else
            {
                if (!ReferenceEquals(owner, expected) || !ReferenceEquals(expected.armor[0], debugHelmet))
                    throw new InvalidOperationException("Debug overlay changed externally; refusing to overwrite current equipment.");
                expected.armor[0] = savedHelmet;
                expected.shirtColor = savedShirt;
                Clear();
            }
            NetworkStateSync.Full(expected);
        }
    }
}
