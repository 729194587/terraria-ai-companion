using System;
using System.Linq;
using System.Reflection;
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
    // Pinned tML Entry.OnShopOpen invokes this private delegate. Fail closed on layout changes.
    private static readonly FieldInfo Hooks = typeof(NPCShop.Entry).GetField("shopOpenedHooks", BindingFlags.Instance | BindingFlags.NonPublic);
    internal static bool Supported => VirtualPlayerEngineProbe.VirtualPlayerEngineProbe.Engine?.Selected == "2026.09.3.1"
        && Hooks?.FieldType == typeof(Action<Item,NPC>) && !ExternalHooks.Value;

    // Registered tML content is fixed by the time the server accepts a binding.
    // EngineHooks separately verifies the engine fingerprint and runtime isolation.
    private static readonly Lazy<bool> ExternalHooks = new(() =>
        Overrides<GlobalNPC>("CanChat","ModifyShop","ModifyActiveShop") ||
        Overrides<ModPlayer>("CanBuyItem","PostBuyItem","OnPickup") ||
        Overrides<GlobalItem>("SetDefaults","OnCreated","ItemSpace","CanPickup","OnPickup","CanStack","OnStack","SplitStack"));

    private static bool Overrides<T>(params string[] methods) where T : IModType, ILoadable =>
        ModContent.GetContent<T>().Any(extension => extension.Mod.Name is not ("ModLoader" or "CompanionBridge") &&
            extension.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public).Any(m =>
                methods.Contains(m.Name) && m.DeclaringType != typeof(T) && m.GetBaseDefinition().DeclaringType == typeof(T)));

    private sealed record Listing(int itemType, bool? available, string reason, long? unitPrice, bool canPurchase, bool? canAfford)
    {
        public string currency => unitPrice.HasValue ? "copper" : "unknown";
        public string stock => unitPrice.HasValue ? "unlimited" : "unknown";
    }
    private sealed record Quote(string Token, Player Actor, NPC Merchant, string Reference, string World, long Generation,
        int Slot, int Type, int Quantity, long Price, ulong Expires, long DeadlineMs, string Inventory, MerchantPayment.Funds Funds, string Catalog);
    private static string ShopName(NPC npc) => NPCShopDatabase.GetShopName(npc.type, "Shop");

    // These NPCs open a standard, single coin shop from their ordinary shop button.
    private static bool OrdinaryVendor(int type) => type is NPCID.Merchant or NPCID.Demolitionist
        or NPCID.ArmsDealer or NPCID.GoblinTinkerer or NPCID.Mechanic or NPCID.Wizard
        or NPCID.Dryad or NPCID.DyeTrader or NPCID.Steampunker or NPCID.WitchDoctor or NPCID.Pirate;

    private static string Resolve(Identity id, string reference, out NPC merchant, bool reading = false)
    {
        var error = ResolveService(id, reference, -1, Supported, out merchant, reading);
        if (error != null) return error;
        if (id.RequestLease?.Dialogue is NpcDialogue.Conversation session && session.Reference == reference && session.IsCurrent)
            return null;
        if (reading) throw new QueryUnavailableException("merchant_dialogue_required");
        return "merchant_dialogue_required";
    }

    internal static string ResolveService(Identity id, string reference, int npcType, bool supported, out NPC merchant, bool reading = false)
    {
        if (reading && id.RequestLease == null) throw new ShopReadException("not_bound");
        merchant = NpcDialogue.Resolve(id, reference);
        if (merchant == null || merchant.life <= 0) return "stale_entity_ref";
        if (!supported || (npcType == -1 ? !OrdinaryVendor(merchant.type) : merchant.type != npcType)) return npcType == -1 ? "unsupported_merchant" : "unsupported_npc_service";
        string unavailable = NpcDialogue.UnavailableReason((Player)id.Actor, merchant);
        if (unavailable == null) return null;
        if (reading) throw new QueryUnavailableException(unavailable);
        return npcType == -1 ? "merchant_not_in_range_or_busy" : "npc_not_in_range_or_busy";
    }

    private static Listing Inspect(NPCShop.Entry entry, Item item, Player player)
    {
        Listing Skip(bool? available, string reason) => new(item.type, available, reason, null, false, null);
        if (entry.Disabled) return Skip(false, "offer_disabled");
        if (item.type <= 0 || item.type >= ItemID.Count || item.shopSpecialCurrency != -1 || item.buyOnce ||
            item.stack != 1 || Hooks.GetValue(entry) != null) return Skip(null, "unsupported_offer");
        bool unknown = false;
        foreach (var condition in entry.Conditions)
        {
            bool? met = condition == Condition.TimeDay ? Main.dayTime : condition == Condition.InSnow ? player.ZoneSnow : null;
            if (met == false) return Skip(false, "conditions_not_met");
            unknown |= met == null;
        }
        if (unknown) return Skip(null, "unknown_conditions");
        player.GetItemExpectedPrice(item, out _, out long price);
        if (price <= 0) return Skip(null, "unsupported_offer");
        var space = player.ItemSpace(item);
        bool canAfford = player.CanAfford(price, -1);
        string reason = !canAfford ? "insufficient_funds" :
            !space.CanTakeItem || space.ItemIsGoingToVoidVault ? "inventory_full" : null;
        return new(item.type, true, reason, price, reason == null, canAfford);
    }

    private static (Listing[] listings, Item[] offers) Catalog(Player p, NPC merchant)
    {
        var original = p.currentShoppingSettings;
        try
        {
            p.currentShoppingSettings = Main.ShopHelper.GetShoppingSettings(p, merchant);
            if (!NPCShopDatabase.TryGetNPCShop(ShopName(merchant), out var shop) || shop is not NPCShop ordinary)
                throw new InvalidOperationException("Merchant shop unavailable.");
            var entries = ordinary.Entries.ToArray();
            var offers = entries.Select(e =>
            {
                if (e.Item.type <= 0 || e.Item.type >= ItemID.Count) return e.Item;
                var offer = e.Item.Clone();
                offer.isAShopItem = true;
                return offer;
            }).ToArray();
            return (entries.Select((entry,i) => Inspect(entry,offers[i],p)).ToArray(), offers);
        }
        finally { p.currentShoppingSettings = original; }
    }

    internal static bool VanillaServicesSupported => VirtualPlayerEngineProbe.VirtualPlayerEngineProbe.Engine?.Selected == "2026.09.3.1"
        && ModLoader.Mods.All(m => m.Name is "ModLoader" or "CompanionBridge");

    internal static string Inventory(Identity id, Player p) =>
        string.Concat(p.inventory.Select((item, slot) => ActorInventory.Token(id,item,slot)));

    internal static string[] BankTokens(Identity id, Player p) => new[]{p.bank,p.bank2,p.bank3,p.bank4}.SelectMany((bank,b) =>
        bank.item.Select((item,s) => ActorInventory.Token(id,item,$"bank:{b}:{s}"))).ToArray();

    private static int Unique(Listing[] listings, int type)
    {
        var index = Array.FindIndex(listings, l => l.itemType == type && l.available == true);
        return index >= 0 && listings.Count(l => l.itemType == type && l.available != false) == 1 ? index : -1;
    }

    internal static object Read(Identity id, JsonElement args, bool quote)
    {
        string entityRef = args.GetProperty("entityRef").GetString();
        var error = Resolve(id,entityRef,out var merchant, reading: true);
        if (error != null) throw new ShopReadException(error);
        var p = (Player)id.Actor;
        var funds = MerchantPayment.Balance(p);
        var (entries, _) = Catalog(p,merchant);
        if (!quote) return new { entityRef, shopName = ShopName(merchant), funds.carriedCopper, funds, entries };
        var lease = id.RequestLease ?? throw new ShopReadException("not_bound");
        lease.MerchantQuote = null;
        Quote current = null;
        int itemType = args.GetProperty("itemType").GetInt32();
        int quantity = args.TryGetProperty("quantity", out var requested) ? requested.GetInt32() : 1;
        if (quantity is < 1 or > 50) throw new ShopReadException("invalid_quantity");
        int index = Unique(entries,itemType);
        var listing = index < 0 ? null : entries[index];
        if (listing != null)
        {
            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            current = new(token,p,merchant,entityRef,id.World,id.Generation,id.Slot,itemType,quantity,listing.unitPrice.Value,
                Main.GameUpdateCount + 600, Environment.TickCount64 + 10_000, Inventory(id,p),funds,JsonSerializer.Serialize(entries));
            lease.MerchantQuote = current;
        }
        bool? affordable = listing?.unitPrice is long price ? p.CanAfford(checked(price * quantity), -1) : null;
        return new { entityRef,itemType,quantity,unitPrice=listing?.unitPrice,funds.carriedCopper,funds,canAfford=affordable,
            currency=listing?.currency ?? "unknown",stock=listing?.stock ?? "unknown",
            canPurchase=listing?.canPurchase == true && affordable == true,quoteToken=current?.Token,expiresAtGameUpdate=current?.Expires,
            reason=listing == null ? entries.FirstOrDefault(l => l.itemType == itemType)?.reason ?? "unsupported_offer" : affordable == false ? "insufficient_funds" : listing.reason };
    }

    internal static Result Buy(Identity id, JsonElement args)
    {
        string entityRef = args.GetProperty("entityRef").GetString();
        Result Reject(string reason) => new("failed",reason,new {entityRef,mutationAttempted=false},true,InputsAcquired:false);
        var lease = id.RequestLease;
        var q = lease?.MerchantQuote as Quote;
        bool matches = q != null && q.Token == args.GetProperty("quoteToken").GetString();
        if (matches) lease.MerchantQuote = null;
        string error = Resolve(id,entityRef,out var merchant);
        if (error != null) return Reject(error);
        var p = (Player)id.Actor;
        if (!matches || q.Actor != p || q.Merchant != merchant || q.Reference != entityRef || q.World != id.World ||
            q.Generation != id.Generation || q.Slot != id.Slot || Main.GameUpdateCount >= q.Expires || Environment.TickCount64 >= q.DeadlineMs ||
            q.Inventory != Inventory(id,p)) return Reject("stale_quote");
        int Count() => p.inventory.Take(58).Where(i => i.type == q.Type).Sum(i => i.stack);
        var beforeFunds = MerchantPayment.Balance(p);
        if (beforeFunds != q.Funds) return Reject("stale_quote");
        var (entries, offers) = Catalog(p,merchant);
        int index = Unique(entries,q.Type);
        if (index < 0 || entries[index].unitPrice != q.Price || JsonSerializer.Serialize(entries) != q.Catalog)
            return Reject("stale_quote");
        if (!entries[index].canPurchase) return Reject(entries[index].reason);
        var banks = MerchantPayment.NonCurrency(id,p);
        int beforeCount = Count();
        var offer = offers[index];
        if (!p.CanAfford(checked(q.Price * q.Quantity), -1)) return Reject("insufficient_funds");
        bool unitPaymentStarted = false;
        int verifiedQuantity = 0;
        var previousFunds = beforeFunds;
        var original = p.currentShoppingSettings;
        try
        {
            p.currentShoppingSettings = Main.ShopHelper.GetShoppingSettings(p, merchant);
            while (verifiedQuantity < q.Quantity)
            {
                unitPaymentStarted = false;
                p.GetItemExpectedPrice(offer, out _, out long currentPrice);
                if (currentPrice != q.Price)
                {
                    if (verifiedQuantity == 0) return Reject("stale_quote");
                    error = "stale_quote";
                    break;
                }
                error = MerchantPayment.Deliver(p,merchant,offer,q.Price,ref unitPaymentStarted);
                if (error != null)
                {
                    if (verifiedQuantity == 0 && !unitPaymentStarted) return Reject(error);
                    break;
                }
                var funds = MerchantPayment.Balance(p);
                if (previousFunds.totalCopper - funds.totalCopper != q.Price ||
                    Count() != beforeCount + verifiedQuantity + 1 ||
                    !banks.SequenceEqual(MerchantPayment.NonCurrency(id,p)) || !ServerAiNativeActions.Owned(id) ||
                    !merchant.active || !ReferenceEquals(Main.npc[merchant.whoAmI],merchant) ||
                    NativeCombatWorld.Reference(id,merchant) != entityRef)
                {
                    error = "purchase_unconfirmed";
                    break;
                }
                verifiedQuantity++;
                previousFunds = funds;
            }
        }
        catch when (unitPaymentStarted || verifiedQuantity > 0) { error = "purchase_unconfirmed"; }
        finally { p.currentShoppingSettings = original; }
        var afterFunds = MerchantPayment.Balance(p);
        int afterCount = Count();
        bool bankNonCurrencyUnchanged = banks.SequenceEqual(MerchantPayment.NonCurrency(id,p));
        bool effectsMatch = beforeFunds.totalCopper - afterFunds.totalCopper == q.Price * verifiedQuantity &&
            afterCount == beforeCount + verifiedQuantity &&
            bankNonCurrencyUnchanged && ServerAiNativeActions.Owned(id) && ReferenceEquals(Main.npc[merchant.whoAmI],merchant) &&
            merchant.active && NativeCombatWorld.Reference(id,merchant) == entityRef;
        bool ok = effectsMatch && error == null && verifiedQuantity == q.Quantity;
        bool partial = effectsMatch && verifiedQuantity > 0 && !unitPaymentStarted &&
            error is ("stale_quote" or "inventory_full" or "purchase_vetoed");
        string status = ok ? "completed" : partial ? "failed" : "unknown";
        string reason = ok ? "purchase_verified" : partial ? error : "purchase_unconfirmed";
        string effectStatus = ok ? "complete" : partial ? "partial" : "unconfirmed";
        return new(status,reason,
            new { entityRef,quoteToken=q.Token,itemType=q.Type,quantity=q.Quantity,verifiedQuantity,effectStatus,unitPrice=q.Price,beforeFunds,afterFunds,beforeCount,afterCount,bankNonCurrencyUnchanged },
            true,ok,InputsAcquired:false);
    }
}
