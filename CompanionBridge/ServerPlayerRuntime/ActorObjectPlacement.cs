using System;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ObjectData;
using Terraria.Enums;

namespace VirtualPlayerEngineProbe;

// Observe the native placement event and retain the resolved object footprint.
internal static class ActorObjectPlacement
{
    internal static bool Blocks(Tile t)=>CompanionBridge.WorkbenchFootprint.Blocks(t.HasTile,t.TileType,
        Main.tileCut[t.TileType],TileID.Sets.BreakableWhenPlacing[t.TileType]);
    internal sealed record Shape(int Width,int Height,int OriginX,int OriginY,int SupportStart,int SupportCount,int Stride)
    {
        internal TileObjectData Data {get;init;}
    }
    private static Shape FromData(TileObjectData d)=>d==null?null:new(d.Width,d.Height,d.Origin.X,d.Origin.Y,
        d.AnchorBottom.checkStart,d.AnchorBottom.tileCount,d.CoordinateWidth+d.CoordinatePadding){Data=d};
    internal static Shape Definition(Item item)=>FromData(TileObjectData.GetTileData(item.createTile,item.placeStyle));
    internal static bool CanPlace(Item item)=>ActorPlacement.TileMaterial(item)
        &&TileObjectData.CustomPlace(item.createTile,item.placeStyle)&&Definition(item)!=null;
    // The scene's existing Work Bench suggestion remains scoped to that fixture.
    internal static Shape Definition()
    {
        var d=TileObjectData.GetTileData(TileID.WorkBenches,0);
        if(d==null||d.Width!=2||d.Height!=1||d.Origin.X!=0||d.Origin.Y!=0
            ||d.AnchorBottom.tileCount!=2||d.AnchorBottom.checkStart!=0
            ||d.AnchorBottom.type!=(AnchorType.SolidTile|AnchorType.SolidWithTop|AnchorType.SolidSide)||!TileObjectData.CustomPlace(TileID.WorkBenches,0)
            ||d.CoordinateWidth!=16||d.CoordinatePadding!=2||!d.CoordinateHeights.SequenceEqual(new[]{18}))return null;
        return FromData(d);
    }
    internal static bool Supported(Item i)=>!i.IsAir&&i.ModItem==null&&i.type==ItemID.WorkBench
        &&i.createTile==TileID.WorkBenches&&i.placeStyle==0&&i.consumable&&!i.channel&&i.shoot<=0
        &&i.createWall<0&&i.tileWand<0&&i.pick==0&&i.axe==0&&i.hammer==0&&Definition()!=null;
    internal static object Bounds(ActorPlacement.Context c)=>new{x=c.X-c.Shape.OriginX,y=c.Y-c.Shape.OriginY,width=c.Shape.Width,height=c.Shape.Height};
    internal static string Preflight(Player p,int x,int y)=>Preflight(p,x,y,out _);
    internal static string Preflight(Player p,int x,int y,out Shape shape)
    {
        shape=null;
        if(!CanPlace(p.HeldItem))return "unsupported_selected_object_item";
        if(p.gravDir!=1||p.noBuilding||p.CCed||p.noItems||p.frozen||p.stoned||p.mount.Active)return "unsupported_placement_state";
        var item=p.HeldItem;
        var primary=Definition(item);
        string error=EmptyError(p,x,y,primary);
        if(error==null){shape=primary;return null;}
        for(int alternate=1;alternate<=primary.Data.AlternatesCount;alternate++)
        {
            var candidate=FromData(TileObjectData.GetTileData(item.createTile,item.placeStyle,alternate));
            if(candidate!=null&&EmptyError(p,x,y,candidate)==null){shape=candidate;return null;}
        }
        return error;
    }
    internal static string PreflightDetail(Player p,int x,int y,string reason)
    {
        if(!CanPlace(p.HeldItem))return null;
        var d=Definition(p.HeldItem);
        int left=x-d.OriginX,top=y-d.OriginY;
        var footprint=new Rectangle(left*16,top*16,d.Width*16,d.Height*16);
        if(reason=="object_footprint_occupied")
            for(int dx=0;dx<d.Width;dx++)for(int dy=0;dy<d.Height;dy++)
            {
                var t=Main.tile[left+dx,top+dy];
                if(Blocks(t))return $"occupiedTile=({left+dx},{top+dy}); tileType={t.TileType}; footprintTiles=({left},{top},{d.Width},{d.Height}); anchorTile=({x},{y})";
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
        if(!WorldGen.InWorld(left,top,2)||!WorldGen.InWorld(left+d.Width-1,top+d.Height-1,2))return "invalid_target";
        var bounds=new Rectangle(left*16,top*16,d.Width*16,d.Height*16);
        if(bounds.Intersects(p.Hitbox)||Main.player.Any(other=>other.active&&!other.dead&&!other.ghost&&bounds.Intersects(other.Hitbox)))return "object_player_collision";
        for(int dx=0;dx<d.Width;dx++)for(int dy=0;dy<d.Height;dy++)
        {
            var t=Main.tile[left+dx,top+dy];
            if(Blocks(t))return "object_footprint_occupied";
        }
        return null;
    }
    internal static bool Watches(ActorPlacement.Context c,int x,int y)=>x>=c.X-c.Shape.OriginX&&x<c.X-c.Shape.OriginX+c.Shape.Width
        &&y>=c.Y-c.Shape.OriginY&&y<=c.Y-c.Shape.OriginY+c.Shape.Height;
    internal static string SupportSnapshot(ActorPlacement.Context c)
    {
        var d=c.Shape.Data;
        int left=c.X-c.Shape.OriginX,top=c.Y-c.Shape.OriginY;
        string Read(int x,int y)
        {
            if(!WorldGen.InWorld(x,y))return "?";
            var t=Main.tile[x,y];
            return $"{t.HasTile},{t.TileType},{t.IsActuated},{t.IsHalfBlock},{t.Slope},{t.LiquidAmount},{t.LiquidType}";
        }
        string Edge(Terraria.DataStructures.AnchorData anchor,int x,int y,bool horizontal)
            =>string.Join(";",Enumerable.Range(anchor.checkStart,anchor.tileCount)
                .Select(offset=>Read(x+(horizontal?offset:0),y+(horizontal?0:offset))));
        return string.Join("|",Edge(d.AnchorTop,left,top-1,true),Edge(d.AnchorBottom,left,top+d.Height,true),
            Edge(d.AnchorLeft,left-1,top,false),Edge(d.AnchorRight,left+d.Width,top,false));
    }
    internal static string ReadFrames(ActorPlacement.Context c)
    {
        var d=c.Shape.Data;
        int left=c.X-c.Shape.OriginX,top=c.Y-c.Shape.OriginY;
        var root=Main.tile[left,top];
        if(!root.HasTile||root.TileType!=c.TileType||TileObjectData.GetTileStyle(root)!=c.Style)return null;
        for(int dy=0,frameY=root.TileFrameY;dy<d.Height;frameY+=d.CoordinateHeights[dy++]+d.CoordinatePadding)
            for(int dx=0;dx<d.Width;dx++)
            {
                var t=Main.tile[left+dx,top+dy];
                if(!t.HasTile||t.IsActuated||t.TileType!=c.TileType
                    ||t.TileFrameX!=root.TileFrameX+dx*c.Shape.Stride||t.TileFrameY!=frameY)return null;
            }
        return $"{root.TileFrameX}:{root.TileFrameY}";
    }
    internal static bool Present(ActorPlacement.Context c)=>c.ObjectFrames!=null&&c.ObjectFrames==ReadFrames(c)
        &&(!c.Unified||!c.IsChest||c.Chest!=null&&ReferenceEquals(Main.chest[c.ChestIndex],c.Chest)
            &&c.Chest.x==c.X-c.Shape.OriginX&&c.Chest.y==c.Y-c.Shape.OriginY&&c.Chest.item?.Length==40);

    internal static void FinishNativePlacement(ActorPlacement.Context c)
    {
        int left=c.X-c.Shape.OriginX,top=c.Y-c.Shape.OriginY;
        if(c.IsChest)
        {
            // The native TileObjectData post-placement hook registers storage on the server.
            c.ChestIndex=Chest.FindChest(left,top);
            if(c.ChestIndex>=0)c.Chest=Main.chest[c.ChestIndex];
            if(!Present(c))c.UncertainNativeEffect=true;
            else
            {
                int action=c.TileType switch {TileID.Containers=>0,TileID.Containers2=>4,TileID.Dressers=>2,
                    _=>TileID.Sets.BasicDresser[c.TileType]?102:100};
                int packetX=TileID.Sets.BasicDresser[c.TileType]?left+1:left;
                NetMessage.SendData(MessageID.ChestUpdates,-1,-1,null,action,packetX,top+1,c.Style,c.ChestIndex,c.TileType);
            }
        }
        NetMessage.SendTileSquare(-1,left,top,c.Shape.Width,c.Shape.Height);
    }

    internal static bool Placed(On_TileObject.orig_Place orig,TileObject data)
    {
        var c=ActorItemUse.Current?.Placement;
        if(c?.Object!=true||!ActorPlacement.Own(c))return orig(data);
        var shape=FromData(TileObjectData.GetTileData(data.type,data.style,data.alternate));
        if(shape==null||data.type!=c.TileType||data.style!=c.Style
            ||data.xCoord+shape.OriginX!=c.X||data.yCoord+shape.OriginY!=c.Y)
        {c.UncertainNativeEffect=true;return orig(data); }
        c.Shape=shape;
        c.ActualStyle=data.style;
        c.Alternate=data.alternate;
        c.Direction=ActorItemUse.Current.Actor.direction;
        c.SupportSnapshot=SupportSnapshot(c);
        string before=FootprintState(c);
        c.NativeEvents++;
        c.NativeObjectPlacement=true;
        try
        {
            bool result=orig(data);
            c.Placed=result;
            c.UncertainNativeEffect|=!result&&FootprintState(c)!=before;
            return result;
        }
        catch{c.UncertainNativeEffect=true;throw;}
        finally{c.NativeObjectPlacement=false;}
    }
    private static string FootprintState(ActorPlacement.Context c)
    {
        int left=c.X-c.Shape.OriginX,top=c.Y-c.Shape.OriginY;
        return string.Join(";",from dx in Enumerable.Range(0,c.Shape.Width)
            from dy in Enumerable.Range(0,c.Shape.Height)
            let t=Main.tile[left+dx,top+dy]
            select $"{t.HasTile},{t.TileType},{t.TileFrameX},{t.TileFrameY},{t.WallType},{t.LiquidAmount}");
    }
    internal static string Validate(ActorItemUse.Context use)
    {
        var c=use.Placement;
        if(c.ExternalChange||c.Placed&&!Present(c)||c.SupportSnapshot!=SupportSnapshot(c))
        {c.ExternalChange=true;return "object_target_changed";}
        if(!c.Placed&&EmptyError(use.Actor,c.X,c.Y,c.Shape) is {} invalid)return invalid;
        if((!use.Item.IsAir||c.ConsumedCount==0)&&(!CanPlace(use.Item)||use.Item.prefix!=c.Prefix||use.Item.placeStyle!=c.Style||use.Item.createTile!=c.TileType))return "placement_item_changed";
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
