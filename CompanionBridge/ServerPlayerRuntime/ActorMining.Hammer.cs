using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using CompanionBridge.ServerIpc;

namespace VirtualPlayerEngineProbe;

internal static partial class ActorMining
{
    internal sealed record HammerCell(bool hasTile, int tileType, int wallType, bool halfBlock, int slope, bool actuated)
    {
        internal static HammerCell Read(int x,int y)
        {
            var t=Main.tile[x,y];
            return new(t.HasTile,t.HasTile?t.TileType:0,t.WallType,t.IsHalfBlock,(int)t.Slope,t.IsActuated);
        }
    }

    internal sealed class HammerWork(bool wall,int power,HammerCell before)
    {
        internal readonly bool Wall=wall;
        internal readonly int Power=power;
        internal readonly HammerCell Before=before;
        internal HammerCell After=before;
        internal bool Effected, NativeMutation, Refused;
    }

    private static void InstallHammer(Action<Action,Action> install)
    {
        install(()=>On_Player.ItemCheck_UseMiningTools_TryHittingWall+=HitHammerWall,()=>On_Player.ItemCheck_UseMiningTools_TryHittingWall-=HitHammerWall);
        install(()=>On_WorldGen.KillWall+=WatchWallRemoval,()=>On_WorldGen.KillWall-=WatchWallRemoval);
        install(()=>On_WorldGen.SlopeTile+=WatchSlope,()=>On_WorldGen.SlopeTile-=WatchSlope);
        install(()=>On_WorldGen.PoundTile+=WatchPound,()=>On_WorldGen.PoundTile-=WatchPound);
    }

    private static Context WatchingHammer(int x,int y) =>
        Running && Current.Hammer!=null && Current.X==x && Current.Y==y ? Current : null;

    private static void ChangedByHammer(Context c,bool wall)
    {
        if(ReferenceEquals(c,executing) && striking && c.Hammer.Wall==wall && Allowed(c.Actor))
            c.Hammer.NativeMutation=true;
        else c.ExternalChange=true;
    }

    private static void WatchWallRemoval(On_WorldGen.orig_KillWall orig,int x,int y,bool fail)
    {
        var c=WatchingHammer(x,y);
        int before=c!=null?Main.tile[x,y].WallType:0;
        orig(x,y,fail);
        if(c!=null && before!=Main.tile[x,y].WallType)ChangedByHammer(c,true);
    }

    private static bool WatchSlope(On_WorldGen.orig_SlopeTile orig,int x,int y,int slope,bool noEffects)
    {
        var c=WatchingHammer(x,y);
        var before=c!=null?HammerCell.Read(x,y):null;
        bool result=orig(x,y,slope,noEffects);
        if(c!=null && before!=HammerCell.Read(x,y))ChangedByHammer(c,false);
        return result;
    }

    private static bool WatchPound(On_WorldGen.orig_PoundTile orig,int x,int y)
    {
        var c=WatchingHammer(x,y);
        var before=c!=null?HammerCell.Read(x,y):null;
        bool result=orig(x,y);
        if(c!=null && before!=HammerCell.Read(x,y))ChangedByHammer(c,false);
        return result;
    }

    private static bool PureHammer(Item item) => !item.IsAir && item.ModItem==null && item.hammer>0 &&
        item.pick==0 && item.axe==0 && !item.channel && item.shoot<=0 && item.createTile<0 && item.createWall<=0;

    // StartCore's caller owns the game thread, has rechecked the binding and rejected busy actors.
    internal static string StartHammer(Player p,string world,long generation,int x,int y,bool wall,out Context context)
    {
        context=null;
        ActorItemUse.Capture(p,world,generation);
        if(!Healthy(p))return "unsupported_actor_state";
        if(!WorldGen.InWorld(x,y,2))return "invalid_target";
        if(p.selectedItem is <0 or >9 || !PureHammer(p.HeldItem))return "selected_item_requires_pure_hammer";
        var cell=HammerCell.Read(x,y);
        if(wall)
        {
            if(cell.hasTile || cell.wallType==0 || cell.wallType>=WallID.Count || !Player.CanPlayerSmashWall(x,y))
                return "unsupported_wall_target";
        }
        else if(!cell.hasTile || cell.wallType!=0 || cell.actuated || cell.tileType>=TileID.Count ||
                Main.tileHammer[cell.tileType] || Main.tileAxe[cell.tileType] ||
                (!TileID.Sets.Platforms[cell.tileType] && (!Main.tileSolid[cell.tileType] || Main.tileFrameImportant[cell.tileType])))
            return "unsupported_hammer_target";
        var c=new Context{Actor=p,World=world,Generation=generation,Item=p.HeldItem,Slot=p.selectedItem,
            Type=p.HeldItem.type,Prefix=p.HeldItem.prefix,Hammer=new(wall,p.HeldItem.hammer,cell)};
        CaptureTarget(c,x,y);
        if(!HammerInRange(c))return "hammer_out_of_reach";
        ClearProgress(p,x,y,wall?2:1);
        p.controlUseItem=false;p.releaseUseItem=true;p.poundRelease=true;
        Current=context=c;
        return null;
    }

    private static bool HammerInRange(Context c) => Vector2.DistanceSquared(c.Actor.Center,
        new Vector2(c.X*16+8,c.Y*16+8))<=80*80 && InRange(c);

    private static bool HammerReady(Context c) => !c.Hammer.Effected && !c.ExternalChange && Allowed(c.Actor)
        && Healthy(c.Actor) && HammerCell.Read(c.X,c.Y)==c.Hammer.Before && HammerInRange(c);

    // Vanilla can select a neighbouring wall. Only dispatch the resolved strike for this exact cell.
    private static void HitHammerWall(On_Player.orig_ItemCheck_UseMiningTools_TryHittingWall orig,Player p,Item item,int x,int y)
    {
        if(!InInputScope(p) || executing.Hammer==null){orig(p,item,x,y);return;}
        var c=executing;
        if(c.Hammer?.Wall!=true || !usingTools || !ReferenceEquals(c.Item,item) || !HammerReady(c))return;
        if(x!=c.X || y!=c.Y || !Player.CanPlayerSmashWall(x,y))
        {c.Hammer.Refused=true;return;}
        try { striking=true;orig(p,item,x,y); }
        finally { striking=false; }
        ObserveHammerEffect(c);
    }

    private static void ObserveHammerEffect(Context c)
    {
        var work=c.Hammer;
        var after=HammerCell.Read(c.X,c.Y);
        if(after==work.Before)return;
        bool effect=work.Wall ? after==(work.Before with {wallType=0})
            : after==(work.Before with {halfBlock=after.halfBlock,slope=after.slope});
        work.After=after;
        if(!effect || !work.NativeMutation){c.ExternalChange=true;return;}
        work.Effected=true;
        SceneMineTargets.Changed(c.X,c.Y);
        NetMessage.SendTileSquare(-1,c.X,c.Y,1);
    }

    private static void UpdateHammer(Context c,Action<NativeItemInput.Intent> runNative)
    {
        var p=c.Actor;
        var work=c.Hammer;
        if(c.ExternalChange || HammerCell.Read(c.X,c.Y)!=work.After)
        {End(c,ActionPhase.Stale,Failure.TargetChanged);return;}
        if(!work.Effected && !HammerInRange(c)){End(c,ActionPhase.Failed,Failure.OutOfRange);return;}
        if(++c.Ticks>MaxUpdates){End(c,ActionPhase.TimedOut,Failure.Deadline);return;}
        executing=c;
        if(p.itemAnimation==0)p.releaseUseItem=true;
        runNative(NativeItemInput.Intent.AtTile(c.X,c.Y,!work.Effected));
        if(!Bound(c)||!NativeSurvival.Owns(p))throw new InvalidOperationException("Hammer binding changed inside ItemCheck.");
        if(c.Phase!=ActionPhase.Running)return;
        if(work.Refused){End(c,ActionPhase.Failed,Failure.NativeRefused,"Native wall target differs or is protected.");return;}
        if(c.ExternalChange || !Held(c) || HammerCell.Read(c.X,c.Y)!=work.After)
        {End(c,ActionPhase.Stale,Failure.TargetChanged);return;}
        if(work.Effected && p.itemAnimation==0 && p.itemTime==0 && p.reuseDelay==0)
        {End(c,ActionPhase.Completed,Failure.None);return;}
        if(c.Ticks==1 && p.itemAnimation==0){End(c,ActionPhase.Failed,Failure.NativeRefused);return;}
        if(c.Ticks==1 || c.Ticks%6==0)Sync(p);
    }
}
