using System;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using MonoMod.Utils;
using Terraria;
using Terraria.ID;

namespace VirtualPlayerEngineProbe;

// G7 executor only. The existing mine_tile/Lua/IPC lifecycle will own adaptation.
internal static class ActorMining
{
    internal enum Failure { None, InvalidTarget, UnsupportedTarget, OutOfRange, InvalidTool, Busy,
        ActorUnavailable, BindingChanged, ItemChanged, TargetChanged, NativeRefused, NoPickPower, NativeException, ExplicitStop, DeathOrRetirement, Deadline }
    internal sealed class Context
    {
        internal readonly Guid Id=Guid.NewGuid();
        internal Player Actor;internal Item Item;internal string World;internal long Generation;
        internal int Slot,Type,Prefix,Pick, X,Y,TileType,FrameX,FrameY,Ticks,Swings,Strikes,Damage,PeakDamage,AnimationPeak;
        internal byte Slope;internal bool Half,Actuated,Destroyed,ExternalChange;
        internal ActionPhase Phase=ActionPhase.Running;internal Failure Reason;internal string Detail="";
    }
    private delegate void NativeTool(Player p,Item item,ref bool hitWall,int x,int y);
    private static NativeTool tool;
    private static Func<Player,Item,bool> inRange;
    [ThreadStatic] private static Context rangeContext;
    internal static Context Current;
    private static Context executing;
    private static bool striking;
    private static ulong? suppressPickupTick;
    internal static bool Running=>Current?.Phase==ActionPhase.Running;
    internal static void Reset(){Current=executing=rangeContext=null;striking=false;suppressPickupTick=null;}
    internal static void Install(Action<Action,Action> install)
    {
        using(var a=AssemblyDefinition.ReadAssembly(typeof(Player).Assembly.Location))
            MiningPatches.Validate(a.MainModule.Types.Single(t=>t.FullName=="Terraria.Player"));
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
        tool=(NativeTool)typeof(Player).GetMethod("ItemCheck_UseMiningTools_ActuallyUseMiningTool",flags).CreateDelegate(typeof(NativeTool));
        using(var dmd=new DynamicMethodDefinition(typeof(Player).GetMethod("IsTargetTileInItemRange",flags)))
        {
            MiningPatches.RangeClone(dmd.Definition,dmd.Definition.Module.ImportReference(typeof(ActorMining).GetMethod(nameof(RangeX))),
                dmd.Definition.Module.ImportReference(typeof(ActorMining).GetMethod(nameof(RangeY))));
            inRange=(Func<Player,Item,bool>)dmd.Generate().CreateDelegate(typeof(Func<Player,Item,bool>));
        }
        install(()=>On_HitTile.AddDamage+=Damage,()=>On_HitTile.AddDamage-=Damage);
        install(()=>On_WorldGen.KillTile+=Kill,()=>On_WorldGen.KillTile-=Kill);
        install(()=>On_WorldGen.PlaceTile+=Place,()=>On_WorldGen.PlaceTile-=Place);
        install(()=>On_Player.GrabItems+=Grab,()=>On_Player.GrabItems-=Grab);
    }
    private static bool Bound(Context c)=>c!=null&&ReferenceEquals(c.Actor,ProbeSession.Actor)
        &&c.World==ProbeSession.WorldSession&&c.Generation==ProbeSession.ActorGeneration
        &&Main.dedServ&&Main.netMode==NetmodeID.Server&&ProbeSession.Reservation.Snapshot is {} v
        &&c.Actor.whoAmI==v.Slot&&Main.player!=null&&v.Slot>=0&&v.Slot<Main.player.Length&&ReferenceEquals(Main.player[v.Slot],c.Actor)
        &&Netplay.Clients!=null&&v.Slot<Netplay.Clients.Length
        &&VirtualPlayerEngineProbe.ConnectionIdle(Netplay.Clients[v.Slot]);
    private static bool Held(Context c)=>c.Actor.selectedItem==c.Slot&&ReferenceEquals(c.Actor.inventory[c.Slot],c.Item)
        &&!c.Item.IsAir&&c.Item.type==c.Type&&c.Item.prefix==c.Prefix&&c.Item.pick==c.Pick&&SupportedTool(c.Item);
    private static Context RangeTarget()=>Program.IsMainThread&&Bound(rangeContext)?rangeContext:throw new InvalidOperationException("No bound G7 reach scope.");
    public static int RangeX()=>RangeTarget().X;
    public static int RangeY()=>RangeTarget().Y;
    private static bool InRange(Context c)
    {
        if(rangeContext!=null)throw new InvalidOperationException("Reentrant G7 reach check.");
        try{rangeContext=c;return inRange(c.Actor,c.Item);}finally{rangeContext=null;}
    }
    private static bool SupportedTool(Item i)=>!i.IsAir&&i.pick>0&&i.axe==0&&i.hammer==0&&!i.channel&&i.shoot<=0;
    private static bool Same(Context c)
    {
        var t=Main.tile[c.X,c.Y];return t.HasTile&&t.TileType==c.TileType
            &&(byte)t.Slope==c.Slope&&t.IsHalfBlock==c.Half&&t.IsActuated==c.Actuated;
    }
    private static bool Healthy(Player p)=>!p.dead&&!p.ghost&&!p.CCed&&!p.noItems&&!p.noBuilding&&!p.frozen&&!p.stoned&&!p.mount.Active
        &&!Main.gameMenu&&!Netplay.Disconnect&&float.IsFinite(p.position.X)&&float.IsFinite(p.position.Y);
    internal static void Start(Player p,string world,long generation,int x,int y)
    {
        ProbeSession.Reservation.AssertOwner(Program.IsMainThread);
        lock(ProbeSession.Reservation.Gate)
        {
            ActorItemUse.Capture(p,world,generation);
            if(Running||ActorItemUse.Running||ActorControl.Current?.Phase==ActionPhase.Running
                ||p.itemAnimation!=0||p.itemTime!=0||p.reuseDelay!=0||p.channel)
                throw new InvalidOperationException("Busy: stop walking/item use/mining before starting another action.");
            var c=new Context{Actor=p,World=world,Generation=generation,X=x,Y=y,Slot=p.selectedItem,Item=p.HeldItem};Current=c;
            void Reject(Failure reason,string detail){c.Phase=ActionPhase.Failed;c.Reason=reason;c.Detail=detail;Emit(c);}
            if(!Healthy(p)){Reject(Failure.ActorUnavailable,"Actor cannot mine in its current state.");return;}
            if(p.selectedItem<0||p.selectedItem>9||!SupportedTool(c.Item)){Reject(Failure.InvalidTool,"Select a real non-projectile pickaxe in hotbar 1–10; combined axe/hammer tools are outside G7.");return;}
            c.Type=c.Item.type;c.Prefix=c.Item.prefix;c.Pick=c.Item.pick;
            if(!WorldGen.InWorld(x,y,2)||!Main.tile[x,y].HasTile){Reject(Failure.InvalidTarget,"Target must be an occupied in-world tile.");return;}
            var t=Main.tile[x,y];int type=t.TileType;
            if(!Main.tileSolid[type]||Main.tileFrameImportant[type]||Main.tileAxe[type]||Main.tileHammer[type]
                ||Main.tileCut[type]||TileID.Sets.Falling[type]||t.IsActuated)
            {Reject(Failure.UnsupportedTarget,"G7 supports solid single-cell pick targets; furniture, containers, falling blocks and axe/hammer targets are refused.");return;}
            c.TileType=type;c.FrameX=t.TileFrameX;c.FrameY=t.TileFrameY;c.Slope=(byte)t.Slope;c.Half=t.IsHalfBlock;c.Actuated=t.IsActuated;
            if(!InRange(c)){Reject(Failure.OutOfRange,"Native item reach check refused the fixed target; no movement is attempted.");return;}
            // New explicit intent never inherits another action's partial progress.
            p.hitTile.ClearThisTile(x,y);
            p.controlUseItem=false;p.releaseUseItem=true;
        }
    }
    public static bool Allowed(Player p)=>executing!=null&&ReferenceEquals(executing.Actor,p)&&Bound(executing)
        &&NativeSurvival.Owns(p)&&!p.dead&&Held(executing);
    // Called exactly at G6's owner tool stage, after native toolTime bookkeeping.
    internal static void Strike(Player p)
    {
        var c=executing;if(!Allowed(p)||c.Destroyed||c.ExternalChange||!Same(c))return;
        if(p.toolTime!=0||p.itemAnimation<=0||!p.controlUseItem)return;
        if(!Healthy(p)||!InRange(c))return;
        c.Strikes++;c.Damage=0;bool wall=false;
        try{striking=true;tool(p,c.Item,ref wall,c.X,c.Y);}
        finally{striking=false;}
        // A single-cell executor must not follow native transformations.
        if(!c.Destroyed&&!Same(c))c.ExternalChange=true;
    }
    private static int Damage(On_HitTile.orig_AddDamage orig,HitTile hits,int id,int damage,bool update)
    {
        int total=orig(hits,id,damage,update);var c=executing;
        if(striking&&c!=null&&Allowed(c.Actor)&&ReferenceEquals(hits,c.Actor.hitTile)&&update)
        {
            c.Damage=total;c.PeakDamage=Math.Max(c.PeakDamage,total);
            if(damage>0&&total<100)NetMessage.SendData(MessageID.SyncTilePicking,-1,-1,null,c.Actor.whoAmI,c.X,c.Y,Math.Min(damage,255));
        }
        return total;
    }
    private static void Kill(On_WorldGen.orig_KillTile orig,int x,int y,bool fail,bool effectOnly,bool noItem)
    {
        var c=Current;bool watched=Running&&c.X==x&&c.Y==y;
        bool own=watched&&ReferenceEquals(c,executing)&&striking&&Allowed(c.Actor)&&!c.ExternalChange&&Same(c)&&c.Damage>=100&&!fail&&!effectOnly;
        // Also detects removal + replacement with the same type between updates.
        if(watched&&!own&&!fail&&!effectOnly)c.ExternalChange=true;
        orig(x,y,fail,effectOnly,noItem);
        if(own)
        {
            c.Destroyed=!Main.tile[x,y].HasTile;
            if(!c.Destroyed&&!Same(c))c.ExternalChange=true;
            NetMessage.SendTileSquare(-1,x,y,1);
        }
    }
    private static bool Place(On_WorldGen.orig_PlaceTile orig,int x,int y,int type,bool mute,bool forced,int plr,int style)
    {
        if(Running&&Current.X==x&&Current.Y==y)Current.ExternalChange=true;
        return orig(x,y,type,mute,forced,plr,style);
    }
    private static void Grab(On_Player.orig_GrabItems orig,Player p,int whoAmI)
    {
        if(NativeSurvival.Owns(p)&&(Running||suppressPickupTick==Main.GameUpdateCount))return;
        orig(p,whoAmI);
    }
    internal static void Update(Player p)
    {
        ProbeSession.Reservation.AssertOwner(Program.IsMainThread);
        var c=Current;if(!Running||!ReferenceEquals(c.Actor,p))return;
        if(!Bound(c))
        {
            c.Phase=ActionPhase.Failed;c.Reason=Failure.BindingChanged;c.Detail="World/incarnation/ownership changed; no Player writes.";Emit(c);
            if(c.World==ProbeSession.WorldSession&&c.Generation==ProbeSession.ActorGeneration)ProbeSession.Quarantine(c.Detail);
            return;
        }
        suppressPickupTick=Main.GameUpdateCount;
        try
        {
            if(!Healthy(p)){End(c,ActionPhase.Cancelled,Failure.ActorUnavailable);return;}
            if(!Held(c)){End(c,ActionPhase.Cancelled,Failure.ItemChanged);return;}
            if(c.ExternalChange||(c.Destroyed?Main.tile[c.X,c.Y].HasTile:!Same(c))){End(c,ActionPhase.Failed,Failure.TargetChanged);return;}
            if(!c.Destroyed&&!InRange(c)){End(c,ActionPhase.Failed,Failure.OutOfRange);return;}
            if(++c.Ticks>600){End(c,ActionPhase.TimedOut,Failure.Deadline);return;}
            executing=c;p.controlUseItem=!c.Destroyed;
            // Re-press at the end of each native animation, including non-autoReuse picks.
            if(p.itemAnimation==0)p.releaseUseItem=true;
            int before=p.itemAnimation;int strikes=c.Strikes;
            p.ItemCheck();
            if(!Bound(c)||!NativeSurvival.Owns(p))throw new InvalidOperationException("Mining binding changed inside native ItemCheck.");
            if(c.Phase!=ActionPhase.Running)return;
            c.AnimationPeak=Math.Max(c.AnimationPeak,p.itemAnimation);if(p.itemAnimation>before)c.Swings++;
            if(!Held(c)){End(c,ActionPhase.Cancelled,Failure.ItemChanged);return;}
            if(c.ExternalChange){End(c,ActionPhase.Failed,Failure.TargetChanged);return;}
            if(c.Destroyed)
            {
                if(Main.tile[c.X,c.Y].HasTile){End(c,ActionPhase.Failed,Failure.TargetChanged);return;}
                if(p.itemAnimation==0&&p.itemTime==0&&p.reuseDelay==0){End(c,ActionPhase.Completed,Failure.None);return;}
            }
            else if(!Same(c)){End(c,ActionPhase.Failed,Failure.TargetChanged);return;}
            if(c.Strikes>strikes&&c.Damage==0){End(c,ActionPhase.Failed,Failure.NoPickPower,"Native pick damage was zero or protection refused mining.");return;}
            if(c.Ticks==1&&p.itemAnimation==0){End(c,ActionPhase.Failed,Failure.NativeRefused,"Native CanUseItem/PreItemCheck did not start use.");return;}
            if(c.Ticks==1||c.Ticks%6==0)Sync(p);
        }
        catch(Exception e){End(c,ActionPhase.Failed,Failure.NativeException,(e.InnerException??e).Message);throw;}
        finally
        {
            executing=null;striking=false;
            // Mining is a held native intent until destruction/cancellation.
            // Keep the same input for periodic and full snapshots this tick.
            if(Bound(c))p.controlUseItem=c.Phase==ActionPhase.Running&&(!c.Destroyed||c.Ticks==1&&c.AnimationPeak>0);
        }
    }
    private static void Sync(Player p)
    {
        NetworkStateSync.Require(p);NetMessage.SendData(MessageID.PlayerControls,-1,-1,null,p.whoAmI);
        NetMessage.SendData(MessageID.ShotAnimationAndSound,-1,-1,null,p.whoAmI);
    }
    private static void End(Context c,ActionPhase phase,Failure reason,string detail="")
    {
        c.Phase=phase;c.Reason=reason;c.Detail=detail;
        if(Bound(c))
        {
            var p=c.Actor;p.controlUseItem=false;p.releaseUseItem=true;p.channel=false;
            if(phase!=ActionPhase.Completed)p.itemAnimation=p.itemTime=p.reuseDelay=p.toolTime=0;
            p.hitTile.ClearThisTile(c.X,c.Y);Sync(p);
        }
        Emit(c);
    }
    private static void Emit(Context c)
    {try{ProbeSession.Log("mine-result",new{actionId=c.Id,world=c.World,generation=c.Generation,slot=c.Actor.whoAmI,phase=c.Phase.ToString(),reason=c.Reason.ToString(),c.Detail,c.X,c.Y,c.TileType,c.Ticks,c.Swings,c.Strikes,c.PeakDamage,c.Destroyed});}catch{}}
    internal static void Retire(Player p)
    {
        ProbeSession.Reservation.AssertOwner(Program.IsMainThread);
        if(Running&&ReferenceEquals(Current.Actor,p))End(Current,ActionPhase.Cancelled,Failure.DeathOrRetirement);
    }
    internal static void Stop(Player p,string world,long generation)
    {
        ProbeSession.Reservation.AssertOwner(Program.IsMainThread);
        lock(ProbeSession.Reservation.Gate){ActorItemUse.Capture(p,world,generation);if(Running)End(Current,ActionPhase.Cancelled,Failure.ExplicitStop);}
    }
    internal static string Summary()
    {
        var c=Current;if(c==null)return "AI 尚无挖掘动作。";
        string tile=c.World==ProbeSession.WorldSession&&WorldGen.InWorld(c.X,c.Y)?$"{Main.tile[c.X,c.Y].HasTile}/{Main.tile[c.X,c.Y].TileType}":"unavailable";
        return $"mine {c.Id}: {c.Phase}/{c.Reason}; ({c.X},{c.Y}) type={c.TileType}, tile(active/type)={tile}; swings={c.Swings}, strikes={c.Strikes}, peakDamage={c.PeakDamage}/100, destroyed={c.Destroyed}, ticks={c.Ticks}. {c.Detail}";
    }
}
