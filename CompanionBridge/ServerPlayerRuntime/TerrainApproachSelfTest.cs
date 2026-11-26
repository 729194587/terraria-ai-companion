using System;
using System.Collections.Generic;
using System.IO;
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
        foreach(string scene in new[]{"direct","detour","fast","slow","thin","sequential","single","dead_end",
            "path_changed","stop_clearing","stop_after_cut","falling_sand"})
        for(int repeat=0;repeat<(scene is "fast" or "slow" or "thin" or "detour" or "single"?3:1);repeat++)
        {
            for(int dx=-18;dx<=32;dx++)
            for(int dy=-20;dy<=9;dy++)Set(dx,dy,dy>=0?TileID.Stone:-1);
            int ore=scene=="direct"?3:scene is "sequential" or "single" or "dead_end" or "path_changed"?13:10;
            int oreY=-2;
            if(scene is "fast" or "slow")
            {
                ore=3;oreY=5;
                for(int dx=-18;dx<=32;dx++)
                for(int dy=0;dy<=3;dy++)
                    Set(dx,dy,dy==0&&dx<=2?TileID.Dirt:-1);
            }
            else if(scene=="detour")Set(4,-1,TileID.Dirt);
            else if(scene!="direct")
            {
                foreach(int dx in ore==13?new[]{2,7}:new[]{3})
                for(int dy=-14;dy<0;dy++)Set(dx,dy,dx==7&&scene=="dead_end"?TileID.LihzahrdBrick:TileID.Dirt);
            }
            if(scene=="falling_sand")Set(3,-4,TileID.Sand);
            Set(ore,oreY,TileID.Iron);
            p.position=new Vector2(x*16,y*16-42);p.velocity=Vector2.Zero;p.jump=0;p.releaseJump=true;
            p.statLife=p.statLifeMax2;p.inventory[0]=new Item(scene=="slow"?ItemID.CopperPickaxe:scene=="fast"?ItemID.ShroomiteDiggingClaw:ItemID.BonePickaxe);
            if(scene=="fast")p.inventory[0].Prefix(PrefixID.Light);
            p.inventory[1]=new Item();p.selectedItem=0;p.hitTile.ClearThisTile(x+ore,y+oreY);
            for(int n=0;n<4;n++)Frame();
            var executor=new ServerAiNativeActions();
            var identity=new AiReadOnlyProtocol.Identity(p,world,generation,p.whoAmI,new{},p.selectedItem,Main.GameUpdateCount,true);
            try
            {
                var handle=executor.Start(identity,"mine_target",JsonSerializer.SerializeToElement(new{x=x+ore,y=y+oreY,cluster=scene!="single"}),Guid.NewGuid().ToString("N"));
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
                var row=new{scene,repeat,ticks,tool=p.inventory[0].type,prefix=p.inventory[0].prefix,injected,clearedAtChange,
                    controlsReleased=!p.controlLeft&&!p.controlRight&&!p.controlJump&&!p.controlUseItem,result.Status,result.Reason,result.InputsReleased,
                    result.EffectVerified,oreAbsent=!Main.tile[x+ore,y+oreY].HasTile,task};
                results.Add(row);
                File.WriteAllText(Path.Combine(Main.SavePath,$"approach-{scene}-{repeat}.json"),JsonSerializer.Serialize(row));
            }
            finally{executor.Shutdown();}
        }
        return results;
    }
}
