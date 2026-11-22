using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using Mono.Cecil;
using MonoMod.Cil;
using MonoMod.Utils;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ObjectData;
using CompanionBridge.ServerIpc;

namespace VirtualPlayerEngineProbe;

// G7 executor only. The existing mine_tile/Lua/IPC lifecycle will own adaptation.
internal static partial class ActorMining
{
    internal const int MaxUpdates=600;
    internal enum Failure { None, InvalidTarget, UnsupportedTarget, OutOfRange, InvalidTool, Busy,
        ActorUnavailable, BindingChanged, ItemChanged, TargetChanged, NativeRefused, TargetProtected, NoPickPower, NativeException, ExplicitStop, DeathOrRetirement, Deadline,
        BatchNeedsReplan }
    internal sealed record FurnitureCell(int X,int Y,short FrameX,short FrameY);
    internal sealed class Context
    {
        internal readonly Guid Id=Guid.NewGuid();
        internal Player Actor;internal Item Item;internal string World;internal long Generation;
        internal int Slot,Type,Prefix,Pick,Axe, X,Y,TileType,ExpectedType,FrameX,FrameY,Ticks,Swings,Strikes,Damage,PeakDamage,AnimationPeak,ProposedDamage,Transforms;
        internal byte Slope;internal bool Half,Actuated,Destroyed,ExternalChange,TargetCaptured;
        internal object InvalidTarget;
        internal int OriginalSlot, SourceSlot, LastStrikeTick;
        internal Item Displaced;
        internal bool SelectionChanged,KillingTarget;
        internal FurnitureCell[] Furniture=Array.Empty<FurnitureCell>();
        internal HammerWork Hammer;
        internal Batch Batch;
        internal ActionPhase Phase=ActionPhase.Running;internal Failure Reason;internal string Detail="";
    }
    internal sealed class Batch(ExcavationCell[] cells,Func<ExcavationCell,bool> safe,float estimate)
    {
        internal readonly ExcavationCell[] Cells=cells;
        internal readonly Func<ExcavationCell,bool> Safe=safe;
        internal readonly float Estimate=estimate;
        internal readonly List<ExcavationProof> Verified=new();
        internal int Index,Capability,PreviousStrikes;
        internal bool Draining;
        internal ExcavationCell Target=>Cells[Index];
    }
    private static Func<Player,int,int,int,int,Tile,int> pickDamage;
    private static Func<Player,Item,bool> inRange;
    [ThreadStatic] private static Context rangeContext;
    internal static Context Current;
    private static Context executing;
    private static bool usingTools,striking,picking;
    internal static bool Running=>Current?.Phase==ActionPhase.Running;
    internal static void Reset(){Current=executing=rangeContext=null;usingTools=striking=picking=false;}
    internal static void Install(Action<Action,Action> install)
    {
        using(var a=AssemblyDefinition.ReadAssembly(typeof(Player).Assembly.Location))
            MiningPatches.Validate(a.MainModule.Types.Single(t=>t.FullName=="Terraria.Player"));
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
        pickDamage=typeof(Player).GetMethod("GetPickaxeDamage",flags).CreateDelegate<Func<Player,int,int,int,int,Tile,int>>();
        using(var dmd=new DynamicMethodDefinition(typeof(Player).GetMethod("IsTargetTileInItemRange",flags)))
        {
            MiningPatches.RangeClone(dmd.Definition,dmd.Definition.Module.ImportReference(typeof(ActorMining).GetMethod(nameof(RangeX))),
                dmd.Definition.Module.ImportReference(typeof(ActorMining).GetMethod(nameof(RangeY))));
            inRange=(Func<Player,Item,bool>)dmd.Generate().CreateDelegate(typeof(Func<Player,Item,bool>));
        }
        ILContext.Manipulator ui=il=>ItemUsePatches.Patch(il.Method,"mining-ui",
            il.Module.ImportReference(typeof(ActorMining).GetMethod(nameof(InInputScope))));
        install(()=>IL_Player.ItemCheck_UseMiningTools+=ui,()=>IL_Player.ItemCheck_UseMiningTools-=ui);
        install(()=>On_Player.ItemCheck_UseMiningTools+=UseMiningTools,()=>On_Player.ItemCheck_UseMiningTools-=UseMiningTools);
        install(()=>On_Player.ItemCheck_UseMiningTools_ActuallyUseMiningTool+=ObserveTool,()=>On_Player.ItemCheck_UseMiningTools_ActuallyUseMiningTool-=ObserveTool);
        if (VirtualPlayerEngineProbe.Engine?.Selected == "2026.09.3.1") InstallHammer(install);
        install(()=>On_HitTile.AddDamage+=Damage,()=>On_HitTile.AddDamage-=Damage);
        install(()=>On_Player.PickTile+=Pick,()=>On_Player.PickTile-=Pick);
        install(()=>On_Player.DoesPickTargetTransformOnKill+=TransformPrediction,()=>On_Player.DoesPickTargetTransformOnKill-=TransformPrediction);
        install(()=>On_WorldGen.KillTile+=Kill,()=>On_WorldGen.KillTile-=Kill);
        install(()=>On_WorldGen.PlaceTile+=Place,()=>On_WorldGen.PlaceTile-=Place);
        // Passive GrabItems remains on NativeSurvival's ordinary Player.Update path.
        // Inventory gains never substitute for this action's target-destruction evidence.
    }
    private static bool Bound(Context c)=>c!=null&&ReferenceEquals(c.Actor,ProbeSession.Actor)
        &&c.World==ProbeSession.WorldSession&&c.Generation==ProbeSession.ActorGeneration
        &&Main.dedServ&&Main.netMode==NetmodeID.Server&&ProbeSession.Reservation.Snapshot is {} v
        &&c.Actor.whoAmI==v.Slot&&Main.player!=null&&v.Slot>=0&&v.Slot<Main.player.Length&&ReferenceEquals(Main.player[v.Slot],c.Actor)
        &&Netplay.Clients!=null&&v.Slot<Netplay.Clients.Length
        &&VirtualPlayerEngineProbe.ConnectionIdle(Netplay.Clients[v.Slot]);
    private static bool Held(Context c)=>c.Actor.selectedItem==c.Slot&&ReferenceEquals(c.Actor.inventory[c.Slot],c.Item)
        &&!c.Item.IsAir&&c.Item.type==c.Type&&c.Item.prefix==c.Prefix&&c.Item.pick==c.Pick&&c.Item.axe==c.Axe&&(c.Hammer != null ? PureHammer(c.Item) && c.Item.hammer == c.Hammer.Power : SupportedTool(c.Item));
    internal static bool HoldsBorrowedTool(Player player,string world,long generation,int source,Item item)
    {
        var c=Current;
        return Running && Bound(c) && ReferenceEquals(c.Actor,player) && c.World==world && c.Generation==generation
            && c.SourceSlot==source && c.Displaced!=null && ReferenceEquals(c.Item,item) && Held(c)
            && ReferenceEquals(player.inventory[source],c.Displaced);
    }
    private static Context RangeTarget()=>Program.IsMainThread&&Bound(rangeContext)?rangeContext:throw new InvalidOperationException("No bound G7 reach scope.");
    public static int RangeX()=>RangeTarget().X;
    public static int RangeY()=>RangeTarget().Y;
    private static bool InRange(Context c)
    {
        if(rangeContext!=null)throw new InvalidOperationException("Reentrant G7 reach check.");
        try{rangeContext=c;return inRange(c.Actor,c.Item);}finally{rangeContext=null;}
    }
    private static readonly MethodInfo shallowCopy=typeof(object).GetMethod("MemberwiseClone",BindingFlags.Instance|BindingFlags.NonPublic);
    internal static bool QueryReach(Player actor,string world,long generation,Item item,int x,int y,float px,float py)
    {
        ActorItemUse.Capture(actor,world,generation);
        if(!float.IsFinite(px)||!float.IsFinite(py))return false;
        // Detached, shallow field projection ONLY for the audited read-only native range
        // method. Never installed in Main.player; never update/move the real Player.
        var projection=(Player)shallowCopy.Invoke(actor,null);projection.position=new(px,py);
        var c=new Context{Actor=actor,World=world,Generation=generation,Item=item,X=x,Y=y};
        if(rangeContext!=null)throw new InvalidOperationException("Reentrant mining reach query");
        try
        {
            rangeContext=c;
            return inRange(projection,item);
        }
        finally{rangeContext=null;}
    }
    private static bool SupportedTool(Item i)=>!i.IsAir&&i.type!=ItemID.GravediggerShovel&&i.hammer==0&&!i.channel&&i.shoot<=0
        &&(i.pick>0||i.axe>0);
    internal static bool SupportedPick(Item item)=>item.pick>0&&SupportedTool(item);
    internal static bool TreeTarget(int type)=>Main.tileAxe[type]&&TileID.Sets.IsATreeTrunk[type];
    internal static Failure ChooseTool(Player p,string world,long generation,int x,int y,bool requireReach,
        out int slot,out float updates,int? plannedSlot=null,Vector2? workPosition=null)
    {
        slot=-1;
        updates=float.PositiveInfinity;
        var tile=Main.tile[x,y];
        int type=tile.TileType;
        bool tree=TreeTarget(type);
        if(tile.IsActuated||type==TileID.MysticSnakeRope||Main.tileHammer[type]||Main.tileAxe[type]&&!tree||TileID.Sets.Falling[type])
            return Failure.UnsupportedTarget;
        if(!WorldGen.CanKillTile(x,y))return Failure.TargetProtected;

        bool hasTool=false, hasPower=false;
        var position=workPosition??p.position;
        // Main inventory, including hotbar. Coin, ammo and mouse slots are separate.
        for(int i=0;i<Math.Min(50,p.inventory.Length);i++)
        {
            if(plannedSlot.HasValue&&i!=plannedSlot.Value)continue;
            Item item=p.inventory[i];
            if(!SupportedTool(item)||(tree?item.axe<=0:item.pick<=0))continue;
            hasTool=true;
            float cost=EstimateUpdates(p,i,x,y);
            if(!float.IsFinite(cost))continue;
            hasPower=true;
            if(requireReach&&!QueryReach(p,world,generation,item,x,y,position.X,position.Y))continue;
            if(cost<updates||cost==updates&&i==p.selectedItem)
            {
                slot=i;
                updates=cost;
            }
        }
        return slot>=0?Failure.None:!hasTool?Failure.InvalidTool:!hasPower?Failure.NoPickPower:Failure.OutOfRange;
    }
    internal static float EstimateUpdates(Player player,int slot,int x,int y)
        =>Timing(player,player.inventory[slot]).CompletionUpdates(RequiredStrikes(player,slot,x,y));

    internal static int RequiredStrikes(Player player,int slot,int x,int y)
    {
        var item=player.inventory[slot];var tile=Main.tile[x,y];
        bool tree=TreeTarget(tile.TileType);
        int damage;
        if(tree)
        {
            // Tree targets use the native axe branch, including combined tools.
            damage=(int)(item.axe*(tile.TileType==TileID.Cactus?3:1)*1.2f);
            if(Main.getGoodWorld)damage=(int)(damage*1.3);
        }
        else
        {
            // The pinned GetPickaxeDamage body does not use its HitTile buffer index.
            damage=pickDamage(player,x,y,item.pick,0,tile);
            if(Main.getGoodWorld)damage*=2;
        }
        if(damage<=0)return 0;
        int strikes=(int)Math.Ceiling(100d/damage);
        if(tile.TileType==TileID.Grass)
        {
            // The first hit strips grass. PickTile suppresses the hit only
            // when it would reach 100; smaller hits carry into the exposed dirt.
            int progress=damage<100?damage:0;
            int dirtDamage=item.pick*(Main.getGoodWorld?4:2);
            strikes=1+(int)Math.Ceiling((100d-progress)/dirtDamage);
        }
        return strikes;
    }

    internal static bool CanQuotePick(Player p,int slot,int x,int y,out int strikes)
    {
        strikes=0;
        var item=p.inventory[slot];
        var tile=Main.tile[x,y];
        int type=tile.TileType;
        if(!SupportedPick(item)||!tile.HasUnactuatedTile||type==TileID.MysticSnakeRope
            ||Main.tileHammer[type]||Main.tileAxe[type]||TileID.Sets.Falling[type]||!WorldGen.CanKillTile(x,y))return false;
        strikes=RequiredStrikes(p,slot,x,y);
        return strikes>0;
    }

    internal static MiningTiming Timing(Player player,Item item)=>new(
        CombinedHooks.TotalUseTime(item.useTime*(item.pick>0?player.pickSpeed:1f),player,item),
        CombinedHooks.TotalAnimationTime(item.useAnimation,player,item),
        Math.Max(0,(int)(item.reuseDelay/CombinedHooks.TotalUseSpeedMultiplier(player,item))),
        player.CanAutoReuseItem(item));

    internal readonly record struct MiningTiming(int Cooldown,int Animation,int ReuseDelay,bool AutoReuse)
    {
        internal float CompletionUpdates(int strikes)
        {
            if(strikes<=0)return float.PositiveInfinity;
            // The first press can strike on its own update. Native auto-reuse
            // rearms on the zero-animation update; our manual re-press follows it.
            int rearm=AutoReuse?0:1;
            int hit=1, animationStart=1;
            for(int strike=1;strike<strikes;strike++)
            {
                hit+=Cooldown;
                if(hit<animationStart+Animation)continue;
                if(ReuseDelay>0)
                {
                    // ApplyReuseDelay replaces both timers at the animation boundary.
                    hit=animationStart+=Animation+ReuseDelay+rearm;
                    continue;
                }
                int cycle=Animation+rearm;
                animationStart+=(hit-animationStart)/cycle*cycle;
                if(hit==animationStart+Animation)hit=animationStart+=cycle;
            }
            int animationLeft=animationStart+Animation-hit;
            // The atomic action completes only after the native timers drain.
            return hit+(ReuseDelay>0?animationLeft+ReuseDelay:Math.Max(animationLeft,Cooldown));
        }
    }

    internal static int CapabilityStamp(Player player)
    {
        var stamp=new HashCode();
        stamp.Add(player.pickSpeed);
        stamp.Add(player.selectedItem);
        stamp.Add(Healthy(player));
        stamp.Add(Main.getGoodWorld);
        for(int slot=0;slot<Math.Min(50,player.inventory.Length);slot++)
        {
            var item=player.inventory[slot];
            if(!SupportedTool(item))continue;
            stamp.Add(slot);
            stamp.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(item));
            stamp.Add(item.type);
            stamp.Add(item.prefix);
            stamp.Add(item.pick);
            stamp.Add(item.axe);
            stamp.Add(item.tileBoost);
            stamp.Add(Timing(player,item));
        }
        return stamp.ToHashCode();
    }
    private static bool Same(Context c)
    {
        var t=Main.tile[c.X,c.Y];return t.HasTile&&t.TileType==c.ExpectedType
            &&(byte)t.Slope==c.Slope&&t.IsHalfBlock==c.Half&&t.IsActuated==c.Actuated
            &&(!TreeTarget(c.TileType)&&!Main.tileFrameImportant[c.TileType]||t.TileFrameX==c.FrameX&&t.TileFrameY==c.FrameY);
    }
    internal static FurnitureCell[] FurnitureAt(int x,int y)
    {
        var tile=Main.tile[x,y];
        int type=tile.TileType;
        if(!Main.tileFrameImportant[type])return Array.Empty<FurnitureCell>();
        var data=TileObjectData.GetTileData(tile);
        int frameX=tile.TileFrameX,frameY=tile.TileFrameY;
        if(data==null||data.Width*data.Height<=1||frameX<0||frameY<0)return Array.Empty<FurnitureCell>();
        int stride=data.CoordinateWidth+data.CoordinatePadding;
        int localX=frameX%data.CoordinateFullWidth,localY=frameY%data.CoordinateFullHeight;
        int left=x-localX/stride, top=y;
        int rowOffset=0;
        for(int row=0;row<data.Height&&rowOffset<localY;row++,top--)
            rowOffset+=data.CoordinateHeights[row]+data.CoordinatePadding;
        if(rowOffset!=localY||localX%stride!=0||localX/stride>=data.Width
            ||!WorldGen.InWorld(left,top,2)||!WorldGen.InWorld(left+data.Width-1,top+data.Height-1,2))
            return Array.Empty<FurnitureCell>();
        int frameTop=frameY-localY, frameLeft=frameX-localX;
        var cells=new FurnitureCell[data.Width*data.Height];
        for(int row=0,dy=0;row<data.Height;row++)
        {
            for(int col=0;col<data.Width;col++)
            {
                var cell=new FurnitureCell(left+col,top+row,(short)(frameLeft+col*stride),(short)(frameTop+dy));
                if(!SameFurnitureCell(type,cell))return Array.Empty<FurnitureCell>();
                cells[row*data.Width+col]=cell;
            }
            dy+=data.CoordinateHeights[row]+data.CoordinatePadding;
        }
        return cells;
    }
    internal static bool SameFurnitureCell(int type,FurnitureCell cell)
    {
        var tile=Main.tile[cell.X,cell.Y];
        return tile.HasTile&&tile.TileType==type&&tile.TileFrameX==cell.FrameX&&tile.TileFrameY==cell.FrameY;
    }
    internal static bool FurnitureAbsent(Context c)=>c.Furniture.All(cell=>!Main.tile[cell.X,cell.Y].HasTile);
    internal static bool TargetUnchanged(Context c)=>c.TargetCaptured&&WorldGen.InWorld(c.X,c.Y,2)&&Same(c)
        &&c.Furniture.All(cell=>SameFurnitureCell(c.TileType,cell));
    internal static bool Healthy(Player p)=>!p.dead&&!p.ghost&&!p.CCed&&!p.noItems&&!p.noBuilding&&!p.frozen&&!p.stoned&&!p.mount.Active
        &&!Main.gameMenu&&!Netplay.Disconnect&&float.IsFinite(p.position.X)&&float.IsFinite(p.position.Y);
    internal static object TargetDiagnostic(int x,int y)
    {
        bool inWorld=x>=0&&y>=0&&x<Main.maxTilesX&&y<Main.maxTilesY;
        bool? hasTile=null;int? tileType=null;
        if(inWorld){var target=Main.tile[x,y];hasTile=target.HasTile;tileType=target.TileType;}
        return new{x,y,inWorld,hasTile,tileType,withinMiningBounds=WorldGen.InWorld(x,y,2)};
    }
    internal static void Start(Player p,string world,long generation,int x,int y,int? plannedSlot=null,Batch batch=null)
    {
        ProbeSession.Reservation.AssertOwner(Program.IsMainThread);
        lock(ProbeSession.Reservation.Gate)
        {
            ActorItemUse.Capture(p,world,generation);
            if(Running||ActorItemUse.Running||ActorControl.Running
                ||p.itemAnimation!=0||p.itemTime!=0||p.reuseDelay!=0||p.channel)
                throw new InvalidOperationException("Busy: stop walking/item use/mining before starting another action.");
            var c=new Context{Actor=p,World=world,Generation=generation,X=x,Y=y,Slot=p.selectedItem,Item=p.HeldItem,Batch=batch};Current=c;
            void Reject(Failure reason,string detail){c.Phase=ActionPhase.Failed;c.Reason=reason;c.Detail=detail;Emit(c);}
            if(!Healthy(p)){Reject(Failure.ActorUnavailable,"Actor cannot mine in its current state.");return;}
            bool withinMiningBounds=WorldGen.InWorld(x,y,2);
            if(!withinMiningBounds||!Main.tile[x,y].HasTile)
            {
                // Capture preflight facts once. Never index Tilemap outside physical bounds.
                c.InvalidTarget=TargetDiagnostic(x,y);
                Reject(Failure.InvalidTarget,System.Text.Json.JsonSerializer.Serialize(c.InvalidTarget));return;
            }
            CaptureTarget(c,x,y);
            var failure=ChooseTool(p,world,generation,x,y,true,out int source,out _,plannedSlot);
            if(failure!=Failure.None){Reject(failure,$"Mining preflight refused target ({x},{y}): {failure}.");return;}
            c.Furniture=FurnitureAt(x,y);
            c.Item=p.inventory[source];c.Type=c.Item.type;c.Prefix=c.Item.prefix;c.Pick=c.Item.pick;c.Axe=c.Item.axe;
            c.OriginalSlot=p.selectedItem;c.SourceSlot=source;
            c.Slot=source<10?source:p.selectedItem is >=0 and <10?p.selectedItem:0;
            if(source!=c.Slot)
            {
                c.Displaced=p.inventory[c.Slot];
                Utils.Swap(ref p.inventory[c.Slot],ref p.inventory[source]);
            }
            c.SelectionChanged=source!=c.Slot||c.Slot!=c.OriginalSlot;
            p.selectedItem=c.Slot;
            if(c.SelectionChanged)NetworkStateSync.Full(p);
            // New explicit intent never inherits another action's partial progress.
            ClearProgress(p,x,y);
            p.controlUseItem=false;p.releaseUseItem=true;
            if(batch!=null)batch.Capability=CapabilityStamp(p);
        }
    }

    private static void CaptureTarget(Context c,int x,int y)
    {
        var tile=Main.tile[x,y];
        c.X=x;c.Y=y;c.TileType=c.ExpectedType=tile.TileType;c.FrameX=tile.TileFrameX;c.FrameY=tile.TileFrameY;
        c.Slope=(byte)tile.Slope;c.Half=tile.IsHalfBlock;c.Actuated=tile.IsActuated;c.TargetCaptured=true;
        c.Destroyed=c.ExternalChange=false;c.Damage=c.ProposedDamage=c.PeakDamage=c.Transforms=0;
    }

    internal static Context StartBatch(Player p,string world,long generation,Batch batch,int slot)
    {
        ActorItemUse.Capture(p,world,generation);
        var first=batch.Target;
        if(!first.Matches()||!batch.Safe(first))return null;
        Start(p,world,generation,first.X,first.Y,slot,batch);
        return Current;
    }
    public static bool Allowed(Player p)=>executing!=null&&ReferenceEquals(executing.Actor,p)&&Bound(executing)
        &&NativeSurvival.Owns(p)&&!p.dead&&Held(executing);
    public static bool InInputScope(Player p)=>executing!=null&&ReferenceEquals(executing.Actor,p)&&NativeItemInput.Active(p);
    private static void UseMiningTools(On_Player.orig_ItemCheck_UseMiningTools orig,Player p,Item item)
    {
        if(!InInputScope(p)){orig(p,item);return;}
        if(usingTools)throw new InvalidOperationException("Reentrant native mining tools update.");
        if(!Allowed(p)||!ReferenceEquals(item,executing.Item))return;
        try{usingTools=true;orig(p,item);}finally{usingTools=false;}
    }
    private static void ObserveTool(On_Player.orig_ItemCheck_UseMiningTools_ActuallyUseMiningTool orig,
        Player p,Item item,out bool hitWall,int x,int y)
    {
        if(!InInputScope(p)){orig(p,item,out hitWall,x,y);return;}
        var c=executing;
        if(!usingTools||striking||c.LastStrikeTick==c.Ticks||!ReferenceEquals(item,c.Item)||x!=c.X||y!=c.Y)
            throw new InvalidOperationException("Native mining tool call does not match the current target frame.");
        if(c.Hammer != null)
        {
            hitWall = false;
            if (!HammerReady(c)) return;
            c.LastStrikeTick = c.Ticks;
            c.Strikes++;
            try { striking=true;orig(p,item,out hitWall,x,y); }
            finally { striking=false; }
            if (!c.Hammer.Wall)
            {
                hitWall = false;
                ObserveHammerEffect(c);
            }
            return;
        }
        if(!Allowed(p)||!Healthy(p)||c.Destroyed||c.ExternalChange||!Same(c)){hitWall=false;return;}
        c.LastStrikeTick=c.Ticks;
        c.Strikes++;c.Damage=c.ProposedDamage=0;
        try{striking=true;orig(p,item,out hitWall,x,y);}
        finally{striking=false;}
        // Only an observed native Grass -> Dirt transition may retain target identity.
        if(!c.Destroyed&&!Same(c))c.ExternalChange=true;
    }
    private static void ClearProgress(Player p,int x,int y,int layer=1)
    {int id=p.hitTile.TryFinding(x,y,layer);if(id>=0)p.hitTile.Clear(id);}
    private static void Pick(On_Player.orig_PickTile orig,Player p,int x,int y,int power)
    {
        var c=executing;
        if(!InInputScope(p)){orig(p,x,y,power);return;}
        if(!striking||picking||!Allowed(p)||x!=c.X||y!=c.Y||power!=c.Pick)
            throw new InvalidOperationException("Native AI PickTile does not match the current tool strike.");
        try{picking=true;orig(p,x,y,power);}finally{picking=false;}
    }
    private static bool TransformPrediction(On_Player.orig_DoesPickTargetTransformOnKill orig,Player p,HitTile hits,int damage,int x,int y,int power,int buffer,Tile tile)
    {
        bool result=orig(p,hits,damage,x,y,power,buffer,tile);var c=executing;
        if(picking&&c!=null&&Allowed(p)&&ReferenceEquals(hits,p.hitTile)&&x==c.X&&y==c.Y&&power==c.Pick)
            c.ProposedDamage=damage; // Native damage AFTER CanKillTile, before transform suppression.
        return result;
    }
    private static int Damage(On_HitTile.orig_AddDamage orig,HitTile hits,int id,int damage,bool update)
    {
        int total=orig(hits,id,damage,update);var c=executing;
        if(c!=null&&c.Hammer==null&&(picking||striking&&TreeTarget(c.TileType))&&Allowed(c.Actor)&&ReferenceEquals(hits,c.Actor.hitTile)&&update)
        {
            // Axe damage is computed in ActuallyUseMiningTool AFTER CanKillTile;
            // unlike picks it never passes through PickTile/transform prediction.
            if(!picking)c.ProposedDamage=damage;
            c.Damage=total;c.PeakDamage=Math.Max(c.PeakDamage,total);
            if(damage>0&&total<100)NetMessage.SendData(MessageID.SyncTilePicking,-1,-1,null,c.Actor.whoAmI,c.X,c.Y,Math.Min(damage,255));
        }
        return total;
    }
    private static void Kill(On_WorldGen.orig_KillTile orig,int x,int y,bool fail,bool effectOnly,bool noItem)
    {
        if(!fail&&!effectOnly)CompanionBridge.ServerIpc.SceneMineTargets.Changed(x,y);
        var c=Current;bool watched=Running&&!c.KillingTarget&&c.X==x&&c.Y==y;
        bool nativePick=watched&&ReferenceEquals(c,executing)&&(picking||striking&&TreeTarget(c.TileType))&&Allowed(c.Actor)&&!c.ExternalChange&&Same(c)&&c.ProposedDamage>0;
        bool own=nativePick&&c.Damage>=100&&!fail&&!effectOnly;
        bool grassHit=nativePick&&c.ExpectedType==TileID.Grass&&fail&&!effectOnly&&!noItem;
        // Also detects removal + replacement with the same type between updates.
        if(watched&&!own&&!fail&&!effectOnly)c.ExternalChange=true;
        if(own)c.KillingTarget=true;
        try{orig(x,y,fail,effectOnly,noItem);}
        finally{if(own)c.KillingTarget=false;}
        var tile=watched?Main.tile[x,y]:default;
        if(grassHit&&!c.ExternalChange&&tile.HasTile&&tile.TileType==TileID.Dirt
            &&(byte)tile.Slope==c.Slope&&tile.IsHalfBlock==c.Half&&tile.IsActuated==c.Actuated)
        {
            c.ExpectedType=TileID.Dirt;c.Transforms++;
            NetMessage.SendTileSquare(-1,x,y,1); // Publish the real grass stripping, not completion.
        }
        else if(watched&&!own&&!Same(c))c.ExternalChange=true;
        if(own)
        {
            c.Destroyed=!Main.tile[x,y].HasTile;
            if(!c.Destroyed&&!Same(c))c.ExternalChange=true;
            NetMessage.SendTileSquare(-1,x,y,1);
        }
    }
    private static bool Place(On_WorldGen.orig_PlaceTile orig,int x,int y,int type,bool mute,bool forced,int plr,int style)
    {
        CompanionBridge.ServerIpc.SceneMineTargets.Changed(x,y);
        if(Running&&Current.X==x&&Current.Y==y)Current.ExternalChange=true;
        return orig(x,y,type,mute,forced,plr,style);
    }
    internal static void Update(Player p,Action<NativeItemInput.Intent> runNative)
    {
        ProbeSession.Reservation.AssertOwner(Program.IsMainThread);
        var c=Current;if(!Running||!ReferenceEquals(c.Actor,p))return;
        if(!Bound(c))
        {
            c.Phase=ActionPhase.Failed;c.Reason=Failure.BindingChanged;c.Detail="World/incarnation/ownership changed; no Player writes.";Emit(c);
            if(c.World==ProbeSession.WorldSession&&c.Generation==ProbeSession.ActorGeneration)ProbeSession.Quarantine(c.Detail);
            return;
        }
        try
        {
            if(!Healthy(p)){End(c,ActionPhase.Cancelled,Failure.ActorUnavailable);return;}
            if(!Held(c)){End(c,ActionPhase.Cancelled,Failure.ItemChanged);return;}
            if (c.Hammer != null) { UpdateHammer(c,runNative); return; }
            var batch=c.Batch;
            if(batch==null)
            {
                if(c.ExternalChange||(c.Destroyed?Main.tile[c.X,c.Y].HasTile:!TargetUnchanged(c))){End(c,ActionPhase.Failed,Failure.TargetChanged);return;}
                if(!c.Destroyed&&!InRange(c)){End(c,ActionPhase.Failed,Failure.OutOfRange);return;}
            }
            else if(!batch.Draining&&!c.Destroyed&&(c.ExternalChange||!TargetUnchanged(c)||batch.Cells.Skip(batch.Index).Any(cell=>!cell.Matches())
                ||batch.Verified.Any(proof=>Main.tile[proof.X,proof.Y].HasTile)
                ||CapabilityStamp(p)!=batch.Capability||!batch.Safe(batch.Target)||!InRange(c)))
                batch.Draining=true;
            if(++c.Ticks>MaxUpdates){End(c,ActionPhase.TimedOut,Failure.Deadline);return;}
            executing=c;bool press=!c.Destroyed&&batch?.Draining!=true;
            // Re-press at the end of each native animation, including non-autoReuse picks.
            if(p.itemAnimation==0)p.releaseUseItem=true;
            int before=p.itemAnimation;int strikes=c.Strikes;
            runNative(NativeItemInput.Intent.AtTile(c.X,c.Y,press));
            if(!Bound(c)||!NativeSurvival.Owns(p))throw new InvalidOperationException("Mining binding changed inside native ItemCheck.");
            if(c.Phase!=ActionPhase.Running)return;
            c.AnimationPeak=Math.Max(c.AnimationPeak,p.itemAnimation);if(p.itemAnimation>before)c.Swings++;
            if(!Held(c)){End(c,ActionPhase.Cancelled,Failure.ItemChanged);return;}
            if(batch!=null)
            {
                if(c.Destroyed&&!c.ExternalChange&&!Main.tile[c.X,c.Y].HasTile&&batch.Verified.Count==batch.Index)
                {
                    batch.Verified.Add(new(c.X,c.Y,c.TileType,c.Ticks,c.Strikes-batch.PreviousStrikes));
                    batch.PreviousStrikes=c.Strikes;
                    ClearProgress(p,c.X,c.Y);
                    AiMapRuntime.Remember(c.X,c.Y);
                    if(batch.Index+1<batch.Cells.Length)
                    {
                        var next=batch.Cells[++batch.Index];
                        if(next.Matches())
                        {
                            CaptureTarget(c,next.X,next.Y);
                            ClearProgress(p,c.X,c.Y);
                        }
                        else batch.Draining=true;
                    }
                }
                bool complete=batch.Verified.Count==batch.Cells.Length;
                if(!complete&&c.ExternalChange)batch.Draining=true;
                if((complete||batch.Draining)&&p.itemAnimation==0&&p.itemTime==0&&p.reuseDelay==0)
                {
                    End(c,complete?ActionPhase.Completed:ActionPhase.Failed,complete?Failure.None:Failure.BatchNeedsReplan);
                    return;
                }
                if(!complete&&!batch.Draining&&c.Strikes>strikes&&c.ProposedDamage==0&&batch.PreviousStrikes!=c.Strikes)
                {End(c,ActionPhase.Failed,Failure.NoPickPower);return;}
            }
            else if(c.ExternalChange){End(c,ActionPhase.Failed,Failure.TargetChanged);return;}
            else if(c.Destroyed)
            {
                if(Main.tile[c.X,c.Y].HasTile){End(c,ActionPhase.Failed,Failure.TargetChanged);return;}
                if(p.itemAnimation==0&&p.itemTime==0&&p.reuseDelay==0){End(c,FurnitureAbsent(c)?ActionPhase.Completed:ActionPhase.Stale,Failure.None);return;}
            }
            else if(!Same(c)){End(c,ActionPhase.Failed,Failure.TargetChanged);return;}
            if(batch==null&&c.Strikes>strikes&&c.ProposedDamage==0){End(c,ActionPhase.Failed,WorldGen.CanKillTile(c.X,c.Y)?Failure.NoPickPower:Failure.TargetProtected,$"原生工具规则拒绝：类型 {c.ExpectedType}，镐力 {c.Pick}，斧力 {c.Axe}，保护判定后的伤害为零；目标尚未破坏。");return;}
            if(c.Ticks==1&&p.itemAnimation==0&&batch?.Draining!=true){End(c,ActionPhase.Failed,Failure.NativeRefused,"Native CanUseItem/PreItemCheck did not start use.");return;}
            if(c.Ticks==1||c.Ticks%6==0)Sync(p);
        }
        catch(Exception e){End(c,c.Hammer==null?ActionPhase.Failed:ActionPhase.Stale,Failure.NativeException,(e.InnerException??e).Message);throw;}
        finally
        {
            executing=null;usingTools=striking=picking=false;
            // Mining is a held native intent until destruction/cancellation.
            // Keep the same input for periodic and full snapshots this tick.
            if(Bound(c))p.controlUseItem=c.Phase==ActionPhase.Running&&c.Batch?.Draining!=true&&(c.Hammer != null ? !c.Hammer.Effected : !c.Destroyed||c.Ticks==1&&c.AnimationPeak>0);
        }
    }
    private static void Sync(Player p)
    {
        NetworkStateSync.Require(p);NetMessage.SendData(MessageID.PlayerControls,-1,-1,null,p.whoAmI);
        NetMessage.SendData(MessageID.ShotAnimationAndSound,-1,-1,null,p.whoAmI);
    }
    private static bool CanRestoreSwap(Context c,Player p)=>c.Displaced!=null
        && ReferenceEquals(p.inventory[c.Slot],c.Item) && ReferenceEquals(p.inventory[c.SourceSlot],c.Displaced);

    // Combat admission may inspect the exact hotbar item this mining run will restore.
    // Execution must still verify the physical slot after ownership transfers.
    internal static Item AfterRelease(Player p,string world,long generation,int slot)
    {
        var c=Current;
        return Running && ReferenceEquals(c.Actor,p) && c.World==world && c.Generation==generation
            && c.Slot==slot && CanRestoreSwap(c,p)?c.Displaced:p.inventory[slot];
    }

    private static void End(Context c,ActionPhase phase,Failure reason,string detail="")
    {
        c.Phase=phase;c.Reason=reason;c.Detail=detail;
        if(Bound(c))
        {
            var p=c.Actor;p.controlUseItem=false;p.releaseUseItem=true;p.channel=false;
            if(c.Hammer!=null)p.poundRelease=true;
            if(phase!=ActionPhase.Completed)p.itemAnimation=p.itemTime=p.reuseDelay=p.toolTime=0;
            if(phase==ActionPhase.Completed&&c.Batch==null)AiMapRuntime.Remember(c.X,c.Y);
            ClearProgress(p,c.X,c.Y,c.Hammer?.Wall==true?2:1);
            if(c.SelectionChanged)
            {
                // Swap back only the same two entries. Never overwrite an unrelated inventory change.
                if(CanRestoreSwap(c,p))
                    Utils.Swap(ref p.inventory[c.Slot],ref p.inventory[c.SourceSlot]);
                if(p.selectedItem==c.Slot)p.selectedItem=c.OriginalSlot;
                NetworkStateSync.Full(p);
            }
            Sync(p);
        }
        Emit(c);
    }
    private static void Emit(Context c)
    {try{int observed=c.World==ProbeSession.WorldSession&&WorldGen.InWorld(c.X,c.Y)?Main.tile[c.X,c.Y].HasTile?Main.tile[c.X,c.Y].TileType:-1:-2;
        ProbeSession.Log("mine-result",new{actionId=c.Id,world=c.World,generation=c.Generation,slot=c.Actor.whoAmI,phase=c.Phase.ToString(),reason=c.Reason.ToString(),c.Detail,c.X,c.Y,c.TileType,c.ExpectedType,observedType=observed,itemType=c.Type,pick=c.Pick,axe=c.Axe,c.ProposedDamage,c.Transforms,c.Ticks,c.Swings,c.Strikes,c.PeakDamage,c.Destroyed});}catch{}}
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
        return $"挖掘 {c.Phase}/{c.Reason}：({c.X},{c.Y}) 原类型={c.TileType}，当前(active/type)={tile}；挥动={c.Swings}，工具击={c.Strikes}，进度峰值={c.PeakDamage}/100，草皮转化={c.Transforms}，已破坏={c.Destroyed}。{c.Detail}";
    }
}
