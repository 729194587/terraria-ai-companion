using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using Terraria;
using CompanionBridge.Motion;
using VirtualPlayerEngineProbe;
using Result = CompanionBridge.ServerIpc.AiAtomicActions.NativeResult;

namespace CompanionBridge.ServerIpc;

internal sealed class DestinationNavigationTask : AutonomousNavigationTask
{
    private readonly NativeNavigationWorld journey;
    private readonly AiReadOnlyProtocol.Identity identity;
    private readonly Player player;
    private readonly NavigationKnowledge knowledge;
    private readonly TerrainRouteGoal goal;
    private readonly TerrainRouteMemory memory=new();
    private readonly HashSet<LandingRegion> probes=new();
    private readonly Queue<object> decisions=new();
    private TerrainApproachPlanner planner;
    private TerrainRouteExecution route;
    private NativeNavigationEdits edits;
    private IEnumerator<int> search;
    private (PhysicsState,PhysicsParameters,long,long)? scope;
    private int sequence,replans,moves;
    private double planningMs,maxSliceMs;
    internal DestinationNavigationTask(string id,AiReadOnlyProtocol.Identity identity,JsonElement args,
        AiAtomicActions.IExecutor executor,NavigationKnowledge knowledge)
        :this(id,identity,args,executor,knowledge,new NativeNavigationWorld(identity,args,knowledge)){}
    private DestinationNavigationTask(string id,AiReadOnlyProtocol.Identity identity,JsonElement args,
        AiAtomicActions.IExecutor executor,NavigationKnowledge knowledge,NativeNavigationWorld journey):base(id,journey,executor)
    {
        this.identity=identity;this.knowledge=knowledge;this.journey=journey;player=(Player)identity.Actor;
        goal=new(LandingRegion.AtDestination(args.GetProperty("x").GetSingle(),args.GetProperty("y").GetSingle()));
    }
    internal override string Phase=>route?.Phase??(search!=null?"planning":base.Phase);
    protected override object TerrainEvidence=>new{decisions=decisions.ToArray(),planningMs,maxSliceMs,replans,
        moves=moves+(route?.Moves??0),actualCuts=dugTiles+(route?.Cuts??0),actualPlaces=placedTiles+(route?.Places??0)};
    private void ReleaseSearch(){search?.Dispose();search=null;}
    private void CollectRoute()
    {
        dugTiles+=route.Cuts;placedTiles+=route.Places;clearedTiles+=route.Cuts+route.Places;
        openingMoves+=route.Moves;moves+=route.Moves;acquired|=route.Acquired;route=null;
    }
    protected override Result TickTerrainRoute(AiReadOnlyProtocol.Identity frame)
    {
        if(route!=null)
        {
            int before=route.Cuts+route.Places;
            var result=route.Tick(this,frame);
            if(route.Cuts+route.Places>before)VerifiedTerrainEffect();
            if(result==null)return Running();
            if(result.Status!="completed")route.RememberFailedMove(memory,edits.EquipmentStamp());
            CollectRoute();
            if(result.Status=="unknown"||!result.InputsReleased)return Describe(result);
            if(result.Status!="completed")
            {
                if(result.Status is not ("failed" or "timeout"))return Describe(result);
                scope=null;
                if(++replans>=MaxConsecutiveInvalidations)return Describe(new("timeout","navigation_edit_replan_budget_exhausted",null,true));
            }
            return Running();
        }
        if(phase!="observe"||child!=null||journey.PlanningPending||journey.ContinuingSpecialRoute)return null;
        var observed=ServerAiNavigation.Observe(player);
        var pose=observed.State;
        if(!observed.Settled||!goal.InWindow(pose)||journey.Arrived()){ReleaseSearch();return null;}
        var physics=NativeNavigation.Parameters(player);
        if(!physics.IsBasic||physics.Air!=null||physics.ExtraJump!=null||physics.Impulse!=null||pose.Wet)
        {ReleaseSearch();return null;}
        var world=new NativeMotionWorld(pose);
        var stamp=(pose,physics,world.TerrainSignature(pose),journey.InventoryStamp());
        if(memory.HasDestinationFailures&&memory.ObserveDestination(physics,edits.EquipmentStamp()))scope=null;
        if(scope!=stamp)
        {
            ReleaseSearch();scope=stamp;probes.Clear();
            edits=new(identity,pose,(x,y)=>identity.Autonomy!=null&&AutonomyPolicy.ProtectedTile(x,y));
            planner=new(identity,knowledge,memory,probes,goal,edits,LocalCutTileLimit);
            search=planner.Run(pose,world,physics,stamp.Item3).GetEnumerator();
        }
        if(search==null)return null;
        var timer=Stopwatch.StartNew();
        try
        {
            for(int work=0;work<16&&timer.Elapsed.TotalMilliseconds<3;work++)
            {
                if(search.MoveNext())continue;
                ReleaseSearch();
                int capability=ActorMining.CapabilityStamp(player);
                if(decisions.Count==8)decisions.Dequeue();
                decisions.Enqueue(planner.Decide(capability));
                if(planner.Selected!=null)
                {
                    route=new(identity,native,edits,planner.Selected,physics,capability,taskId+"/route-"+(++sequence));
                    return Running();
                }
                if(planner.Destination is {} landing)
                {
                    return StartChild(frame,"navigate_to_landing_region",NativeNavigationWorld.RegionArguments(landing.Region),"navigating",1800);
                }
                // An incomplete local search hands off to rolling and special-motion navigation.
                return null;
            }
            return Running();
        }
        finally{double ms=timer.Elapsed.TotalMilliseconds;planningMs+=ms;maxSliceMs=Math.Max(maxSliceMs,ms);}
    }
    internal override Result Stop(AiReadOnlyProtocol.Identity frame,string status,string reason)
    {
        bool running=CurrentChild!=null;
        var result=base.Stop(frame,status,reason);
        ReleaseSearch();
        if(route!=null){if(running)route.Record(lastResult);CollectRoute();}
        return result with{InputsAcquired=acquired||result.InputsAcquired};
    }
    internal override void Abandon()
    {
        bool running=CurrentChild!=null;
        ReleaseSearch();base.Abandon();
        if(route!=null){if(running)route.Record(lastResult);CollectRoute();}
    }
}
