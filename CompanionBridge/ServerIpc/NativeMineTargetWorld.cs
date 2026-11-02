using System;
using System.Linq;
using System.Text.Json;
using Terraria;
using Terraria.ID;
using CompanionBridge.Common.Systems;
using CompanionBridge.Motion;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

internal sealed class NativeMineTargetWorld : IMineTargetWorld
{
    private readonly AiReadOnlyProtocol.Identity identity;
    private readonly JsonElement args;
    private Player Player=>(Player)identity.Actor;
    private int x,y,type,slot,toolType,toolPrefix,toolPower,toolBoost;
    private Item item;
    private string shape;
    private long revision;
    private readonly int initialLife;
    internal NativeMineTargetWorld(AiReadOnlyProtocol.Identity identity,JsonElement args){this.identity=identity;this.args=args.Clone();initialLife=Player.statLife;}
    public string InterruptionReason()
    {
        ServerAiNativeActions.Require(identity);
        if(Player.statLife<initialLife)return "task_interrupt_health_decreased";
        if(Player.lavaWet)return "task_interrupt_lava_contact";
        // Bounded native safety check, on the game thread; no model/TCP wait.
        foreach(var npc in Main.npc)
            if(npc.active&&!npc.friendly&&npc.damage>0&&Microsoft.Xna.Framework.Vector2.DistanceSquared(npc.Center,Player.Center)<=160*160)
                return "task_interrupt_nearby_hostile";
        return null;
    }
    public object Target=>new{x,y,typeId=type,toolSlot=slot,scope="one observed vanilla ore tile; local stance search; no automatic retry"};
    public string Prepare()
    {
        ServerAiNativeActions.Require(identity);
        if(!ActorMining.Healthy(Player))return "actor_cannot_mine";
        string error=SceneMineTargets.Resolve(identity,args,out x,out y);if(error!=null)return error;
        if(!WorldGen.InWorld(x,y,2)||!Main.tile[x,y].HasTile)return "invalid_mining_target";
        type=Main.tile[x,y].TileType;
        if(type>=TileID.Count||!TileID.Sets.Ore[type]||Main.tileFrameImportant[type]||Main.tile[x,y].IsActuated||TileID.Sets.Falling[type])return "unsupported_mining_target";
        if(!GameNavigation.Ordinary(Player)||Player.width!=20||Player.height!=42)return "unsupported_mining_profile";
        bool Pick(Item i)=>ActorMining.SupportedPick(i);
        slot=Player.selectedItem is >=0 and <10&&Pick(Player.HeldItem)?Player.selectedItem:Array.FindIndex(Player.inventory,0,Math.Min(10,Player.inventory.Length),Pick);
        if(slot<0)return "no_supported_hotbar_pickaxe";
        item=Player.inventory[slot];toolType=item.type;toolPrefix=item.prefix;toolPower=item.pick;toolBoost=item.tileBoost;shape=SceneMineTargets.Shape(x,y);SceneMineTargets.Watch(x,y);revision=SceneMineTargets.Revision(x,y);
        return null;
    }
    public void SelectTool(){ServerAiNativeActions.Require(identity);ActorItemUse.Select(Player,identity.World,identity.Generation,slot);}
    public string Revalidate()
    {
        ServerAiNativeActions.Require(identity);
        if(!ActorMining.Healthy(Player))return "actor_cannot_mine";
        if(!ReferenceEquals(Player.inventory[slot],item)||Player.selectedItem!=slot||!ActorMining.SupportedPick(item)||item.type!=toolType||item.prefix!=toolPrefix||item.pick!=toolPower||item.tileBoost!=toolBoost)return "task_tool_changed";
        if(SceneMineTargets.Shape(x,y)!=shape||SceneMineTargets.Revision(x,y)!=revision)return "task_target_changed";
        return null;
    }
    private bool SafePose(float px,float py,NativeMotionWorld world)
    {
        var pose=new PhysicsState(px,py,0,0,true,0,true);
        if(!world.SafeFlatLanding(pose))return false;
        var impact=MiningSupportImpact.Read(pose,x,y,TerrainReader.ReadCell);
        return impact.Available&&impact.AffectsCurrentStance==false&&impact.AfterRemoval.Rejection==null;
    }
    public bool CanMine()
    {
        var p=Player;var observed=ServerAiNavigation.Observe(p);
        return observed.Settled&&SafePose(p.position.X,p.position.Y,new NativeMotionWorld(observed.State))
            &&ActorMining.QueryReach(p,identity.World,identity.Generation,item,x,y,p.position.X,p.position.Y);
    }
    public JsonElement? FindApproach()
    {
        var p=Player;var observed=ServerAiNavigation.Observe(p);var world=new NativeMotionWorld(observed.State);
        // Full candidate intervals are filtered by native item reach, not distance-to-ore
        // or generic landing candidates. The real arrival is checked again before mining.
        var candidates=from tx in Enumerable.Range(x-6,13) from floor in Enumerable.Range(y-6,13)
            let px=tx*16-2f let py=floor*16-42f
            orderby Math.Abs(px-p.position.X)+Math.Abs(py-p.position.Y)
            select (px,py);
        foreach(var c in candidates)
        {
            bool valid=true;
            for(int dx=-4;dx<=4;dx++)
                if(!ActorMining.QueryReach(p,identity.World,identity.Generation,item,x,y,c.px+dx,c.py)||!SafePose(c.px+dx,c.py,world)){valid=false;break;}
            if(valid)return JsonSerializer.SerializeToElement(new{minPlayerLeftX=c.px-4,maxPlayerLeftX=c.px+4,surfaceY=c.py+42});
        }
        return null;
    }
    public JsonElement MineArguments()=>JsonSerializer.SerializeToElement(new{x,y});
    public bool TargetAbsent(){ServerAiNativeActions.Require(identity);return !Main.tile[x,y].HasTile;}
}
