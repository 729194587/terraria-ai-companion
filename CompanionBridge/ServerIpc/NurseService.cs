using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Identity = CompanionBridge.ServerIpc.AiReadOnlyProtocol.Identity;
using Result = CompanionBridge.ServerIpc.AiAtomicActions.NativeResult;

namespace CompanionBridge.ServerIpc;

internal static class NurseService
{
    // GUIChatDrawInner is local-player UI. Its per-actor calculation below is pinned
    // to this engine and the vanilla service hooks audited for this configuration.
    internal static bool Supported => MerchantShop.VanillaServicesSupported;

    private sealed record Offer(string Token, Player Actor, NPC Nurse, string Reference, string World,
        long Generation, int Slot, int Life, int MaxLife, int Heal, int Price, int[] Removable,
        long Funds, ulong Expires, long Deadline);

    private static int ProgressMultiplier()
    {
        if (NPC.downedGolemBoss) return 200;
        if (NPC.downedPlantBoss) return 150;
        if (NPC.downedMechBossAny) return 100;
        if (Main.hardMode) return 60;
        if (NPC.downedBoss3 || NPC.downedQueenBee) return 25;
        if (NPC.downedBoss2) return 10;
        return NPC.downedBoss1 ? 3 : 1;
    }

    private static bool Removable(Player p, int slot) => p.buffTime[slot] > 0 &&
        Main.debuff[p.buffType[slot]] && !BuffID.Sets.NurseCannotRemoveDebuff[p.buffType[slot]];

    private sealed record Treatment(int Heal, int Price, int[] Debuffs, bool Allowed);

    private static Treatment Inspect(Player p, NPC nurse)
    {
        var slots = Enumerable.Range(0,p.buffType.Length).Where(s => Removable(p,s)).ToArray();
        int heal = Math.Max(0,p.statLifeMax2 - p.statLife);
        int basis = heal + 100 * slots.Count(s => p.buffTime[s] > 60);
        var settings = Main.ShopHelper.GetShoppingSettings(p,nurse);
        int price = (int)(basis * ProgressMultiplier() * (Main.expertMode ? 2 : 1) * settings.PriceAdjustment);
        bool remove = true;
        string reason = "";
        bool allowed = PlayerLoader.ModifyNurseHeal(p,nurse,ref heal,ref remove,ref reason);
        PlayerLoader.ModifyNursePrice(p,nurse,heal,remove,ref price);
        return new(heal,Math.Max(0,price),remove ? slots.Select(s => p.buffType[s]).ToArray() : Array.Empty<int>(),allowed);
    }

    internal static object Quote(Identity id, JsonElement args)
    {
        string entityRef = args.GetProperty("entityRef").GetString();
        string error = MerchantShop.ResolveService(id,entityRef,NPCID.Nurse,Supported,out var nurse);
        if (error != null) throw new ShopReadException(error);
        var p = (Player)id.Actor;
        var lease = id.RequestLease ?? throw new ShopReadException("not_bound");
        lease.MerchantQuote = null;
        var plan = Inspect(p,nurse);
        long funds = MerchantPayment.Coins(p.inventory,54);
        string reason = !plan.Allowed ? "nurse_vetoed" : plan.Price <= 0 ? "no_paid_treatment" :
            funds < plan.Price ? "insufficient_carried_funds" : null;
        Offer offer = null;
        if (plan.Allowed && plan.Price > 0)
        {
            offer = new(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
                p,nurse,entityRef,id.World,id.Generation,id.Slot,p.statLife,p.statLifeMax2,plan.Heal,plan.Price,
                plan.Debuffs,funds,Main.GameUpdateCount+600,Environment.TickCount64+10000);
            lease.MerchantQuote = offer;
        }
        return new { entityRef,life=p.statLife,maxLife=p.statLifeMax2,heal=plan.Heal,priceCopper=plan.Price,
            removableDebuffs=plan.Debuffs,carriedCopper=funds,canTreat=reason==null,reason,
            quoteToken=offer?.Token,expiresAtGameUpdate=offer?.Expires };
    }

    internal static Result Heal(Identity id, JsonElement args)
    {
        string entityRef = args.GetProperty("entityRef").GetString();
        string quoteToken = args.GetProperty("quoteToken").GetString();
        Result Reject(string reason) => new("failed",reason,new {entityRef,mutationAttempted=false},true,InputsAcquired:false);
        var lease = id.RequestLease;
        var offer = lease?.MerchantQuote as Offer;
        bool matches = offer != null && offer.Token == quoteToken;
        if (matches) lease.MerchantQuote = null;
        string error = MerchantShop.ResolveService(id,entityRef,NPCID.Nurse,Supported,out var nurse);
        if (error != null) return Reject(error);
        var p = (Player)id.Actor;
        if (!matches || offer.Actor != p || offer.Nurse != nurse || offer.Reference != entityRef ||
            offer.World != id.World || offer.Generation != id.Generation || offer.Slot != id.Slot ||
            Main.GameUpdateCount >= offer.Expires || Environment.TickCount64 >= offer.Deadline) return Reject("stale_quote");
        var plan = Inspect(p,nurse);
        long beforeCopper = MerchantPayment.Coins(p.inventory,54);
        if (!plan.Allowed || plan.Price != offer.Price || plan.Heal != offer.Heal || p.statLife != offer.Life ||
            p.statLifeMax2 != offer.MaxLife || !plan.Debuffs.SequenceEqual(offer.Removable)) return Reject("stale_quote");
        if (beforeCopper < plan.Price) return Reject("insufficient_carried_funds");
        if (beforeCopper != offer.Funds) return Reject("stale_quote");
        var banks = MerchantShop.BankTokens(id,p);
        var retained = p.buffType.Where(t => t != 0 && !plan.Debuffs.Contains(t)).ToArray();
        bool treated = false;
        try
        {
            if (p.BuyItem(plan.Price,-1))
            {
                p.statLife += plan.Heal;
                for (int i=p.buffType.Length-1;i>=0;i--)
                    if (plan.Debuffs.Contains(p.buffType[i])) p.DelBuff(i);
                PlayerLoader.PostNurseHeal(p,nurse,plan.Heal,true,plan.Price);
                treated = true;
            }
        }
        catch (Exception) { treated = false; } // Payment may already have changed the inventory.
        long afterCopper = MerchantPayment.Coins(p.inventory,54);
        bool banksUnchanged = banks.SequenceEqual(MerchantShop.BankTokens(id,p));
        bool buffsVerified = retained.SequenceEqual(p.buffType.Where(t => t != 0));
        bool verified = treated && p.statLife == offer.Life + plan.Heal && beforeCopper-afterCopper == plan.Price &&
            banksUnchanged && buffsVerified && ServerAiNativeActions.Owned(id) && nurse.active &&
            ReferenceEquals(Main.npc[nurse.whoAmI],nurse);
        return new(verified?"completed":"unknown",verified?"treatment_verified":"treatment_unconfirmed",
            new {entityRef,quoteToken,priceCopper=plan.Price,beforeCopper,afterCopper,beforeLife=offer.Life,
                afterLife=p.statLife,heal=plan.Heal,removableDebuffs=plan.Debuffs,buffsVerified,banksUnchanged},
            true,verified,InputsAcquired:false);
    }
}
