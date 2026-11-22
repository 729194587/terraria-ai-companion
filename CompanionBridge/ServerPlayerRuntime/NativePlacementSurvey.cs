using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ObjectData;
using Shape = VirtualPlayerEngineProbe.ActorObjectPlacement.Shape;

namespace VirtualPlayerEngineProbe;

// Game-thread observation. ItemCheck retains the final hook, reach and placement decisions.
internal static class NativePlacementSurvey
{
    internal sealed record Obstacle(int x, int y, int tileType);
    internal sealed record Candidate(int alternate, int left, int top, int width, int height);
    internal sealed record Result(string Status, Shape Shape = null, int? Alternate = null, Obstacle Blocker = null, Candidate[] Candidates = null)
    {
        internal bool Attempt => Status is "ready" or "native_replaceable" or "native_attempt_required";
    }

    internal static bool Replaceable(Tile tile, bool furniture)
    {
        if (!tile.HasTile) return false;
        int type = tile.TileType;
        // Crops are deliberate harvest targets even when native cut flags permit removal.
        if (type==TileID.Pumpkins || TileID.Sets.BasicChest[type] || TileID.Sets.BasicDresser[type]) return false;
        if (furniture) return !CompanionBridge.WorkbenchFootprint.Blocks(true, type,
            Main.tileCut[type], TileID.Sets.BreakableWhenPlacing[type]);
        return Main.tileCut[type] && type != 484 || TileID.Sets.BreakableWhenPlacing[type]
            || type is >= 373 and <= 375 or 461;
    }

    private static Result Occupied(int x, int y, Shape shape = null, int? alternate = null)
    {
        int type = Main.tile[x,y].TileType;
        bool furniture = Main.tileFrameImportant[type] && type != TileID.Pumpkins;
        return new(furniture ? "protected_or_occupied" : "occupied_requires_dig",
            shape, alternate, new(x,y,type));
    }

    private static bool Collider(Player p, Rectangle bounds) => bounds.Intersects(p.Hitbox) ||
        Main.player.Any(other => other.active && !other.dead && !other.ghost && bounds.Intersects(other.Hitbox));

    internal static Result Read(Player p, Item item, int x, int y)
    {
        if (!WorldGen.InWorld(x,y,5)) return new("unknown_or_not_loaded");
        var tile = Main.tile[x,y];
        if (item.createWall > 0)
        {
            if (tile.WallType != 0) return new(tile.WallType == item.createWall ? "already_matches" : "wall_conflict");
            bool Supports(Tile t) => t.HasTile || t.WallType != 0;
            return new(Supports(Main.tile[x-1,y]) || Supports(Main.tile[x+1,y]) ||
                Supports(Main.tile[x,y-1]) || Supports(Main.tile[x,y+1]) ? "ready" : "missing_support_or_anchor");
        }
        if (ActorObjectPlacement.CanPlace(item)) return Object(p,item,x,y);
        if (!ActorPlacement.Supported(item)) return new("unsupported_content");
        if (tile.HasTile && tile.TileType == item.createTile && !tile.IsActuated) return new("already_matches");
        if (tile.HasTile && !Replaceable(tile,false)) return Occupied(x,y);
        if (Main.tileSolid[item.createTile] && Collider(p,new(x*16,y*16,16,16))) return new("blocked_by_player");
        // BlockPlacementForAssortedThings IL_0e47..103a. Other target types can
        // take entirely different branches, so preserve one native attempt there.
        if (item.createTile is TileID.WoodBlock or TileID.Stone or TileID.Dirt)
        {
            bool Support(Tile t) => t.WallType!=0 || t.HasTile && (Main.tileSolid[t.TileType] ||
                TileID.Sets.IsBeam[t.TileType] || Main.tileRope[t.TileType] || t.TileType==TileID.MinecartTrack);
            if (tile.WallType==0 && !Support(Main.tile[x-1,y]) && !Support(Main.tile[x+1,y]) &&
                !Support(Main.tile[x,y-1]) && !Support(Main.tile[x,y+1])) return new("missing_support_or_anchor");
            return new(tile.HasTile ? "native_replaceable" : "ready");
        }
        return new(tile.HasTile ? "native_replaceable" : "native_attempt_required");
    }

    private static Result Footprint(Player p, Item item, int x, int y, Shape shape, int alternate)
    {
        int left=x-shape.OriginX, top=y-shape.OriginY;
        if (!WorldGen.InWorld(left,top,5) || !WorldGen.InWorld(left+shape.Width-1,top+shape.Height-1,5))
            return new("unknown_or_not_loaded",shape,alternate);
        var root=Main.tile[left,top];
        if (root.HasTile && root.TileType==item.createTile && root.TileFrameX%shape.Data.CoordinateFullWidth==0 &&
            root.TileFrameY%shape.Data.CoordinateFullHeight==0 && ActorObjectPlacement.ReadFrames(new(){
                Shape=shape,X=x,Y=y,TileType=item.createTile,Style=item.placeStyle,Alternate=alternate})!=null)
            return new("already_matches",shape,alternate);
        bool clears=false;
        for (int dx=0;dx<shape.Width;dx++)
            for (int dy=0;dy<shape.Height;dy++)
            {
                var t=Main.tile[left+dx,top+dy];
                if (t.HasTile && !Replaceable(t,true)) return Occupied(left+dx,top+dy,shape,alternate);
                if (!shape.Data.LiquidPlace(t,false)) return new("liquid_not_allowed",shape,alternate);
                clears |= t.HasTile;
            }
        if (Collider(p,new(left*16,top*16,shape.Width*16,shape.Height*16))) return new("blocked_by_player",shape,alternate);
        return new(clears?"native_replaceable":"ready",shape,alternate);
    }

    private static Result Object(Player p, Item item, int x, int y)
    {
        var primary=ActorObjectPlacement.Definition(item);
        var candidates=new List<Result>();
        bool safeQuery=true;
        for(int alt=0;alt<=primary.Data.AlternatesCount;alt++)
        {
            var shape=ActorObjectPlacement.Definition(item,alt);
            if(shape==null) return new("unknown_or_not_loaded");
            var cell=Footprint(p,item,x,y,shape,alt);
            if(cell.Status=="already_matches") return cell;
            var data=shape.Data;
            if((int)data.Direction==1 && p.direction==1 || (int)data.Direction==2 && p.direction==-1) continue;
            candidates.Add(cell);
            // CanPlace may run mod delegates and sample the RNG for random styles.
            var hook=data.HookCheckIfCanPlace.hook;
            safeQuery &= (hook==null || hook.Method.DeclaringType==typeof(Chest)) &&
                data.RandomStyleRange==0 && data.SpecificRandomStyles==null;
        }
        if(candidates.Count==0) return new("unknown_or_not_loaded");
        var first=candidates[0];
        if(!safeQuery) return new("unknown_or_not_loaded");
        if(TileObject.CanPlace(x,y,item.createTile,item.placeStyle,p.direction,out var placement))
        {
            var found=candidates.FirstOrDefault(c=>c.Alternate==placement.alternate);
            if(found!=null && placement.xCoord==x-found.Shape.OriginX && placement.yCoord==y-found.Shape.OriginY)
                return found;
            return new("unknown_or_not_loaded");
        }
        var viable = candidates.Where(candidate => candidate.Attempt).ToArray();
        if (viable.Length > 0)
        {
            var bounds = viable.Select(candidate => new Candidate(candidate.Alternate.Value,
                x - candidate.Shape.OriginX, y - candidate.Shape.OriginY,
                candidate.Shape.Width, candidate.Shape.Height)).ToArray();
            // No alternate has been selected. Keep nominal bounds for planning and
            // publish all possible footprints until native CanPlace resolves one.
            string status = viable.Any(candidate => candidate.Shape.Data.HookCheckIfCanPlace.hook != null)
                ? "unknown_or_not_loaded" : "missing_support_or_anchor";
            return new(status, viable[0].Shape, Candidates: bounds);
        }
        return candidates.All(c=>c.Status==first.Status && c.Blocker==first.Blocker) ? first : new("unknown_or_not_loaded");
    }
}
