using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using CompanionBridge.Motion;
using Terraria.ModLoader;

namespace CompanionBridge.ServerIpc;

internal static partial class ServerCombatFixture
{
    private static bool whole,observedCombat;
    private static ulong setupTick;
    private static void WholeTick(Player p)
    {
        if(!whole)return;
        ulong elapsed=Main.GameUpdateCount-setupTick;
        if(MobilityArena && target?.active==true)
        {
            // Copied-world contact sweep, independent of actor position and equipment.
            int phase=(int)(elapsed%120);
            target.Center=origin+new Vector2(-240+8*(phase<=60?phase:120-phase),0);
            target.velocity=new(phase<60?8:-8,0);
        }
        if(MobilityArena && elapsed==3)
        {
            var m=NativeMovementReader.Capture(p);
            File.WriteAllText(Path.Combine(Main.SavePath,"mobility-profile.json"),JsonSerializer.Serialize(new{
                physics=NativeNavigation.Parameters(p).ToString(),m.Wings,m.Rockets,
                m.ExtraJumps,p.slowFall,p.chilled,p.empressBrooch,p.autoJump,p.windPushed,p.powerrun,p.grapCount,
                p.statLife,p.wet,p.itemAnimation,p.armor[6].type}));
        }
        if(target!=null && elapsed%120==0)File.AppendAllText(Path.Combine(Main.SavePath,"whole-observation.jsonl"),
            JsonSerializer.Serialize(new{elapsed,p.ZoneCorrupt,p.statLife,target.active,target.life,dx=target.Center.X-p.Center.X,dy=target.Center.Y-p.Center.Y})+Environment.NewLine);
        if(NativeCombatWorld.Running)observedCombat=true;
        else if(observedCombat)
        {
            File.WriteAllText(Path.Combine(Main.SavePath,"boss-finished.json"),JsonSerializer.Serialize(new{
                tick=Main.GameUpdateCount,p.dead,p.statLife,target.life,target.active,hits,verifiedShots}));
            observedCombat=false;
        }
    }
    internal static void WholeTerminal(string id,string status,string reason)
    {
        if(Measuring)ArenaEnd(VirtualPlayerEngineProbe.ProbeSession.Actor);
        if(whole)File.WriteAllText(Path.Combine(Main.SavePath,"whole-terminal.json"),JsonSerializer.Serialize(new{id,status,reason}));
    }
    private static void WholeScene(Player p,string mode)
    {
        object result;
        if(mode=="whole-kit" || mode.StartsWith("whole-kit-"))
        {
            arenaArm=mode=="whole-kit"?null:mode.Substring(10);
            int x=Main.spawnTileX,y=Main.spawnTileY+8;
            for(int i=x-110;i<=x+110;i++)for(int j=y-22;j<=y+5;j++)
            {
                var t=Main.tile[i,j];t.ClearEverything();
                if((arenaArm is "platform" or "shield" && (j==y-4 || j==y-8)) || MobilityArena && j==y-8){t.HasTile=true;t.TileType=TileID.Platforms;}
                if(j>=y){t.HasTile=true;t.TileType=TileID.Ebonstone;}
            }
            p.ConsumedLifeCrystals=15;p.ConsumedLifeFruit=20;p.statLife=p.statLifeMax=p.statLifeMax2=500;
            p.armor[0].SetDefaults(ItemID.VortexHelmet);p.armor[1].SetDefaults(ItemID.VortexBreastplate);p.armor[2].SetDefaults(ItemID.VortexLeggings);
            p.armor[3].SetDefaults(ItemID.RangerEmblem);p.armor[4].SetDefaults(ItemID.DestroyerEmblem);p.armor[5].SetDefaults(arenaArm=="shield"?ItemID.CobaltShield:ItemID.AvengerEmblem);
            if(MobilityArena)
            {
                // Use the stock ground-speed profile shared by the pinned flight model.
                for(int slot=0;slot<3;slot++)p.armor[slot].TurnToAir();
                p.armor[6].SetDefaults(arenaArm=="cloud"?ItemID.CloudinaBottle:arenaArm=="wings"?ItemID.AngelWings:0);
            }
            p.inventory[0].SetDefaults(ItemID.BreakerBlade);p.inventory[2].SetDefaults(ItemID.Handgun);
            p.inventory[54].SetDefaults(ItemID.MusketBall);p.inventory[54].stack=9999;
            Main.dayTime=false;Main.time=1000;
            result=new{p.statLife};
        }
        else
        {
            int type=mode.Substring(6) switch {"eye"=>4,"skeleton"=>35,"eater"=>13,"destroyer"=>134,"twins"=>125,"moon"=>398,
                "sample" or "retreat" or "cancel"=>ModContent.Find<ModNPC>("BossStrategySample/Root").Type,_=>throw new ArgumentException(mode)};
            if(arenaArm!=null){Main.rand=new Terraria.Utilities.UnifiedRandom(9817);Main.time=1000;}
            whole=true;setupTick=Main.GameUpdateCount;
            // Dedicated clients normally transmit biome flags. Seed the corresponding
            // native corruption-arena flag in this copied-world scenario.
            if(type==NPCID.EaterofWorldsHead)p.ZoneCorrupt=true;
            int x=(int)p.Center.X-230,y=(int)p.Center.Y-20;
            target=Main.npc[NPC.NewNPC(p.GetSource_Misc("whole_encounter"),x,y,type,Start:2,Target:p.whoAmI)];
            if(MobilityArena)
            {
                origin=p.Center;target.aiStyle=-1;target.noGravity=target.noTileCollide=true;
                target.damage=target.defDamage=16;target.height=216;target.knockBackResist=0;target.life=target.lifeMax=50000;
            }
            if(type==125)NPC.NewNPC(p.GetSource_Misc("whole_encounter"),x+30,y-30,126,Start:2,Target:p.whoAmI);
            result=new{target.life,target.lifeMax,target.type,feetX=(int)p.Bottom.X+240,feetY=(int)p.Bottom.Y};
        }
        File.WriteAllText(Path.Combine(Main.SavePath,"combat-ready.json"),JsonSerializer.Serialize(result));
    }
}
