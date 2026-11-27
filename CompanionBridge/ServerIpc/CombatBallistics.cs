using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

// Deliberately limited to ordinary one-projectile vanilla weapons and simple
// ranged/magic flight profiles. PickAmmo supplies the effective ammo launch;
// magic uses the native held item without consuming ammo.
internal static class CombatBallistics
{
    internal readonly record struct Round(int Projectile,int Ammo,float Speed);
    internal enum Kind { Ranged, Magic }
    internal readonly record struct CollisionProfile(int Width,int Height,int Updates,bool FallThrough,Vector2 Center);
    private static readonly Dictionary<int,CollisionProfile> profiles = new();

    internal static CollisionProfile Profile(int type)
    {
        if(profiles.TryGetValue(type,out var profile))return profile;
        var sample=new Projectile();sample.SetDefaults(type);
        // For the two admitted dry projectile types HandleMovement reaches
        // its default width/height and centered, platform-fall-through path.
        int width=sample.width,height=sample.height;
        bool fallThrough=true;
        var center=new Vector2(.5f);
        if(!ProjectileLoader.TileCollideStyle(sample,ref width,ref height,ref fallThrough,ref center))
            throw new InvalidOperationException("Expected a tile-colliding ordinary projectile.");
        return profiles[type]=new(width,height,sample.extraUpdates+1,fallThrough,center);
    }
    internal static Kind? Family(Item item) =>
        Raw(item) ? Kind.Ranged : MagicFamily(item) ? Kind.Magic : null;
    internal static bool Supported(Item item)=>Family(item)!=null;
    internal static bool IsMagic(Item item)=>Family(item)==Kind.Magic;
    internal static bool MagicShot(int type)=>type is ProjectileID.WandOfSparkingSpark or ProjectileID.AmethystBolt
        or ProjectileID.TopazBolt or ProjectileID.SapphireBolt or ProjectileID.EmeraldBolt
        or ProjectileID.RubyBolt or ProjectileID.DiamondBolt or ProjectileID.AmberBolt;
    // Keep the admission check cheap and tied to the native cast: the staff
    // must request one of the direct bolt projectiles in MagicShot. Wand and
    // gem staff SetDefaults differ in use style, so useStyle is not a stable
    // discriminator here.
    internal static bool MagicFamily(Item item)=>(item.type is ItemID.WandofSparking or ItemID.AmethystStaff
        or ItemID.TopazStaff or ItemID.SapphireStaff or ItemID.EmeraldStaff or ItemID.RubyStaff
        or ItemID.DiamondStaff or ItemID.AmberStaff)&&item.mana>0&&item.useAmmo==AmmoID.None
        &&MagicShot(item.shoot)
        &&!item.channel&&item.shoot>0&&item.shootSpeed>0&&item.noMelee;
    private static bool Raw(Item item) =>
        (item.type is ItemID.WoodenBow or ItemID.BorealWoodBow or ItemID.PalmWoodBow or ItemID.RichMahoganyBow
            or ItemID.EbonwoodBow or ItemID.ShadewoodBow or ItemID.CopperBow or ItemID.TinBow
            or ItemID.IronBow or ItemID.LeadBow or ItemID.SilverBow or ItemID.TungstenBow
            or ItemID.GoldBow or ItemID.PlatinumBow or ItemID.DemonBow or ItemID.TendonBow
            or ItemID.Musket or ItemID.TheUndertaker or ItemID.FlintlockPistol or ItemID.Handgun
            or ItemID.Minishark or ItemID.Revolver)
        && (item.useAmmo==AmmoID.Arrow||item.useAmmo==AmmoID.Bullet) && !item.channel && item.shoot>0
        && item.noMelee && item.useStyle==ItemUseStyleID.Shoot;
    internal static float Range(Item weapon)=>weapon.type==ItemID.WandofSparking?260f
        :Family(weapon)==Kind.Magic?720f
        :weapon.useAmmo==AmmoID.Arrow?400f:480f;


    internal static string ReadRound(Player p,Item weapon,out Round round)
    {
        round=default;
        if(IsMagic(weapon))
        {
            if(!ManaAffordable(p,weapon))return "combat_mana_unavailable";
            if(weapon.shoot<=0||weapon.shoot>=ProjectileID.Count||!float.IsFinite(weapon.shootSpeed)||weapon.shootSpeed<=0)
                return "combat_spell_unsupported";
            // A later registry may remap ProjectileID constants. Tie the
            // admitted staff to the direct vanilla bolt it is known to emit.
            if(!MagicShot(weapon.shoot))return "combat_spell_unsupported";
            round=new(weapon.shoot,AmmoID.None,weapon.shootSpeed);
            return null;
        }
        if(!p.PickAmmo(weapon,out int projectile,out float speed,out _,out _,out int ammo,dontConsume:true))
            return "combat_ammo_unavailable";
        if((weapon.useAmmo==AmmoID.Arrow?projectile!=ProjectileID.WoodenArrowFriendly:projectile!=ProjectileID.Bullet)
            ||!float.IsFinite(speed)||speed<=0||ammo<=0||ammo>=ItemID.Count)
            return "combat_ammo_unsupported";
        round=new(projectile,ammo,speed);
        return null;
    }

    // ItemCheck_PayMana -> CheckMana owns the payment decision, including
    // QuickMana potions behind Mana Flower. Preflight only refuses when the
    // native path provably cannot finance the cast, so an AI decision never
    // suppresses that native recovery.
    internal static bool ManaAffordable(Player p,Item weapon)
    {
        int cost=p.GetManaCost(weapon);
        if(cost<0)return false;
        if(p.statMana>=cost)return true;
        return p.manaFlower&&p.statMana<p.statManaMax2&&!p.cursed&&!p.CCed&&!p.dead
            &&p.QuickMana_GetItemToUse()!=null;
    }

    internal static Vector2 Muzzle(Player p)=>p.RotatedRelativePoint(p.MountedCenter);
    internal static bool Aim(Player p,NPC target,Item weapon,Round round,out Vector2 aim)
        =>Aim(Muzzle(p),target,weapon,round,out aim);

    internal static bool Aim(Vector2 origin,NPC target,Item weapon,Round round,out Vector2 aim)
    {
        aim=target.Center;
        if(Vector2.DistanceSquared(origin,aim)>Range(weapon)*Range(weapon))return false;
        var profile=Profile(round.Projectile);
        int perTick=profile.Updates;
        bool arrow=round.Projectile==ProjectileID.WoodenArrowFriendly;
        // Spark travel is much shorter than the staff range. Its native velocity
        // stays untouched; sample the shorter arc.
        int limit=weapon.type==ItemID.WandofSparking?24:45;
        // AI style 1 wooden arrows start falling after 14 projectile updates.
        // Sample the low arc first. Use the actual ammo-adjusted speed, and
        // inspect the entire predicted arc rather than a straight aim ray.
        for(int step=1;step<=limit*perTick;step++)
        {
            float ticks=(float)step/perTick;
            var future=target.Center+target.velocity*ticks;
            int falling=arrow?Math.Max(0,step-14):0;
            var delta=future-origin-new Vector2(0,.05f*falling*(falling+1));
            float length=delta.Length();
            if(!float.IsFinite(length)||length<1||Math.Abs(length-round.Speed*step)>Math.Max(3,round.Speed*.6f))continue;
            var unit=delta/length;
            var velocity=unit*round.Speed;
            var position=origin;
            bool clear=true;
            for(int n=1;n<=step;n++)
            {
                if(arrow&&n>=15)velocity.Y=Math.Min(16,velocity.Y+.1f);
                var next=position+velocity;
                if(!ClearStep(position,velocity,profile)){clear=false;break;}
                position=next;
            }
            if(!clear||Math.Abs(position.X-future.X)>target.width*.5f||Math.Abs(position.Y-future.Y)>target.height*.5f)continue;
            aim=origin+unit*512f;
            return true;
        }
        return false;
    }
    internal static bool ClearStep(Vector2 center,Vector2 velocity,CollisionProfile profile,bool dryOnly=true)
    {
        var flags=(Collision.up,Collision.down,Collision.stair,Collision.stairFall,Collision.sloping,
            Collision.honey,Collision.shimmer,Collision.landMine);
        var position=center-new Vector2(profile.Width,profile.Height)*profile.Center;
        try
        {
            if(Collision.TileCollision(position,velocity,profile.Width,profile.Height,profile.FallThrough,profile.FallThrough)!=velocity)
                return false;
            var slope=Collision.SlopeCollision(position,velocity,profile.Width,profile.Height,0,true);
            if(new Vector2(slope.X,slope.Y)!=position||new Vector2(slope.Z,slope.W)!=velocity)return false;
            // Water/honey/shimmer motion is outside the dry ballistic profile.
            return !dryOnly || !Collision.WetCollision(position,profile.Width,profile.Height)
                &&!Collision.WetCollision(position+velocity,profile.Width,profile.Height);
        }
        finally
        {
            (Collision.up,Collision.down,Collision.stair,Collision.stairFall,Collision.sloping,
                Collision.honey,Collision.shimmer,Collision.landMine)=flags;
        }
    }

}

// A spawn token holds the original action, target incarnation and native
// projectile identity. It cannot be transferred by reusing either slot.
public sealed class CombatProjectile : GlobalProjectile
{
    public override bool InstancePerEntity=>true;
    internal NativeCombatWorld.Shot Flight;
    internal ulong SpawnUpdate { get; private set; }
    private int lifeBefore;
    private int memberBefore;
    private NPC carrier;
    private NativeCombatWorld.Shot hitSource;
    private static Projectile updating;
    private NativeCombatWorld.Shot assignment;
    private NativeCombatWorld.Shot HitSource => Flight is {PersistentRoot:true} ? assignment : Flight;
    public override void OnSpawn(Projectile projectile,IEntitySource source)
    {
        SpawnUpdate=Main.GameUpdateCount;
        if(projectile.hostile)ServerCombatFixture.HostileSpawn(projectile,source);
        lifeBefore=0;
        // Magic no longer carries an ammo source, but the native item-use
        // source still proves the projectile came from the held spell.
        Flight=source is EntitySource_ItemUse use ? NativeCombatWorld.CaptureShot(projectile,use) : NativeCombatWorld.CaptureAbigail(projectile,source);
        if (Flight==null && updating is {} parent && parent.TryGetGlobalProjectile<CombatProjectile>(out var trace))
        {
            bool confirmed=source is EntitySource_Parent origin && ReferenceEquals(origin.Entity,parent);
            Flight=trace.Flight?.Child(parent,projectile,trace.assignment,confirmed);
        }
        if(Flight is {} shot)ServerCombatFixture.ObserveProjectileSpawn(projectile,shot);
    }
    public override bool? CanHitNPC(Projectile projectile,NPC target)
    {
        if (Flight==null) return null;
        if (Flight.SummonAttack && HitSource?.Allowed==null) return Flight.Verified && Flight.Owns(projectile) ? null : false;
        return HitSource?.CanHit(projectile,target)==true ? null : false;
    }
    public override bool? CanCutTiles(Projectile projectile)=>Flight==null?null:false;
    public override bool CanHitPvp(Projectile projectile,Player target)=>Flight==null;
    public override void ModifyHitNPC(Projectile projectile,NPC target,ref NPC.HitModifiers modifiers)
    {hitSource=HitSource; carrier=hitSource?.Carrier(target); memberBefore=target.life; lifeBefore=hitSource?.CanHit(projectile,target)==true?carrier.life:0;}
    public override void OnHitNPC(Projectile projectile,NPC target,NPC.HitInfo hit,int damageDone)
    {
        bool credited=lifeBefore>0 && hitSource?.ObserveHit(projectile,target,carrier,memberBefore,lifeBefore,damageDone)==true;
        if(credited) ServerCombatFixture.ObserveProjectileHit(projectile,target,damageDone,hitSource);
        if(Flight is {SummonAttack:true}) ServerCombatFixture.ObserveSummonHit(projectile,target,damageDone,Flight,credited);
        lifeBefore=0;
    }

    internal static void Install(Action<Action,Action> install)
        =>install(()=>On_Projectile.Update+=Update,()=>On_Projectile.Update-=Update);

    private static void Update(On_Projectile.orig_Update orig,Projectile p,int index)
    {
        if (updating != null) throw new InvalidOperationException("Reentrant projectile update during AI owner scope.");
        if(!Main.dedServ||Main.netMode!=NetmodeID.Server||!p.active||p.hostile
            ||!p.TryGetGlobalProjectile<CombatProjectile>(out var trace)||trace.Flight is not {Verified:true} flight
            || (!p.friendly && !flight.SummonAttack && !flight.HeldCursor) || !flight.Owns(p))
        {orig(p,index);return;}
        // Native Update encloses AI, Damage and Kill. Supply owner authority
        // only to a validated shot, with no client netMode or packet replay.
        int local=Main.myPlayer;
        var actor=Main.player[p.owner];
        var cursor=(Main.mouseX,Main.mouseY,Main.lastMouseX,Main.lastMouseY);
        var pose=(actor.itemAnimation,actor.itemTime,actor.heldProj);
        bool previousChannel=actor.channel, releaseOldFlight=false;
        int previousTarget=actor.MinionAttackTargetNPC;
        try
        {
            updating=p;
            trace.assignment=flight.PersistentRoot ? flight.ForCurrentTarget() : null;
            if (trace.assignment is {} active) actor.MinionAttackTargetNPC=active.Target.Slot;
            if(flight.HeldCursor)
            {
                if(flight.CursorAim(p) is {} aim)
                {
                    Main.mouseX=Main.lastMouseX=(int)(aim.X-Main.screenPosition.X);
                    Main.mouseY=Main.lastMouseY=(int)(aim.Y-Main.screenPosition.Y);
                }
                else {releaseOldFlight=true;actor.channel=false;} // Run the native release path.
            }
            Main.myPlayer=p.owner;
            orig(p,index);
        }
        catch(Exception e){flight.ObserveFault(p,e);throw;}
        finally
        {
            (Main.mouseX,Main.mouseY,Main.lastMouseX,Main.lastMouseY)=cursor;
            // Returning native projectiles can set the owner's held pose even after this task
            // released the body. Keep the return flight, discard its stale input/pose.
            if(releaseOldFlight)
            {
                actor.channel=previousChannel;
                (actor.itemAnimation,actor.itemTime,actor.heldProj)=pose;
                if(actor.heldProj==p.whoAmI)actor.heldProj=-1;
            }
            Main.myPlayer=local;
            if (trace.assignment!=null) actor.MinionAttackTargetNPC=previousTarget;
            trace.assignment=null;
            updating=null;
            ServerCombatFixture.ObserveFlight(p,flight);
        }
    }
}
