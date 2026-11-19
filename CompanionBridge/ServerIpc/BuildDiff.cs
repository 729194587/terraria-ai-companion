using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

internal static class BuildDiff
{
    internal static object Read(Player player, JsonElement args)
    {
        var targets = BuildingBlueprint.Parse(args);
        var ledger = new BuildingMaterials(player);
        var entries = new List<object>();
        int matched=0, placeable=0;
        foreach (var target in targets)
        {
            var (layer,x,y,item,supported,shape,left,top,width,height) = target;
            bool known = left >= 5 && top >= 5 && left+width <= Main.maxTilesX-5 && top+height <= Main.maxTilesY-5;
            bool inReach = known && supported && ActorRope.InReach(player,item,(int)x,(int)y);
            string status = !supported ? "unsupported_content" : !known ? "unknown_or_not_loaded" :
                Inspect(player,item,layer,(int)x,(int)y,shape);
            if (status == "already_matches") matched++;
            else if (supported)
            {
                int type = item.type;
                ledger.RequireOne(type);
                if (status == null)
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
            entries.Add(new { layer,x,y,itemType=item?.type,left,top,width,height,inReach,status,plannedSupportCandidate });
        }
        return new { entries, matched, pending=entries.Count-matched, placeable, materials=ledger.Summary() };
    }

    private static bool HasPlannedSupport(BuildingBlueprint.Target target, IReadOnlyList<BuildingBlueprint.Target> targets)
    {
        bool Planned(int x,int y,bool wall=false) => WorldGen.InWorld(x,y,5) &&
            (wall ? Main.tile[x,y].WallType==0 : !Main.tile[x,y].HasTile) && targets.Any(t => t.Supported && !ReferenceEquals(t,target) &&
            (t.Layer=="walls")==wall && x>=t.Left && x<t.Left+t.Width && y>=t.Top && y<t.Top+t.Height);
        var data=target.Shape?.Data;
        if (data==null)
        {
            if (target.Layer=="walls" ? Planned(target.X,target.Y) : Planned(target.X,target.Y,true)) return true;
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

    private static string Inspect(Player player,Item item,string layer,int x,int y,ActorObjectPlacement.Shape shape)
    {
        var tile=Main.tile[x,y];
        bool wall=layer=="walls";
        if (wall && tile.WallType!=0) return tile.WallType==item.createWall ? "already_matches" : "wall_conflict";
        if (!wall && shape==null && tile.HasTile)
            return tile.TileType==item.createTile && !tile.IsActuated ? "already_matches" : "occupied_requires_dig";
        int left=x-(shape?.OriginX??0), top=y-(shape?.OriginY??0);
        int w=shape?.Width??1,h=shape?.Height??1;
        if (shape!=null)
        {
            var root=Main.tile[left,top];
            if (root.HasTile && root.TileType==item.createTile && root.TileFrameX%shape.Data.CoordinateFullWidth==0 &&
                root.TileFrameY%shape.Data.CoordinateFullHeight==0 && ActorObjectPlacement.ReadFrames(new(){
                    Shape=shape,X=x,Y=y,TileType=item.createTile,Style=item.placeStyle})!=null) return "already_matches";
        }
        var bounds=new Rectangle(left*16,top*16,w*16,h*16);
        if (!wall && (shape!=null || Main.tileSolid[item.createTile]) &&
            (bounds.Intersects(player.Hitbox) || Main.player.Any(p=>p.active && !p.dead && !p.ghost && bounds.Intersects(p.Hitbox)))) return "blocked_by_player";
        for (int dx=0;dx<w;dx++)
            for (int dy=0;dy<h;dy++)
            {
                var cell=Main.tile[left+dx,top+dy];
                if (shape!=null && cell.HasTile) return "object_footprint_blocked";
                if (cell.LiquidAmount>0) return "liquid";
            }
        if (shape!=null)
        {
            if (!TileObject.CanPlace(x,y,item.createTile,item.placeStyle,player.direction,out var candidate)) return "missing_support_or_anchor";
            return candidate.xCoord==left && candidate.yCoord==top ? null : "unsupported_content";
        }
        if (tile.WallType!=0 || (wall && tile.HasTile)) return null;
        foreach (var (dx,dy) in new[]{(-1,0),(1,0),(0,-1),(0,1)})
        {
            var adjacent=Main.tile[x+dx,y+dy];
            if (adjacent.HasTile && !adjacent.IsActuated || wall && adjacent.WallType!=0) return null;
        }
        return "missing_support_or_anchor";
    }
}
