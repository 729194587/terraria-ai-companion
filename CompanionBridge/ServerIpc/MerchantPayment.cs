using System.Linq;
using Terraria;
using Terraria.ID;
using Terraria.DataStructures;
using Terraria.ModLoader;

namespace CompanionBridge.ServerIpc;

internal static class MerchantPayment
{
    internal static long Coins(Item[] items, int count) => items.Take(count).Sum(i => (long)i.stack * (i.type switch
    {
        ItemID.CopperCoin => 1, ItemID.SilverCoin => 100, ItemID.GoldCoin => 10000,
        ItemID.PlatinumCoin => 1000000, _ => 0
    }));

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
