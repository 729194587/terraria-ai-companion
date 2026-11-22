using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using CompanionBridge.Motion;
using CompanionBridge.ServerIpc;
using Result = CompanionBridge.ServerIpc.AiAtomicActions.NativeResult;

namespace VirtualPlayerEngineProbe;

// Only called by the copied-world mining diagnostic.
internal static class ExcavationSelfTest
{
    internal static object Run(Player player,string world,long generation,int x,int y,Action frame,Action<bool,string> check)
    {
        x+=24;y+=4;
        var saved=(player.position,player.velocity,player.releaseJump,player.pickSpeed,player.inventory[0],player.inventory[1]);
        var savedDriver=ActorControl.Driver;
        var identity=new AiReadOnlyProtocol.Identity(player,world,generation,player.whoAmI,new{},player.selectedItem,0,true);
        var executor=new ServerAiNativeActions();
        void Set(int tx,int ty,int type)
        {
            var tile=Main.tile[tx,ty];
            tile.ClearEverything();
            if(type>=0){tile.HasTile=true;tile.TileType=(ushort)type;}
        }
        try
        {
            for(int dx=-10;dx<=14;dx++)
                for(int dy=-8;dy<=2;dy++)Set(x+dx,y+dy,dy>=0?TileID.Stone:-1);
            player.position=new Vector2(x*16,y*16-42);
            player.velocity=Vector2.Zero;player.releaseJump=true;player.pickSpeed=1f;
            player.inventory[0]=new Item(ItemID.BonePickaxe);
            var pose=ServerAiNavigation.Observe(player);
            check(pose.Settled,"Excavation fixture must start on native stable support.");
            void Ore()
            {
                for(int i=2;i<=4;i++)Set(x+i,y-2,TileID.Iron);
            }
            ulong tick=0;
            var hitTrace=new List<object>();
            (Result Result,JsonElement View,int Runs,bool HeldBetween) Cluster(string name,Action onFirst=null,bool stop=false)
            {
                Ore();
                var previous=ActorMining.Current;
                int lastHit=0;hitTrace.Clear();
                var args=JsonSerializer.SerializeToElement(new{x=x+2,y=y-2,cluster=true});
                var root=new TaskSlot(new ExcavationTask(name,identity,args,executor,new()),name,null,identity with{GameUpdate=tick},1000);
                bool sawFirst=false,held=false;
                for(int n=0;n<850;n++)
                {
                    var update=identity with{GameUpdate=++tick};
                    var result=root.Tick(update);
                    if(result.Status!="running")
                    {
                        var view=JsonSerializer.SerializeToElement(root.View());
                        int runs=view.GetProperty("progress").GetProperty("children").EnumerateArray()
                            .Count(child=>child.GetProperty("taskName").GetString()=="excavate_tiles");
                        return (result,view,runs,held);
                    }
                    frame();
                    if(!ReferenceEquals(previous,ActorMining.Current)&&ActorMining.Current is {Batch:not null} c&&c.Strikes>lastHit)
                    {
                        lastHit=c.Strikes;
                        hitTrace.Add(new{c.Ticks,c.Strikes,c.Swings,player.itemAnimation,player.itemTime});
                    }
                    if(sawFirst||ReferenceEquals(previous,ActorMining.Current)||ActorMining.Current?.Batch?.Verified.Count!=1)continue;
                    sawFirst=true;held=player.controlUseItem;
                    onFirst?.Invoke();
                    if(stop)
                    {
                        result=root.Stop(update,"cancelled","explicit_stop");
                        return (result,JsonSerializer.SerializeToElement(root.View()),1,held);
                    }
                }
                throw new InvalidOperationException("Cluster fixture did not reach a terminal state.");
            }
            Ore();
            var ranged=ExcavationCell.Capture(x+4,y-2);
            var shortPick=player.inventory[0];
            shortPick.tileBoost=-4;player.inventory[1]=new Item(ItemID.NightmarePickaxe);
            var rangedQuote=NativeExcavation.Choose(identity,pose.State,new[]{ranged},
                new LocalTerrainView(CompanionBridge.Common.Systems.TerrainReader.ReadEnvironment),ordered:false);
            check(rangedQuote?.Slot==1,"Work-position quote must exclude the faster pick when its native reach misses the ore.");
            shortPick.SetDefaults(ItemID.BonePickaxe);
            var strongPick=player.inventory[1];
            strongPick.useTime=12;strongPick.useAnimation=18;
            Set(x+2,y-2,TileID.Stone);Set(x+3,y-2,TileID.Ebonstone);
            var mixed=NativeExcavation.Choose(identity,pose.State,new[]{ExcavationCell.Capture(x+2,y-2),ExcavationCell.Capture(x+3,y-2)},
                new LocalTerrainView(CompanionBridge.Common.Systems.TerrainReader.ReadEnvironment),ordered:true);
            check(ActorMining.EstimateUpdates(player,0,x+2,y-2)<ActorMining.EstimateUpdates(player,1,x+2,y-2)
                &&mixed?.Slot==1&&mixed.Cells.Length==2,"Batch tool choice must minimize the whole run, even when the first tile has a faster pick.");
            shortPick.SetDefaults(ItemID.CopperPickaxe);player.inventory[1]=new Item();
            Ore();
            float isolated=Enumerable.Range(2,3).Sum(i=>ActorMining.EstimateUpdates(player,0,x+i,y-2));
            var collected=Cluster("iron");
            check(collected.Result.Status=="completed","Iron cluster failed: "+collected.View);
            var proof=collected.View.GetProperty("result").GetProperty("evidence");
            var run=collected.View.GetProperty("lastResult").GetProperty("evidence");
            check(collected.Result.Status=="completed"&&collected.Result.InputsReleased&&collected.Result.EffectVerified
                &&collected.Runs==1&&collected.HeldBetween&&proof.GetProperty("targets").EnumerateArray()
                    .All(t=>t.GetProperty("status").GetString()=="verified"),
                "Iron cluster needs one continuous native run and proof for every tile: "+collected.View);
            check(run.GetProperty("verified").GetArrayLength()==3&&run.GetProperty("actualUpdates").GetInt32()==run.GetProperty("estimatedUpdates").GetSingle()
                &&run.GetProperty("actualUpdates").GetInt32()<isolated,
                $"Continuous run timing mismatch. Isolated={isolated}; run={run}; timing={ActorMining.Timing(player,player.inventory[0])}; hits={JsonSerializer.Serialize(hitTrace)}");
            shortPick.SetDefaults(ItemID.BonePickaxe);
            var automatic=Cluster("bone-iron");
            var autoRun=automatic.View.GetProperty("lastResult").GetProperty("evidence");
            check(automatic.Result.Status=="completed"&&automatic.Runs==1
                &&autoRun.GetProperty("actualUpdates").GetInt32()==autoRun.GetProperty("estimatedUpdates").GetSingle(),
                $"Automatic rearm must use the native animation boundary: {autoRun}; hits={JsonSerializer.Serialize(hitTrace)}");
            shortPick.reuseDelay=5;shortPick.autoReuse=false;
            var manual=Cluster("manual-reuse-delay");
            var manualRun=manual.View.GetProperty("lastResult").GetProperty("evidence");
            check(manual.Result.Status=="completed"&&manual.Runs==1
                &&manualRun.GetProperty("actualUpdates").GetInt32()==manualRun.GetProperty("estimatedUpdates").GetSingle(),
                $"Manual rearm and reuse delay must match native timing: {manualRun}; hits={JsonSerializer.Serialize(hitTrace)}");
            shortPick.SetDefaults(ItemID.BonePickaxe);

            var interrupted=Cluster("stop",stop:true);
            var stopProof=interrupted.View.GetProperty("result").GetProperty("evidence");
            check(interrupted.Result.Status=="cancelled"&&interrupted.Result.InputsReleased&&!player.controlUseItem
                &&stopProof.GetProperty("outcome").GetString()=="interrupted"
                &&stopProof.GetProperty("targets").EnumerateArray().Count(t=>t.GetProperty("status").GetString()=="verified")==1
                &&Main.tile[x+3,y-2].HasTile&&Main.tile[x+4,y-2].HasTile,"Stopping must retain the first proof and release before later strikes.");

            var partial=Cluster("external",()=>WorldGen.KillTile(x+3,y-2));
            var partialProof=partial.View.GetProperty("result").GetProperty("evidence");
            check(partial.Result.Status=="failed"&&partial.Result.InputsReleased&&partial.Runs==2
                &&partialProof.GetProperty("outcome").GetString()=="partial"
                &&partialProof.GetProperty("targets").EnumerateArray().Count(t=>t.GetProperty("status").GetString()=="verified")==2,
                "External destruction must not count as owned progress: "+partial.View);

            var repriced=Cluster("speed-change",()=>player.pickSpeed=.75f);
            check(repriced.Result.Status=="completed"&&repriced.Runs==2,"A speed change must drain and reprice only remaining tiles.");
            player.pickSpeed=1f;

            // A required two-block corridor plus an unrelated side block.
            Set(x+2,y-2,TileID.Stone);Set(x+2,y-1,TileID.Stone);Set(x+2,y-4,TileID.Stone);
            var edits=new NativeNavigationEdits(identity,pose.State);
            using var evaluation=new LocalEditEvaluation(pose.State,new LandingRegion(x*16+14,x*16+18,y*16),
                NativeNavigation.Parameters(player),edits);
            for(int n=0;n<100&&!evaluation.Finished;n++)evaluation.Advance();
            var plan=LocalEditEvaluation.Choose(evaluation.Plans,null,false);
            var step=plan==null?null:edits.ArmPlan(plan.Actions);
            check(step?.Batch!=null,"Required side wall must arm one clearance batch.");
            var clearance=new TaskSlot(step.Batch,"corridor",null,identity with{GameUpdate=tick},605);
            Result cleared=null;
            for(int n=0;n<610;n++)
            {
                cleared=clearance.Tick(identity with{GameUpdate=++tick});
                if(cleared.Status!="running")break;
                frame();
            }
            check(cleared.Status=="completed"&&cleared.InputsReleased&&cleared.Evidence is MiningRunReceipt receipt
                &&receipt.verified.Length==2&&receipt.estimatedUpdates==receipt.actualUpdates&&plan.Cost.Mining==receipt.estimatedUpdates
                &&!Main.tile[x+2,y-2].HasTile&&!Main.tile[x+2,y-1].HasTile&&Main.tile[x+2,y-4].HasTile,
                "Clearance must mine only the selected corridor, with a single correctly priced run.");
            return new{excavation=true,isolated,cluster=run,autoRun,manualRun,interrupted=stopProof,partial=partialProof,clearance=cleared.Evidence};
        }
        finally
        {
            if(ActorMining.Running)ActorMining.Stop(player,world,generation);
            (player.position,player.velocity,player.releaseJump,player.pickSpeed,player.inventory[0],player.inventory[1])=saved;
            ActorControl.Driver=savedDriver;
        }
    }

    internal static object RunWithMovement(Player p,string world,long generation,int x,int y,Action<bool,string> check)
    {
        x+=52;y+=4;
        var saved=(p.position,p.velocity,p.releaseJump,p.inventory[0]);
        var savedDriver=ActorControl.Driver;
        var executor=new ServerAiNativeActions();
        var identity=new AiReadOnlyProtocol.Identity(p,world,generation,p.whoAmI,new{},p.selectedItem,Main.GameUpdateCount,true);
        var update=typeof(Main).GetMethod("Update",BindingFlags.Instance|BindingFlags.NonPublic,null,new[]{typeof(GameTime)},null);
        void Frame()=>update.Invoke(Main.instance,new object[]{new GameTime()});
        try
        {
            for(int dx=-6;dx<=16;dx++)
            for(int dy=-6;dy<=2;dy++)
            {
                var tile=Main.tile[x+dx,y+dy];tile.ClearEverything();
                if(dy>=0||dy==-2&&dx is >=2 and <=8)
                {tile.HasTile=true;tile.TileType=dy>=0?TileID.Stone:TileID.Iron;}
            }
            p.position=new Vector2(x*16,y*16-42);p.velocity=Vector2.Zero;p.releaseJump=true;
            p.inventory[0]=new Item(ItemID.BonePickaxe);
            for(int i=0;i<4;i++)Frame();
            float start=p.position.X;
            var handle=executor.Start(identity,"mine_target",JsonSerializer.SerializeToElement(new{x=x+2,y=y-2,cluster=true}),Guid.NewGuid().ToString("N"));
            Result result=null;
            for(int n=0;n<1500;n++)
            {
                Frame();result=executor.Poll(identity,handle);
                if(result.Status!="running")break;
            }
            var task=JsonSerializer.SerializeToElement(result.Evidence).GetProperty("task");
            check(result.Status=="completed"&&result.InputsReleased&&result.EffectVerified&&p.position.X>start+16,
                "Native cluster should navigate to another work position and finish: "+task);
            var children=task.GetProperty("progress").GetProperty("children").EnumerateArray().ToArray();
            var runs=children.Where(c=>c.GetProperty("taskName").GetString()=="excavate_tiles").ToArray();
            check(runs.Length>=2&&children.Any(c=>c.GetProperty("taskName").GetString()=="navigate_to_landing_region")
                &&runs.Sum(c=>c.GetProperty("result").GetProperty("evidence").GetProperty("verified").GetArrayLength())==7,
                "Native multi-position cluster must keep a single root and prove all seven tiles.");
            return new{multiPositionExcavation=true,movedPixels=p.position.X-start,workRuns=runs.Length,verifiedTiles=7};
        }
        finally
        {
            executor.Shutdown();
            (p.position,p.velocity,p.releaseJump,p.inventory[0])=saved;
            ActorControl.Driver=savedDriver;
        }
    }
}
