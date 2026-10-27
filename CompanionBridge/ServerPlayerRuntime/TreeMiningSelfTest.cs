using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using CompanionBridge.ServerIpc;

namespace VirtualPlayerEngineProbe;

// Called only by -miningselftest in a disposable copied world. Fixtures may
// grow trees/move this diagnostic actor; production mining does neither.
internal static class TreeMiningSelfTest
{
    internal static object Run(Player p, string world, long generation, int x, int y, Action frame, Action verifyIsolation)
    {
        void Check(bool ok,string message){if(!ok)throw new InvalidOperationException("R3: "+message);}
        bool Released()=>!p.controlUseItem&&p.releaseUseItem&&!p.channel&&p.itemAnimation==0&&p.itemTime==0;
        var executor=new ServerAiNativeActions();
        var identity=new AiReadOnlyProtocol.Identity(p,world,generation,p.whoAmI,new{},p.selectedItem,Main.GameUpdateCount,true);
        object Start(int tx,int ty)=>executor.Start(identity,"mine_tile",JsonSerializer.SerializeToElement(new{x=tx,y=ty}),Guid.NewGuid().ToString("N"));
        p.inventory[2].SetDefaults(ItemID.CopperAxe);
        ActorItemUse.Select(p,world,generation,2);
        void Block(int type)
        {
            var tile=Main.tile[x,y];tile.ClearEverything();tile.HasTile=true;tile.TileType=(ushort)type;
        }
        // Tree-shaped fixture is used only for preflight failures. Successful
        // felling below MUST use a tree grown by the real WorldGen.GrowTree.
        Block(TileID.Stone);var rejected=Start(x,y);var refusal=executor.Poll(identity,rejected);
        Check(ActorMining.Current.Reason==ActorMining.Failure.UnsupportedTarget&&ActorMining.Current.Ticks==0
            &&Main.tile[x,y].HasTile&&refusal.Status=="failed"&&!refusal.EffectVerified&&Released(),"axe damaged an ordinary block");
        Block(TileID.Trees);ActorItemUse.Select(p,world,generation,0);Start(x,y);
        Check(ActorMining.Current.Reason==ActorMining.Failure.UnsupportedTarget&&ActorMining.Current.Ticks==0
            &&Main.tile[x,y].HasTile&&Released(),"pickaxe accepted a tree");
        ActorItemUse.Select(p,world,generation,2);
        Block(TileID.ClosedDoor);Start(x,y);
        Check(ActorMining.Current.Reason==ActorMining.Failure.UnsupportedTarget&&Main.tile[x,y].HasTile&&Released(),"axe accepted furniture");
        var invalid=executor.Poll(identity,Start(-1,y));
        Check(invalid.Status=="failed"&&!invalid.InputsAcquired&&!invalid.EffectVerified&&Released(),"invalid coordinates were not preflight failure");
        var distant=executor.Poll(identity,Start(x+30,y));
        Check(distant.Status=="failed"&&!distant.InputsAcquired&&!distant.EffectVerified&&Released(),"distant target was not preflight failure");

        for(int dx=-5;dx<=5;dx++)for(int dy=-38;dy<=3;dy++)
        {
            var tile=Main.tile[x+dx,y+dy];tile.ClearEverything();
            if(dy>=1){tile.HasTile=true;tile.TileType=dy==1?TileID.Grass:TileID.Dirt;}
        }
        Check(WorldGen.GrowTree(x,y+1)&&Main.tile[x,y].HasTile&&Main.tile[x,y].TileType==TileID.Trees,"native tree fixture failed to grow");
        var tree=(from tx in Enumerable.Range(x-5,11) from ty in Enumerable.Range(y-38,39)
            where Main.tile[tx,ty].HasTile&&Main.tile[tx,ty].TileType==TileID.Trees select (x:tx,y:ty)).ToArray();
        Check(tree.Length>5,"native fixture was not a complete tree");
        int woodBefore=p.inventory.Where(i=>i.type==ItemID.Wood).Sum(i=>i.stack);
        int dropsBefore=Main.item.Where(i=>i.active&&i.type==ItemID.Wood).Sum(i=>i.stack);
        MiningSelfTest.DenyTile=true;
        try{Start(x,y);frame();}finally{MiningSelfTest.DenyTile=false;}
        Check(ActorMining.Current.Reason==ActorMining.Failure.NoPickPower&&!ActorMining.Current.Destroyed
            &&Main.tile[x,y].HasTile&&Released(),"axe bypassed native CanKillTile");
        ItemUseSelfTest.Deny=true;
        try{Start(x,y);frame();}finally{ItemUseSelfTest.Deny=false;}
        Check(ActorMining.Current.Reason==ActorMining.Failure.NativeRefused&&ActorMining.Current.Swings==0&&Released(),"axe bypassed native CanUseItem");
        var cancel=Start(x,y);frame();var cancelled=executor.Cancel(identity,cancel);
        Check(cancelled.Status=="cancelled"&&!cancelled.EffectVerified&&Main.tile[x,y].HasTile&&Released(),"axe cancellation falsely completed");
        Start(x,y);frame();ActorMining.Current.Ticks=600;frame();
        Check(ActorMining.Current.Phase==ActionPhase.TimedOut&&!ActorMining.Current.Destroyed&&Released(),"axe deadline falsely completed or retained inputs");
        var original=Main.tile[x,y].TileType;Start(x,y);frame();Main.tile[x,y].TileType=TileID.Stone;frame();
        Check(ActorMining.Current.Reason==ActorMining.Failure.TargetChanged&&!ActorMining.Current.Destroyed&&Released(),"replaced tree falsely completed");
        Main.tile[x,y].TileType=original;
        var handle=Start(x,y);
        for(int tick=0;ActorMining.Running&&tick<610;tick++)frame();
        var c=ActorMining.Current;var result=executor.Poll(identity,handle);
        int remaining=tree.Count(t=>Main.tile[t.x,t.y].HasTile&&Main.tile[t.x,t.y].TileType==TileID.Trees);
        int drops=Main.item.Where(i=>i.active&&i.type==ItemID.Wood).Sum(i=>i.stack)-dropsBefore;
        Check(c.Phase==ActionPhase.Completed&&c.Axe>0&&c.Swings>0&&c.Strikes>1&&c.PeakDamage>=100
            &&c.Destroyed&&!c.ExternalChange&&!Main.tile[x,y].HasTile&&Released(),"native tree strike did not complete: "+ActorMining.Summary());
        Check(result.Status=="completed"&&result.EffectVerified&&result.InputsReleased,"API did not confirm native target destruction/release");
        Check(remaining==0&&drops>0,"tree did not fully fall or no real wood drops were observed");
        Check(p.inventory.Where(i=>i.type==ItemID.Wood).Sum(i=>i.stack)==woodBefore,"target destruction was conflated with collection");
        verifyIsolation(); // Explicit ItemCheck frames must not touch human cursor/globals.

        // Separate pickup observation after the action. Full native world frames
        // drive item delay, falling, attraction and Player.GrabItems naturally.
        var update=typeof(Main).GetMethod("Update",BindingFlags.Instance|BindingFlags.NonPublic,null,new[]{typeof(GameTime)},null);
        int cursorX=Player.tileTargetX,cursorY=Player.tileTargetY;bool mouse=Main.blockMouse;
        p.position=new Vector2(x*16-10,(y+1)*16-p.height);p.velocity=Vector2.Zero;
        for(int tick=0;tick<300&&p.inventory.Where(i=>i.type==ItemID.Wood).Sum(i=>i.stack)==woodBefore;tick++)
            update.Invoke(Main.instance,new object[]{new GameTime()});
        int collected=p.inventory.Where(i=>i.type==ItemID.Wood).Sum(i=>i.stack)-woodBefore;
        Check(collected>0,"no actual wood pickup into AI inventory after native world frames");
        executor.Shutdown();
        return new{itemType=c.Type,axe=c.Axe,c.Ticks,c.Swings,c.Strikes,c.AnimationPeak,c.PeakDamage,
            targetDestroyed=c.Destroyed,treeTilesBefore=tree.Length,treeTilesRemaining=remaining,worldWoodDropped=drops,
            woodInBagAtDestruction=0,woodActuallyCollected=collected,apiResult=result,
            fullWorldFrameCursor=new{beforeX=cursorX,beforeY=cursorY,afterX=Player.tileTargetX,afterY=Player.tileTargetY,beforeMouse=mouse,afterMouse=Main.blockMouse},
            nativeProtection=true,nativeCanUseRefusal=true,cancelReleased=true,timeoutReleased=true,targetReplacementRejected=true,
            mode="isolated native APIs + post-action full native world frames; no graphical client"};
    }
}
