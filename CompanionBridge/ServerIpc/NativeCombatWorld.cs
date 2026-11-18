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

internal sealed class NativeCombatWorld(Identity identity, ServerAiNavigation navigation, string actionId, string entityRef, Func<Identity,string,bool> reflexAllowed = null) : ICombatWorld
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

    private static NPC Resolve(Identity identity, string entityRef)
    {
        var p = (Player)identity.Actor;
        for (int i = 0; i < Main.maxNPCs; i++)
        {
            var candidate = Main.npc[i];
            if (Enemy(candidate, p) && InRange(p, candidate, AcquireRange)
                && entityRef == Reference(identity, candidate)) return candidate;
        }
        return null;
    }

    internal static string Preflight(Identity identity, string entityRef)
    {
        if (!ServerAiNativeActions.Owned(identity)) return "actor_binding_changed";
        var p = (Player)identity.Actor;
        if (Resolve(identity, entityRef) == null) return "combat_target_unavailable";
        if (p.selectedItem is < 0 or > 9 || !WeaponSupported(p.HeldItem)) return "combat_weapon_unsupported";
        return !GameNavigation.Ordinary(p) || p.noItems || p.CCed ? "combat_actor_state_unsupported" : ShotRefusal(p,p.HeldItem);
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
        if (p.selectedItem is < 0 or > 9 || !WeaponSupported(p.HeldItem) || !GameNavigation.Ordinary(p,combatSwing)
            || p.noItems || p.CCed || !combatSwing && ShotRefusal(p,p.HeldItem)!=null) return null;
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
        LastHit=null;targetEpoch++;attemptedFiringPositions.Clear();
        missedSwings=0;
    }

    public string Prepare()
    {
        ServerAiNativeActions.Require(identity);
        npc = Resolve(identity, entityRef);
        if (npc == null) return "combat_target_unavailable";
        if(reflexAllowed!=null&&ThreatReason(identity with{GameUpdate=Main.GameUpdateCount},player,npc,entityRef)==null)
            return "combat_target_unavailable";
        BindTarget(npc,entityRef);
        if (player.selectedItem is < 0 or > 9 || !WeaponSupported(player.HeldItem)) return "combat_weapon_unsupported";
        if (!GameNavigation.Ordinary(player) || player.noItems || player.CCed) return "combat_actor_state_unsupported";
        if (!ServerAiNativeActions.BodyIdle(identity)) return "actor_busy";
        if(ShotRefusal(player,player.HeldItem) is {} shotFailure)return shotFailure;
        slot = player.selectedItem;
        item = player.HeldItem;
        Weapon = new(slot, item.type, item.Name);
        return null;
    }

    public void Acquire()
    {
        if (current != null) throw new InvalidOperationException("Combat input already owned.");
        current = this;
    }

    private bool SameSpawn(NPC n) => Target != null && ReferenceEquals(npc, n) && n.type == Target.TypeId
        && ReferenceEquals(n.GetGlobalNPC<AtomicNpcIdentity>(),spawn) && spawn.Token==spawnToken;

    public CombatObservation Observe()
    {
        if (fault != null) throw new InvalidOperationException("Combat ItemCheck: " + fault.Message, fault);
        var n = Main.npc[Target.Slot];
        return SameSpawn(n) ? new(true, n.active, StillEligible(n), n.life) : new(false, false, false, 0);
    }

    private bool StillEligible(NPC n)=>reflexAllowed==null?Enemy(n,player):
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
        if (current is not {} combat || !ReferenceEquals(combat.player, p)
            || input.Direction == 0 && !input.Jump && Math.Abs(p.velocity.X)<=0.1f) return;
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
            || !SameSpawn(Main.npc[Target.Slot]) || !StillEligible(npc) || !ValidWeapon()
            || !inputs.BeginItemCheck(tick)) return;
        if(player.itemAnimation==0)drainSwing=false;
        try
        {
            checking = this;
            Vector2 aim=npc.Center;
            bool ready=player.itemTime==0&&player.reuseDelay==0;
            bool press;
            if(Ranged)
            {
                firing=null;ammoChanged=false;
                bool canPress=player.itemTime<=1&&player.reuseDelay==0
                    &&(player.itemAnimation>0||player.releaseUseItem||item.autoReuse);
                if (Summoning)
                {
                    int root=SummonRoot(item);
                    if (canPress && !drainSwing && !summonAttempted && FindSummon(player,root)==null
                        && CombatBallistics.ManaAffordable(player,item))
                    {
                        aim = root == ProjectileID.FrostHydra ? player.Center : npc.Center;
                        firing = new(root,AmmoID.None,0);
                        summonAttempted=true;
                    }
                }
                else if(!drainSwing&&canPress&&CombatBallistics.ReadRound(player,item,out var round)==null
                    &&CombatBallistics.Aim(player,npc,item,round,out aim))firing=round;
                press=firing.HasValue;
            }
            else press=player.itemAnimation==0&&ready&&player.releaseUseItem&&CanSwing();
            var previousHit=LastHit;
            if (press || player.itemAnimation > 0)
            {
                player.direction = npc.Center.X < player.Center.X ? -1 : 1;
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
            runNative(new(aim, press));
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
        if(combat.Ranged||combat.drainSwing||!AllowsItem(p)||!combat.ValidWeapon()||!combat.SameSpawn(Main.npc[combat.Target.Slot])||!combat.SameSpawn(target)||!Enemy(target,p))return 0;
        int before=target.life;
        int damage=target.StrikeNPC(hit,fromNet,noPlayerInteraction);
        if(combat.Owned() && CombatStrikeEvidence.Observe(combat.SameSpawn(Main.npc[combat.Target.Slot]),
            before,target.life,damage,target.active,Main.GameUpdateCount) is {} receipt)
        {
            combat.LastHit=receipt;
            combat.missedSwings=0;
        }
        return damage;
    }

    internal static void Shoot(On_Player.orig_ItemCheck_Shoot orig,Player p,int slot,Item held,int damage)
    {
        var combat=checking;
        if(!InInputScope(p)||!AllowsItem(p)||combat.inShoot||combat.firing is not {} round||p.whoAmI!=slot
            ||!ReferenceEquals(held,combat.item)||!combat.ValidWeapon()||!combat.StillEligible(combat.npc)
            ||!combat.SameSpawn(Main.npc[combat.Target.Slot]))return;
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
        bool verified=p.type==round.Projectile&&ammo==round.Ammo&&combat.SameSpawn(Main.npc[combat.Target.Slot]);
        if (verified && combat.Summoning) combat.observedRootIdentity=p.identity;
        return new(combat.ActorBinding,combat,combat.targetEpoch,combat.Target,combat.Weapon.TypeId,ammo,magic,
            p.whoAmI,p.owner,p.identity,p.type,Main.GameUpdateCount,verified);
    }

    internal sealed record Shot(AiReadOnlyProtocol.Identity Binding,NativeCombatWorld Combat,int Epoch,CombatTarget Target,int Weapon,int Ammo,bool Magic,
        int Slot,int Owner,int Identity,int Type,ulong Launched,bool Verified,int? ParentIdentity=null)
    {
        internal bool PersistentRoot => (Weapon,Type) is (ItemID.SlimeStaff,ProjectileID.BabySlime)
            or (ItemID.StaffoftheFrostHydra,ProjectileID.FrostHydra);
        internal bool SummonAttack => PersistentRoot || ParentIdentity.HasValue;
        // A root can collect evidence for the current task. A child takes a
        // snapshot of that assignment at birth, including the absence of one.
        internal Shot ForCurrentTarget()
        {
            var task=current;
            if (!PersistentRoot || task == null || task.fault != null || !Combat.Owned()
                || !ReferenceEquals(task.player,Combat.player) || !task.ValidWeapon()
                || task.item.type != Weapon || !task.Owned()) return null;
            if (!task.SameSpawn(Main.npc[task.Target.Slot]) || !task.StillEligible(task.npc)) return null;
            return ReferenceEquals(task,Combat) && Epoch==task.targetEpoch ? this
                : this with { Combat=task, Epoch=task.targetEpoch, Target=task.Target };
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
            return this with { Combat=assignment?.Combat, Target=assignment?.Target, Epoch=assignment?.Epoch ?? 0,
                Slot=child.whoAmI, Identity=child.identity, Type=child.type,
                Launched=Main.GameUpdateCount, ParentIdentity=parentConfirmed ? Identity : null, Verified=parentConfirmed };
        }

        internal bool Owns(Projectile p)=>p.whoAmI==Slot&&p.owner==Owner&&p.identity==Identity&&p.type==Type
            &&ReferenceEquals(Main.projectile[Slot],p)&&ServerAiNativeActions.Owned(Binding)
            &&Binding.Actor is Player {active:true,dead:false,ghost:false};
        internal bool Pending(NativeCombatWorld combat,Projectile p)=>Verified&&Combat!=null&&Combat.fault==null&&ReferenceEquals(combat,Combat)
            &&ReferenceEquals(current,Combat)&&Epoch==Combat.targetEpoch&&Owns(p);
        internal void ObserveFault(Projectile p,Exception exception)
        {if(Pending(Combat,p))Combat.fault=exception;}
        private bool SameTarget(Projectile p,NPC target)=>Pending(Combat,p)&&target.whoAmI==Target.Slot
            &&Combat.SameSpawn(target)&&Combat.SameSpawn(Main.npc[Target.Slot]);
        internal bool CanHit(Projectile p,NPC target)=>SameTarget(p,target)&&Combat.StillEligible(target);
        internal bool ObserveHit(Projectile p,NPC target,int before,int damage)
        {
            if(!SameTarget(p,target)||CombatStrikeEvidence.Observe(true,before,target.life,damage,target.active,Main.GameUpdateCount) is not {} hit)
                return false;
            Combat.LastHit=PersistentRoot || ParentIdentity.HasValue
                ? hit with { Summon=new(Owner,Type,Identity,ParentIdentity,Launched,Epoch) }
                : hit;
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
