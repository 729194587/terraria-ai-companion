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

// The same fixture also runs against the audit commit. Keep observations in
// the receipt and apply version-specific acceptance criteria in the verifier.
internal static class TerrainApproachSelfTest
{
    internal static object Run(Player p,string world,long generation)
    {
        var results=new List<object>();
        int x=Main.spawnTileX+65,y=Main.spawnTileY-12;
        var update=typeof(Main).GetMethod("Update",BindingFlags.Instance|BindingFlags.NonPublic,null,new[]{typeof(GameTime)},null);
        void Frame()
        {
            foreach(var npc in Main.npc)if(npc.active&&!npc.friendly&&!npc.townNPC)npc.active=false;
            update.Invoke(Main.instance,new object[]{new GameTime()});
        }
        void Set(int dx,int dy,int type)
        {
            var tile=Main.tile[x+dx,y+dy];tile.ClearEverything();
            if(type>=0){tile.HasTile=true;tile.TileType=(ushort)type;}
        }
        bool shared=Environment.GetCommandLineArgs().Contains("-sharedterrainselftest");
        var scenes=shared?new[]{"nav_detour","nav_fast","nav_slow","nav_thin","nav_sequential","nav_support",
            "ore_support","ore_stock","ore_gear","ore_target","nav_cancel","ore_stop_place","nav_deck"}
            :new[]{"direct","detour","fast","slow","thin","sequential","single","dead_end",
                "path_changed","stop_clearing","stop_after_cut","falling_sand"};
        foreach(string name in scenes)
        for(int repeat=0;repeat<(name is "fast" or "slow" or "thin" or "detour" or "single" or "nav_fast" or "nav_slow"
            or "nav_sequential" or "nav_support" or "ore_support"?3:1);repeat++)
        {
            string scene=name.Split('_',2)[^1];
            if(!shared)scene=name;
            bool nav=name.StartsWith("nav_");
            bool gap=scene is "support" or "stock" or "stop_place" or "deck";
            for(int dx=-18;dx<=32;dx++)
            for(int dy=-20;dy<=9;dy++)Set(dx,dy,dy>=0?TileID.Stone:-1);
            int ore=scene=="direct"?3:scene is "sequential" or "single" or "dead_end" or "path_changed" or "gear" or "target" or "cancel"?13:10;
            int oreY=-2;
            if(scene is "fast" or "slow")
            {
                ore=3;oreY=5;
                for(int dx=-18;dx<=32;dx++)
                for(int dy=0;dy<=3;dy++)
                    Set(dx,dy,dy==0&&dx<=2?(nav?TileID.Stone:TileID.Dirt):-1);
            }
            else if(gap)
            {
                ore=12;
                for(int dx=-16;dx<=25;dx++)Set(dx,-4,TileID.Stone);
                for(int dx=2;dx<=5;dx++)
                for(int dy=0;dy<18;dy++)Set(dx,dy,-1);
            }
            else if(scene=="detour")Set(4,-1,TileID.Dirt);
            else if(scene!="direct")
            {
                foreach(int dx in ore==13?new[]{2,7}:new[]{3})
                for(int dy=-14;dy<0;dy++)Set(dx,dy,dx==7&&scene=="dead_end"?TileID.LihzahrdBrick:TileID.Dirt);
            }
            if(scene=="falling_sand")Set(3,-4,TileID.Sand);
            if(!nav)Set(ore,oreY,TileID.Iron);
            int goalX=scene is "fast" or "slow"?0:scene=="deck"?4:ore;
            int goalY=scene is "fast" or "slow"?4:0;
            if(shared)
            {
                foreach(var item in Main.item)
                    if(item.active&&Math.Abs(item.Center.X-x*16)<40*16&&Math.Abs(item.Center.Y-y*16)<25*16)item.TurnToAir();
                for(int slot=0;slot<50;slot++)p.inventory[slot]=new Item();
            }
            p.position=new Vector2(x*16,y*16-42);p.velocity=Vector2.Zero;p.jump=0;p.releaseJump=true;
            p.statLife=p.statLifeMax2;p.inventory[0]=new Item(scene=="slow"?ItemID.CopperPickaxe:scene=="fast"?ItemID.ShroomiteDiggingClaw:ItemID.BonePickaxe);
            if(scene=="fast")p.inventory[0].Prefix(PrefixID.Light);
            p.inventory[1]=new Item();p.selectedItem=0;p.hitTile.ClearThisTile(x+ore,y+oreY);
            if(gap)p.inventory[1]=new Item(ItemID.DirtBlock,scene=="stock"?1:8);
            for(int n=0;n<4;n++)Frame();
            var start=new{p.position.X,p.position.Y,goalX=(x+goalX)*16+8,goalY=(y+goalY)*16,
                oreX=x+ore,oreY=y+oreY,tool=p.inventory[0].type,prefix=p.inventory[0].prefix,stock=p.inventory[1].stack};
            var executor=new ServerAiNativeActions();
            var identity=new AiReadOnlyProtocol.Identity(p,world,generation,p.whoAmI,new{},p.selectedItem,Main.GameUpdateCount,true);
            try
            {
                var arguments=nav?JsonSerializer.SerializeToElement(new{x=start.goalX,y=start.goalY}):
                    JsonSerializer.SerializeToElement(new{x=x+ore,y=y+oreY,cluster=scene!="single"});
                var handle=executor.Start(identity,nav?"navigate_to":"mine_target",arguments,Guid.NewGuid().ToString("N"));
                AiAtomicActions.NativeResult result=null;
                int ticks=0;
                bool injected=false;
                int clearedAtChange=0;
                int WallCuts()
                {
                    int removed=0;
                    foreach(int dx in ore==13?new[]{2,7}:new[]{3})
                    for(int dy=-3;dy<0;dy++)if(!Main.tile[x+dx,y+dy].HasTile)removed++;
                    return removed;
                }
                do
                {
                    Frame();ticks++;result=executor.Poll(identity,handle);
                    if(result.Status!="running"||injected)continue;
                    if(scene=="target"&&WallCuts()>0){injected=true;Set(ore,oreY,TileID.Copper);}
                    if(scene=="gear"&&WallCuts()>0){injected=true;p.inventory[0]=new Item();}
                    if((scene=="cancel"&&WallCuts()>0)||(scene=="stop_place"&&Enumerable.Range(2,4).Any(dx=>Main.tile[x+dx,y].HasTile)))
                    {injected=true;clearedAtChange=WallCuts();result=executor.Cancel(identity,handle);}
                    if(scene=="path_changed"&&WallCuts()>0)
                    {
                        injected=true;clearedAtChange=WallCuts();
                        for(int dy=-14;dy<0;dy++)Set(7,dy,TileID.LihzahrdBrick);
                    }
                    if(scene=="stop_clearing"&&ActorMining.Running||scene=="stop_after_cut"&&WallCuts()>0)
                    {
                        injected=true;clearedAtChange=WallCuts();result=executor.Cancel(identity,handle);
                    }
                }while(result.Status=="running"&&ticks<2800);
                var task=JsonSerializer.SerializeToElement(result.Evidence).GetProperty("task");
                var row=new{scene=name,repeat,ticks,start,finalX=p.position.X,finalFeet=p.Bottom.Y,
                    placed=gap?Enumerable.Range(2,4).Where(dx=>Main.tile[x+dx,y].HasTile).Select(dx=>x+dx).ToArray():Array.Empty<int>(),
                    stockLeft=p.inventory[1].stack,tool=p.inventory[0].type,prefix=p.inventory[0].prefix,injected,clearedAtChange,
                    controlsReleased=!p.controlLeft&&!p.controlRight&&!p.controlJump&&!p.controlUseItem,result.Status,result.Reason,result.InputsReleased,
                    result.EffectVerified,oreAbsent=!Main.tile[x+ore,y+oreY].HasTile,task};
                results.Add(row);
                File.WriteAllText(Path.Combine(Main.SavePath,$"approach-{name}-{repeat}.json"),JsonSerializer.Serialize(row));
            }
            finally{executor.Shutdown();}
        }
        return results;
    }
}
