using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Terraria;
using Terraria.ID;
using CompanionBridge.Common.Systems;
using CompanionBridge.Motion;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

internal sealed partial class NativeMineTargetWorld
{
    private const int TileSize=16, MaxWidthTiles=12, MaxDepthTiles=8;
    private PhysicsState openingOrigin;
    private PhysicsState? expectedLanding;
    private PhysicsState cutPose;
    private (int X,int Y,int Slot,Item Item,string Shape,long Revision)? openingTile;
    private bool openingModeHasStarted, expectedDrop;
    private readonly List<object> drops=new();
    public string SearchDetail{get;private set;}
    public object OpeningEvidence=>new{drops};
    private static float Feet(PhysicsState p)=>p.Py+PhysicsSimulator.PlayerH;
    private static int Grid(float coordinate)=>(int)Math.Floor(coordinate/TileSize);
    private static IEnumerable<int> Columns(float left,float right)
        =>Enumerable.Range(Grid(left),(int)Math.Ceiling(right/TileSize)-Grid(left));

    public MiningOpening FindOpening(int remainingTiles)
    {
        var p=Player;
        var observed=ServerAiNavigation.Observe(p);
        var pose=observed.State;
        SearchDetail=$"opening window ±{MaxWidthTiles}x/{MaxDepthTiles} down from start; target=({x},{y}); " +
            $"actor tile=({Grid(p.Center.X)},{Grid(Feet(pose))}); remaining cuts={remainingTiles}";
        if(!observed.Settled||!GameNavigation.Ordinary(p))
        {SearchDetail+="; actor is not settled in the ground profile";return null;}
        if(Math.Abs(x-Grid(openingOrigin.Px+PhysicsSimulator.PlayerW/2f))>MaxWidthTiles
            ||y-Grid(Feet(openingOrigin))>MaxDepthTiles
            ||Math.Abs(pose.Px-openingOrigin.Px)>MaxWidthTiles*TileSize
            ||Feet(pose)-Feet(openingOrigin)>=MaxDepthTiles*TileSize)
        {SearchDetail+="; excavation window exceeded";return null;}
        if(ActorMining.QueryReach(p,identity.World,identity.Generation,item,x,y,pose.Px,pose.Py)&&!SafeCutNeighbours(x,y))
        {SearchDetail+="; target neighbours contain hazardous or unknown terrain";return null;}
        if(item.pick<=0||Main.tileFrameImportant[type])
        {SearchDetail+="; excavation requires an ordinary pickaxe target";return null;}

        var world=new NativeMotionWorld(pose);
        if(!world.SafeFlatLanding(pose)||Math.Abs(Feet(pose)-(float)Math.Round(Feet(pose)/TileSize)*TileSize)>LandingRegion.FeetTolerance)
        {SearchDetail+="; opening requires a flat starting stance";return null;}
        float targetCenter=x*TileSize+TileSize/2f;
        bool aligned=targetCenter>=pose.Px&&targetCenter<=pose.Px+PhysicsSimulator.PlayerW;
        if(aligned)
        {
            if(y>=(int)Math.Round(Feet(pose)/TileSize))return PlanDown(pose,remainingTiles);
            SearchDetail+="; aligned target needs an unsupported upward approach";return null;
        }
        return PlanAcross(pose,targetCenter>=p.Center.X?1:-1,remainingTiles);
    }

    private MiningOpening PlanDown(PhysicsState pose,int remainingTiles)
    {
        int row=(int)Math.Round(Feet(pose)/TileSize);
        var floor=Columns(pose.Px,pose.Px+PhysicsSimulator.PlayerW)
            .Where(tx=>TerrainReader.ReadCell(tx,row)!='.').Select(tx=>(tx,row)).ToArray();
        var blocks=floor.Where(c=>!IsTargetCell(c.tx,c.row))
            .OrderBy(c=>Math.Min(pose.Px+PhysicsSimulator.PlayerW,(c.tx+1)*TileSize)-Math.Max(pose.Px,c.tx*TileSize))
            .ToArray();
        if(blocks.Length==0||!CanDropAfter(pose,floor))
        {SearchDetail+=$"; down: no clear one-row descent to safe support at row {row+1}";return null;}
        var step=PlanCut(blocks,remainingTiles);
        if(step==null)return null;
        var cut=blocks[0];
        bool drop=CanDropAfter(pose,new[]{cut});
        if(!drop&&!MotionSupport.SafeLanding(pose,(tx,ty)=>(tx,ty)==cut?'.':TerrainReader.ReadCell(tx,ty)))
        {SearchDetail+="; down: intermediate stance would lose support";openingTile=null;return null;}
        expectedDrop=drop;
        expectedLanding=pose with{Py=pose.Py+(drop?TileSize:0)};
        SearchDetail+=$"; down: next cut=({cut.tx},{cut.row}); expected drop={(drop?TileSize:0)}px";
        return step;
    }

    private MiningOpening PlanAcross(PhysicsState pose,int direction,int remainingTiles)
    {
        float destination=pose.Px+direction*TileSize;
        if(Math.Abs(destination-openingOrigin.Px)>MaxWidthTiles*TileSize)
        {SearchDetail+="; horizontal destination exceeds opening window";return null;}
        const float arrivalWidth=2;
        float left=Math.Min(pose.Px,destination-arrivalWidth);
        float right=Math.Max(pose.Px,destination+PhysicsSimulator.PlayerW+arrivalWidth);
        int top=Grid(pose.Py), bottom=(int)Math.Round(Feet(pose)/TileSize);
        var blocks=(from ty in Enumerable.Range(top,bottom-top)
            from tx in Columns(left,right)
            where TerrainReader.ReadCell(tx,ty)!='.'
            select (tx,ty)).ToArray();

        char After(int tx,int ty)=>blocks.Contains((tx,ty))?'.':TerrainReader.ReadCell(tx,ty);
        for(int offset=-2;offset<=2;offset++)
            if(!MotionSupport.SafeFlatLanding(pose with{Px=destination+offset},After))
            {SearchDetail+="; horizontal: no supported landing behind the proposed cut";return null;}
        if(blocks.Length>0)
        {
            var step=PlanCut(blocks,remainingTiles);
            if(step!=null)
            {
                expectedDrop=false;expectedLanding=pose;
                SearchDetail+=$"; horizontal: next cut=({blocks[0].tx},{blocks[0].ty}); remaining in step={blocks.Length}";
            }
            return step;
        }
        SearchDetail+=$"; horizontal: cleared, navigate one tile toward target";
        openingTile=null;expectedLanding=null;
        return new("navigate_to_landing_region",JsonSerializer.SerializeToElement(new
        {
            minPlayerLeftX=destination-arrivalWidth,maxPlayerLeftX=destination+arrivalWidth,surfaceY=Feet(pose)
        }));
    }

    private bool CanDropAfter(PhysicsState pose,(int X,int Y)[] removed)
    {
        float feet=Feet(pose);
        int row=(int)Math.Round(feet/TileSize);
        if(Math.Abs(feet-row*TileSize)>LandingRegion.FeetTolerance
            ||feet+TileSize-Feet(openingOrigin)>MaxDepthTiles*TileSize+LandingRegion.FeetTolerance)return false;
        char After(int tx,int ty)
        {
            char cell=TerrainReader.ReadCell(tx,ty);
            return cell is '?' or '!'?cell:removed.Contains((tx,ty))?'.':cell;
        }
        // A fall is only admitted into a dry, fully cleared row with a verified
        // flat floor one tile below. No jump, teleports or speculative world edits.
        return Columns(pose.Px,pose.Px+PhysicsSimulator.PlayerW).All(tx=>After(tx,row)=='.')
            &&MotionSupport.SafeFlatLanding(pose with{Py=pose.Py+TileSize},After);
    }

    private MiningOpening PlanCut((int X,int Y)[] blocks,int remainingTiles)
    {
        if(blocks.Length>remainingTiles)
        {SearchDetail+=$"; proposed cut needs {blocks.Length} tiles; budget insufficient";return null;}
        var p=Player;
        int nextSlot=-1;
        foreach(var (tx,ty) in blocks)
        {
            if(IsTargetCell(tx,ty)||!WorldGen.InWorld(tx,ty,2)
                ||Math.Abs(tx-Grid(openingOrigin.Px+PhysicsSimulator.PlayerW/2f))>MaxWidthTiles
                ||ty<Grid(openingOrigin.Py)||ty>=(int)Math.Round(Feet(openingOrigin)/TileSize)+MaxDepthTiles
                ||TerrainReader.ReadCell(tx,ty)!='#'
                ||Main.tileFrameImportant[Main.tile[tx,ty].TileType]||!SafeCutNeighbours(tx,ty))
            {SearchDetail+=$"; protected, hazardous or unsupported blocker ({tx},{ty})";return null;}
            var failure=ActorMining.ChooseTool(p,identity.World,identity.Generation,tx,ty,true,out int toolSlot);
            if(failure!=ActorMining.Failure.None||p.inventory[toolSlot].pick<=0)
            {SearchDetail+=$"; blocker ({tx},{ty}): {AiActionReasons.Mining("failed",failure.ToString())}";return null;}
            if(nextSlot<0)nextSlot=toolSlot;
        }
        var first=blocks[0];
        SceneMineTargets.Watch(first.X,first.Y);
        cutPose=ServerAiNavigation.Observe(p).State;
        openingTile=(first.X,first.Y,nextSlot,p.inventory[nextSlot],TaskShape(first.X,first.Y),SceneMineTargets.Revision(first.X,first.Y));
        openingModeHasStarted=true;
        return new("mine_tile",JsonSerializer.SerializeToElement(new{x=first.X,y=first.Y,plannedToolSlot=nextSlot}));
    }

    private static bool SafeCutNeighbours(int x,int y)
    {
        for(int tx=x-1;tx<=x+1;tx++)
            for(int ty=y-1;ty<=y+1;ty++)
            {
                if(TerrainReader.ReadCell(tx,ty) is '?' or '!')return false;
                var tile=Main.tile[tx,ty];
                if(tile.HasUnactuatedTile&&(TileID.Sets.Falling[tile.TileType]||Main.tileFrameImportant[tile.TileType]))return false;
            }
        return true;
    }

    public string RevalidateOpening()
    {
        if(openingTile is not {} cut||IsTargetCell(cut.X,cut.Y)
            ||!ReferenceEquals(Player.inventory[cut.Slot],cut.Item)
            ||TaskShape(cut.X,cut.Y)!=cut.Shape||SceneMineTargets.Revision(cut.X,cut.Y)!=cut.Revision)
            return "mining_opening_tile_changed";
        if(!SafeCutNeighbours(cut.X,cut.Y)||!OpeningSettled())return "mining_opening_pose_or_safety_changed";
        var pose=ServerAiNavigation.Observe(Player).State;
        if(expectedLanding==null||Math.Abs(pose.Px-cutPose.Px)>.1f||Math.Abs(pose.Py-cutPose.Py)>.1f
            ||(expectedDrop&&!CanDropAfter(pose,new[]{(cut.X,cut.Y)}))
            ||(!expectedDrop&&!MotionSupport.SafeLanding(pose,
                (tx,ty)=>tx==cut.X&&ty==cut.Y?'.':TerrainReader.ReadCell(tx,ty))))
            return "mining_opening_pose_or_safety_changed";
        return null;
    }

    public bool OpeningSettled()=>ServerAiNavigation.Observe(Player).Settled;
    public string OpeningLandingError()
    {
        var pose=ServerAiNavigation.Observe(Player).State;
        if(expectedLanding is not {} expected||Math.Abs(pose.Px-expected.Px)>2
            ||Math.Abs(Feet(pose)-Feet(expected))>LandingRegion.FeetTolerance)
        {
            SearchDetail+=$"; unexpected settled pose left={pose.Px:F1}, feet={Feet(pose):F1}";
            return "mining_opening_landing_changed";
        }
        if(expectedDrop&&openingTile is {} cut)
            drops.Add(new{fromFeetY=cut.Y*TileSize,toFeetY=Feet(pose),playerLeftX=pose.Px,gameUpdate=Main.GameUpdateCount});
        openingTile=null;expectedLanding=null;
        return null;
    }
}
