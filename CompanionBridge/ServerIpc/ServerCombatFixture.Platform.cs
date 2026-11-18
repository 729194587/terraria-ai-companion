using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CompanionBridge.Motion;
using Terraria;
using CompanionBridge.Common.Systems;

namespace CompanionBridge.ServerIpc;

internal static partial class ServerCombatFixture
{
    private static string arenaArm;
    private static bool arenaPrimed,previouslySupported;
    private static float? enemyX;
    private static float openingDistance;
    private static bool arenaReleased,arenaMeasured,downSinceSupport,jumpSinceSupport;
    private static int lastLayer,hurtCount,hurtTotal,arenaHits,arenaDamage;
    private static float previousX,platformRun;
    private static int direction;
    private static ulong? arenaStart;
    private static object initialPose;
    private static readonly List<object> changes=new();
    private static bool MobilityArena=>arenaArm is "bare" or "cloud" or "wings";
    private static int cloudStarts,wingSpent,abilityLandings,cloudPlans,wingPlans;
    private static bool cloudActive,awaitLanding;
    private static float previousWing;
    internal static void ArenaPlan(Prediction plan)
    {
        if(!Measuring || !MobilityArena || plan==null)return;
        if(plan.ExtraJumpFrame>=0)cloudPlans++;
        if(plan.AirStartFrame>=0)wingPlans++;
    }
    internal static bool ArenaPaused()
    {
        if(arenaArm==null || !whole || arenaReleased || Main.GameUpdateCount-setupTick<3)return false;
        if(!arenaPrimed)
        {
            File.WriteAllText(Path.Combine(Main.SavePath,"arena-ready.json"),"{}");
            arenaPrimed=true;
        }
        if(File.Exists(Path.Combine(Main.SavePath,"arena-release")))arenaReleased=true;
        return !arenaReleased;
    }
    internal static void ArenaAcquired(Player p)
    {
        if(arenaArm==null)return;
        arenaStart=Main.GameUpdateCount;
        previousWing=p.wingTime;lastLayer=0;previousX=p.position.X;
        initialPose=new{p.position.X,p.position.Y,p.statLife,vx=p.velocity.X,vy=p.velocity.Y,
            targetX=target.Center.X,targetY=target.Center.Y,target.life,ammo=p.inventory[54].stack,accessory=p.armor[5].type,mobilityAccessory=p.armor[6].type,
            spawnAge=Main.GameUpdateCount-setupTick};
    }
    private static bool Measuring=>arenaStart.HasValue && !arenaMeasured;
    internal static void ArenaInput(PhysicsInput input,NPC enemy)
    {
        if(!Measuring)return;
        enemyX=enemy?.Center.X;
        downSinceSupport|=input.Down;jumpSinceSupport|=input.Jump;direction=input.Direction;
    }
    internal static void ArenaHit(int amount){if(Measuring){arenaHits++;arenaDamage+=amount;}}
    internal static void ArenaAfter(Player p)
    {
        if(!Measuring)return;
        int floor=Main.spawnTileY+8;
        int row=(int)MathF.Round(p.Bottom.Y/16);
        int layer=(floor-row)/4;
        bool onArena=layer is >=0 and <=2 && row==floor-4*layer && (layer==0 || arenaArm!="flat")
            && Math.Abs(p.Bottom.Y-row*16)<.5f && NativeMotionWorld.Observe(p).Grounded;
        if(layer>0 && onArena)
            onArena=TerrainReader.ReadCell((int)(p.Center.X/16),row)=='=';
        if(MobilityArena)
        {
            var observed=NativeMotionWorld.Observe(p);
            if(!observed.Grounded && observed.ExtraActive && !cloudActive && observed.Vy<0){cloudStarts++;awaitLanding=true;}
            cloudActive=observed.ExtraActive;
            if(!observed.Grounded && previousWing>p.wingTime){wingSpent+=(int)(previousWing-p.wingTime);awaitLanding=true;}
            previousWing=p.wingTime;
            if(awaitLanding && onArena && !p.dead){abilityLandings++;awaitLanding=false;}
        }
        if(onArena && !p.dead)
        {
            if(lastLayer!=layer)
            {
                changes.Add(new{tick=Main.GameUpdateCount,from=lastLayer,to=layer,downSinceSupport,jumpSinceSupport});
                downSinceSupport=jumpSinceSupport=false;
            }
            else if(layer>0 && previouslySupported && direction*(p.position.X-previousX)>0)
            {
                float dx=p.position.X-previousX;
                platformRun+=Math.Abs(dx);
                if(enemyX is {} enemy && direction*(p.Center.X-dx-enemy)>0)openingDistance+=Math.Abs(dx);
            }
            if(p.velocity.Y==0){downSinceSupport=jumpSinceSupport=false;}
            lastLayer=layer;
        }
        previouslySupported=onArena;previousX=p.position.X;direction=0;enemyX=null;
        if(p.dead || Main.GameUpdateCount-arenaStart.Value+1>=1200)ArenaEnd(p);
    }
    private static void ArenaEnd(Player p)
    {
        if(!Measuring)return;
        arenaMeasured=true;
        File.WriteAllText(Path.Combine(Main.SavePath,"arena-result.json"),JsonSerializer.Serialize(new{
            arenaArm,initialPose,cloudPlans,wingPlans,cloudStarts,wingSpent,abilityLandings,updates=Main.GameUpdateCount-arenaStart.Value+1,changes,platformRun,openingDistance,hurtCount,hurtTotal,
            arenaHits,arenaDamage,p.statLife,p.dead}));
    }
}
