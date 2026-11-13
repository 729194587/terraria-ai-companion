using System;
using System.Linq;
using MonoMod.Cil;
using Terraria;
using Terraria.ID;
using CompanionBridge.ServerIpc;

namespace VirtualPlayerEngineProbe;

internal static class ActorItemUse
{
    internal sealed class Context
    {
        internal Player Actor;internal Item Item;internal string World;internal long Generation;
        internal int Slot,Type,InitialStack,Ticks,AnimationPeak,TimePeak,ToolPeak,UsesPeak,Starts;
        internal string Phase="running",Reason="native_item_pending";internal bool Consumed;
        internal ActorPlacement.Context Placement;
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
        if(Running||ActorMining.Running||ActorControl.Running||p.itemAnimation!=0||p.itemTime!=0||p.reuseDelay!=0||p.channel)
            throw new InvalidOperationException("Wait for walking/item animation/cooldown to finish, or /vph use-stop.");
    }
    // The server action carries one press, with no world-space cursor.
    internal static bool Supported(Item item)=>!item.IsAir&&!item.channel
        &&item.shoot<=0&&item.useAmmo<=0&&item.createTile<0&&item.createWall<0;
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
    internal static void StartPlacement(Player p,string world,long generation,int x,int y,bool objectPlacement=false,ActorRope.Target rope=null)
    {lock(ProbeSession.Reservation.Gate){StartCore(p,world,generation,x,y,objectPlacement,rope);}}
    private static void StartCore(Player p,string world,long generation,int? x=null,int? y=null,bool objectPlacement=false,ActorRope.Target rope=null)
    {
        Capture(p,world,generation);Idle(p);
        var placement=x.HasValue?ActorPlacement.Create(p,x.Value,y.Value,objectPlacement,rope):null;
        if(p.selectedItem<0||p.selectedItem>9||(placement==null?!Supported(p.HeldItem):!ActorPlacement.SupportedFor(placement,p.HeldItem))||p.HeldItem.IsAir)
            throw new InvalidOperationException("Select an item in hotbar 1–10 that can use a single press without a world target.");
        if(p.CCed||p.noItems||p.frozen||p.stoned||p.mount.Active)throw new InvalidOperationException("AI cannot use an item in this state.");
        Current=new Context{Actor=p,World=world,Generation=generation,Slot=p.selectedItem,Item=p.HeldItem,Type=p.HeldItem.type,InitialStack=p.HeldItem.stack,Placement=placement};
        // Release an old input; the next natural ItemCheck supplies one press.
        p.controlUseItem=false;p.releaseUseItem=true;
    }
    public static bool Allowed(Player p)=>NativeCombatWorld.AllowsItem(p)||ActorMining.Allowed(p)||(executing!=null&&ReferenceEquals(executing.Actor,p)&&NativeSurvival.Owns(p)
        &&Binding(executing)&&!p.dead&&p.selectedItem==executing.Slot&&ReferenceEquals(p.inventory[executing.Slot],executing.Item)&&(executing.Placement!=null?ActorPlacement.SupportedFor(executing.Placement,executing.Item)||executing.Placement.Placed&&executing.Item.IsAir:(executing.Item.type==executing.Type||executing.Consumed&&executing.Item.IsAir)));
    internal static bool Placing(Player p)=>executing?.Placement!=null&&Allowed(p);
    public static bool SkipMelee(Player p)=>ActorMining.Allowed(p)||ActorPlacement.Allowed(p);
    public static bool SkipCursor(Player p){if(ActorMining.Allowed(p))ActorMining.Strike(p);else if(ActorPlacement.Allowed(p))ActorPlacement.Dispatch(p);return Allowed(p);}
    internal static void Install(Action<Action,Action> install)
    {
        var predicate=typeof(ActorItemUse).GetMethod(nameof(Allowed));
        var combat=typeof(NativeCombatWorld).GetMethod(nameof(NativeCombatWorld.AllowsItem));
        ILContext.Manipulator inner=il=>ItemUsePatches.Patch(il.Method,"inner",il.Module.ImportReference(predicate));
        ILContext.Manipulator melee=il=>ItemUsePatches.Patch(il.Method,"melee",il.Module.ImportReference(predicate),
            il.Module.ImportReference(typeof(ActorItemUse).GetMethod(nameof(SkipCursor))),
            il.Module.ImportReference(typeof(ActorItemUse).GetMethod(nameof(SkipMelee))),il.Module.ImportReference(combat));
        ILContext.Manipulator combatPose=il=>ItemUsePatches.Patch(il.Method,"combat-pose",
            il.Module.ImportReference(combat));
        install(()=>IL_Player.ItemCheck_Inner+=inner,()=>IL_Player.ItemCheck_Inner-=inner);
        install(()=>IL_Player.ItemCheck_OwnerOnlyCode+=melee,()=>IL_Player.ItemCheck_OwnerOnlyCode-=melee);
        install(()=>IL_Player.ItemCheck_ApplyUseStyle_Inner+=combatPose,()=>IL_Player.ItemCheck_ApplyUseStyle_Inner-=combatPose);
        ILContext.Manipulator combatStrike=il=>ItemUsePatches.Patch(il.Method,"combat-strike",
            il.Module.ImportReference(typeof(NativeCombatWorld).GetMethod(nameof(NativeCombatWorld.StrikeWithEvidence))));
        install(()=>IL_Player.ProcessHitAgainstNPC+=combatStrike,()=>IL_Player.ProcessHitAgainstNPC-=combatStrike);
        ILContext.Manipulator combatCooldown=il=>ItemUsePatches.Patch(il.Method,"combat-cooldown",il.Module.ImportReference(combat));
        install(()=>IL_Player.UpdateMeleeHitCooldowns+=combatCooldown,()=>IL_Player.UpdateMeleeHitCooldowns-=combatCooldown);
        install(()=>On_Player.ItemCheck_ApplyUseStyle_Inner+=ApplyUseStyle,()=>On_Player.ItemCheck_ApplyUseStyle_Inner-=ApplyUseStyle);
        install(()=>On_Player.ItemCheckWrapped+=Update,()=>On_Player.ItemCheckWrapped-=Update);
        install(()=>On_Player.ItemCheck_Shoot+=Shoot,()=>On_Player.ItemCheck_Shoot-=Shoot);
        install(()=>On_Player.SmartSelectLookup+=SmartSelect,()=>On_Player.SmartSelectLookup-=SmartSelect);
    }
    private static void ApplyUseStyle(On_Player.orig_ItemCheck_ApplyUseStyle_Inner orig,Player p,float height,Item item,Microsoft.Xna.Framework.Rectangle frame)
        =>orig(p,height,item,NativeCombatWorld.SwingFrame(p,item,frame));
    private static void Shoot(On_Player.orig_ItemCheck_Shoot orig,Player p,int slot,Item item,int damage)
    {
        // A Mod hook can change shoot/ammo requirements after preflight.
        // Stop before the native PickAmmo path for an actor without a cursor.
        if(!NativeSurvival.Owns(p))orig(p,slot,item,damage);
    }
    private static void SmartSelect(On_Player.orig_SmartSelectLookup orig,Player p)
    {if(!NativeSurvival.Owns(p))orig(p);}
    internal static void Update(On_Player.orig_ItemCheckWrapped orig,Player p,int index)
    {
        if(!ReferenceEquals(p,ProbeSession.Actor)){orig(p,index);return;}
        // Never run the UI wrapper for an AI, even when idle or quarantined.
        if(!NativeSurvival.Owns(p))return;
        if(NativeCombatWorld.UpdateItem(p))return;
        if(ActorMining.Running){ActorMining.Update(p);return;}
        var c=Current;
        if(!Running||!ReferenceEquals(c.Actor,p))return;
        if(!Binding(c))
        {
            c.Phase="stale";c.Reason="actor_binding_changed";
            if(c.World==ProbeSession.WorldSession&&c.Generation==ProbeSession.ActorGeneration)ProbeSession.Quarantine("G6 ownership changed; no item/input writes.");
            return;
        }
        if(c.Placement!=null&&ActorPlacement.Validate(c) is {} invalid)
        {Retire(p);if(c.Phase=="unknown")return;c.Phase=(c.Placement.Placed||c.Placement.ConsumedCount!=0||c.Placement.UncertainNativeEffect)?"unknown":"failed";c.Reason=invalid;return;}
        bool nativeEmpty=c.Consumed&&c.Item.IsAir;
        if(p.dead||p.ghost||p.CCed||p.noItems){Retire(p);return;}
        if(p.selectedItem!=c.Slot||!ReferenceEquals(p.inventory[c.Slot],c.Item)||c.Item.type!=c.Type&&!nativeEmpty)
        {Retire(p);if(c.Placement!=null&&c.Phase=="unknown")return;c.Phase="cancelled";c.Reason="selected_item_changed";return;}
        if(++c.Ticks>(c.Placement!=null?600:180)){Retire(p);if(c.Placement!=null&&c.Phase=="unknown")return;c.Phase="timeout";c.Reason=c.Placement!=null?"native_placement_deadline":"native_item_timeout";return;}
        try
        {
            executing=c;p.controlUseItem=c.Ticks==1;
            int before=p.itemAnimation,beforeStack=c.Item.stack;
            p.ItemCheck(); // Exactly once at the native Update call site, with normal tML hooks.
            if(c.Placement!=null)ActorPlacement.AfterCheck(c,beforeStack);
            if(!Binding(c)||!NativeSurvival.Owns(p)||p.selectedItem!=c.Slot)throw new InvalidOperationException("AI binding changed during ItemCheck.");
            if(c.Phase!="running"||p.dead)return;
            if(c.Placement==null&&c.Item.stack<beforeStack)c.Consumed=true;
            if(!ReferenceEquals(p.inventory[c.Slot],c.Item)&&!(c.Consumed&&c.Item.stack<=0&&p.inventory[c.Slot].IsAir&&p.itemAnimation==0&&p.itemTime==0))
                throw new InvalidOperationException("Item object changed during ItemCheck.");
            c.AnimationPeak=Math.Max(c.AnimationPeak,p.itemAnimation);c.TimePeak=Math.Max(c.TimePeak,p.itemTime);
            c.ToolPeak=Math.Max(c.ToolPeak,p.toolTime);c.UsesPeak=Math.Max(c.UsesPeak,p.ItemUsesThisAnimation);
            if(p.itemAnimation>before)c.Starts++;
            if(c.Ticks==1&&c.AnimationPeak>0||c.Ticks>1&&(p.itemAnimation==0||c.Ticks%6==0))Sync(p);
            if(c.Ticks==1&&c.AnimationPeak==0){c.Phase="rejected";c.Reason=c.Placement?.Rope!=null?"native_rope_placement_refused":"native_use_refused";}
            else if(p.itemAnimation==0&&p.itemTime==0&&p.reuseDelay==0&&!p.channel)
            {if(c.Placement!=null)ActorPlacement.Complete(c);else {c.Phase="completed";c.Reason="native_item_cycle_verified";}}
            if(c.Phase!="running")
            {
                p.controlUseItem=false;p.releaseUseItem=true;Sync(p);NetworkStateSync.Full(p);
                Emit(new {c.Phase,c.Reason,c.Type,c.Ticks,c.Starts,c.AnimationPeak,c.TimePeak,c.ToolPeak,c.UsesPeak,c.InitialStack,stack=p.inventory[c.Slot].stack});
            }
        }
        catch
        {
            if(c.Placement!=null)
            {c.Placement.UncertainNativeEffect=true;c.Phase="unknown";c.Reason="native_placement_effect_unconfirmed";}
            Retire(p);
            if(c.Placement==null&&c.Phase!="stale"){c.Phase="unknown";c.Reason="native_item_effect_unconfirmed";}
            throw;
        }
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
        var c=Current;if(c==null||!ReferenceEquals(c.Actor,p)
            ||c.Phase!="running"&&!(c.Placement!=null&&c.Phase=="unknown"))return;
        if(!Binding(c)){c.Phase="stale";c.Reason="actor_binding_changed";return;} // Unknown object/slot never receives a write.
        if(c.Placement!=null)
        {
            // Assessment failure must never prevent release of owned inputs or
            // turn an uncertain native effect into an ordinary cancellation.
            string uncertainty;
            try{uncertainty=ActorPlacement.CancellationUncertainty(c);}
            catch{c.Placement.UncertainNativeEffect=true;uncertainty="object_cancellation_check_unconfirmed";}
            if(c.Phase!="unknown")
            {c.Phase=uncertainty==null?"cancelled":"unknown";c.Reason=uncertainty??"actor_retired";}
        }
        else {c.Phase="cancelled";c.Reason="actor_retired";}
        p.controlUseItem=false;p.releaseUseItem=true;p.channel=false;
        // Cancellation only. Completed actions drain these counters in native code.
        p.itemAnimation=p.itemTime=p.reuseDelay=p.toolTime=0;
    }
    internal static void Stop(Player p,string world,long generation)
    {lock(ProbeSession.Reservation.Gate){StopCore(p,world,generation);}}
    private static void StopCore(Player p,string world,long generation)
    {
        if(Current?.Placement!=null&&ReferenceEquals(Current.Actor,p))
        {
            ProbeSession.Reservation.AssertOwner(Program.IsMainThread);
            if(world!=Current.World||generation!=Current.Generation||!Binding(Current))throw new InvalidOperationException("Object cancellation binding changed.");
            NetworkStateSync.Require(p,true); // An owned dead/disabled actor still needs input release.
            if(Current.Phase=="completed")
            {
                string uncertainty;
                try{uncertainty=ActorPlacement.CancellationUncertainty(Current);}
                catch{uncertainty="object_cancellation_check_unconfirmed";}
                if(uncertainty!=null){Current.Phase="unknown";Current.Reason=uncertainty;}
            }
            Retire(p);
            if(Current.Phase=="cancelled")Current.Reason="explicit_stop";
            try{if(!p.dead&&!p.ghost){Sync(p);NetworkStateSync.Full(p);}}
            catch
            {
                Current.Placement.UncertainNativeEffect=true;Current.Phase="unknown";Current.Reason="object_cleanup_unconfirmed";
                Retire(p);throw;
            }
            return;
        }
        Capture(p,world,generation);bool running=Running;Retire(p);if(running)Current.Reason="explicit_stop";Sync(p);NetworkStateSync.Full(p);
    }
    internal static string Summary()
    {
        var p=ProbeSession.Actor;if(p==null)return "AI 尚未载入。";
        var c=Current;
        string phase=c?.Phase switch {"running"=>"进行中","completed"=>"已完成","rejected"=>"原生规则拒绝","cancelled"=>"已停止","stale"=>"已失效","failed"=>"失败",_=>"空闲"};
        return $"快捷栏 {p.selectedItem+1}：{(p.HeldItem.IsAir?"空栏":p.HeldItem.Name)}×{p.HeldItem.stack}；使用 {phase}，动画峰值 {c?.AnimationPeak??0}，时间峰值 {c?.TimePeak??0}，当前 {p.itemAnimation}/{p.itemTime}。";
    }
}
