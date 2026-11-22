using System;
using System.Collections.Generic;
using System.Linq;
using CompanionBridge.Motion;
using Terraria;
using VirtualPlayerEngineProbe;
using Target = CompanionBridge.ServerIpc.BuildingBlueprint.Target;

namespace CompanionBridge.ServerIpc;

// Candidates are observations. Only a complete native motion prediction admits travel.
internal sealed class BuildWorksiteSearch
{
    internal LandingRegion Destination {get;private set;}
    internal string Reason {get;private set;}="no_observed_worksite";
    internal readonly record struct Site(LandingRegion Region,int Coverage,int Materials,float Distance);
    internal IEnumerable<int> Find(Player player,Target[] ready,HashSet<LandingRegion> excluded)
    {
        if(!GameNavigation.Ordinary(player)){Reason="unsupported_navigation_profile";yield break;}
        var start=NativeMotionWorld.Observe(player);
        var world=new NativeMotionWorld(start);
        var physics=NativeNavigation.Parameters(player);
        var sites=new List<Site>();
        int boost=Math.Max(0,ready.Max(t=>t.Material.tileBoost)+player.blockRange);
        int rx=Player.tileRangeX+boost+2, ry=Player.tileRangeY+boost+4;
        int left=Math.Max((int)(start.Px/16)-64,ready.Min(t=>t.X)-rx);
        int right=Math.Min((int)(start.Px/16)+64,ready.Max(t=>t.X)+rx);
        int top=Math.Max((int)(start.Py/16)-64,ready.Min(t=>t.Y)-ry);
        int bottom=Math.Min((int)(start.Py/16)+64,ready.Max(t=>t.Y)+ry);
        for(int col=left;col<=right;col++)
        {
            for(int feet=top*16;feet<=bottom*16;feet+=8)
            {
                int x=col*16+8-player.width/2,y=feet-player.height;
                if(!WorldGen.InWorld(col,feet/16,5))continue;
                var region=new LandingRegion(x-2,x+2,feet);
                if(excluded.Contains(region)||region.Contains(start)||
                    !world.SafeFlatLanding(new(x,y,0,0,true,0,true)))continue;
                var covered=ready.Where(t=>ActorRope.InReachAt(player,t.Material,t.X,t.Y,x-2,y) &&
                    ActorRope.InReachAt(player,t.Material,t.X,t.Y,x+2,y) &&
                    (t.Layer=="walls" || x+2+player.width<=16*t.Left || x-2>=16*(t.Left+t.Width) ||
                     feet<=16*t.Top || y>=16*(t.Top+t.Height))).ToArray();
                if(covered.Length>0)sites.Add(new(region,covered.Length,covered.Select(t=>t.Material.type).Distinct().Count(),region.Distance(start)));
            }
            yield return 0;
        }
        var failures=new HashSet<string>();
        foreach(var site in sites.OrderByDescending(s=>s.Coverage).ThenBy(s=>s.Materials).ThenBy(s=>s.Distance).Take(8))
        {
            float x=site.Region.MinPlayerLeftX+2;
            var route=SafeGroundWalk.TryPlan(start,x,x>=start.Px?1:-1,physics,world,site.Region);
            // SafeGroundWalk returns one verified prefix. Follow the predicted
            // checkpoints through the same motion adapter before pricing a full trip.
            for(int prefix=1;prefix<24 && route is {ReachedGoal:false} && route.Actions.Count>0;prefix++)
            {
                var checkpoint=route.Actions[^1].Landing;
                yield return 0;
                route=SafeGroundWalk.TryPlan(checkpoint,x,x>=checkpoint.Px?1:-1,physics,world,site.Region);
            }
            if(route?.ReachedGoal!=true)
            {
                using var search=new SurfaceNavigator(new(),start,site.Region,physics,world,world,new(),(_,_)=>false);
                for(int slice=0;slice<48 && !search.Finished;slice++)
                {
                    route=search.Advance(budgetMs:2);
                    yield return 0;
                }
                failures.Add(!search.Finished || route.TimeBudgetReached?"worksite_search_budget":route.SearchExit switch
                {
                    "no_route_in_coverage"=>"no_route_in_coverage",
                    "unknown_terrain"=>"unknown_terrain",
                    _=>"worksite_route_unconfirmed"
                });
            }
            if(route?.ReachedGoal==true){Destination=site.Region;yield break;}
        }
        if(failures.Count>0)Reason=failures.Contains("worksite_search_budget") || sites.Count>8?"worksite_search_budget":
            failures.Contains("unknown_terrain")?"unknown_terrain":
            failures.Count==1?failures.First():"worksite_route_unconfirmed";
    }
}
