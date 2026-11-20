using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Terraria;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

internal static class BuildDiff
{
    internal static object Read(Player player, JsonElement args)
    {
        var targets = BuildingBlueprint.Parse(args,player);
        var ledger = new BuildingMaterials(player);
        var entries = new List<object>();
        int matched=0, placeable=0;
        foreach (var target in targets)
        {
            var (layer,x,y,item,supported,shape,left,top,width,height) = target;
            var survey = Survey(player,target,out bool inReach);
            string status = survey.Status;
            if (survey.Shape is {} actual)
            {
                left=x-actual.OriginX; top=y-actual.OriginY; width=actual.Width; height=actual.Height;
            }
            if(supported)ledger.RequireOne(item.type,status=="already_matches");
            if (status == "already_matches") matched++;
            else if (supported)
            {
                int type = item.type;
                if (survey.Attempt)
                {
                    if (!inReach) status = "out_of_reach";
                    else if (player.noBuilding || player.noItems || player.CCed || player.gravDir!=1 || player.mount.Active || player.frozen || player.stoned)
                        status = "unsupported_placement_state";
                    else
                    {
                        status = ledger.ReserveOne(type);
                        if (status == null) { status="placeable"; placeable++; }
                    }
                }
            }
            var dependency=Providers(target,targets,survey).Select(Id).ToArray();
            bool plannedSupportCandidate=dependency.Length>0;
            entries.Add(new { layer,x,y,itemType=item?.type,left,top,width,height,inReach,status,survey=survey.Status,alternate=survey.Alternate,candidates=survey.Candidates,blocker=survey.Blocker,plannedSupportCandidate,dependency,id=Id(target),nextAction=Next(status,plannedSupportCandidate) });
        }
        return new { entries, matched, currentSiteCoverage=ServerAiNavigation.Observe(player).Settled?(int?)placeable:null, pending=entries.Count-matched, placeable, materials=ledger.Summary() };
    }

    internal static string Observe(Player player, BuildingBlueprint.Target target, out bool inReach)
    {
        var survey=Survey(player,target,out inReach);
        return survey.Attempt ? null : survey.Status;
    }

    internal static NativePlacementSurvey.Result Survey(Player p, BuildingBlueprint.Target target, out bool inReach)
    {
        inReach=target.Supported && WorldGen.InWorld(target.X,target.Y,5) &&
            ActorRope.InReach(p,target.Material,target.X,target.Y);
        return target.Supported ? NativePlacementSurvey.Read(p,target.Material,target.X,target.Y) : new("unsupported_content");
    }

    internal static string Id(BuildingBlueprint.Target t)=>$"{t.Layer}/{t.X}/{t.Y}/{t.Material?.type}/{t.Material?.placeStyle}";

    internal static string Next(string status,bool planned=false)=>status switch
    {
        "already_matches" or "matched" or "placed"=>"none",
        "ready" or "placeable" or "native_replaceable" or "native_attempt_required"=>"place",
        "out_of_reach"=>"move",
        "missing_support_or_anchor"=>planned?"wait_for_support":"provide_support",
        "dependency_cycle"=>"revise_blueprint",
        "missing_material" or "material_not_available_for_native_placement"=>"prepare_material",
        "occupied_requires_dig"=>"clear_obstruction",
        _=>"resurvey"
    };

    internal static BuildingBlueprint.Target[] Providers(BuildingBlueprint.Target target,
        IReadOnlyList<BuildingBlueprint.Target> targets,NativePlacementSurvey.Result survey)
    {
        if(survey.Status!="missing_support_or_anchor")return Array.Empty<BuildingBlueprint.Target>();
        var cells=new HashSet<(int x,int y,bool wall)>();
        void Cell(int x,int y,bool wall=false)
        {
            if(WorldGen.InWorld(x,y,5) && (wall?Main.tile[x,y].WallType==0:!Main.tile[x,y].HasTile))
                cells.Add((x,y,wall));
        }
        void Anchors(ActorObjectPlacement.Shape shape)
        {
            int left=target.X-shape.OriginX,top=target.Y-shape.OriginY;
            void Edge(Terraria.DataStructures.AnchorData a,int x,int y,bool horizontal)
            {
                for(int i=a.checkStart;i<a.checkStart+a.tileCount;i++)Cell(x+(horizontal?i:0),y+(horizontal?0:i));
            }
            var d=shape.Data;
            if(d.AnchorWall)for(int x=left;x<left+shape.Width;x++)for(int y=top;y<top+shape.Height;y++)Cell(x,y,true);
            Edge(d.AnchorBottom,left,top+shape.Height,true);
            Edge(d.AnchorTop,left,top-1,true);
            Edge(d.AnchorLeft,left-1,top,false);
            Edge(d.AnchorRight,left+shape.Width,top,false);
        }
        if(survey.Candidates is {Length:>0})
            foreach(var candidate in survey.Candidates)Anchors(ActorObjectPlacement.Definition(target.Material,candidate.alternate));
        else if(survey.Shape!=null)Anchors(survey.Shape);
        else
        {
            if(target.Layer!="walls")Cell(target.X,target.Y,true);
            foreach(var (dx,dy) in new[]{(-1,0),(1,0),(0,-1),(0,1)})
            {
                Cell(target.X+dx,target.Y+dy);
                Cell(target.X+dx,target.Y+dy,true);
            }
        }
        return targets.Where(t=>t!=target && t.Supported && cells.Any(c=>c.wall==(t.Layer=="walls") &&
            c.x>=t.Left && c.x<t.Left+t.Width && c.y>=t.Top && c.y<t.Top+t.Height)).ToArray();
    }
}
