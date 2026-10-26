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

// Opt-in private copied world only. Calls the installed real native item entry.
internal static class MiningSelfTest
{
    private static bool ran;
    internal static bool DenyTile;
    private static int hitPackets,tilePackets,dropPackets;
    private static bool watchFirstPress;
    private static readonly List<bool> firstPressControls=new();
    private static void Packets(On_NetMessage.orig_SendData orig,int msg,int remote,int ignore,NetworkText text,int number,float x,float y,float extra,int n5,int n6,int n7)
    {
        orig(msg,remote,ignore,text,number,x,y,extra,n5,n6,n7);
        if(watchFirstPress&&msg==MessageID.PlayerControls&&number==ProbeSession.Actor?.whoAmI)firstPressControls.Add(ProbeSession.Actor.controlUseItem);
        if(msg==MessageID.SyncTilePicking&&number==ProbeSession.Actor?.whoAmI)hitPackets++;
        if(msg==MessageID.TileSquare)tilePackets++;
        if(msg==MessageID.SyncItem)dropPackets++;
    }
    internal static void RunIfRequested()
    {
        if(ran||!Environment.GetCommandLineArgs().Contains("-miningselftest"))return;
        ran=true;bool failure=Environment.GetCommandLineArgs().Contains("-miningfailureselftest");
        string report=Path.Combine(Main.SavePath,"mining-native-result.json");int checks=0;
        var evidence=new List<object>();
        void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);checks++;}
        try
        {
            On_NetMessage.SendData+=Packets;
            Main.gameMenu=false;ProbeSession.Spawn();var p=ProbeSession.Actor;
            string world=ProbeSession.WorldSession;long generation=ProbeSession.ActorGeneration;
            var wrapped=typeof(Player).GetMethod("ItemCheckWrapped",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public);
            void Frame()=>NativeSurvival.Run(p,()=>wrapped.Invoke(p,new object[]{p.whoAmI}));
            int x=(int)(p.position.X/16)+3,y=(int)(p.position.Y/16)+1;
            // Only the copied diagnostic world is changed; never invoked in normal play.
            for(int dx=-2;dx<=2;dx++)for(int dy=-2;dy<=2;dy++)Main.tile[x+dx,y+dy].ClearTile();
            void Target(int type)
            {
                var tile=Main.tile[x,y];tile.ClearTile();tile.HasTile=true;tile.TileType=(ushort)type;
                if(type!=TileID.Sand)WorldGen.SquareTileFrame(x,y);p.hitTile.ClearThisTile(x,y);
            }
            p.inventory[0].SetDefaults(ItemID.CopperPickaxe);p.selectedItem=0;
            var human=Main.player[0];var humanItems=human.inventory.Select(i=>(i.type,i.stack)).ToArray();
            int local=Main.myPlayer,tx=Player.tileTargetX,ty=Player.tileTargetY;bool mouse=Main.blockMouse;
            Target(TileID.Stone);
            void Start()=>ActorMining.Start(p,world,generation,x,y);
            if(failure)
            {
                Start();ItemUseSelfTest.Throw=true;bool threw=false;
                try{Frame();}catch{threw=true;}finally{ItemUseSelfTest.Throw=false;}
                Check(threw&&ProbeSession.Quarantined&&!ActorMining.Running&&!p.controlUseItem&&p.itemAnimation==0
                    &&ProbeSession.Reservation.IsReserved(p.whoAmI)&&ReferenceEquals(Main.player[p.whoAmI],p),"Native exception did not release/quarantine/retain reservation.");
                File.WriteAllText(report,JsonSerializer.Serialize(new{result="passed",checks,nativeExceptionQuarantine=true,processTerminationRequired=true}));return;
            }
            bool stale=false;try{ActorMining.Start(p,world,generation-1,x,y);}catch(InvalidOperationException){stale=true;}
            Check(stale,"Queued stale incarnation accepted.");
            Main.tile[x+100,y].ClearTile();ActorMining.Start(p,world,generation,x+100,y);Check(ActorMining.Current.Reason==ActorMining.Failure.InvalidTarget,"Empty target accepted.");
            ActorMining.Start(p,world,generation,-1,y);Check(ActorMining.Current.Reason==ActorMining.Failure.InvalidTarget,"World edge accepted.");
            Target(TileID.Stone);int distant=x+30;var far=Main.tile[distant,y];far.HasTile=true;far.TileType=TileID.Stone;
            ActorMining.Start(p,world,generation,distant,y);Check(ActorMining.Current.Reason==ActorMining.Failure.OutOfRange,"Out of range target accepted.");
            Target(TileID.Sand);Start();Check(ActorMining.Current.Reason==ActorMining.Failure.UnsupportedTarget,"Falling tile accepted.");
            Target(TileID.Stone);var pick=p.HeldItem;p.inventory[0]=new Item(ItemID.WoodenSword);Start();
            Check(ActorMining.Current.Reason==ActorMining.Failure.InvalidTool,"Sword accepted as pickaxe.");p.inventory[0]=pick;
            object Mine(int type)
            {
                Target(type);var adjacent=Main.tile[x+1,y];adjacent.HasTile=true;adjacent.TileType=TileID.Stone;
                // Cuttable grass in the swing rectangle must survive G7.
                var grass=Main.tile[x-1,y];grass.HasTile=true;grass.TileType=TileID.Plants;
                var support=Main.tile[x-1,y+1];support.HasTile=true;support.TileType=TileID.Grass;
                WorldGen.SquareTileFrame(x-1,y);Check(grass.HasTile,"Diagnostic grass fixture lacks native support.");
                int[] bag=p.inventory.Select(i=>i.stack).ToArray();int drops=Main.item.Count(i=>i.active&&i.type==ItemID.StoneBlock);
                Start();bool walk=false,use=false,slot=false,save=false;
                try{ActorControl.Start(p,generation,world);}catch(InvalidOperationException){walk=true;}
                try{ActorItemUse.Start(p,world,generation);}catch(InvalidOperationException){use=true;}
                try{ActorItemUse.Select(p,world,generation,1);}catch(InvalidOperationException){slot=true;}
                try{CharacterStore.Save(null);}catch(InvalidOperationException){save=true;}
                Check(walk&&use&&slot&&save,"Mine did not exclude walk/use/selection/save.");
                bool progress=false,earlyCompletion=false;
                int firstTickControls=0;
                for(int i=0;ActorMining.Running&&i<610;i++)
                {
                    if(i==0)
                    {
                        typeof(NetworkStateSync).GetField("controlTick",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,unchecked(Main.GameUpdateCount-6UL));
                        typeof(NetworkStateSync).GetField("fullTick",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,unchecked(Main.GameUpdateCount-300UL));
                        watchFirstPress=true;firstPressControls.Clear();
                    }
                    Frame();var a=ActorMining.Current;
                    if(i==0)
                    {
                        NetworkStateSync.Tick(p);NetworkStateSync.Full(p);watchFirstPress=false;
                        Check(firstPressControls.Count>=3&&firstPressControls.All(v=>v),"Mining first press was overwritten by periodic/full controls.");firstTickControls=firstPressControls.Count;
                    }
                    if(a.PeakDamage>0&&a.PeakDamage<100&&Main.tile[x,y].HasTile){progress=true;earlyCompletion|=a.Phase==ActionPhase.Completed;}
                }
                var c=ActorMining.Current;
                Check(c.Phase==ActionPhase.Completed&&c.Destroyed&&!Main.tile[x,y].HasTile,
                    $"No attributed native destruction: {ActorMining.Summary()}");
                Check(progress&&!earlyCompletion&&c.Swings>0&&c.Strikes>1&&c.PeakDamage>=100,"Swing/progress/destruction were conflated.");
                Check(Main.tile[x+1,y].HasTile&&Main.tile[x+1,y].TileType==TileID.Stone&&Main.tile[x-1,y].HasTile,
                    $"Neighbor tile or grass was mined/cut: stone={Main.tile[x+1,y].HasTile}, grass={Main.tile[x-1,y].HasTile}.");
                Check(!p.controlUseItem&&p.itemAnimation==0&&p.itemTime==0,"Completion left inputs/timers active.");
                Check(bag.SequenceEqual(p.inventory.Select(i=>i.stack)),"Mining automatically collected or changed inventory.");
                Check(Main.item.Count(i=>i.active&&i.type==ItemID.StoneBlock)>drops,"No real world stone drop was created.");
                Check(hitPackets>0&&tilePackets>0&&dropPackets>0,"Native progress/tile/drop publication was not observed.");
                return new{actionId=c.Id,c.X,c.Y,c.TileType,c.Ticks,c.Swings,c.Strikes,c.PeakDamage,c.AnimationPeak,c.Destroyed,progressBeforeDestruction=progress,worldDrop=true,hitPackets,tilePackets,dropPackets,firstTickControls};
            }
            evidence.Add(Mine(TileID.Stone));
            Target(TileID.Stone);DenyTile=true;try{Start();Frame();}finally{DenyTile=false;}
            Check(ActorMining.Current.Reason==ActorMining.Failure.NoPickPower&&Main.tile[x,y].HasTile,"Native GlobalTile protection hook bypassed.");
            Target(TileID.Obsidian);Start();Frame();Check(ActorMining.Current.Phase==ActionPhase.Failed
                &&ActorMining.Current.Reason==ActorMining.Failure.NoPickPower&&Main.tile[x,y].HasTile,"Copper pick bypassed obsidian power rule.");
            Target(TileID.Stone);Start();Frame();ActorMining.Stop(p,world,generation);
            Check(ActorMining.Current.Phase==ActionPhase.Cancelled&&!p.controlUseItem&&p.itemAnimation==0&&Main.tile[x,y].HasTile,"Stop did not cancel a partial mine.");
            Start();ItemUseSelfTest.Deny=true;try{Frame();}finally{ItemUseSelfTest.Deny=false;}
            Check(ActorMining.Current.Reason==ActorMining.Failure.NativeRefused&&Main.tile[x,y].HasTile,"Native CanUseItem denial bypassed.");
            Start();Frame();Main.tile[x,y].TileType=TileID.Dirt;Frame();
            Check(ActorMining.Current.Reason==ActorMining.Failure.TargetChanged&&Main.tile[x,y].HasTile,"External tile replacement falsely completed.");
            Target(TileID.Stone);Start();Frame();WorldGen.KillTile(x,y);Target(TileID.Stone);Frame();
            Check(ActorMining.Current.Reason==ActorMining.Failure.TargetChanged&&Main.tile[x,y].HasTile,"Same-type remove/replacement was adopted.");
            Target(TileID.Stone);Start();Frame();var replacement=pick.Clone();p.inventory[0]=replacement;Frame();
            Check(ActorMining.Current.Reason==ActorMining.Failure.ItemChanged&&ReferenceEquals(p.inventory[0],replacement),"Replacement tool was used or overwritten.");p.inventory[0]=pick;
            Start();ActorMining.Current.Ticks=600;Frame();Check(ActorMining.Current.Phase==ActionPhase.TimedOut&&!p.controlUseItem,"Deadline did not stop.");
            Start();ActorMining.Current.Generation--;int animation=p.itemAnimation;Frame();
            Check(ActorMining.Current.Reason==ActorMining.Failure.BindingChanged&&p.itemAnimation==animation&&!ProbeSession.Quarantined,"Old incarnation wrote the Player.");
            Start();Frame();NativeSurvival.Debug(p,world,generation,true);
            Check(p.dead&&ActorMining.Current.Phase==ActionPhase.Cancelled&&!p.controlUseItem&&p.itemAnimation==0,"Death did not release mining before KillMe.");
            Check(Main.myPlayer==local&&Main.blockMouse==mouse&&Player.tileTargetX==tx&&Player.tileTargetY==ty
                &&ReferenceEquals(human,Main.player[0])&&humanItems.SequenceEqual(human.inventory.Select(i=>(i.type,i.stack))),"Human/global state changed.");
            ProbeSession.Finish("G7 selftest complete",true);
            File.WriteAllText(report,JsonSerializer.Serialize(new{result="passed",checks,evidence,hostAndPlay="not executed",remoteMultiplayer="not executed",
                cadence="explicit real ItemCheckWrapped in native survival scope, before world frames; not a realtime cadence test"}));
        }
        catch(Exception e){File.WriteAllText(report,JsonSerializer.Serialize(new{result="failed",checks,evidence,error=e.ToString()}));VirtualPlayerEngineProbe.Instance?.Logger.Error("G7 diagnostic failed",e);}
        finally{watchFirstPress=false;firstPressControls.Clear();On_NetMessage.SendData-=Packets;if(!failure)Netplay.Disconnect=true;}
    }
}

// Inert except during the explicit private-profile test.
public sealed class MiningDiagnosticTile : GlobalTile
{
    public override bool CanKillTile(int i,int j,int type,ref bool blockDamaged)=>!MiningSelfTest.DenyTile;
}
