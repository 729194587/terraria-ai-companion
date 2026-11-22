using System.Linq;
using Terraria;
using Terraria.ID;
using Terraria.DataStructures;
using VirtualPlayerEngineProbe;
using Terraria.ModLoader;

namespace CompanionBridge.ServerIpc;

internal static class MerchantPayment
{
    internal static long Coins(Item[] items, int count) => items.Take(count).Sum(i => (long)i.stack * (i.type switch
    {
        ItemID.CopperCoin => 1, ItemID.SilverCoin => 100, ItemID.GoldCoin => 10000,
        ItemID.PlatinumCoin => 1000000, _ => 0
    }));

    internal sealed record Funds(long carriedCopper, long piggyBank, long safe, long defendersForge, long voidVault)
    {
        public long totalCopper => carriedCopper + piggyBank + safe + defendersForge + voidVault;
    }

    internal static Funds Balance(Player p) => new(Coins(p.inventory, 54),
        Coins(p.bank.item, p.bank.item.Length), Coins(p.bank2.item, p.bank2.item.Length),
        Coins(p.bank3.item, p.bank3.item.Length), Coins(p.bank4.item, p.bank4.item.Length));

    internal static string[] NonCurrency(AiReadOnlyProtocol.Identity id, Player p) =>
        new[] { p.bank, p.bank2, p.bank3, p.bank4 }.SelectMany((bank, b) =>
            bank.item.Select((item, slot) => item.IsAir || item.IsACoin ? null :
                ActorInventory.Token(id, item, $"bank:{b}:{slot}"))).ToArray();

    internal static string Deliver(Player p, NPC merchant, Item offer, long price, ref bool paymentStarted)
    {
        var received = offer.Clone();
        received.stack = 1;
        received.isAShopItem = false;
        var space = p.ItemSpace(received);
        if (!space.CanTakeItem || space.ItemIsGoingToVoidVault) return "inventory_full";
        var offers = new[] { offer };
        if (!PlayerLoader.CanBuyItem(p, merchant, offers, offer)) return "purchase_vetoed";
        paymentStarted = true;
        if (!p.BuyItem(price, offer.shopSpecialCurrency)) return "payment_unconfirmed";
        received.OnCreated(new BuyItemCreationContext(new Item(), merchant));
        // ItemSlot passes the purchased item on the cursor, not the shop's display item.
        PlayerLoader.PostBuyItem(p, merchant, offers, received);
        var left = p.GetItem(p.whoAmI, received, GetItemSettings.InventoryEntityToPlayerInventorySettings);
        return left.IsAir ? null : "delivery_unconfirmed";
    }
}
