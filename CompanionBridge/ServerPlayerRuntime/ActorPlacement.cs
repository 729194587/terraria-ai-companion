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
        internal ActorRope.Target Rope;
        internal bool Placed,ExternalChange,Object,UncertainNativeEffect,NativeObjectPlacement;
        internal ActorObjectPlacement.Shape Shape;
        internal string SupportSnapshot, ObjectFrames;
        internal bool Unified;
        internal int WallType, Alternate, Direction, ChestIndex=-1;
        internal int? ActualStyle;
        internal Chest Chest;
        internal bool IsChest=>TileType>=0&&StorageTile(TileType);
        internal string Kind=>WallType>0?"wall":Rope!=null?"rope":IsChest?"chest":Object?"object":"tile";
    }
    [ThreadStatic] private static Player scope;
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
            if(Main.tile[x,y].WallType!=0)return "wall_target_occupied";
            context=new(){X=x,Y=y,WallType=item.createWall,TileType=-1,Prefix=item.prefix,Style=item.placeStyle,Unified=true};
            return null;
        }
        bool obj=kind is "object" or "chest";
        ActorRope.Target rope=null;
        string error=kind=="rope"?ActorRope.Resolve(p,item,x,y,out rope):Preflight(p,x,y,obj);
        if(error!=null)return error;
        context=Create(p,x,y,obj,rope);
        context.Unified=true;
        return null;
    }
    internal static bool TileMaterial(Item i)=>!i.IsAir&&i.consumable&&!i.channel
        &&i.createTile>=0&&i.createTile<Main.tileSolid.Length&&i.placeStyle>=0
        // Rope is classified separately because its native placement can extend a column.
        &&i.createWall<0&&i.tileWand<0&&!Main.tileRope[i.createTile];
    internal static bool Supported(Item i)=>TileMaterial(i)&&!TileObjectData.CustomPlace(i.createTile,i.placeStyle);
    internal static string Preflight(Player p,int x,int y,bool objectPlacement=false)
        =>Preflight(p,x,y,objectPlacement,out _);
    private static string Preflight(Player p,int x,int y,bool objectPlacement,out ActorRope.Target rope)
    {
        rope=null;
        if(objectPlacement)return ActorObjectPlacement.Preflight(p,x,y);
        if(!WorldGen.InWorld(x,y,2))return "invalid_target";
        if(Main.tile[x,y].HasTile)return "target_occupied";
        bool ropeMaterial=ActorRope.Supported(p.HeldItem);
        if(!ropeMaterial&&!Supported(p.HeldItem))return "unsupported_selected_placement_item";
        if(p.gravDir!=1||p.noBuilding||p.CCed||p.noItems||p.frozen||p.stoned||p.mount.Active)return "unsupported_placement_state";
        // An empty requested cell keeps place_tile's exact-coordinate contract.
        // Clicking an existing segment to extend it is handled by place_rope.
        return ropeMaterial?ActorRope.Resolve(p,p.HeldItem,x,y,out rope):null;
    }
    internal static Context Create(Player p,int x,int y,bool objectPlacement=false,ActorRope.Target rope=null)
    {
        ActorObjectPlacement.Shape shape=null;
        if(rope==null)
        {
            string error=objectPlacement?ActorObjectPlacement.Preflight(p,x,y,out shape):Preflight(p,x,y,false,out rope);
            if(error!=null)throw new InvalidOperationException(error);
        }
        if(rope!=null)return new Context{X=rope.X,Y=rope.EndY,Rope=rope,
            TileType=p.HeldItem.createTile,Prefix=p.HeldItem.prefix,Style=p.HeldItem.placeStyle};
        var c=new Context{X=x,Y=y,TileType=p.HeldItem.createTile,Prefix=p.HeldItem.prefix,Style=p.HeldItem.placeStyle,Object=objectPlacement};
        if(objectPlacement){c.Shape=shape;c.SupportSnapshot=ActorObjectPlacement.SupportSnapshot(c);}
        return c;
    }
    internal static void Install(Action<Action,Action> install)
    {
        install(()=>On_WorldGen.PlaceTile+=Placed,()=>On_WorldGen.PlaceTile-=Placed);
        install(()=>On_WorldGen.PlaceWall+=WallPlaced,()=>On_WorldGen.PlaceWall-=WallPlaced);
        install(()=>On_TileObject.Place+=ActorObjectPlacement.Placed,()=>On_TileObject.Place-=ActorObjectPlacement.Placed);
        install(()=>On_WorldGen.KillTile+=Killed,()=>On_WorldGen.KillTile-=Killed);
    }

    internal static bool InputActive(Player p)=>ReferenceEquals(scope,p);
    internal static void CheckItem(ActorItemUse.Context use)
    {
        var p=use.Actor;
        var c=use.Placement;
        if(scope!=null||!Program.IsMainThread||!ActorItemUse.Placing(p))
            throw new InvalidOperationException("Native placement input has no exclusive Actor scope.");

        int local=Main.myPlayer, mouseX=Main.mouseX, mouseY=Main.mouseY;
        int lastX=Main.lastMouseX,lastY=Main.lastMouseY;
        int targetX=Player.tileTargetX,targetY=Player.tileTargetY;
        var screen=Main.screenPosition;
        var mouseItem=Main.mouseItem;
        bool blockMouse=Main.blockMouse, mouseInterface=p.mouseInterface;
        bool left=Main.mouseLeft,leftRelease=Main.mouseLeftRelease,right=Main.mouseRight,rightRelease=Main.mouseRightRelease;
        int x=c.Rope?.X??c.X,y=c.Rope?.Y??c.Y;
        var aim=new Vector2(x*16+8,y*16+8);
        int eventsBefore=c.NativeEvents;
        var before=ReadTarget(c);
        try
        {
            scope=p;
            Main.myPlayer=p.whoAmI;
            Main.screenPosition=p.Center-new Vector2(Main.screenWidth,Main.screenHeight)*.5f;
            Main.mouseX=Main.lastMouseX=(int)(aim.X-Main.screenPosition.X);
            Main.mouseY=Main.lastMouseY=(int)(aim.Y-Main.screenPosition.Y);
            Main.mouseItem=new Item();
            Main.blockMouse=p.mouseInterface=false;
            Main.mouseLeft=p.controlUseItem;Main.mouseLeftRelease=p.releaseUseItem;
            Main.mouseRight=false;Main.mouseRightRelease=true;
            Player.tileTargetX=x;Player.tileTargetY=y;
            if(p.controlUseItem)c.Attempts++;
            p.ItemCheck();
        }
        finally
        {
            Player.tileTargetX=targetX;Player.tileTargetY=targetY;
            Main.mouseX=mouseX;Main.mouseY=mouseY;
            Main.lastMouseX=lastX;Main.lastMouseY=lastY;
            Main.screenPosition=screen;
            Main.mouseItem=mouseItem;Main.blockMouse=blockMouse;p.mouseInterface=mouseInterface;
            Main.mouseLeft=left;Main.mouseLeftRelease=leftRelease;
            Main.mouseRight=right;Main.mouseRightRelease=rightRelease;
            Main.myPlayer=local;
            scope=null;
        }
        if(c.NativeEvents==eventsBefore&&before!=ReadTarget(c))
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
        bool own=c?.Object==false&&Own(c)&&x==c.X&&y==c.Y&&type==c.TileType&&plr==scope.whoAmI&&!forced&&!Main.tile[x,y].HasTile;
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
        bool ownClear=c?.Object==true&&c.NativeObjectPlacement&&Own(c)&&!c.Placed
            &&y<c.Y-c.Shape.OriginY+c.Shape.Height&&!ActorObjectPlacement.Blocks(Main.tile[x,y]);
        if(c!=null&&!fail&&!effectOnly&&occupied&&!ownClear)c.ExternalChange=true;
        orig(x,y,fail,effectOnly,noItem);
        if(ownClear&&!fail&&!effectOnly&&occupied&&!Main.tile[x,y].HasTile)c.NativeClearEvents++;
    }
    internal static bool Own(Context c)=>scope!=null&&ReferenceEquals(scope,ActorItemUse.Current?.Actor)
        &&ReferenceEquals(c,ActorItemUse.Current.Placement);
    internal static bool SupportedFor(Context c,Item item)=>c?.WallType>0?WallMaterial(item):c?.Rope!=null?ActorRope.Supported(item)
        :c?.Object==true?ActorObjectPlacement.CanPlace(item):Supported(item);
    internal static bool Present(Context c)=>c.WallType>0?Main.tile[c.X,c.Y].WallType==c.WallType:
        c.Object?ActorObjectPlacement.Present(c):Main.tile[c.X,c.Y].HasTile&&Main.tile[c.X,c.Y].TileType==c.TileType
            &&(c.Rope==null||!Main.tile[c.X,c.Y].IsActuated);
    internal static string Validate(ActorItemUse.Context use)
    {
        var c=use.Placement;var p=use.Actor;
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
            ||!c.Placed&&(c.WallType>0?Main.tile[c.X,c.Y].WallType!=0:Main.tile[c.X,c.Y].HasTile)){c.ExternalChange=true;return "placement_target_changed";}
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
    internal static void AfterCheck(ActorItemUse.Context use,int beforeStack)
    {
        var c=use.Placement;int delta=beforeStack-use.Item.stack;
        if(delta!=0){c.ConsumedCount+=delta;use.Consumed=true;}
    }
    internal static void Complete(ActorItemUse.Context use)
    {
        var c=use.Placement;string invalid=Validate(use);
        if(invalid!=null){use.Phase=(c.Placed||c.ConsumedCount!=0||c.UncertainNativeEffect)?"unknown":"failed";use.Reason=invalid;}
        else if(c.Placed&&c.NativeEvents==1&&c.ConsumedCount==1&&!c.UncertainNativeEffect){use.Phase="completed";use.Reason=c.WallType>0?"native_wall_placed_and_consumed":c.Rope!=null?"native_rope_placed_and_consumed":c.Object?"native_object_placed_and_consumed":"native_tile_placed_and_consumed";
            int left=c.X-(c.Object?c.Shape.OriginX:0),top=c.Y-(c.Object?c.Shape.OriginY:0);
            for(int y=top;y<top+(c.Object?c.Shape.Height:1);y++)for(int x=left;x<left+(c.Object?c.Shape.Width:1);x++)AiMapRuntime.Remember(x,y);}
        else {use.Phase=c.Placed||c.ConsumedCount!=0||c.UncertainNativeEffect?"unknown":"failed";
            use.Reason=c.Placed?"native_placement_consumption_unconfirmed":c.Rope!=null?"native_rope_placement_refused":"native_placement_refused";}
    }
}
