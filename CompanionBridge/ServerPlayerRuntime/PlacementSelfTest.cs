using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using CompanionBridge.ServerIpc;

namespace VirtualPlayerEngineProbe;

// Opt-in isolated copied-world fixture, before normal server listening.
internal static class PlacementSelfTest
{
    private static bool ran;
    internal static bool DenyTile,DenyConsumption,Throw;
    internal static int HumanScopeChecks;
    internal static void RunIfRequested()
    {
        if(ran||!Environment.GetCommandLineArgs().Contains("-placementselftest"))return;
        ran=true;bool failure=Environment.GetCommandLineArgs().Contains("-placementfailureselftest");
        if(Environment.GetCommandLineArgs().Contains("-objectplacementselftest")){ObjectPlacementSelfTest.Run(failure);return;}
        int checks=0;var evidence=new List<object>();
        string report=Path.Combine(Main.SavePath,"placement-native-result.json");
        void Check(bool ok,string why){if(!ok)throw new InvalidOperationException(why);checks++;}
        try
        {
            Main.gameMenu=false;ProbeSession.Spawn();var p=ProbeSession.Actor;
            var human=Main.player[0];var bag=human.inventory.Select(i=>(i.type,i.stack)).ToArray();
            int local=Main.myPlayer,tx=Player.tileTargetX,ty=Player.tileTargetY;bool mouse=Main.blockMouse;
            int x=Main.spawnTileX+3,floor=Main.spawnTileY+8,y=floor-1;
            for(int xx=x-8;xx<=x+6;xx++)for(int yy=floor-8;yy<=floor+6;yy++)
            {var t=Main.tile[xx,yy];t.ClearEverything();if(yy>=floor){t.HasTile=true;t.TileType=TileID.Dirt;}}
            p.position=new((x-3)*16,floor*16-p.height);p.velocity=Vector2.Zero;
            var identity=new AiReadOnlyProtocol.Identity(p,ProbeSession.WorldSession,ProbeSession.ActorGeneration,p.whoAmI,new{},0,Main.GameUpdateCount,true);
            var executor=new ServerAiNativeActions();
            var wrapped=typeof(Player).GetMethod("ItemCheckWrapped",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
            void Frame()=>NativeSurvival.Run(p,()=>wrapped.Invoke(p,new object[]{p.whoAmI}));
            bool Released()=>!p.controlUseItem&&p.releaseUseItem&&!p.channel&&p.itemAnimation==0&&p.itemTime==0;
            void Block(int type){var t=Main.tile[x,y];t.HasTile=true;t.TileType=(ushort)type;}
            void Material(int type=ItemID.Wood,int stack=5){p.inventory[4].SetDefaults(type);p.inventory[4].stack=stack;ActorItemUse.Select(p,identity.World,identity.Generation,4);Main.tile[x,y].ClearEverything();}
            object Start(int? xx=null,int? yy=null)=>executor.Start(identity,"place_tile",JsonSerializer.SerializeToElement(new{x=xx??x,y=yy??y}),Guid.NewGuid().ToString("N"));
            AiAtomicActions.NativeResult Finish(object handle)
            {
                for(int tick=0;ActorItemUse.Running&&tick<605;tick++)Frame();
                var r=executor.Poll(identity,handle);evidence.Add(r);return r;
            }
            var queryState=(p.position,p.velocity,p.controlUseItem,p.releaseUseItem,p.channel,p.itemAnimation,p.itemTime,p.selectedItem);
            var impactEnvelope=JsonSerializer.SerializeToElement(ServerAiQueries.Read(identity,"inspect_tile",
                JsonSerializer.SerializeToElement(new{x=x-3,y=floor})));
            var impact=impactEnvelope.GetProperty("data").GetProperty("miningSupportImpact");
            Check(impact.GetProperty("available").GetBoolean()
                &&impact.GetProperty("current").GetProperty("supportPixels").GetSingle()==20
                &&impact.GetProperty("afterRemoval").GetProperty("supportPixels").GetSingle()==4
                &&impact.GetProperty("affectsCurrentStance").GetBoolean(),"server inspect_tile fixed-pose support projection failed");
            Check(Main.tile[x-3,floor].HasTile&&queryState.Equals((p.position,p.velocity,p.controlUseItem,p.releaseUseItem,p.channel,p.itemAnimation,p.itemTime,p.selectedItem)),
                "support query mutated Tile/Player/input state");
            var unavailable=JsonSerializer.SerializeToElement(ServerAiQueries.Read(identity,"inspect_tile",
                JsonSerializer.SerializeToElement(new{x=Main.maxTilesX,y=floor}))).GetProperty("data").GetProperty("miningSupportImpact");
            Check(!unavailable.GetProperty("available").GetBoolean()&&unavailable.GetProperty("affectsCurrentStance").ValueKind==JsonValueKind.Null,
                "out-of-world support query must remain unknown without Tile access");
            evidence.Add(new{miningSupportImpact=impact.Clone(),unknownTarget=unavailable.Clone()});
            Material();
            if(failure)
            {
                Start();Throw=true;bool threw=false;try{Frame();}catch{threw=true;}finally{Throw=false;}
                Check(threw&&ProbeSession.Quarantined&&!ActorItemUse.Running&&Released()
                    &&ReferenceEquals(Main.player[p.whoAmI],p)&&ProbeSession.Reservation.IsReserved(p.whoAmI)
                    &&!Main.tile[x,y].HasTile&&p.inventory[4].stack==5,"placement exception did not release/quarantine without world/inventory mutation");
                File.WriteAllText(report,JsonSerializer.Serialize(new{result="passed",checks,processTerminationRequired=true,nativePlacementException=true}));return;
            }
            foreach(int type in new[]{ItemID.Wood,ItemID.StoneBlock,ItemID.DirtBlock})
            {
                Material(type);var r=Finish(Start());var c=ActorItemUse.Current;
                Check(r.Status=="completed"&&r.EffectVerified&&r.InputsReleased&&c.Placement.Placed
                    &&c.Placement.ConsumedCount==1&&c.Placement.Attempts==1&&c.Starts==1&&p.inventory[4].stack==4
                    &&Main.tile[x,y].TileType==c.Placement.TileType&&Released(),"native block placement/consumption failed: "+JsonSerializer.Serialize(r));
            }
            Material(ModContent.ItemType<PlacementFixtureBlock>());
            Check(Finish(Start()).EffectVerified&&Main.tile[x,y].TileType==TileID.GrayBrick&&p.inventory[4].stack==4,
                "ModItem did not reach the native placement and consumption path");
            Material(stack:1);var last=Finish(Start());
            Check(last.EffectVerified&&p.inventory[4].IsAir&&Released(),"last native block did not complete after slot replacement");
            AiMapRuntime.Map=new AiMap(Guid.NewGuid(),"native-fixture",Main.maxTilesX,Main.maxTilesY);
            Material(ItemID.StoneBlock);var placedStone=Finish(Start());
            Check(placedStone.EffectVerified&&AiMapRuntime.Map.Get(x,y)==2,"confirmed placement not remembered");
            p.inventory[5].SetDefaults(ItemID.CopperPickaxe);ActorItemUse.Select(p,identity.World,identity.Generation,5);
            var mining=executor.Start(identity,"mine_tile",JsonSerializer.SerializeToElement(new{x,y}),Guid.NewGuid().ToString("N"));
            for(int frame=0;ActorMining.Running&&frame<2000;frame++)Frame();
            var mined=executor.Poll(identity,mining);evidence.Add(mined);
            Check(mined.EffectVerified&&AiMapRuntime.Map.Get(x,y)==1,"confirmed native mining not remembered: "+JsonSerializer.Serialize(mined));
            Material(ItemID.Torch);var torch=Finish(Start());
            Check(torch.Status=="completed"&&torch.EffectVerified&&Main.tile[x,y].TileType==TileID.Torches,"native torch placement failed: "+JsonSerializer.Serialize(torch));
            Check(AiMapRuntime.Map.Get(x,y)==1,"confirmed torch Tile category not recorded");
            AiMapNativeTest.Run(p,Check);
            AiMapRuntime.Reset();
            foreach(int type in new[]{ItemID.WoodenSword,ItemID.WorkBench,ItemID.Rope})
            {
                Material(type);var r=executor.Poll(identity,Start());
                Check(r.Status=="failed"&&!r.InputsAcquired&&!Main.tile[x,y].HasTile,"unsupported item was accepted: "+type);
            }
            Material();Block(TileID.Stone);
            Check(executor.Poll(identity,Start()).Reason=="target_occupied"&&p.inventory[4].stack==5,"placement replaced an occupied tile");
            Material();Check(executor.Poll(identity,Start(-1,y)).Reason=="invalid_target","out-of-world target accepted");
            Main.tile[x+30,y].ClearEverything();
            Check(Finish(Start(x+30,y)).Status=="failed"&&!Main.tile[x+30,y].HasTile,"distant placement accepted");
            var floating=Finish(Start(x,y-3));
            Check(floating.Status=="failed"&&!floating.EffectVerified&&!Main.tile[x,y-3].HasTile&&p.inventory[4].stack==5&&Released(),"floating block bypassed native adjacency");
            // Below the feet: inside conservative centre radius, outside the native
            // placement rectangle. No private reach enlargement is permitted.
            int belowX=x-3,belowY=floor+3;Main.tile[belowX,belowY].ClearEverything();
            var reach=Finish(Start(belowX,belowY));
            Check(reach.Status=="failed"&&!Main.tile[belowX,belowY].HasTile&&p.inventory[4].stack==5,"native placement reach was bypassed");
            Material();DenyTile=true;AiAtomicActions.NativeResult protectedTile;
            try{protectedTile=Finish(Start());}finally{DenyTile=false;}
            Check(protectedTile.Status=="failed"&&!Main.tile[x,y].HasTile&&p.inventory[4].stack==5,"native GlobalTile.CanPlace was bypassed");
            Material();ItemUseSelfTest.Deny=true;AiAtomicActions.NativeResult deniedUse;
            try{deniedUse=Finish(Start());}finally{ItemUseSelfTest.Deny=false;}
            Check(deniedUse.Status=="failed"&&!Main.tile[x,y].HasTile&&p.inventory[4].stack==5,"native CanUseItem was bypassed");
            Material();DenyConsumption=true;AiAtomicActions.NativeResult noConsume;
            try{noConsume=Finish(Start());}finally{DenyConsumption=false;}
            Check(noConsume.Status=="unknown"&&!noConsume.EffectVerified&&Main.tile[x,y].HasTile&&p.inventory[4].stack==5&&Released(),"unconsumed native placement was falsely verified or rolled back");
            Material();var cancelled=Start();Frame();var cancel=executor.Cancel(identity,cancelled);evidence.Add(cancel);
            Check(cancel.Status=="cancelled"&&!cancel.EffectVerified&&Main.tile[x,y].HasTile&&p.inventory[4].stack==4&&Released(),"cancel rolled back native effect or retained input");
            Material();var changed=Start();Block(TileID.Stone);Frame();
            Check(executor.Poll(identity,changed).Status=="failed"&&!ActorItemUse.Current.Placement.Placed&&p.inventory[4].stack==5,"foreign placement became owned success");
            Material();var replaced=Start();Frame();WorldGen.KillTile(x,y);Block(TileID.WoodBlock);Frame();
            Check(executor.Poll(identity,replaced).Status=="unknown"&&ActorItemUse.Current.Placement.ExternalChange&&Released(),"same-type remove/replace was adopted");
            Material();var tool=Start();p.inventory[4]=p.inventory[4].Clone();Frame();
            Check(executor.Poll(identity,tool).Status=="cancelled"&&!Main.tile[x,y].HasTile&&p.inventory[4].stack==5,"replaced item object was used");
            Material();var prefix=Start();Frame();ActorItemUse.Current.Item.prefix=1;Frame();
            var prefixResult=executor.Poll(identity,prefix);evidence.Add(prefixResult);
            Check(prefixResult.Status=="unknown"&&ActorItemUse.Current.Reason=="placement_item_changed"&&Released(),"remaining material metadata change after consumption was ignored");
            Material();var deadline=Start();Frame();ActorItemUse.Current.Ticks=600;Frame();
            Check(executor.Poll(identity,deadline).Status=="timeout"&&Released(),"placement deadline did not release");
            evidence.Add(new{isolation=new{HumanScopeChecks,localBefore=local,localAfter=Main.myPlayer,txBefore=tx,txAfter=Player.tileTargetX,tyBefore=ty,tyAfter=Player.tileTargetY,mouseBefore=mouse,mouseAfter=Main.blockMouse}});
            Check(HumanScopeChecks>0&&ReferenceEquals(human,Main.player[0])&&bag.SequenceEqual(human.inventory.Select(i=>(i.type,i.stack)))
                &&Main.myPlayer==local&&Player.tileTargetX==tx&&Player.tileTargetY==ty&&Main.blockMouse==mouse,"placement changed human inventory, cursor or identity");
            executor.Shutdown();ProbeSession.Finish("native placement diagnostic complete",true);
            File.WriteAllText(report,JsonSerializer.Serialize(new{result="passed",checks,HumanScopeChecks,evidence,
                mode="isolated native ItemCheckWrapped, real Player placement and consumption; not graphical Host & Play"}));
        }
        catch(Exception e){File.WriteAllText(report,JsonSerializer.Serialize(new{result="failed",checks,evidence,error=e.ToString()}));}
        finally{DenyTile=DenyConsumption=Throw=false;if(!failure)Netplay.Disconnect=true;}
    }
}

public sealed class PlacementDiagnosticTile : GlobalTile
{
    public override bool CanPlace(int i,int j,int type)
    {
        CompanionBridge.ServerIpc.ServerRopeFixture.ObservePlacementScope(i,j);
        if(PlacementSelfTest.Throw)throw new InvalidOperationException("Intentional native placement exception");
        if(Environment.GetCommandLineArgs().Contains("-placementselftest"))
        {
            var human=Main.player[0];
            if(ActorPlacement.TargetX(human)!=Player.tileTargetX||ActorPlacement.TargetY(human)!=Player.tileTargetY)
                throw new InvalidOperationException("AI scope contaminated another Player's placement target");
            PlacementSelfTest.HumanScopeChecks++;
        }
        return !PlacementSelfTest.DenyTile;
    }
}
public sealed class PlacementDiagnosticItem : GlobalItem
{
    public override bool ConsumeItem(Item item,Player player)
    {
        if(Environment.GetCommandLineArgs().Contains("-objectplacementafterexception")&&ActorItemUse.Current?.Placement?.Placed==true)
            throw new InvalidOperationException("Intentional native exception after object placement, before consumption");
        return !PlacementSelfTest.DenyConsumption;
    }
}

public sealed class PlacementFixtureBlock : ModItem
{
    public override bool IsLoadingEnabled(Mod mod)=>Environment.GetCommandLineArgs().Contains("-placementselftest");
    public override string Texture=>"Terraria/Images/Item_"+ItemID.GrayBrick;
    public override void SetDefaults()=>Item.DefaultToPlaceableTile(TileID.GrayBrick);
}
