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
    public string ProgressKey=>NativeNavigationWorld.CheckpointKey(Player);
    public bool RefreshApproachAfterOpening=>true;
    private readonly AiReadOnlyProtocol.Identity identity;
    private readonly JsonElement args;
    private Player Player=>(Player)identity.Actor;
    private int x,y,type,slot,miningCapability;
    private Item item;
    private ActorMining.FurnitureCell[] furniture;
    private string shape;
    private long revision;
    private readonly int initialLife;
    private NativeNavigationEdits edits;
    public string SearchDetail { get; private set; }
    public object OpeningEvidence=>edits?.OpeningEvidence;
    internal NativeMineTargetWorld(AiReadOnlyProtocol.Identity identity,JsonElement args){this.identity=identity;this.args=args.Clone();initialLife=Player.statLife;}
    public string InterruptionReason()=>NativeNavigationWorld.CheckHazards(identity,initialLife);

    public object Target=>new{x,y,typeId=type,toolSlot=slot,scope="one specified tile; bounded local opening"};
    public string Prepare()
    {
        ServerAiNativeActions.Require(identity);
        if(!ActorMining.Healthy(Player))return "actor_cannot_mine";
        string error=SceneMineTargets.Resolve(identity,args,out x,out y);if(error!=null)return error;
        if(!WorldGen.InWorld(x,y,2)||!Main.tile[x,y].HasTile)return "invalid_mining_target";
        type=Main.tile[x,y].TileType;
        if(!GameNavigation.Ordinary(Player)||Player.width!=PhysicsSimulator.PlayerW||Player.height!=PhysicsSimulator.PlayerH)
            return "unsupported_mining_profile";
        if(RefreshTool() is {} toolError)return toolError;
        furniture=ActorMining.FurnitureAt(x,y);
        shape=SceneMineTargets.NavigationShape(x,y);edits=new(identity,ServerAiNavigation.Observe(Player).State,IsTargetCell,rolling:true);SceneMineTargets.Watch(x,y);revision=SceneMineTargets.Revision(x,y);
        return null;
    }

    private string RefreshTool(int? capability=null)
    {
        var failure=ActorMining.ChooseTool(Player,identity.World,identity.Generation,x,y,false,out slot,out _);
        if(failure!=ActorMining.Failure.None)return AiActionReasons.Mining("failed",failure.ToString());
        item=Player.inventory[slot];
        miningCapability=capability??ActorMining.CapabilityStamp(Player);
        return null;
    }

    public string Revalidate()
    {
        ServerAiNativeActions.Require(identity);
        if(!ActorMining.Healthy(Player))return "actor_cannot_mine";
        if(SceneMineTargets.NavigationShape(x,y)!=shape||SceneMineTargets.Revision(x,y)!=revision
            ||furniture.Any(cell=>!ActorMining.SameFurnitureCell(type,cell)))return "task_target_changed";
        int capability=ActorMining.CapabilityStamp(Player);
        return capability==miningCapability?null:RefreshTool(capability);
    }
    private bool IsTargetCell(int tx,int ty)=>tx==x&&ty==y||furniture.Any(part=>part.X==tx&&part.Y==ty);

    private bool SafePose(PhysicsState pose,NativeMotionWorld world,bool arrival)
    {
        if(!world.SafeLanding(pose))return false;
        // Native framing can remove the other parts with the targeted segment.
        bool safe=MotionSupport.SafeLanding(pose,(tx,ty)=>
        {
            char cell=TerrainReader.ReadDryCell(tx,ty);
            if(cell is '?' or '!')return cell;
            if(IsTargetCell(tx,ty))return '.';
            return cell;
        });
        return safe||!arrival&&edits.Edited&&furniture.Length==0&&edits.LandingAfterCut(pose,new[]{(x,y)}).HasValue;
    }
    public bool CanMine()
    {
        var p=Player;var observed=ServerAiNavigation.Observe(p);
        if(!observed.Settled||edits.Edited&&!NativeNavigationEdits.SafeRemovalNeighbours(x,y,new(edits.Environment))
            ||!SafePose(observed.State,new NativeMotionWorld(observed.State),false)
            ||ActorMining.ChooseTool(p,identity.World,identity.Generation,x,y,true,out int reachableSlot,out _)!=ActorMining.Failure.None)
            return false;
        slot=reachableSlot;
        item=p.inventory[slot];
        return true;
    }
    public JsonElement? FindApproach()
    {
        var p=Player;var observed=ServerAiNavigation.Observe(p);var world=new NativeMotionWorld(observed.State);
        // Scan player-left positions, including offsets within a tile. A 20px
        // body can fit between obstacles even when the old tile-centred band cannot.
        LandingRegion best=null;
        int validBands=0;
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
                validBands++;
                start=null;
                float distance=candidate.Distance(observed.State);
                if(distance>=bestDistance)continue;
                best=candidate;
                bestDistance=distance;
            }
        }
        SearchDetail=$"stance search ±6 tiles; valid bands={validBands}; target=({x},{y})";
        return best==null?null:JsonSerializer.SerializeToElement(new
        {
            minPlayerLeftX=best.MinPlayerLeftX,maxPlayerLeftX=best.MaxPlayerLeftX,surfaceY=best.SurfaceY
        });
    }
    public NavigationStep FindOpening(int remainingTiles)
    {
        if(item.pick<=0||Main.tileFrameImportant[type])
        {SearchDetail+="; opening requires an ordinary pickaxe target";return null;}
        var p=Player;
        if(ActorMining.QueryReach(p,identity.World,identity.Generation,item,x,y,p.position.X,p.position.Y)
            &&!NativeNavigationEdits.SafeRemovalNeighbours(x,y,new(edits.Environment)))
        {SearchDetail+="; reachable target has unsafe removal neighbours";return null;}
        var step=edits.FindOpening(x*16+8,y*16,remainingTiles);
        SearchDetail=edits.SearchDetail;
        return step;
    }
    public string RevalidateOpening()=>edits.RevalidateOpening();
    public bool OpeningSettled()=>edits.OpeningSettled();
    public string OpeningLandingError()=>edits.OpeningLandingError();
    public JsonElement MineArguments()=>JsonSerializer.SerializeToElement(new{x,y});
    public bool TargetAbsent(){ServerAiNativeActions.Require(identity);return !Main.tile[x,y].HasTile;}
}
