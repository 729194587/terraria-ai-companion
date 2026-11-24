using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using Terraria;
using Terraria.ModLoader;
using Terraria.ID;
using Microsoft.Xna.Framework;
using CompanionBridge.Motion;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

internal sealed class NativeNavigationWorld : INavigationWorld
{
    private readonly AiReadOnlyProtocol.Identity identity;
    private readonly Player player;
    private readonly JsonElement target;
    private readonly float x,y;
    private readonly LandingRegion destination;
    private readonly int initialLife;
    private NativeNavigationEdits edits;
    private readonly NavigationKnowledge knowledge;
    private LocalEditEvaluation evaluation;
    private SurfaceNavigator movement;
    private LocalEditPlan selected;
    private bool ready,forced,preferDryBank;
    private PhysicsState plannedStart;
    private PhysicsParameters plannedPhysics;
    private long plannedTerrain,plannedInventory;
    private string planningDetail;
    private bool liquidBoundary;
    private (int X,int Y,int Side)? bridgeTip, pendingTip;
    private NavigationStep bridgeStep;
    private WallPlatform wallPlatform;
    private bool wallPlacementIssued;
    private sealed record WallPlatform(int X,int Y,int Side,int Slot,int ItemType,int JumpFrames);
    private readonly HashSet<(int,int)> triedWallPlatforms=new();
    public bool CanBuildWallStep=>PlanWallPlatform()!=null;
    private readonly HashSet<(int X,int Row)> attemptedBridgeStarts=new();
    private readonly Queue<object> decisions=new();
    public bool PlanningPending{get;private set;}
    public AiAtomicActions.NativeResult PlanningFailure{get;private set;}
    public object PlanningEvidence{get;private set;}

    internal NativeNavigationWorld(AiReadOnlyProtocol.Identity identity,JsonElement target,NavigationKnowledge knowledge=null)
    {
        this.knowledge=knowledge??new();
        this.identity=identity;
        this.target=target.Clone();
        player=(Player)identity.Actor;
        initialLife=player.statLife;
        x=target.GetProperty("x").GetSingle();
        y=target.GetProperty("y").GetSingle();
        destination=LandingRegion.AtDestination(x,y);
        this.knowledge.ShoreGoal=destination;
    }

    public object Target=>target;
    public bool ReplanAfterChange=>true;
    public bool CanInspectWaterBoundary
    {
        get
        {
            var p=ServerAiNavigation.Observe(player);
            return p.Settled&&new NativeMotionWorld(p.State).WaterAhead(p.State,Math.Sign(x-player.Center.X));
        }
    }
    public string ProgressKey=>CheckpointKey(player);
    internal static string CheckpointKey(Player actor)
    {
        var pose=NativeMotionWorld.Observe(actor);
        return JsonSerializer.Serialize(new{pose,terrain=new NativeMotionWorld(pose).TerrainSignature(pose)});
    }
    public string NoRouteReason=>edits.SearchFailure??(liquidBoundary
        ?"navigation_liquid_local_frontier_exhausted":"navigation_local_frontier_exhausted");
    public string SearchDetail=>planningDetail??edits?.SearchDetail;
    public object OpeningEvidence=>edits?.OpeningEvidence;
    public object ArrivalEvidence{get;private set;}
    public string InterruptionReason()=>CheckHazards(identity,initialLife,travelling:true);

    internal static string CheckHazards(AiReadOnlyProtocol.Identity identity,int initialLife,bool travelling=false)
    {
        var player=(Player)identity.Actor;
        ServerAiNativeActions.Require(identity);
        if(travelling?NativeNavigation.ExceedsTravelBudget(player,initialLife):player.statLife<initialLife)return "task_interrupt_health_decreased";
        if(player.lavaWet)return "task_interrupt_lava_contact";
        // Bounded native safety check, on the game thread; no model/TCP wait.
        foreach(var npc in Main.npc)
        {
            if(!npc.active||npc.life<=0||npc.friendly||npc.townNPC||npc.damage<=0)continue;
            if(NativeCombatWorld.ContactTick(player,npc,player.velocity.X) is not int contact)continue;
            if(contact>1 && (!npc.HasValidTarget||npc.target!=player.whoAmI))continue;
            if(contact>0 && !npc.noTileCollide && !Collision.CanHitLine(npc.position,npc.width,npc.height,player.position,player.width,player.height))continue;
            int cooldown=-1;
            if((!travelling||npc.boss||NativeNavigation.ExceedsTravelBudget(player,initialLife,npc.damage))
                &&NPCLoader.CanHitPlayer(npc,player,ref cooldown))return "task_interrupt_nearby_hostile";
        }
        return null;
    }

    public string Prepare()
    {
        if(!WorldGen.InWorld((int)(x/16),(int)(y/16),2))return "invalid_navigation_destination";
        if(!GameNavigation.Ordinary(player))return "unsupported_navigation_profile";
        edits=new(identity,ServerAiNavigation.Observe(player).State,rolling:true);
        return null;
    }

    public string Revalidate()=>null;

    public bool Arrived()
    {
        var observed=ServerAiNavigation.Observe(player);
        if(!observed.Settled||!destination.Arrived(observed.State,new NativeMotionWorld(observed.State)))
            return false;
        var pose=observed.State;
        ArrivalEvidence=new{target=Target,x=pose.Px,y=pose.Py,feetY=pose.Py+PhysicsSimulator.PlayerH,
            arrived=true,grounded=pose.Grounded,safeSupport=observed.SafeSupport,settled=observed.Settled};
        return true;
    }

    public JsonElement? FindApproach()
    {
        ReleasePlanning();
        return RegionArguments(destination);
    }

    public NavigationStep FindOpening(int remainingTiles)
    {
        if(wallPlatform!=null && !wallPlacementIssued)
        {
            if(ValidateWallPlacement()!=null)
            {
                wallPlatform=null; planningDetail="jump did not clear wall platform placement";return null;
            }
            wallPlacementIssued=true;
            var step=wallPlatform;
            planningDetail="place wall platform below airborne player";
            return new("place_tile",JsonSerializer.SerializeToElement(new{x=step.X,y=step.Y,plannedPlacementSlot=step.Slot}));
        }
        if(PlanWallPlatform() is {} launch)
        {
            wallPlatform=launch;
            triedWallPlatforms.Add((launch.X,launch.Y));
            planningDetail="jump vertically to place a wall platform";
            return new("move_input",JsonSerializer.SerializeToElement(new{horizontal="none",vertical="none",
                jumpHoldUpdates=launch.JumpFrames,maxUpdates=launch.JumpFrames}));
        }
        if(bridgeStep is {} continuation)
        {
            bridgeStep=null;
            return TrackPlacement(continuation);
        }
        Evaluate(remainingTiles,ready?forced:true);
        if(PlanningPending||PlanningFailure!=null)return null;
        var plan=selected;
        ReleasePlanning();
        return TrackPlacement(plan!=null?edits.ArmPlan(plan.Actions):BridgeStart());
    }

    private NavigationStep TrackPlacement(NavigationStep step)
    {
        pendingTip=null;
        if(step?.Operation=="place_tile"&&step.Arguments.TryGetProperty("waterBridgeDirection",out var dir)
            &&dir.GetInt32() is -1 or 1)
            pendingTip=(step.Arguments.GetProperty("x").GetInt32(),step.Arguments.GetProperty("y").GetInt32(),dir.GetInt32());
        return step;
    }

    private NavigationStep ContinueBridge()
    {
        if(bridgeTip is not {} tip)return null;
        var stance=ServerAiNavigation.Observe(player);
        var material=edits.Materials.FirstOrDefault(m=>m.Shape=='='&&m.Count>0);
        var view=new LocalTerrainView(edits.Environment);
        int next=tip.X+tip.Side;
        if(stance.Settled&&material!=null&&view.Cell(tip.X,tip.Y)=='='
            &&(x-tip.X*16-8)*tip.Side>0&&LocalEditGeometry.WaterDeckCell(view,next,tip.Y))
        {
            var pose=stance.State;
            float tipLeft=tip.X*16-2;
            if(Math.Abs(next*16+8-player.Center.X)>Player.tileRangeX*16
                ||!player.InInteractionRange(next,tip.Y,Terraria.DataStructures.TileReachCheckSettings.Simple)
                ||Math.Abs(pose.Py+42-tip.Y*16)>LandingRegion.FeetTolerance)
            {
                var onDeck=new PhysicsState(tipLeft,tip.Y*16-42,0,0,true);
                if(new NativeMotionWorld(pose).SafeLanding(onDeck))
                    return new("navigate_to_landing_region",RegionArguments(new(tipLeft-2,tipLeft+2,tip.Y*16)));
            }
            else if(edits.Place(pose,next,tip.Y,material,view))
            {
                var after=view.Fork();
                var landing=new PhysicsState(next*16-2,tip.Y*16-42,0,0,true);
                if(after.Set(next,tip.Y,'=',material.Type)&&LocalEditGeometry.LandingBand(landing,
                    (tx,ty)=>LocalEditGeometry.LandingCell(after,tx,ty,tip.Y)))
                {
                    var step=edits.Arm(new(LocalEditKind.Place,pose,pose,next,tip.Y,material.Slot,material.Type,'=',tip.Side));
                    if(step!=null)return step;
                }
            }
        }
        bridgeTip=null;
        return null;
    }

    private NavigationStep BridgeStart(bool placeOnly=false)
    {
        var observed=ServerAiNavigation.Observe(player);
        var material=edits.Materials.FirstOrDefault(m=>m.Shape=='='&&m.Count>0);
        if(!observed.Settled||material==null)return null;
        var pose=observed.State;
        int side=Math.Sign(Math.Clamp(pose.Px,destination.MinPlayerLeftX,destination.MaxPlayerLeftX)-pose.Px);
        if(side==0)return null;
        int cx=FallSafety.Row(pose.Px+10),row=FallSafety.Row(pose.Py+42);
        var view=new LocalTerrainView(edits.Environment);
        var world=new NativeMotionWorld(pose);
        LandingRegion best=null;
        float distance=float.MaxValue;
        (int X,int Row) key=default;
        // Search both ways: the exploration stride may already have walked
        // below the waterline. A new deck starts at an attached, level bank.
        for(int offset=-12;offset<=12;offset++)
            for(int y=row-5;y<=row+3;y++)
            {
                int bank=cx+offset,deck=bank+side;
                if(!LocalEditGeometry.WaterDeckCell(view,deck,y))continue;
                // A player can attach the first platform from a nearby sloping
                // shore. Verify the resulting deck support before spending it.
                if(Math.Abs(offset)<=3&&side*(deck-cx)>=-1&&Math.Abs(y*16-pose.Py-42)<=32
                    &&edits.Place(pose,deck,y,material,view))
                {
                    var after=view.Fork();
                    var landing=new PhysicsState(deck*16-2,y*16-42,0,0,true);
                    if(after.Set(deck,y,'=',material.Type)&&LocalEditGeometry.LandingBand(landing,
                        (x,ty)=>LocalEditGeometry.LandingCell(after,x,ty,y)))
                    {
                        var placement=edits.Arm(new(LocalEditKind.Place,pose,pose,deck,y,material.Slot,material.Type,'=',side));
                        if(placement!=null){planningDetail="attach reachable shoreline platform";return placement;}
                    }
                }
                if(placeOnly||view.Cell(bank,y) is not ('#' or '=')||attemptedBridgeStarts.Contains((bank,y)))continue;
                for(int overhang=0;overhang<=8;overhang+=4)
                {
                    var start=new PhysicsState(bank*16-2+side*overhang,y*16-42,0,0,true);
                    if(!world.SafeLanding(start)||!LocalEditGeometry.LandingBand(start,
                        (x,ty)=>LocalEditGeometry.LandingCell(view,x,ty,y)))continue;
                    var region=new LandingRegion(start.Px-2,start.Px+2,y*16);
                    float cost=region.Distance(pose);
                    if(cost<=2||cost>=distance)continue;
                    best=region;key=(bank,y);distance=cost;
                }
            }
        if(best==null)return null;
        attemptedBridgeStarts.Add(key);
        planningDetail="move to level shoreline support to start a horizontal platform deck";
        return new("navigate_to_landing_region",RegionArguments(best));
    }

    private WallPlatform PlanWallPlatform()
    {
        var pose=ServerAiNavigation.Observe(player);
        int side=Math.Sign(x-player.Center.X);
        if(side==0 || !pose.Settled || pose.State.Wet || !player.releaseJump)return null;
        int col=(int)(player.Center.X/16),feet=(int)Math.Round(player.Bottom.Y/16);
        int wall=col+side;
        if(!WorldGen.InWorld(wall,feet,12))return null;
        // Require a continuous face higher than a normal step or short hop.
        for(int rise=1;rise<=5;rise++)
            if(!WorldGen.SolidTile(wall,feet-rise))return null;
        var material=edits.Materials.FirstOrDefault(m=>m.Type==TileID.Platforms);
        if(material==null)return null;
        var ph=NativeNavigation.Parameters(player);
        var world=new NativeMotionWorld(pose.State);
        var jump=PhysicsSimulator.Predict(pose.State,side,ph.JumpHeight,ph,world);
        if(jump.Safe && side*(jump.Landing.Px+PhysicsSimulator.PlayerW/2-(wall*16+8))>=0
            && jump.Landing.Py<pose.State.Py-16)return null;
        var predicted=pose.State;
        WallPlatform best=null;
        try
        {
            for(int frame=1;frame<=ph.JumpHeight && frame<=30;frame++)
            {
                var next=PhysicsSimulator.Step(predicted,new PhysicsInput(0,true),ph,world);
                if(!world.SafeSweep(predicted,next,out _) || next.Py>=predicted.Py)break;
                predicted=next;
                int row=(int)Math.Ceiling((next.Py+PhysicsSimulator.PlayerH+4)/16);
                if(feet-row<2 || triedWallPlatforms.Contains((col,row)) || Main.tile[col,row].HasTile
                    || Main.tile[col,row].LiquidAmount!=0 || !WorldGen.SolidTile(wall,row))continue;
                best=new(col,row,side,material.Slot,player.inventory[material.Slot].type,frame);
            }
        }
        catch(UnsafeMotionException){return null;}
        return best;
    }

    private string ValidateWallPlacement()
    {
        var step=wallPlatform;
        var item=player.inventory[step.Slot];
        return item.type==step.ItemType && item.createTile==TileID.Platforms && item.stack>0
            && player.Bottom.Y<=step.Y*16 && player.Right.X>step.X*16 && player.Left.X<(step.X+1)*16
            && !Main.tile[step.X,step.Y].HasTile && Main.tile[step.X,step.Y].LiquidAmount==0
            && WorldGen.SolidTile(step.X+step.Side,step.Y)
            && ActorRope.InReach(player,item,step.X,step.Y)?null:"navigation_wall_platform_changed";
    }

    public bool PreferEdit(int remainingTiles)
    {
        if(wallPlatform!=null || PlanWallPlatform()!=null)return true;
        bridgeStep=ContinueBridge();
        if(bridgeStep!=null){planningDetail="continue verified platform deck";return true;}
        // Let the movement child handle an ordinary doorway before pricing terrain edits.
        if(ServerDoor.Ahead(player,Math.Clamp(player.position.X,destination.MinPlayerLeftX,destination.MaxPlayerLeftX),out _,out _))
        {
            ReleasePlanning();
            return false;
        }
        Evaluate(remainingTiles,false);
        if(!ready||selected!=null)return ready&&selected!=null;
        if(!preferDryBank)return false;
        bridgeStep=BridgeStart(placeOnly:true);
        return bridgeStep!=null;
    }

    public void ReleasePlanning()
    {evaluation?.Dispose();evaluation=null;movement?.Dispose();movement=null;ready=false;preferDryBank=false;selected=null;PlanningPending=false;}
    private long InventoryStamp()
    {
        unchecked
        {
            long hash=ActorMining.CapabilityStamp(player);
            foreach(var item in player.inventory.Take(50))
            {
                foreach(int value in new[]{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(item),item.type,item.prefix,item.stack,
                    item.pick,item.axe,item.tileBoost,item.useTime,item.useAnimation,item.createTile,item.placeStyle})hash=hash*31+value;
            }
            return hash;
        }
    }
    private void Evaluate(int remaining,bool force)
    {
        if(!GameNavigation.Ordinary(player))
        {
            ReleasePlanning();PlanningFailure=new("failed","unsupported_navigation_profile",null,true);
            planningDetail="local evaluation requires the supported ground profile";return;
        }
        var observed=ServerAiNavigation.Observe(player);
        if(!observed.Settled){PlanningPending=true;planningDetail="waiting for real settled planning state";return;}
        var pose=observed.State;var ph=NativeNavigation.Parameters(player);var world=new NativeMotionWorld(pose,breath:player.breath);
        long terrain=world.TerrainSignature(pose),inventory=InventoryStamp();
        if(evaluation==null||plannedStart!=pose||plannedPhysics!=ph||plannedTerrain!=terrain||plannedInventory!=inventory||forced!=force)
        {
            ReleasePlanning();PlanningFailure=null;forced=force;plannedStart=pose;plannedPhysics=ph;plannedTerrain=terrain;plannedInventory=inventory;
            edits.BeginLocalPlanning(pose);
            knowledge.Bind(player,identity.World,identity.Generation,identity.Slot);knowledge.ObservePhysics(ph);
            evaluation=new(pose,destination,ph,edits,remaining);
        }
        if(ready)return;
        PlanningPending=true;evaluation.Advance();
        if(!evaluation.Finished)
        {PlanningEvidence=new{status="evaluation_budget",work=evaluation.Work};planningDetail="local candidate evaluation deferred";return;}
        var plans=evaluation.Plans;
        Route route=null;SurfaceGraphSnapshot graph=null;float? moving=null;bool complete=false;
        bool dryBank=!pose.Wet&&world.WaterAhead(pose,Math.Sign(x-player.Center.X));
        if(!force&&(dryBank||plans.Any(p=>p.WaterPlatform))&&PoolCrossing.TryPlan(pose,destination,ph,world) is {} crossing
            &&!knowledge.Failures.Reject(pose,crossing.Actions[0],(long)Main.GameUpdateCount,world,ph))
        {
            ready=true;PlanningPending=false;selected=null;planningDetail="verified pool crossing before platform placement";
            PlanningEvidence=new{status="complete",choice=PoolCrossing.Kind,frames=crossing.Actions[0].ExecuteFrames};return;
        }
        if(!force&&plans.Length>0)
        {
            if(destination.Distance(pose)<=64)route=SafeGroundWalk.TryPlan(pose,(destination.MinPlayerLeftX+destination.MaxPlayerLeftX)/2,
                destination.MinPlayerLeftX>=pose.Px?1:-1,ph,world,destination);
            if(route==null)
            {
                knowledge.Search.Scope(terrain,ph,(long)Main.GameUpdateCount);
                movement??=new(knowledge.Surfaces,pose,destination,ph,world,world,knowledge.Search,
                    (at,edge)=>knowledge.Failures.Reject(at,edge,(long)Main.GameUpdateCount,world,ph),allowTravelPrefix:true);
                route=movement.Advance(budgetMs:3);graph=movement.Snapshot;
                if(route.Actions.Count==0&&!movement.Finished)
                {PlanningEvidence=new{status="movement_budget",candidates=plans.Length,graph};planningDetail="movement comparison deferred";return;}
                if(route.Actions.Count==0&&route.TimeBudgetReached)
                {
                    // A total local-search allowance is inconclusive. Retain
                    // normal native navigation rather than declaring no route
                    // and performing edits on the strength of a budget exit.
                    ready=true;PlanningPending=false;selected=null;
                    PlanningEvidence=new{status="movement_evaluation_incomplete",choice="movement",graph};
                    planningDetail="movement comparison incomplete; defer to native movement";return;
                }
                if(route.SearchExit is "surface_node_budget" or "unknown_terrain" or "unsupported_model")
                {
                    string reason=route.SearchExit=="surface_node_budget"?"navigation_surface_node_budget":route.SearchExit=="unknown_terrain"?"native_navigation_terrain_unknown":"unsupported_navigation_profile";
                    PlanningFailure=new(route.SearchExit=="unknown_terrain"?"unknown":route.SearchExit=="surface_node_budget"?"timeout":"failed",reason,null,true);
                    PlanningPending=false;return;
                }
            }
            if(route.Actions.Count>0)
            {
                complete=route.ReachedGoal;
                float actual=route.Actions.Sum(a=>a.ExecuteFrames);
                moving=complete?actual:Math.Max(actual,graph?.EstimatedRouteUpdates??(actual+destination.Distance(route.Actions[^1].Landing)/ph.MaxRun));
                // A prefix ending at the blocker is not a priced bypass. Keep
                // that uncertainty explicit rather than treating straight-line
                // remaining distance as a proved cheap route through the wall.
                if(!complete&&plans.Any(p=>p.Cost.Remaining*ph.MaxRun<destination.Distance(route.Actions[^1].Landing)))moving=null;
            }
        }
        preferDryBank=!force&&dryBank&&!complete;
        bool localLiquidBoundary=evaluation.LiquidBoundary;
        liquidBoundary|=localLiquidBoundary;
        selected=complete&&localLiquidBoundary?null:LocalEditEvaluation.Choose(plans,moving,complete);
        ready=true;PlanningPending=false;
        planningDetail=selected!=null?"bounded local edit plan selected; execute one action then reobserve"
            :liquidBoundary&&!complete?"no verified movement or admissible edit across the local liquid boundary"
            :"movement preferred or no admissible local edits";
        var decision=new{choice=selected==null?"movement":"local_edits",movementCost=moving,movementVerifiedComplete=complete,
            candidateCount=plans.Length,selected,liquidBoundary=localLiquidBoundary,virtualNativeVerified=false,gameUpdate=Main.GameUpdateCount};
        if(decisions.Count>=8)decisions.Dequeue();decisions.Enqueue(decision);
        PlanningEvidence=new{status="complete",decision.choice,decision.movementCost,decision.movementVerifiedComplete,
            candidateCount=plans.Length,selected,liquidBoundary,virtualNativeVerified=false,graph,history=decisions.ToArray(),
            candidates=plans.OrderBy(p=>p.Cost.Total).Take(6).Select(p=>new{p.Cost,p.OpenExit,changes=p.Changes,steps=p.Actions.Length}).ToArray()};
    }

    internal static JsonElement RegionArguments(LandingRegion region)=>JsonSerializer.SerializeToElement(new
    {
        minPlayerLeftX=region.MinPlayerLeftX,maxPlayerLeftX=region.MaxPlayerLeftX,surfaceY=region.SurfaceY
    });

    public string RevalidateOpening()=>wallPlacementIssued?ValidateWallPlacement():edits.RevalidateOpening();
    public bool OpeningSettled()=>edits.OpeningSettled();
    public string OpeningLandingError()
    {
        if(wallPlacementIssued)
        {
            var step=wallPlatform;wallPlatform=null;wallPlacementIssued=false;
            return Main.tile[step.X,step.Y].HasUnactuatedTile && Main.tile[step.X,step.Y].TileType==TileID.Platforms
                && Math.Abs(player.Bottom.Y-step.Y*16)<=LandingRegion.FeetTolerance?null:"wall platform landing changed";
        }
        var error=edits.OpeningLandingError();
        if(pendingTip.HasValue&&error==null)bridgeTip=pendingTip;
        pendingTip=null;
        return error;
    }
}
