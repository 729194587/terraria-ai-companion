using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Terraria;
using Terraria.ID;
using Terraria.DataStructures;
using Terraria.ObjectData;
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
    private readonly bool miningAccess;
    private readonly Func<int,int,bool> protectedCell;
    private readonly Player Player;
    private (int X,int Y,int Slot,Item Item,int Type,PhysicsState Pose)? placement;
    private int waterPlacementDirection;

    internal NativeNavigationEdits(AiReadOnlyProtocol.Identity identity, PhysicsState origin,
        Func<int,int,bool> protectedCell=null,bool rolling=false,bool miningAccess=false)
    {
        this.identity=identity;
        this.rolling=rolling;
        this.miningAccess=miningAccess;
        openingOrigin=origin;
        this.protectedCell=protectedCell ?? ((_,_)=>false);
        Player=(Player)identity.Actor;
    }

    internal PhysicsState Waypoint { get; private set; }
    internal float Cost { get; private set; }
    internal bool Edited { get; private set; }
    internal object ProtectedEdit {get;private set;}
    private bool IsTargetCell(int x,int y)
    {
        if(!NavigationProtection.Reserved(x,y))return protectedCell(x,y);
        ProtectedEdit??=new{x,y};
        return true;
    }
    private PhysicsState? expectedLanding;
    private PhysicsState cutPose;
    private (int X,int Y,int Slot,Item Item,string Shape,long Revision)? openingTile;
    private bool expectedDrop;
    private bool openingRun;
    private string armedInventory;
    private int armedMiningCapability;
    private static string ItemStamp(Item item)=>$"{item.type}/{item.prefix}/{item.pick}/{item.axe}/{item.tileBoost}/{item.useTime}/{item.useAnimation}/{item.createTile}/{item.placeStyle}/{item.stack}";
    private readonly List<object> drops=new();
    internal string SearchFailure { get; private set; }
    public string SearchDetail{get;private set;}
    public object OpeningEvidence=>new{drops=drops.ToArray(),protectedEdit=ProtectedEdit};
    private static float Feet(PhysicsState p)=>p.Py+PhysicsSimulator.PlayerH;
    private static int Grid(float coordinate)=>(int)Math.Floor(coordinate/TileSize);
    private static IEnumerable<int> Columns(float left,float right)
        =>Enumerable.Range(Grid(left),(int)Math.Ceiling(right/TileSize)-Grid(left));

    internal NavigationStep FindOpening(float targetCenter, float targetFeet, int remainingTiles,bool mining=false)
    {
        placement=null; openingTile=null; expectedLanding=null; SearchFailure=null;armedInventory=null;openingRun=false;
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
        if(mining && world.SafeLanding(pose) && FindMiningAccess(pose,x,y,remainingTiles) is {} access)return access;
        if(targetFeet>Feet(pose)&&LocalEditGeometry.LowerWalkLanding(pose,NativeNavigation.Parameters(Player),world,
            targetCenter,targetFeet,FallSafety.LookaheadPixels) is {} lower)
        {
            SearchDetail+=$"; native collision predicts lower landing at left={lower.Px:F1}, feet={Feet(lower):F1}";
            return new("navigate_to_landing_region",JsonSerializer.SerializeToElement(new
                {minPlayerLeftX=lower.Px-1,maxPlayerLeftX=lower.Px+1,surfaceY=Feet(lower)}));
        }
        if(!world.SafeFlatLanding(pose)||Math.Abs(Feet(pose)-(float)Math.Round(Feet(pose)/TileSize)*TileSize)>LandingRegion.FeetTolerance)
        {SearchDetail+="; terrain edits require a flat starting stance";return null;}
        if(mining&&targetFeet>Feet(pose)&&Math.Abs(targetCenter-(pose.Px+10))<=3*TileSize
            &&CanStartDescent(pose)&&PlanDown(pose,remainingTiles) is {} descent)return descent;
        bool aligned=targetCenter>=pose.Px&&targetCenter<=pose.Px+PhysicsSimulator.PlayerW;
        if(aligned)
        {
            if(y>=(int)Math.Round(Feet(pose)/TileSize))return PlanDown(pose,remainingTiles);
            SearchDetail+="; aligned target needs an unsupported upward approach";return null;
        }
        int direction=targetCenter>=Player.Center.X?1:-1;
        var across=PlanAcross(pose,direction,remainingTiles);
        if(across!=null||targetFeet<=Feet(pose))return across;
        return PlanDown(pose,remainingTiles);
    }

    // Clear only the first intervening ordinary block on the short approach
    // to the selected ore. Reobserve after this one native action.
    private NavigationStep FindMiningAccess(PhysicsState pose,int x,int y,int remaining)
    {
        int fromX=Grid(Player.Center.X),fromY=Grid(Player.Center.Y);
        int length=Math.Max(Math.Abs(x-fromX),Math.Abs(y-fromY));
        if(length is <2 or >10)return null;
        for(int i=1;i<length;i++)
        {
            int tx=fromX+(int)Math.Round((x-fromX)*i/(double)length);
            int ty=fromY+(int)Math.Round((y-fromY)*i/(double)length);
            char shape=TerrainReader.ReadDryCell(tx,ty);
            if(shape=='.')continue;
            if(shape is not ('#' or 'h' or >= '1' and <= '4')||TileID.Sets.Ore[Main.tile[tx,ty].TileType]||IsTargetCell(tx,ty))return null;
            var block=new[]{(tx,ty)};
            bool supported=MotionSupport.SafeLanding(pose,(cx,cy)=>(cx,cy)==(tx,ty)?'.':TerrainReader.ReadDryCell(cx,cy));
            var landing=supported?pose:LandingAfterCut(pose,block);
            if(landing==null)return null;
            var step=PlanCut(block,remaining);
            if(step==null)return null;
            expectedDrop=!supported;
            expectedLanding=landing;
            SearchDetail+=$"; access cut ({tx},{ty})";
            return step;
        }
        return null;
    }

    internal bool CanStartDescent(PhysicsState pose)
    {
        int row=Grid(Feet(pose));
        var view=new LocalTerrainView(Environment);
        var floor=Columns(pose.Px,pose.Px+PhysicsSimulator.PlayerW)
            .Where(tx=>view.DryCell(tx,row)!='.').Select(tx=>(tx,row)).ToArray();
        return floor.Length>0&&floor.All(c=>!IsTargetCell(c.tx,c.row)&&NavigationProtection.NaturalObstacle(Main.tile[c.tx,c.row].TileType)&&SafeExcavation(c.tx,c.row,view,miningAccess))
            &&LandingAfterCut(pose,floor).HasValue;
    }

    private NavigationStep PlanDown(PhysicsState pose,int remainingTiles)
    {
        int row=(int)Math.Round(Feet(pose)/TileSize);
        var floor=Columns(pose.Px,pose.Px+PhysicsSimulator.PlayerW)
            .Where(tx=>TerrainReader.ReadDryCell(tx,row)!='.').Select(tx=>(tx,row)).ToArray();
        var blocks=floor.Where(c=>!IsTargetCell(c.tx,c.row))
            .OrderBy(c=>Math.Min(pose.Px+PhysicsSimulator.PlayerW,(c.tx+1)*TileSize)-Math.Max(pose.Px,c.tx*TileSize))
            .ToArray();
        if(blocks.Length==0||LandingAfterCut(pose,blocks) is not {} lower)
        {SearchDetail+="; down: no safe landing within bounded descent";return null;}
        Waypoint=lower;
        var step=PlanCut(blocks,remainingTiles);
        if(step==null)return null;
        var cut=blocks[0];
        var landing=LandingAfterCut(pose,new[]{cut});
        if(!landing.HasValue&&!MotionSupport.SafeLanding(pose,(tx,ty)=>(tx,ty)==cut?'.':TerrainReader.ReadDryCell(tx,ty)))
        {SearchDetail+="; down: intermediate stance would lose support";openingTile=null;return null;}
        expectedDrop=landing.HasValue;
        expectedLanding=landing??pose;
        SearchDetail+=$"; down: next cut=({cut.tx},{cut.row}); expected drop={expectedLanding.Value.Py-pose.Py:F1}px";
        return step;
    }

    private NavigationStep PlanAcross(PhysicsState pose,int direction,int remainingTiles)
    {
        float destination=pose.Px+direction*TileSize;
        if(Math.Abs(destination-openingOrigin.Px)>MaxWidthTiles*TileSize)
        {SearchDetail+="; horizontal destination exceeds opening window";return null;}
        Waypoint=pose with{Px=destination};
        var blocks=(from cell in LocalEditGeometry.Across(pose,destination)
            let tx=cell.X let ty=cell.Y
            where TerrainReader.ReadDryCell(tx,ty)!='.'
            select (tx,ty)).ToArray();

        char After(int tx,int ty)=>blocks.Contains((tx,ty))?'.':TerrainReader.ReadDryCell(tx,ty);
        var band=LocalEditGeometry.FindFlatLandingBand(pose with{Px=destination},After);
        if(band==null)
        {SearchDetail+="; horizontal: no supported landing behind the proposed cut";return blocks.Length==0?PlanPlacement(pose,destination):null;}
        if(blocks.Length>0)
        {
            var step=blocks.Length>1&&blocks.Length<=remainingTiles&&blocks.All(c=>WorldGen.InWorld(c.tx,c.ty,2))
                ?ArmRun(pose,blocks.Select(c=>ExcavationCell.Capture(c.tx,c.ty)).ToArray())
                :null;
            step??=PlanCut(blocks,remainingTiles);
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
            minPlayerLeftX=band.MinPlayerLeftX,maxPlayerLeftX=band.MaxPlayerLeftX,surfaceY=band.SurfaceY
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
        if(IsTargetCell(x,y)||!WorldGen.InWorld(x,y,2)||Main.tile[x,y].HasTile||PlacementBodyOverlap(x,y)
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

    internal PhysicsState? LandingAfterCut(PhysicsState pose,(int X,int Y)[] removed)
    {
        char After(int tx,int ty)
        {
            char cell=TerrainReader.ReadDryCell(tx,ty);
            return cell is '?' or '!'?cell:removed.Contains((tx,ty))?'.':cell;
        }
        return LocalEditGeometry.FallLanding(pose,After,NativeNavigation.Parameters(Player),FallSafety.LookaheadPixels);
    }

    internal void BeginLocalPlanning(PhysicsState pose)
    {if(rolling)openingOrigin=pose;SearchFailure=null;SearchDetail="bounded local edit evaluation";}
    public char Cell(int x,int y)=>TerrainReader.ReadCell(x,y);
    public TerrainCell Environment(int x,int y)=>TerrainReader.ReadEnvironment(x,y);
    private static bool SafeMaterial(Item item)
    {
        if(!ActorPlacement.Supported(item))return false;
        int type=item.createTile;
        return type<TileID.Count&&Main.tileSolid[type]&&!TileID.Sets.Falling[type]
            &&(!Main.tileFrameImportant[type]||TileID.Sets.Platforms[type])
            &&TileID.Sets.TouchDamageImmediate[type]==0&&!TileID.Sets.TouchDamageHot[type]
            &&!TileID.Sets.TouchDamageBleeding[type]&&!TileID.Sets.TouchDamageDestroyTile[type]&&!TileID.Sets.Boulders[type];
    }
    internal long EquipmentStamp()
    {
        var stamp=new HashCode();
        stamp.Add(ActorMining.CapabilityStamp(Player,includeBindings:false));stamp.Add(Player.blockRange);stamp.Add(Player.noBuilding);
        foreach(var material in Materials)stamp.Add(material);
        return stamp.ToHashCode();
    }
    public IReadOnlyList<LocalMaterial> Materials=>Enumerable.Range(0,10).Where(slot=>SafeMaterial(Player.inventory[slot])).Select(slot=>
        {
            var item=Player.inventory[slot];
            return new LocalMaterial(slot,item.createTile,Main.tileSolidTop[item.createTile]?'=':'#',item.stack,Math.Max(1,Math.Max(item.useTime,item.useAnimation))+1);
        }).ToArray();
    private static bool SafeVirtualNeighbours(int x,int y,LocalTerrainView view,bool allowPlatforms,bool allowWater=false)
    {
        for(int tx=x-1;tx<=x+1;tx++)for(int ty=y-1;ty<=y+1;ty++)
        {
            var cell=view.Environment(tx,ty);
            char shape=cell.Shape;
            if(shape is '?' or '!'||!WorldGen.InWorld(tx,ty,1)
                ||cell.Liquid.Amount>0&&(!allowWater||ty<y||cell.Liquid.Kind!=LiquidKind.Water))return false;
            if(shape=='.'&&TerrainReader.ReadCell(tx,ty)!='.')continue; // a previously admitted virtual removal
            var tile=Main.tile[tx,ty];
            if(allowWater&&shape=='.'&&NativePlacementSurvey.Replaceable(tile,false))continue;
            if(tile.HasUnactuatedTile&&(TileID.Sets.Falling[tile.TileType]
                ||Main.tileFrameImportant[tile.TileType]&&!(allowPlatforms&&TileID.Sets.Platforms[tile.TileType])))return false;
        }
        return true;
    }
    public LocalEditQuote Mine(PhysicsState pose,int x,int y,LocalTerrainView view)
    {
        if(!ActorMining.Healthy(Player)||IsTargetCell(x,y)||!WorldGen.InWorld(x,y,2)||!NavigationProtection.NaturalObstacle(Main.tile[x,y].TileType)||view.Cell(x,y)!='#'||!Main.tile[x,y].HasUnactuatedTile
            ||!SafeExcavation(x,y,view)
            ||Math.Abs(x-Grid(openingOrigin.Px+10))>MaxWidthTiles||y<Grid(openingOrigin.Py)
            ||y>=(int)Math.Round(Feet(openingOrigin)/16)+MaxDepthTiles)return null;
        var failure=ActorMining.ChooseTool(Player,identity.World,identity.Generation,x,y,true,out int slot,out float updates,
            workPosition:new(pose.Px,pose.Py));
        return failure==ActorMining.Failure.None&&Player.inventory[slot].pick>0
            ?new(slot,Main.tile[x,y].TileType,updates):null;
    }
    public float? PriceMiningRun(PhysicsState pose,IReadOnlyList<LocalEditAction> cuts,LocalTerrainView view)
    {
        var plan=NativeExcavation.Choose(identity,pose,cuts.Select(c=>ExcavationCell.Capture(c.X,c.Y)),view,ordered:true);
        return plan?.Coverage==cuts.Count?plan.TotalUpdates:null;
    }

    internal NavigationStep ArmPlan(LocalEditAction[] actions)
    {
        var first=actions[0];
        if(first.Kind==LocalEditKind.Mine)
        {
            var prefix=actions.TakeWhile(a=>a.Kind==LocalEditKind.Mine&&a.Start==first.Start&&a.Expected==first.Start).ToArray();
            if(prefix.Length>1&&ServerAiNavigation.Observe(Player) is {Settled:true} observed&&observed.State==first.Start
                &&prefix.All(a=>WorldGen.InWorld(a.X,a.Y,2)&&Main.tile[a.X,a.Y].HasTile&&Main.tile[a.X,a.Y].TileType==a.Type))
            {
                var run=ArmRun(first.Start,prefix.Select(a=>ExcavationCell.Capture(a.X,a.Y)).ToArray());
                if(run!=null)return run;
            }
        }
        return Arm(first);
    }

    private NavigationStep ArmRun(PhysicsState pose,ExcavationCell[] cells)
    {
        if(cells.Any(c=>IsTargetCell(c.X,c.Y)||!NavigationProtection.NaturalObstacle(c.Type)
            ||Math.Abs(c.X-Grid(openingOrigin.Px+PhysicsSimulator.PlayerW/2f))>MaxWidthTiles
            ||c.Y<Grid(openingOrigin.Py)||c.Y>=(int)Math.Round(Feet(openingOrigin)/TileSize)+MaxDepthTiles))return null;
        var plan=NativeExcavation.Choose(identity,pose,cells,new(Environment),ordered:true);
        if(plan==null||plan.Coverage<2)return null;
        placement=null;openingTile=null;armedInventory=null;openingRun=true;
        expectedDrop=false;expectedLanding=pose;Cost=plan.TotalUpdates;
        var cuts=plan.Cells.Select(c=>(c.X,c.Y)).ToHashSet();
        int left=Math.Min(Grid(pose.Px),cells.Min(c=>c.X))-1;
        int right=Math.Max(Grid(pose.Px+PhysicsSimulator.PlayerW),cells.Max(c=>c.X))+1;
        int top=Math.Min(Grid(pose.Py),cells.Min(c=>c.Y))-1;
        int bottom=Math.Max(Grid(Feet(pose)),cells.Max(c=>c.Y))+1;
        int Stamp()
        {
            var stamp=new HashCode();
            for(int x=left;x<=right;x++)
            for(int y=top;y<=bottom;y++)
                if(!cuts.Contains((x,y)))stamp.Add(Environment(x,y));
            return stamp.ToHashCode();
        }
        int before=Stamp();
        return new("excavate_tiles",default,new MiningRunTask(identity,plan,pose,()=>Stamp()==before));
    }
    public bool Place(PhysicsState pose,int x,int y,LocalMaterial material,LocalTerrainView view)
        =>Place(pose,x,y,material,view,projectReach:false);
    internal bool Place(PhysicsState pose,int x,int y,LocalMaterial material,LocalTerrainView view,bool projectReach)
    {
        bool water=view.LiquidAtSupport(x,y);
        if(IsTargetCell(x,y)||!WorldGen.InWorld(x,y,2)||view.Cell(x,y)!='.'||!SafeVirtualNeighbours(x,y,view,true,water)
            ||Player.noBuilding||Player.CCed||Player.noItems||Player.frozen||Player.stoned)return false;
        var item=Player.inventory[material.Slot];
        if(!SafeMaterial(item)||item.createTile!=material.Type||item.stack<=0||Main.tile[x,y].HasTile&&Cell(x,y)=='.'&&!(water&&NativePlacementSurvey.Replaceable(Main.tile[x,y],false))
            ||water&&(item.createTile!=TileID.Platforms||PlacementBodyOverlap(x,y)))return false;
        var actor=projectReach?ActorMining.ProjectReachPose(Player,pose.Px,pose.Py):Player;
        if(!actor.InInteractionRange(x,y,TileReachCheckSettings.Simple))return false;
        return view.Cell(x-1,y) is '#' or '='||view.Cell(x+1,y) is '#' or '='||view.Cell(x,y+1)=='#';
    }

    private static bool PlacementBodyOverlap(int x,int y)
        =>Main.player.Any(p=>p.active&&!p.dead&&!p.ghost
            &&p.position.X<(x+1)*16&&p.Right.X>x*16&&p.position.Y<(y+1)*16&&p.Bottom.Y>y*16);

    private bool LivePlacementAllowed(int x,int y,int slot,int direction)
    {
        var item=Player.inventory[slot];
        if(direction==0)return PlacementAllowed(x,y,item);
        return !IsTargetCell(x,y)&&item.createTile==TileID.Platforms&&SafeMaterial(item)
            &&WaterPlacementStillSafe(Player,x,y,direction,item.stack);
    }

    internal static bool WaterPlacementStillSafe(Player player,int x,int y,int direction,int stock)
    {
        if(direction is not (-1 or 1)||!WorldGen.InWorld(x,y,2)||Main.tile[x,y].HasTile&&!NativePlacementSurvey.Replaceable(Main.tile[x,y],false)
            ||PlacementBodyOverlap(x,y)
            ||!player.InInteractionRange(x,y,Terraria.DataStructures.TileReachCheckSettings.Simple))return false;
        var stance=ServerAiNavigation.Observe(player);
        if(!stance.Settled||Math.Abs(Feet(stance.State)-y*16)>32)return false;
        var view=new LocalTerrainView(TerrainReader.ReadEnvironment);
        return stock>0&&LocalEditGeometry.WaterDeckCell(view,x,y)
            &&SafeVirtualNeighbours(x,y,view,true,allowWater:true)
            &&(view.Cell(x-1,y) is '#' or '='||view.Cell(x+1,y) is '#' or '='||view.Cell(x,y+1)=='#');
    }
    internal NavigationStep Arm(LocalEditAction action)
    {
        placement=null;openingTile=null;expectedLanding=null;waterPlacementDirection=0;openingRun=false;
        var now=ServerAiNavigation.Observe(Player);
        if(!now.Settled||now.State!=action.Start){SearchDetail="local plan start changed";return null;}
        if(action.Kind==LocalEditKind.Move)return new("navigate_to_landing_region",JsonSerializer.SerializeToElement(new
            {minPlayerLeftX=action.Expected.Px-2,maxPlayerLeftX=action.Expected.Px+2,surfaceY=Feet(action.Expected)}));
        if(action.Kind==LocalEditKind.Place)
        {
            var item=Player.inventory[action.Slot];
            if(item.createTile!=action.Type||!LivePlacementAllowed(action.X,action.Y,action.Slot,action.WaterDirection))return null;
            waterPlacementDirection=action.WaterDirection;
            placement=(action.X,action.Y,action.Slot,item,item.createTile,now.State);
            armedInventory=ItemStamp(item);
            expectedLanding=now.State;expectedDrop=false;
            return new("place_tile",JsonSerializer.SerializeToElement(new{x=action.X,y=action.Y,plannedPlacementSlot=action.Slot,
                waterBridgeDirection=action.WaterDirection}));
        }
        var quote=Mine(now.State,action.X,action.Y,new(Environment));
        if(quote==null||quote.Slot!=action.Slot||quote.Type!=action.Type)return null;
        var landing=LandingAfterCut(now.State,new[]{(action.X,action.Y)});
        if(!landing.HasValue&&!MotionSupport.SafeLanding(now.State,(x,y)=>x==action.X&&y==action.Y?'.':TerrainReader.ReadDryCell(x,y)))return null;
        SceneMineTargets.Watch(action.X,action.Y);cutPose=now.State;
        openingTile=(action.X,action.Y,action.Slot,Player.inventory[action.Slot],SceneMineTargets.NavigationShape(action.X,action.Y),SceneMineTargets.Revision(action.X,action.Y));
        armedInventory=ItemStamp(Player.inventory[action.Slot]);
        armedMiningCapability=ActorMining.CapabilityStamp(Player);
        expectedDrop=landing.HasValue;expectedLanding=landing??now.State;
        return new("mine_tile",JsonSerializer.SerializeToElement(new{x=action.X,y=action.Y,navigationEdit=true,plannedToolSlot=action.Slot}));
    }

    private NavigationStep PlanCut((int X,int Y)[] blocks,int remainingTiles)
    {
        if(blocks.Length>remainingTiles)
        {SearchFailure="navigation_edit_budget_exhausted";SearchDetail+=$"; proposed cut needs {blocks.Length} tiles; budget insufficient";return null;}
        var p=Player;
        int nextSlot=-1;
        Cost=0;
        var view=new LocalTerrainView(Environment);
        foreach(var (tx,ty) in blocks)
        {
            if(IsTargetCell(tx,ty)||!WorldGen.InWorld(tx,ty,2)||!NavigationProtection.NaturalObstacle(Main.tile[tx,ty].TileType)
                ||Math.Abs(tx-Grid(openingOrigin.Px+PhysicsSimulator.PlayerW/2f))>MaxWidthTiles
                ||ty<Grid(openingOrigin.Py)||ty>=(int)Math.Round(Feet(openingOrigin)/TileSize)+MaxDepthTiles
                ||!SafeExcavation(tx,ty,view,miningAccess))
            {SearchDetail+=$"; protected, hazardous or unsupported blocker ({tx},{ty}); shape={view.DryCell(tx,ty)}, type={view.Environment(tx,ty).TileType}";return null;}
            var failure=ActorMining.ChooseTool(p,identity.World,identity.Generation,tx,ty,true,out int toolSlot,out float updates);
            if(failure!=ActorMining.Failure.None||p.inventory[toolSlot].pick<=0)
            {SearchDetail+=$"; blocker ({tx},{ty}): {AiActionReasons.Mining("failed",failure.ToString())}";return null;}
            Cost+=updates;
            if(nextSlot<0)nextSlot=toolSlot;
        }
        var first=blocks[0];
        SceneMineTargets.Watch(first.X,first.Y);
        cutPose=ServerAiNavigation.Observe(p).State;
        openingTile=(first.X,first.Y,nextSlot,p.inventory[nextSlot],SceneMineTargets.NavigationShape(first.X,first.Y),SceneMineTargets.Revision(first.X,first.Y));
        armedMiningCapability=ActorMining.CapabilityStamp(p);
        return new("mine_tile",JsonSerializer.SerializeToElement(new{x=first.X,y=first.Y,navigationEdit=true,plannedToolSlot=nextSlot}));
    }

    internal static bool SafeExcavation(int x,int y,LocalTerrainView view,bool shaped=false)
    {
        var target=view.Environment(x,y);
        bool solid=target.DryShape=='#'||shaped&&target.DryShape is ('h' or >= '1' and <= '4');
        if(!solid||target.TileType is not int type
            ||Main.tileFrameImportant[type]||TileID.Sets.Falling[type])return false;
        return SafeRemovalNeighbours(x,y,view);
    }

    internal static bool SafeRemovalNeighbours(int x,int y,LocalTerrainView view)
    {
        for(int tx=x-1;tx<=x+1;tx++)for(int ty=y-1;ty<=y+1;ty++)
        {
            var cell=view.Environment(tx,ty);
            if(cell.DryShape is '?' or '!')return false;
            if((tx==x&&ty==y)||cell.TileType is not int type)continue;
            if(!cell.Actuated&&TileID.Sets.Falling[type]&&tx==x&&ty==y-1)return false;
            if(!Main.tileFrameImportant[type])continue;
            var data=TileObjectData.GetTileData(Main.tile[tx,ty]);
            // Cuttable, pass-through vegetation can be reframed without moving
            // solid support or destroying a multi-tile object.
            if(cell.Shape=='.'&&Main.tileCut[type]&&(data==null||data.Width*data.Height==1))continue;
            if(!TileID.Sets.Platforms[type]&&AffectsAnchor(x,y,tx,ty,data))return false;
        }
        return true;
    }

    private static bool AffectsAnchor(int x,int y,int tx,int ty,TileObjectData data)
    {
        bool adjacent=Math.Abs(tx-x)+Math.Abs(ty-y)==1;
        if(data==null)return adjacent;
        var parts=ActorMining.FurnitureAt(tx,ty);
        if(data.Width*data.Height>1&&parts.Length==0)return adjacent;
        int left=parts.Length==0?tx:parts[0].X,top=parts.Length==0?ty:parts[0].Y;
        static bool In(AnchorData anchor,int offset)=>offset>=anchor.checkStart&&offset<anchor.checkStart+anchor.tileCount;
        return (y==top-1&&In(data.AnchorTop,x-left))||(y==top+data.Height&&In(data.AnchorBottom,x-left))
            ||(x==left-1&&In(data.AnchorLeft,y-top))||(x==left+data.Width&&In(data.AnchorRight,y-top));
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
                &&LivePlacementAllowed(block.X,block.Y,block.Slot,waterPlacementDirection)?null:"navigation_placement_changed";
        }
        if(openingTile is not {} cut||IsTargetCell(cut.X,cut.Y)
            ||!ReferenceEquals(Player.inventory[cut.Slot],cut.Item)
            ||SceneMineTargets.NavigationShape(cut.X,cut.Y)!=cut.Shape||SceneMineTargets.Revision(cut.X,cut.Y)!=cut.Revision)
            return "mining_opening_tile_changed";
        if(armedMiningCapability!=ActorMining.CapabilityStamp(Player))return "navigation_mining_capability_changed";
        if(!SafeExcavation(cut.X,cut.Y,new(Environment),miningAccess)||!OpeningSettled())return "mining_opening_pose_or_safety_changed";
        var pose=ServerAiNavigation.Observe(Player).State;
        if(expectedLanding==null||Math.Abs(pose.Px-cutPose.Px)>.1f||Math.Abs(pose.Py-cutPose.Py)>.1f
            ||(expectedDrop&&(LandingAfterCut(pose,new[]{(cut.X,cut.Y)}) is not {} landing
                ||Math.Abs(Feet(landing)-Feet(expectedLanding.Value))>LandingRegion.FeetTolerance))
            ||(!expectedDrop&&!MotionSupport.SafeLanding(pose,
                (tx,ty)=>tx==cut.X&&ty==cut.Y?'.':TerrainReader.ReadDryCell(tx,ty))))
            return "mining_opening_pose_or_safety_changed";
        return null;
    }

    public bool OpeningSettled()=>ServerAiNavigation.Observe(Player).Settled;
    public string OpeningLandingError()
    {
        // Called only after a child returned a verified native effect.
        if(openingRun||openingTile!=null||placement!=null)Edited=true;
        var pose=ServerAiNavigation.Observe(Player).State;
        if(expectedLanding is not {} expected||Math.Abs(pose.Px-expected.Px)>2
            ||Math.Abs(Feet(pose)-Feet(expected))>LandingRegion.FeetTolerance)
        {
            SearchDetail+=$"; unexpected settled pose left={pose.Px:F1}, feet={Feet(pose):F1}";
            return "mining_opening_landing_changed";
        }
        if(placement is {} block&&(!Main.tile[block.X,block.Y].HasUnactuatedTile
            ||Main.tile[block.X,block.Y].TileType!=block.Type||TerrainReader.ReadCell(block.X,block.Y) is not ('#' or '=')))
            return "navigation_placed_support_changed";
        placement=null;
        if(expectedDrop&&openingTile!=null)
            drops.Add(new{fromFeetY=Feet(cutPose),toFeetY=Feet(pose),playerLeftX=pose.Px,gameUpdate=Main.GameUpdateCount});
        openingTile=null;expectedLanding=null;
        return null;
    }
}
