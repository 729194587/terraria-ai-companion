using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using Terraria;
using Terraria.ModLoader;
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
    private bool ready,forced;
    private PhysicsState plannedStart;
    private PhysicsParameters plannedPhysics;
    private long plannedTerrain,plannedInventory;
    private string planningDetail;
    private bool liquidBoundary;
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
    }

    public object Target=>target;
    public bool ReplanAfterChange=>true;
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
    public string InterruptionReason()=>CheckHazards(identity,initialLife);

    internal static string CheckHazards(AiReadOnlyProtocol.Identity identity,int initialLife)
    {
        var player=(Player)identity.Actor;
        ServerAiNativeActions.Require(identity);
        if(player.statLife<initialLife)return "task_interrupt_health_decreased";
        if(player.lavaWet)return "task_interrupt_lava_contact";
        // Bounded native safety check, on the game thread; no model/TCP wait.
        foreach(var npc in Main.npc)
        {
            if(!npc.active||npc.life<=0||npc.friendly||npc.townNPC||npc.damage<=0)continue;
            var offset=player.Center-npc.Center;
            bool contact=npc.Hitbox.Intersects(player.Hitbox);
            if(!contact)
            {
                float distanceSquared=offset.LengthSquared();
                if(distanceSquared>160*160||!npc.HasValidTarget||npc.target!=player.whoAmI)continue;
                if(!npc.noTileCollide&&!Collision.CanHitLine(npc.position,npc.width,npc.height,player.position,player.width,player.height))continue;
                if(distanceSquared>64*64&&Vector2.Dot(npc.velocity-player.velocity,offset)<=0)continue;
            }
            int cooldown=-1;
            if(NPCLoader.CanHitPlayer(npc,player,ref cooldown))return "task_interrupt_nearby_hostile";
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
        Evaluate(remainingTiles,ready?forced:true);
        if(PlanningPending||PlanningFailure!=null)return null;
        var plan=selected;
        ReleasePlanning();
        return plan==null?null:edits.ArmPlan(plan.Actions);
    }

    public bool PreferEdit(int remainingTiles)
    {
        // Let the movement child handle an ordinary doorway before pricing terrain edits.
        if(ServerDoor.Ahead(player,Math.Clamp(player.position.X,destination.MinPlayerLeftX,destination.MaxPlayerLeftX),out _,out _))
        {
            ReleasePlanning();
            return false;
        }
        Evaluate(remainingTiles,false);
        return ready&&selected!=null;
    }

    public void ReleasePlanning()
    {evaluation?.Dispose();evaluation=null;movement?.Dispose();movement=null;ready=false;selected=null;PlanningPending=false;}
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
        var pose=observed.State;var ph=NativeNavigation.Parameters(player);var world=new NativeMotionWorld(pose);
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
        if(!force&&plans.Length>0)
        {
            if(destination.Distance(pose)<=64)route=SafeGroundWalk.TryPlan(pose,(destination.MinPlayerLeftX+destination.MaxPlayerLeftX)/2,
                destination.MinPlayerLeftX>=pose.Px?1:-1,ph,world,destination);
            if(route==null)
            {
                knowledge.Search.Scope(terrain,ph,(long)Main.GameUpdateCount);
                movement??=new(knowledge.Surfaces,pose,destination,ph,world,world,knowledge.Search,
                    (at,edge)=>knowledge.Failures.Reject(at,edge,(long)Main.GameUpdateCount,world,ph));
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

    private static JsonElement RegionArguments(LandingRegion region)=>JsonSerializer.SerializeToElement(new
    {
        minPlayerLeftX=region.MinPlayerLeftX,maxPlayerLeftX=region.MaxPlayerLeftX,surfaceY=region.SurfaceY
    });

    public string RevalidateOpening()=>edits.RevalidateOpening();
    public bool OpeningSettled()=>edits.OpeningSettled();
    public string OpeningLandingError()=>edits.OpeningLandingError();
}
