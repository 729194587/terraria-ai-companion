using System;
using System.Linq;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

// Main-thread isolated vanilla-Merchant fixture only. IL on 2026.09.3.1:
// NPCShopDatabase.RegisterMerchant IL_0092..0098 adds Torch with no conditions.
// Chest.SetupShop(int) obtains LocalPlayer.TalkNPC; FillShop evaluates unrelated,
// potentially client-bound conditions. Resolve only the registered torch entry.
// ItemSlot.HandleShopSlot uses LocalPlayer, mouseItem and client shopSellbackHelper.
// Use its actor payment/creation primitives with GetItem for inventory delivery.
internal static class G3ShopProbe
{
    // Read the definitions without executing arbitrary registered predicates. These two
    // translations mirror the pinned Condition delegates (Main.dayTime / LocalPlayer.ZoneSnow).
    internal static object Catalog(Player p, NPC merchant)
    {
        string[] shops = NPCShopDatabase.AllShops.Where(s => s.NpcType == merchant.type)
            .Select(s => s.FullName).ToArray();
        string name = NPCShopDatabase.GetShopName(merchant.type, "Shop");
        if (!NPCShopDatabase.TryGetNPCShop(name, out var registered) || registered is not NPCShop shop)
            throw new InvalidOperationException("Merchant shop is not registered");
        var entries = shop.Entries.Select(e => new {
            itemType = e.Item.type, disabled = e.Disabled,
            conditions = e.Conditions.Select(c => new {
                key = c.Description.Key,
                value = c == Condition.TimeDay ? (bool?)Main.dayTime : c == Condition.InSnow ? p.ZoneSnow : null
            }).ToArray()
        }).ToArray();
        return new { shops, name, entries, day = Main.dayTime, actorSnow = p.ZoneSnow };
    }

    internal static object Run(Player p, NPC merchant, string entityRef, string requestedRef, bool purchase)
    {
        long? unitPrice = null;
        int? conditionCount = null;
        double? priceAdjustment = null;
        int? baseValue = null;
        object Reply(string status, string reason) => new { status, reason, unitPrice, baseValue, priceAdjustment,
            conditionCount, itemType = ItemID.Torch };
        if (!Program.IsMainThread || !Main.dedServ || p == null || p.dead)
            return Reply("failed", "actor_unavailable");
        var identity = new AiReadOnlyProtocol.Identity(p, ProbeSession.WorldSession, ProbeSession.ActorGeneration, p.whoAmI, null);
        if (!ServerAiNativeActions.Owned(identity)) return Reply("failed", "actor_unavailable");
        if (merchant == null || !merchant.active || merchant.life <= 0 || merchant.type != NPCID.Merchant ||
            merchant.whoAmI < 0 || merchant.whoAmI >= Main.maxNPCs || !ReferenceEquals(Main.npc[merchant.whoAmI], merchant) ||
            entityRef != requestedRef || NativeCombatWorld.Reference(identity, merchant) != requestedRef)
            return Reply("failed", "stale_entity_ref");
        // A conservative radius for this isolated probe; the client UI is not opened.
        if (!merchant.CanTalk || p.Distance(merchant.Center) > 80f) return Reply("failed", "merchant_not_in_range_or_busy");

        string name = NPCShopDatabase.GetShopName(NPCID.Merchant, "Shop");
        if (!NPCShopDatabase.TryGetNPCShop(name, out var registered) || registered is not NPCShop shop ||
            !shop.TryGetEntry(ItemID.Torch, out var entry) || entry.Disabled)
            return Reply("failed", "torch_unavailable");
        conditionCount = entry.Conditions.Count();
        if (conditionCount != 0) return Reply("failed", "unsupported_conditions");

        Item offer = entry.Item.Clone();
        entry.OnShopOpen(offer, merchant);
        offer.isAShopItem = true;
        if (offer.type != ItemID.Torch || offer.shopSpecialCurrency != -1 || offer.buyOnce)
            return Reply("failed", "unsupported_offer");
        var original = p.currentShoppingSettings;
        bool paymentStarted = false;
        try
        {
            p.currentShoppingSettings = Main.ShopHelper.GetShoppingSettings(p, merchant);
            priceAdjustment = p.currentShoppingSettings.PriceAdjustment;
            baseValue = offer.value;
            p.GetItemExpectedPrice(offer, out _, out long price);
            unitPrice = price;
            if (price <= 0) return Reply("failed", "unsupported_price");
            if (!purchase) return Reply("completed", "price_quoted");

            // PayCurrency skips carried slots 54..58 and also considers four banks.
            var funds = MerchantPayment.Balance(p);
            if (!p.CanAfford(price, -1)) return Reply("failed", "insufficient_funds");
            int Torches() => p.inventory.Take(50).Where(i => i.type == ItemID.Torch).Sum(i => i.stack);
            var banks = MerchantPayment.NonCurrency(identity,p);
            int before = Torches();
            string error = MerchantPayment.Deliver(p, merchant, offer, price, ref paymentStarted);
            if (error != null) return Reply(paymentStarted ? "unknown" : "failed", error);
            bool verified = Torches() == before + 1 && MerchantPayment.Balance(p).totalCopper == funds.totalCopper - price &&
                banks.SequenceEqual(MerchantPayment.NonCurrency(identity,p)) && ServerAiNativeActions.Owned(identity) && merchant.active &&
                ReferenceEquals(Main.npc[merchant.whoAmI], merchant) && NativeCombatWorld.Reference(identity, merchant) == requestedRef;
            return verified ? Reply("completed", "purchase_verified") : Reply("unknown", "purchase_unconfirmed");
        }
        catch (Exception e)
        {
            return Reply(paymentStarted ? "unknown" : "failed", e.GetType().Name);
        }
        finally { p.currentShoppingSettings = original; }
    }
}
