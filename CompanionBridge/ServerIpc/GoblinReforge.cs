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
    internal sealed record Offer(string Token, Player Actor, NPC Goblin, string Reference, string World,
        long Generation, int Slot, int SourceSlot, string SourceToken, int Price, MerchantPayment.Funds Funds, string Inventory,
        ulong Expires, long Deadline);

    // Mirrors Main.DrawInventory on the pinned vanilla service configuration. Only
    // Prefix(-2) below rolls a paid prefix. Prefix(-3) is the UI eligibility query.
    private static string Inspect(Player p, NPC goblin, Item item, out int price)
    {
        price = 0;
        if (p.selectedItem == 58 || p.inventoryChestStack.Any(locked => locked)) return "inventory_item_busy";
        if (item.IsAir || item.type >= ItemID.Count || item.stack != 1 || item.maxStack != 1 ||
            !item.Prefix(-3) || !ItemLoader.CanReforge(item)) return "item_not_reforgeable";
        price = Price(p,goblin,item);
        return price < 0 ? "unsupported_reforge_price" : null;
    }

    private static int Price(Player p,NPC goblin,Item item)
    {
        int price = item.value * item.stack;
        bool canDiscount = true;
        if (ItemLoader.ReforgePrice(item,ref price,ref canDiscount))
        {
            if (canDiscount && p.discountAvailable) price = (int)(price * 0.8);
            price = (int)(price * Main.ShopHelper.GetShoppingSettings(p,goblin).PriceAdjustment) / 3;
        }
        return price;
    }

    private static int[] Prefixes(Item item)
    {
        var plain = item.Clone();
        plain.ResetPrefix();
        return Enumerable.Range(1,PrefixID.Count-1).Where(plain.CanApplyPrefix).ToArray();
    }

    internal static object Quote(Identity id, JsonElement args)
    {
        string entityRef = args.GetProperty("entityRef").GetString();
        string error = MerchantShop.ResolveService(id,entityRef,NPCID.GoblinTinkerer,MerchantShop.VanillaServicesSupported,out var goblin, reading: true);
        if (error != null) throw new ShopReadException(error);
        var lease = id.RequestLease ?? throw new ShopReadException("not_bound");
        lease.MerchantQuote = null;
        var p = (Player)id.Actor;
        int sourceSlot = args.GetProperty("sourceSlot").GetInt32();
        string sourceToken = args.GetProperty("sourceToken").GetString();
        var item = p.inventory[sourceSlot];
        if (ActorInventory.Token(id,item,sourceSlot) != sourceToken) throw new ShopReadException("stale_source_token");
        error = Inspect(p,goblin,item,out int price);
        var funds = MerchantPayment.Balance(p);
        bool? canAfford = error == null ? p.CanAfford(price,-1) : null;
        Offer offer = null;
        if (error == null)
        {
            offer = new(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),p,goblin,
                entityRef,id.World,id.Generation,id.Slot,sourceSlot,sourceToken,price,funds,MerchantShop.Inventory(id,p),
                Main.GameUpdateCount+600,Environment.TickCount64+10000);
            lease.MerchantQuote = offer;
            if (canAfford == false) error = "insufficient_funds";
        }
        return new {entityRef,sourceSlot,sourceToken,itemType=item.type,prefix=item.prefix,priceCopper=price,
            prefixes=(offer == null ? Array.Empty<int>() : Prefixes(item)).Select(prefixId =>
                new {prefixId, name=Lang.prefix[prefixId].Value, internalName=PrefixID.Search.GetName(prefixId)}).ToArray(),
            funds.carriedCopper,funds,canAfford,currency="copper",canReforge=error==null,reason=error,quoteToken=offer?.Token,expiresAtGameUpdate=offer?.Expires};
    }

    internal static Result Start(Identity id, JsonElement args, out Batch batch)
    {
        string entityRef = args.GetProperty("entityRef").GetString();
        batch = null;
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
        var beforeFunds = MerchantPayment.Balance(p);
        if (offer.Funds != beforeFunds) return Reject("stale_quote");
        int? target = args.TryGetProperty("targetPrefix",out var prefix) ? prefix.GetInt32() : null;
        if (target.HasValue && target != item.prefix && !Prefixes(item).Contains(target.Value))
            return Reject("incompatible_target_prefix");
        batch = new Batch(id, offer, item, target);
        return batch.Step();
    }

    internal sealed class Batch
    {
        private readonly Identity id;
        private readonly Offer offer;
        private readonly Item item;
        private readonly int? target;
        private readonly int itemType, initial;
        private readonly bool favorite;
        private int rolls, lastPrefix;
        private long spent;
        private ulong? tick;
        private Result terminal;

        internal Batch(Identity id, Offer offer, Item item, int? target)
        {
            this.id=id; this.offer=offer; this.item=item; this.target=target;
            itemType=item.type; initial=lastPrefix=item.prefix; favorite=item.favorited;
        }

        private Result Finish(string status, string reason, bool confirmed=true)
        {
            var afterFunds=MerchantPayment.Balance(offer.Actor);
            var after=offer.Actor.inventory[offer.SourceSlot];
            bool same=ReferenceEquals(after,item) && after.type==itemType && after.stack==1 && after.favorited==favorite;
            var proof=new {entityRef=offer.Reference,sourceSlot=offer.SourceSlot,sourceToken=offer.SourceToken,
                quoteToken=offer.Token,itemType,beforePrefix=initial,afterPrefix=after.prefix,priceCopper=spent,
                beforeFunds=offer.Funds,afterFunds,rolled=rolls>0,itemVerified=same,bankNonCurrencyUnchanged=confirmed,
                targetPrefix=target,rolls,paymentsVerified=confirmed};
            return terminal=new(status,reason,proof,true,status=="completed",InputsAcquired:false);
        }

        internal Result Cancel()=>terminal ?? Finish("cancelled","explicit_stop");

        internal Result Step()
        {
            if (terminal!=null) return terminal;
            var p=offer.Actor;
            if (!ReferenceEquals(p.inventory[offer.SourceSlot],item) || item.type!=itemType || item.prefix!=lastPrefix ||
                item.stack!=1 || item.favorited!=favorite) return Finish("failed","source_item_changed");
            if (target==lastPrefix) return Finish("completed","target_prefix_reached");
            var pending=new Result("running","reforging",null,true,InputsAcquired:false);
            if (tick==Main.GameUpdateCount) return pending;
            tick=Main.GameUpdateCount;
            string error=MerchantShop.ResolveService(id,offer.Reference,NPCID.GoblinTinkerer,
                MerchantShop.VanillaServicesSupported,out var goblin);
            if (error!=null || !ReferenceEquals(goblin,offer.Goblin)) return Finish("failed",error ?? "stale_entity_ref");
            int price=Price(p,goblin,item);
            if (price<0) return Finish("failed","unsupported_reforge_price");
            var before=MerchantPayment.Balance(p);
            var banks=MerchantPayment.NonCurrency(id,p);
            try
            {
                if (!p.BuyItem(price,-1)) return Finish("failed","insufficient_funds");
                spent+=price;
                ItemLoader.PreReforge(item);
                item.ResetPrefix();
                bool rolled=item.Prefix(-2);
                ItemLoader.PostReforge(item);
                ActorInventory.InvalidateSlot(id,offer.SourceSlot);
                if (!rolled || item.prefix<=0 || !ReferenceEquals(p.inventory[offer.SourceSlot],item) ||
                    item.type!=itemType || item.stack!=1 || item.favorited!=favorite ||
                    before.totalCopper-MerchantPayment.Balance(p).totalCopper!=price ||
                    !banks.SequenceEqual(MerchantPayment.NonCurrency(id,p)) || !ServerAiNativeActions.Owned(id))
                    return Finish("unknown","reforge_unconfirmed",false);
            }
            catch (Exception) { return Finish("unknown","reforge_unconfirmed",false); }
            rolls++;
            lastPrefix=item.prefix;
            if (!target.HasValue) return Finish("completed","reforge_verified");
            return target==lastPrefix ? Finish("completed","target_prefix_reached") : pending;
        }
    }
}
