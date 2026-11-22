using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using VirtualPlayerEngineProbe;
using Identity = CompanionBridge.ServerIpc.AiReadOnlyProtocol.Identity;
using Result = CompanionBridge.ServerIpc.AiAtomicActions.NativeResult;

namespace CompanionBridge.ServerIpc;

internal static partial class MerchantShop
{
    private static readonly Lazy<bool> SaleHooks = new(() => Overrides<ModPlayer>("CanSellItem", "PostSellItem"));
    internal static bool SaleSupported => Supported && !SaleHooks.Value;

    private sealed record Sale(string Token, Player Actor, NPC Merchant, string Reference, string World,
        long Generation, int ActorSlot, int SourceSlot, string SourceToken, string Inventory, long Copper,
        ulong Expires, long Deadline);

    // SellItem writes coin denominations into carried slots 0..53 before the source item is removed.
    private static bool CanReceive(Player p, long copper)
    {
        int empty = p.inventory.Take(54).Count(i => i.IsAir);
        foreach (var (type, value) in new[] { (ItemID.PlatinumCoin, 1000000), (ItemID.GoldCoin, 10000),
                                               (ItemID.SilverCoin, 100), (ItemID.CopperCoin, 1) })
        {
            long count = copper / value;
            copper %= value;
            long room = p.inventory.Take(54).Where(i => i.type == type).Sum(i => (long)i.maxStack - i.stack);
            if (count > room)
            {
                long stack = ContentSamples.ItemsByType[type].maxStack;
                empty -= (int)((count - room + stack - 1) / stack);
            }
        }
        return empty >= 0;
    }

    // Called with the bound player's native shopping settings installed.
    private static string SalePrice(Player p, Item item, out long copper)
    {
        copper = 0;
        if (p.selectedItem == 58 || p.itemAnimation != 0 || p.itemTime != 0 || p.reuseDelay != 0 ||
            p.channel || p.inventoryChestStack.Any(locked => locked))
            return "inventory_item_busy";
        if (!SaleSupported || item.IsAir || item.type >= ItemID.Count || item.favorited ||
            item.type is >= ItemID.CopperCoin and <= ItemID.PlatinumCoin || item.isAShopItem ||
            item.shopSpecialCurrency != -1 || item.buyOnce || item.shopCustomPrice.HasValue)
            return "unsupported_sale_item";
        // Dedicated transactions use independent shop visits. Never consume client refund credit.
        if (Main.shopSellbackHelper.GetAmount(item) != 0) return "unsupported_sellback_credit";
        p.GetItemExpectedPrice(item, out var adjustedValue, out _);
        if (adjustedValue <= 0) return "item_has_no_sale_value";
        copper = Math.Max(1L, adjustedValue / 5);
        return CanReceive(p, copper) ? null : "insufficient_coin_space";
    }

    internal static object QuoteSale(Identity id, JsonElement args)
    {
        string entityRef = args.GetProperty("entityRef").GetString();
        string error = Resolve(id, entityRef, out var merchant, reading: true);
        if (error != null) throw new ShopReadException(error);
        int sourceSlot = args.GetProperty("sourceSlot").GetInt32();
        string sourceToken = args.GetProperty("sourceToken").GetString();
        var p = (Player)id.Actor;
        var item = p.inventory[sourceSlot];
        var lease = id.RequestLease ?? throw new ShopReadException("not_bound");
        lease.MerchantQuote = null;
        if (ActorInventory.Token(id, item, sourceSlot) != sourceToken) throw new ShopReadException("stale_source_token");
        long unitPrice;
        var original = p.currentShoppingSettings;
        try
        {
            p.currentShoppingSettings = Main.ShopHelper.GetShoppingSettings(p, merchant);
            error = SalePrice(p, item, out unitPrice);
        }
        finally { p.currentShoppingSettings = original; }
        Sale sale = null;
        if (error == null)
        {
            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            sale = new(token, p, merchant, entityRef, id.World, id.Generation, id.Slot, sourceSlot, sourceToken,
                Inventory(id,p), unitPrice, Main.GameUpdateCount + 600, Environment.TickCount64 + 10_000);
            lease.MerchantQuote = sale;
        }
        return new {entityRef, sourceSlot, sourceToken, itemType=item.type, quantity=1, unitPrice,
            currency="copper", canSell=sale != null, reason=error, quoteToken=sale?.Token, expiresAtGameUpdate=sale?.Expires};
    }

    internal static Result Sell(Identity id, JsonElement args)
    {
        string entityRef = args.GetProperty("entityRef").GetString();
        int sourceSlot = args.GetProperty("sourceSlot").GetInt32();
        string sourceToken = args.GetProperty("sourceToken").GetString();
        string quoteToken = args.GetProperty("quoteToken").GetString();
        Result Reject(string reason) => new("failed",reason,new {entityRef,mutationAttempted=false},true,InputsAcquired:false);
        var lease = id.RequestLease;
        var sale = lease?.MerchantQuote as Sale;
        bool matches = sale != null && quoteToken == sale.Token;
        if (matches) lease.MerchantQuote = null;
        string error = Resolve(id, entityRef, out var merchant);
        if (error != null) return Reject(error);
        var p = (Player)id.Actor;
        var item = p.inventory[sourceSlot];
        if (ActorInventory.Token(id,item,sourceSlot) != sourceToken) return Reject("stale_source_token");
        if (!matches || sale.Actor != p || sale.Merchant != merchant || sale.Reference != entityRef ||
            sale.World != id.World || sale.Generation != id.Generation || sale.ActorSlot != id.Slot ||
            sale.SourceSlot != sourceSlot || sale.SourceToken != sourceToken || sale.Inventory != Inventory(id,p) ||
            Main.GameUpdateCount >= sale.Expires || Environment.TickCount64 >= sale.Deadline) return Reject("stale_quote");

        int itemType = item.type;
        long Copper() => MerchantPayment.Coins(p.inventory,54);
        int Count() => p.inventory.Take(58).Where(i => i.type == itemType).Sum(i => i.stack);
        var banks = BankTokens(id,p);
        long beforeCopper = Copper();
        int beforeCount = Count(), beforeStack = item.stack;
        var original = p.currentShoppingSettings;
        bool started = false;
        try
        {
            p.currentShoppingSettings = Main.ShopHelper.GetShoppingSettings(p, merchant);
            error = SalePrice(p,item,out long price);
            if (error != null || price != sale.Copper) return Reject(error ?? "stale_quote");
            var visit = new Chest();
            var unit = item.Clone();
            unit.stack = 1;
            if (!PlayerLoader.CanSellItem(p,merchant,visit.item,unit)) return Reject("sale_vetoed");
            started = true;
            if (p.SellItem(unit,1))
            {
                int index = visit.AddItemToShop(unit);
                ItemLoader.TransferWithLimit(item,1);
                ActorInventory.InvalidateSlot(id,sourceSlot);
                PlayerLoader.PostSellItem(p,merchant,visit.item,visit.item[index]);
            }
            else error = "sale_unconfirmed";
        }
        catch when (started) { error = "sale_unconfirmed"; }
        finally { p.currentShoppingSettings = original; }
        long afterCopper = Copper();
        int afterCount = Count();
        int afterStack = p.inventory[sourceSlot].IsAir ? 0 : p.inventory[sourceSlot].stack;
        bool banksUnchanged = banks.SequenceEqual(BankTokens(id,p));
        bool ok = error == null && afterCopper - beforeCopper == sale.Copper && beforeCount - afterCount == 1 &&
            beforeStack - afterStack == 1 && banksUnchanged && ServerAiNativeActions.Owned(id) &&
            ReferenceEquals(Main.npc[merchant.whoAmI],merchant) && merchant.active && NativeCombatWorld.Reference(id,merchant) == entityRef;
        return new(ok ? "completed" : "unknown", ok ? "sale_verified" : "sale_unconfirmed",
            new {entityRef,sourceSlot,sourceToken,quoteToken,itemType,quantity=1,unitPrice=sale.Copper,
                beforeCopper,afterCopper,beforeCount,afterCount,beforeStack,afterStack,banksUnchanged},true,ok,InputsAcquired:false);
    }
}
