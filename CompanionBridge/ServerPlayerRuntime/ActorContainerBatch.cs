using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using CompanionBridge.ServerIpc;
using Result = CompanionBridge.ServerIpc.AiAtomicActions.NativeResult;

namespace VirtualPlayerEngineProbe;

internal static partial class ActorContainer
{
    internal static bool IsBatch(string operation) =>
        operation is "quick_stack_nearby" or "quick_stack_container" or "loot_all" or "deposit_all" or "restock" or "transfer_container_amount";

    internal sealed class Batch(AiReadOnlyProtocol.Identity identity, string operation, JsonElement arguments)
    {
        private const int MoveLimit = 128;
        private readonly Player player = (Player)identity.Actor;
        private readonly bool nearby = operation == "quick_stack_nearby";
        private readonly bool byAmount = operation == "transfer_container_amount";
        private readonly bool incoming = operation is "loot_all" or "restock"
            || operation == "transfer_container_amount" && arguments.GetProperty("direction").GetString() == "take";
        private string amountLimitReason;
        private readonly List<MoveReceipt> moves = new();
        private readonly List<Blocked> blocked = new();
        private Storage inventory;
        private Storage[] chests;
        private Session session;
        private Result terminal;
        private ulong mutationTick;
        private bool attempted;
        private int selected;
        private object localPlayerFile, mouseItem;
        private int localSlot;

        private sealed record Snapshot(Item Item, ActorInventory.Core Core, int RawStack);
        private sealed record MoveReceipt(int InventorySlot, int ChestIndex, int ContainerSlot, int TypeId, int Prefix, int Count);
        private sealed record Blocked(int Slot, int Remaining, string Reason);

        private sealed class Storage(Item[] items, Target target = null)
        {
            internal readonly Item[] Items = items;
            internal readonly Target Target = target;
            internal readonly Snapshot[] Expected = items.Select(Read).ToArray();
            internal readonly SortedSet<int> Changed = new();
            internal static Snapshot Read(Item item) => new(item, ActorInventory.Read(item), item.stack);
            internal bool Matches(int except = -1)
            {
                for (int slot = 0; slot < Items.Length; slot++)
                    if (slot != except && (Items[slot] is not Item item || !ReferenceEquals(item, Expected[slot].Item)
                        || item.stack != Expected[slot].RawStack || ActorInventory.Read(item) != Expected[slot].Core))
                        return false;
                return true;
            }
        }

        private bool InRange(Chest chest) =>
            Vector2.DistanceSquared(player.Center, new(chest.x * 16 + 16, chest.y * 16 + 16)) < 600 * 600;

        private bool TargetValid(Storage storage)
        {
            var target = storage.Target;
            if (!nearby) return ReferenceEquals(opened, session) && Check(session, identity) == null
                && ReferenceEquals(target.Record.item, storage.Items);
            return ReferenceEquals(Main.chest[target.Index], target.Record)
                && InRange(target.Record) && !Occupied(target.Index)
                && Resolve(player, target.X, target.Y, out var current, requireReach: false) == null
                && current == target && ReferenceEquals(target.Record.item, storage.Items);
        }

        private bool ActorValid() => ServerAiNativeActions.Owned(identity) && player.active && !player.dead && !player.ghost
            && player.selectedItem == selected && ReferenceEquals(player.inventory, inventory.Items)
            && Main.myPlayer == localSlot && ReferenceEquals(Main.ActivePlayerFileData, localPlayerFile)
            && ReferenceEquals(Main.mouseItem, mouseItem);

        private bool Confirm() => ActorValid() && inventory.Matches()
            && chests.All(storage => TargetValid(storage) && storage.Matches());

        private Result Report(string status, string reason, bool verified = false) =>
            new(status, reason, new {
                mutationAttempted = attempted, mutationGameUpdate = mutationTick, observedGameUpdate = Main.GameUpdateCount,
                effectStatus = status == "unknown" || status == "running" ? "unknown"
                    : verified && moves.Count > 0 ? blocked.Count == 0 ? "complete" : "partial" : "none",
                movedCount = status is "unknown" or "running" ? (long?)null : moves.Sum(move => (long)move.Count),
                moves = verified ? moves.Select(move => new { inventorySlot = move.InventorySlot, chestIndex = move.ChestIndex,
                    containerSlot = move.ContainerSlot, typeId = move.TypeId, prefix = move.Prefix, count = move.Count }).ToArray() : [],
                sourceContainer = verified && incoming ? new { x = session.Target.X, y = session.Target.Y, chestIndex = session.Target.Index } : null,
                blocked = verified ? blocked.Select(row => {
                    var item = (incoming ? chests[0] : inventory).Items[row.Slot];
                    return new { slot = row.Slot, remaining = row.Remaining, reason = row.Reason, typeId = item.type,
                        contentKey = item.ModItem?.FullName ?? "Terraria/" + ItemID.Search.GetName(item.type) };
                }).ToArray() : []
            }, ServerAiNativeActions.Released(player), verified && (moves.Count > 0 || blocked.Count == 0), InputsAcquired: false);

        internal Result Poll(bool cancel = false)
        {
            if (terminal != null) return terminal;
            try
            {
                if (attempted)
                {
                    if (Main.GameUpdateCount <= mutationTick)
                        return cancel ? terminal = Report("unknown", "container_batch_confirmation_interrupted")
                            : Report("running", "awaiting_container_batch_confirmation");
                    if (!Confirm()) return terminal = Report("unknown", "container_batch_changed_before_confirmation");
                    return terminal = blocked.Count == 0
                        ? Report("completed", amountLimitReason ?? (moves.Count == 0 ? "container_batch_no_eligible_items" : "container_batch_verified"), true)
                        : Report("failed", moves.Count == 0 ? "container_batch_blocked" : "container_batch_partial", true);
                }
                if (cancel) return terminal = Report("cancelled", "explicit_stop");
                return Execute();
            }
            catch
            {
                try { SyncChanged(verifiedOnly: true); }
                catch { /* The outcome remains unknown if a recovery send fails. */ }
                return terminal = Report("unknown", "container_batch_native_exception");
            }
        }

        private Result Execute()
        {
            session = opened;
            if (!nearby && !Fresh(session, identity, arguments.GetProperty("containerToken").GetString()))
                return terminal = Report("failed", "stale_container_token");
            if (byAmount && session.Target.Index < 0) return terminal = Report("failed", "unsupported_container");
            if (player.selectedItem == 58 || player.itemAnimation != 0 || player.itemTime != 0 || player.reuseDelay != 0 || player.channel)
                return terminal = Report("failed", "inventory_item_busy");
            if (player.inventory.Length < 58 || player.inventory.Any(item => item == null))
                return terminal = Report("failed", "invalid_inventory_slot_contents");

            inventory = new(player.inventory);
            chests = nearby ? Nearby().ToArray() : [new(session.Target.Record.item, session.Target)];
            var references = new HashSet<Item>(ReferenceEqualityComparer.Instance);
            if (chests.SelectMany(chest => chest.Items).Concat(inventory.Items).Any(item => !references.Add(item)))
                return terminal = Report("failed", "container_slots_alias_same_item");

            selected = player.selectedItem;
            localSlot = Main.myPlayer;
            localPlayerFile = Main.ActivePlayerFileData;
            mouseItem = Main.mouseItem;
            attempted = true;
            mutationTick = Main.GameUpdateCount;
            if (byAmount) amountLimitReason = TransferAmount(chests[0]);
            else if (incoming) Take(chests[0]);
            else Store();
            if (!Confirm()) return terminal = Report("unknown", "container_batch_native_result_not_verified");

            if (!SyncChanged()) return terminal = Report("unknown", "container_batch_changed_during_sync");
            return Report("running", "awaiting_container_batch_confirmation");
        }

        private bool SyncChanged(bool verifiedOnly = false)
        {
            if (inventory == null || chests == null || !ActorValid()) return false;
            bool MatchesSlot(Storage storage, int slot) => storage.Items[slot] is Item item
                && Storage.Read(item) == storage.Expected[slot];
            foreach (int slot in inventory.Changed)
            {
                if (!ActorValid()) return false;
                if (MatchesSlot(inventory, slot))
                    NetMessage.SendData(MessageID.SyncEquipment, -1, -1, null, player.whoAmI, slot);
                else if (!verifiedOnly) return false;
            }
            foreach (var chest in chests.Where(chest => chest.Changed.Count > 0))
            foreach (int slot in chest.Changed)
            {
                if (!ActorValid()) return false;
                if (TargetValid(chest) && MatchesSlot(chest, slot))
                    SyncChestSlot(chest.Target, slot);
                else if (!verifiedOnly) return false;
            }
            return true;
        }
        private IEnumerable<Storage> Nearby()
        {
            var types = inventory.Items.Skip(10).Take(40).Where(CanStore).Select(item => item.netID).ToHashSet();
            if (types.Count == 0) yield break;
            for (int index = 0; index < Main.chest.Length; index++)
            {
                var chest = Main.chest[index];
                if (chest == null || !InRange(chest) || Occupied(index)
                    || Resolve(player, chest.x, chest.y, out var target, requireReach: false) != null || target.Index != index
                    || !chest.item.Any(item => !item.IsAir && types.Contains(item.netID))) continue;
                yield return new(chest.item, target);
            }
        }

        private static bool Occupied(int index) => Main.player.Take(255).Any(actor => actor.chest == index);

        private static bool CanStore(Item item) => !item.IsAir && !item.favorited;
        private static bool SameKind(Item source, Item destination) => !destination.IsAir && source.netID == destination.netID && source.type == destination.type;

        private string TransferAmount(Storage chest)
        {
            int type = arguments.GetProperty("typeId").GetInt32();
            long remaining = arguments.GetProperty("count").GetInt64();
            var bagSlots = Enumerable.Range(10, 40).Concat(Enumerable.Range(0, 10)).ToArray();
            var chestSlots = Enumerable.Range(0, chest.Items.Length).ToArray();
            var (from, sources, to, destinations) = incoming
                ? (chest, chestSlots, inventory, bagSlots) : (inventory, bagSlots, chest, chestSlots);
            bool Eligible(Item item) => !item.IsAir && item.type == type && (incoming || !item.favorited);
            bool stackRefused = false;
            // Fill existing stacks first. Later sources can top up a stack created in a formerly empty slot.
            var occupied = destinations.Where(d => !to.Items[d].IsAir).ToArray();
            var empty = destinations.Where(d => to.Items[d].IsAir).ToArray();
            foreach (var slots in new[] { occupied, empty })
            foreach (int s in sources)
            {
                var source = from.Items[s];
                if (!Eligible(source)) continue;
                foreach (int d in slots)
                {
                    var dest = to.Items[d];
                    if (!dest.IsAir && (!SameKind(source, dest) || dest.prefix != source.prefix || dest.stack >= dest.maxStack))
                        continue;
                    int moved = Move(from, s, to, d, remaining);
                    stackRefused |= moved == 0;
                    remaining -= moved;
                    if (remaining == 0) return null;
                    if (source.IsAir) break;
                }
            }
            if (!sources.Any(s => Eligible(from.Items[s]))) return "insufficient_items";
            return stackRefused ? "container_stacking_denied" : "destination_full";
        }

        private void Store()
        {
            bool quick = operation != "deposit_all";
            var slots = Enumerable.Range(10, 40);
            if (!quick) slots = slots.Reverse();
            foreach (int slot in slots)
            {
                var source = inventory.Items[slot];
                if (!CanStore(source) || quick && source.IsACoin) continue;
                bool eligible = false;
                foreach (var chest in chests)
                {
                    if (source.IsAir || moves.Count >= MoveLimit) break;
                    if (quick && !chest.Items.Any(item => SameKind(source, item))) continue;
                    eligible = true;
                    Fill(inventory, slot, chest, Enumerable.Range(0, 40), allowEmpty: true);
                }
                // Nearby's candidate chest set is known even after the move budget is exhausted.
                if (!source.IsAir && (eligible || !quick || chests.Any(chest => chest.Items.Any(item => SameKind(source, item)))))
                    Block(slot, source);
            }
        }

        private void Take(Storage chest)
        {
            bool restock = operation == "restock";
            var existing = Enumerable.Range(0, 58).Where(slot => slot is < 50 or >= 54).Reverse().ToArray();
            var types = existing.Select(slot => inventory.Items[slot]).Where(item => !item.IsAir && !item.IsACoin && item.maxStack > 1)
                .Select(item => item.netID).ToHashSet();
            for (int slot = 0; slot < 40; slot++)
            {
                var source = chest.Items[slot];
                if (source.IsAir || restock && !types.Contains(source.netID)) continue;
                if (!restock && (source.noGrabDelay > 0 || source.uniqueStack && player.HasItem(source.type)))
                {
                    blocked.Add(new(slot, source.stack, "native_pickup_filter"));
                    continue;
                }
                var destinations = restock ? existing : LootSlots(source).ToArray();
                Fill(chest, slot, inventory, destinations, allowEmpty: !restock || source.ammo != 0,
                    ammoOnlyEmpty: restock);
                if (!source.IsAir)
                {
                    // A partially filled matching stack may refuse a move through a mod hook.
                    bool noRoom = !restock && !destinations.Any(i => inventory.Items[i].IsAir ||
                        SameKind(source, inventory.Items[i]) && inventory.Items[i].prefix == source.prefix &&
                        inventory.Items[i].stack < inventory.Items[i].maxStack);
                    Block(slot, source, noRoom);
                }
            }
        }

        private static IEnumerable<int> LootSlots(Item source)
        {
            if (source.FitsAmmoSlot())
                for (int slot = 54; slot < 58; slot++) yield return slot;
            if (source.IsACoin)
                for (int slot = 50; slot < 54; slot++) yield return slot;
            if (source.useStyle != 0 || source.favorited)
                for (int slot = 0; slot < 50; slot++) yield return slot;
            else
                for (int slot = 49; slot >= 0; slot--) yield return slot;
        }

        private void Fill(Storage from, int sourceSlot, Storage to, IEnumerable<int> destinations, bool allowEmpty, bool ammoOnlyEmpty = false)
        {
            var source = from.Items[sourceSlot];
            foreach (int slot in destinations)
            {
                if (source.IsAir || moves.Count >= MoveLimit) return;
                if (SameKind(source, to.Items[slot])) Move(from, sourceSlot, to, slot);
            }
            if (!allowEmpty) return;
            foreach (int slot in destinations)
            {
                if (source.IsAir || moves.Count >= MoveLimit) return;
                if (to.Items[slot].IsAir && (!ammoOnlyEmpty || slot < 54 || source.FitsAmmoSlot())) Move(from, sourceSlot, to, slot);
            }
        }

        private void Block(int slot, Item source, bool noRoom = false) =>
            blocked.Add(new(slot, source.stack, moves.Count >= MoveLimit ? "batch_move_limit"
                : noRoom ? "inventory_full" : "capacity_or_stacking_rule"));

        private int Move(Storage from, int sourceSlot, Storage to, int destinationSlot, long limit = int.MaxValue)
        {
            var source = from.Items[sourceSlot];
            var destination = to.Items[destinationSlot];
            var beforeSource = from.Expected[sourceSlot].Core;
            var beforeDestination = to.Expected[destinationSlot].Core;
            bool empty = destination.IsAir;
            if (beforeSource.Stack <= 0 || beforeSource.Stack > beforeSource.MaxStack
                || !empty && (beforeSource.Prefix != beforeDestination.Prefix || beforeDestination.Stack > beforeDestination.MaxStack)) return 0;
            int capacity = empty ? source.maxStack : destination.maxStack - destination.stack;
            int count = (int)Math.Min(limit, Math.Min(source.stack, capacity));
            if (count <= 0) return 0;
            var chest = incoming ? from : to;
            int bagSlot = incoming ? destinationSlot : sourceSlot;
            bool Stable(int sourceExcept = -1, int destinationExcept = -1) =>
                ActorValid() && TargetValid(chest) && from.Matches(sourceExcept) && to.Matches(destinationExcept);
            if (!Stable()) throw new InvalidOperationException("Container batch target changed.");
            if (!empty)
            {
                if (session != null) session.Token = null;
                bool allowed = ItemLoader.CanStack(destination, source);
                if (!Stable()) throw new InvalidOperationException("Container batch permission hook changed state.");
                if (!allowed) return 0;
            }
            if (session != null) session.Token = null;
            ActorInventory.InvalidateSlot(identity, bagSlot);
            int moved = ActorInventory.Move(source, ref to.Items[destinationSlot], count);
            if (moved != count || !Stable(sourceSlot, destinationSlot) || !ReferenceEquals(source, from.Items[sourceSlot])
                || !empty && !ReferenceEquals(destination, to.Items[destinationSlot])
                || !ActorInventory.MatchesMove(beforeSource, beforeDestination, source, to.Items[destinationSlot], count))
                throw new InvalidOperationException("Container batch native move changed unexpected state.");
            from.Expected[sourceSlot] = Storage.Read(source);
            to.Expected[destinationSlot] = Storage.Read(to.Items[destinationSlot]);
            from.Changed.Add(sourceSlot);
            to.Changed.Add(destinationSlot);
            moves.Add(new(bagSlot, chest.Target.Index, incoming ? sourceSlot : destinationSlot,
                beforeSource.Type, beforeSource.Prefix, moved));
            return moved;
        }
    }
}
