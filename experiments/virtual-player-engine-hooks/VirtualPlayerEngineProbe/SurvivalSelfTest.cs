using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;

namespace VirtualPlayerEngineProbe;

// Actual native APIs in an explicit isolated diagnostic profile, not a live client test.
internal static class SurvivalSelfTest
{
    private static bool ran;
    private static void Check(bool condition,string message) { if(!condition)throw new InvalidOperationException(message); }
    private static void Vulnerable(Player p) { p.immune=false;p.immuneTime=0;Array.Clear(p.hurtCooldowns);p.velocity=Vector2.Zero; }
    internal static void RunIfRequested()
    {
        if(ran||!Environment.GetCommandLineArgs().Contains("-survivalselftest"))return;
        ran=true;string report=Path.Combine(Main.SavePath,"survival-native-result.json");
        try
        {
            Main.gameMenu=false; // Explicit isolated server-API test, before its normal first world frame.
            ProbeSession.Spawn();var p=ProbeSession.Actor;CharacterSelfTest.Seed();
            var grab=typeof(Player).GetMethod("GrabItems",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public);
            int Drop(int count)
            {
                int i=Item.NewItem(new EntitySource_Misc("survival-selftest"),p.Hitbox,ItemID.StoneBlock,count);
                Main.item[i].playerIndexTheItemIsReservedFor=p.whoAmI;Main.item[i].noGrabDelay=0;
                return i;
            }
            int Count()=>p.inventory.Where(i=>i.type==ItemID.StoneBlock).Sum(i=>i.stack);
            int initial=Count();int item=Drop(7);
            NativeSurvival.Run(p,()=>grab.Invoke(p,new object[]{p.whoAmI}));
            Check(Count()==initial+7&&Main.item[item].IsAir,"Native pickup did not transfer 7 items exactly once.");
            var inventory=p.inventory.Select(i=>i.Clone()).ToArray();
            foreach(var i in p.inventory){i.SetDefaults(ItemID.DirtBlock);i.stack=i.maxStack;}
            p.inventory[0].SetDefaults(ItemID.StoneBlock);p.inventory[0].stack=p.inventory[0].maxStack-2;
            item=Drop(7);int max=p.inventory[0].maxStack;
            NativeSurvival.Run(p,()=>grab.Invoke(p,new object[]{p.whoAmI}));
            Check(p.inventory[0].stack==max&&Main.item[item].stack==5,"Partial stack pickup lost/duplicated items.");
            NativeSurvival.Run(p,()=>grab.Invoke(p,new object[]{p.whoAmI}));
            Check(Main.item[item].stack==5,"Full inventory consumed a world item.");
            Main.item[item].TurnToAir();p.inventory=inventory;
            int npc=NPC.NewNPC(new EntitySource_Misc("survival-selftest"),(int)p.position.X,(int)p.position.Y,NPCID.Zombie);
            var enemy=Main.npc[npc];enemy.position=p.position;enemy.damage=40;enemy.friendly=false;
            var collision=typeof(Player).GetMethod("Update_NPCCollision",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public);
            Vulnerable(p);int life=p.statLife;
            NativeSurvival.Run(p,()=>collision.Invoke(p,null));
            Check(p.statLife<life&&p.immuneTime>0&&p.velocity.X!=0,"Native NPC contact damage/immunity/knockback did not run.");
            int hurtLife=p.statLife;NativeSurvival.Run(p,()=>collision.Invoke(p,null));
            Check(p.statLife==hurtLife,"NPC contact bypassed native immunity frames.");enemy.active=false;
            Vulnerable(p);life=p.statLife;
            int proj=Projectile.NewProjectile(new EntitySource_Misc("survival-selftest"),p.Center,Vector2.Zero,ProjectileID.DeathLaser,10,0,Main.myPlayer);
            Main.projectile[proj].hostile=true;Main.projectile[proj].friendly=false;
            Main.projectile[proj].Damage();
            Check(p.statLife<life,"Native hostile projectile adapter did not cause damage.");Main.projectile[proj].active=false;
            int mode=Main.GameMode;Main.GameMode=0;
            int normal=NativeSurvival.ExpectedRespawn(p);Main.GameMode=1;int expert=NativeSurvival.ExpectedRespawn(p);
            int boss=NPC.NewNPC(new EntitySource_Misc("survival-selftest"),(int)p.Center.X+100,(int)p.Center.Y,NPCID.EyeofCthulhu);
            Main.npc[boss].boss=true;int expertBoss=NativeSurvival.ExpectedRespawn(p);
            Main.GameMode=0;int normalBoss=NativeSurvival.ExpectedRespawn(p);Main.npc[boss].active=false;Main.GameMode=mode;
            Check(normal==600&&expert==900&&normalBoss==1200&&expertBoss==1800,"Native mode/boss respawn rules differ from audited DLL.");
            Vulnerable(p);p.statLife=10;long generation=ProbeSession.ActorGeneration;
            ActorControl.Start(p,generation,ProbeSession.WorldSession);
            npc=NPC.NewNPC(new EntitySource_Misc("survival-selftest"),(int)p.position.X,(int)p.position.Y,NPCID.Zombie);
            enemy=Main.npc[npc];enemy.position=p.position;enemy.damage=5000;enemy.friendly=false;
            int expected=NativeSurvival.ExpectedRespawn(p);
            NativeSurvival.Run(p,()=>collision.Invoke(p,null));enemy.active=false;
            Check(p.dead&&p.active&&p.respawnTimer==expected&&ActorControl.Current==null,"Native death did not retain AI/cancel control/set original timer.");
            NetworkStateSync.Full(p);
            Check(p.dead&&p.respawnTimer==expected,"Dead/late-join native snapshot changed server death state.");
            var dead=typeof(Player).GetMethod("UpdateDead",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public);
            for(int tick=0;tick<expected-1;tick++)NativeSurvival.Update((player,index)=>dead.Invoke(player,null),p,p.whoAmI);
            Check(p.dead&&p.respawnTimer==1,"Respawn occurred before the native player timer elapsed.");
            NativeSurvival.Update((player,index)=>dead.Invoke(player,null),p,p.whoAmI);
            Check(!p.dead&&p.active&&p.statLife>0&&ReferenceEquals(Main.player[p.whoAmI],p)
                &&ProbeSession.ActorGeneration==generation+1,"Native timed respawn failed identity or incarnation checks.");
            string id=CharacterStore.Save();ProbeSession.Finish("native survival selftest",true);CharacterStore.Load(id);
            Check(ProbeSession.Actor.inventory.Any(i=>i.type==ItemID.StoneBlock&&i.stack==7),"Picked-up inventory did not survive native character save/load.");
            ProbeSession.Finish("native survival selftest complete",true);
            File.WriteAllText(report,JsonSerializer.Serialize(new {result="passed",nativePickup=true,partialStack=true,fullInventory=true,
                npcContact=true,immunity=true,hostileProjectile=true,normal,expert,normalBoss,expertBoss,
                deathTimer=expected,respawned=true,persistence=true,hostAndPlay="not executed",remoteMultiplayer="not executed"}));
            Netplay.Disconnect=true;
        }
        catch(Exception e)
        {
            File.WriteAllText(report,JsonSerializer.Serialize(new {result="failed",error=e.ToString(),fault=VirtualPlayerEngineProbe.Fault}));
            ProbeSession.Log("survival-selftest-failed",e.ToString());
        }
    }
}
