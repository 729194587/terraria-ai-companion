using System;
using System.Linq;
using MonoMod.Cil;
using Terraria;
using Terraria.ID;

namespace VirtualPlayerEngineProbe;

internal static class ActorItemUse
{
    internal sealed class Context
    {
        internal Player Actor;internal Item Item;internal string World;internal long Generation;
        internal int Slot,Type,InitialStack,Ticks,AnimationPeak,TimePeak,ToolPeak,UsesPeak,Starts;
        internal string Phase="running",Reason="";internal bool Consumed;
    }
    internal static Context Current;
    private static Context executing;
    internal static bool Running=>Current?.Phase=="running";
    internal static void Reset(){Current=executing=null;}
    private static bool Binding(Context c)=>c!=null&&ReferenceEquals(c.Actor,ProbeSession.Actor)
        &&c.World==ProbeSession.WorldSession&&c.Generation==ProbeSession.ActorGeneration
        &&Main.dedServ&&Main.netMode==NetmodeID.Server&&ProbeSession.Reservation.Snapshot is {} view
        &&view.Slot==c.Actor.whoAmI&&Main.player!=null&&view.Slot>=0&&view.Slot<Main.player.Length&&ReferenceEquals(Main.player[view.Slot],c.Actor)
        &&Netplay.Clients!=null&&view.Slot<Netplay.Clients.Length
        &&VirtualPlayerEngineProbe.ConnectionIdle(Netplay.Clients[c.Actor.whoAmI]);
    internal static void Capture(Player p,string world,long generation)
    {
        ProbeSession.Reservation.AssertOwner(Program.IsMainThread);
        if(p==null||!ReferenceEquals(p,ProbeSession.Actor)||world!=ProbeSession.WorldSession||generation!=ProbeSession.ActorGeneration)
            throw new InvalidOperationException("AI changed since command was queued.");
        NetworkStateSync.Require(p);
        if(p.dead||p.ghost||Main.gameMenu||Netplay.Disconnect)throw new InvalidOperationException("AI must be alive in the current world.");
    }
    private static void Idle(Player p)
    {
        if(Running||ActorMining.Running||ActorControl.Current?.Phase==ActionPhase.Running||p.itemAnimation!=0||p.itemTime!=0||p.reuseDelay!=0||p.channel)
            throw new InvalidOperationException("Wait for walking/item animation/cooldown to finish, or /vph use-stop.");
    }
    internal static bool Supported(int type)=>type==ItemID.WoodenSword||type==ItemID.CopperPickaxe||type==ItemID.Apple;
    internal static void Select(Player p,string world,long generation,int slot)
    {lock(ProbeSession.Reservation.Gate){SelectCore(p,world,generation,slot);}}
    private static void SelectCore(Player p,string world,long generation,int slot)
    {
        Capture(p,world,generation);Idle(p);
        if(slot<0||slot>9)throw new InvalidOperationException("Hotbar slots are 1–10.");
        p.selectedItem=slot;NetworkStateSync.Full(p);
    }
    internal static string Fixture(Player p,string world,long generation)
    {lock(ProbeSession.Reservation.Gate){return FixtureCore(p,world,generation);}}
    private static string FixtureCore(Player p,string world,long generation)
    {
        Capture(p,world,generation);Idle(p);
        if(ProbeDebugAppearance.IsApplied(p))throw new InvalidOperationException("Restore debug-style before adding test items.");
        var slots=Enumerable.Range(0,10).Where(i=>p.inventory[i].IsAir).Take(3).ToArray();
        if(slots.Length!=3)throw new InvalidOperationException("Need three empty hotbar slots; existing items are never overwritten.");
        int[] types={ItemID.WoodenSword,ItemID.CopperPickaxe,ItemID.Apple};
        for(int i=0;i<3;i++){p.inventory[slots[i]].SetDefaults(types[i]);p.inventory[slots[i]].stack=i==2?3:1;}
        NetworkStateSync.Full(p);
        return $"测试物品已加入空栏：木剑 {slots[0]+1}、铜镐 {slots[1]+1}、苹果×3 {slots[2]+1}；用 /vph slot 数字，再 /vph use。";
    }
    internal static void Start(Player p,string world,long generation)
    {lock(ProbeSession.Reservation.Gate){StartCore(p,world,generation);}}
    private static void StartCore(Player p,string world,long generation)
    {
        Capture(p,world,generation);Idle(p);
        if(p.selectedItem<0||p.selectedItem>9||!Supported(p.HeldItem.type)||p.HeldItem.IsAir)
            throw new InvalidOperationException("G6 supports only a wooden sword, copper pickaxe or apple in hotbar 1–10.");
        if(p.CCed||p.noItems||p.frozen||p.stoned||p.mount.Active)throw new InvalidOperationException("AI cannot use an item in this state.");
        Current=new Context{Actor=p,World=world,Generation=generation,Slot=p.selectedItem,Item=p.HeldItem,Type=p.HeldItem.type,InitialStack=p.HeldItem.stack};
        // Release an old input; the next natural ItemCheck supplies one press.
        p.controlUseItem=false;p.releaseUseItem=true;
    }
    public static bool Allowed(Player p)=>ActorMining.Allowed(p)||(executing!=null&&ReferenceEquals(executing.Actor,p)&&NativeSurvival.Owns(p)
        &&Binding(executing)&&!p.dead&&p.selectedItem==executing.Slot&&ReferenceEquals(p.inventory[executing.Slot],executing.Item)&&Supported(executing.Type));
    public static bool SkipCursor(Player p){if(ActorMining.Allowed(p))ActorMining.Strike(p);return Allowed(p);}
    internal static void Install(Action<Action,Action> install)
    {
        var predicate=typeof(ActorItemUse).GetMethod(nameof(Allowed));
        ILContext.Manipulator inner=il=>ItemUsePatches.Patch(il.Method,"inner",il.Module.ImportReference(predicate));
        ILContext.Manipulator melee=il=>ItemUsePatches.Patch(il.Method,"melee",il.Module.ImportReference(predicate),
            il.Module.ImportReference(typeof(ActorItemUse).GetMethod(nameof(SkipCursor))),
            il.Module.ImportReference(typeof(ActorMining).GetMethod(nameof(ActorMining.Allowed))));
        install(()=>IL_Player.ItemCheck_Inner+=inner,()=>IL_Player.ItemCheck_Inner-=inner);
        install(()=>IL_Player.ItemCheck_OwnerOnlyCode+=melee,()=>IL_Player.ItemCheck_OwnerOnlyCode-=melee);
        install(()=>On_Player.ItemCheckWrapped+=Update,()=>On_Player.ItemCheckWrapped-=Update);
        install(()=>On_Player.SmartSelectLookup+=SmartSelect,()=>On_Player.SmartSelectLookup-=SmartSelect);
    }
    private static void SmartSelect(On_Player.orig_SmartSelectLookup orig,Player p)
    {if(!NativeSurvival.Owns(p))orig(p);}
    internal static void Update(On_Player.orig_ItemCheckWrapped orig,Player p,int index)
    {
        if(!ReferenceEquals(p,ProbeSession.Actor)){orig(p,index);return;}
        // Never run the UI wrapper for an AI, even when idle or quarantined.
        if(!NativeSurvival.Owns(p))return;
        if(ActorMining.Running){ActorMining.Update(p);return;}
        var c=Current;
        if(!Running||!ReferenceEquals(c.Actor,p))return;
        if(!Binding(c))
        {
            c.Phase="stale";c.Reason="world/generation/ownership changed; no writes";
            if(c.World==ProbeSession.WorldSession&&c.Generation==ProbeSession.ActorGeneration)ProbeSession.Quarantine("G6 ownership changed; no item/input writes.");
            return;
        }
        bool nativeEmpty=c.Consumed&&c.Item.IsAir;
        if(p.dead||p.ghost||p.CCed||p.noItems){Retire(p);return;}
        if(p.selectedItem!=c.Slot||!ReferenceEquals(p.inventory[c.Slot],c.Item)||c.Item.type!=c.Type&&!nativeEmpty)
        {Retire(p);c.Phase="cancelled";c.Reason="selected item/object changed";return;}
        if(++c.Ticks>180){Retire(p);c.Phase="failed";c.Reason="native item timeout";return;}
        try
        {
            executing=c;p.controlUseItem=c.Ticks==1;
            int before=p.itemAnimation;
            p.ItemCheck(); // Exactly once at the native Update call site, with normal tML hooks.
            if(!Binding(c)||!NativeSurvival.Owns(p)||p.selectedItem!=c.Slot)throw new InvalidOperationException("AI binding changed during ItemCheck.");
            if(c.Phase!="running"||p.dead)return;
            if(c.Type==ItemID.Apple&&c.Item.stack<c.InitialStack)c.Consumed=true;
            if(!ReferenceEquals(p.inventory[c.Slot],c.Item)&&!(c.Consumed&&c.Item.stack<=0&&p.inventory[c.Slot].IsAir&&p.itemAnimation==0&&p.itemTime==0))
                throw new InvalidOperationException("Item object changed during ItemCheck.");
            c.AnimationPeak=Math.Max(c.AnimationPeak,p.itemAnimation);c.TimePeak=Math.Max(c.TimePeak,p.itemTime);
            c.ToolPeak=Math.Max(c.ToolPeak,p.toolTime);c.UsesPeak=Math.Max(c.UsesPeak,p.ItemUsesThisAnimation);
            if(p.itemAnimation>before)c.Starts++;
            if(c.Ticks==1&&c.AnimationPeak>0||c.Ticks>1&&(p.itemAnimation==0||c.Ticks%6==0))Sync(p);
            if(c.Ticks==1&&c.AnimationPeak==0){c.Phase="rejected";c.Reason="native CanUse/start refused";}
            else if(p.itemAnimation==0&&p.itemTime==0&&p.reuseDelay==0)
            {c.Phase="completed";c.Reason="native animation/time drained";}
            if(c.Phase!="running")
            {
                p.controlUseItem=false;p.releaseUseItem=true;Sync(p);NetworkStateSync.Full(p);
                Emit(new {c.Phase,c.Reason,c.Type,c.Ticks,c.Starts,c.AnimationPeak,c.TimePeak,c.ToolPeak,c.UsesPeak,c.InitialStack,stack=p.inventory[c.Slot].stack});
            }
        }
        catch{Retire(p);throw;}
        finally
        {
            executing=null;
            // Publish the actual first press through the rest of this world tick.
            // PostUpdatePlayers/full sync must not overwrite it with a release
            // before a client has had any chance to observe the press. The next
            // natural ItemCheck supplies false; errors/stops always release now.
            if(Binding(c))p.controlUseItem=c.Phase=="running"&&c.Ticks==1&&c.AnimationPeak>0;
        }
    }
    private static void Emit(object detail)
    {try{ProbeSession.Log("item-use-result",detail);}catch{ /* Diagnostics must not interrupt native item transitions. */ }}
    private static void Sync(Player p)
    {
        NetworkStateSync.Require(p);
        NetMessage.SendData(MessageID.PlayerControls,-1,-1,null,p.whoAmI);
        NetMessage.SendData(MessageID.ShotAnimationAndSound,-1,-1,null,p.whoAmI);
    }
    internal static void Retire(Player p)
    {
        ProbeSession.Reservation.AssertOwner(Program.IsMainThread);
        var c=Current;if(c==null||!ReferenceEquals(c.Actor,p)||c.Phase!="running")return;
        if(!Binding(c)){c.Phase="stale";return;} // Unknown object/slot never receives a write.
        c.Phase="cancelled";c.Reason="explicit stop/death/retirement";
        p.controlUseItem=false;p.releaseUseItem=true;p.channel=false;
        // Cancellation only. Completed actions drain these counters in native code.
        p.itemAnimation=p.itemTime=p.reuseDelay=p.toolTime=0;
    }
    internal static void Stop(Player p,string world,long generation)
    {lock(ProbeSession.Reservation.Gate){StopCore(p,world,generation);}}
    private static void StopCore(Player p,string world,long generation)
    {Capture(p,world,generation);Retire(p);Sync(p);NetworkStateSync.Full(p);}
    internal static string Summary()
    {
        var p=ProbeSession.Actor;if(p==null)return "AI 尚未载入。";
        var c=Current;
        string phase=c?.Phase switch {"running"=>"进行中","completed"=>"已完成","rejected"=>"原生规则拒绝","cancelled"=>"已停止","stale"=>"已失效","failed"=>"失败",_=>"空闲"};
        return $"快捷栏 {p.selectedItem+1}：{(p.HeldItem.IsAir?"空栏":p.HeldItem.Name)}×{p.HeldItem.stack}；使用 {phase}，动画峰值 {c?.AnimationPeak??0}，时间峰值 {c?.TimePeak??0}，当前 {p.itemAnimation}/{p.itemTime}。";
    }
}
