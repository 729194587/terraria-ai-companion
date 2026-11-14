using System;
using System.Linq;
using System.Reflection;
using CompanionBridge.ServerIpc;
using Mono.Cecil;
using MonoMod.RuntimeDetour;
using Terraria;
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
        internal int ScopedY;
        internal bool RopeRetargetRefused;
        internal bool Placed,ExternalChange,Object,UncertainNativeEffect,NativeObjectPlacement;
        internal ActorObjectPlacement.Shape Shape;
        internal string SupportSnapshot, ObjectFrames;
        internal bool Unified, PostHookRejected;
        internal int WallType, Alternate, Direction, ChestIndex=-1;
        internal int? ActualStyle;
        internal Chest Chest;
        internal bool IsChest=>TileType>=0&&StorageTile(TileType);
        internal string Kind=>WallType>0?"wall":Rope!=null?"rope":IsChest?"chest":Object?"object":"tile";
    }
    [ThreadStatic] private static Player scope;
    private static Action<Player> place;
    private static Action<Player> placeWall, fillWalls;
    internal static bool UnifiedAvailable=>VirtualPlayerEngineProbe.Engine?.Selected=="2026.09.3.1"&&placeWall!=null;
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
        // Native rope extension retargets via the shared Player.tileTargetY.
        &&i.createWall<0&&i.tileWand<0&&!Main.tileRope[i.createTile]
        // These native static placement helpers read the process cursor without an Actor.
        &&i.createTile is not (TileID.Pumpkins or TileID.Coral or TileID.BeachPiles or TileID.Pigronata);
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
    internal static bool Allowed(Player p)=>ActorItemUse.Placing(p);
    private static int Coordinate(Player p,bool x)
    {
        if(!ReferenceEquals(scope,p))return x?Player.tileTargetX:Player.tileTargetY;
        if(!Program.IsMainThread||!Allowed(p))throw new InvalidOperationException("Placement target scope lost ownership.");
        var c=ActorItemUse.Current.Placement;return x?c.X:c.Rope!=null?c.ScopedY:c.Y;
    }
    public static int TargetX(Player p)=>Coordinate(p,true);
    public static int TargetY(Player p)=>Coordinate(p,false);
    public static bool TileReplacementEnabled(Player p)=>p.TileReplacementEnabled
        &&!(ReferenceEquals(scope,p)&&ActorItemUse.Current?.Placement is {} c&&(c.Rope!=null||c.Unified));
    public static void FillWalls(Player p)
    {
        if(!ReferenceEquals(scope,p))fillWalls(p);
    }
    public static bool PostPlacement(int x,int y,int type,int style,int direction,int alternate,TileObject data)
    {
        bool rejected=TileObjectData.CallPostPlacementPlayerHook(x,y,type,style,direction,alternate,data);
        if(rejected&&ActorItemUse.Current?.Placement is {Unified:true} c&&Own(c))
        {
            c.PostHookRejected=true;
            c.UncertainNativeEffect=true;
        }
        return rejected;
    }
    public static void RetargetRopeY(int y,Player p)
    {
        if(!ReferenceEquals(scope,p)){Player.tileTargetY=y;return;}
        var c=ActorItemUse.Current.Placement;
        if(!Program.IsMainThread||!Allowed(p)||c.Rope==null)throw new InvalidOperationException("Rope target scope lost ownership.");
        c.RopeRetargetRefused|=y!=c.Y;
        c.ScopedY=c.Y;
    }
    internal static void Install(Action<Action,Action> install)
    {
        using(var a=AssemblyDefinition.ReadAssembly(typeof(Player).Assembly.Location))
        {
            var p=a.MainModule.Types.Single(t=>t.FullName=="Terraria.Player");
            foreach(var name in PlacementPatches.Reads.Keys)PlacementPatches.Validate(p.Methods.Single(m=>m.Name==name));
            PlacementPatches.ValidateRope(p.Methods.Single(m=>m.Name==PlacementPatches.RopeMethod));
            if(VirtualPlayerEngineProbe.Engine.Selected=="2026.09.3.1")
                PlacementPatches.Validate(p.Methods.Single(m=>m.Name=="PlaceThing_Walls"));
        }
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
        ILHook ropeHook=null;
        install(()=>ropeHook=new ILHook(typeof(Player).GetMethod(PlacementPatches.RopeMethod,flags),il=>
            PlacementPatches.PatchRope(il.Method,
                il.Module.ImportReference(typeof(ActorPlacement).GetMethod(nameof(TargetX))),
                il.Module.ImportReference(typeof(ActorPlacement).GetMethod(nameof(TargetY))),
                il.Module.ImportReference(typeof(ActorPlacement).GetMethod(nameof(RetargetRopeY))))),()=>ropeHook?.Dispose());
        foreach(var name in PlacementPatches.Reads.Keys)
        {
            ILHook hook=null;var method=typeof(Player).GetMethod(name,flags);
            install(()=>hook=new ILHook(method,il=>PlacementPatches.Patch(il.Method,
                il.Module.ImportReference(typeof(ActorPlacement).GetMethod(nameof(TargetX))),
                il.Module.ImportReference(typeof(ActorPlacement).GetMethod(nameof(TargetY))),
                name=="PlaceThing_Tiles"?il.Module.ImportReference(typeof(ActorPlacement).GetMethod(nameof(TileReplacementEnabled))):null,
                postPlacement:name=="PlaceThing_Tiles_PlaceIt"&&VirtualPlayerEngineProbe.Engine.Selected=="2026.09.3.1"
                    ?il.Module.ImportReference(typeof(ActorPlacement).GetMethod(nameof(PostPlacement))):null)),()=>hook?.Dispose());
        }
        place=(Action<Player>)typeof(Player).GetMethod("PlaceThing_Tiles",flags).CreateDelegate(typeof(Action<Player>));
        if(VirtualPlayerEngineProbe.Engine.Selected=="2026.09.3.1")
        {
            var walls=typeof(Player).GetMethod("PlaceThing_Walls",flags);
            fillWalls=(Action<Player>)typeof(Player).GetMethod("PlaceThing_Walls_FillEmptySpace",flags).CreateDelegate(typeof(Action<Player>));
            ILHook hook=null;
            install(()=>hook=new ILHook(walls,il=>PlacementPatches.Patch(il.Method,
                il.Module.ImportReference(typeof(ActorPlacement).GetMethod(nameof(TargetX))),
                il.Module.ImportReference(typeof(ActorPlacement).GetMethod(nameof(TargetY))),
                il.Module.ImportReference(typeof(ActorPlacement).GetMethod(nameof(TileReplacementEnabled))),
                il.Module.ImportReference(typeof(ActorPlacement).GetMethod(nameof(FillWalls))))),()=>{hook?.Dispose();placeWall=null;});
            install(()=>On_WorldGen.PlaceWall+=WallPlaced,()=>On_WorldGen.PlaceWall-=WallPlaced);
            placeWall=(Action<Player>)walls.CreateDelegate(typeof(Action<Player>));
        }
        install(()=>On_WorldGen.PlaceTile+=Placed,()=>On_WorldGen.PlaceTile-=Placed);
        install(()=>On_TileObject.Place+=ActorObjectPlacement.Placed,()=>On_TileObject.Place-=ActorObjectPlacement.Placed);
        install(()=>On_WorldGen.KillTile+=Killed,()=>On_WorldGen.KillTile-=Killed);
    }
    internal static void Dispatch(Player p)
    {
        var use=ActorItemUse.Current;var c=use.Placement;
        if(!Allowed(p)||c.Placed||c.ExternalChange||use.Ticks!=1||!p.controlUseItem)return;
        if(c.Rope is {} expected&&(ActorRope.Resolve(p,use.Item,expected.X,expected.Y,out var actual)!=null||actual!=expected))
        {c.ExternalChange=true;return;}
        if(c.WallType>0?Main.tile[c.X,c.Y].WallType!=0:
            c.Object?ActorObjectPlacement.EmptyError(p,c.X,c.Y,c.Shape)!=null:Main.tile[c.X,c.Y].HasTile){c.ExternalChange=true;return;}
        if(scope!=null)throw new InvalidOperationException("Reentrant native placement rejected.");
        c.Attempts++;
        if(c.Rope is {} rope)c.ScopedY=rope.Y;
        try
        {
            scope=p;
            if(c.WallType>0)
            {
                placeWall(p);
                if(c.Placed)NetMessage.SendTileSquare(-1,c.X,c.Y,1);
                return;
            }
            place(p);
            if(c.Object&&c.Placed)
            {
                c.ObjectFrames=ActorObjectPlacement.ReadFrames(c);
                if(c.ObjectFrames==null)c.UncertainNativeEffect=true;
                if(c.Unified)ActorObjectPlacement.FinishNativePlacement(c);
            }
        }
        catch{c.UncertainNativeEffect=true;throw;}
        finally
        {
            scope=null;
            if(c.Unified&&!c.Placed&&!c.Object)
                c.UncertainNativeEffect|=c.WallType>0?Main.tile[c.X,c.Y].WallType!=0:Main.tile[c.X,c.Y].HasTile;
        }
    }
    private static void WallPlaced(On_WorldGen.orig_PlaceWall orig,int x,int y,int type,bool mute)
    {
        var c=ActorItemUse.Current?.Placement;
        if(c==null||c.WallType==0||!Own(c)){orig(x,y,type,mute);return;}
        if(x!=c.X||y!=c.Y||type!=c.WallType||Main.tile[x,y].WallType!=0)
        {c.UncertainNativeEffect=true;return;}
        orig(x,y,type,mute);
        if(Main.tile[x,y].WallType==type){c.Placed=true;c.NativeEvents++;}
        else if(Main.tile[x,y].WallType!=0)c.UncertainNativeEffect=true;
    }
    internal static Context Watching(int x,int y)
    {
        var u=ActorItemUse.Current;
        return ActorItemUse.Running&&u.Placement is {} c&&(c.Object?ActorObjectPlacement.Watches(c,x,y):c.X==x&&c.Y==y)?c:null;
    }
    private static bool Placed(On_WorldGen.orig_PlaceTile orig,int x,int y,int type,bool mute,bool forced,int plr,int style)
    {
        var c=Watching(x,y);
        bool own=c?.Object==false&&Own(c)&&x==c.X&&y==c.Y
            &&type==c.TileType&&plr==scope.whoAmI&&!forced&&!Main.tile[x,y].HasTile;
        if(scope!=null&&ActorItemUse.Current?.Placement?.Rope!=null
            &&(!own||c.RopeRetargetRefused||c.ScopedY!=c.Y||style!=c.Style))return false;
        bool result=orig(x,y,type,mute,forced,plr,style);
        if(c!=null&&result&&Main.tile[x,y].HasTile)
        {
            if(own&&Main.tile[x,y].TileType==c.TileType)
            {c.Placed=true;c.NativeEvents++;c.ActualStyle=style;NetMessage.SendTileSquare(-1,x,y,1);}
            else if(!(c.Object&&c.NativeObjectPlacement&&Own(c)))c.ExternalChange=true;
        }
        return result;
    }
    private static void Killed(On_WorldGen.orig_KillTile orig,int x,int y,bool fail,bool effectOnly,bool noItem)
    {
        if(scope!=null&&ActorItemUse.Current?.Placement is {} active
            &&(active.Rope!=null||active.WallType>0)&&!fail&&!effectOnly)return;
        var c=Watching(x,y);
        bool occupied=c!=null&&Main.tile[x,y].HasTile;
        bool ownClear=c?.Object==true&&c.NativeObjectPlacement&&Own(c)&&!c.Placed
            &&y<c.Y-c.Shape.OriginY+c.Shape.Height&&!ActorObjectPlacement.Blocks(Main.tile[x,y]);
        if(c!=null&&!fail&&!effectOnly&&occupied&&!ownClear)c.ExternalChange=true;
        orig(x,y,fail,effectOnly,noItem);
        if(ownClear&&!fail&&!effectOnly&&occupied&&!Main.tile[x,y].HasTile)c.NativeClearEvents++;
    }
    internal static bool Own(Context c)=>ReferenceEquals(scope,ActorItemUse.Current?.Actor)&&scope!=null&&Allowed(scope)
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
