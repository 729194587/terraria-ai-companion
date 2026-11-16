using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using CompanionBridge.Common.Systems;
using CompanionBridge.Motion;
using VirtualPlayerEngineProbe;
using Identity = CompanionBridge.ServerIpc.AiReadOnlyProtocol.Identity;

namespace CompanionBridge.ServerIpc;

internal sealed class NativeCombatWorld(Identity identity, ServerAiNavigation navigation, string actionId, string entityRef) : ICombatWorld
{
    private const int ThreatRange = 112, AcquireRange = 640, SwingGap = 34;
    private static NativeCombatWorld current;
    private static NativeCombatWorld checking;
    private static (Player actor, string world, long generation, string entityRef, ulong tick) recentHurt;
    private readonly Player player = (Player)identity.Actor;
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

    private static bool WeaponSupported(Item i) => ActorItemUse.Supported(i) && i.type < ItemID.Count
        && i.useStyle == ItemUseStyleID.Swing && i.CountsAsClass(Terraria.ModLoader.DamageClass.Melee)
        && !i.noMelee && !i.noUseGraphic && !i.consumable && i.damage > 0
        && i.pick == 0 && i.axe == 0 && i.hammer == 0 && i.mana == 0 && i.mountType < 0
        && i.useTime > 0 && i.useAnimation > 0;

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
        return !GameNavigation.Ordinary(p) || p.noItems || p.CCed ? "combat_actor_state_unsupported" : null;
    }

    internal static void RecordHurt(Player p, int sourceNpc)
    {
        if (sourceNpc < 0 || sourceNpc >= Main.maxNPCs || !Enemy(Main.npc[sourceNpc], p)) return;
        var world = ProbeSession.WorldSession;
        var generation = ProbeSession.ActorGeneration;
        recentHurt = (p, world, generation, Reference(world, generation, Main.npc[sourceNpc]), Main.GameUpdateCount);
    }

    internal static (string entityRef, string threat)? Threat(Identity identity)
    {
        if (identity?.CanAct != true || !ServerAiNativeActions.Owned(identity)) return null;
        var p = (Player)identity.Actor;
        if (p.selectedItem is < 0 or > 9 || !WeaponSupported(p.HeldItem) || !GameNavigation.Ordinary(p)
            || p.noItems || p.CCed) return null;
        var hurt = recentHurt;
        bool recent = ReferenceEquals(hurt.actor, p) && hurt.world == identity.World && hurt.generation == identity.Generation
            && identity.GameUpdate >= hurt.tick && identity.GameUpdate - hurt.tick <= 45;
        NPC nearest = null;
        float best = float.PositiveInfinity;
        for (int i = 0; i < Main.maxNPCs; i++)
        {
            var n = Main.npc[i];
            if (!Enemy(n, p)) continue;
            if (recent && InRange(p, n, 192) && hurt.entityRef == Reference(identity, n) && Visible(p, n))
                return (hurt.entityRef, "recent_native_npc_damage");
            if (!n.HasValidTarget || n.target != p.whoAmI || !InRange(p, n, ThreatRange) || !Visible(p, n)) continue;
            float distance = Vector2.DistanceSquared(p.Center, n.Center);
            if (distance < best) { best = distance; nearest = n; }
        }
        return nearest == null ? null : (Reference(identity, nearest), "npc_targeting_actor_in_range");
    }

    public string Prepare()
    {
        ServerAiNativeActions.Require(identity);
        npc = Resolve(identity, entityRef);
        if (npc == null) return "combat_target_unavailable";
        spawn = npc.GetGlobalNPC<AtomicNpcIdentity>();
        spawnToken = spawn.Token;
        Target = new(npc.whoAmI, npc.type, entityRef);
        if (player.selectedItem is < 0 or > 9 || !WeaponSupported(player.HeldItem)) return "combat_weapon_unsupported";
        if (!GameNavigation.Ordinary(player) || player.noItems || player.CCed) return "combat_actor_state_unsupported";
        if (!ServerAiNativeActions.BodyIdle(identity)) return "actor_busy";
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
        return SameSpawn(n) ? new(true, n.active, Enemy(n, player), n.life) : new(false, false, false, 0);
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

    private void CheckItem(Action<NativeItemInput.Intent> runNative)
    {
        ulong tick = Main.GameUpdateCount;
        if (checking != null || fault != null || !ServerAiNativeActions.Owned(identity)
            || !NativeSurvival.Owns(player) || player.dead || player.ghost || player.noItems || player.CCed
            || !SameSpawn(Main.npc[Target.Slot]) || !Enemy(npc, player) || !ValidWeapon()
            || !inputs.BeginItemCheck(tick)) return;
        bool press = player.itemAnimation == 0 && player.itemTime == 0 && player.reuseDelay == 0
            && player.releaseUseItem && CanSwing();
        try
        {
            checking = this;
            var previousHit=LastHit;
            if (press || player.itemAnimation > 0)
            {
                player.direction = npc.Center.X < player.Center.X ? -1 : 1;
                inputs.Swing(tick);
            }
            // The native local-input path owns the release latch. The AI
            // supplies its equivalent on the up frame, never on a down frame.
            if (!press) player.releaseUseItem = true;
            runNative(new(npc.Center, press));
            if (press && player.itemAnimation > 0 && Owned())
            {
                player.releaseUseItem=false;
                if (ReferenceEquals(LastHit,previousHit)) missedSwings++;
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
            checking = null;
            if (Owned()) player.controlUseItem = false;
        }
    }

    public static bool AllowsItem(Player p) => checking is {} c && ReferenceEquals(c.player, p)
        && NativeSurvival.Owns(p) && c.Owned();

    // Dedicated ItemCheck passes an empty texture frame to use-style. The swing
    // pose needs native item dimensions, while the server still computes its
    // own native melee hitbox.
    internal static Rectangle SwingFrame(Player p, Item held, Rectangle frame) =>
        AllowsItem(p) ? new Rectangle(0, 0, held.width, held.height) : frame;

    private static bool InInputScope(Player p) => checking is {} c && ReferenceEquals(c.player,p) && NativeItemInput.Active(p);

    internal static bool? CanHit(Player p, Item held, NPC target) => InInputScope(p)
        && (!AllowsItem(p) || !ReferenceEquals(held,checking.item) || !checking.ValidWeapon()
            || !checking.SameSpawn(target) || !checking.SameSpawn(Main.npc[checking.Target.Slot]))
        ? false : null;

    // This is the single native strike call in ProcessHitAgainstNPC, after
    // hit modifiers and before tML's post-hit callbacks.
    public static int StrikeWithEvidence(NPC target,NPC.HitInfo hit,bool fromNet,bool noPlayerInteraction,Player p)
    {
        var combat=checking;
        if(!InInputScope(p))return target.StrikeNPC(hit,fromNet,noPlayerInteraction);
        if(!AllowsItem(p)||!combat.ValidWeapon()||!combat.SameSpawn(Main.npc[combat.Target.Slot])||!combat.SameSpawn(target)||!Enemy(target,p))return 0;
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
