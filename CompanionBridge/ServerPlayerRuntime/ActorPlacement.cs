using System;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.ID;
using Terraria.ObjectData;

namespace VirtualPlayerEngineProbe;

// A targeted native placement inside the existing G6 item-use lifecycle.
// No independent scheduler, input loop, inventory edit or WorldGen.PlaceTile call.
internal static class ActorPlacement
{
    internal sealed class Context
    {
        internal int X,Y,TileType,Prefix,Style,Attempts,ConsumedCount,NativeEvents,NativeClearEvents;
        internal bool Placed,ExternalChange,Object,UncertainNativeEffect,NativeObjectPlacement;
        internal ActorObjectPlacement.Shape Shape;
        internal string SupportSnapshot, ObjectFrames;
    }
    [ThreadStatic] private static Player scope;
    private static Action<Player> place;
    internal static bool TileMaterial(Item i)=>!i.IsAir&&i.consumable&&!i.channel
        &&i.createTile>=0&&i.createTile<Main.tileSolid.Length&&i.placeStyle>=0
        // Native rope extension retargets via the shared Player.tileTargetY.
        &&i.createWall<0&&i.tileWand<0&&!Main.tileRope[i.createTile]
        // These native static placement helpers read the process cursor without an Actor.
        &&i.createTile is not (TileID.Pumpkins or TileID.Coral or TileID.BeachPiles or TileID.Pigronata);
    internal static bool Supported(Item i)=>TileMaterial(i)&&!TileObjectData.CustomPlace(i.createTile,i.placeStyle);
    internal static string Preflight(Player p,int x,int y,bool objectPlacement=false)
    {
        if(objectPlacement)return ActorObjectPlacement.Preflight(p,x,y);
        if(!WorldGen.InWorld(x,y,2))return "invalid_target";
        if(Main.tile[x,y].HasTile)return "target_occupied";
        if(!Supported(p.HeldItem))return "unsupported_selected_placement_item";
        if(p.gravDir!=1||p.noBuilding||p.CCed||p.noItems||p.frozen||p.stoned||p.mount.Active)return "unsupported_placement_state";
        return null;
    }
    internal static Context Create(Player p,int x,int y,bool objectPlacement=false)
    {
        ActorObjectPlacement.Shape shape=null;
        string error=objectPlacement?ActorObjectPlacement.Preflight(p,x,y,out shape):Preflight(p,x,y);if(error!=null)throw new InvalidOperationException(error);
        var c=new Context{X=x,Y=y,TileType=p.HeldItem.createTile,Prefix=p.HeldItem.prefix,Style=p.HeldItem.placeStyle,Object=objectPlacement};
        if(objectPlacement){c.Shape=shape;c.SupportSnapshot=ActorObjectPlacement.SupportSnapshot(c);}
        return c;
    }
    internal static bool Allowed(Player p)=>ActorItemUse.Placing(p);
    private static int Coordinate(Player p,bool x)
    {
        if(!ReferenceEquals(scope,p))return x?Player.tileTargetX:Player.tileTargetY;
        if(!Program.IsMainThread||!Allowed(p))throw new InvalidOperationException("Placement target scope lost ownership.");
        var c=ActorItemUse.Current.Placement;return x?c.X:c.Y;
    }
    public static int TargetX(Player p)=>Coordinate(p,true);
    public static int TargetY(Player p)=>Coordinate(p,false);
    internal static void Install(Action<Action,Action> install)
    {
        using(var a=AssemblyDefinition.ReadAssembly(typeof(Player).Assembly.Location))
        {
            var p=a.MainModule.Types.Single(t=>t.FullName=="Terraria.Player");
            foreach(var name in PlacementPatches.Reads.Keys)PlacementPatches.Validate(p.Methods.Single(m=>m.Name==name));
        }
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
        foreach(var name in PlacementPatches.Reads.Keys)
        {
            ILHook hook=null;var method=typeof(Player).GetMethod(name,flags);
            install(()=>hook=new ILHook(method,il=>PlacementPatches.Patch(il.Method,
                il.Module.ImportReference(typeof(ActorPlacement).GetMethod(nameof(TargetX))),
                il.Module.ImportReference(typeof(ActorPlacement).GetMethod(nameof(TargetY))))),()=>hook?.Dispose());
        }
        place=(Action<Player>)typeof(Player).GetMethod("PlaceThing_Tiles",flags).CreateDelegate(typeof(Action<Player>));
        install(()=>On_WorldGen.PlaceTile+=Placed,()=>On_WorldGen.PlaceTile-=Placed);
        install(()=>On_TileObject.Place+=ActorObjectPlacement.Placed,()=>On_TileObject.Place-=ActorObjectPlacement.Placed);
        install(()=>On_WorldGen.KillTile+=Killed,()=>On_WorldGen.KillTile-=Killed);
    }
    internal static void Dispatch(Player p)
    {
        var use=ActorItemUse.Current;var c=use.Placement;
        if(!Allowed(p)||c.Placed||c.ExternalChange||use.Ticks!=1||!p.controlUseItem)return;
        if(c.Object?ActorObjectPlacement.EmptyError(p,c.X,c.Y,c.Shape)!=null:Main.tile[c.X,c.Y].HasTile){c.ExternalChange=true;return;}
        if(scope!=null)throw new InvalidOperationException("Reentrant native placement rejected.");
        c.Attempts++;
        try
        {
            scope=p;place(p);
            if(c.Object&&c.Placed)
            {
                c.ObjectFrames=ActorObjectPlacement.ReadFrames(c);
                if(c.ObjectFrames==null)c.UncertainNativeEffect=true;
            }
        }
        catch{c.UncertainNativeEffect=true;throw;}
        finally{scope=null;}
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
        bool result=orig(x,y,type,mute,forced,plr,style);
        if(c!=null&&result&&Main.tile[x,y].HasTile)
        {
            if(own&&Main.tile[x,y].TileType==c.TileType){c.Placed=true;c.NativeEvents++;NetMessage.SendTileSquare(-1,x,y,1);}
            else if(!(c.Object&&c.NativeObjectPlacement&&Own(c)))c.ExternalChange=true;
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
    internal static bool Own(Context c)=>ReferenceEquals(scope,ActorItemUse.Current?.Actor)&&scope!=null&&Allowed(scope)
        &&ReferenceEquals(c,ActorItemUse.Current.Placement);
    internal static bool SupportedFor(Context c,Item item)=>c?.Object==true?ActorObjectPlacement.CanPlace(item):Supported(item);
    internal static string Validate(ActorItemUse.Context use)
    {
        var c=use.Placement;var p=use.Actor;
        if(c.Object)return ActorObjectPlacement.Validate(use);
        if(c.ExternalChange||c.Placed&&(!Main.tile[c.X,c.Y].HasTile||Main.tile[c.X,c.Y].TileType!=c.TileType)
            ||!c.Placed&&Main.tile[c.X,c.Y].HasTile){c.ExternalChange=true;return "placement_target_changed";}
        if((!use.Item.IsAir||c.ConsumedCount==0)&&(!Supported(use.Item)||use.Item.prefix!=c.Prefix||use.Item.placeStyle!=c.Style||use.Item.createTile!=c.TileType))
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
        else if(c.Placed&&c.NativeEvents==1&&c.ConsumedCount==1&&!c.UncertainNativeEffect){use.Phase="completed";use.Reason=c.Object?"native_object_placed_and_consumed":"native_tile_placed_and_consumed";
            int left=c.X-(c.Object?c.Shape.OriginX:0),top=c.Y-(c.Object?c.Shape.OriginY:0);
            for(int y=top;y<top+(c.Object?c.Shape.Height:1);y++)for(int x=left;x<left+(c.Object?c.Shape.Width:1);x++)AiMapRuntime.Remember(x,y);}
        else {use.Phase=c.Placed||c.ConsumedCount!=0||c.UncertainNativeEffect?"unknown":"failed";
            use.Reason=c.Placed?"native_placement_consumption_unconfirmed":"native_placement_refused";}
    }
}
