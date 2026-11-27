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
        if(defenseCase!=null)wounds.Add(new{tick=Main.GameUpdateCount,amount,npc=info.DamageSource.SourceNPCIndex,projectile=info.DamageSource.SourceProjectileType});
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
        bool ordinary=parts.Length==4 && parts[3]=="ordinary";
        target=Main.npc[NPC.NewNPC(p.GetSource_Misc("isolated_evasion"),(int)origin.X-180,(int)origin.Y,ordinary?NPCID.Zombie:NPCID.EyeofCthulhu,Target:p.whoAmI)];
        target.aiStyle=-1;target.noGravity=true;target.noTileCollide=true;target.knockBackResist=0;
        target.Center=origin+new Vector2(-180,0);target.velocity=Vector2.Zero;
        target.damage=target.defDamage=16;
        if(ordinary){target.life=target.lifeMax=1000;p.selectedItem=2;}
        sample=lostLife=opportunities=conflicts=0;worst=plannerWorst=0;wounds.Clear();movementTick=null;samples.Clear();candidates.Clear();
        impacts=hitTrace.Count+projectileHits.Count;
        File.WriteAllText(Path.Combine(Main.SavePath,"combat-ready.json"),JsonSerializer.Serialize(new{
            entityRef=NativeCombatWorld.Reference(Bound(p),target),initial=new{p.position.X,p.position.Y,p.statLife,p.selectedItem,
            arrows=p.inventory[54].stack,sword=p.inventory[0].type,bow=p.inventory[2].type,dx=target.Center.X-origin.X,pattern}}));
    }

    private static string defenseCase;
    private static ulong defenseStart;
    private static ulong? workBoltUpdate;
    private static readonly List<object> hostileSpawns=new();
    internal static void HostileSpawn(Projectile q,Terraria.DataStructures.IEntitySource source)
    {
        if(defenseCase!=null && source is Terraria.DataStructures.EntitySource_Parent {Entity:NPC n})
            hostileSpawns.Add(new{tick=Main.GameUpdateCount,n.type,n.aiStyle,projectile=q.type,q.velocity.X,q.velocity.Y});
    }

    private static void SampleDefense(Player p)
    {
        ulong tick=Main.GameUpdateCount;
        if(hazard==null && (defenseCase=="mine" && VirtualPlayerEngineProbe.ActorMining.Current is {Swings:>0} && p.itemAnimation>0
            || defenseCase=="travel" && VirtualPlayerEngineProbe.ActorControl.Running && p.velocity.X>.5f && p.velocity.Y==0))
        {
            workBoltUpdate=tick;
            hazard=LinearBolt(p,p.GetSource_Misc("isolated_work_bolt"),0,-6);
        }
        if(defenseCase=="boundary" && target.active)
            target.Bottom=p.Bottom+new Vector2((tick-defenseStart)/8%2==0?-92:-124,0);
        samples.Add(new{tick,x=p.Center.X,y=p.Center.Y,p.statLife,running=NativeCombatWorld.Running,
            distance=target?.active==true?(float?)Vector2.Distance(p.Center,target.Center):null,
            contact=target?.active==true?NativeCombatWorld.ContactTick(p,target,p.velocity.X):null,
            boltX=hazard?.active==true?(float?)hazard.Center.X:null,boltY=hazard?.active==true?(float?)hazard.Center.Y:null});
        if(tick-defenseStart<(defenseCase=="archer"?360u:180u))return;
        File.WriteAllText(Path.Combine(Main.SavePath,"self-"+defenseCase+".json"),JsonSerializer.Serialize(new{
            defenseStart,workBoltUpdate,samples,wounds,hostileSpawns,firstPress,hitTrace,projectileHits,hitViolations}));
        // End the observation window for surviving native NPCs.
        if(target!=null)target.active=false;
        foreach(var n in defenseTargets)n.active=false;
        defenseCase=null;
        Baseline=false;
    }

    private static void SelfDefenseScene(Player p,string mode)
    {
        foreach(var n in defenseTargets)n.active=false;
        defenseTargets.Clear();
        if(target!=null)target.active=false;
        target=null;hazard=null;
        foreach(var q in Main.ActiveProjectiles)q.active=false;
        int x=Main.spawnTileX,floor=Main.spawnTileY+8;
        for(int dx=-36;dx<=36;dx++)for(int dy=-18;dy<=2;dy++)
        {
            var tile=Main.tile[x+dx,floor+dy];
            tile.ClearEverything();
            if(dy>=0 || mode=="wall" && dx==-7 || mode is "blocked" or "through" && dx==6)
            {tile.HasTile=true;tile.TileType=TileID.Stone;}
        }
        Main.dayTime=false;Main.time=1800;
        Main.rand=new UnifiedRandom(817);
        p.Bottom=new(x*16+10,floor*16);p.velocity=Vector2.Zero;p.direction=-1;
        p.ConsumedLifeCrystals=10;p.statLife=p.statLifeMax=p.statLifeMax2=300;
        p.immune=false;p.immuneTime=0;Array.Clear(p.hurtCooldowns);
        p.jump=0;p.releaseJump=true;p.itemAnimation=p.itemTime=p.reuseDelay=0;p.releaseUseItem=true;
        for(int i=0;i<58;i++)p.inventory[i].TurnToAir();
        p.selectedItem=0;
        NativeCombatWorld.ClearLoadout();
        if(mode is "ground" or "flyer" or "archer" or "mixed" or "wall" or "empty" or "boundary")
            p.inventory[0].SetDefaults(ItemID.Minishark);
        if(mode=="no-mana"){p.inventory[0].SetDefaults(ItemID.AmethystStaff);ManaPool(p,20,0);suppressManaRegen=true;}
        if(mode!="empty"){p.inventory[54].SetDefaults(ItemID.MusketBall);p.inventory[54].stack=999;}
        // A stream of Minishark hits can interrupt the archer's native draw.
        // Give the actor one musket round, then observe the full enemy attack.
        if(mode=="archer"){p.inventory[0].SetDefaults(ItemID.Musket);p.inventory[54].stack=1;}
        if(mode=="mine")
        {
            p.inventory[1].SetDefaults(ItemID.CopperPickaxe);
            var ore=Main.tile[x+3,floor-1];ore.HasTile=true;ore.TileType=TileID.Stone;
        }
        samples.Clear();wounds.Clear();hostileSpawns.Clear();hitTrace.Clear();projectileHits.Clear();shots.Clear();
        workBoltUpdate=firstPress=null;hitViolations=0;
        defenseCase=mode;defenseStart=Main.GameUpdateCount;Baseline=mode is "bolt-off" or "charge-off";
        NPC Spawn(int type,int dx)
        {
            int slot=NPC.NewNPC(p.GetSource_Misc("isolated_self_defense"),(int)p.Center.X+dx,floor*16,type,Target:p.whoAmI);
            var n=Main.npc[slot];
            n.Bottom=p.Bottom+new Vector2(dx,type==NPCID.DemonEye?-32:0);n.target=p.whoAmI;
            defenseTargets.Add(n);
            return n;
        }
        if(mode is "ground" or "flyer" or "mixed" or "archer" or "wall" or "empty" or "no-mana" or "boundary" or "charge-off" or "charge-on")
        {
            bool archer=mode is "archer" or "wall";
            target=Spawn(archer?NPCID.SkeletonArcher:mode=="flyer"?NPCID.DemonEye:NPCID.Zombie,archer?-248:-88);
            if(archer){target.life=target.lifeMax=500;target.knockBackResist=0;}
            if(mode=="mixed")target.life=18;
            if(mode=="boundary"){target.aiStyle=-1;target.life=target.lifeMax=500;}
            if(mode is "charge-off" or "charge-on")
            {target.aiStyle=-1;target.velocity.X=4;target.noGravity=true;target.knockBackResist=0;}
        }
        bool sourceDead=false;
        if(mode is "bolt-off" or "bolt-on" or "dead" or "mixed" or "friendly" or "away" or "miss" or "empty" or "no-mana" or "blocked" or "through")
        {
            NPC shooter=mode is "mixed" or "dead"?Spawn(NPCID.Zombie,270):null;
            if(mode=="mixed"){shooter.aiStyle=-1;shooter.target=255;}
            var source=shooter?.GetSource_FromAI()??p.GetSource_Misc("isolated_linear_bolt");
            hazard=LinearBolt(p,source,mode=="miss"?-100:10,mode=="away"?6:-6);
            hazard.tileCollide=mode=="blocked";
            if(mode=="friendly"){hazard.hostile=false;hazard.friendly=true;}
            if(mode=="dead"){shooter.StrikeNPC(new NPC.HitInfo{Damage=9999});sourceDead=!shooter.active;}
        }
        File.WriteAllText(Path.Combine(Main.SavePath,"combat-ready.json"),JsonSerializer.Serialize(new{mode,defenseStart,
            entityRef=target==null?null:NativeCombatWorld.Reference(Bound(p),target),aiStyle=target?.aiStyle,
            distance=target==null?(float?)null:Vector2.Distance(target.Center,p.Center),
            contact=target==null?null:NativeCombatWorld.ContactTick(p,target,0),boltX=hazard?.Center.X,playerX=p.Center.X,
            sourceDead,workGoal=mode=="mine"?new{x=x+3,y=floor-1}:new{x=(x+24)*16,y=floor*16}}));
    }

    private static Projectile LinearBolt(Player p,Terraria.DataStructures.IEntitySource source,int height,int vx)
    {
        int i=Projectile.NewProjectile(source,p.Center+new Vector2(176,height),new Vector2(vx,0),ProjectileID.DeathLaser,16,0,255);
        var bolt=Main.projectile[i];bolt.aiStyle=-1;bolt.extraUpdates=0;bolt.tileCollide=false;
        return bolt;
    }
}
