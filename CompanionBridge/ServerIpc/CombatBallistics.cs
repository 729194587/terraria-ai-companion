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
// ammo flight profiles. PickAmmo supplies the effective launch speed and type.
internal static class CombatBallistics
{
    internal readonly record struct Round(int Projectile,int Ammo,float Speed);
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
    internal static bool Supported(Item item) =>
        (item.type is ItemID.WoodenBow or ItemID.BorealWoodBow or ItemID.PalmWoodBow or ItemID.RichMahoganyBow
            or ItemID.EbonwoodBow or ItemID.ShadewoodBow or ItemID.CopperBow or ItemID.TinBow
            or ItemID.IronBow or ItemID.LeadBow or ItemID.SilverBow or ItemID.TungstenBow
            or ItemID.GoldBow or ItemID.PlatinumBow or ItemID.DemonBow or ItemID.TendonBow
            or ItemID.Musket or ItemID.TheUndertaker or ItemID.FlintlockPistol or ItemID.Handgun
            or ItemID.Minishark or ItemID.Revolver)
        && (item.useAmmo==AmmoID.Arrow||item.useAmmo==AmmoID.Bullet) && !item.channel && item.shoot>0
        && item.noMelee && item.useStyle==ItemUseStyleID.Shoot;
    internal static float Range(Item weapon)=>weapon.useAmmo==AmmoID.Arrow?400f:480f;

    internal static string ReadRound(Player p,Item weapon,out Round round)
    {
        round=default;
        if(!p.PickAmmo(weapon,out int projectile,out float speed,out _,out _,out int ammo,dontConsume:true))
            return "combat_ammo_unavailable";
        if((weapon.useAmmo==AmmoID.Arrow?projectile!=ProjectileID.WoodenArrowFriendly:projectile!=ProjectileID.Bullet)
            ||!float.IsFinite(speed)||speed<=0||ammo<=0||ammo>=ItemID.Count)
            return "combat_ammo_unsupported";
        round=new(projectile,ammo,speed);
        return null;
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
        // AI style 1 wooden arrows start falling after 14 projectile updates.
        // Sample the low arc first. Use the actual ammo-adjusted speed, and
        // inspect the entire predicted arc rather than a straight aim ray.
        for(int step=1;step<=45*perTick;step++)
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
    internal static bool ClearStep(Vector2 center,Vector2 velocity,CollisionProfile profile)
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
            return !Collision.WetCollision(position,profile.Width,profile.Height)
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
    private int lifeBefore;
    public override void OnSpawn(Projectile projectile,IEntitySource source)
    {
        lifeBefore=0;
        Flight=source is EntitySource_ItemUse_WithAmmo use ? NativeCombatWorld.CaptureShot(projectile,use) : null;
        if(Flight is {} shot)ServerCombatFixture.ObserveProjectileSpawn(projectile,shot);
    }
    public override bool? CanHitNPC(Projectile projectile,NPC target)=>
        Flight==null||Flight.CanHit(projectile,target)?null:false;
    public override bool? CanCutTiles(Projectile projectile)=>Flight==null?null:false;
    public override bool CanHitPvp(Projectile projectile,Player target)=>Flight==null;
    public override void ModifyHitNPC(Projectile projectile,NPC target,ref NPC.HitModifiers modifiers)
    {lifeBefore=Flight?.CanHit(projectile,target)==true?target.life:0;}
    public override void OnHitNPC(Projectile projectile,NPC target,NPC.HitInfo hit,int damageDone)
    {
        if(lifeBefore>0&&Flight?.ObserveHit(projectile,target,lifeBefore,damageDone)==true)
            ServerCombatFixture.ObserveProjectileHit(projectile,target,damageDone,Flight);
        lifeBefore=0;
    }

    internal static void Install(Action<Action,Action> install)
        =>install(()=>On_Projectile.Update+=Update,()=>On_Projectile.Update-=Update);

    private static void Update(On_Projectile.orig_Update orig,Projectile p,int index)
    {
        if(!Main.dedServ||Main.netMode!=NetmodeID.Server||!p.active||!p.friendly||p.hostile
            ||!p.TryGetGlobalProjectile<CombatProjectile>(out var trace)||trace.Flight is not {Verified:true} flight||!flight.Owns(p))
        {orig(p,index);return;}
        // Native Update encloses AI, Damage and Kill. Supply owner authority
        // only to a validated shot, with no client netMode or packet replay.
        int local=Main.myPlayer;
        try{Main.myPlayer=p.owner;orig(p,index);}
        catch(Exception e){flight.ObserveFault(p,e);throw;}
        finally{Main.myPlayer=local;ServerCombatFixture.ObserveFlight(p,flight);}
    }
}
