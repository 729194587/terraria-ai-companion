using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using CompanionBridge.ServerIpc;
using Result = CompanionBridge.ServerIpc.AiAtomicActions.NativeResult;

namespace VirtualPlayerEngineProbe;

internal static partial class ActorContainer
{
    internal sealed class SortAction(AiReadOnlyProtocol.Identity identity, string operation, JsonElement arguments)
    {
        private readonly Player player = (Player)identity.Actor;
        private readonly bool inventory = operation == "sort_inventory";
        private Item[] items;
        private Session session;
        private Snapshot[] before, after, other;
        private Item[] otherItems;
        private int[] changed = [];
        private int selected, localSlot;
        private Item mouse;
        private object file;
        private ulong mutationTick;
        private Result terminal;
        private sealed record Snapshot(Item Item, ActorInventory.Core Core, ModItem Mod, int RawStack, bool Ammo)
        {
            internal static Snapshot Read(Item item) => new(item, ActorInventory.Read(item), item.ModItem, item.stack, item.FitsAmmoSlot());
        }

        private bool Bound() => ServerAiNativeActions.Owned(identity) && player.active && !player.dead && !player.ghost
            && player.selectedItem == selected && Main.myPlayer == localSlot
            && ReferenceEquals(Main.mouseItem, mouse) && ReferenceEquals(Main.ActivePlayerFileData, file)
            && (inventory ? ReferenceEquals(items, player.inventory)
                : ReferenceEquals(opened, session) && Check(session, identity) == null
                && ReferenceEquals(items, session.Target.Record.item));

        private Result Report(string status, string reason, bool verified = false) =>
            new(status, reason, new {
                mutationAttempted = before != null, mutationGameUpdate = mutationTick, observedGameUpdate = Main.GameUpdateCount,
                sortVerified = verified, chestIndex = inventory ? null : session?.Target.Index,
                changedSlots = verified ? changed.Select(slot => new {
                    slot, typeId = after[slot].Core.Type, stack = after[slot].Core.Stack,
                    prefix = after[slot].Core.Prefix, favorited = after[slot].Core.Favorite,
                    instanceId = ActorInventory.Instance(after[slot].Item)
                }).ToArray() : []
            }, ServerAiNativeActions.Released(player), verified, InputsAcquired: false);

        internal Result Poll(bool cancel = false)
        {
            if (terminal != null) return terminal;
            try
            {
                if (after != null)
                {
                    if (Main.GameUpdateCount <= mutationTick)
                        return cancel ? terminal = Report("unknown", "sort_confirmation_interrupted")
                            : Report("running", "awaiting_sort_confirmation");
                    bool verified = Bound() && items.Select(Snapshot.Read).SequenceEqual(after);
                    return terminal = verified ? Report("completed", "native_sort_verified", true)
                        : Report("unknown", "sort_changed_before_confirmation");
                }
                if (cancel) return terminal = Report("cancelled", "explicit_stop");
                return Execute();
            }
            catch { return terminal = Report("unknown", "native_sort_exception"); }
        }

        private Result Execute()
        {
            session = opened;
            if (!inventory && !Fresh(session, identity, arguments.GetProperty("containerToken").GetString()))
                return terminal = Report("failed", "stale_container_token");
            if (player.selectedItem == 58 || player.itemAnimation != 0 || player.itemTime != 0 || player.reuseDelay != 0 || player.channel)
                return terminal = Report("failed", "inventory_item_busy");
            items = inventory ? player.inventory : session.Target.Record.item;
            otherItems = inventory ? session?.Target.Record.item : player.inventory;
            if (items.Any(item => item == null || item.stack < 0 || !item.IsAir && item.stack > item.maxStack)
                || items.Distinct(ReferenceEqualityComparer.Instance).Count() != items.Length
                || otherItems != null && items.Intersect(otherItems, ReferenceEqualityComparer.Instance).Any())
                return terminal = Report("failed", "invalid_sort_storage");

            selected = player.selectedItem;
            localSlot = Main.myPlayer;
            mouse = Main.mouseItem;
            file = Main.ActivePlayerFileData;
            var baseline = items.Select(Snapshot.Read).ToArray();
            // Native SortCoins can emit world drops. Reject inputs that could overflow
            // before calling native code, using its denomination and available-slot rules.
            if (inventory && !player.HasItem(905) && CoinOverflow(baseline))
                return terminal = Report("failed", "native_sort_coin_overflow");
            other = otherItems?.Select(Snapshot.Read).ToArray();
            before = baseline;
            mutationTick = Main.GameUpdateCount;
            if (!inventory) session.Token = null;
            if (inventory)
                for (int slot = 0; slot < items.Length; slot++) ActorInventory.InvalidateSlot(identity, slot);

            ActorSorting.Run(identity, inventory ? null : items);
            if (!Bound() || other != null && !otherItems.Select(Snapshot.Read).SequenceEqual(other))
                return terminal = Report("unknown", "sort_binding_or_storage_changed");
            var sorted = items.Select(Snapshot.Read).ToArray();
            if (!Verify(sorted)) return terminal = Report("unknown", "native_sort_result_not_verified");
            changed = Enumerable.Range(0, items.Length).Where(slot => before[slot] != sorted[slot]).ToArray();
            after = sorted;
            foreach (int slot in changed)
            {
                if (!Bound() || Snapshot.Read(items[slot]) != sorted[slot])
                    return terminal = Report("unknown", "sort_changed_during_sync");
                if (inventory) NetMessage.SendData(MessageID.SyncEquipment, -1, -1, null, player.whoAmI, slot);
                else SyncChestSlot(session.Target, slot);
            }
            return Report("running", "awaiting_sort_confirmation");
        }

        private static bool Coin(int type) => type is >= ItemID.CopperCoin and <= ItemID.PlatinumCoin;

        private static bool CoinOverflow(Snapshot[] rows)
        {
            long value = CoinValue(rows.Take(58));
            if (value == 0) return false;
            var split = Utils.CoinsSplit(value);
            long required = split.Take(3).Sum(count => (count + 98L) / 99)
                + (split[3] + (long)Item.CommonMaxStack - 1) / Item.CommonMaxStack;
            // The vanilla coin pass runs only if it finds enough existing coin stacks.
            if (rows.Take(58).Count(row => Coin(row.Core.Type)) < required) return false;
            return rows.Take(54).Count(row => row.Core.Type == 0 || Coin(row.Core.Type)) < required;
        }

        private static long CoinValue(System.Collections.Generic.IEnumerable<Snapshot> rows) =>
            rows.Where(row => Coin(row.Core.Type)).Sum(row => (long)row.Core.Stack *
                (row.Core.Type switch { ItemID.PlatinumCoin => 1_000_000, ItemID.GoldCoin => 10_000, ItemID.SilverCoin => 100, _ => 1 }));

        private bool Verify(Snapshot[] sorted)
        {
            if (sorted.Select(row => row.Item).Distinct(ReferenceEqualityComparer.Instance).Count() != items.Length
                || sorted.Any(row => row.RawStack < 0 || row.Core.Stack > row.Core.MaxStack)) return false;
            var originals = before.ToDictionary(row => row.Item, (IEqualityComparer<Item>)ReferenceEqualityComparer.Instance);
            foreach (var row in sorted.Where(row => row.Core.Type != 0))
            {
                if (inventory && Coin(row.Core.Type)) continue;
                if (!originals.TryGetValue(row.Item, out var old) || !ReferenceEquals(old.Mod, row.Mod)
                    || old.Core.Type != row.Core.Type || old.Core.Prefix != row.Core.Prefix || old.Core.MaxStack != row.Core.MaxStack)
                    return false;
            }
            // General sorting leaves favorites and hotbar slots alone. The native
            // inventory button's coin and ammo passes can still touch their own items.
            for (int slot = 0; slot < before.Length; slot++)
            {
                var old = before[slot];
                if (old == sorted[slot]) continue;
                if (inventory && slot == 58) return false;
                if ((old.Core.Favorite || inventory && slot < 10) &&
                    !(inventory && (Coin(old.Core.Type) || Coin(sorted[slot].Core.Type) && old.Core.Type == 0
                        || old.Core.Type != 0 && old.Ammo || old.Core.Type == 0 && sorted[slot].Ammo)))
                    return false;
            }
            var left = before.Where(row => row.Core.Type != 0 && (!inventory || !Coin(row.Core.Type)))
                .GroupBy(row => (row.Core.Type, row.Core.Prefix)).ToDictionary(g => g.Key, g => g.Sum(row => (long)row.Core.Stack));
            var right = sorted.Where(row => row.Core.Type != 0 && (!inventory || !Coin(row.Core.Type)))
                .GroupBy(row => (row.Core.Type, row.Core.Prefix)).ToDictionary(g => g.Key, g => g.Sum(row => (long)row.Core.Stack));
            return left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out long count) && count == pair.Value)
                && (!inventory || CoinValue(before) == CoinValue(sorted));
        }
    }
}
