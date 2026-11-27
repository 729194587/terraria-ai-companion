using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Terraria;
using Terraria.DataStructures;
using Terraria.ModLoader;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

// A reference names one observed active item object, scoped to the bound actor and world.
internal static class ServerDrops
{
    private sealed class Stamp { internal readonly string Value = Guid.NewGuid().ToString("N"); }
    private static readonly ConditionalWeakTable<Item, Stamp> stamps = new();
    internal sealed record Drop(int Slot, Item Item, string Reference, int Type, int Prefix);

    // The stamp belongs to the observed object, so a reference is only valid while
    // that exact object still occupies the slot. A slot reused by a brand-new Item
    // instance is already safe: the new object has no stamp and Reference mints a
    // fresh one. The reinitialisation hooks cover the other case below.
    internal static string Reference(AiReadOnlyProtocol.Identity id, int slot, Item item)
    {
        var stamp = stamps.GetValue(item, _ => new Stamp());
        return $"{id.World}:{id.Generation}:{id.Slot}:{slot}:{stamp.Value}";
    }

    // Liveness tracks the observed object instance plus the metadata seen at
    // observation time. Type/prefix are part of that snapshot: a change makes the
    // observation stale, and CollectDropsTask retires the target on a failed
    // observation rather than trying to follow the mutation. A slot that no longer
    // exists (world unload, array replacement) can never keep a drop live.
    internal static bool Live(Drop drop)
    {
        var slots = Main.item;
        if (drop == null || drop.Item == null || slots == null || drop.Slot < 0 || drop.Slot >= slots.Length)
            return false;
        return ReferenceEquals(slots[drop.Slot], drop.Item)
            && drop.Item.active && !drop.Item.IsAir && drop.Item.type == drop.Type && drop.Item.prefix == drop.Prefix
            && drop.Reference != null
            && stamps.TryGetValue(drop.Item, out var stamp)
            && drop.Reference.EndsWith($":{drop.Slot}:{stamp.Value}", StringComparison.Ordinal);
    }

    // Clones have a separate weak-table key. Reset the stamp on lifecycle
    // hooks that can reinitialize the original object in place.
    internal static void ResetIdentity(Item item)
    {
        if (item != null) stamps.Remove(item);
    }

    internal static CollectDropsTask Current { get; set; }

    private static long reportedGeneration;
    internal static void Rearm(Player player)
    {
        if (reportedGeneration != 0 && (reportedGeneration != ProbeSession.ActorGeneration ||
            Array.FindIndex(player.inventory, 0, 50, i => i.IsAir) >= 0))
            reportedGeneration = 0;
    }

    public static bool Allows(Player player, Item item)
    {
        if (!ReferenceEquals(player, ProbeSession.Actor)) return true;
        if (Current != null && NativeSurvival.Owns(player) && !Current.Allows(item)) return false;
        // Full players may never win FindOwner. Count only contact with an otherwise eligible, unclaimed drop.
        if (item.playerIndexTheItemIsReservedFor == Main.maxPlayers && item.keepTime == 0 && item.ownIgnore != player.whoAmI &&
            item.noGrabDelay == 0 && item.shimmerTime == 0 && (!item.shimmered || item.velocity.Length() < 0.2) &&
            player.Hitbox.Intersects(item.Hitbox) && reportedGeneration != ProbeSession.ActorGeneration &&
            NativeSurvival.Owns(player) && player.CanAcceptItemIntoInventory(item) && ItemLoader.CanPickup(item, player))
            ReportRemainder(player, item, 0);
        return true;
    }

    internal static void ReportRemainder(Player player, Item item, int received)
    {
        if (reportedGeneration == ProbeSession.ActorGeneration || player.dead || player.ghost || !item.active || item.IsAir ||
            item.uniqueStack && player.HasItem(item.type) || player.ItemSpace(item).CanTakeItem) return;
        var tile = item.Center.ToTileCoordinates();
        string key = item.ModItem?.FullName ?? "Terraria/" + Terraria.ID.ItemID.Search.GetName(item.type);
        ServerGameMessages.InventoryFull($"背包放不下 {key}。这次拾取 {received} 个，地面 ({tile.X},{tile.Y}) 还剩 {item.stack} 个。可先存放多余物资，腾出空间后回原处补捡。");
        reportedGeneration = ProbeSession.ActorGeneration;
    }

    internal static void ResetPickupNotice() => reportedGeneration = 0;

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

// Native defaults and spawn can start a new incarnation of the same object.
public sealed class DropIdentityGlobalItem : GlobalItem
{
    public override void SetDefaults(Item entity) => ServerDrops.ResetIdentity(entity);
    public override void OnSpawn(Item item, IEntitySource source) { ServerDrops.ResetIdentity(item); ServerItemOpening.Spawn(item,source); }
}
