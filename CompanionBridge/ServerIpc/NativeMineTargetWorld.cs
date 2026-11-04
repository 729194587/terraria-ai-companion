using System;
using System.Linq;
using System.Text.Json;
using Terraria;
using CompanionBridge.Common.Systems;
using CompanionBridge.Motion;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

internal sealed class NativeMineTargetWorld : IMineTargetWorld
{
    private readonly AiReadOnlyProtocol.Identity identity;
    private readonly JsonElement args;
    private Player Player=>(Player)identity.Actor;
    private int x,y,type,slot,toolType,toolPrefix,toolPower,toolAxe,toolBoost;
    private Item item;
    private ActorMining.FurnitureCell[] furniture;
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
    public object Target=>new{x,y,typeId=type,toolSlot=slot,scope="one specified tile; local stance search; no automatic retry"};
    public string Prepare()
    {
        ServerAiNativeActions.Require(identity);
        if(!ActorMining.Healthy(Player))return "actor_cannot_mine";
        string error=SceneMineTargets.Resolve(identity,args,out x,out y);if(error!=null)return error;
        if(!WorldGen.InWorld(x,y,2)||!Main.tile[x,y].HasTile)return "invalid_mining_target";
        type=Main.tile[x,y].TileType;
        if(!GameNavigation.Ordinary(Player)||Player.width!=PhysicsSimulator.PlayerW||Player.height!=PhysicsSimulator.PlayerH)
            return "unsupported_mining_profile";
        var failure=ActorMining.ChooseTool(Player,identity.World,identity.Generation,x,y,false,out slot);
        if(failure!=ActorMining.Failure.None)return AiActionReasons.Mining("failed",failure.ToString());
        furniture=ActorMining.FurnitureAt(x,y);
        item=Player.inventory[slot];toolType=item.type;toolPrefix=item.prefix;toolPower=item.pick;toolAxe=item.axe;toolBoost=item.tileBoost;shape=SceneMineTargets.Shape(x,y);SceneMineTargets.Watch(x,y);revision=SceneMineTargets.Revision(x,y);
        return null;
    }
    public string Revalidate()
    {
        ServerAiNativeActions.Require(identity);
        if(!ActorMining.Healthy(Player))return "actor_cannot_mine";
        if(!ReferenceEquals(Player.inventory[slot],item)||item.type!=toolType||item.prefix!=toolPrefix||item.pick!=toolPower||item.axe!=toolAxe||item.tileBoost!=toolBoost)return "task_tool_changed";
        if(SceneMineTargets.Shape(x,y)!=shape||SceneMineTargets.Revision(x,y)!=revision
            ||furniture.Any(cell=>!ActorMining.SameFurnitureCell(type,cell)))return "task_target_changed";
        return null;
    }
    private bool SafePose(PhysicsState pose,NativeMotionWorld world,bool arrival)
    {
        if(!(arrival?world.SafeFlatLanding(pose):world.SafeLanding(pose)))return false;
        // Native framing can remove the other parts with the targeted segment.
        return MotionSupport.SafeLanding(pose,(tx,ty)=>
        {
            char cell=TerrainReader.ReadCell(tx,ty);
            if(cell is '?' or '!')return cell;
            if(tx==x&&ty==y)return '.';
            foreach(var part in furniture)
                if(part.X==tx&&part.Y==ty)return '.';
            return cell;
        });
    }
    public bool CanMine()
    {
        var p=Player;var observed=ServerAiNavigation.Observe(p);
        return observed.Settled&&SafePose(observed.State,new NativeMotionWorld(observed.State),false)
            &&ActorMining.QueryReach(p,identity.World,identity.Generation,item,x,y,p.position.X,p.position.Y);
    }
    public JsonElement? FindApproach()
    {
        var p=Player;var observed=ServerAiNavigation.Observe(p);var world=new NativeMotionWorld(observed.State);
        // Scan player-left positions, including offsets within a tile. A 20px
        // body can fit between obstacles even when the old tile-centred band cannot.
        LandingRegion best=null;
        float bestDistance=float.PositiveInfinity;
        int minLeft=(x-6)*16-PhysicsSimulator.PlayerW/2;
        int maxLeft=(x+7)*16-PhysicsSimulator.PlayerW/2;
        for(int feet=(y-6)*16;feet<=(y+6)*16;feet+=8)
        {
            float py=feet-PhysicsSimulator.PlayerH;
            int? start=null;
            for(int px=minLeft;px<=maxLeft+1;px++)
            {
                var pose=new PhysicsState(px,py,0,0,true,0,true);
                if(px<=maxLeft&&SafePose(pose,world,true)
                    &&ActorMining.QueryReach(p,identity.World,identity.Generation,item,x,y,px,py))
                {
                    start??=px;
                    continue;
                }
                if(!start.HasValue)continue;
                var candidate=new LandingRegion(start.Value,px-1,feet);
                start=null;
                float distance=candidate.Distance(observed.State);
                if(distance>=bestDistance)continue;
                best=candidate;
                bestDistance=distance;
            }
        }
        return best==null?null:JsonSerializer.SerializeToElement(new
        {
            minPlayerLeftX=best.MinPlayerLeftX,maxPlayerLeftX=best.MaxPlayerLeftX,surfaceY=best.SurfaceY
        });
    }
    public JsonElement MineArguments()=>JsonSerializer.SerializeToElement(new{x,y,plannedToolSlot=slot});
    public bool TargetAbsent(){ServerAiNativeActions.Require(identity);return !Main.tile[x,y].HasTile;}
}
