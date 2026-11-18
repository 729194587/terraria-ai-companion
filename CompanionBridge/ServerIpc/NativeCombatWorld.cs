using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.DataStructures;
using CompanionBridge.Common.Systems;
using CompanionBridge.Motion;
using VirtualPlayerEngineProbe;
using Identity = CompanionBridge.ServerIpc.AiReadOnlyProtocol.Identity;

namespace CompanionBridge.ServerIpc;

internal sealed partial class NativeCombatWorld(Identity identity, ServerAiNavigation navigation, string actionId, string entityRef, Func<Identity,string,bool> reflexAllowed = null,AiAtomicActions.CombatAdmission admission=null,bool bossEncounter=false) : ICombatWorld
{
    private const int ThreatRange = 112, AcquireRange = 640, SwingGap = 34;
    private static NativeCombatWorld current;
    private static NativeCombatWorld checking;
    private static (Player actor, string world, long generation, string entityRef, ulong tick) recentHurt;
    private readonly Player player = (Player)identity.Actor;
    private Identity ActorBinding => identity;
    private bool Owned() => ServerAiNativeActions.Owned(identity);
    private NPC npc;
    private AtomicNpcIdentity spawn;
    private Guid spawnToken;
    private Item item;
    private int slot;
    private readonly CombatInputCycle inputs = new();
    private Exception fault;
    private ServerAiNavigation.Run approach;
    private ulong plannedAt;
    private int missedSwings;
    private bool drainSwing;
    private int targetEpoch;
    private CombatBallistics.Kind? ShotKind=>CombatBallistics.Family(item);
    private bool Summoning => SummonRoot(item) != 0;
    private bool Ranged=>ShotKind!=null || Summoning;
    private bool summonAttempted, summonShootObserved;
    private bool? observedBuff;
    private int? observedRootIdentity;
    private int verifiedChildren, unconfirmedChildren;
    public CombatSummonObservation Summon => Summoning ? new(summonAttempted,summonShootObserved,
        SummonRoot(item),observedRootIdentity,observedBuff,verifiedChildren,unconfirmedChildren) : null;

    private static int SummonRoot(Item held) => held?.type switch
    {
        ItemID.SlimeStaff when held.shoot == ProjectileID.BabySlime => ProjectileID.BabySlime,
        ItemID.StaffoftheFrostHydra when held.shoot == ProjectileID.FrostHydra => ProjectileID.FrostHydra,
        _ => 0
    };

    private static Shot FindSummon(Player actor, int type)
    {
        foreach (var projectile in Main.ActiveProjectiles)
            if (projectile.type == type && projectile.owner == actor.whoAmI &&
                projectile.TryGetGlobalProjectile<CombatProjectile>(out var trace) &&
                trace.Flight is { Verified: true, PersistentRoot: true } root && root.Owns(projectile))
                return root;
        return null;
    }
    private CombatBallistics.Round? firing;
    private bool ammoChanged,inShoot;
    // Mana still held at the native shoot step, i.e. after ItemCheck_PayMana
    // has already funded the cast (possibly through QuickMana).
    internal int ManaAtLaunch { get; private set; }
    private const int MaxFiringPositions=8;
    private sealed record FiringAttempt(LandingRegion Goal,bool Left,ServerAiNavigation.Run Run);
    private readonly List<FiringAttempt> attemptedFiringPositions=new();

    public CombatTarget Target { get; private set; }
    public CombatWeapon Weapon { get; private set; }
    public CombatHit LastHit { get; private set; }
    public int ItemChecks => inputs.ItemChecks;
    public int CombinedUpdates => inputs.CombinedUpdates;
    internal static bool Running => current != null;

    internal static string Reference(Identity identity, NPC n) =>
        Reference(identity.World, identity.Generation, n);

    private static string Reference(string world, long generation, NPC n) =>
        world + ":" + generation + ":npc:" + n.whoAmI + ":" + n.GetGlobalNPC<AtomicNpcIdentity>().Token;

    private static bool Enemy(NPC n, Player p) => n != null && n.active && n.life > 0 && n.damage > 0
        && !n.friendly && !n.townNPC && !n.boss && n.realLife < 0 && !NPCID.Sets.ShouldBeCountedAsBoss[n.type]
        && n.CanBeChasedBy(p);

    private static bool WeaponSupported(Item i) => !i.IsAir && i.type < ItemID.Count
        && !i.consumable && !i.channel && i.damage > 0 && i.createTile < 0 && i.createWall < 0
        && i.pick == 0 && i.axe == 0 && i.hammer == 0 && i.mountType < 0
        && i.useTime > 0 && i.useAnimation > 0
        && (SummonRoot(i) != 0 && i.CountsAsClass(Terraria.ModLoader.DamageClass.Summon)
            || CombatBallistics.Family(i)==CombatBallistics.Kind.Ranged
                && i.CountsAsClass(Terraria.ModLoader.DamageClass.Ranged)
            || CombatBallistics.Family(i)==CombatBallistics.Kind.Magic
                && i.CountsAsClass(Terraria.ModLoader.DamageClass.Magic)
            || ActorItemUse.Supported(i) && i.useStyle == ItemUseStyleID.Swing
                && i.CountsAsClass(Terraria.ModLoader.DamageClass.Melee) && !i.noMelee && !i.noUseGraphic);

    private sealed record WeaponRole(int Key,int Type);
    private static Identity configuredActor;
    private static WeaponRole primary,secondary,summonRole;
    private bool waitingSwitch;
    private int pendingSlot=-1;
    private ulong pendingSince;
    private static long version;
    private sealed record Refusal(string reason,ulong gameUpdate,string entityRef);
    private static Refusal lastRefusal;
    internal static void ClearLoadout()
    {
        configuredActor=null;
        if(Maintaining)current.EndMaintenance(false);
        primary=secondary=summonRole=null;
        ResetMaintenance();
        lastRefusal=null;
        version=0;
    }

    internal static Func<bool> CapturePrimary(Identity frame)
    {
        if(!Configured(frame))return null;
        var revision=version;
        var p=(Player)frame.Actor;
        Item Instance(WeaponRole role,bool beforeRelease)=>role==null?null:
            beforeRelease?ActorMining.AfterRelease(p,frame.World,frame.Generation,HotbarSlots.ToInventorySlot(role.Key)):
            p.inventory[HotbarSlots.ToInventorySlot(role.Key)];
        var first=Instance(primary,true);
        var second=Instance(secondary,true);
        return ()=>MatchesLoadout(frame) && version==revision && ReferenceEquals(first,Instance(primary,false))
            && ReferenceEquals(second,Instance(secondary,false));
    }

    private static bool MatchesLoadout(Identity frame) => configuredActor!=null
        && ReferenceEquals(frame.Actor,configuredActor.Actor) && frame.World==configuredActor.World
        && frame.Generation==configuredActor.Generation && frame.Slot==configuredActor.Slot;
    private static bool Configured(Identity frame)=>MatchesLoadout(frame)&&(primary!=null||secondary!=null||summonRole!=null);
    private static bool SupportedRole(Item weapon)=>WeaponSupported(weapon) && SummonRoot(weapon)==0;

    internal static string LoadoutFamily(Item item)
    {
        if(!WeaponSupported(item))return null;
        if(SummonRoot(item)!=0)return "summon";
        return CombatBallistics.Family(item) switch { CombatBallistics.Kind.Ranged=>"ranged",
            CombatBallistics.Kind.Magic=>"magic", _=>"melee" };
    }

    internal static string Configure(Identity frame,System.Text.Json.JsonElement args)
    {
        string ReadRole(string name,out WeaponRole next)
        {
            next=null;
            if(!args.TryGetProperty(name,out var role))return null;
            int key=role.GetProperty("hotbarKey").GetInt32(),index=HotbarSlots.ToInventorySlot(key);
            var weapon=((Player)frame.Actor).inventory[index];
            if(ActorInventory.Token(frame,weapon,index)!=role.GetProperty("inventoryToken").GetString())
                return "stale_inventory_token";
            if(weapon.type!=role.GetProperty("expectedTypeId").GetInt32() || !(name=="summon"?WeaponSupported(weapon)&&SummonRoot(weapon)!=0:SupportedRole(weapon)))
                return $"combat_{name}_unsupported";
            next=new(key,weapon.type);
            return null;
        }
        if(ReadRole("primary",out var first) is {} error)return error;
        if(ReadRole("secondary",out var second) is {} invalid)return invalid;
        if(ReadRole("summon",out var staff) is {} summonError)return summonError;
        if(first!=null && first.Key==second?.Key || staff!=null && (staff.Key==first?.Key || staff.Key==second?.Key))
            return "combat_duplicate_weapon_slot";
        version=MatchesLoadout(frame)?version+1:1;
        lastRefusal=null;
        configuredActor=frame;
        primary=first;
        secondary=second;
        summonRole=staff;
        ResetMaintenance();
        return null;
    }

    private readonly record struct Candidate(int Slot,Item Item,string Error);
    private static Candidate Inspect(Player player,WeaponRole role,string name,Identity handoff=null)
    {
        int slot=HotbarSlots.ToInventorySlot(role.Key);
        var item=handoff==null?player.inventory[slot]:ActorMining.AfterRelease(player,handoff.World,handoff.Generation,slot);
        bool supported=name=="summon"?WeaponSupported(item)&&SummonRoot(item)!=0:SupportedRole(item);
        string error=item.type!=role.Type || !supported?$"combat_{name}_unavailable":
            name=="summon"?SummonRefusal(player,item):ShotRefusal(player,item);
        return new(slot,item,error);
    }

    internal static object Loadout(Identity frame)
    {
        bool bound=MatchesLoadout(frame);
        object Describe(WeaponRole role,string name)
        {
            if(!bound || role==null)return null;
            var choice=Inspect((Player)frame.Actor,role,name);
            return new {hotbarKey=role.Key,expectedTypeId=role.Type,actualTypeId=choice.Item.type,
                available=choice.Error==null,reason=choice.Error};
        }
        return new {version=bound?version:0,lastRefusal=bound?lastRefusal:null,
            primary=Describe(primary,"primary"),secondary=Describe(secondary,"secondary"),summon=Describe(summonRole,"summon"),
            maintenance=bound && summonRole!=null?MaintenanceState():null};
    }

    private static bool Effective(Player player,NPC enemy,Candidate candidate,bool retaining)
    {
        if(CombatBallistics.Supported(candidate.Item))
            return Vector2.Distance(CombatBallistics.Muzzle(player),enemy.Center)<=CombatBallistics.Range(candidate.Item)-(retaining?0:8)
                && CombatBallistics.ReadRound(player,candidate.Item,out var round)==null
                && CombatBallistics.Aim(player,enemy,candidate.Item,round,out _);
        // Keep the current stance through a one-tile band around melee reach.
        int margin=retaining?8:-8;
        return Math.Abs(player.Center.X-enemy.Center.X)<=(player.width+enemy.width)/2+SwingGap+margin
            && Math.Abs(player.Center.Y-enemy.Center.Y)<=(player.height+enemy.height)/2+10 && Visible(player,enemy);
    }

    private static Candidate Choose(Identity frame,NPC enemy,int currentSlot=-1,bool handoff=false)
    {
        var p=(Player)frame.Actor;
        if(!Configured(frame))
        {
            var held=p.HeldItem;
            return new(p.selectedItem,held,p.selectedItem is <0 or >9 || !WeaponSupported(held)
                ?"combat_weapon_unsupported":ShotRefusal(p,held));
        }
        if(primary==null && secondary==null)return new(-1,null,"combat_attack_weapon_unconfigured");
        if(primary==null)return Inspect(p,secondary,"secondary",handoff?frame:null);
        var first=Inspect(p,primary,"primary",handoff?frame:null);
        if(secondary==null)return first;
        var second=Inspect(p,secondary,"secondary",handoff?frame:null);
        if(second.Error!=null)return first;
        if(first.Error!=null)return second;
        bool primaryRanged=CombatBallistics.Supported(first.Item),secondaryRanged=CombatBallistics.Supported(second.Item);
        if(primaryRanged!=secondaryRanged)
        {
            var melee=primaryRanged?second:first;
            var shot=primaryRanged?first:second;
            if(Effective(p,enemy,melee,currentSlot==melee.Slot))return melee;
            if(Effective(p,enemy,shot,currentSlot==shot.Slot))return shot;
            return first;
        }
        if(Effective(p,enemy,first,currentSlot==first.Slot))return first;
        return Effective(p,enemy,second,currentSlot==second.Slot)?second:first;
    }

    private static bool MiningSwing(Identity frame) => Configured(frame)
        && ActorMining.Running && ActorMining.Current is {} mining && ReferenceEquals(mining.Actor,frame.Actor)
        && mining.World==frame.World && mining.Generation==frame.Generation;

    private static string ShotRefusal(Player p,Item held)
    {
        int root=SummonRoot(held);
        if (root != 0)
            return FindSummon(p,root) != null || CombatBallistics.ManaAffordable(p,held) ? null : "combat_mana_unavailable";
        return CombatBallistics.Supported(held) ? CombatBallistics.ReadRound(p,held,out _) : null;
    }

    private static bool InRange(Player p, NPC n, int range) =>
        Vector2.DistanceSquared(p.Center, n.Center) <= range * range;

    private static bool Visible(Player p, NPC n) =>
        Collision.CanHitLine(p.position, p.width, p.height, n.position, n.width, n.height);

    internal BossMembers Members {get;private set;}
    internal long VerifiedDamage {get;private set;}
    internal int VerifiedHits {get;private set;}
    private readonly Queue<object> impacts=new();
    private bool bossWaiting;
    internal object[] RecentImpacts=>impacts.ToArray();
    internal static void ObserveDeath(NPC n)
    {
        if(current is {} combat && combat.Members!=null && combat.Owned())combat.Members.Death(n);
    }
    private CombatHit ConfirmDamage(NPC struck,NPC carrier,int before,int damage)
    {
        if(Members!=null && (!Members.Contains(struck) || !Members.Contains(carrier)
            || Math.Min(before,damage)!=before-Math.Max(0,carrier.life)))return null;
        return CombatStrikeEvidence.Observe(true,before,carrier.life,damage,carrier.active,Main.GameUpdateCount);
    }
    private void Credit(NPC struck,NPC carrier,int memberBefore,CombatTarget aim,CombatHit hit,int epoch)
    {
        if(Members==null)return;
        int amount=Math.Min(hit.DamageDone,hit.LifeBefore-Math.Max(0,hit.LifeAfter));
        VerifiedDamage+=amount;
        VerifiedHits++;
        ServerCombatFixture.ArenaHit(amount);
        if(impacts.Count==4)impacts.Dequeue();
        impacts.Enqueue(new{sequence=VerifiedHits,aim,member=Members.Ref(struck),carrier=Members.Ref(carrier),
            currentTarget=Target,heldType=item.type,epoch,amount,memberBefore,memberAfter=struck.life,hit});
    }
    internal string UpdateMembers(out bool waiting)
    {
        waiting=false;
        string error=Members.Refresh();
        if(error!=null || Members.Victory.HasValue)return error;
        NPC next=Members.Select(player,npc);
        waiting=bossWaiting=next==null || !InRange(player,next,AcquireRange);
        if(!waiting && SameSpawn(next) || waiting && approach?.Evasion!=null && approach.Status=="running")return null;
        if(approach?.Status=="running")navigation.Stop("cancelled","boss_member_changed");
        if(approach?.Status=="unknown")return "combat_native_unconfirmed";
        approach=null;
        if(!waiting)
        {
            drainSwing=player.itemAnimation>0;
            BindTarget(next,Reference(identity,next));
        }
        return null;
    }
    private bool IsBossEncounter=>bossEncounter;

    private static NPC Resolve(Identity identity, string entityRef,bool boss=false)
    {
        var p = (Player)identity.Actor;
        for (int i = 0; i < Main.maxNPCs; i++)
        {
            var candidate = Main.npc[i];
            if ((boss?candidate.active && BossMembers.Supported(candidate.type) && candidate.life>0:Enemy(candidate, p)) && InRange(p, candidate, AcquireRange)
                && entityRef == Reference(identity, candidate)) return candidate;
        }
        return null;
    }

    internal static string Preflight(Identity identity, string entityRef)
    {
        if (!ServerAiNativeActions.Owned(identity)) return "actor_binding_changed";
        var p = (Player)identity.Actor;
        var enemy=Resolve(identity,entityRef);
        if (enemy == null) return "combat_target_unavailable";
        return !GameNavigation.Ordinary(p,MiningSwing(identity)) || p.noItems || p.CCed ? "combat_actor_state_unsupported" : Choose(identity,enemy,handoff:true).Error;
    }

    internal static void RecordHurt(Player p, int sourceNpc)
    {
        if (sourceNpc < 0 || sourceNpc >= Main.maxNPCs || !Enemy(Main.npc[sourceNpc], p)) return;
        var world = ProbeSession.WorldSession;
        var generation = ProbeSession.ActorGeneration;
        recentHurt = (p, world, generation, Reference(world, generation, Main.npc[sourceNpc]), Main.GameUpdateCount);
    }

    private static string ThreatReason(Identity frame,Player p,NPC n,string reference)
    {
        if(!Enemy(n,p))return null;
        var hurt=recentHurt;
        bool recent=ReferenceEquals(hurt.actor,p)&&hurt.world==frame.World&&hurt.generation==frame.Generation
            &&frame.GameUpdate>=hurt.tick&&frame.GameUpdate-hurt.tick<=45;
        if(recent&&reference==hurt.entityRef&&InRange(p,n,192)&&Visible(p,n))return "recent_native_npc_damage";
        return n.HasValidTarget&&n.target==p.whoAmI&&InRange(p,n,ThreatRange)&&Visible(p,n)
            ? "npc_targeting_actor_in_range" : null;
    }

    internal static (string entityRef, string threat)? Threat(Identity identity,Func<string,bool> allowed=null,bool combatSwing=false)
    {
        if (identity?.CanAct != true || !ServerAiNativeActions.Owned(identity)) return null;
        var p = (Player)identity.Actor;
        if (!GameNavigation.Ordinary(p,combatSwing || MiningSwing(identity)) || p.noItems || p.CCed) return null;
        (string entityRef,string threat)? nearest=null;
        float best = float.PositiveInfinity;
        for (int i = 0; i < Main.maxNPCs; i++)
        {
            var n = Main.npc[i];
            if (!Enemy(n,p)) continue;
            string reference=Reference(identity,n);
            if(allowed?.Invoke(reference)==false)continue;
            var reason=ThreatReason(identity,p,n,reference);
            if(reason==null)continue;
            if(!combatSwing && Choose(identity,n,handoff:true).Error is {} refusal)
            {
                if(MatchesLoadout(identity))lastRefusal=new(refusal,identity.GameUpdate,reference);
                return null;
            }
            if(reason=="recent_native_npc_damage")return (reference,reason);
            float distance=Vector2.DistanceSquared(p.Center,n.Center);
            if(distance<best){best=distance;nearest=(reference,reason);}
        }
        return nearest;
    }

    private void BindTarget(NPC next,string reference)
    {
        npc=next;
        spawn=next.GetGlobalNPC<AtomicNpcIdentity>();
        spawnToken=spawn.Token;
        Target=new(next.whoAmI,next.type,reference);
        if(!bossEncounter)lastMelee=lastSummon=LastHit=null;
        pendingSlot=-1;targetEpoch++;attemptedFiringPositions.Clear();
        missedSwings=0;
    }

    public string Prepare()
    {
        ServerAiNativeActions.Require(identity);
        npc = Resolve(identity, entityRef,bossEncounter);
        if(bossEncounter && !Configured(identity))return "boss_loadout_required";
        if (npc == null) return bossEncounter?"boss_root_unavailable_or_unsupported":"combat_target_unavailable";
        if(reflexAllowed!=null&&ThreatReason(identity with{GameUpdate=Main.GameUpdateCount},player,npc,entityRef)==null)
            return "combat_target_unavailable";
        if(bossEncounter)
        {
            Members=new(identity,npc);
            if(Members.Start() is {} error)return error;
            npc=Members.Select(player,npc)??npc;
        }
        BindTarget(npc,Reference(identity,npc));
        if(admission?.SameWeapon?.Invoke()==false)return "combat_weapon_changed";
        var choice=Choose(identity,npc);
        if(choice.Error!=null)return choice.Error;
        slot=choice.Slot;
        item=choice.Item;
        if (!GameNavigation.Ordinary(player) || player.noItems || player.CCed) return "combat_actor_state_unsupported";
        if (!ServerAiNativeActions.BodyIdle(identity)) return "actor_busy";

        Weapon = new(slot, item.type, item.Name);
        return null;
    }

    public void Acquire()
    {
        if (current != null) throw new InvalidOperationException("Combat input already owned.");
        if(!Owned() || !ServerAiNativeActions.BodyIdle(identity) || !ReferenceEquals(player.inventory[slot],item))
            throw new InvalidOperationException("Combat weapon handoff changed.");
        int previousSlot=player.selectedItem;
        if(player.selectedItem!=slot)ActorItemUse.Select(player,identity.World,identity.Generation,slot);
        if(!ValidWeapon())throw new InvalidOperationException("Combat weapon selection unconfirmed.");
        RecordSelection(previousSlot);
        current = this;
        if(bossEncounter)ServerCombatFixture.ArenaAcquired(player);
    }

    private void RecordSelection(int previousSlot)
    {
        if(!Configured(identity))return;
        string role=primary?.Key==HotbarSlots.KeyForInventorySlot(slot)?"primary":"secondary";
        Weapon=Weapon with {Selection=new(role,version,previousSlot,Main.GameUpdateCount,
            admission?.Update??Main.GameUpdateCount,admission?.InterruptedAction)};
    }

    private string ReassessWeapon(out bool waiting)
    {
        waiting=waitingSwitch=false;
        if(!MatchesLoadout(identity) || secondary==null || primary==null)return null;
        if(admission?.SameWeapon?.Invoke()==false)return "combat_weapon_changed";
        var choice=Choose(identity,npc,slot);
        if(choice.Error!=null || choice.Slot==slot)
        {
            pendingSlot=-1;
            return null; // Current weapon handles resource exhaustion and pending shots.
        }
        if(pendingSlot!=choice.Slot)
        {
            pendingSlot=choice.Slot;
            pendingSince=Main.GameUpdateCount;
        }
        // Geometry can alternate between adjacent ticks at the last clear ballistic arc.
        // Resource loss is definitive and only waits for the native item cycle.
        if(Main.GameUpdateCount-pendingSince<3 && ShotRefusal(player,item)==null)
        {
            waiting=waitingSwitch=true;
            return null;
        }
        if(player.itemAnimation!=0 || player.itemTime!=0 || player.reuseDelay!=0 || player.channel)
        {
            waiting=waitingSwitch=true;
            return null;
        }
        if(approach?.Status=="running")navigation.Stop("cancelled","combat_weapon_switch");
        if(approach?.Status=="unknown")return "combat_native_unconfirmed";
        approach=null;
        int previous=slot;
        ActorItemUse.Select(player,identity.World,identity.Generation,choice.Slot);
        slot=choice.Slot;
        item=choice.Item;
        Weapon=new(slot,item.type,item.Name);
        if(!ValidWeapon())return "combat_native_unconfirmed";
        RecordSelection(previous);
        pendingSlot=-1;
        missedSwings=0;
        attemptedFiringPositions.Clear();
        return null;
    }

    private bool SameSpawn(NPC n) => Target != null && ReferenceEquals(npc, n) && n.type == Target.TypeId
        && ReferenceEquals(n.GetGlobalNPC<AtomicNpcIdentity>(),spawn) && spawn.Token==spawnToken;

    public CombatObservation Observe()
    {
        if (fault != null) throw new InvalidOperationException("Combat ItemCheck: " + fault.Message, fault);
        var n = Main.npc[Target.Slot];
        return SameSpawn(n) ? new(true, n.active, StillEligible(n), n.life) : new(false, false, false, 0);
    }

    private bool StillEligible(NPC n)=>bossEncounter?Members.Attackable(n):reflexAllowed==null?Enemy(n,player):
        ThreatReason(identity with{GameUpdate=Main.GameUpdateCount},player,n,Target.EntityRef)!=null;

    public string Retarget(Identity frame,IReadOnlySet<string> skipped)
    {
        ServerAiNativeActions.Require(frame);
        if(!ValidWeapon())return "combat_weapon_changed";
        if(!GameNavigation.Ordinary(player,combatSwing:true)||player.noItems||player.CCed)
            return "combat_actor_state_unsupported";
        if(approach?.Status=="unknown"||approach?.Reason=="native_navigation_exception")
            return "combat_native_unconfirmed";
        bool Allowed(string reference)=>reflexAllowed(frame,reference);
        var candidate=Threat(frame,r=>!skipped.Contains(r)&&Allowed(r),combatSwing:true);
        if(candidate==null)
            return skipped.Count>0&&Threat(frame,Allowed,combatSwing:true)!=null
                ? "combat_threat_unreachable" : "combat_threats_cleared";
        var next=Resolve(frame,candidate.Value.entityRef);
        if(next==null)return "combat_native_unconfirmed";
        if(approach?.Status=="running")navigation.Stop("cancelled","combat_retarget");
        if(approach?.Status=="unknown")return "combat_native_unconfirmed";
        approach=null;
        // Drain the existing native swing without using it against the new NPC.
        // A new press is still gated by the original animation and item timers.
        drainSwing=player.itemAnimation>0;
        BindTarget(next,candidate.Value.entityRef);
        return null;
    }

    internal readonly BossEvasion Evasion=bossEncounter?new():null;
    internal bool Evade()
    {
        if(Evasion==null || ServerCombatFixture.Baseline)return false;
        if(bossWaiting && !GameNavigation.Ordinary(player,combatSwing:true))
        {
            if(approach?.Status=="running")navigation.Stop("cancelled","boss_movement_unsupported");
            return false;
        }
        int toward=npc.Center.X<player.Center.X?-1:1;
        Evasion.PreferredDirection=Ranged?-toward:toward;
        bool needed=Evasion.Needed(player,npc,Ranged && !bossWaiting);
        if(approach?.Evasion!=null && !needed)
        {
            if(approach.Status=="running")navigation.Stop("cancelled","boss_evasion_complete");
            if(approach.Status!="unknown")approach=null;
        }
        if(!needed)return false;
        if(approach?.Evasion==null || approach.Status!="running")
        {
            if(approach?.Status=="running")navigation.Stop("cancelled","boss_evade");
            if(approach?.Status=="unknown" || approach?.Reason=="native_navigation_exception")return false;
            approach=navigation.StartCombat(identity,player.position.X,actionId);
            approach.Evasion=Evasion;
        }
        return true;
    }

    public string Step(out string phase)
    {
        phase = "reassess";
        if (!ValidWeapon()) return "combat_weapon_changed";
        if (!GameNavigation.Ordinary(player,combatSwing:true) || player.noItems || player.CCed) return "combat_actor_state_unsupported";
        if (!InRange(player, npc, AcquireRange)) return "combat_target_out_of_range";
        if (approach?.Status == "unknown" || approach?.Reason == "native_navigation_exception")
        {
            if (approach.ReasonDetail != null) throw new InvalidOperationException(approach.ReasonDetail);
            return "combat_native_unconfirmed";
        }
        if(ReassessWeapon(out var waiting) is {} failure)return failure;
        if(Evade()){phase=Evasion.Reason??"evade";return null;}
        if(waiting){phase="switch_weapon";return null;}
        if (Summoning)
        {
            int rootType=SummonRoot(item);
            if (rootType==ProjectileID.BabySlime) observedBuff=player.HasBuff(BuffID.BabySlime);
            if (FindSummon(player,rootType) is {} root)
            {
                observedRootIdentity=root.Identity;
                phase="summon_active";
                return null;
            }
            phase="summon_cast";
            if (summonAttempted && player.itemAnimation == 0) return "combat_native_unconfirmed";
            return summonAttempted ? null : ShotRefusal(player,item);
        }
        if(Ranged)return StepRanged(out phase);
        if (approach?.Status is "failed" or "timeout") return "combat_approach_failed";
        bool swingRange=CanSwing();
        float horizontal=Math.Abs(player.Center.X-npc.Center.X);
        // A jumping enemy can leave the swing envelope after the planner
        // settles. Reassess on subsequent ticks while it is airborne.
        if (!swingRange && Math.Abs(npc.velocity.Y)>0.1f
            && horizontal <= (player.width+npc.width)/2f+SwingGap)
            return null;
        if (approach?.Status == "completed" && !swingRange
            && Math.Abs(npc.Center.X - approach.Target - player.width / 2f) <= (player.width + npc.width) / 2 + 4)
            return null; // Wait for the target to move into reach or the task deadline.
        float halfWidth=(player.width+npc.width)/2f;
        if (swingRange && approach?.Status != "running" && horizontal <= halfWidth + 12 && missedSwings < 2)
        {
            phase = "attack";
            return null;
        }
        phase = swingRange ? "attack" : "approach";
        if (approach?.Status != "running" && !ServerAiNavigation.Observe(player).Settled)
            return null;
        int side=player.Center.X<npc.Center.X?-1:1;
        // First settle in weapon reach, outside contact. If the native arc
        // misses twice, close the remaining gap on the next approach.
        float standOff=Math.Max(0,halfWidth+(missedSwings<2?0:-8));
        float targetX = Math.Clamp(npc.Center.X + side*standOff - player.width / 2f, 32, Main.maxTilesX * 16 - 64);
        if (approach?.Status == "completed" && Math.Abs(targetX - approach.Target) < 1)
        {
            // The regular horizontal arrival window may leave the actor at the
            // fringe of the arc. Move the goal once beyond that window.
            if (missedSwings < 2) return null;
            targetX = Math.Clamp(targetX-side*12,32,Main.maxTilesX*16-64);
        }
        if (approach?.Status != "running" || Main.GameUpdateCount - plannedAt >= 24 && Math.Abs(targetX - approach.Target) >= 48
            && ServerAiNavigation.Observe(player).Settled)
        {
            if (approach?.Status == "running") navigation.Stop("cancelled", "combat_replan");
            if (approach?.Status == "unknown") return "combat_native_unconfirmed";
            approach = navigation.StartCombat(identity, targetX, actionId);
            plannedAt = Main.GameUpdateCount;
        }
        return null;
    }

    private string StepRanged(out string phase)
    {
        phase="aim";
        if(approach?.Status=="timeout")return "combat_firing_navigation_timeout";
        var failure=CombatBallistics.ReadRound(player,item,out var round);
        if(failure!=null)
        {
            if(failure=="combat_mana_unavailable")
            {
                phase="waiting_mana";
                return Array.Exists(Main.projectile,p=>p.active
                    &&p.TryGetGlobalProjectile<CombatProjectile>(out var trace)&&trace.Flight is {} shot&&shot.Pending(this,p)
                    &&Main.GameUpdateCount-shot.Launched<=90)?null:"combat_mana_unavailable";
            }
            if(failure=="combat_ammo_unavailable"&&Array.Exists(Main.projectile,p=>p.active
                &&p.TryGetGlobalProjectile<CombatProjectile>(out var trace)&&trace.Flight is {} shot&&shot.Pending(this,p)&&Main.GameUpdateCount-shot.Launched<=90))
            {phase="projectile_in_flight";return null;}
            return failure;
        }
        if(CombatBallistics.Aim(player,npc,item,round,out _))
        {
            if(approach?.Status=="running"&&ServerAiNavigation.Observe(player).Settled)
                navigation.Stop("cancelled","combat_firing_position");
            if(approach?.Status=="unknown")return "combat_native_unconfirmed";
            phase="attack";return null;
        }
        phase="approach";
        if(approach?.Status=="failed"&&AiActionReasons.NavigationRecoveryKind(approach.Snapshot?.Reason)!="no_route")
            return "combat_approach_failed";
        if(approach?.Status=="running")return null;
        var observed=ServerAiNavigation.Observe(player);
        if(!observed.Settled)return null;
        var (goal,left,failureToFind)=FindFiringPosition(observed.State,round);
        if(goal==null)return failureToFind;
        if(attemptedFiringPositions.Count>=MaxFiringPositions)return "combat_firing_navigation_timeout";
        approach=navigation.StartCombat(identity,goal,actionId);
        attemptedFiringPositions.Add(new(goal,left,approach));
        return null;
    }

    private static bool ConfirmedNoRoute(FiringAttempt attempt,PhysicsState start)
    {
        var run=attempt.Run;
        // A route failure from a different pose cannot prove that the
        // position is still unreachable after moving to another candidate.
        return run.Status=="failed"&&AiActionReasons.NavigationRecoveryKind(run.Snapshot?.Reason)=="no_route"
            &&run.FinalObservation is {Settled:true} final
            &&Math.Abs(final.State.Px-start.Px)<=1&&Math.Abs(final.State.Py-start.Py)<=.5f;
    }

    private (LandingRegion Goal,bool Left,string Failure) FindFiringPosition(PhysicsState start,CombatBallistics.Round round)
    {
        var muzzleOffset=CombatBallistics.Muzzle(player)-player.position;
        float targetX=npc.Center.X;
        int npcX=(int)(targetX/16),npcY=(int)(npc.Bottom.Y/16);
        int radius=(int)(CombatBallistics.Range(item)/16);
        int footRow=(int)((start.Py+player.height)/16);
        int minY=Math.Max(3,Math.Max(Math.Min(npcY,footRow)-8,npcY-radius));
        int maxY=Math.Min(Main.maxTilesY-4,Math.Min(Math.Max(npcY,footRow)+8,npcY+radius));
        LandingRegion bestLeft=null,bestRight=null;
        float leftScore=float.PositiveInfinity,rightScore=float.PositiveInfinity;
        bool anyValid=false,allNoRoute=true;
        for(int x=Math.Max(3,npcX-radius);x<=Math.Min(Main.maxTilesX-4,npcX+radius);x++)
        for(int y=minY;y<=maxY;y++)
        {
            var support=Main.tile[x,y];
            if(!support.HasUnactuatedTile||(!Main.tileSolid[support.TileType]&&!Main.tileSolidTop[support.TileType]))continue;
            for(int half=0;half<=(support.IsHalfBlock||support.Slope!=SlopeType.Solid?1:0);half++)
            for(int offset=0;offset<=8;offset+=8)
            {
                float feet=y*16+half*8, center=x*16+offset+4, px=center-player.width*.5f;
                var pose=new PhysicsState(px,feet-player.height,0,0,true,0,true);
                if(Math.Abs(px-start.Px)<8&&Math.Abs(pose.Py-start.Py)<1)continue;
                bool left=center<targetX;
                var attempt=attemptedFiringPositions.Find(a=>a.Goal.MinPlayerLeftX==px-2&&a.Goal.SurfaceY==feet);
                float score=Math.Abs(px-start.Px)+1.5f*Math.Abs(pose.Py-start.Py)+Math.Max(0,48-Math.Abs(center-targetX));
                if(attempt==null&&score>=(left?leftScore:rightScore))continue;
                if(!new NativeMotionWorld(pose).SafeLanding(pose)
                    ||!CombatBallistics.Aim(new Vector2(pose.Px,pose.Py)+muzzleOffset,npc,item,round,out _))continue;
                anyValid=true;
                if(attempt!=null)
                {
                    allNoRoute&=ConfirmedNoRoute(attempt,start);
                    continue;
                }
                var region=new LandingRegion(px-2,px+2,feet);
                if(left){leftScore=score;bestLeft=region;}
                else{rightScore=score;bestRight=region;}
            }
        }
        if(bestLeft==null&&bestRight==null)
            return (null,false,!anyValid?"combat_shot_blocked"
                :allNoRoute?"combat_firing_navigation_no_route":"combat_firing_navigation_timeout");
        int leftAttempts=attemptedFiringPositions.FindAll(a=>a.Left).Count;
        int rightAttempts=attemptedFiringPositions.Count-leftAttempts;
        // Alternate sides when both have candidates. A cheaper cluster
        // on one side cannot consume the other side's opportunity.
        bool chooseLeft=bestRight==null||bestLeft!=null
            &&(leftAttempts<rightAttempts||leftAttempts==rightAttempts&&player.Center.X<targetX);
        return chooseLeft?(bestLeft,true,null):(bestRight,false,null);
    }

    private bool ValidWeapon() => player.selectedItem == slot && ReferenceEquals(player.HeldItem, item)
        && item.type == Weapon.TypeId && WeaponSupported(item);

    private bool CanSwing() => Math.Abs(player.Center.X - npc.Center.X) <= (player.width + npc.width) / 2 + SwingGap
        && Math.Abs(player.Center.Y - npc.Center.Y) <= (player.height + npc.height) / 2 + 10 && Visible(player, npc);

    internal static void Moved(Player p, PhysicsInput input)
    {
        if (current is not {} combat || !ReferenceEquals(combat.player,p))return;
        ServerCombatFixture.ArenaInput(input,combat.npc);
        if (input.Direction == 0 && !input.Jump && !input.Down && Math.Abs(p.velocity.X)<=0.1f) return;
        combat.inputs.Movement(Main.GameUpdateCount);
    }

    internal static bool UpdateItem(Player p, Action<NativeItemInput.Intent> runNative)
    {
        var combat = current;
        if (combat == null || !ReferenceEquals(combat.player, p)) return false;
        combat.CheckItem(runNative);
        return true;
    }

    // QuickMana can consume from either the inventory or an open Void Bag.
    private readonly List<(Item[] items, int index, int networkSlot, int stack, int type)> manaItems = new();

    private void TrackManaItems(Item[] items, int firstSlot)
    {
        for (int i = 0; i < items.Length; i++)
            if (items[i].healMana > 0 && items[i].stack > 0)
                manaItems.Add((items, i, firstSlot + i, items[i].stack, items[i].type));
    }

    private void CheckItem(Action<NativeItemInput.Intent> runNative)
    {
        ulong tick = Main.GameUpdateCount;
        if (checking != null || fault != null || !ServerAiNativeActions.Owned(identity)
            || !NativeSurvival.Owns(player) || player.dead || player.ghost || player.noItems || player.CCed
            || !ItemContextValid() || !ValidWeapon()
            || !inputs.BeginItemCheck(tick)) return;
        if(player.itemAnimation==0)drainSwing=false;
        try
        {
            checking = this;
            Vector2 aim=maintenance?player.Center:npc.Center;
            bool ready=player.itemTime==0&&player.reuseDelay==0;
            bool press;
            if(Ranged)
            {
                firing=null;ammoChanged=false;
                bool canPress=!bossWaiting&&player.itemTime<=1&&player.reuseDelay==0
                    &&(player.itemAnimation>0||player.releaseUseItem||item.autoReuse);
                if (Summoning)
                {
                    int root=SummonRoot(item);
                    if (canPress && (!maintenance || !maintenanceYield && SafeToCast(player) && SummonRefusal(player,item)==null) && !drainSwing && !summonAttempted && FindSummon(player,root)==null
                        && CombatBallistics.ManaAffordable(player,item))
                    {
                        aim = maintenance || root == ProjectileID.FrostHydra ? player.Center : npc.Center;
                        firing = new(root,AmmoID.None,0);
                        summonAttempted=true;
                    }
                }
                else if(!waitingSwitch&&!drainSwing&&canPress&&CombatBallistics.ReadRound(player,item,out var round)==null
                    &&CombatBallistics.Aim(player,npc,item,round,out aim))firing=round;
                press=firing.HasValue;
            }
            else press=!bossWaiting&&!waitingSwitch&&player.itemAnimation==0&&ready&&player.releaseUseItem&&CanSwing();
            ServerCombatFixture.EvasionPress(press);
            var previousHit=LastHit;
            if (press || player.itemAnimation > 0)
            {
                if(!maintenance)player.direction = npc.Center.X < player.Center.X ? -1 : 1;
                inputs.Swing(tick);
            }
            // The native local-input path owns the release latch. The AI
            // supplies its equivalent on the up frame, never on a down frame.
            if (!press) player.releaseUseItem = true;
            // Mana moves inside the complete native ItemCheck: ItemCheck_PayMana
            // (including QuickMana potions) runs before ItemCheck_Shoot. Observe
            // the whole frame instead of the shooting sub-step.
            int manaBefore=player.statMana;
            manaItems.Clear();
            if (press && (Summoning || ShotKind == CombatBallistics.Kind.Magic) && player.manaFlower)
            {
                TrackManaItems(player.inventory, PlayerItemSlotID.Inventory0);
                if (player.useVoidBag()) TrackManaItems(player.bank4.item, PlayerItemSlotID.Bank4_0);
            }
            var movement=(player.controlLeft,player.controlRight,player.controlJump,player.controlDown);
            runNative(new(aim, press));
            ServerCombatFixture.ItemMovementConflict(movement!=(player.controlLeft,player.controlRight,player.controlJump,player.controlDown));
            bool manaChanged=player.statMana!=manaBefore;
            if (Owned())
            {
                if (manaChanged) NetMessage.SendData(MessageID.PlayerMana, -1, -1, null, player.whoAmI);
                foreach (var slot in manaItems)
                {
                    var after = slot.items[slot.index];
                    if (after.stack != slot.stack || after.type != slot.type)
                        NetMessage.SendData(MessageID.SyncEquipment, -1, -1, null, player.whoAmI, slot.networkSlot);
                }
                if (ammoChanged) NetworkStateSync.Full(player);
            }
            if (press && player.itemAnimation > 0 && Owned())
            {
                player.releaseUseItem=false;
                if (!Ranged&&ReferenceEquals(LastHit,previousHit)) missedSwings++;
                NetMessage.SendData(MessageID.PlayerControls, -1, -1, null, player.whoAmI);
                NetMessage.SendData(MessageID.ShotAnimationAndSound, -1, -1, null, player.whoAmI);
            }
        }
        catch(Exception e)
        {
            fault = e;
            if (Owned()) player.releaseUseItem = true;
        }
        finally
        {
            firing=null;ammoChanged=false;checking = null;
            if (Owned()) player.controlUseItem = false;
        }
    }

    public static bool AllowsItem(Player p) => checking is {} c && ReferenceEquals(c.player, p)
        && NativeSurvival.Owns(p) && c.Owned();

    // Dedicated ItemCheck passes an empty texture frame to use-style. The swing
    // pose needs native item dimensions, while the server still computes its
    // own native melee hitbox.
    internal static Rectangle SwingFrame(Player p, Item held, Rectangle frame) =>
        AllowsItem(p) && !checking.Ranged ? new Rectangle(0, 0, held.width, held.height) : frame;

    private static bool InInputScope(Player p) => checking is {} c && ReferenceEquals(c.player,p) && NativeItemInput.Active(p);

    internal static bool? CanHit(Player p, Item held, NPC target) => InInputScope(p)
        && (checking.Ranged || checking.drainSwing || !AllowsItem(p) || !ReferenceEquals(held,checking.item) || !checking.ValidWeapon()
            || !checking.SameSpawn(target) || !checking.SameSpawn(Main.npc[checking.Target.Slot]))
        ? false : null;

    // This is the single native strike call in ProcessHitAgainstNPC, after
    // hit modifiers and before tML's post-hit callbacks.
    public static int StrikeWithEvidence(NPC target,NPC.HitInfo hit,bool fromNet,bool noPlayerInteraction,Player p)
    {
        var combat=checking;
        if(!InInputScope(p))return target.StrikeNPC(hit,fromNet,noPlayerInteraction);
        if(combat.Ranged||combat.drainSwing||!AllowsItem(p)||!combat.ValidWeapon()||!combat.SameSpawn(Main.npc[combat.Target.Slot])||!combat.SameSpawn(target)||!(combat.IsBossEncounter?combat.Members.Attackable(target):Enemy(target,p)))return 0;
        NPC carrier=combat.Members?.Carrier(target)??target;
        int before=carrier.life,memberBefore=target.life;
        int damage=target.StrikeNPC(hit,fromNet,noPlayerInteraction);
        if(combat.Owned() && combat.SameSpawn(Main.npc[combat.Target.Slot])
            && combat.ConfirmDamage(target,carrier,before,damage) is {} receipt)
        {
            combat.lastMelee=combat.LastHit=receipt with {MeleeWeaponType=combat.Weapon.TypeId};
            combat.Credit(target,carrier,memberBefore,combat.Target,combat.LastHit,combat.targetEpoch);
            combat.missedSwings=0;
        }
        return damage;
    }

    internal static void Shoot(On_Player.orig_ItemCheck_Shoot orig,Player p,int slot,Item held,int damage)
    {
        var combat=checking;
        if(!InInputScope(p)||!AllowsItem(p)||combat.inShoot||combat.firing is not {} round||p.whoAmI!=slot
            ||!ReferenceEquals(held,combat.item)||!combat.ValidWeapon()||!combat.ItemContextValid())return;
        bool magic=CombatBallistics.Family(held)==CombatBallistics.Kind.Magic;
        Item ammo=null;int stack=0;
        // ItemCheck_PayMana -> CheckMana already paid (or financed through
        // QuickMana) before this shoot step. Re-testing the remaining mana
        // would drop a cast the native rules just authorised.
        if(combat.Summoning) combat.summonShootObserved=true;
        if(magic || combat.Summoning)combat.ManaAtLaunch=p.statMana;
        if(!magic && !combat.Summoning)
        {
            ammo=p.ChooseAmmo(held);
            if(ammo==null||ammo.type!=round.Ammo)return;
            stack=ammo.stack;
        }
        try{combat.inShoot=true;orig(p,slot,held,damage);}
        finally
        {
            combat.inShoot=false;
            if(!magic && !combat.Summoning)combat.ammoChanged|=ammo.stack!=stack;
        }
    }

    internal static Shot CaptureShot(Projectile p,EntitySource_ItemUse source)
    {
        var combat=checking;
        if(combat==null||!combat.inShoot||!InInputScope(combat.player)||!AllowsItem(combat.player)||combat.firing is not {} round
            ||p.owner!=combat.player.whoAmI||!ReferenceEquals(source.Entity,combat.player)
            ||!ReferenceEquals(source.Item,combat.item))return null;
        int ammo=source is EntitySource_ItemUse_WithAmmo withAmmo?withAmmo.AmmoItemIdUsed:AmmoID.None;
        bool magic=CombatBallistics.Family(combat.item)==CombatBallistics.Kind.Magic;
        bool verified=p.type==round.Projectile&&ammo==round.Ammo&&combat.ItemContextValid();
        if (verified && combat.Summoning) combat.observedRootIdentity=p.identity;
        return new(combat.ActorBinding,combat,combat.targetEpoch,combat.Target,combat.Weapon.TypeId,ammo,magic,
            p.whoAmI,p.owner,p.identity,p.type,Main.GameUpdateCount,verified){Allowed=combat.Members?.Permissions()};
    }

    internal sealed record Shot(AiReadOnlyProtocol.Identity Binding,NativeCombatWorld Combat,int Epoch,CombatTarget Target,int Weapon,int Ammo,bool Magic,
        int Slot,int Owner,int Identity,int Type,ulong Launched,bool Verified,int? ParentIdentity=null)
    {
        internal IReadOnlyDictionary<int,Guid> Allowed {get;init;}
        internal NPC Carrier(NPC n)=>Combat?.Members?.Carrier(n)??n;
        internal bool PersistentRoot => (Weapon,Type) is (ItemID.SlimeStaff,ProjectileID.BabySlime)
            or (ItemID.StaffoftheFrostHydra,ProjectileID.FrostHydra);
        internal bool SummonAttack => PersistentRoot || ParentIdentity.HasValue;
        // A root can collect evidence for the current task. A child takes a
        // snapshot of that assignment at birth, including the absence of one.
        internal Shot ForCurrentTarget()
        {
            var task=current;
            if (!PersistentRoot || task?.Target == null || task.fault != null || !Combat.Owned()
                || !ReferenceEquals(task.player,Combat.player) || !task.ValidWeapon()
                || !(task.item.type==Weapon || MatchesLoadout(task.ActorBinding) && summonRole?.Type==Weapon) || !task.Owned()) return null;
            if (!task.SameSpawn(Main.npc[task.Target.Slot]) || !task.StillEligible(task.npc)) return null;
            return ReferenceEquals(task,Combat) && Epoch==task.targetEpoch && task.Members==null ? this
                : this with { Combat=task, Epoch=task.targetEpoch, Target=task.Target, Allowed=task.Members?.Permissions() };
        }

        internal Shot Child(Projectile parent, Projectile child, Shot assignment, bool parentConfirmed)
        {
            if (!Verified || Type!=ProjectileID.FrostHydra || !PersistentRoot || !Owns(parent)
                || child.owner!=Owner || child.type!=ProjectileID.FrostBlastFriendly) return null;
            if (assignment != null)
            {
                if (parentConfirmed) assignment.Combat.verifiedChildren++;
                else assignment.Combat.unconfirmedChildren++;
            }
            return this with { Combat=assignment?.Combat, Target=assignment?.Target, Allowed=assignment?.Allowed, Epoch=assignment?.Epoch ?? 0,
                Slot=child.whoAmI, Identity=child.identity, Type=child.type,
                Launched=Main.GameUpdateCount, ParentIdentity=parentConfirmed ? Identity : null, Verified=parentConfirmed };
        }

        internal bool Owns(Projectile p)=>p.whoAmI==Slot&&p.owner==Owner&&p.identity==Identity&&p.type==Type
            &&ReferenceEquals(Main.projectile[Slot],p)&&ServerAiNativeActions.Owned(Binding)
            &&Binding.Actor is Player {active:true,dead:false,ghost:false};
        internal bool Pending(NativeCombatWorld combat,Projectile p)=>Verified&&Combat!=null&&Combat.fault==null&&ReferenceEquals(combat,Combat)
            &&ReferenceEquals(current,Combat)&&(Allowed!=null || Epoch==Combat.targetEpoch)&&Owns(p)&&Combat.ValidWeapon();
        internal void ObserveFault(Projectile p,Exception exception)
        {if(Pending(Combat,p))Combat.fault=exception;}
        private bool SameTarget(Projectile p,NPC target)=>Pending(Combat,p) && (Allowed!=null
            ? ReferenceEquals(Main.npc[target.whoAmI],target) && Combat.Members.Contains(target)
                && Allowed.TryGetValue(target.whoAmI,out var token) && token==BossMembers.Id(target).Token
            : target.whoAmI==Target.Slot && Combat.SameSpawn(target) && Combat.SameSpawn(Main.npc[Target.Slot]));
        internal bool CanHit(Projectile p,NPC target)=>SameTarget(p,target)&&Combat.StillEligible(target);
        internal bool ObserveHit(Projectile p,NPC target,NPC carrier,int memberBefore,int before,int damage)
        {
            if(!SameTarget(p,target)||carrier!=Carrier(target)||Combat.ConfirmDamage(target,carrier,before,damage) is not {} hit)
                return false;
            Combat.LastHit=PersistentRoot || ParentIdentity.HasValue
                ? hit with { Summon=new(Owner,Type,Identity,ParentIdentity,Launched,Epoch){WeaponType=Weapon} }
                : hit with {Projectile=new(Weapon,Ammo,Type,Owner,Identity,Epoch,Launched)};
            Combat.Credit(target,carrier,memberBefore,Target,Combat.LastHit,Epoch);
            if(SummonAttack)Combat.lastSummon=Combat.LastHit;
            return true;
        }
    }

    public bool Release()
    {
        if (!ReferenceEquals(current, this)) return ServerAiNativeActions.Owned(identity) && ServerAiNativeActions.Released(player);
        try
        {
            try
            {
                if (approach?.Status == "running") navigation.Stop("cancelled", "combat_stopped");
            }
            finally
            {
                if (ServerAiNativeActions.Owned(identity))
                {
                    ActorControl.ReleaseCombatInputs(player);
                    // A terminated action must not carry a partially executed swing into the next owner.
                    player.itemAnimation = player.itemTime = player.reuseDelay = 0;
                }
            }
            if (!ServerAiNativeActions.Owned(identity)) return false;
            if (!player.dead && !player.ghost) NetworkStateSync.Full(player);
            return approach?.Status != "running" && ServerAiNativeActions.Owned(identity) && ServerAiNativeActions.Released(player);
        }
        finally { current = null; }
    }

    public void Abandon()
    {
        if (ReferenceEquals(current, this)) current = null;
    }
}
