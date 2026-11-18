using System;
using Terraria;
using Terraria.ID;
using CompanionBridge.Motion;
using VirtualPlayerEngineProbe;
using Identity = CompanionBridge.ServerIpc.AiReadOnlyProtocol.Identity;

namespace CompanionBridge.ServerIpc;

internal sealed partial class NativeCombatWorld
{
    private bool maintenance,maintenanceYield;
    private int restoreSlot;
    private Item restoreItem;
    private ulong started;
    private static string maintenanceStatus;
    private static ulong nextCheck;
    private static bool maintenanceUnknown;
    private static int confirmedCasts;
    private CombatHit lastMelee,lastSummon;
    internal static bool Maintaining=>current?.maintenance==true;
    public CombatContributions Contributions => MatchesLoadout(identity) && summonRole!=null
        ?new(lastMelee,lastSummon):null;

    private static void ResetMaintenance()
    {
        maintenanceStatus="waiting_safe_window";
        confirmedCasts=0;
        maintenanceUnknown=false;
        nextCheck=0;
    }

    private static object MaintenanceState()=>new {status=maintenanceStatus,confirmedCasts};
    private bool ItemContextValid()=>maintenance
        ? ReferenceEquals(current,this) && MatchesLoadout(identity) && summonRole?.Type==item.type
        : SameSpawn(Main.npc[Target.Slot]) && StillEligible(npc);

    private static bool SafeToCast(Player p)=>!p.noItems && !p.CCed && GameNavigation.Ordinary(p)
        && !ServerTorchMaintenance.Threatened(p);

    // Both verified and externally created summons occupy native capacity. Only a
    // verified item-use root can subsequently supply combat evidence.
    private static string SummonRefusal(Player p,Item staff)
    {
        int type=SummonRoot(staff);
        float slots=0;
        int turrets=0;
        foreach(var q in Main.ActiveProjectiles)
        {
            if(q.owner!=p.whoAmI)continue;
            if(q.type==type && (!q.TryGetGlobalProjectile<CombatProjectile>(out var trace)
                || trace.Flight==null || trace.Flight.Owns(q)))return "summon_already_active";
            if(q.minion)slots+=q.minionSlots;
            if(q.sentry)turrets++;
        }
        if(type==ProjectileID.FrostHydra?turrets>=p.maxTurrets:slots+1>p.maxMinions)
            return "summon_capacity_unavailable";
        return CombatBallistics.ManaAffordable(p,staff)?null:"combat_mana_unavailable";
    }

    internal static void TickMaintenance(Identity frame,bool busy)
    {
        try
        {
            if(Maintaining)
            {
                current.AdvanceMaintenance(frame,busy);
                return;
            }
            if(summonRole==null || maintenanceUnknown || !MatchesLoadoutFrame(frame) || frame.GameUpdate<nextCheck)return;
            nextCheck=frame.GameUpdate+12;
            if(busy || !ServerAiNativeActions.BodyIdle(frame)){maintenanceStatus="body_busy";return;}
            var p=(Player)frame.Actor;
            if(!SafeToCast(p)){maintenanceStatus="waiting_safe_window";return;}
            var candidate=Inspect(p,summonRole,"summon");
            if(candidate.Error!=null){maintenanceStatus=candidate.Error;return;}
            var cast=new NativeCombatWorld(frame,null,"summon_maintenance",null){
                maintenance=true,slot=candidate.Slot,item=candidate.Item,started=frame.GameUpdate,
                restoreSlot=p.selectedItem,restoreItem=p.HeldItem,
                Weapon=new(candidate.Slot,candidate.Item.type,candidate.Item.Name)};
            current=cast;
            maintenanceStatus="summon_cast";
            if(p.selectedItem!=cast.slot)ActorItemUse.Select(p,frame.World,frame.Generation,cast.slot);
            if(!cast.ValidWeapon())throw new InvalidOperationException("Summon selection unconfirmed.");
        }
        catch(Exception e)
        {
            maintenanceUnknown=true;
            maintenanceStatus="summon_native_unconfirmed";
            // Preserve existing quarantine semantics when cleanup itself is uncertain.
            try {if(Maintaining)current.EndMaintenance(false);}
            catch(Exception cleanup){ProbeSession.Quarantine("Summon maintenance cleanup: "+cleanup.Message);}
            ProbeSession.Log("summon-maintenance-error",new{error=e.Message});
        }
    }

    private static bool MatchesLoadoutFrame(Identity frame)=>frame?.CanAct==true && MatchesLoadout(frame)
        && ServerAiNativeActions.Owned(frame);

    private void AdvanceMaintenance(Identity frame,bool busy)
    {
        if(!MatchesLoadoutFrame(frame) || !Owned() || !ValidWeapon())
        {
            maintenanceUnknown=true;
            maintenanceStatus="summon_binding_or_weapon_changed";
            EndMaintenance(false);
            return;
        }
        if(fault!=null || frame.GameUpdate-started>120)throw new InvalidOperationException("Summon input unconfirmed.",fault);
        maintenanceYield|=busy || player.CCed || player.noItems || !GameNavigation.Ordinary(player,combatSwing:true)
            || ServerTorchMaintenance.Threatened(player);
        if(player.itemAnimation!=0 || player.itemTime!=0 || player.reuseDelay!=0)return;
        if(!summonAttempted && !maintenanceYield && SummonRefusal(player,item)==null)return;
        if(summonAttempted && (!summonShootObserved || observedRootIdentity==null
            || FindSummon(player,SummonRoot(item))?.Identity!=observedRootIdentity))
            throw new InvalidOperationException("Native summon root unconfirmed.");
        if(summonAttempted)confirmedCasts++;
        maintenanceStatus=summonAttempted?"summon_confirmed":"summon_deferred";
        EndMaintenance(true);
    }

    private void EndMaintenance(bool restore)
    {
        try
        {
            // Only the still-bound owner may unwind an interrupted native item cycle.
            if(!restore && Owned() && ValidWeapon() && !Release())
                throw new InvalidOperationException("Summon inputs not released.");
            if(restore && Owned() && ValidWeapon() && restoreSlot!=slot && restoreSlot is >=0 and <10
                && ReferenceEquals(player.inventory[restoreSlot],restoreItem))
                ActorItemUse.Select(player,identity.World,identity.Generation,restoreSlot);
        }
        catch(Exception e)
        {
            ProbeSession.Quarantine("Summon selection release unconfirmed: "+e.Message);
            throw;
        }
        finally {if(ReferenceEquals(current,this))current=null;}
    }
}
