using System;
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
    internal sealed class Transfer(AiReadOnlyProtocol.Identity identity, string operation, JsonElement arguments)
    {
        private readonly Player player = (Player)identity.Actor;
        private readonly bool deposit = operation == "deposit_container_item";
        private readonly int inventorySlot = arguments.GetProperty("inventorySlot").GetInt32();
        private readonly int containerSlot = arguments.GetProperty("containerSlot").GetInt32();
        private readonly int count = arguments.GetProperty("count").GetInt32();
        private Session session;
        private Result terminal;
        private bool attempted;
        private int moved;
        private ulong mutationTick;
        private string[] afterInventory, afterChest;
        private Pair before, after;
        private sealed record Pair(ActorInventory.Core Source, ActorInventory.Core Destination);

        private string[] InventoryTokens() => player.inventory.Select((item, slot) => ActorInventory.Token(identity, item, slot)).ToArray();
        private string[] ChestTokens() => session.Target.Record.item.Select((item, slot) =>
            SlotToken(identity, item, "confirmation", slot)).ToArray();
        private bool OwnsTarget() => ServerAiNativeActions.Owned(identity) && ReferenceEquals(opened, session)
            && Check(session, identity) == null;

        private Result Report(string status, string reason, bool verified = false, bool unchanged = false)
        {
            object Describe(ActorInventory.Core item) => new { typeId = item.Type, stack = item.Stack, prefix = item.Prefix,
                maxStack = item.MaxStack, favorited = item.Favorite };
            object DescribePair(Pair pair) => pair == null ? null : new { source = Describe(pair.Source), destination = Describe(pair.Destination) };
            return new(status, reason, new {
                inventorySlot, containerSlot, requestedCount = count,
                movedCount = verified ? moved : unchanged ? (int?)0 : null,
                remainingCount = verified ? count - moved : unchanged ? (int?)count : null,
                blockedSlot = verified && moved < count || unchanged && reason == "container_destination_full"
                    ? deposit ? containerSlot : inventorySlot : (int?)null,
                effectStatus = verified ? moved == count ? "complete" : "partial" : unchanged ? "none" : "unknown",
                mutationAttempted = attempted, chestIndex = session?.Target.Index, before = DescribePair(before), after = DescribePair(after),
                mutationGameUpdate = mutationTick, observedGameUpdate = Main.GameUpdateCount
            }, ServerAiNativeActions.Released(player), verified, InputsAcquired: false);
        }

        internal Result Poll(bool cancel = false)
        {
            if (terminal != null) return terminal;
            try
            {
                if (attempted)
                {
                    if (Main.GameUpdateCount <= mutationTick)
                        return cancel ? terminal = Report("unknown", "container_transfer_confirmation_interrupted")
                            : Report("running", "awaiting_container_transfer_confirmation");
                    if (!OwnsTarget() || !InventoryTokens().SequenceEqual(afterInventory) || !ChestTokens().SequenceEqual(afterChest))
                        return terminal = Report("unknown", "container_transfer_changed_before_confirmation");
                    return terminal = moved == count ? Report("completed", "container_transfer_verified", verified: true)
                        : Report("failed", "container_transfer_partial_capacity", verified: true);
                }
                if (cancel) return terminal = Report("cancelled", "explicit_stop", unchanged: true);
                return Execute();
            }
            catch
            {
                if (attempted && ReferenceEquals(opened, session)) session.Token = null;
                return terminal = Report("unknown", "container_transfer_native_exception");
            }
        }

        private Result Execute()
        {
            Result Refuse(string reason) => terminal = Report("failed", reason, unchanged: true);
            session = opened;
            string token = arguments.GetProperty("containerToken").GetString();
            if (!Fresh(session, identity, token)) return Refuse("stale_container_token");
            if (player.selectedItem == 58 || player.itemAnimation != 0 || player.itemTime != 0 || player.reuseDelay != 0 || player.channel)
                return Refuse("inventory_item_busy");
            var inventory = player.inventory;
            var chest = session.Target.Record.item;
            if (inventory.Length < 50 || inventory.Any(item => item == null))
                return Refuse("invalid_inventory_slot_contents");
            var bagItem = inventory[inventorySlot];
            var chestItem = chest[containerSlot];
            if (ActorInventory.Token(identity, bagItem, inventorySlot) != arguments.GetProperty("inventoryToken").GetString())
                return Refuse("stale_inventory_snapshot");
            if (SlotToken(identity, chestItem, token, containerSlot) != arguments.GetProperty("slotToken").GetString())
                return Refuse("stale_container_slot");
            var source = deposit ? bagItem : chestItem;
            var destination = deposit ? chestItem : bagItem;
            if (source.IsAir || source.stack < count || source.stack > source.maxStack)
                return Refuse("container_source_count_insufficient_or_invalid");
            if (ReferenceEquals(source, destination) || inventory.Count(item => ReferenceEquals(item, source)) +
                chest.Count(item => ReferenceEquals(item, source)) != 1 || inventory.Count(item => ReferenceEquals(item, destination)) +
                chest.Count(item => ReferenceEquals(item, destination)) != 1)
                return Refuse("container_slots_alias_same_item");
            bool empty = destination.IsAir;
            if (!empty && (destination.type != source.type || destination.prefix != source.prefix))
                return Refuse("container_destination_incompatible");
            int capacity = empty ? source.maxStack : destination.maxStack - destination.stack;
            if (!empty && (destination.stack < 1 || destination.stack > destination.maxStack))
                return Refuse("container_destination_invalid");
            if (capacity <= 0) return Refuse("container_destination_full");

            before = new(ActorInventory.Read(source), ActorInventory.Read(destination));
            var inventoryBefore = InventoryTokens();
            var chestBefore = ChestTokens();
            int selected = player.selectedItem;
            bool Unchanged() => OwnsTarget() && player.selectedItem == selected
                && inventoryBefore.SequenceEqual(InventoryTokens()) && chestBefore.SequenceEqual(ChestTokens());

            attempted = true;
            mutationTick = Main.GameUpdateCount;
            session.Token = null;
            if (!empty)
            {
                bool allowed = ItemLoader.CanStack(destination, source);
                if (!Unchanged()) return terminal = Report("unknown", "container_changed_during_stack_permission_hook");
                if (!allowed) return Refuse("container_stacking_denied");
            }

            int planned = Math.Min(count, capacity);
            ref Item destinationSlot = ref (deposit ? ref chest[containerSlot] : ref inventory[inventorySlot]);
            moved = ActorInventory.Move(source, ref destinationSlot, planned);
            if (!OwnsTarget() || !ReferenceEquals(player.inventory, inventory) || !ReferenceEquals(session.Target.Record.item, chest))
                return terminal = Report("unknown", "container_binding_changed_during_transfer");
            after = new(ActorInventory.Read(deposit ? inventory[inventorySlot] : chest[containerSlot]),
                ActorInventory.Read(deposit ? chest[containerSlot] : inventory[inventorySlot]));
            afterInventory = InventoryTokens();
            afterChest = ChestTokens();
            bool OtherSlotsMatch(string[] original, string[] current, int changed) =>
                original.Length == current.Length && original.Where((value, slot) => slot != changed && value != current[slot]).Any() == false;
            if (moved != planned || !ActorInventory.MatchesMove(before.Source, before.Destination, source, destinationSlot, planned)
                || player.selectedItem != selected || !OtherSlotsMatch(inventoryBefore, afterInventory, inventorySlot)
                || !OtherSlotsMatch(chestBefore, afterChest, containerSlot)
                || !ReferenceEquals(source, deposit ? inventory[inventorySlot] : chest[containerSlot])
                || !empty && !ReferenceEquals(destination, destinationSlot))
                return terminal = Report("unknown", "container_native_transfer_not_verified");

            // Native item serialization keeps ModItem network payloads on both paths.
            NetMessage.SendData(MessageID.SyncEquipment, -1, -1, null, player.whoAmI, inventorySlot);
            if (!OwnsTarget() || !InventoryTokens().SequenceEqual(afterInventory) || !ChestTokens().SequenceEqual(afterChest))
                return terminal = Report("unknown", "container_changed_during_inventory_sync");
            SyncChestSlot(session.Target, containerSlot);
            return Report("running", "awaiting_container_transfer_confirmation");
        }
    }
}
