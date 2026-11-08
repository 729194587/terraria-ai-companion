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

internal sealed class NativeNavigationEdits : ILocalEditSource
{
    private const int TileSize=16, MaxWidthTiles=12, MaxDepthTiles=8;
    private readonly AiReadOnlyProtocol.Identity identity;
    private PhysicsState openingOrigin;
    private readonly bool rolling;
    private readonly Func<int,int,bool> protectedCell;
    private readonly Player Player;
    private (int X,int Y,int Slot,Item Item,int Type,PhysicsState Pose)? placement;

    internal NativeNavigationEdits(AiReadOnlyProtocol.Identity identity, PhysicsState origin,
        Func<int,int,bool> protectedCell=null,bool rolling=false)
    {
        this.identity=identity;
        this.rolling=rolling;
        openingOrigin=origin;
        this.protectedCell=protectedCell ?? ((_,_)=>false);
        Player=(Player)identity.Actor;
    }

    internal PhysicsState Waypoint { get; private set; }
    internal float Cost { get; private set; }
    internal bool Edited { get; private set; }
    private bool IsTargetCell(int x,int y)=>protectedCell(x,y);
    private PhysicsState? expectedLanding;
    private PhysicsState cutPose;
    private (int X,int Y,int Slot,Item Item,string Shape,long Revision)? openingTile;
    private bool expectedDrop;
    private string armedInventory;
    private static string ItemStamp(Item item)=>$"{item.type}/{item.prefix}/{item.pick}/{item.axe}/{item.tileBoost}/{item.useTime}/{item.useAnimation}/{item.createTile}/{item.placeStyle}/{item.stack}";
    private readonly List<object> drops=new();
    internal string SearchFailure { get; private set; }
    public string SearchDetail{get;private set;}
    public object OpeningEvidence=>new{drops=drops.ToArray()};
    private static float Feet(PhysicsState p)=>p.Py+PhysicsSimulator.PlayerH;
    private static int Grid(float coordinate)=>(int)Math.Floor(coordinate/TileSize);
    private static IEnumerable<int> Columns(float left,float right)
        =>Enumerable.Range(Grid(left),(int)Math.Ceiling(right/TileSize)-Grid(left));

    internal NavigationStep FindOpening(float targetCenter, float targetFeet, int remainingTiles)
    {
        placement=null; openingTile=null; expectedLanding=null; SearchFailure=null;armedInventory=null;
        int x=Grid(targetCenter), y=Grid(targetFeet);
        var observed=ServerAiNavigation.Observe(Player);
        var pose=observed.State;
        if(rolling&&observed.Settled)openingOrigin=pose;
        SearchDetail=$"opening window ±{MaxWidthTiles}x/{MaxDepthTiles} down from start; target=({x},{y}); " +
            $"actor tile=({Grid(Player.Center.X)},{Grid(Feet(pose))}); remaining cuts={remainingTiles}";
        if(!observed.Settled||!GameNavigation.Ordinary(Player))
        {SearchDetail+="; actor is not settled in the ground profile";return null;}
        if(!rolling&&(Math.Abs(x-Grid(openingOrigin.Px+PhysicsSimulator.PlayerW/2f))>MaxWidthTiles
            ||y-Grid(Feet(openingOrigin))>MaxDepthTiles)
            ||Math.Abs(pose.Px-openingOrigin.Px)>MaxWidthTiles*TileSize
            ||Feet(pose)-Feet(openingOrigin)>=MaxDepthTiles*TileSize)
        {SearchFailure="navigation_edit_window_exceeded";SearchDetail+="; excavation window exceeded";return null;}
        var world=new NativeMotionWorld(pose);
        if(!world.SafeFlatLanding(pose)||Math.Abs(Feet(pose)-(float)Math.Round(Feet(pose)/TileSize)*TileSize)>LandingRegion.FeetTolerance)
        {SearchDetail+="; opening requires a flat starting stance";return null;}
        bool aligned=targetCenter>=pose.Px&&targetCenter<=pose.Px+PhysicsSimulator.PlayerW;
        if(aligned)
        {
            if(y>=(int)Math.Round(Feet(pose)/TileSize))return PlanDown(pose,remainingTiles);
            SearchDetail+="; aligned target needs an unsupported upward approach";return null;
        }
        return PlanAcross(pose,targetCenter>=Player.Center.X?1:-1,remainingTiles);
    }

    private NavigationStep PlanDown(PhysicsState pose,int remainingTiles)
    {
        int row=(int)Math.Round(Feet(pose)/TileSize);
        var floor=Columns(pose.Px,pose.Px+PhysicsSimulator.PlayerW)
            .Where(tx=>TerrainReader.ReadDryCell(tx,row)!='.').Select(tx=>(tx,row)).ToArray();
        var blocks=floor.Where(c=>!IsTargetCell(c.tx,c.row))
            .OrderBy(c=>Math.Min(pose.Px+PhysicsSimulator.PlayerW,(c.tx+1)*TileSize)-Math.Max(pose.Px,c.tx*TileSize))
            .ToArray();
        if(blocks.Length==0||!CanDropAfter(pose,floor))
        {SearchDetail+=$"; down: no clear one-row descent to safe support at row {row+1}";return null;}
        Waypoint=pose with{Py=pose.Py+TileSize};
        var step=PlanCut(blocks,remainingTiles);
        if(step==null)return null;
        var cut=blocks[0];
        bool drop=CanDropAfter(pose,new[]{cut});
        if(!drop&&!MotionSupport.SafeLanding(pose,(tx,ty)=>(tx,ty)==cut?'.':TerrainReader.ReadDryCell(tx,ty)))
        {SearchDetail+="; down: intermediate stance would lose support";openingTile=null;return null;}
        expectedDrop=drop;
        expectedLanding=pose with{Py=pose.Py+(drop?TileSize:0)};
        SearchDetail+=$"; down: next cut=({cut.tx},{cut.row}); expected drop={(drop?TileSize:0)}px";
        return step;
    }

    private NavigationStep PlanAcross(PhysicsState pose,int direction,int remainingTiles)
    {
        float destination=pose.Px+direction*TileSize;
        if(Math.Abs(destination-openingOrigin.Px)>MaxWidthTiles*TileSize)
        {SearchDetail+="; horizontal destination exceeds opening window";return null;}
        Waypoint=pose with{Px=destination};
        const float arrivalWidth=2;
        var blocks=(from cell in LocalEditGeometry.Across(pose,destination)
            let tx=cell.X let ty=cell.Y
            where TerrainReader.ReadDryCell(tx,ty)!='.'
            select (tx,ty)).ToArray();

        char After(int tx,int ty)=>blocks.Contains((tx,ty))?'.':TerrainReader.ReadDryCell(tx,ty);
        for(int offset=-2;offset<=2;offset++)
            if(!MotionSupport.SafeFlatLanding(pose with{Px=destination+offset},After))
            {SearchDetail+="; horizontal: no supported landing behind the proposed cut";return blocks.Length==0?PlanPlacement(pose,destination):null;}
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

    private NavigationStep PlanPlacement(PhysicsState pose,float destination)
    {
        int row=(int)Math.Round(Feet(pose)/TileSize);
        foreach(int tx in Columns(destination,destination+PhysicsSimulator.PlayerW))
        {
            for(int slot=0;slot<10;slot++)
            {
                var item=Player.inventory[slot];
                if(!PlacementAllowed(tx,row,item))continue;
                char support=Main.tileSolidTop[item.createTile]?'=':'#';
                char After(int x,int y)=>x==tx&&y==row?support:TerrainReader.ReadDryCell(x,y);
                if(!MotionSupport.SafeFlatLanding(pose with{Px=destination},After))continue;
                Cost=Math.Max(1,item.useTime)+20;
                placement=(tx,row,slot,item,item.createTile,pose);
                expectedDrop=false;expectedLanding=pose;
                SearchDetail+=$"; bridge support at ({tx},{row})";
                return new("place_tile",JsonSerializer.SerializeToElement(new{x=tx,y=row,plannedPlacementSlot=slot}));
            }
        }
        return null;
    }

    private bool PlacementAllowed(int x,int y,Item item)
    {
        if(IsTargetCell(x,y)||!WorldGen.InWorld(x,y,2)||Main.tile[x,y].HasTile
            ||TerrainReader.ReadDryCell(x,y)!='.'||!SafeCutNeighbours(x,y,allowPlatforms:true)
            ||!ActorPlacement.Supported(item)||!Main.tileSolid[item.createTile]
            ||TileID.Sets.Falling[item.createTile]
            ||Player.noBuilding||Player.CCed||Player.noItems||Player.frozen||Player.stoned
            ||!Player.InInteractionRange(x,y,Terraria.DataStructures.TileReachCheckSettings.Simple))
            return false;
        return TerrainReader.ReadDryCell(x-1,y) is '#' or '='
            ||TerrainReader.ReadDryCell(x+1,y) is '#' or '='
            ||TerrainReader.ReadDryCell(x,y+1)=='#';
    }

    internal bool CanDropAfter(PhysicsState pose,(int X,int Y)[] removed)
    {
        float feet=Feet(pose);
        int row=(int)Math.Round(feet/TileSize);
        if(Math.Abs(feet-row*TileSize)>LandingRegion.FeetTolerance
            ||feet+TileSize-Feet(openingOrigin)>MaxDepthTiles*TileSize+LandingRegion.FeetTolerance)return false;
        char After(int tx,int ty)
        {
            char cell=TerrainReader.ReadDryCell(tx,ty);
            return cell is '?' or '!'?cell:removed.Contains((tx,ty))?'.':cell;
        }
        // A fall is only admitted into a dry, fully cleared row with a verified
        // flat floor one tile below. No jump, teleports or speculative world edits.
        return LocalEditGeometry.DropOneRow(pose,After);
    }

    internal void BeginLocalPlanning(PhysicsState pose)
    {if(rolling)openingOrigin=pose;SearchFailure=null;SearchDetail="bounded local edit evaluation";}
    public char Cell(int x,int y)=>TerrainReader.ReadDryCell(x,y);
    private static bool SafeMaterial(Item item)
    {
        if(!ActorPlacement.Supported(item))return false;
        int type=item.createTile;
        return type<TileID.Count&&Main.tileSolid[type]&&!TileID.Sets.Falling[type]
            &&(!Main.tileFrameImportant[type]||TileID.Sets.Platforms[type])
            &&TileID.Sets.TouchDamageImmediate[type]==0&&!TileID.Sets.TouchDamageHot[type]
            &&!TileID.Sets.TouchDamageBleeding[type]&&!TileID.Sets.TouchDamageDestroyTile[type]&&!TileID.Sets.Boulders[type];
    }
    public IReadOnlyList<LocalMaterial> Materials=>Enumerable.Range(0,10).Where(slot=>SafeMaterial(Player.inventory[slot])).Select(slot=>
        {
            var item=Player.inventory[slot];
            return new LocalMaterial(slot,item.createTile,Main.tileSolidTop[item.createTile]?'=':'#',item.stack,Math.Max(1,Math.Max(item.useTime,item.useAnimation))+1);
        }).ToArray();
    private bool SafeVirtualNeighbours(int x,int y,LocalTerrainView view,bool allowPlatforms)
    {
        for(int tx=x-1;tx<=x+1;tx++)for(int ty=y-1;ty<=y+1;ty++)
        {
            char shape=view.Cell(tx,ty);
            if(shape is '?' or '!'||!WorldGen.InWorld(tx,ty,1))return false;
            if(shape=='.'&&Cell(tx,ty)!='.')continue; // a previously admitted virtual removal
            var tile=Main.tile[tx,ty];
            if(tile.HasUnactuatedTile&&(TileID.Sets.Falling[tile.TileType]
                ||Main.tileFrameImportant[tile.TileType]&&!(allowPlatforms&&TileID.Sets.Platforms[tile.TileType])))return false;
        }
        return true;
    }
    public LocalEditQuote Mine(PhysicsState pose,int x,int y,LocalTerrainView view)
    {
        if(!ActorMining.Healthy(Player)||IsTargetCell(x,y)||!WorldGen.InWorld(x,y,2)||view.Cell(x,y)!='#'||!Main.tile[x,y].HasUnactuatedTile
            ||Main.tileFrameImportant[Main.tile[x,y].TileType]||!SafeVirtualNeighbours(x,y,view,false)
            ||Math.Abs(x-Grid(openingOrigin.Px+10))>MaxWidthTiles||y<Grid(openingOrigin.Py)
            ||y>=(int)Math.Round(Feet(openingOrigin)/16)+MaxDepthTiles)return null;
        var failure=ActorMining.ChooseTool(Player,identity.World,identity.Generation,x,y,false,out int slot);
        if(failure!=ActorMining.Failure.None||Player.inventory[slot].pick<=0
            ||!ActorMining.QueryReach(Player,identity.World,identity.Generation,Player.inventory[slot],x,y,pose.Px,pose.Py))return null;
        return new(slot,Main.tile[x,y].TileType,ActorMining.EstimateUpdates(Player,slot,x,y));
    }
    public bool Place(PhysicsState pose,int x,int y,LocalMaterial material,LocalTerrainView view)
    {
        if(IsTargetCell(x,y)||!WorldGen.InWorld(x,y,2)||view.Cell(x,y)!='.'||!SafeVirtualNeighbours(x,y,view,true)
            ||Player.noBuilding||Player.CCed||Player.noItems||Player.frozen||Player.stoned)return false;
        var item=Player.inventory[material.Slot];
        if(!SafeMaterial(item)||item.createTile!=material.Type||item.stack<=0||Main.tile[x,y].HasTile&&Cell(x,y)=='.')return false;
        // Keep the existing Simple placement reach check. Future work poses in
        // this template move toward the cell; cells outside current reach are
        // conservatively reconsidered only after an actual movement checkpoint.
        if(!Player.InInteractionRange(x,y,Terraria.DataStructures.TileReachCheckSettings.Simple))return false;
        return view.Cell(x-1,y) is '#' or '='||view.Cell(x+1,y) is '#' or '='||view.Cell(x,y+1)=='#';
    }
    internal NavigationStep Arm(LocalEditAction action)
    {
        placement=null;openingTile=null;expectedLanding=null;
        var now=ServerAiNavigation.Observe(Player);
        if(!now.Settled||now.State!=action.Start){SearchDetail="local plan start changed";return null;}
        if(action.Kind==LocalEditKind.Move)return new("navigate_to_landing_region",JsonSerializer.SerializeToElement(new
            {minPlayerLeftX=action.Expected.Px-2,maxPlayerLeftX=action.Expected.Px+2,surfaceY=Feet(action.Expected)}));
        if(action.Kind==LocalEditKind.Place)
        {
            var item=Player.inventory[action.Slot];
            if(item.createTile!=action.Type||!PlacementAllowed(action.X,action.Y,item))return null;
            placement=(action.X,action.Y,action.Slot,item,item.createTile,now.State);
            armedInventory=ItemStamp(item);
            expectedLanding=now.State;expectedDrop=false;
            return new("place_tile",JsonSerializer.SerializeToElement(new{x=action.X,y=action.Y,plannedPlacementSlot=action.Slot}));
        }
        var quote=Mine(now.State,action.X,action.Y,new(Cell));
        if(quote==null||quote.Slot!=action.Slot||quote.Type!=action.Type)return null;
        bool drop=CanDropAfter(now.State,new[]{(action.X,action.Y)});
        if(!drop&&!MotionSupport.SafeLanding(now.State,(x,y)=>x==action.X&&y==action.Y?'.':Cell(x,y)))return null;
        SceneMineTargets.Watch(action.X,action.Y);cutPose=now.State;
        openingTile=(action.X,action.Y,action.Slot,Player.inventory[action.Slot],SceneMineTargets.NavigationShape(action.X,action.Y),SceneMineTargets.Revision(action.X,action.Y));
        armedInventory=ItemStamp(Player.inventory[action.Slot]);
        expectedDrop=drop;expectedLanding=now.State with{Py=now.State.Py+(drop?16:0)};
        return new("mine_tile",JsonSerializer.SerializeToElement(new{x=action.X,y=action.Y,plannedToolSlot=action.Slot}));
    }

    private NavigationStep PlanCut((int X,int Y)[] blocks,int remainingTiles)
    {
        if(blocks.Length>remainingTiles)
        {SearchFailure="navigation_edit_budget_exhausted";SearchDetail+=$"; proposed cut needs {blocks.Length} tiles; budget insufficient";return null;}
        var p=Player;
        int nextSlot=-1;
        Cost=0;
        foreach(var (tx,ty) in blocks)
        {
            if(IsTargetCell(tx,ty)||!WorldGen.InWorld(tx,ty,2)
                ||Math.Abs(tx-Grid(openingOrigin.Px+PhysicsSimulator.PlayerW/2f))>MaxWidthTiles
                ||ty<Grid(openingOrigin.Py)||ty>=(int)Math.Round(Feet(openingOrigin)/TileSize)+MaxDepthTiles
                ||TerrainReader.ReadDryCell(tx,ty)!='#'
                ||Main.tileFrameImportant[Main.tile[tx,ty].TileType]||!SafeCutNeighbours(tx,ty))
            {SearchDetail+=$"; protected, hazardous or unsupported blocker ({tx},{ty})";return null;}
            var failure=ActorMining.ChooseTool(p,identity.World,identity.Generation,tx,ty,true,out int toolSlot);
            if(failure!=ActorMining.Failure.None||p.inventory[toolSlot].pick<=0)
            {SearchDetail+=$"; blocker ({tx},{ty}): {AiActionReasons.Mining("failed",failure.ToString())}";return null;}
            Cost+=Math.Max(1,p.inventory[toolSlot].useTime)+20;
            if(nextSlot<0)nextSlot=toolSlot;
        }
        var first=blocks[0];
        SceneMineTargets.Watch(first.X,first.Y);
        cutPose=ServerAiNavigation.Observe(p).State;
        openingTile=(first.X,first.Y,nextSlot,p.inventory[nextSlot],SceneMineTargets.NavigationShape(first.X,first.Y),SceneMineTargets.Revision(first.X,first.Y));
        return new("mine_tile",JsonSerializer.SerializeToElement(new{x=first.X,y=first.Y,plannedToolSlot=nextSlot}));
    }

    internal static bool SafeCutNeighbours(int x,int y,bool allowPlatforms=false)
    {
        for(int tx=x-1;tx<=x+1;tx++)
            for(int ty=y-1;ty<=y+1;ty++)
            {
                if(TerrainReader.ReadDryCell(tx,ty) is '?' or '!')return false;
                var tile=Main.tile[tx,ty];
                if(tile.HasUnactuatedTile&&(TileID.Sets.Falling[tile.TileType]
                    ||Main.tileFrameImportant[tile.TileType]&&!(allowPlatforms&&TileID.Sets.Platforms[tile.TileType])))return false;
            }
        return true;
    }

    public string RevalidateOpening()
    {
        int armedSlot=placement?.Slot??openingTile?.Slot??-1;
        if(armedInventory!=null&&(armedSlot<0||ItemStamp(Player.inventory[armedSlot])!=armedInventory))return "navigation_planned_item_changed";
        if(placement is {} block)
        {
            var now=ServerAiNavigation.Observe(Player);
            return now.Settled&&now.State==block.Pose
                &&ReferenceEquals(Player.inventory[block.Slot],block.Item)&&block.Item.createTile==block.Type
                &&PlacementAllowed(block.X,block.Y,block.Item)?null:"navigation_placement_changed";
        }
        if(openingTile is not {} cut||IsTargetCell(cut.X,cut.Y)
            ||!ReferenceEquals(Player.inventory[cut.Slot],cut.Item)
            ||SceneMineTargets.NavigationShape(cut.X,cut.Y)!=cut.Shape||SceneMineTargets.Revision(cut.X,cut.Y)!=cut.Revision)
            return "mining_opening_tile_changed";
        if(!SafeCutNeighbours(cut.X,cut.Y)||!OpeningSettled())return "mining_opening_pose_or_safety_changed";
        var pose=ServerAiNavigation.Observe(Player).State;
        if(expectedLanding==null||Math.Abs(pose.Px-cutPose.Px)>.1f||Math.Abs(pose.Py-cutPose.Py)>.1f
            ||(expectedDrop&&!CanDropAfter(pose,new[]{(cut.X,cut.Y)}))
            ||(!expectedDrop&&!MotionSupport.SafeLanding(pose,
                (tx,ty)=>tx==cut.X&&ty==cut.Y?'.':TerrainReader.ReadDryCell(tx,ty))))
            return "mining_opening_pose_or_safety_changed";
        return null;
    }

    public bool OpeningSettled()=>ServerAiNavigation.Observe(Player).Settled;
    public string OpeningLandingError()
    {
        // Called only after a child returned a verified native effect.
        if(openingTile!=null||placement!=null)Edited=true;
        var pose=ServerAiNavigation.Observe(Player).State;
        if(expectedLanding is not {} expected||Math.Abs(pose.Px-expected.Px)>2
            ||Math.Abs(Feet(pose)-Feet(expected))>LandingRegion.FeetTolerance)
        {
            SearchDetail+=$"; unexpected settled pose left={pose.Px:F1}, feet={Feet(pose):F1}";
            return "mining_opening_landing_changed";
        }
        if(placement is {} block&&TerrainReader.ReadDryCell(block.X,block.Y)!='#'
            &&TerrainReader.ReadDryCell(block.X,block.Y)!='=')
            return "navigation_placed_support_changed";
        placement=null;
        if(expectedDrop&&openingTile is {} cut)
            drops.Add(new{fromFeetY=cut.Y*TileSize,toFeetY=Feet(pose),playerLeftX=pose.Px,gameUpdate=Main.GameUpdateCount});
        openingTile=null;expectedLanding=null;
        return null;
    }
}
