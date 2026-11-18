using System;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.Utilities;
using CompanionBridge.Motion;

namespace CompanionBridge.ServerIpc;

internal static partial class ServerCombatFixture
{
    internal static bool Baseline {get;private set;}
    private static string pattern;
    private static Vector2 origin;
    private static int sample, lostLife, conflicts, opportunities, impacts;
    private static ulong? movementTick;
    private static long frameStart;
    private static double worst,plannerWorst;
    private static readonly List<object> wounds=new();
    private static Projectile hazard;
    private static readonly List<object> samples=new();
    private static readonly List<object> candidates=new();
    internal static void Candidate(int decision,PhysicsState state,int dir,int hold,Prediction path,int cost)
    {
        if(Sampling && decision<=5)candidates.Add(new{sample,decision,state,dir,hold,path.Safe,path.Reason,cost});
    }
    internal static void EvasionInput(Player p)
    {
        if(!Sampling)return;
        if(movementTick==Main.GameUpdateCount || p.controlLeft&&p.controlRight)conflicts++;
        movementTick=Main.GameUpdateCount;
    }
    private static bool Sampling=>pattern!=null && sample is >=1 and <=96;
    internal static void EvasionHurt(Player.HurtInfo info,int amount)
    {
        if(Measuring){hurtCount++;hurtTotal+=amount;}
        if(!Sampling)return;
        lostLife+=amount;
        wounds.Add(new{sample,amount,npc=info.DamageSource.SourceNPCIndex,projectile=info.DamageSource.SourceProjectileType});
    }
    internal static long PlannerStart()=>Sampling?Stopwatch.GetTimestamp():0;
    internal static void PlannerEnd(long start)
    {
        if(start!=0)plannerWorst=Math.Max(plannerWorst,Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }
    internal static void ItemMovementConflict(bool changed){if(Sampling && changed)conflicts++;}
    internal static void EvasionPress(bool press){if(Sampling && press)opportunities++;}
    internal static void BeginFrame()
    {
        if(Sampling)frameStart=Stopwatch.GetTimestamp();
    }
    internal static void EndFrame(Player p)
    {
        if(!Sampling)return;
        double ms=Stopwatch.GetElapsedTime(frameStart).TotalMilliseconds;
        worst=Math.Max(worst,ms);
        samples.Add(new{sample,p.position.X,p.position.Y,p.statLife,p.selectedItem,lostLife,opportunities,ms});
        if(sample==96)
        {
            impacts=hitTrace.Count+projectileHits.Count-impacts;
            File.WriteAllText(Path.Combine(Main.SavePath,"evasion-"+pattern+"-"+(Baseline?"off":"on")+".json"),
                JsonSerializer.Serialize(new{pattern,Baseline,lostLife,conflicts,opportunities,impacts,worst,plannerWorst,wounds,candidates,samples}));
        }
    }
    private static void AdvanceEvasion(Player p)
    {
        if(sample>=96){sample=97;return;}
        if(!NativeCombatWorld.Running)return;
        sample++;
        if(sample==1)Main.rand=new UnifiedRandom(817);
        float dx=pattern switch {
            "contact"=>-130+sample*2, "charge" or "edge"=>-180+sample*5,
            "trapped"=>-72+Math.Min(72,sample*3), _=>-180};
        target.Center=origin+new Vector2(dx,0);
        target.velocity=new(pattern switch {"contact"=>2,"charge" or "edge"=>5,"trapped" when sample<24=>3,_=>0},0);
        if(pattern is "beam" or "bolt" && sample==8)
        {
            bool beam=pattern=="beam";
            Vector2 pos=origin+new Vector2(-160,12);
            int i=Projectile.NewProjectile(p.GetSource_Misc("isolated_evasion"),pos,new Vector2(beam?1:6,0),
                beam?ProjectileID.PhantasmalDeathray:ProjectileID.DeathLaser,16,0,255);
            hazard=Main.projectile[i];hazard.aiStyle=-1;hazard.tileCollide=false;
            if(beam){hazard.scale=1;hazard.localAI[1]=400;}
        }
    }
    private static void EvasionScene(Player p,string mode)
    {
        if(mode=="evasion-clear")
        {
            if(target!=null)target.active=false;
            if(hazard!=null)hazard.active=false;
            pattern=null;Baseline=false;
            File.WriteAllText(Path.Combine(Main.SavePath,"combat-ready.json"),"{}");
            return;
        }
        string[] parts=mode.Split('-');
        pattern=parts[1];Baseline=parts[2]=="off";
        int x=Main.spawnTileX,floor=Main.spawnTileY+8;
        for(int dx=-24;dx<=24;dx++)for(int dy=-15;dy<=40;dy++)
        {
            var tile=Main.tile[x+dx,floor+dy];tile.ClearEverything();
            bool platform=pattern=="edge" && dx<=2 && dy==0;
            bool room=pattern=="trapped" && (dy==-4 || dy>=-4 && Math.Abs(dx)==2);
            if(platform||room||dy>=0 && pattern!="edge" || dy==40)
            {tile.HasTile=true;tile.TileType=platform?TileID.Platforms:TileID.Dirt;}
        }
        p.Bottom=new(x*16+10,floor*16);p.velocity=Vector2.Zero;p.direction=-1;
        p.ConsumedLifeCrystals=10;p.statLife=p.statLifeMax=p.statLifeMax2=300;p.immune=false;p.immuneTime=0;Array.Clear(p.hurtCooldowns);
        p.jump=0;p.releaseJump=true;p.lifeRegenTime=p.lifeRegenCount=0;
        p.itemAnimation=p.itemTime=p.reuseDelay=0;p.selectedItem=0;p.releaseUseItem=true;
        p.inventory[54].stack=999;origin=p.Center;
        foreach(var q in Main.ActiveProjectiles)if(q.owner==p.whoAmI)q.active=false;
        Main.dayTime=false;Main.time=1200;
        target=Main.npc[NPC.NewNPC(p.GetSource_Misc("isolated_evasion"),(int)origin.X-180,(int)origin.Y,NPCID.EyeofCthulhu,Target:p.whoAmI)];
        target.aiStyle=-1;target.noGravity=true;target.noTileCollide=true;target.knockBackResist=0;
        target.Center=origin+new Vector2(-180,0);target.velocity=Vector2.Zero;
        target.damage=target.defDamage=16;
        sample=lostLife=opportunities=conflicts=0;worst=plannerWorst=0;wounds.Clear();movementTick=null;samples.Clear();candidates.Clear();
        impacts=hitTrace.Count+projectileHits.Count;
        File.WriteAllText(Path.Combine(Main.SavePath,"combat-ready.json"),JsonSerializer.Serialize(new{
            entityRef=NativeCombatWorld.Reference(Bound(p),target),initial=new{p.position.X,p.position.Y,p.statLife,p.selectedItem,
            arrows=p.inventory[54].stack,sword=p.inventory[0].type,bow=p.inventory[2].type,dx=target.Center.X-origin.X,pattern}}));
    }
}
