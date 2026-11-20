using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using CompanionBridge.ServerIpc;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace VirtualPlayerEngineProbe;

internal static partial class ActorItemUse
{
    // Independent oracle for the isolated copied-world test. Keep the normal bound
    // cursor and ItemCheck hooks, but omit the survey being compared against it.
    internal static void SurveyOracle(Player p, ActorPlacement.Context c)
    {
        if (!Environment.GetCommandLineArgs().Contains("-placementsurveyselftest") || Netplay.HasClients)
            throw new InvalidOperationException("Isolated survey fixture required");
        StartPlacement(p,ProbeSession.WorldSession,ProbeSession.ActorGeneration,c);
        try
        {
            executing=Current;
            for(int tick=0;tick<605;tick++)
            {
                bool press=tick==0;
                NativeSurvival.Run(p,()=>NativeItemInput.Run((actor,_)=>actor.ItemCheck(),
                    p,p.whoAmI,ActorPlacement.Input(c,press)));
                if(p.itemAnimation==0 && p.itemTime==0 && p.reuseDelay==0) break;
            }
        }
        finally
        {
            p.controlUseItem=false;p.releaseUseItem=true;
            Current.Phase="finished";executing=null;
        }
    }
}

internal static class PlacementSurveySelfTest
{
    internal static void Run()
    {
        var evidence=new List<object>();
        string report=Path.Combine(Main.SavePath,"placement-native-result.json");
        try
        {
            Main.gameMenu=false; ProbeSession.Spawn();
            var p=ProbeSession.Actor;
            int x=Main.spawnTileX+3,y=Main.spawnTileY+7;
            var wrapped=typeof(Player).GetMethod("ItemCheckWrapped",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
            void Reset(int item,int dir,Action setup)
            {
                for(int dx=-8;dx<=8;dx++)for(int dy=-8;dy<=4;dy++)
                {
                    var t=Main.tile[x+dx,y+dy];t.ClearEverything();
                    if(dy>0){t.HasTile=true;t.TileType=TileID.Grass;}
                }
                p.position=new((x-3*dir)*16,(y+1)*16-p.height);
                p.velocity=Vector2.Zero;p.direction=dir;
                p.inventory[4].SetDefaults(item);p.inventory[4].stack=5;
                ActorItemUse.Select(p,ProbeSession.WorldSession,ProbeSession.ActorGeneration,4);
                setup();
            }
            void Tile(int type,int dx=0,int dy=0)
            {var t=Main.tile[x+dx,y+dy];t.HasTile=true;t.TileType=(ushort)type;}
            void Liquid(int kind)
            {var t=Main.tile[x,y];t.LiquidType=kind;t.LiquidAmount=255;}
            string Cells(bool allFrames=true)=> string.Join("|",from dx in Enumerable.Range(-5,11) from dy in Enumerable.Range(-5,9)
                let t=Main.tile[x+dx,y+dy] select $"{t.HasTile},{t.TileType},{(allFrames||t.HasTile&&Main.tileFrameImportant[t.TileType]?t.TileFrameX:0)},{(allFrames||t.HasTile&&Main.tileFrameImportant[t.TileType]?t.TileFrameY:0)},{t.WallType},{t.LiquidAmount},{t.LiquidType},{t.IsActuated},{t.IsHalfBlock},{t.Slope}");
            var errors=new List<string>();
            void Case(string name,int item,Action setup,string expected=null,int dir=1,bool policy=false)
            {
                try { Compare(name,item,setup,expected,dir,policy); }
                catch(Exception ex){errors.Add(name+": "+ex.Message);ActorItemUse.Retire(p);}
            }
            void Compare(string name,int item,Action setup,string expected=null,int dir=1,bool policy=false)
            {
                Reset(item,dir,setup);
                var held=p.HeldItem;
                string kind=ActorPlacement.UnifiedKind(held);
                string layer=kind=="wall"?"walls":kind=="tile"?"tiles":"objects";
                var args=JsonSerializer.SerializeToElement(new{blueprint=new Dictionary<string,object>{
                    ["origin"]=new{x,y},[layer]=new[]{new{x=0,y=0,itemType=item}}}});
                string before=Cells();
                var npcs=Main.npc.Where(n=>n.active).Select(n=>(n,n.position,n.life)).ToArray();
                var entry=JsonSerializer.SerializeToElement(BuildDiff.Read(p,args)).GetProperty("entries")[0];
                var survey=NativePlacementSurvey.Read(p,held,x,y);
                var refusal=ActorPlacement.PrepareUnified(p,x,y,out var context);
                if(before!=Cells() || p.HeldItem.stack!=5 || npcs.Any(n=>n.n.position!=n.position || n.n.life!=n.life) ||
                    survey.Status!=entry.GetProperty("survey").GetString() || survey.Attempt!=(refusal==null) ||
                    expected!=null && survey.Status!=expected)
                    throw new Exception($"{name}: inconsistent survey {entry}, refusal={refusal}, expected={expected}");
                // Rebuild the same scene without calling the survey or action preflight.
                Reset(item,dir,setup);
                bool obj=kind is "object" or "chest";
                var raw=new ActorPlacement.Context{X=x,Y=y,TileType=held.createTile,WallType=Math.Max(0,held.createWall),
                    Prefix=held.prefix,Style=held.placeStyle,Object=obj,Shape=obj?ActorObjectPlacement.Definition(held):null};
                ActorItemUse.SurveyOracle(p,raw);
                bool native=raw.Placed && raw.NativeEvents==1 && p.HeldItem.stack==4 &&
                    (obj?ActorObjectPlacement.ReadFrames(raw)!=null:ActorPlacement.Present(raw));
                string nativeCells=Cells(false);
                evidence.Add(new{name,entry,native,raw.NativeEvents,raw.NativeClearEvents,raw.Alternate,
                    raw.Direction, frameX=Main.tile[x,y].TileFrameX,frameY=Main.tile[x,y].TileFrameY,
                    placedType=Main.tile[x,y].TileType, raw.Placed,stack=p.HeldItem.stack,inputsReleased=!p.controlUseItem && p.releaseUseItem,refusal});
                if(!policy && survey.Status!="native_attempt_required" && survey.Attempt!=native)
                    throw new Exception($"{name}: survey {survey.Status}, native={native}, events={raw.NativeEvents}, stack={p.HeldItem.stack}");
                Reset(item,dir,setup);
                if(survey.Attempt)
                {
                    if(ActorPlacement.PrepareUnified(p,x,y,out context)!=null)throw new Exception(name+" unstable preflight");
                    ActorItemUse.StartPlacement(p,ProbeSession.WorldSession,ProbeSession.ActorGeneration,context);
                    for(int i=0;ActorItemUse.Running && i<605;i++)
                        NativeSurvival.Run(p,()=>wrapped.Invoke(p,new object[]{p.whoAmI}));
                    if((ActorItemUse.Current.Phase=="completed")!=native || Cells(false)!=nativeCells ||
                        native && (context.ConsumedCount!=1 || context.NativeEvents!=1 || !ActorPlacement.Present(context)) ||
                        !p.releaseUseItem || p.controlUseItem || p.itemTime!=0 || p.itemAnimation!=0)
                        throw new Exception($"{name}: ItemCheck difference {ActorItemUse.Current.Phase}/{ActorItemUse.Current.Reason}");
                }
            }
            Case("wood-empty",ItemID.Wood,()=>{},"ready");
            Case("wood-cut",ItemID.Wood,()=>Tile(TileID.Plants),"native_replaceable");
            Case("wood-breakable",ItemID.Wood,()=>Tile(TileID.SmallPiles),"native_replaceable");
            Case("grass-terrain",ItemID.Wood,()=>Tile(TileID.Grass),"occupied_requires_dig");
            Case("pumpkin",ItemID.Wood,()=>Tile(TileID.Pumpkins),"occupied_requires_dig",policy:true);
            Case("chair-cut",ItemID.WoodenChair,()=>Tile(TileID.Plants),"native_replaceable");
            Case("door-cut",ItemID.WoodenDoor,()=>{Tile(TileID.WoodBlock,0,-3);Tile(TileID.Plants);},"native_replaceable");
            Case("bed-right",ItemID.Bed,()=>Tile(TileID.Plants),"native_replaceable");
            Case("bed-left",ItemID.Bed,()=>Tile(TileID.Plants),"native_replaceable",-1);
            Case("bench-stone",ItemID.WorkBench,()=>Tile(TileID.Stone,1),"occupied_requires_dig");
            Case("bench-water",ItemID.WorkBench,()=>Liquid(LiquidID.Water));
            Case("bench-lava",ItemID.WorkBench,()=>Liquid(LiquidID.Lava));
            Case("chair-platform",ItemID.WoodenChair,()=>Tile(TileID.Platforms,0,1));
            Case("chair-rope",ItemID.WoodenChair,()=>Tile(TileID.Rope,0,1));
            Case("chair-half",ItemID.WoodenChair,()=>{var t=Main.tile[x,y+1];t.IsHalfBlock=true;});
            Case("chair-slope",ItemID.WoodenChair,()=>{var t=Main.tile[x,y+1];t.Slope=SlopeType.SlopeDownLeft;});
            Case("chair-actuated",ItemID.WoodenChair,()=>{var t=Main.tile[x,y+1];t.IsActuated=true;});
            Case("bench-player",ItemID.WorkBench,()=>p.position=new(x*16,(y+1)*16-p.height),"blocked_by_player",policy:true);
            Case("bench-container",ItemID.WorkBench,()=>Tile(TileID.Containers,1),"protected_or_occupied");
            Case("bench-furniture",ItemID.WorkBench,()=>Tile(TileID.Chairs,1),"protected_or_occupied");
            Case("wall-foreground-water",ItemID.WoodWall,()=>{Tile(TileID.WoodBlock);Liquid(LiquidID.Water);},"ready");
            Case("wall-empty",ItemID.WoodWall,()=>{},"ready");
            Case("wall-existing",ItemID.WoodWall,()=>{var t=Main.tile[x,y];t.WallType=WallID.Stone;},"wall_conflict",policy:true);
            Case("door-origin-middle",ItemID.WoodenDoor,()=>{
                Tile(TileID.WoodBlock,0,-2);Main.tile[x,y+1].ClearEverything();Tile(TileID.WoodBlock,0,2);});
            Case("switch-left",ItemID.Switch,()=>{Main.tile[x,y+1].ClearEverything();Tile(TileID.WoodBlock,-1);});
            Case("switch-right",ItemID.Switch,()=>{Main.tile[x,y+1].ClearEverything();Tile(TileID.WoodBlock,1);});
            Case("wall-isolated-foreground",ItemID.WoodWall,()=>{Main.tile[x,y+1].ClearEverything();Tile(TileID.Stone);},"missing_support_or_anchor");
            // Native clear succeeded, then the final placement call was refused.
            Reset(ItemID.Wood,1,()=>Tile(TileID.Plants));
            if(ActorPlacement.PrepareUnified(p,x,y,out var partial)!=null)throw new Exception("partial preflight");
            bool Deny(On_WorldGen.orig_PlaceTile orig,int i,int j,int type,bool mute,bool forced,int plr,int style) =>
                i==x && j==y && type==TileID.WoodBlock ? false : orig(i,j,type,mute,forced,plr,style);
            On_WorldGen.PlaceTile+=Deny;
            try
            {
                ActorItemUse.StartPlacement(p,ProbeSession.WorldSession,ProbeSession.ActorGeneration,partial);
                for(int i=0;ActorItemUse.Running && i<605;i++)NativeSurvival.Run(p,()=>wrapped.Invoke(p,new object[]{p.whoAmI}));
                if(ActorItemUse.Current.Phase!="unknown" || partial.Attempts!=1 || partial.NativeClearEvents==0 ||
                    Main.tile[x,y].HasTile || p.HeldItem.stack!=5 || p.controlUseItem || !p.releaseUseItem)
                    throw new Exception("native clear without placement was not independently verified as unknown: "+ActorItemUse.Current.Reason);
                evidence.Add(new{name="partial-clear",partial.NativeClearEvents,partial.Attempts,ActorItemUse.Current.Phase});
            }
            finally{On_WorldGen.PlaceTile-=Deny;}
            if(errors.Count!=0)throw new Exception(string.Join("\n",errors));
            ProbeSession.Finish("placement survey test complete",true);
            File.WriteAllText(report,JsonSerializer.Serialize(new{result="passed",evidence}));
        }
        catch(Exception ex){File.WriteAllText(report,JsonSerializer.Serialize(new{result="failed",evidence,error=ex.ToString()}));}
        finally{Netplay.Disconnect=true;}
    }
}
