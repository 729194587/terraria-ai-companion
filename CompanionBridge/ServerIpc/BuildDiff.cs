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
            if (status == "already_matches") matched++;
            else if (supported)
            {
                int type = item.type;
                ledger.RequireOne(type);
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
            bool plannedSupportCandidate = status=="missing_support_or_anchor" && HasPlannedSupport(target, targets);
            entries.Add(new { layer,x,y,itemType=item?.type,left,top,width,height,inReach,status,survey=survey.Status,alternate=survey.Alternate,candidates=survey.Candidates,blocker=survey.Blocker,plannedSupportCandidate });
        }
        return new { entries, matched, pending=entries.Count-matched, placeable, materials=ledger.Summary() };
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

    private static bool HasPlannedSupport(BuildingBlueprint.Target target, IReadOnlyList<BuildingBlueprint.Target> targets)
    {
        bool Planned(int x,int y,bool wall=false) => WorldGen.InWorld(x,y,5) &&
            (wall ? Main.tile[x,y].WallType==0 : !Main.tile[x,y].HasTile) && targets.Any(t => t.Supported && !ReferenceEquals(t,target) &&
            (t.Layer=="walls")==wall && x>=t.Left && x<t.Left+t.Width && y>=t.Top && y<t.Top+t.Height);
        var data=target.Shape?.Data;
        if (data==null)
        {
            if (target.Layer!="walls" && Planned(target.X,target.Y,true)) return true;
            foreach (var (dx,dy) in new[]{(-1,0),(1,0),(0,-1),(0,1)})
                if (Planned(target.X+dx,target.Y+dy) || target.Layer=="walls" && Planned(target.X+dx,target.Y+dy,true)) return true;
            return false;
        }
        bool Edge(Terraria.DataStructures.AnchorData anchor,int x,int y,bool horizontal) =>
            Enumerable.Range(anchor.checkStart,anchor.tileCount).Any(i => Planned(x+(horizontal?i:0),y+(horizontal?0:i)));
        return data.AnchorWall && Planned(target.X,target.Y,true) ||
            Edge(data.AnchorBottom,target.Left,target.Top+target.Height,true) ||
            Edge(data.AnchorTop,target.Left,target.Top-1,true) ||
            Edge(data.AnchorLeft,target.Left-1,target.Top,false) ||
            Edge(data.AnchorRight,target.Left+target.Width,target.Top,false);
    }
}
