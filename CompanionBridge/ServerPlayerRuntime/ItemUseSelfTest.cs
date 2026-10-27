using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Terraria;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;

namespace VirtualPlayerEngineProbe;

// Explicit private-profile server diagnostic; no graphical client is involved.
internal static class ItemUseSelfTest
{
    private static bool ran;
    internal static bool Deny,Throw;
    private static Player controlActor;
    private static readonly List<bool> controls=new();
    private static void ObserveControls(On_NetMessage.orig_SendData orig,int msg,int remote,int ignore,NetworkText text,int number,float a,float b,float c,int d,int e,int f)
    {
        orig(msg,remote,ignore,text,number,a,b,c,d,e,f);
        if(msg==MessageID.PlayerControls&&controlActor!=null&&number==controlActor.whoAmI)controls.Add(controlActor.controlUseItem);
    }
    private static void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
    internal static void RunIfRequested()
    {
        if(ran||!Environment.GetCommandLineArgs().Contains("-itemuseselftest"))return;
        bool failure=Environment.GetCommandLineArgs().Contains("-itemusefailureselftest");
        ran=true;string report=Path.Combine(Main.SavePath,"item-use-native-result.json");
        try
        {
            On_NetMessage.SendData+=ObserveControls;
            Main.gameMenu=false;ProbeSession.Spawn();var p=ProbeSession.Actor;
            var wrapped=typeof(Player).GetMethod("ItemCheckWrapped",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
            void Frame()=>NativeSurvival.Run(p,()=>wrapped.Invoke(p,new object[]{p.whoAmI}));
            var human=Main.player[0];var humanItems=human.inventory.Select(i=>i.Clone()).ToArray();
            int local=Main.myPlayer,tx=Player.tileTargetX,ty=Player.tileTargetY;bool mouse=Main.blockMouse;
            string world=ProbeSession.WorldSession;long gen=ProbeSession.ActorGeneration;
            // Explicit diagnostic inventory, independent of normal starter gear.
            for(int i=0;i<10;i++)p.inventory[i].TurnToAir();
            ActorItemUse.Fixture(p,world,gen);
            p.inventory[3].SetDefaults(ItemID.CopperAxe);
            if(failure)
            {
                ActorItemUse.Select(p,world,gen,0);ActorItemUse.Start(p,world,gen);Throw=true;
                bool isolated=false;try{Frame();}catch{isolated=true;}finally{Throw=false;}
                Check(isolated&&ProbeSession.Quarantined&&!ActorItemUse.Running&&!p.controlUseItem&&p.itemAnimation==0
                    &&ReferenceEquals(Main.player[p.whoAmI],p)&&ProbeSession.Reservation.IsReserved(p.whoAmI)&&p.inventory[0].stack==1,
                    "Native hook exception did not retain/quarantine AI and release inputs.");
                File.WriteAllText(report,JsonSerializer.Serialize(new{result="passed",nativeExceptionQuarantine=true,
                    processTerminationRequired=true,hostAndPlay="not executed",remoteMultiplayer="not executed"}));
                return; // Quarantine intentionally forbids in-process unload/recovery.
            }

            bool humanPath=false;ActorItemUse.Update((player,index)=>humanPath=true,human,0);
            Check(humanPath&&!ActorItemUse.Allowed(human)&&!ActorItemUse.Allowed(p),"Human wrapper or out-of-scope identity changed.");
            bool stale=false;try{ActorItemUse.Start(p,world,gen-1);}catch(InvalidOperationException){stale=true;}
            Check(stale,"Stale queued generation accepted.");
            object Exercise(int slot,int type)
            {
                ActorItemUse.Select(p,world,gen,slot);int before=p.HeldItem.stack;
                ActorItemUse.Start(p,world,gen);
                bool concurrent=false;try{ActorControl.Start(p,gen,world);}catch(InvalidOperationException){concurrent=true;}
                Check(concurrent,"Walking was allowed during item action.");
                // Reproduce the live first-use collision: force both periodic
                // controls and full snapshot due on the actual first press tick.
                typeof(NetworkStateSync).GetField("controlTick",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,unchecked(Main.GameUpdateCount-6UL));
                typeof(NetworkStateSync).GetField("fullTick",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,unchecked(Main.GameUpdateCount-300UL));
                controlActor=p;controls.Clear();Frame();
                Check(p.controlUseItem,"First press was released before end-of-world-tick publication.");
                NetworkStateSync.Tick(p);NetworkStateSync.Full(p);
                Check(controls.Count>=3&&controls.All(v=>v),"Periodic/full snapshot overwrote the first native press with a release.");
                int firstTickControls=controls.Count;controlActor=null;
                Check(ProbeSession.StatusText().Contains($"槽位 {p.whoAmI}")&&ProbeSession.StatusText().Contains("进行中"),"Readable status omitted actor or running item action.");
                for(int i=1;ActorItemUse.Running&&i<190;i++)
                {Frame();if(i==1)Check(!p.controlUseItem,"Single-use press was repeated on the next native item update.");}
                var c=ActorItemUse.Current;
                Check(c.Phase=="completed"&&c.Reason=="native_item_cycle_verified"&&c.Starts==1&&c.AnimationPeak>0&&p.itemAnimation==0&&p.itemTime==0&&!p.controlUseItem,
                    $"Native cycle failed for {type}: {c.Phase}/{c.Reason}, peaks={c.AnimationPeak}/{c.TimePeak}, starts={c.Starts}");
                Check(type==ItemID.Apple?p.inventory[slot].stack==before-1:p.inventory[slot].stack==before,"Wrong native consumption.");
                if(type==ItemID.Apple)Check(p.HasBuff(BuffID.WellFed),"Native food buff was skipped.");
                return new {type,c.Ticks,c.Starts,c.AnimationPeak,c.TimePeak,c.ToolPeak,c.UsesPeak,stackBefore=before,stackAfter=p.inventory[slot].stack,firstTickControls,firstPressPreserved=true};
            }
            var sword=Exercise(0,ItemID.WoodenSword);var pick=Exercise(1,ItemID.CopperPickaxe);var axe=Exercise(3,ItemID.CopperAxe);var apple=Exercise(2,ItemID.Apple);
            // Last item must exercise the native zero-stack cleanup without touching a human mouse.
            p.inventory[2].stack=1;var lastApple=Exercise(2,ItemID.Apple);
            Check(p.inventory[2].IsAir,"Native final consumable cleanup failed.");
            ActorItemUse.Select(p,world,gen,3);Deny=true;ActorItemUse.Start(p,world,gen);Frame();Deny=false;
            Check(ActorItemUse.Current.Phase=="rejected"&&ActorItemUse.Current.Starts==0&&p.itemAnimation==0
                &&!p.controlUseItem&&p.releaseUseItem,"Native copper axe CanUseItem denial bypassed or input retained.");
            ActorItemUse.Start(p,world,gen);Frame();ActorItemUse.Current.Ticks=180;Frame();
            Check(ActorItemUse.Current.Phase=="failed"&&!p.controlUseItem&&p.releaseUseItem&&p.itemAnimation==0,"Copper axe timeout did not release input.");
            ActorItemUse.Start(p,world,gen);Frame();ActorItemUse.Stop(p,world,gen);
            Check(ActorItemUse.Current.Phase=="cancelled"&&!p.controlUseItem&&p.releaseUseItem&&p.itemAnimation==0,"Copper axe cancel did not release input.");
            ActorItemUse.Select(p,world,gen,0);
            ActorItemUse.Start(p,world,gen);ActorItemUse.Current.Generation--;
            int animation=p.itemAnimation;Frame();
            Check(ActorItemUse.Current.Phase=="stale"&&!ProbeSession.Quarantined&&p.itemAnimation==animation,"Stale action mutated a new generation.");
            ActorItemUse.Start(p,world,gen);var held=p.HeldItem;var replacement=held.Clone();p.inventory[0]=replacement;Frame();
            Check(ActorItemUse.Current.Phase=="cancelled"&&ReferenceEquals(p.inventory[0],replacement)&&replacement.stack==1,"Replacement item was used or overwritten.");
            p.inventory[0]=held;
            ActorItemUse.Select(p,world,gen,0);ActorItemUse.Start(p,world,gen);Frame();ActorItemUse.Stop(p,world,gen);
            Check(!p.controlUseItem&&p.itemAnimation==0&&p.itemTime==0&&!ActorItemUse.Running,"Explicit stop did not release native use.");
            ActorItemUse.Start(p,world,gen);Frame();NativeSurvival.Debug(p,world,gen,true);
            Check(p.dead&&!ActorItemUse.Running&&!p.controlUseItem&&p.itemAnimation==0,"Death did not cancel use immediately.");
            Check(ProbeSession.StatusText().Contains("AI 已死亡"),"Readable status did not report native death.");
            Check(Main.myPlayer==local&&Main.blockMouse==mouse&&Player.tileTargetX==tx&&Player.tileTargetY==ty
                &&ReferenceEquals(human,Main.player[0])&&humanItems.Select((i,n)=>i.type==human.inventory[n].type&&i.stack==human.inventory[n].stack).All(v=>v),"Human/global state changed.");
            ProbeSession.Finish("G6 selftest complete",true);
            File.WriteAllText(report,JsonSerializer.Serialize(new{result="passed",sword,pick,axe,apple,lastApple,explicitStop=true,deathStops=true,axeTimeout=true,axeCancel=true,
                humanUnchanged=true,nativeCanUseDenial=true,staleGeneration=true,itemReplacement=true,
                hostAndPlay="not executed",remoteMultiplayer="not executed",mining="air animation only; no tile target"}));
        }
        catch(Exception e)
        {
            File.WriteAllText(report,JsonSerializer.Serialize(new{result="failed",error=e.ToString()}));
            VirtualPlayerEngineProbe.Instance?.Logger.Error("G6 native selftest failed",e);
        }
        finally{On_NetMessage.SendData-=ObserveControls;controlActor=null;controls.Clear();if(!failure)Netplay.Disconnect=true;}
    }
}

// Inert in normal play. Exercises native tML CanUseItem cancellation/exception
// paths only while the explicit private-profile selftest is running.
public sealed class ItemUseDiagnosticHook : GlobalItem
{
    public override bool CanUseItem(Item item,Player player)
    {
        if(NativeSurvival.Owns(player))
        {
            if(ItemUseSelfTest.Throw)throw new InvalidOperationException("G6 intentional native hook failure");
            if(ItemUseSelfTest.Deny)return false;
        }
        return true;
    }
}
