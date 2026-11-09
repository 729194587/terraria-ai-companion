using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using CompanionBridge.Common.Systems;
using CompanionBridge.ServerIpc;
using Terraria;
using Terraria.ModLoader;
using Terraria.ModLoader.Default;
using Terraria.UI;

namespace VirtualPlayerEngineProbe;

internal static class ActorEquipment
{
    private const int FunctionalModContext = -10;
    private static readonly FieldInfo modItems = typeof(ModAccessorySlotPlayer).GetField("exAccessorySlot",
        BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingFieldException("Mod functional equipment storage");
    private static readonly Func<Player, Item[], Item, int, bool> conflicts =
        typeof(ItemSlot).GetMethod("AccCheck_ForPlayer", BindingFlags.Static | BindingFlags.NonPublic,
            null, new[] { typeof(Player), typeof(Item[]), typeof(Item), typeof(int) }, null)
        ?.CreateDelegate<Func<Player, Item[], Item, int, bool>>() ?? throw new MissingMethodException("Actor accessory conflict check");
    private static AccessorySlotLoader Loader => LoaderManager.Get<AccessorySlotLoader>();
    private static Item[] Extended(Player player) => (Item[])modItems.GetValue(player.GetModPlayer<ModAccessorySlotPlayer>());
    internal static bool IsOperation(string operation) => operation is "equip_item" or "unequip_item";

    private sealed record Slot(string Id, string Kind, int Index, ModAccessorySlot Mod)
    {
        internal Item Get(Player p) => Mod == null ? p.armor[Index] : Mod.FunctionalItem;
        internal void Set(Player p, Item item)
        {
            if (Mod == null) p.armor[Index] = item;
            else Mod.FunctionalItem = item;
        }
        internal bool Enabled(Player p)
        {
            if (Mod == null) return p.IsItemSlotUnlockedAndUsable(Index);
            bool functional = Mod.DrawFunctionalSlot;
            if (!ReferenceEquals(Main.CurrentPlayer, p)) throw new InvalidOperationException("Mod slot changed the player scope.");
            return functional && Loader.ModdedIsSpecificItemSlotUnlockedAndUsable(Index, p, false);
        }
        internal string Token(AiReadOnlyProtocol.Identity id, Player p, Item item) =>
            ActorInventory.Token(id, item, $"equipment:{p.CurrentLoadoutIndex}:{Id}");
    }

    private static IEnumerable<Slot> Slots(Player p)
    {
        yield return new("vanilla:head", "head", 0, null);
        yield return new("vanilla:body", "body", 1, null);
        yield return new("vanilla:legs", "legs", 2, null);
        for (int i = 3; i < 3 + AccessorySlotLoader.MaxVanillaSlotCount; i++)
            yield return new($"vanilla:accessory:{i - 2}", "accessory", i, null);
        int count = p.GetModPlayer<ModAccessorySlotPlayer>().LoadedSlotCount;
        for (int i = 0; i < count; i++)
        {
            var slot = Loader.Get(i, p);
            if (slot != null && slot.Type == i)
                yield return new("mod:" + slot.FullName, "accessory", i, slot);
        }
    }

    private sealed record Stamp(Item Item, string Token, int Head, int Body, int Legs, int Wing, bool Accessory);
    private static Stamp Capture(AiReadOnlyProtocol.Identity id, Item item) =>
        new(item, ActorInventory.Token(id, item, "equipment-check"), item.headSlot, item.bodySlot,
            item.legSlot, item.wingSlot, item.accessory);

    private sealed class State(AiReadOnlyProtocol.Identity id, Player p)
    {
        internal readonly Item[] Inventory = p.inventory, Armor = p.armor, ModItems = Extended(p);
        internal readonly int Selected = p.selectedItem, Loadout = p.CurrentLoadoutIndex;
        internal readonly Stamp[] Items = p.inventory.Concat(p.armor).Concat(Extended(p)).Select(item => Capture(id, item)).ToArray();

        internal bool Matches(AiReadOnlyProtocol.Identity identity, Player player, int bag = -1, int gear = -1)
        {
            if (!ServerAiNativeActions.Owned(identity) || player.selectedItem != Selected || player.CurrentLoadoutIndex != Loadout
                || !ReferenceEquals(player.inventory, Inventory) || !ReferenceEquals(player.armor, Armor)
                || !ReferenceEquals(Extended(player), ModItems)) return false;
            int index = 0;
            foreach (var item in Inventory.Concat(Armor).Concat(ModItems))
            {
                int expected = index == bag ? gear : index == gear ? bag : index;
                if (Capture(identity, item) != Items[expected]) return false;
                index++;
            }
            return true;
        }

        internal int IndexOf(Slot slot) => Inventory.Length + slot.Index + (slot.Mod == null ? 0 : Armor.Length);
    }

    internal static object Observe(AiReadOnlyProtocol.Identity id)
    {
        ServerAiNativeActions.Require(id);
        var p = (Player)id.Actor;
        var state = new State(id, p);
        using var scope = new Main.CurrentPlayerOverride(p);
        var rows = new List<object>();
        foreach (var slot in Slots(p))
        {
            bool enabled = slot.Enabled(p);
            if (slot.Mod != null && (!ReferenceEquals(Main.CurrentPlayer, p) || !state.Matches(id, p)))
                throw new InvalidOperationException("Equipment changed during slot observation.");
            var item = slot.Get(p);
            rows.Add(new { slotId = slot.Id, kind = slot.Kind, source = slot.Mod == null ? "vanilla" : "mod_accessory_slot",
                displayName = slot.Mod?.Name ?? (slot.Kind == "accessory" ? $"Accessory {slot.Index - 2}" : slot.Kind),
                enabled, occupied = !item.IsAir, item = item.IsAir ? null : ObservationReader.ItemData(item, slot.Index),
                targetToken = slot.Token(id, p, item) });
        }
        if (!state.Matches(id, p)) throw new InvalidOperationException("Equipment changed during observation.");
        return rows;
    }

    internal sealed class Context(AiReadOnlyProtocol.Identity identity, string operation, JsonElement args)
    {
        internal readonly AiReadOnlyProtocol.Identity Identity = identity;
        internal readonly string Operation = operation, SlotId = args.GetProperty("targetSlotId").GetString();
        internal readonly JsonElement Arguments = args.Clone();
        internal string Status = "running", Reason = "awaiting_equipment_mutation", Error;
        internal bool MutationAttempted;
        internal ulong MutationTick, ConfirmedTick;
        internal int BackpackSlot = -1;
        internal object Before, After;
        private State confirmed;

        private void Finish(string status, string reason) { Status = status; Reason = reason; }
        internal void Cancel()
        {
            if (Status == "running") Finish(MutationAttempted ? "unknown" : "cancelled",
                MutationAttempted ? "equipment_cancelled_before_confirmation" : "explicit_stop");
        }

        private static bool Busy(Player p) => p.itemAnimation != 0 || p.itemTime != 0 || p.reuseDelay != 0
            || p.channel || p.selectedItem == 58 || p.noItems || p.CCed;
        private bool Live(Player p) => ServerAiNativeActions.Owned(Identity) && p.active && !p.dead && !p.ghost;

        private object Describe(Item item, string token) => new { instanceId = ActorInventory.Instance(item),
            typeId = item.type, item.stack, prefix = (int)item.prefix, item.favorited, token };
        private object Snapshot(Player p, Slot slot) => new {
            backpack = Describe(p.inventory[BackpackSlot], ActorInventory.Token(Identity, p.inventory[BackpackSlot], BackpackSlot)),
            equipment = Describe(slot.Get(p), slot.Token(Identity, p, slot.Get(p))) };

        internal void Step()
        {
            if (Status != "running") return;
            try
            {
                var p = (Player)Identity.Actor;
                if (!Live(p)) { Finish(MutationAttempted ? "unknown" : "failed", "equipment_actor_unavailable"); return; }
                if (confirmed != null)
                {
                    if (Main.GameUpdateCount <= MutationTick) return;
                    ConfirmedTick = Main.GameUpdateCount;
                    bool valid = confirmed.Matches(Identity, p);
                    Finish(valid ? "completed" : "unknown",
                        valid ? "equipment_change_verified_next_update" : "equipment_changed_before_confirmation");
                    return;
                }
                if (Busy(p)) { Finish("failed", "equipment_actor_busy"); return; }
                bool equip = Operation == "equip_item";
                using var scope = new Main.CurrentPlayerOverride(p);
                var slot = Slots(p).FirstOrDefault(candidate => candidate.Id == SlotId);
                if (slot == null) { Finish("failed", "equipment_slot_not_found"); return; }
                var equipment = slot.Get(p);
                if (slot.Token(Identity, p, equipment) != Arguments.GetProperty("targetToken").GetString())
                { Finish("failed", "stale_equipment_snapshot"); return; }
                BackpackSlot = equip ? Arguments.GetProperty("sourceSlot").GetInt32()
                    : Arguments.TryGetProperty("destinationSlot", out var destination) ? destination.GetInt32()
                    : Array.FindIndex(p.inventory, 0, 50, item => item.IsAir);
                if (BackpackSlot < 0) { Finish("failed", "equipment_backpack_full"); return; }
                var backpack = p.inventory[BackpackSlot];
                if (equip && ActorInventory.Token(Identity, backpack, BackpackSlot) != Arguments.GetProperty("sourceToken").GetString())
                { Finish("failed", "stale_inventory_snapshot"); return; }
                if (equip ? backpack.IsAir : equipment.IsAir)
                { Finish("failed", "equipment_source_empty"); return; }
                if (!equip && !backpack.IsAir) { Finish("failed", "equipment_destination_occupied"); return; }

                var state = new State(Identity, p);
                int gear = state.IndexOf(slot);
                if (!ReferenceEquals(state.Items[gear].Item, equipment)
                    || state.Items.Count(stamp => ReferenceEquals(stamp.Item, equip ? backpack : equipment)) != 1
                    || !equipment.IsAir && state.Items.Count(stamp => ReferenceEquals(stamp.Item, equipment)) != 1)
                { Finish("failed", "equipment_item_alias"); return; }
                Before = Snapshot(p, slot);
                MutationAttempted = slot.Mod != null; // Slot permission hooks may already have side effects.
                bool enabled = slot.Enabled(p);
                if (!VerifyUnchanged("equipment_changed_during_slot_hook")) return;
                if (!enabled) { Finish("failed", "equipment_slot_disabled"); return; }
                if (equip)
                {
                    MutationAttempted = slot.Kind == "accessory";
                    string rejection = CheckCompatibility(p, slot, backpack, state);
                    if (!VerifyUnchanged("equipment_changed_during_compatibility_hook")) return;
                    if (rejection != null) { Finish("failed", rejection); return; }
                }

                MutationAttempted = true;
                MutationTick = Main.GameUpdateCount;
                slot.Set(p, backpack);
                p.inventory[BackpackSlot] = equipment;
                if (!state.Matches(Identity, p, BackpackSlot, gear))
                { Finish("unknown", "equipment_swap_not_verified"); return; }
                After = Snapshot(p, slot);
                var expected = new State(Identity, p);
                NetworkStateSync.Full(p); // Native SyncOnePlayer includes PlayerLoader.SyncPlayer and Mod slot packets.
                if (!Live(p) || !expected.Matches(Identity, p))
                { Finish("unknown", "equipment_changed_during_sync"); return; }
                confirmed = expected;
                Reason = "awaiting_equipment_confirmation";

                bool VerifyUnchanged(string reason)
                {
                    if (ReferenceEquals(Main.CurrentPlayer, p) && Live(p) && !Busy(p) && state.Matches(Identity, p)) return true;
                    Finish("unknown", reason);
                    return false;
                }
            }
            catch (Exception e)
            {
                Error = e.GetType().Name + ": " + e.Message;
                Error = Error[..Math.Min(Error.Length, 768)];
                Finish("unknown", "equipment_native_exception");
            }
        }

        private static string CheckCompatibility(Player p, Slot slot, Item item, State state)
        {
            if (item.stack != 1 || ItemSlot.isEquipLocked(item.type)) return "equipment_item_incompatible";
            if (slot.Kind != "accessory")
                return (slot.Kind switch { "head" => item.headSlot, "body" => item.bodySlot, _ => item.legSlot }) >= 0
                    ? null : "equipment_item_incompatible";
            if (!item.accessory) return "equipment_item_incompatible";
            if (slot.Mod != null)
                return Loader.IsAccessoryInConflict(p, item, slot.Index, FunctionalModContext) ? "equipment_accessory_rejected" : null;
            var nativeSlots = state.Armor.Concat(state.ModItems).ToArray();
            nativeSlots[slot.Index] = new Item(); // Evaluate the layout after replacing only the requested slot.
            return conflicts(p, nativeSlots, item, slot.Index) ? "equipment_accessory_rejected" : null;
        }

        internal object Evidence() => new { targetSlotId = SlotId, backpackSlot = BackpackSlot, before = Before, after = After,
            mutationAttempted = MutationAttempted, mutationGameUpdate = MutationTick, confirmedAtGameUpdate = ConfirmedTick,
            equipmentVerified = Status == "completed", nativeError = Error };
    }
}
