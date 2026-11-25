using System;
using Terraria;
using Terraria.ID;
using CompanionBridge.Motion;
using VirtualPlayerEngineProbe;
using Identity = CompanionBridge.ServerIpc.AiReadOnlyProtocol.Identity;

namespace CompanionBridge.ServerIpc;

internal sealed partial class NativeCombatWorld
{
    private bool maintenance,maintenanceYield,manualSummon;
    private float initialSlots;
    private int restoreSlot;
    private Item restoreItem;
    private ulong started;
    private static string maintenanceStatus;
    private static ulong nextCheck;
    private static bool maintenanceUnknown;
    private static int confirmedCasts;
    private CombatHit lastMelee,lastSummon;
    internal static bool Maintaining=>current?.maintenance==true && !current.manualSummon;
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
        ? ReferenceEquals(current,this) && (manualSummon || MatchesLoadout(identity) && summonRole?.Type==item.type)
        : SameSpawn(Main.npc[Target.Slot]) && StillEligible(npc);

    private static bool SafeToCast(Player p)=>!p.noItems && !p.CCed && GameNavigation.Ordinary(p)
        && !ServerTorchMaintenance.Threatened(p);

    // Both verified and externally created summons occupy native capacity. Only a
    // verified item-use root can subsequently supply combat evidence.
    internal static string SummonRefusal(Player p,Item staff,bool additional=false)
    {
        int type=SummonRoot(staff);
        float slots=0;
        int turrets=0;
        foreach(var q in Main.ActiveProjectiles)
        {
            if(q.owner!=p.whoAmI)continue;
            if(!additional && q.type==type && (!q.TryGetGlobalProjectile<CombatProjectile>(out var trace)
                || trace.Flight==null || trace.Flight.Owns(q)))return "summon_already_active";
            slots+=q.minionSlots;
            if(q.sentry)turrets++;
        }
        if(staff.sentry?turrets>=p.maxTurrets:slots+Math.Max(ItemID.Sets.StaffMinionSlotsRequired[staff.type],ContentSamples.ProjectilesByType[type].minionSlots)>p.maxMinions)
            return "summon_capacity_unavailable";
        return CombatBallistics.ManaAffordable(p,staff)?null:"combat_mana_unavailable";
    }

    private static float OccupiedSlots(Player p)
    {
        float used=0;
        foreach(var q in Main.ActiveProjectiles) if(q.owner==p.whoAmI) used+=q.minionSlots;
        return used;
    }

    internal static NativeCombatWorld StartSummon(Identity frame)
    {
        var p=(Player)frame.Actor;
        return current=new NativeCombatWorld(frame,null,"use_item",null){
            manualSummon=true,maintenance=true,slot=p.selectedItem,item=p.HeldItem,started=frame.GameUpdate,
            initialSlots=OccupiedSlots(p),Weapon=new(p.selectedItem,p.HeldItem.type,p.HeldItem.Name)};
    }

    internal AiAtomicActions.NativeResult PollSummon(bool cancel=false)
    {
        if(!ReferenceEquals(current,this) || !Owned() || !ValidWeapon())
            return new("unknown","summon_context_changed",null,Release());
        if(cancel || fault!=null || Main.GameUpdateCount-started>120)
            return new(cancel?"cancelled":"unknown",cancel?"explicit_stop":"summon_native_unconfirmed",null,Release());
        if(!summonAttempted || player.itemAnimation!=0 || player.itemTime!=0 || player.reuseDelay!=0)
            return new("running","native_summon_pending",null,false);
        float used=OccupiedSlots(player);
        bool root=false;
        foreach(var q in Main.ActiveProjectiles)
            if(q.owner==player.whoAmI && q.type==item.shoot && q.identity==observedRootIdentity) root=true;
        bool buff=player.HasBuff(item.buffType);
        bool body=item.type!=ItemID.AbigailsFlower || player.ownedProjectileCounts[ProjectileID.AbigailMinion]>0;
        if (!body) return new("running","native_summon_pending",null,false);
        bool ok=summonShootObserved && root && buff && used>initialSlots;
        var evidence=new {itemType=item.type,rootType=item.shoot,observedRootIdentity,buffType=item.buffType,buff,
            cycleVerified=summonShootObserved && player.releaseUseItem,slotsBefore=initialSlots,slotsAfter=used,maxSlots=player.maxMinions,effectStatus=ok?"native_summon_verified":"unconfirmed"};
        bool released=Release();
        return new(ok && released?"completed":"unknown",ok && released?"native_summon_verified":"summon_native_unconfirmed",evidence,released,ok && released);
    }

    internal static AiReadOnlyProtocol.AutonomyGrant LoadoutGrant(Identity frame) =>
        frame != null && MatchesLoadout(frame) ? configuredActor.Autonomy : null;

    internal static void TickMaintenance(Identity frame,bool busy)
    {
        try
        {
            if(Maintaining)
            {
                if (current.ActorBinding.Autonomy?.Valid == false)
                    current.EndMaintenance(false);
                else
                    current.AdvanceMaintenance(frame,busy);
                return;
            }
            if(summonRole==null || maintenanceUnknown || !MatchesLoadoutFrame(frame) || frame.GameUpdate<nextCheck)return;
            var owner = LoadoutGrant(frame);
            if (owner != null)
            {
                if (!owner.Valid || !owner.InputsReleased) return;
                frame = frame with { Autonomy = owner };
            }
            nextCheck=frame.GameUpdate+12;
            if(SummonPaused(frame)){maintenanceStatus="summon_paused";return;}
            if(busy || !ServerAiNativeActions.BodyIdle(frame)){maintenanceStatus="body_busy";return;}
            var p=(Player)frame.Actor;
            if(!SafeToCast(p)){maintenanceStatus="waiting_safe_window";return;}
            var candidate=Inspect(p,summonRole,"summon");
            if(candidate.Error!=null){maintenanceStatus=candidate.Error;return;}
            var cast=new NativeCombatWorld(frame,null,"summon_maintenance",null){
                maintenance=true,slot=candidate.Slot,item=candidate.Item,started=frame.GameUpdate,
                restoreSlot=p.selectedItem,restoreItem=p.HeldItem,
                Weapon=new(candidate.Slot,candidate.Item.type,candidate.Item.Name)};
            if (owner != null) owner.BackgroundBusy = true;
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
            if (identity.Autonomy != null && Owned() && ServerAiNativeActions.Released(player))
                identity.Autonomy.BackgroundBusy = false;
        }
        catch(Exception e)
        {
            ProbeSession.Quarantine("Summon selection release unconfirmed: "+e.Message);
            throw;
        }
        finally {if(ReferenceEquals(current,this))current=null;}
    }
}
