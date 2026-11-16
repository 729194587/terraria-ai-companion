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
    internal static bool Deny;
    internal static Action Reenter;
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
            var pointer=(Main.screenPosition,Main.mouseX,Main.mouseY,Main.lastMouseX,Main.lastMouseY,Main.mouseItem,
                Main.mouseLeft,Main.mouseLeftRelease,Main.mouseRight,Main.mouseRightRelease,p.mouseInterface);
            bool PointerRestored()=>pointer==(Main.screenPosition,Main.mouseX,Main.mouseY,Main.lastMouseX,Main.lastMouseY,Main.mouseItem,
                Main.mouseLeft,Main.mouseLeftRelease,Main.mouseRight,Main.mouseRightRelease,p.mouseInterface)
                &&Main.myPlayer==local&&Player.tileTargetX==tx&&Player.tileTargetY==ty&&Main.blockMouse==mouse&&!NativeItemInput.Active(p);
            string world=ProbeSession.WorldSession;long gen=ProbeSession.ActorGeneration;
            // Explicit diagnostic inventory, independent of normal starter gear.
            for(int i=0;i<10;i++)p.inventory[i].TurnToAir();
            ActorItemUse.Fixture(p,world,gen);
            p.inventory[3].SetDefaults(ItemID.CopperAxe);
            if(failure)
            {
                ActorItemUse.Select(p,world,gen,0);ActorItemUse.Start(p,world,gen);
                bool attempted=false;int humanNativeCalls=0;
                void ObserveItemCheck(On_Player.orig_ItemCheck orig,Player player)
                {
                    if(ReferenceEquals(player,human))humanNativeCalls++;
                    orig(player);
                }
                Reenter=()=>{
                    Check(NativeItemInput.Active(p)&&Main.myPlayer==p.whoAmI&&!ReferenceEquals(p,human),
                        "Cross-player probe did not run inside the AI input scope.");
                    attempted=true;
                    wrapped.Invoke(human,new object[]{0});
                };
                Exception error=null;
                On_Player.ItemCheck+=ObserveItemCheck;
                try{Frame();}catch(Exception e){error=e.GetBaseException();}
                finally{On_Player.ItemCheck-=ObserveItemCheck;Reenter=null;}
                Check(attempted&&humanNativeCalls==0
                    &&error is InvalidOperationException{Message:"Reentrant native item update during an AI input scope."},
                    "Nested human wrapper reached native item execution or was not rejected by the scope guard.");
                Check(ProbeSession.Quarantined&&!ActorItemUse.Running&&!p.controlUseItem&&p.releaseUseItem&&p.itemAnimation==0
                    &&ReferenceEquals(Main.player[p.whoAmI],p)&&ProbeSession.Reservation.IsReserved(p.whoAmI)&&p.inventory[0].stack==1,
                    "Reentry exception did not retain/quarantine AI and release inputs.");
                Check(PointerRestored(),"Reentry exception did not restore virtual input state.");
                bool humanAfter=false;
                NativeItemInput.Update((player,index)=>humanAfter=ReferenceEquals(player,human)&&index==0,human,0);
                Check(humanAfter&&PointerRestored(),"Normal human dispatch did not resume after reentry unwound.");
                File.WriteAllText(report,JsonSerializer.Serialize(new{result="passed",nativeExceptionQuarantine=true,
                    crossPlayerReentryRejected=true,humanNativeCalls,virtualInputRestored=true,humanAfter,
                    processTerminationRequired=true,hostAndPlay="not executed",remoteMultiplayer="not executed"}));
                return; // Quarantine intentionally forbids in-process unload/recovery.
            }

            bool humanPath=false;NativeItemInput.Update((player,index)=>humanPath=true,human,0);
            Check(humanPath&&!ActorItemUse.Allowed(human)&&!ActorItemUse.Allowed(p),"Human wrapper or out-of-scope identity changed.");
            bool stale=false;try{ActorItemUse.Start(p,world,gen-1);}catch(InvalidOperationException){stale=true;}
            Check(stale,"Stale queued generation accepted.");
            object Exercise(int slot,int type,bool consumable=false)
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
                Check(consumable?p.inventory[slot].stack==before-1:p.inventory[slot].stack==before,"Wrong native consumption.");
                if(type==ItemID.Apple)Check(p.HasBuff(BuffID.WellFed),"Native food buff was skipped.");
                return new {type,c.Ticks,c.Starts,c.AnimationPeak,c.TimePeak,c.ToolPeak,c.UsesPeak,stackBefore=before,stackAfter=p.inventory[slot].stack,firstTickControls,firstPressPreserved=true};
            }
            var sword=Exercise(0,ItemID.WoodenSword);var pick=Exercise(1,ItemID.CopperPickaxe);var axe=Exercise(3,ItemID.CopperAxe);var apple=Exercise(2,ItemID.Apple,true);
            p.statLife=20;
            p.inventory[4].SetDefaults(ItemID.LesserHealingPotion);
            var potion=Exercise(4,ItemID.LesserHealingPotion,true);
            Check(p.statLife>20&&p.potionDelay>0,"native potion effect/cooldown missing");
            p.inventory[5].SetDefaults(ModContent.ItemType<ItemUseFixtureFood>());
            var modItem=Exercise(5,p.inventory[5].type,true);
            Check(p.HasBuff(BuffID.Regeneration),"ModItem native buff missing");
            p.inventory[6].SetDefaults(ItemID.WoodenBow);
            p.inventory[54].SetDefaults(ItemID.WoodenArrow);p.inventory[54].stack=5;
            ActorItemUse.Select(p,world,gen,6);
            bool bowRejected=false;
            try{ActorItemUse.Start(p,world,gen);}catch(InvalidOperationException){bowRejected=true;}
            Check(bowRejected&&p.inventory[54].stack==5,"untargeted bow consumed ammo");
            // Last item must exercise the native zero-stack cleanup without touching a human mouse.
            p.inventory[2].stack=1;var lastApple=Exercise(2,ItemID.Apple,true);
            Check(p.inventory[2].IsAir,"Native final consumable cleanup failed.");
            ActorItemUse.Select(p,world,gen,3);Deny=true;ActorItemUse.Start(p,world,gen);Frame();Deny=false;
            Check(ActorItemUse.Current.Phase=="rejected"&&ActorItemUse.Current.Starts==0&&p.itemAnimation==0
                &&!p.controlUseItem&&p.releaseUseItem,"Native copper axe CanUseItem denial bypassed or input retained.");
            ActorItemUse.Start(p,world,gen);Frame();ActorItemUse.Current.Ticks=180;Frame();
            Check(ActorItemUse.Current.Phase=="timeout"&&!p.controlUseItem&&p.releaseUseItem&&p.itemAnimation==0,"Copper axe timeout did not release input.");
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
            Check(PointerRestored()&&ReferenceEquals(human,Main.player[0])
                &&humanItems.Select((i,n)=>i.type==human.inventory[n].type&&i.stack==human.inventory[n].stack).All(v=>v),
                "Human/global state changed during the item wrapper.");
            // Player.Update also maintains the engine's tile target outside the item
            // wrapper. Check natural cadence separately from wrapper isolation.
            ActorItemUse.Start(p,world,gen);p.Update(p.whoAmI);
            Check(ActorItemUse.Current.Ticks==1&&ActorItemUse.Current.Starts==1&&p.controlUseItem,
                "Player.Update did not start exactly one native item frame.");
            p.Update(p.whoAmI);
            Check(ActorItemUse.Current.Ticks==2&&ActorItemUse.Current.Starts==1&&!p.controlUseItem,
                "Player.Update repeated the press or missed the second native item frame.");
            NativeSurvival.Debug(p,world,gen,true);
            Check(p.dead&&!ActorItemUse.Running&&!p.controlUseItem&&p.itemAnimation==0,"Death did not cancel use immediately.");
            Check(ProbeSession.StatusText().Contains($"AI 已死亡"),"Readable status did not report native death.");
            ProbeSession.Finish("G6 selftest complete",true);
            File.WriteAllText(report,JsonSerializer.Serialize(new{result="passed",sword,pick,axe,apple,lastApple,potion,modItem,bowRejected,explicitStop=true,deathStops=true,axeTimeout=true,axeCancel=true,
                humanUnchanged=true,naturalPlayerUpdate=true,nativeCanUseDenial=true,staleGeneration=true,itemReplacement=true,
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
            ItemUseSelfTest.Reenter?.Invoke();
            if(ItemUseSelfTest.Deny)return false;
        }
        return true;
    }
}

public sealed class ItemUseFixtureFood : ModItem
{
    public override bool IsLoadingEnabled(Mod mod)=>Environment.GetCommandLineArgs().Contains("-itemuseselftest");
    public override string Texture=>"Terraria/Images/Item_"+ItemID.Apple;
    public override void SetDefaults()
    {
        Item.CloneDefaults(ItemID.Apple);
        Item.buffType=BuffID.Regeneration;
    }
}
