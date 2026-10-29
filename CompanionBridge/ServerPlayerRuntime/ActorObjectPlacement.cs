using System;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ObjectData;
using Terraria.Enums;

namespace VirtualPlayerEngineProbe;

// Only the vanilla default Work Bench. All writes remain in the native Player
// placement call graph. These checks deliberately accept a subset of native sites.
internal static class ActorObjectPlacement
{
    internal sealed record Shape(int Width,int Height,int OriginX,int OriginY,int SupportStart,int SupportCount,int Stride);
    internal static Shape Definition()
    {
        var d=TileObjectData.GetTileData(TileID.WorkBenches,0);
        if(d==null||d.Width!=2||d.Height!=1||d.Origin.X!=0||d.Origin.Y!=0
            ||d.AnchorBottom.tileCount!=2||d.AnchorBottom.checkStart!=0
            ||d.AnchorBottom.type!=(AnchorType.SolidTile|AnchorType.SolidWithTop|AnchorType.SolidSide)||!TileObjectData.CustomPlace(TileID.WorkBenches,0)
            ||d.CoordinateWidth!=16||d.CoordinatePadding!=2||!d.CoordinateHeights.SequenceEqual(new[]{18}))return null;
        return new(d.Width,d.Height,d.Origin.X,d.Origin.Y,d.AnchorBottom.checkStart,d.AnchorBottom.tileCount,d.CoordinateWidth+d.CoordinatePadding);
    }
    internal static bool Supported(Item i)=>!i.IsAir&&i.ModItem==null&&i.type==ItemID.WorkBench
        &&i.createTile==TileID.WorkBenches&&i.placeStyle==0&&i.consumable&&!i.channel&&i.shoot<=0
        &&i.createWall<0&&i.tileWand<0&&i.pick==0&&i.axe==0&&i.hammer==0&&Definition()!=null;
    internal static object Bounds(ActorPlacement.Context c)=>new{x=c.X-c.Shape.OriginX,y=c.Y-c.Shape.OriginY,width=c.Shape.Width,height=c.Shape.Height};
    internal static string Preflight(Player p,int x,int y)
    {
        if(!Supported(p.HeldItem))return "unsupported_selected_object_item";
        if(p.gravDir!=1||p.noBuilding||p.CCed||p.noItems||p.frozen||p.stoned||p.mount.Active)return "unsupported_placement_state";
        return EmptyError(p,x,y,Definition());
    }
    internal static string PreflightDetail(Player p,int x,int y,string reason)
    {
        var d=Definition();if(d==null)return null;
        int left=x-d.OriginX,top=y-d.OriginY;
        var footprint=new Rectangle(left*16,top*16,d.Width*16,d.Height*16);
        if(reason=="out_of_conservative_80_pixel_reach")
            return FormattableString.Invariant($"anchorTile=({x},{y}); distancePixels={Vector2.Distance(p.Center,new Vector2(x*16+8,y*16+8)):F3}; limitPixels=80; actorCenterPixels=({p.Center.X:F3},{p.Center.Y:F3})");
        if(reason=="object_footprint_occupied")
            for(int dx=0;dx<d.Width;dx++)for(int dy=0;dy<d.Height;dy++)
            {
                var t=Main.tile[left+dx,top+dy];
                if(t.HasTile)return $"occupiedTile=({left+dx},{top+dy}); tileType={t.TileType}; footprintTiles=({left},{top},{d.Width},{d.Height}); anchorTile=({x},{y})";
            }
        if(reason=="object_player_collision")
        {
            var other=footprint.Intersects(p.Hitbox)?p:Main.player.FirstOrDefault(other=>other.active&&!other.dead&&!other.ghost&&footprint.Intersects(other.Hitbox));
            if(other!=null)return $"playerSlot={other.whoAmI}; playerBoundsPixels={other.Hitbox}; footprintPixels={footprint}; footprintTiles=({left},{top},{d.Width},{d.Height}); anchorTile=({x},{y})";
        }
        return null;
    }
    internal static string EmptyError(Player p,int x,int y,Shape d)
    {
        int left=x-d.OriginX,top=y-d.OriginY;
        if(!WorldGen.InWorld(left,top,2)||!WorldGen.InWorld(left+d.Width-1,top+d.Height,2))return "invalid_target";
        if(Vector2.Distance(p.Center,new Vector2(x*16+8,y*16+8))>80)return "out_of_conservative_80_pixel_reach";
        var bounds=new Rectangle(left*16,top*16,d.Width*16,d.Height*16);
        if(bounds.Intersects(p.Hitbox)||Main.player.Any(other=>other.active&&!other.dead&&!other.ghost&&bounds.Intersects(other.Hitbox)))return "object_player_collision";
        for(int dx=0;dx<d.Width;dx++)for(int dy=0;dy<d.Height;dy++)
        {
            var t=Main.tile[left+dx,top+dy];
            if(t.HasTile)return "object_footprint_occupied";
            if(t.LiquidAmount>0)return "object_footprint_liquid";
        }
        for(int dx=d.SupportStart;dx<d.SupportStart+d.SupportCount;dx++)
        {
            var t=Main.tile[left+dx,top+d.Height];
            if(!t.HasTile||t.IsActuated||!Main.tileSolid[t.TileType]||Main.tileSolidTop[t.TileType]||t.IsHalfBlock||(int)t.Slope!=0||t.LiquidAmount>0)return "object_support_missing";
        }
        return null;
    }
    internal static bool Watches(ActorPlacement.Context c,int x,int y)=>x>=c.X-c.Shape.OriginX&&x<c.X-c.Shape.OriginX+c.Shape.Width
        &&y>=c.Y-c.Shape.OriginY&&y<=c.Y-c.Shape.OriginY+c.Shape.Height;
    internal static string SupportSnapshot(ActorPlacement.Context c)=>string.Join(";",Enumerable.Range(c.Shape.SupportStart,c.Shape.SupportCount).Select(dx=>
    {
        var t=Main.tile[c.X-c.Shape.OriginX+dx,c.Y-c.Shape.OriginY+c.Shape.Height];
        return $"{t.HasTile},{t.TileType},{t.IsActuated},{t.IsHalfBlock},{t.Slope},{t.LiquidAmount},{t.LiquidType}";
    }));
    internal static bool Present(ActorPlacement.Context c)
    {
        for(int dx=0;dx<c.Shape.Width;dx++)for(int dy=0;dy<c.Shape.Height;dy++)
        {
            var t=Main.tile[c.X-c.Shape.OriginX+dx,c.Y-c.Shape.OriginY+dy];
            if(!t.HasTile||t.IsActuated||t.TileType!=TileID.WorkBenches||t.TileFrameX!=dx*c.Shape.Stride||t.TileFrameY!=0||t.LiquidAmount>0)return false;
        }
        return true;
    }
    internal static bool Placed(On_TileObject.orig_Place orig,TileObject data)
    {
        var c=ActorPlacement.Watching(data.xCoord,data.yCoord);
        bool own=c?.Object==true&&ActorPlacement.Own(c)&&data.type==c.TileType&&data.style==0&&data.alternate==0
            &&data.xCoord==c.X-c.Shape.OriginX&&data.yCoord==c.Y-c.Shape.OriginY
            &&EmptyError(ActorItemUse.Current.Actor,c.X,c.Y,c.Shape)==null;
        bool result;
        if(own)c.NativeEvents++;
        try{result=orig(data);}
        catch{if(own)c.UncertainNativeEffect=true;throw;}
        if(c?.Object==true)
        {
            if(own&&result&&Present(c)){c.Placed=true;NetMessage.SendTileSquare(-1,data.xCoord,data.yCoord,c.Shape.Width,c.Shape.Height);}
            else if(result||Present(c))c.ExternalChange=true;
            if(own&&(result!=Present(c)||!Present(c)&&EmptyError(ActorItemUse.Current.Actor,c.X,c.Y,c.Shape)!=null))c.UncertainNativeEffect=true;
        }
        return result;
    }
    internal static string Validate(ActorItemUse.Context use)
    {
        var c=use.Placement;
        if(c.ExternalChange||c.Placed&&!Present(c)||c.SupportSnapshot!=SupportSnapshot(c))
        {c.ExternalChange=true;return "object_target_changed";}
        if(!c.Placed&&EmptyError(use.Actor,c.X,c.Y,c.Shape) is {} invalid)return invalid;
        if((!use.Item.IsAir||c.ConsumedCount==0)&&(!Supported(use.Item)||use.Item.prefix!=c.Prefix))return "placement_item_changed";
        int remaining=use.Actor.inventory[use.Slot].IsAir?0:use.Actor.inventory[use.Slot].stack;
        if(remaining!=use.InitialStack-c.ConsumedCount)return "placement_stack_changed";
        if(use.Actor.gravDir!=1||use.Actor.noBuilding||use.Actor.frozen||use.Actor.stoned)return "unsupported_placement_state";
        return null;
    }
    // Called on the owning game thread before cancellation clears native timers.
    // A successful placement event without its matching observed consumption is
    // still uncertain, even before the next ItemCheck has a chance to validate it.
    internal static string CancellationUncertainty(ActorItemUse.Context use)
    {
        var c=use.Placement;
        if(c.UncertainNativeEffect)return "native_object_effect_unconfirmed";
        string invalid=Validate(use);if(invalid!=null)return invalid;
        if(c.NativeEvents==0&&!c.Placed&&c.ConsumedCount==0)return null;
        if(c.NativeEvents!=1||!c.Placed||!Present(c))return "native_object_effect_unconfirmed";
        if(c.ConsumedCount!=1)return "native_placement_consumption_unconfirmed";
        if(!ReferenceEquals(use.Actor.inventory[use.Slot],use.Item)
            &&!(use.Item.IsAir&&use.Actor.inventory[use.Slot].IsAir&&use.InitialStack==1))return "placement_item_changed";
        return null;
    }
}
