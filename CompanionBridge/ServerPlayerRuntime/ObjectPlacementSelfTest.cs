using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ObjectData;
using CompanionBridge.ServerIpc;

namespace VirtualPlayerEngineProbe;

// Opt-in copied-world diagnostic only. Scenario injection is never an executor.
internal static class ObjectPlacementSelfTest
{
    internal static void Run(bool failure)
    {
        int checks=0;var evidence=new List<object>();string report=Path.Combine(Main.SavePath,"placement-native-result.json");
        void Check(bool ok,string why){if(!ok)throw new InvalidOperationException(why);checks++;evidence.Add(new{check=why,passed=true});}
        try
        {
            Main.gameMenu=false;ProbeSession.Spawn();var p=ProbeSession.Actor;
            var human=Main.player[0];var bag=human.inventory.Select(i=>(i.type,i.stack)).ToArray();
            int local=Main.myPlayer,tx=Player.tileTargetX,ty=Player.tileTargetY;bool mouse=Main.blockMouse;
            var d=TileObjectData.GetTileData(TileID.WorkBenches,0);
            evidence.Add(new{definition=new{d.Width,d.Height,origin=new{x=d.Origin.X,y=d.Origin.Y},d.CoordinateWidth,d.CoordinatePadding,d.CoordinateHeights,
                anchorCount=d.AnchorBottom.tileCount,anchorStart=d.AnchorBottom.checkStart,anchorType=d.AnchorBottom.type.ToString(),custom=TileObjectData.CustomPlace(TileID.WorkBenches,0)}});
            Check(ActorObjectPlacement.Definition()!=null,"Unsupported loaded Work Bench definition: "+JsonSerializer.Serialize(evidence));
            int x=Main.spawnTileX+3,floor=Main.spawnTileY+8,y=floor-1;
            var id=new AiReadOnlyProtocol.Identity(p,ProbeSession.WorldSession,ProbeSession.ActorGeneration,p.whoAmI,new{},0,Main.GameUpdateCount,true);
            var executor=new ServerAiNativeActions();
            var wrapped=typeof(Player).GetMethod("ItemCheckWrapped",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
            void Frame()=>NativeSurvival.Run(p,()=>wrapped.Invoke(p,new object[]{p.whoAmI}));
            bool Released()=>!p.controlUseItem&&p.releaseUseItem&&!p.channel&&p.itemAnimation==0&&p.itemTime==0;
            void Setup(int type=ItemID.WorkBench,int stack=5)
            {
                for(int xx=x-10;xx<=x+10;xx++)for(int yy=floor-8;yy<=floor+4;yy++)
                {var t=Main.tile[xx,yy];t.ClearEverything();if(yy>=floor){t.HasTile=true;t.TileType=TileID.Dirt;}}
                p.position=new((x-3)*16,floor*16-p.height);p.velocity=Vector2.Zero;
                p.inventory[4].SetDefaults(type);p.inventory[4].stack=stack;
                ActorItemUse.Select(p,id.World,id.Generation,4);
            }
            object Start(int? xx=null,int? yy=null)=>executor.Start(id,"place_object",JsonSerializer.SerializeToElement(new{x=xx??x,y=yy??y}),Guid.NewGuid().ToString("N"));
            AiAtomicActions.NativeResult Finish(object handle)
            {
                for(int i=0;ActorItemUse.Running&&i<605;i++)Frame();
                var r=executor.Poll(id,handle);evidence.Add(r);return r;
            }
            AiAtomicActions.NativeResult Refused(int? xx=null,int? yy=null){var r=executor.Poll(id,Start(xx,yy));evidence.Add(r);return r;}
            Setup();
            if(failure)
            {
                bool afterException=Environment.GetCommandLineArgs().Contains("-objectplacementafterexception");
                Start();PlacementSelfTest.Throw=!afterException;bool threw=false;
                try{Frame();}catch{threw=true;}finally{PlacementSelfTest.Throw=false;}
                Check(threw&&ProbeSession.Quarantined&&Released()&&!ActorItemUse.Running
                    &&ActorItemUse.Current.Phase=="unknown"
                    &&Main.tile[x,y].HasTile==afterException&&Main.tile[x+1,y].HasTile==afterException&&p.inventory[4].stack==5
                    &&ProbeSession.Reservation.IsReserved(p.whoAmI),"native exception did not release/quarantine or preserve partial effects");
                evidence.Add(new{afterException,placed=ActorItemUse.Current.Placement.Placed,stack=p.inventory[4].stack,ProbeSession.Quarantined,
                    reserved=ProbeSession.Reservation.IsReserved(p.whoAmI),inputsReleased=Released()});
                File.WriteAllText(report,JsonSerializer.Serialize(new{result="passed",checks,evidence,processTerminationRequired=true,nativeObjectException=true}));return;
            }
            var initialScene=JsonSerializer.SerializeToElement(ServerSceneReader.Read(p,id,JsonSerializer.SerializeToElement(new{})),new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
            var candidate=initialScene.GetProperty("objectPlacementCandidates").GetProperty("items").EnumerateArray().Single(e=>e.GetProperty("target").GetProperty("x").GetInt32()==x&&e.GetProperty("target").GetProperty("y").GetInt32()==y);
            evidence.Add(new{nativeCandidate=candidate.Clone(),coverage=initialScene.GetProperty("coverage").Clone()});
            Check(candidate.GetProperty("footprint").GetProperty("width").GetInt32()==d.Width&&candidate.GetProperty("support").GetProperty("width").GetInt32()==d.AnchorBottom.tileCount,"native scene omitted full candidate occupancy/support");
            var placed=Finish(Start());var c=ActorItemUse.Current;
            evidence.Add(new{nativeFootprint=Enumerable.Range(0,d.Width).Select(dx=>{var t=Main.tile[x+dx,y];return new{x=x+dx,y,t.HasTile,t.TileType,t.TileFrameX,t.TileFrameY};}).ToArray()});
            Check(placed.Status=="completed"&&placed.EffectVerified&&placed.InputsReleased&&Released()
                &&c.Placement.Placed&&ActorObjectPlacement.Present(c.Placement)&&c.Placement.ConsumedCount==1
                &&p.inventory[4].stack==4&&c.Placement.Attempts==1&&c.Starts==1,"native full object/consumption failed");
            var scene=JsonSerializer.SerializeToElement(ServerSceneReader.Read(p,id,JsonSerializer.SerializeToElement(new{})),new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
            Check(scene.GetProperty("objects").GetProperty("items").EnumerateArray().Count(o=>o.GetProperty("kind").GetString()=="workbench")==1,"one bench became multiple objects");
            // Choose from the actual loaded registry, then query native adjacency
            // before and after Player placement, never write adjTile in the fixture.
            Setup();int recipeId=-1;JsonElement before=default;
            for(int i=0;i<Recipe.numRecipes;i++)
            {
                var r=Main.recipe[i];
                if(r.Mod!=null||r.Disabled||r.requiredTile.Count!=1||r.requiredTile[0]!=TileID.WorkBenches||r.Conditions.Count!=0||r.requiredItem.Count>8)continue;
                var query=JsonSerializer.SerializeToElement(ActorCrafting.ReadRecipes(id,JsonSerializer.SerializeToElement(new{resultType=r.createItem.type,availableOnly=false,limit=100})));
                var entries=query.GetProperty("items").EnumerateArray().Where(e=>e.GetProperty("recipeId").GetInt32()==i&&e.GetProperty("supported").GetBoolean()).ToArray();
                if(entries.Length==0)continue;recipeId=i;before=entries[0].Clone();break;
            }
            Check(recipeId>=0,"no ordinary supported native bench recipe found");
            var recipe=Main.recipe[recipeId];
            for(int i=0;i<recipe.requiredItem.Count;i++){p.inventory[10+i].SetDefaults(recipe.requiredItem[i].type);p.inventory[10+i].stack=recipe.requiredItem[i].stack;}
            Check(!before.GetProperty("stations")[0].GetProperty("adjacent").GetBoolean()&&before.GetProperty("reason").GetString()=="missing_crafting_station","missing station not observed");
            Check(Finish(Start()).EffectVerified,"recipe scenario bench not placed");
            var after=JsonSerializer.SerializeToElement(ActorCrafting.ReadRecipes(id,JsonSerializer.SerializeToElement(new{resultType=recipe.createItem.type,availableOnly=false,limit=100})))
                .GetProperty("items").EnumerateArray().Single(e=>e.GetProperty("recipeId").GetInt32()==recipeId).Clone();
            Check(after.GetProperty("stations")[0].GetProperty("adjacent").GetBoolean()&&after.GetProperty("available").GetBoolean(),"placed world bench not reflected in native recipe query");
            var craft=executor.Start(id,"craft_recipe",JsonSerializer.SerializeToElement(new{recipeId,recipeToken=after.GetProperty("recipeToken").GetString(),crafts=1}),"object-native-craft");
            var crafted=executor.Poll(id,craft);evidence.Add(new{recipeId,before,after,crafted});
            Check(crafted.Status=="completed"&&crafted.EffectVerified,"native station recipe craft failed");
            Setup(stack:1);Check(Finish(Start()).EffectVerified&&p.inventory[4].IsAir,"last bench stack not consumed");
            foreach(int type in new[]{ItemID.Wood,ItemID.WoodenSword,ItemID.WoodenChair,ItemID.Furnace})
            {Setup(type);Check(Refused().Reason=="unsupported_selected_object_item","wrong item accepted: "+type);}
            Setup();p.HeldItem.placeStyle=1;Check(Refused().Reason=="unsupported_selected_object_item","non-default style accepted");
            Setup();var blocked=Main.tile[x+1,y];blocked.HasTile=true;blocked.TileType=TileID.Stone;
            Check(Refused().Reason=="object_footprint_occupied","second footprint cell ignored");
            Setup();Main.tile[x+1,floor].ClearEverything();Check(Refused().Reason=="object_support_missing","second support ignored");
            Setup();Check(Refused(Main.maxTilesX-2,y).Reason=="invalid_target","full bounds ignored");
            Check(Refused(x+20,y).Reason=="out_of_conservative_80_pixel_reach","distance ignored");
            Setup();p.position=new(x*16,floor*16-p.height);Check(Refused().Reason=="object_player_collision","actor overlap accepted");
            // No real client owns this collider. Netplay resets disconnected client
            // slots concurrently, so use the non-network sentinel only as a saved/
            // restored geometry surrogate; this does not validate live multiplayer.
            Setup();var body=Main.player[Main.player.Length-1];var bodyPosition=body.position;
            bool bodyActive=body.active,bodyDead=body.dead,bodyGhost=body.ghost;int bodyWidth=body.width,bodyHeight=body.height;
            try
            {
                body.active=true;body.dead=body.ghost=false;body.width=20;body.height=42;body.position=new((x+1)*16,y*16);
                evidence.Add(new{colliderSurrogate=new{slot=Main.player.Length-1,body.active,body.dead,body.ghost,position=body.position.ToString(),bounds=body.Hitbox.ToString(),preflight=ActorObjectPlacement.Preflight(p,x,y)},liveMultiplayer=false});
                Check(Refused().Reason=="object_player_collision","other player bounds exclusion failed");
            }
            finally{body.position=bodyPosition;body.active=bodyActive;body.dead=bodyDead;body.ghost=bodyGhost;body.width=bodyWidth;body.height=bodyHeight;}
            Setup();Main.tile[x+1,y].LiquidAmount=1;Check(Refused().Reason=="object_footprint_liquid","shallow liquid accepted");
            Setup();bool stale=false;try{executor.Start(id with{Generation=id.Generation+1},"place_object",JsonSerializer.SerializeToElement(new{x,y}),"stale");}catch{stale=true;}
            Check(stale&&!Main.tile[x,y].HasTile,"old identity accepted");
            Setup();var pending=Start();Check(Refused().Reason=="actor_busy","overlapping action accepted");executor.Cancel(id,pending);Check(Released(),"pre-input cancellation failed");
            Setup();pending=Start();blocked=Main.tile[x+1,y];blocked.HasTile=true;blocked.TileType=TileID.Stone;Frame();
            Check(executor.Poll(id,pending).Status=="unknown"&&p.inventory[4].stack==5&&Released(),"changed footprint adopted");
            Setup();pending=Start();Main.tile[x+1,floor].ClearEverything();Frame();
            Check(executor.Poll(id,pending).Status=="unknown"&&p.inventory[4].stack==5&&Released(),"changed support was used");
            Setup();pending=Start();p.inventory[4]=p.inventory[4].Clone();Frame();
            Check(executor.Poll(id,pending).Status=="cancelled"&&p.inventory[4].stack==5&&Released(),"changed item object was used");
            Setup();pending=Start();Frame();WorldGen.KillTile(x+1,y);Frame();var changed=executor.Poll(id,pending);evidence.Add(changed);
            Check(changed.Status=="unknown"&&!changed.EffectVerified&&Released(),"partial destruction adopted or hidden");
            Check(executor.Cancel(id,pending).Status=="unknown","cancel erased uncertain object outcome");
            Setup();pending=Start();Frame();var cancel=executor.Cancel(id,pending);evidence.Add(cancel);
            Check(cancel.Status=="cancelled"&&Released()&&ActorObjectPlacement.Present(ActorItemUse.Current.Placement)&&p.inventory[4].stack==4,"cancel rolled back side effects or held input");
            // No additional Frame() between the mutation/first use and Cancel:
            // this is the window where a cached running phase hid uncertainty.
            Setup();pending=Start();PlacementSelfTest.DenyConsumption=true;
            try{Frame();}finally{PlacementSelfTest.DenyConsumption=false;}
            int cancelTick=ActorItemUse.Current.Ticks;var immediate=executor.Cancel(id,pending);evidence.Add(new{caseId="immediate_unconfirmed_consumption",immediate});
            Check(immediate.Status=="unknown"&&immediate.InputsReleased&&!immediate.EffectVerified&&Released()
                &&ActorItemUse.Current.Ticks==cancelTick&&ActorObjectPlacement.Present(ActorItemUse.Current.Placement)&&p.inventory[4].stack==5,
                "immediate cancellation hid unconfirmed consumption or advanced native execution");
            ActorItemUse.Retire(p);ActorItemUse.Stop(p,id.World,id.Generation);
            Check(executor.Cancel(id,pending).Status=="unknown"&&Released(),"repeated Retire/Stop downgraded unknown");
            foreach(bool support in new[]{false,true})
            {
                Setup();pending=Start();Frame();cancelTick=ActorItemUse.Current.Ticks;
                // Raw fixture mutation deliberately bypasses KillTile, ensuring
                // cancellation rereads the complete footprint/support itself.
                Main.tile[x+1,support?floor:y].ClearEverything();
                immediate=executor.Cancel(id,pending);evidence.Add(new{caseId=support?"immediate_changed_support":"immediate_changed_footprint",immediate});
                Check(immediate.Status=="unknown"&&immediate.InputsReleased&&!immediate.EffectVerified&&Released()
                    &&ActorItemUse.Current.Ticks==cancelTick&&p.inventory[4].stack==4,"immediate cancellation adopted changed footprint/support");
            }
            foreach(bool cancelDirectly in new[]{false,true})
            {
                Setup();pending=Start();PlacementSelfTest.DenyConsumption=true;
                try{Frame();}finally{PlacementSelfTest.DenyConsumption=false;}
                p.noItems=true;
                try{if(cancelDirectly)immediate=executor.Cancel(id,pending);else {Frame();immediate=executor.Poll(id,pending);}}
                finally{p.noItems=false;}
                evidence.Add(new{caseId=cancelDirectly?"disabled_immediate_cancel":"disabled_update",immediate});
                Check(immediate.Status=="unknown"&&immediate.InputsReleased&&Released()&&p.inventory[4].stack==5,"actor state cleanup downgraded uncertain placement");
            }
            Setup();pending=Start();PlacementSelfTest.DenyConsumption=true;
            try{Frame();}finally{PlacementSelfTest.DenyConsumption=false;}
            p.dead=true;
            try{immediate=executor.Cancel(id,pending);}finally{p.dead=false;}
            evidence.Add(new{caseId="dead_immediate_cancel",immediate});
            Check(immediate.Status=="unknown"&&immediate.InputsReleased&&Released()&&p.inventory[4].stack==5,"dead actor cancellation lost unknown or input release");
            Setup();pending=Start();Frame();p.inventory[4]=p.inventory[4].Clone();Frame();
            Check(executor.Poll(id,pending).Status=="unknown"&&Released(),"selected item cleanup downgraded uncertain inventory identity");
            Setup();pending=Start();PlacementSelfTest.DenyConsumption=true;
            try{Frame();}finally{PlacementSelfTest.DenyConsumption=false;}
            ActorItemUse.Current.Ticks=600;Frame();
            Check(executor.Poll(id,pending).Status=="unknown"&&Released(),"deadline overwrote uncertain consumption");
            Setup();pending=Start();Frame();ActorItemUse.Current.Ticks=600;Frame();var timeout=executor.Poll(id,pending);evidence.Add(timeout);
            Check(timeout.Status=="timeout"&&Released()&&p.inventory[4].stack==4,"timeout lost release or rolled back");
            Setup();PlacementSelfTest.DenyTile=true;AiAtomicActions.NativeResult denied;
            try{denied=Finish(Start());}finally{PlacementSelfTest.DenyTile=false;}
            Check(denied.Status=="failed"&&!Main.tile[x,y].HasTile&&p.inventory[4].stack==5,"native placement permission bypassed");
            Setup();PlacementSelfTest.DenyConsumption=true;AiAtomicActions.NativeResult unconsumed;
            try{unconsumed=Finish(Start());}finally{PlacementSelfTest.DenyConsumption=false;}
            Check(unconsumed.Status=="unknown"&&!unconsumed.EffectVerified&&Main.tile[x,y].HasTile&&Main.tile[x+1,y].HasTile&&p.inventory[4].stack==5&&Released(),"ambiguous consumption falsely succeeded or rolled back");
            Check(ReferenceEquals(human,Main.player[0])&&bag.SequenceEqual(human.inventory.Select(i=>(i.type,i.stack)))
                &&Main.myPlayer==local&&Player.tileTargetX==tx&&Player.tileTargetY==ty&&Main.blockMouse==mouse,"human/UI state changed");
            executor.Shutdown();ProbeSession.Finish("object placement diagnostic complete",true);
            File.WriteAllText(report,JsonSerializer.Serialize(new{result="passed",checks,evidence,mode="isolated native ItemCheckWrapped; copied world; real placement/consumption/AdjTiles/craft; no graphical Host & Play"}));
        }
        catch(Exception e){File.WriteAllText(report,JsonSerializer.Serialize(new{result="failed",checks,evidence,error=e.ToString()}));}
        finally{PlacementSelfTest.DenyTile=PlacementSelfTest.DenyConsumption=PlacementSelfTest.Throw=false;if(!failure)Netplay.Disconnect=true;}
    }
}
