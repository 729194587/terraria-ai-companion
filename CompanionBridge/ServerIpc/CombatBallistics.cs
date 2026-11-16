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
    private static readonly Dictionary<int,int> updates = new();
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

    internal static bool Aim(Player p,NPC target,Item weapon,Round round,out Vector2 aim)
    {
        var origin=p.RotatedRelativePoint(p.MountedCenter);
        aim=target.Center;
        if(Vector2.DistanceSquared(origin,aim)>Range(weapon)*Range(weapon))return false;
        if(!updates.TryGetValue(round.Projectile,out int perTick))
        {
            var sample=new Projectile();sample.SetDefaults(round.Projectile);
            updates[round.Projectile]=perTick=sample.extraUpdates+1;
        }
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
                if(!ClearStep(position,velocity)){clear=false;break;}
                position=next;
            }
            if(!clear||Math.Abs(position.X-future.X)>target.width*.5f||Math.Abs(position.Y-future.Y)>target.height*.5f)continue;
            aim=origin+unit*512f;
            return true;
        }
        return false;
    }
    private static bool ClearStep(Vector2 center,Vector2 velocity)
    {
        // TileCollision is the native swept test. Preserve its shared
        // scratch flags so a planning query cannot affect player physics.
        var flags=(Collision.up,Collision.down,Collision.stair,Collision.stairFall,Collision.sloping);
        try{return Collision.TileCollision(center-new Vector2(2),velocity,4,4)==velocity;}
        finally{(Collision.up,Collision.down,Collision.stair,Collision.stairFall,Collision.sloping)=flags;}
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
