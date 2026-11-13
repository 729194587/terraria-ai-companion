using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Terraria;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

// A reference names one observed active item object, scoped to the bound actor and world.
internal static class ServerDrops
{
    private sealed class Stamp { internal readonly string Value = Guid.NewGuid().ToString("N"); }
    private static readonly ConditionalWeakTable<Item, Stamp> stamps = new();
    internal sealed record Drop(int Slot, Item Item, string Reference, int Type, int Prefix);
    internal static string Reference(AiReadOnlyProtocol.Identity id, Item item) =>
        $"{id.World}:{id.Generation}:{id.Slot}:{stamps.GetValue(item, _ => new Stamp()).Value}";

    internal static bool Live(Drop drop) => ReferenceEquals(Main.item[drop.Slot], drop.Item)
        && drop.Item.active && !drop.Item.IsAir && drop.Item.type == drop.Type && drop.Item.prefix == drop.Prefix;

    internal static CollectDropsTask Current { get; set; }

    public static bool Allows(Player player, Item item) =>
        Current == null || !NativeSurvival.Owns(player) || Current.Allows(item);

    internal static Item Pickup(On_Player.orig_PickupItem orig, Player player, int playerIndex, int itemIndex, Item item)
    {
        var task = Current;
        if (task == null || !NativeSurvival.Owns(player) || !task.Allows(item))
            return orig(player, playerIndex, itemIndex, item);
        int type = item.type, prefix = item.prefix, stack = item.stack;
        long Count()
        {
            var items = player.inventory.Concat(player.bank4.item);
            if (type is >= 71 and <= 74)
                return items.Where(i => !i.IsAir && i.type is >= 71 and <= 74)
                    .Sum(i => (long)i.stack * CoinValue(i.type));
            return items.Where(i => !i.IsAir && i.type == type && i.prefix == prefix).Sum(i => (long)i.stack);
        }
        long before = Count();
        var remainder = orig(player, playerIndex, itemIndex, item);
        int removed = stack - (remainder.active && !remainder.IsAir && remainder.type == type && remainder.prefix == prefix
            ? remainder.stack : 0);
        long delta = Count() - before;
        if (removed > 0 && delta == removed * (type is >= 71 and <= 74 ? CoinValue(type) : 1))
            task.Credit(itemIndex, item, type, removed);
        else if (removed > 0 || delta != 0) task.Uncertain();
        return remainder;
    }

    private static long CoinValue(int type) => type switch { 71 => 1, 72 => 100, 73 => 10000, _ => 1000000 };
}
