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

    private sealed record Listing(int itemType, bool? available, string reason, long? unitPrice, bool canPurchase)
    {
        public string currency => unitPrice.HasValue ? "copper" : "unknown";
        public string stock => unitPrice.HasValue ? "unlimited" : "unknown";
    }
    private sealed record Quote(string Token, Player Actor, NPC Merchant, string Reference, string World, long Generation,
        int Slot, int Type, long Price, ulong Expires, long DeadlineMs, string Inventory, string Catalog);
    private static string Name => NPCShopDatabase.GetShopName(NPCID.Merchant, "Shop");

    private static string Resolve(Identity id, string reference, out NPC merchant) =>
        ResolveService(id, reference, NPCID.Merchant, Supported, out merchant);

    internal static string ResolveService(Identity id, string reference, int npcType, bool supported, out NPC merchant)
    {
        merchant = null;
        foreach (var npc in Main.ActiveNPCs)
            if (NativeCombatWorld.Reference(id, npc) == reference) { merchant = npc; break; }
        if (merchant == null || merchant.life <= 0) return "stale_entity_ref";
        if (!supported || merchant.type != npcType) return npcType == NPCID.Merchant ? "unsupported_merchant" : "unsupported_npc_service";
        var player = (Player)id.Actor;
        return player.dead || player.ghost || !merchant.CanTalk || player.Distance(merchant.Center) > 80f
            ? (npcType == NPCID.Merchant ? "merchant_not_in_range_or_busy" : "npc_not_in_range_or_busy") : null;
    }

    private static Listing Inspect(NPCShop.Entry entry, Item item, Player player, long carried)
    {
        Listing Skip(bool? available, string reason) => new(item.type, available, reason, null, false);
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
        string reason = carried < price ? "insufficient_carried_funds" :
            !space.CanTakeItem || space.ItemIsGoingToVoidVault ? "inventory_full" : null;
        return new(item.type, true, reason, price, reason == null);
    }

    private static (Listing[] listings, Item[] offers) Catalog(Player p, NPC merchant, long carried)
    {
        var original = p.currentShoppingSettings;
        try
        {
            p.currentShoppingSettings = Main.ShopHelper.GetShoppingSettings(p, merchant);
            if (!NPCShopDatabase.TryGetNPCShop(Name, out var shop) || shop is not NPCShop ordinary)
                throw new InvalidOperationException("Merchant shop unavailable.");
            var entries = ordinary.Entries.ToArray();
            var offers = entries.Select(e =>
            {
                if (e.Item.type <= 0 || e.Item.type >= ItemID.Count) return e.Item;
                var offer = e.Item.Clone();
                offer.isAShopItem = true;
                return offer;
            }).ToArray();
            return (entries.Select((entry,i) => Inspect(entry,offers[i],p,carried)).ToArray(), offers);
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
        var error = Resolve(id,entityRef,out var merchant);
        if (error != null) throw new ShopReadException(error);
        var p = (Player)id.Actor;
        long carriedCopper = MerchantPayment.Coins(p.inventory,54);
        var (entries, _) = Catalog(p,merchant,carriedCopper);
        if (!quote) return new { entityRef, shopName = Name, carriedCopper, entries };
        var lease = id.RequestLease ?? throw new ShopReadException("not_bound");
        lease.MerchantQuote = null;
        Quote current = null;
        int itemType = args.GetProperty("itemType").GetInt32();
        int index = Unique(entries,itemType);
        var listing = index < 0 ? null : entries[index];
        if (listing != null)
        {
            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            current = new(token,p,merchant,entityRef,id.World,id.Generation,id.Slot,itemType,listing.unitPrice.Value,
                Main.GameUpdateCount + 600, Environment.TickCount64 + 10_000, Inventory(id,p),JsonSerializer.Serialize(entries));
            lease.MerchantQuote = current;
        }
        return new { entityRef,itemType,quantity=1,unitPrice=listing?.unitPrice,carriedCopper,
            currency=listing?.currency ?? "unknown",stock=listing?.stock ?? "unknown",
            canPurchase=listing?.canPurchase == true,quoteToken=current?.Token,expiresAtGameUpdate=current?.Expires,
            reason=listing == null ? entries.FirstOrDefault(l => l.itemType == itemType)?.reason ?? "unsupported_offer" : listing.reason };
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
        long Copper() => MerchantPayment.Coins(p.inventory,54);
        int Count() => p.inventory.Take(58).Where(i => i.type == q.Type).Sum(i => i.stack);
        long beforeCopper = Copper();
        var (entries, offers) = Catalog(p,merchant,beforeCopper);
        int index = Unique(entries,q.Type);
        if (index < 0 || entries[index].unitPrice != q.Price || JsonSerializer.Serialize(entries) != q.Catalog)
            return Reject("stale_quote");
        if (!entries[index].canPurchase) return Reject(entries[index].reason);
        var banks = BankTokens(id,p);
        int beforeCount = Count();
        var offer = offers[index];
        bool paymentStarted = false;
        var original = p.currentShoppingSettings;
        try
        {
            p.currentShoppingSettings = Main.ShopHelper.GetShoppingSettings(p, merchant);
            error = MerchantPayment.Deliver(p,merchant,offer,q.Price,ref paymentStarted);
            if (error != null && !paymentStarted) return Reject(error);
        }
        catch when (paymentStarted) { error = "purchase_unconfirmed"; }
        finally { p.currentShoppingSettings = original; }
        long afterCopper = Copper();
        int afterCount = Count();
        bool banksUnchanged = banks.SequenceEqual(BankTokens(id,p));
        bool ok = error == null && beforeCopper - afterCopper == q.Price && afterCount == beforeCount + 1 &&
            banksUnchanged && ServerAiNativeActions.Owned(id) && ReferenceEquals(Main.npc[merchant.whoAmI],merchant) &&
            merchant.active && NativeCombatWorld.Reference(id,merchant) == entityRef;
        return new(ok ? "completed" : "unknown",ok ? "purchase_verified" : "purchase_unconfirmed",
            new { entityRef,quoteToken=q.Token,itemType=q.Type,quantity=1,unitPrice=q.Price,beforeCopper,afterCopper,beforeCount,afterCount,banksUnchanged },
            true,ok,InputsAcquired:false);
    }
}
