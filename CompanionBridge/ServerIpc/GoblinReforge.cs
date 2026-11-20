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

internal static class GoblinReforge
{
    private sealed record Offer(string Token, Player Actor, NPC Goblin, string Reference, string World,
        long Generation, int Slot, int SourceSlot, string SourceToken, int Price, string Inventory,
        ulong Expires, long Deadline);

    // Mirrors Main.DrawInventory on the pinned vanilla service configuration. Only
    // Prefix(-2) below rolls a paid prefix. Prefix(-3) is the UI eligibility query.
    private static string Inspect(Player p, NPC goblin, Item item, out int price)
    {
        price = 0;
        if (p.selectedItem == 58 || p.inventoryChestStack.Any(locked => locked)) return "inventory_item_busy";
        if (item.IsAir || item.type >= ItemID.Count || item.stack != 1 || item.maxStack != 1 ||
            !item.Prefix(-3) || !ItemLoader.CanReforge(item)) return "item_not_reforgeable";
        price = item.value * item.stack;
        bool canDiscount = true;
        if (ItemLoader.ReforgePrice(item,ref price,ref canDiscount))
        {
            if (canDiscount && p.discountAvailable) price = (int)(price * 0.8);
            price = (int)(price * Main.ShopHelper.GetShoppingSettings(p,goblin).PriceAdjustment) / 3;
        }
        return price < 0 ? "unsupported_reforge_price" : null;
    }

    internal static object Quote(Identity id, JsonElement args)
    {
        string entityRef = args.GetProperty("entityRef").GetString();
        string error = MerchantShop.ResolveService(id,entityRef,NPCID.GoblinTinkerer,MerchantShop.VanillaServicesSupported,out var goblin);
        if (error != null) throw new ShopReadException(error);
        var lease = id.RequestLease ?? throw new ShopReadException("not_bound");
        lease.MerchantQuote = null;
        var p = (Player)id.Actor;
        int sourceSlot = args.GetProperty("sourceSlot").GetInt32();
        string sourceToken = args.GetProperty("sourceToken").GetString();
        var item = p.inventory[sourceSlot];
        if (ActorInventory.Token(id,item,sourceSlot) != sourceToken) throw new ShopReadException("stale_source_token");
        error = Inspect(p,goblin,item,out int price);
        long funds = MerchantPayment.Coins(p.inventory,54);
        Offer offer = null;
        if (error == null)
        {
            offer = new(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),p,goblin,
                entityRef,id.World,id.Generation,id.Slot,sourceSlot,sourceToken,price,MerchantShop.Inventory(id,p),
                Main.GameUpdateCount+600,Environment.TickCount64+10000);
            lease.MerchantQuote = offer;
            if (funds < price) error = "insufficient_carried_funds";
        }
        return new {entityRef,sourceSlot,sourceToken,itemType=item.type,prefix=item.prefix,priceCopper=price,
            carriedCopper=funds,canReforge=error==null,reason=error,quoteToken=offer?.Token,expiresAtGameUpdate=offer?.Expires};
    }

    internal static Result Execute(Identity id, JsonElement args)
    {
        string entityRef = args.GetProperty("entityRef").GetString();
        string quoteToken = args.GetProperty("quoteToken").GetString();
        int sourceSlot = args.GetProperty("sourceSlot").GetInt32();
        string sourceToken = args.GetProperty("sourceToken").GetString();
        Result Reject(string reason) => new("failed",reason,new {entityRef,mutationAttempted=false},true,InputsAcquired:false);
        var lease = id.RequestLease;
        var offer = lease?.MerchantQuote as Offer;
        bool matches = offer != null && offer.Token == quoteToken;
        if (matches) lease.MerchantQuote = null;
        string error = MerchantShop.ResolveService(id,entityRef,NPCID.GoblinTinkerer,MerchantShop.VanillaServicesSupported,out var goblin);
        if (error != null) return Reject(error);
        var p = (Player)id.Actor;
        var item = p.inventory[sourceSlot];
        if (ActorInventory.Token(id,item,sourceSlot) != sourceToken) return Reject("stale_source_token");
        if (!matches || offer.Actor != p || offer.Goblin != goblin || offer.Reference != entityRef ||
            offer.World != id.World || offer.Generation != id.Generation || offer.Slot != id.Slot ||
            offer.SourceSlot != sourceSlot || offer.SourceToken != sourceToken || offer.Inventory != MerchantShop.Inventory(id,p) ||
            Main.GameUpdateCount >= offer.Expires || Environment.TickCount64 >= offer.Deadline) return Reject("stale_quote");
        error = Inspect(p,goblin,item,out int price);
        if (error != null || price != offer.Price) return Reject(error ?? "stale_quote");
        long beforeCopper = MerchantPayment.Coins(p.inventory,54);
        if (beforeCopper < price || !p.CanAfford(price,-1)) return Reject("insufficient_carried_funds");
        int itemType = item.type, beforePrefix = item.prefix, beforeStack = item.stack;
        bool favorite = item.favorited;
        var banks = MerchantShop.BankTokens(id,p);
        bool rolled = false;
        try
        {
            if (p.BuyItem(price,-1))
            {
                ItemLoader.PreReforge(item);
                item.ResetPrefix();
                rolled = item.Prefix(-2);
                ItemLoader.PostReforge(item);
                ActorInventory.InvalidateSlot(id,sourceSlot);
            }
        }
        catch (Exception) { rolled = false; } // No replay once payment begins.
        var after = p.inventory[sourceSlot];
        int afterPrefix = after.prefix;
        long afterCopper = MerchantPayment.Coins(p.inventory,54);
        bool banksUnchanged = banks.SequenceEqual(MerchantShop.BankTokens(id,p));
        bool itemVerified = ReferenceEquals(after,item) && after.type == itemType &&
            after.stack == beforeStack && after.favorited == favorite;
        bool verified = rolled && afterPrefix > 0 && itemVerified && beforeCopper-afterCopper == price &&
            banksUnchanged && ServerAiNativeActions.Owned(id) && goblin.active && ReferenceEquals(Main.npc[goblin.whoAmI],goblin);
        return new(verified?"completed":"unknown",verified?"reforge_verified":"reforge_unconfirmed",
            new {entityRef,sourceSlot,sourceToken,quoteToken,itemType,beforePrefix,afterPrefix,priceCopper=price,
                beforeCopper,afterCopper,rolled,itemVerified,banksUnchanged},true,verified,InputsAcquired:false);
    }
}
