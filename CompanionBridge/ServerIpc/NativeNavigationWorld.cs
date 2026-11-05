using System;
using System.Linq;
using System.Text.Json;
using Terraria;
using CompanionBridge.Motion;

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
    private NavigationStep preferredEdit;

    internal NativeNavigationWorld(AiReadOnlyProtocol.Identity identity,JsonElement target)
    {
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
    public string NoRouteReason=>edits.SearchFailure??"navigation_local_frontier_exhausted";
    public string SearchDetail=>edits?.SearchDetail;
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
            if(npc.active&&!npc.friendly&&npc.damage>0&&Microsoft.Xna.Framework.Vector2.DistanceSquared(npc.Center,player.Center)<=160*160)
                return "task_interrupt_nearby_hostile";
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
        preferredEdit=null;
        return RegionArguments(destination);
    }

    public NavigationStep FindOpening(int remainingTiles)
    {
        var choice=preferredEdit;
        preferredEdit=null;
        return choice??edits.FindOpening(x,y,remainingTiles);
    }

    public bool PreferEdit(int remainingTiles)
    {
        preferredEdit=edits.FindOpening(x,y,remainingTiles);
        if(preferredEdit?.Operation=="navigate_to_landing_region"&&edits.Edited)
            return true; // Continue the verified opening before attempting the far goal again.
        if(preferredEdit==null||preferredEdit.Operation=="navigate_to_landing_region")
        {preferredEdit=null;return false;}

        var pose=ServerAiNavigation.Observe(player).State;
        var physics=NativeNavigation.Parameters(player);
        var route=StateSpacePlanner.Plan(pose,destination,physics,new NativeMotionWorld(pose),
            maxExpansions:16,budgetMs:12);
        if(route.Actions.Count==0)return true;
        float remaining=destination.Distance(route.Actions[^1].Landing);
        float editRemaining=destination.Distance(edits.Waypoint);
        // A short walk into the face of a blocker is not a cheaper route
        // around it. Rank partial routes only if they reach beyond this edit.
        if(!route.ReachedGoal&&remaining>=editRemaining)return true;
        // Rank safe local progress in estimated game updates, including
        // material/tool use, jump penalties and distance remaining.
        float movement=route.Actions.Sum(edge=>edge.ExecuteFrames+(edge.Jump?20:0))
            +remaining/physics.MaxRun;
        float alteration=edits.Cost+16/physics.MaxRun+editRemaining/physics.MaxRun;
        if(alteration<movement)return true;
        preferredEdit=null;
        return false;
    }

    private static JsonElement RegionArguments(LandingRegion region)=>JsonSerializer.SerializeToElement(new
    {
        minPlayerLeftX=region.MinPlayerLeftX,maxPlayerLeftX=region.MaxPlayerLeftX,surfaceY=region.SurfaceY
    });

    public string RevalidateOpening()=>edits.RevalidateOpening();
    public bool OpeningSettled()=>edits.OpeningSettled();
    public string OpeningLandingError()=>edits.OpeningLandingError();
}
