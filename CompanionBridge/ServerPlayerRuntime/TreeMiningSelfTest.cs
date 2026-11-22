using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.DataStructures;
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
        var savedPick=p.inventory[0];p.inventory[0]=new Item();
        Block(TileID.Stone);var rejected=Start(x,y);var refusal=executor.Poll(identity,rejected);
        Check(ActorMining.Current.Reason==ActorMining.Failure.InvalidTool&&ActorMining.Current.Ticks==0
            &&Main.tile[x,y].HasTile&&refusal.Status=="failed"&&!refusal.EffectVerified&&Released(),"axe damaged an ordinary block");
        p.inventory[0]=savedPick;var savedAxe=p.inventory[2];p.inventory[2]=new Item();
        Block(TileID.Trees);ActorItemUse.Select(p,world,generation,0);Start(x,y);
        Check(ActorMining.Current.Reason==ActorMining.Failure.UnsupportedTarget&&ActorMining.Current.Ticks==0
            &&Main.tile[x,y].HasTile&&Released(),"pickaxe accepted a tree");
        p.inventory[2]=savedAxe;ActorItemUse.Select(p,world,generation,2);
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
        Check(ActorMining.Current.Reason==ActorMining.Failure.TargetProtected&&!ActorMining.Current.Destroyed
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
        verifyIsolation(); // Item-only checks end before native world frames evolve the cursor.
        // Real Player.Update must retain passive pickup while holding a mining intent.
        // Only fixture setup moves the actor/creates a drop; no corrections during mining.
        foreach(var npc in Main.npc)npc.active=false;
        p.position=new Vector2(x*16-10,(y+1)*16-p.height);p.velocity=Vector2.Zero;
        var held=p.HeldItem;
        int nearby=Item.NewItem(new EntitySource_Misc("mining-pickup-selftest"),p.Hitbox,ItemID.Wood,7);
        Main.item[nearby].playerIndexTheItemIsReservedFor=p.whoAmI;
        Main.item[nearby].noGrabDelay=0;
        var update=typeof(Main).GetMethod("Update",BindingFlags.Instance|BindingFlags.NonPublic,null,new[]{typeof(GameTime)},null);
        void WorldFrame()=>update.Invoke(Main.instance,new object[]{new GameTime()});
        int Wood()=>p.inventory.Where(i=>i.type==ItemID.Wood).Sum(i=>i.stack);
        int cursorX=Player.tileTargetX,cursorY=Player.tileTargetY;bool mouse=Main.blockMouse;
        var handle=Start(x,y);
        for(int tick=0;tick<10&&Wood()==woodBefore&&ActorMining.Running;tick++)WorldFrame();
        var pickup=ActorMining.Current;
        Check(Wood()==woodBefore+7&&Main.item[nearby].IsAir,
            "nearby wood was not picked up exactly once during sustained native mining: "+ActorMining.Summary());
        Check(ActorMining.Running&&!pickup.Destroyed&&Main.tile[x,y].HasTile&&pickup.Strikes>0
            &&pickup.PeakDamage<100&&ReferenceEquals(p.HeldItem,held)&&p.selectedItem==2,
            "pickup changed held tool, ended mining early, or was observed only after destruction");
        var pickupEvidence=new{pickup.Ticks,pickup.Strikes,pickup.PeakDamage,woodCollected=Wood()-woodBefore,
            targetPresent=Main.tile[x,y].HasTile,running=ActorMining.Running,heldToolUnchanged=ReferenceEquals(p.HeldItem,held)};
        for(int tick=0;ActorMining.Running&&tick<610;tick++)WorldFrame();
        var c=ActorMining.Current;var result=executor.Poll(identity,handle);
        int remaining=tree.Count(t=>Main.tile[t.x,t.y].HasTile&&Main.tile[t.x,t.y].TileType==TileID.Trees);
        int drops=Main.item.Where(i=>i.active&&i.type==ItemID.Wood).Sum(i=>i.stack)-dropsBefore;
        Check(c.Phase==ActionPhase.Completed&&c.Axe>0&&c.Swings>0&&c.Strikes>1&&c.PeakDamage>=100
            &&c.Destroyed&&!c.ExternalChange&&!Main.tile[x,y].HasTile&&Released(),"native tree strike did not complete: "+ActorMining.Summary());
        Check(result.Status=="completed"&&result.Reason=="native_tile_destroyed"&&result.ReasonDetail==null
            &&result.EffectVerified&&result.InputsReleased,"API did not confirm native target destruction/release/reason");
        int woodAtCompletion=Wood()-woodBefore;
        Check(remaining==0&&drops+woodAtCompletion>7,"tree did not fully fall or no real wood drops were observed");
        Check(ReferenceEquals(p.HeldItem,held)&&p.selectedItem==2,"native pickup replaced the mining tool");

        // Continue without moving: native delay/attraction may leave some tree drops pending.
        for(int tick=0;tick<300&&Wood()-woodBefore<=7;tick++)WorldFrame();
        int collected=Wood()-woodBefore;
        Check(collected>7,"no actual tree wood pickup into AI inventory after native world frames");
        executor.Shutdown();
        return new{itemType=c.Type,axe=c.Axe,c.Ticks,c.Swings,c.Strikes,c.AnimationPeak,c.PeakDamage,
            targetDestroyed=c.Destroyed,treeTilesBefore=tree.Length,treeTilesRemaining=remaining,worldWoodDropped=drops,
            woodInBagAtCompletion=woodAtCompletion,woodActuallyCollected=collected,pickupDuringMining=pickupEvidence,apiResult=result,
            fullWorldFrameCursor=new{beforeX=cursorX,beforeY=cursorY,afterX=Player.tileTargetX,afterY=Player.tileTargetY,beforeMouse=mouse,afterMouse=Main.blockMouse},
            nativeProtection=true,nativeCanUseRefusal=true,cancelReleased=true,timeoutReleased=true,targetReplacementRejected=true,
            mode="isolated native APIs + sustained mining in full native world frames; no graphical client"};
    }
}
