using System;
using CompanionBridge.ServerIpc;
using Terraria;
using Microsoft.Xna.Framework;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ObjectData;

namespace VirtualPlayerEngineProbe;

// A targeted native placement inside the existing G6 item-use lifecycle.
// No independent scheduler, input loop, inventory edit or WorldGen.PlaceTile call.
internal static class ActorPlacement
{
    internal sealed class Context
    {
        internal int X,Y,TileType,Prefix,Style,Attempts,ConsumedCount,NativeEvents,NativeClearEvents;
        internal int WaterBridgeDirection;
        internal ushort? InitialTile;
        internal ActorRope.Target Rope;
        internal bool NavigationEdit,Placed,ExternalChange,Object,UncertainNativeEffect,NativeObjectPlacement;
        internal ActorObjectPlacement.Shape Shape;
        internal string SupportSnapshot, ObjectFrames;
        internal bool Unified;
        internal int WallType, Alternate, Direction, ChestIndex=-1;
        internal int? ActualStyle;
        internal Chest Chest;
        internal bool IsChest=>TileType>=0&&StorageTile(TileType);
        internal string Kind=>WallType>0?"wall":Rope!=null?"rope":IsChest?"chest":Object?"object":"tile";
    }
    internal static bool UnifiedAvailable=>VirtualPlayerEngineProbe.Engine?.Selected=="2026.09.3.1";
    private static bool WallMaterial(Item i)=>!i.IsAir&&i.consumable&&!i.channel&&i.createTile<0
        &&i.createWall>0&&i.createWall<WallLoader.WallCount&&i.tileWand<0;
    private static bool StorageTile(int type)=>TileID.Sets.BasicChest[type]||TileID.Sets.BasicDresser[type];
    internal static string UnifiedKind(Item i)=>i.shoot>0||i.useAmmo>0?null:WallMaterial(i)?"wall":ActorRope.Supported(i)?"rope":
        ActorObjectPlacement.CanPlace(i)?(StorageTile(i.createTile)?"chest":"object"):
        Supported(i)&&!StorageTile(i.createTile)?"tile":null;
    internal static string PrepareUnified(Player p,int x,int y,out Context context)
    {
        context=null;
        if(!UnifiedAvailable)return "unsupported_operation";
        if(!WorldGen.InWorld(x,y,5))return "invalid_target";
        if(p.selectedItem is <0 or >9)return "unsupported_selected_placement_item";
        if(p.gravDir!=1||p.noBuilding||p.CCed||p.noItems||p.frozen||p.stoned||p.mount.Active)return "unsupported_placement_state";
        var item=p.HeldItem;
        string kind=UnifiedKind(item);
        if(kind==null)return "unsupported_selected_placement_item";
        if(!ActorRope.InReach(p,item,x,y))return "placement_out_of_range";
        if(kind=="wall")
        {
            var survey=NativePlacementSurvey.Read(p,item,x,y);
            if(!survey.Attempt)return survey.Status is "already_matches" or "wall_conflict"?"wall_target_occupied":survey.Status;
            context=new(){X=x,Y=y,WallType=item.createWall,TileType=-1,Prefix=item.prefix,Style=item.placeStyle,Unified=true};
            return null;
        }
        bool obj=kind is "object" or "chest";
        if(kind=="rope")
        {
            string error=ActorRope.Resolve(p,item,x,y,out var rope);
            if(error!=null)return error;
            context=FromRope(item,rope);
        }
        else
        {
            string error=Prepare(p,x,y,obj,out context);
            if(error!=null)return error;
        }
        context.Unified=true;
        return null;
    }
    internal static bool TileMaterial(Item i)=>!i.IsAir&&i.consumable&&!i.channel
        &&i.createTile>=0&&i.createTile<Main.tileSolid.Length&&i.placeStyle>=0
        // Rope is classified separately because its native placement can extend a column.
        &&i.createWall<0&&i.tileWand<0&&!Main.tileRope[i.createTile];
    internal static bool Supported(Item i)=>TileMaterial(i)&&!TileObjectData.CustomPlace(i.createTile,i.placeStyle);
    internal static Context FromRope(Item item,ActorRope.Target rope)=>new()
    {
        X=rope.X,Y=rope.EndY,Rope=rope,TileType=item.createTile,Prefix=item.prefix,Style=item.placeStyle
    };

    internal static string Prepare(Player p,int x,int y,bool objectPlacement,out Context context)
    {
        context=null;
        var item=p.HeldItem;
        ActorObjectPlacement.Shape shape=null;
        if(objectPlacement)
        {
            string error=ActorObjectPlacement.Preflight(p,x,y,out shape);
            if(error!=null)return error;
        }
        else
        {
            if(!WorldGen.InWorld(x,y,2))return "invalid_target";
            bool ropeMaterial=ActorRope.Supported(item);
            if(!ropeMaterial&&!Supported(item))return "unsupported_selected_placement_item";
            if(p.gravDir!=1||p.noBuilding||p.CCed||p.noItems||p.frozen||p.stoned||p.mount.Active)return "unsupported_placement_state";
            if(ropeMaterial)
            {
                string error=ActorRope.Resolve(p,item,x,y,out var rope);
                if(error==null)context=FromRope(item,rope);
                return error;
            }
            var survey=NativePlacementSurvey.Read(p,item,x,y);
            if(!survey.Attempt)return survey.Status is "occupied_requires_dig" or "already_matches" or "protected_or_occupied"
                ? "target_occupied" : survey.Status;
        }
        context=new(){X=x,Y=y,TileType=item.createTile,Prefix=item.prefix,Style=item.placeStyle,Object=objectPlacement,Shape=shape};
        if(!objectPlacement && Main.tile[x,y].HasTile)context.InitialTile=Main.tile[x,y].TileType;
        if(objectPlacement)context.SupportSnapshot=ActorObjectPlacement.SupportSnapshot(context);
        return null;
    }
    internal static void Install(Action<Action,Action> install)
    {
        install(()=>On_WorldGen.PlaceTile+=Placed,()=>On_WorldGen.PlaceTile-=Placed);
        install(()=>On_WorldGen.PlaceWall+=WallPlaced,()=>On_WorldGen.PlaceWall-=WallPlaced);
        install(()=>On_TileObject.Place+=ActorObjectPlacement.Placed,()=>On_TileObject.Place-=ActorObjectPlacement.Placed);
        install(()=>On_WorldGen.KillTile+=Killed,()=>On_WorldGen.KillTile-=Killed);
    }

    internal static NativeItemInput.Intent Input(Context c,bool press)
        =>NativeItemInput.Intent.AtTile(c.Rope?.X??c.X,c.Rope?.Y??c.Y,press);

    internal readonly record struct Observation(int Events,(bool Active,ushort Tile,ushort Wall,short FrameX,short FrameY) Target);

    internal static Observation BeforeCheck(Context c,bool press)
    {
        if(press)c.Attempts++;
        return new(c.NativeEvents,ReadTarget(c));
    }

    internal static void AfterCheck(ActorItemUse.Context use,int beforeStack,Observation observation)
    {
        var c=use.Placement;
        var (eventsBefore,before)=observation;
        var after=ReadTarget(c);
        // ItemCheck can spend the first press clearing replaceable water plants.
        // Only recognize our own clear at the requested cell with stock intact.
        bool clearedForBridge=c.WaterBridgeDirection!=0 && c.NativeClearEvents==1 && !c.ExternalChange
            && !c.Placed && !after.active && c.ConsumedCount==0 && use.Actor.inventory[use.Slot].stack==use.InitialStack;
        if(clearedForBridge)c.InitialTile=null;
        if(c.NativeClearEvents>0&&!c.Placed&&!clearedForBridge)c.UncertainNativeEffect=true;
        if(c.NativeEvents==eventsBefore&&before!=after&&!(clearedForBridge&&before.Active))
        {
            c.UncertainNativeEffect=true;
            NetMessage.SendTileSquare(-1,c.X,c.Y,1);
        }
        if(c.Object&&c.Placed&&c.NativeEvents!=eventsBefore)
        {
            c.ObjectFrames=ActorObjectPlacement.ReadFrames(c);
            if(c.ObjectFrames==null)c.UncertainNativeEffect=true;
            ActorObjectPlacement.FinishNativePlacement(c);
        }
        int delta=beforeStack-use.Item.stack;
        if(delta!=0){c.ConsumedCount+=delta;use.Consumed=true;}
    }
    private static (bool active,ushort tile,ushort wall,short frameX,short frameY) ReadTarget(Context c)
    {
        var tile=Main.tile[c.X,c.Y];
        return (tile.HasTile,tile.TileType,tile.WallType,tile.TileFrameX,tile.TileFrameY);
    }
    private static void WallPlaced(On_WorldGen.orig_PlaceWall orig,int x,int y,int type,bool mute)
    {
        var c=ActorItemUse.Current?.Placement;
        if(c==null||c.WallType==0||!Own(c)){orig(x,y,type,mute);return;}
        int before=WorldGen.InWorld(x,y)?Main.tile[x,y].WallType:0;
        orig(x,y,type,mute);
        if(!WorldGen.InWorld(x,y)||Main.tile[x,y].WallType==before)return;
        NetMessage.SendTileSquare(-1,x,y,1);
        if(x==c.X&&y==c.Y&&type==c.WallType&&Main.tile[x,y].WallType==type)
        {c.Placed=true;c.NativeEvents++;}
        else c.UncertainNativeEffect=true;
    }
    internal static Context Watching(int x,int y)
    {
        var u=ActorItemUse.Current;
        return ActorItemUse.Running&&u.Placement is {} c&&(c.Object?ActorObjectPlacement.Watches(c,x,y):c.X==x&&c.Y==y)?c:null;
    }
    private static bool Placed(On_WorldGen.orig_PlaceTile orig,int x,int y,int type,bool mute,bool forced,int plr,int style)
    {
        var c=Watching(x,y);
        var active=ActorItemUse.Current?.Placement;
        bool own=c?.Object==false&&Own(c)&&x==c.X&&y==c.Y&&type==c.TileType&&plr==ActorItemUse.Current.Actor.whoAmI&&!forced&&(!Main.tile[x,y].HasTile||NativePlacementSurvey.Replaceable(Main.tile[x,y],false));
        bool result=orig(x,y,type,mute,forced,plr,style);
        if(result&&c==null&&active!=null&&Own(active)&&!active.NativeObjectPlacement&&WorldGen.InWorld(x,y))
        {
            active.UncertainNativeEffect=true;
            NetMessage.SendTileSquare(-1,x,y,1);
        }
        if(c!=null&&result&&Main.tile[x,y].HasTile)
        {
            if(own&&Main.tile[x,y].TileType==c.TileType)
            {c.Placed=true;c.NativeEvents++;c.ActualStyle=style;NetMessage.SendTileSquare(-1,x,y,1);}
            else if(!(c.Object&&Own(c)))c.ExternalChange=true;
        }
        return result;
    }
    private static void Killed(On_WorldGen.orig_KillTile orig,int x,int y,bool fail,bool effectOnly,bool noItem)
    {
        var c=Watching(x,y);
        bool occupied=c!=null&&Main.tile[x,y].HasTile;
        bool ownClear=c!=null && Own(c) && !c.Placed && c.WallType==0 && c.Rope==null &&
            (!c.Object || y<c.Y-c.Shape.OriginY+c.Shape.Height) &&
            NativePlacementSurvey.Replaceable(Main.tile[x,y],c.Object);
        if(c!=null&&!fail&&!effectOnly&&occupied&&!ownClear)c.ExternalChange=true;
        orig(x,y,fail,effectOnly,noItem);
        if(ownClear&&!fail&&!effectOnly&&occupied&&!Main.tile[x,y].HasTile)c.NativeClearEvents++;
    }
    internal static bool Own(Context c)=>ActorItemUse.Current is {} use&&ReferenceEquals(c,use.Placement)
        &&NativeItemInput.Active(use.Actor)&&ActorItemUse.Placing(use.Actor);
    internal static bool SupportedFor(Context c,Item item)=>c?.WallType>0?WallMaterial(item):c?.Rope!=null?ActorRope.Supported(item)
        :c?.Object==true?ActorObjectPlacement.CanPlace(item):Supported(item);
    internal static bool Present(Context c)=>c.WallType>0?Main.tile[c.X,c.Y].WallType==c.WallType:
        c.Object?ActorObjectPlacement.Present(c):Main.tile[c.X,c.Y].HasTile&&Main.tile[c.X,c.Y].TileType==c.TileType
            &&(c.Rope==null||!Main.tile[c.X,c.Y].IsActuated);
    internal static string Validate(ActorItemUse.Context use)
    {
        var c=use.Placement;var p=use.Actor;
        if(c.NavigationEdit&&!c.Placed&&NavigationProtection.Reserved(c.X,c.Y))return "navigation_protected_structure";
        if(c.Unified&&use.Ticks==0)
        {
            if(!ActorRope.InReach(p,use.Item,c.Rope?.X??c.X,c.Rope?.Y??c.Y))return "placement_out_of_range";
            if(use.Item.shoot>0||use.Item.useAmmo>0)return "unsupported_selected_placement_item";
        }
        if(c.Object)return ActorObjectPlacement.Validate(use);
        if(use.Ticks==0&&c.Rope is {} rope)
        {
            string error=ActorRope.Resolve(p,use.Item,rope.X,rope.Y,out var actual);
            if(error!=null)return error;
            if(actual!=rope)return "rope_target_changed";
        }
        if(use.Ticks==0&&c.WaterBridgeDirection!=0&&(c.TileType!=TileID.Platforms
            ||!NativeNavigationEdits.WaterPlacementStillSafe(p,c.X,c.Y,c.WaterBridgeDirection,use.Item.stack)))
            return "navigation_water_placement_changed";
        if(c.ExternalChange||c.Placed&&!Present(c)
            ||!c.Placed&&(c.WallType>0?Main.tile[c.X,c.Y].WallType!=0:(Main.tile[c.X,c.Y].HasTile?(ushort?)Main.tile[c.X,c.Y].TileType:null)!=c.InitialTile)){c.ExternalChange=true;return "placement_target_changed";}
        if((!use.Item.IsAir||c.ConsumedCount==0)&&(!SupportedFor(c,use.Item)||use.Item.prefix!=c.Prefix||use.Item.placeStyle!=c.Style
            ||(c.WallType>0?use.Item.createWall!=c.WallType:use.Item.createTile!=c.TileType)))
            return "placement_item_changed";
        int remaining=p.inventory[use.Slot].IsAir?0:p.inventory[use.Slot].stack;
        if(remaining!=use.InitialStack-c.ConsumedCount)return "placement_stack_changed";
        if(p.gravDir!=1||p.noBuilding||p.frozen||p.stoned)return "unsupported_placement_state";
        return null;
    }
    internal static string CancellationUncertainty(ActorItemUse.Context use)
    {
        var c=use.Placement;
        if(c.Object)return ActorObjectPlacement.CancellationUncertainty(use);
        if(c.UncertainNativeEffect)return "native_placement_effect_unconfirmed";
        if(c.NativeEvents==0&&!c.Placed&&c.ConsumedCount==0)return null;
        return Validate(use)??(c.NativeEvents==1&&c.Placed&&c.ConsumedCount==1
            ?null:"native_placement_effect_unconfirmed");
    }
    internal static void Complete(ActorItemUse.Context use)
    {
        var c=use.Placement;string invalid=Validate(use);
        if(invalid!=null){use.Phase=(c.Placed||c.ConsumedCount!=0||c.UncertainNativeEffect)?"unknown":"failed";use.Reason=invalid;}
        else if(c.Placed&&c.NativeEvents==1&&c.ConsumedCount==1&&!c.UncertainNativeEffect){use.Phase="completed";use.Reason=c.WallType>0?"native_wall_placed_and_consumed":c.Rope!=null?"native_rope_placed_and_consumed":c.Object?"native_object_placed_and_consumed":"native_tile_placed_and_consumed";
            int left=c.X-(c.Object?c.Shape.OriginX:0),top=c.Y-(c.Object?c.Shape.OriginY:0);
            for(int y=top;y<top+(c.Object?c.Shape.Height:1);y++)for(int x=left;x<left+(c.Object?c.Shape.Width:1);x++)AiMapRuntime.Remember(x,y);}
        else {use.Phase=c.Placed||c.ConsumedCount!=0||c.UncertainNativeEffect?"unknown":"failed";
            use.Reason=c.Placed?"native_placement_consumption_unconfirmed":
                c.WaterBridgeDirection!=0&&c.NativeClearEvents==1&&!c.UncertainNativeEffect?"navigation_water_placement_changed":
                c.Rope!=null?"native_rope_placement_refused":"native_placement_refused";}
    }
}
