using System;
using System.Linq;
using System.Reflection;
using CompanionBridge.ServerIpc;
using Microsoft.Xna.Framework;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace VirtualPlayerEngineProbe;

// The engine reuses both Projectile objects and identity numbers on the server.
// tML instantiates this marker afresh on SetDefaults for a basic hook.
public sealed class GrappleIncarnation : GlobalProjectile
{
    public override bool InstancePerEntity => true;
    public override bool AppliesToEntity(Projectile entity, bool lateInstantiation)
        => lateInstantiation && entity.type == ProjectileID.Hook;
}

internal sealed class ActorGrapple
{
    private static ActorGrapple current;
    private static Func<Player, Item, bool> canUse;
    private readonly AiReadOnlyProtocol.Identity actor;
    private readonly Projectile projectile;
    private readonly GrappleIncarnation incarnation;
    internal readonly int Index, Identity;
    internal int Owner => actor.Slot;

    private ActorGrapple(AiReadOnlyProtocol.Identity actor, Projectile projectile, int index)
    {
        this.actor = actor;
        this.projectile = projectile;
        Index = index;
        Identity = projectile.identity;
        incarnation = projectile.GetGlobalProjectile<GrappleIncarnation>();
    }

    private bool Matches(Projectile candidate) =>
        ReferenceEquals(candidate, projectile) && ReferenceEquals(Main.projectile[Index], projectile)
        && candidate.whoAmI == Index && candidate.identity == Identity && candidate.owner == Owner
        && candidate.type == ProjectileID.Hook && candidate.aiStyle == 7
        && candidate.TryGetGlobalProjectile<GrappleIncarnation>(out var marker) && ReferenceEquals(marker, incarnation);

    internal Projectile Active => ServerAiNativeActions.Owned(actor) && Matches(projectile) && projectile.active ? projectile : null;
    internal bool Attached(Player p) => Active?.ai[0]==2 && p.grapCount==1 && p.grappling[0]==Index
        && !Main.projectile.Any(q=>q.active&&q.owner==Owner&&q.aiStyle==7&&!ReferenceEquals(q,projectile));
    internal bool HasNativeEntry(Player p) => actor.World==ProbeSession.WorldSession&&p.whoAmI==Owner
        &&p.grapCount==1&&p.grappling[0]==Index&&Matches(projectile);

    // The original comparison value is substituted only within native AI_007,
    // only for the current, still bound projectile. No global or caller changes.
    private static int NativeOwner(Projectile candidate) =>
        current is {} shot && ReferenceEquals(shot.projectile,candidate) && shot.Active!=null ? shot.Owner : Main.myPlayer;

    internal static void Install(Action<Action, Action> install)
    {
        var check = typeof(Player).GetMethod("ItemCheck_CheckCanUse", BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { typeof(Item) }, null) ?? throw new MissingMethodException("Native hook item admission");
        canUse = (Func<Player, Item, bool>)check.CreateDelegate(typeof(Func<Player, Item, bool>));
        ILContext.Manipulator patch = il =>
        {
            var reads = il.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldsfld
                && i.Operand is FieldReference f && f.DeclaringType.FullName == "Terraria.Main" && f.Name == "myPlayer").ToArray();
            if (reads.Length != 3 || reads.Any(i => i.Next.OpCode != OpCodes.Ldarg_0
                || i.Next.Next.Operand is not FieldReference f || f.DeclaringType.FullName!="Terraria.Projectile" || f.Name != "owner"
                || i.Next.Next.Next.OpCode.Code is not (Code.Bne_Un or Code.Bne_Un_S)))
                throw new InvalidOperationException("Unexpected native grapple owner branches");
            var cursor = new ILCursor(il);
            foreach (var read in reads)
            {
                // Keep the original instruction as a valid branch target.
                read.OpCode = OpCodes.Ldarg_0;
                read.Operand = null;
                cursor.Goto(read, MoveType.After);
                cursor.EmitDelegate<Func<Projectile, int>>(NativeOwner);
            }
        };
        install(() => IL_Projectile.AI_007_GrapplingHooks += patch, () => IL_Projectile.AI_007_GrapplingHooks -= patch);
        On_Projectile.hook_AI_007_GrapplingHooks lifetime = (orig,q) =>
        {
            if(current is {} shot&&ReferenceEquals(shot.projectile,q)&&shot.Matches(q)
                &&!ServerAiNativeActions.Owned(shot.actor))
            {
                // Do not let an attached projectile populate a replacement
                // Actor's grapple list before the navigation driver can run.
                shot.ReleaseStale();
                return;
            }
            orig(q);
        };
        install(() => On_Projectile.AI_007_GrapplingHooks += lifetime, () => On_Projectile.AI_007_GrapplingHooks -= lifetime);
        On_Player.hook_GrappleMovement movement = (orig,p) =>
        {
            if(current is {} shot&&shot.HasNativeEntry(p)&&!ServerAiNativeActions.Owned(shot.actor))
            {shot.ReleaseStale();return;}
            if(ActorControl.Driver is ServerAiNavigation driver && !driver.BeforeGrappleMovement(p))return;
            orig(p);
        };
        install(() => On_Player.GrappleMovement += movement, () => On_Player.GrappleMovement -= movement);
    }

    internal static Item Selected(Player p)
    {
        var item = p.QuickGrapple_GetItemToUse();
        return item is { IsAir: false, type: ItemID.GrapplingHook, shoot: ProjectileID.Hook, ModItem: null }
            && float.IsFinite(item.shootSpeed) && item.shootSpeed > 0 ? item : null;
    }

    internal static bool HasHook(Player p) => p.grapCount != 0 || p.grappling[0] >= 0
        || Main.projectile.Any(q => q.active && q.owner == p.whoAmI && q.aiStyle == 7);

    internal static ActorGrapple Fire(AiReadOnlyProtocol.Identity actor, Item item, Vector2 target)
    {
        ServerAiNativeActions.Require(actor);
        var p = (Player)actor.Actor;
        if (current != null || item == null || !ReferenceEquals(Selected(p), item) || HasHook(p)
            || p.dead || p.ghost || p.noItems || p.CCed || p.tongued || p.mount.Active
            || p.frozen || p.webbed || p.stoned || !canUse(p, item)
            || ProjectileLoader.CanUseGrapple(ProjectileID.Hook, p) == false)
            return null;
        // NewProjectile otherwise replaces the oldest projectile at capacity.
        if (!Main.projectile.Take(Main.maxProjectiles).Any(q => !q.active)) return null;
        var delta = target - p.Center;
        float length = delta.Length();
        if (!float.IsFinite(length) || length < 1 || length > CompanionBridge.Motion.GrappleClimbPlan.Range) return null;
        int type = item.shoot;
        ProjectileLoader.UseGrapple(p, ref type);
        if (type != ProjectileID.Hook || !ServerAiNativeActions.Owned(actor)||!ReferenceEquals(Selected(p),item)||HasHook(p)
            || !Main.projectile.Take(Main.maxProjectiles).Any(q=>!q.active))return null;
        int index = Projectile.NewProjectile(p.GetSource_ItemUse(item), p.Center, delta * (item.shootSpeed / length),
            type, 0, 0, p.whoAmI);
        if (index < 0 || index >= Main.maxProjectiles) throw new InvalidOperationException("Native hook allocation unconfirmed");
        var shot = new ActorGrapple(actor, Main.projectile[index], index);
        if (shot.Active == null) throw new InvalidOperationException("Native hook ownership unconfirmed");
        current = shot;
        try { shot.Sync(); }
        catch { shot.Release(); throw; }
        return shot;
    }

    internal void Sync() => NetMessage.SendData(MessageID.SyncProjectile,-1,-1,null,Index);

    private void ReleaseStale()
    {
        try{Release();}
        catch(Exception e){ProbeSession.Log("navigation-grapple-stale-cleanup-error",new{Index,Identity,Owner,e.Message});}
    }

    internal bool Release()
    {
        if(!Program.IsMainThread)return false;
        if (ReferenceEquals(current, this)) current = null;
        if(actor.World!=ProbeSession.WorldSession)return false;
        bool stillOwned=ServerAiNativeActions.Owned(actor);
        var p = (Player)actor.Actor;
        if (!Matches(projectile))
        {
            // A reused slot is outside this lease. Never send its kill packet.
            // If our obsolete entry was the only one and the Actor has no
            // active hook left, native removal only clears the Player list.
            if(stillOwned&&p.grapCount==1&&p.grappling[0]==Index
                &&!Main.projectile.Any(q=>q.active&&q.owner==Owner&&q.aiStyle==7))
                p.RemoveAllGrapplingHooks();
            return false;
        }
        if (projectile.active)
        {
            if (stillOwned&&p.grapCount == 1 && p.grappling[0] == Index
                && !Main.projectile.Any(q => q.active && q.owner == Owner && q.aiStyle == 7 && !ReferenceEquals(q, projectile)))
                p.RemoveAllGrapplingHooks();
            else projectile.Kill();
        }
        if (projectile.active) return false;
        NetMessage.SendData(MessageID.KillProjectile, -1, -1, null, Identity, Owner);
        // The exact old projectile may be retired without writing the
        // replacement Player. Navigation still reports binding loss.
        return stillOwned;
    }
}
