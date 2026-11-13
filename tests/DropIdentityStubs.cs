// Minimal Terraria surface for compiling the ACTUAL ServerDrops identity logic.
// These stubs reproduce only the item-lifecycle facts the identity rules depend on:
// a reused slot receives a new Item instance, SetDefaults reinitialises in place and
// notifies hooks, Clone copies through MemberwiseClone without any hook.
using System;

namespace Terraria
{
    public sealed class Item
    {
        public bool active = true;
        public int type, prefix = -1, stack = 1;
        public bool IsAir => stack <= 0;

        // Deliberately not a virtual/hook-notifying path, matching MemberwiseClone.
        public Item Clone() => (Item)MemberwiseClone();

        public void SetDefaults(int newType)
        {
            type = newType;
            prefix = -1;
            stack = 1;
            active = true;
            ModLoader.ItemLoader.SetDefaults(this);
        }
    }

    public static class Main
    {
        public static Item[] item = Array.Empty<Item>();
    }

    public sealed class Player
    {
        public Item[] inventory = Array.Empty<Item>();
        // Mirrors Terraria.Player.bank4: a chest-like bag whose items live in .item.
        public Bag bank4 = new();
    }

    public sealed class Bag
    {
        public Item[] item = Array.Empty<Item>();
    }

    // The pickup path is not exercised by the identity tests; it only needs to compile.
    public static class On_Player
    {
        public delegate Item orig_PickupItem(Player player, int playerIndex, int itemIndex, Item item);
    }
}

namespace Terraria.DataStructures
{
    public interface IEntitySource { }
}

namespace Terraria.ModLoader
{
    public class GlobalItem
    {
        public virtual void SetDefaults(Terraria.Item entity) { }
        public virtual void OnSpawn(Terraria.Item item, Terraria.DataStructures.IEntitySource source) { }
    }

    public static class ItemLoader
    {
        internal static Action<Terraria.Item> DefaultsHook;
        internal static Action<Terraria.Item, Terraria.DataStructures.IEntitySource> SpawnHook;
        public static void SetDefaults(Terraria.Item item, bool createModItem = true) => DefaultsHook?.Invoke(item);
        public static void OnSpawn(Terraria.Item item, Terraria.DataStructures.IEntitySource source) => SpawnHook?.Invoke(item, source);
    }
}

namespace VirtualPlayerEngineProbe
{
    internal static class NativeSurvival
    {
        internal static bool Owns(Terraria.Player player) => false;
    }
}

namespace CompanionBridge.ServerIpc
{
    internal static class AiReadOnlyProtocol
    {
        internal sealed record Identity(object Actor, string World, long Generation, int Slot);
    }

    // The identity tests never drive a collection task; this only satisfies the
    // field and member references the production source carries.
    internal sealed class CollectDropsTask
    {
        internal bool Allows(Terraria.Item item) => false;
        internal void Credit(int slot, Terraria.Item item, int type, int count) { }
        internal void Uncertain() { }
    }
}
