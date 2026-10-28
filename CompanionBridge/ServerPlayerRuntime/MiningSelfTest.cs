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
    internal static bool DenyTile,ThrowInPick;
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
            void CheckIsolation()=>Check(Main.myPlayer==local&&Main.blockMouse==mouse&&Player.tileTargetX==tx&&Player.tileTargetY==ty
                &&ReferenceEquals(human,Main.player[0])&&humanItems.SequenceEqual(human.inventory.Select(i=>(i.type,i.stack))),"Human/global state changed during explicit item frames.");
            Target(TileID.Stone);
            void Start()=>ActorMining.Start(p,world,generation,x,y);
            if(failure)
            {
                Start();ThrowInPick=true;bool threw=false;
                try{Frame();}catch{threw=true;}finally{ThrowInPick=false;}
                Check(threw&&ProbeSession.Quarantined&&!ActorMining.Running&&!p.controlUseItem&&p.itemAnimation==0
                    &&ProbeSession.Reservation.IsReserved(p.whoAmI)&&ReferenceEquals(Main.player[p.whoAmI],p),"Native exception did not release/quarantine/retain reservation.");
                File.WriteAllText(report,JsonSerializer.Serialize(new{result="passed",checks,nativeExceptionQuarantine=true,exceptionInsidePickTile=true,processTerminationRequired=true}));return;
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
            // e2d6c35 chained fixture: an NPC hit moved the actor across the
            // native right-edge reach boundary after exactly one 35-damage strike.
            // Replay the recorded relative positions, not NPC AI/RNG or fake damage.
            {
                var savedPosition=p.position;var savedVelocity=p.velocity;
                try
                {
                    Check(p.width==20&&p.height==42&&Player.tileRangeX==5&&Player.tileRangeY==4
                        &&pick.tileBoost==-1&&pick.pick==35,"Unexpected basic copper-pick reach profile.");
                    object Reach()=>new{x=p.position.X,y=p.position.Y,vx=p.velocity.X,vy=p.velocity.Y,
                        targetX=x,targetY=y,centerDistance=Microsoft.Xna.Framework.Vector2.Distance(p.Center,new(x*16+8,y*16+8)),
                        nativeRightTile=(p.position.X+p.width)/16+Player.tileRangeX+pick.tileBoost-1,
                        Player.tileRangeX,Player.tileRangeY,pick.tileBoost,pick.pick,p.selectedItem};
                    Target(TileID.Stone);
                    p.position=new(x*16+8-67.57f,y*16+8-21.1f);p.velocity=new(-4.4f,-3.1f);
                    var startReach=Reach();Start();Frame();
                    Check(ActorMining.Current.Ticks==1&&ActorMining.Current.Strikes==1&&ActorMining.Current.PeakDamage==35
                        &&ActorMining.Running&&Main.tile[x,y].HasTile,"Recorded in-range start did not produce one partial native strike.");
                    // Diagnostic state injection at the next recorded check; not a movement/action path.
                    p.position=new(x*16+8-76.07f,y*16+8-26.1f);p.velocity=new(-4.2f,-2.3f);
                    var failedReach=Reach();
                    Check(Microsoft.Xna.Framework.Vector2.Distance(p.Center,new(x*16+8,y*16+8))<80,
                        "Regression must remain inside conservative center radius.");
                    Frame();var lost=ActorMining.Current;
                    Check(lost.Phase==ActionPhase.Failed&&lost.Reason==ActorMining.Failure.OutOfRange
                        &&lost.Ticks==1&&lost.Strikes==1&&lost.PeakDamage==35&&!lost.Destroyed&&Main.tile[x,y].HasTile
                        &&!p.controlUseItem&&!p.channel&&p.itemAnimation==0&&p.itemTime==0,
                        "Leaving native reach after partial progress must fail and release, never complete/retry.");
                    // A separate explicit diagnostic case at the settled recorded position.
                    Target(TileID.Stone);p.position=new(x*16+8-63.17f,y*16+8-18);p.velocity=Microsoft.Xna.Framework.Vector2.Zero;
                    var stationaryReach=Reach();Start();
                    for(int i=0;ActorMining.Running&&i<610;i++)Frame();
                    var stationary=ActorMining.Current;
                    Check(stationary.Phase==ActionPhase.Completed&&stationary.Destroyed&&!Main.tile[x,y].HasTile
                        &&stationary.Strikes==3&&stationary.PeakDamage==105&&!p.controlUseItem,
                        "Same target/tool at stationary in-range position must still complete natively.");
                    evidence.Add(new{caseName="chained-knockback-native-reach",mode="recorded relative post-hurt state injection; real ItemCheckWrapped and native range",
                        startReach,failedReach,stationaryReach,failed=new{lost.Ticks,lost.Strikes,lost.PeakDamage,reason=lost.Reason.ToString(),lost.Destroyed},
                        stationary=new{stationary.Ticks,stationary.Strikes,stationary.PeakDamage,stationary.Destroyed}});
                }
                finally{p.position=savedPosition;p.velocity=savedVelocity;}
            }

            // Surface grass is a solid block, distinct from cuttable plants.
            // Reproduce the live failure with a real copper pick and native API.
            for(int round=0;round<2;round++)
            {
                Target(TileID.Grass);int dirtBefore=Main.item.Where(i=>i.active&&i.type==ItemID.DirtBlock).Sum(i=>i.stack);
                Start();bool stripped=false,earlyGrassCompletion=false;
                for(int i=0;ActorMining.Running&&i<610;i++)
                {
                    Frame();
                    if(Main.tile[x,y].HasTile&&Main.tile[x,y].TileType==TileID.Dirt)
                    {stripped=true;earlyGrassCompletion|=ActorMining.Current.Phase==ActionPhase.Completed;}
                }
                var grass=ActorMining.Current;
                Check(grass.Phase==ActionPhase.Completed&&!Main.tile[x,y].HasTile,"Native grass block failed: "+ActorMining.Summary());
                Check(stripped&&!earlyGrassCompletion&&grass.Transforms==1&&grass.TileType==TileID.Grass&&grass.ExpectedType==TileID.Dirt,"Grass -> Dirt was not attributed to this native pick, or was falsely completed.");
                Check(Main.item.Where(i=>i.active&&i.type==ItemID.DirtBlock).Sum(i=>i.stack)>dirtBefore,"Native grass mining produced no dirt world drop.");
                evidence.Add(new{grassBlock=true,round,grass.Ticks,grass.Swings,grass.Strikes,grass.ProposedDamage,grass.PeakDamage,grass.Transforms,grass.Destroyed});
            }
            Target(TileID.Grass);Start();WorldGen.KillTile(x,y,true);Frame();
            Check(ActorMining.Current.Reason==ActorMining.Failure.TargetChanged&&Main.tile[x,y].HasTile,"External grass stripping was adopted as owned progress.");
            Target(TileID.Grass);Start();Main.tile[x,y].TileType=TileID.Dirt;Frame();
            Check(ActorMining.Current.Reason==ActorMining.Failure.TargetChanged,"External Grass -> Dirt replacement was adopted.");
            Target(TileID.Stone);int oldHit=p.hitTile.HitObject(x,y,1);p.hitTile.AddDamage(oldHit,70,true);
            var other=Main.tile[x+1,y];other.HasTile=true;other.TileType=TileID.Stone;
            int otherHit=p.hitTile.HitObject(x+1,y,1);p.hitTile.AddDamage(otherHit,20,true);
            Start();Frame();
            Check(ActorMining.Current.PeakDamage==35,"New action inherited stale target damage.");
            int remaining=p.hitTile.TryFinding(x+1,y,1);
            Check(remaining>=0&&p.hitTile.AddDamage(remaining,0,false)==20,"Clearing target progress changed another cached target.");
            ActorMining.Stop(p,world,generation);
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
            evidence.Add(TreeMiningSelfTest.Run(p,world,generation,x,y,Frame,CheckIsolation));
            ActorItemUse.Select(p,world,generation,0);Target(TileID.Stone);
            Start();ActorMining.Current.Generation--;int animation=p.itemAnimation;Frame();
            Check(ActorMining.Current.Reason==ActorMining.Failure.BindingChanged&&p.itemAnimation==animation&&!ProbeSession.Quarantined,"Old incarnation wrote the Player.");
            Start();Frame();NativeSurvival.Debug(p,world,generation,true);
            Check(p.dead&&ActorMining.Current.Phase==ActionPhase.Cancelled&&!p.controlUseItem&&p.itemAnimation==0,"Death did not release mining before KillMe.");
            // The pickup test above deliberately drives full Main.Update frames,
            // which evolve the native cursor. Item-only isolation was checked
            // before that boundary; identity and human inventory remain protected.
            Check(Main.myPlayer==local&&ReferenceEquals(human,Main.player[0])
                &&humanItems.SequenceEqual(human.inventory.Select(i=>(i.type,i.stack))),"Human identity/inventory changed during native world frames.");
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
    public override bool CanKillTile(int i,int j,int type,ref bool blockDamaged)
    {
        if(MiningSelfTest.ThrowInPick)throw new InvalidOperationException("G7 intentional native CanKillTile failure");
        return !MiningSelfTest.DenyTile;
    }
}
