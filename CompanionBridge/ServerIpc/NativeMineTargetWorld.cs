using System;
using System.Linq;
using System.Collections.Generic;
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
    private readonly Func<int,int,bool> protectedOre;
    private readonly NavigationKnowledge navigation;
    private bool searchLimited;
    private Player Player=>(Player)identity.Actor;
    private int x,y,type,slot,miningCapability;
    private Item item;
    private (int Type,int Prefix,int Pick,int Axe,int Boost) toolStats;
    private void RememberTool(int selected)
    {
        slot=selected;
        item=Player.inventory[slot];
        toolStats=(item.type,item.prefix,item.pick,item.axe,item.tileBoost);
    }
    private ActorMining.FurnitureCell[] furniture;
    private string shape;
    private long revision;
    private readonly int initialLife;
    private NativeNavigationEdits edits;
    public string SearchDetail { get; private set; }
    public object OpeningEvidence=>edits?.OpeningEvidence;
    internal NativeMineTargetWorld(AiReadOnlyProtocol.Identity identity,JsonElement args,Func<int,int,bool> protectedOre=null,NavigationKnowledge navigation=null){this.navigation=navigation??new();this.protectedOre=protectedOre;this.identity=identity;this.args=args.Clone();initialLife=Player.statLife;}
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
        shape=SceneMineTargets.NavigationShape(x,y);edits=new(identity,ServerAiNavigation.Observe(Player).State,(tx,ty)=>IsTargetCell(tx,ty)||protectedOre?.Invoke(tx,ty)==true,rolling:true,miningAccess:true);SceneMineTargets.Watch(x,y);revision=SceneMineTargets.Revision(x,y);
        return null;
    }

    private string RefreshTool(int? capability=null)
    {
        var failure=ActorMining.ChooseTool(Player,identity.World,identity.Generation,x,y,false,out slot,out _);
        if(failure!=ActorMining.Failure.None)return AiActionReasons.Mining("failed",failure.ToString());
        RememberTool(slot);
        miningCapability=capability??ActorMining.CapabilityStamp(Player);
        return null;
    }

    public string Revalidate()
    {
        ServerAiNativeActions.Require(identity);
        if(!ActorMining.Healthy(Player))return "actor_cannot_mine";
        if(SceneMineTargets.NavigationShape(x,y)!=shape||SceneMineTargets.Revision(x,y)!=revision
            ||furniture.Any(cell=>!ActorMining.SameFurnitureCell(type,cell)))return "task_target_changed";
        if(toolStats!=(item.type,item.prefix,item.pick,item.axe,item.tileBoost))return "task_tool_changed";
        if(ActorMining.HoldsBorrowedTool(Player,identity.World,identity.Generation,slot,item))return null;
        if(!ReferenceEquals(Player.inventory[slot],item))return "task_tool_changed";
        int capability=ActorMining.CapabilityStamp(Player);
        return capability==miningCapability?null:RefreshTool(capability);
    }
    private bool IsTargetCell(int tx,int ty)=>tx==x&&ty==y||furniture.Any(part=>part.X==tx&&part.Y==ty);

    private bool SafePose(PhysicsState pose,NativeMotionWorld world,bool arrival)
    {
        if(!world.SafeLanding(pose))return false;
        if(Terraria.ID.TileID.Sets.Ore[type])
            return NativeExcavation.SafeOrePose(pose,x,y,liveTerrain)
                ||!arrival&&NativeNavigationEdits.SafeRemovalNeighbours(x,y,liveTerrain)
                    &&edits.LandingAfterCut(pose,new[]{(x,y)}).HasValue;
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
        RememberTool(reachableSlot);
        return true;
    }
    private readonly HashSet<LandingRegion> rejected=new();
    private LandingRegion lastApproach;
    private readonly LocalTerrainView liveTerrain=new(TerrainReader.ReadEnvironment);
    public bool RetryApproach()
    {
        if(lastApproach==null)return false;
        rejected.Add(lastApproach);
        lastApproach=null;
        return rejected.Count<12;
    }
    public string NoRouteReason=>rejected.Count>=12||searchLimited?"mining_stance_search_budget_exhausted":
        edits.SearchFailure??"no_safe_mining_stance_in_local_window";
    private IEnumerator<int> scan;
    private PhysicsState scanPose;
    public bool ScanningApproaches=>scan!=null;
    public void ReleasePlanning(){scan?.Dispose();scan=null;}
    public JsonElement? FindApproach()
    {
        var pose=ServerAiNavigation.Observe(Player).State;
        if(scan==null||scanPose!=pose)
        {
            ReleasePlanning();
            lastApproach=null;scanPose=pose;scan=ScanBands().GetEnumerator();
        }
        if(scan.MoveNext())return null;
        ReleasePlanning();
        return lastApproach==null?null:JsonSerializer.SerializeToElement(new{
            minPlayerLeftX=lastApproach.MinPlayerLeftX,maxPlayerLeftX=lastApproach.MaxPlayerLeftX,surfaceY=lastApproach.SurfaceY});
    }
    private IEnumerable<int> ScanBands()
    {
        var p=Player;var observed=ServerAiNavigation.Observe(p);var world=new NativeMotionWorld(observed.State);
        // Scan player-left positions, including offsets within a tile. A 20px
        // body can fit between obstacles even when the old tile-centred band cannot.
        var candidates=new List<LandingRegion>();
        searchLimited=false;
        int validBands=0;
        int minLeft=(x-6)*16-PhysicsSimulator.PlayerW/2;
        int maxLeft=(x+7)*16-PhysicsSimulator.PlayerW/2;
        for(int feet=(y-6)*16;feet<=(y+6)*16;feet+=8)
        {
            yield return 0;
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
                if(!rejected.Contains(candidate)&&!candidate.Contains(observed.State))candidates.Add(candidate);
            }
        }
        // A buried vein may have no exposed work band. First reach the surface
        // over the ore; FindOpening then checks each proposed cut and landing.
        if(item.pick>0 && !Main.tileFrameImportant[type])
        {
            for(int dx=-3;dx<=3;dx++)
            for(int feet=y*16;feet>=(y-8)*16;feet-=16)
            {
                int px=(x+dx)*16+8-PhysicsSimulator.PlayerW/2;
                var pose=new PhysicsState(px,feet-PhysicsSimulator.PlayerH,0,0,true,0,true);
                var candidate=new LandingRegion(px-1,px+1,feet);
                if(rejected.Contains(candidate)||candidate.Contains(observed.State)||!new NativeMotionWorld(pose).SafeFlatLanding(pose)
                    ||!edits.CanStartDescent(pose))continue;
                candidates.Add(candidate);
            }
        }
        var physics=NativeNavigation.Parameters(p);
        navigation.Bind(p,identity.World,identity.Generation,identity.Slot);
        navigation.ObservePhysics(physics);
        navigation.Search.Scope(world.TerrainSignature(observed.State),physics,(long)Main.GameUpdateCount);
        var ranked=candidates.Distinct().OrderBy(region=>region.Distance(observed.State)).ToArray();
        int tried=0;
        foreach(var region in ranked)
        {
            if(++tried>16){searchLimited=true;break;}
            float px=Math.Clamp(observed.State.Px,region.MinPlayerLeftX,region.MaxPlayerLeftX);
            // Distant regions are only waypoints; native rolling travel and
            // the subsequent standing-pose check still have to succeed.
            if(region.Distance(observed.State)>16*20){lastApproach=region;break;}
            var route=SafeGroundWalk.TryPlan(observed.State,px,px>=observed.State.Px?1:-1,physics,world,region);
            if(route?.ReachedGoal!=true)
            {
                using var navigator=new SurfaceNavigator(navigation.Surfaces,observed.State,region,physics,world,world,navigation.Search,
                    (at,edge)=>navigation.Failures.Reject(at,edge,(long)Main.GameUpdateCount,world,physics));
                for(int i=0;i<16&&!navigator.Finished;i++)
                {
                    route=navigator.Advance(budgetMs:2);
                    yield return 0;
                }
                if(!navigator.Finished||route?.TimeBudgetReached==true)searchLimited=true;
            }
            if(route?.ReachedGoal==true){lastApproach=region;break;}
        }
        SearchDetail=$"stance search ±6 tiles; valid bands={validBands}; target=({x},{y})";

    }
    private NavigationStep readyCut;
    public bool PreferEdit(int remainingTiles)
    {
        if(protectedOre==null)return false;
        // The cluster route planner already tried unchanged terrain. Act on
        // a verified local cut before dispatching another speculative route.
        var step=FindOpening(remainingTiles);
        if(step?.Operation!="mine_tile")return false;
        ReleasePlanning();
        readyCut=step;
        return true;
    }
    public NavigationStep FindOpening(int remainingTiles)
    {
        if(readyCut!=null){var planned=readyCut;readyCut=null;return planned;}
        if(item.pick<=0||Main.tileFrameImportant[type])
        {SearchDetail+="; opening requires an ordinary pickaxe target";return null;}
        var p=Player;
        if(ActorMining.QueryReach(p,identity.World,identity.Generation,item,x,y,p.position.X,p.position.Y)
            &&!NativeNavigationEdits.SafeRemovalNeighbours(x,y,new(edits.Environment)))
        {SearchDetail+="; reachable target has unsafe removal neighbours";return null;}
        var step=edits.FindOpening(x*16+8,y*16,remainingTiles,mining:true);
        SearchDetail=edits.SearchDetail;
        return step;
    }
    public string RevalidateOpening()=>edits.RevalidateOpening();
    public bool OpeningSettled()=>edits.OpeningSettled();
    public string OpeningLandingError(){ReleasePlanning();return edits.OpeningLandingError();}
    public object ArrivalEvidence=>new{leftX=Player.position.X,feetY=Player.Bottom.Y};
    public JsonElement MineArguments()=>JsonSerializer.SerializeToElement(new{x,y});
    public bool TargetAbsent(){ServerAiNativeActions.Require(identity);return !Main.tile[x,y].HasTile;}
}
