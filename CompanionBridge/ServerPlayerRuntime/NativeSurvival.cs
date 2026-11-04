using System;
using System.Linq;
using System.Reflection;
using MonoMod.Cil;
using MonoMod.Utils;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using EngineProgram = Terraria.Program;

namespace VirtualPlayerEngineProbe;

internal static class NativeSurvival
{
    [ThreadStatic] private static Player selected;
    [ThreadStatic] private static string world;
    [ThreadStatic] private static long generation;
    private static Player managed;
    private static string managedWorld;
    private static long managedGeneration;
    private static Action<Projectile> hostileDamage;
    private static readonly MethodInfo npcCollision=typeof(Player).GetMethod("Update_NPCCollision",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public);
    private static readonly MethodInfo respawnTime=typeof(Player).GetMethod("GetRespawnTime",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public);
    internal static int ExpectedRespawn(Player p,bool pvp=false)=>(int)respawnTime.Invoke(p,new object[]{pvp});
    internal static long Pickups,Hurts,Deaths,Respawns;
    private static void Emit(string name,object detail)
    {
        try { ProbeSession.Log(name,detail); } catch { /* Logging cannot interrupt a native item/health transition. */ }
    }
    internal static void Install(Action<Action,Action> install)
    {
        MethodInfo predicate=typeof(NativeSurvival).GetMethod(nameof(Owns));
        ILContext.Manipulator Patch(string operation)=>il=>SurvivalPatches.Patch(il.Method,operation,il.Module.ImportReference(predicate));
        var pickup=Patch("pickup");var lethal=Patch("lethal");var dodge=Patch("dodge");
        var drops=Patch("death-drops");var spawn=Patch("spawn");var coins=Patch("coin-mouse");
        install(()=>IL_Player.GrabItems+=pickup,()=>IL_Player.GrabItems-=pickup);
        install(()=>IL_Player.Hurt_HurtInfo_bool+=lethal,()=>IL_Player.Hurt_HurtInfo_bool-=lethal);
        install(()=>IL_Player.Hurt_PlayerDeathReason_int_int_refHurtInfo_bool_bool_int_bool_float_float_float+=dodge,
            ()=>IL_Player.Hurt_PlayerDeathReason_int_int_refHurtInfo_bool_bool_int_bool_float_float_float-=dodge);
        install(()=>IL_Player.KillMe+=drops,()=>IL_Player.KillMe-=drops);
        install(()=>IL_Player.Spawn+=spawn,()=>IL_Player.Spawn-=spawn);
        install(()=>IL_Player.DropCoins+=coins,()=>IL_Player.DropCoins-=coins);
        BuildProjectileAdapter();
        install(()=>On_Player.Hurt_HurtInfo_bool+=Hurt,()=>On_Player.Hurt_HurtInfo_bool-=Hurt);
        install(()=>On_Player.KillMe+=Kill,()=>On_Player.KillMe-=Kill);
        install(()=>On_Player.KillMeForGood+=KillForGood,()=>On_Player.KillMeForGood-=KillForGood);
        install(()=>On_Projectile.Damage+=ProjectileDamage,()=>On_Projectile.Damage-=ProjectileDamage);
    }
    private static void KillForGood(On_Player.orig_KillMeForGood orig,Player p)
    {
        if(Owns(p))throw new InvalidOperationException("AI hardcore deletion cannot touch native active player files.");
        orig(p);
    }
    internal static void Reset() { selected=managed=null;world=managedWorld=null;generation=managedGeneration=0;Pickups=Hurts=Deaths=Respawns=0;hostileDamage=null; }
    private static bool Binding(Player p,string session,long incarnation)
    {
        var view=ProbeSession.Reservation.Snapshot;
        return p!=null&&Main.dedServ&&Main.netMode==NetmodeID.Server&&session==ProbeSession.WorldSession
            &&incarnation==ProbeSession.ActorGeneration&&ReferenceEquals(p,ProbeSession.Actor)&&view!=null
            &&p.whoAmI==view.Slot&&p.active&&Main.player!=null&&view.Slot<Main.player.Length&&ReferenceEquals(Main.player[view.Slot],p)
            &&Netplay.Clients!=null&&view.Slot<Netplay.Clients.Length&&VirtualPlayerEngineProbe.ConnectionIdle(Netplay.Clients[view.Slot]);
    }
    public static bool Owns(Player p)=>selected!=null&&ReferenceEquals(p,selected)&&EngineProgram.IsMainThread
        &&ProbeSession.Reservation.OwnerThread==Environment.CurrentManagedThreadId&&Binding(p,world,generation);
    public static int CurrentSlot() => Owns(selected)?selected.whoAmI:throw new InvalidOperationException("No validated AI damage scope.");
    internal static bool ManagedDead(Player p)=>p!=null&&p.dead&&ReferenceEquals(p,managed)&&Binding(p,managedWorld,managedGeneration);
    internal static void BuildProjectileAdapter()
    {
        using var dmd=new DynamicMethodDefinition(typeof(Projectile).GetMethod("Damage",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic));
        SurvivalPatches.HostileClone(dmd.Definition,dmd.Definition.Module.ImportReference(typeof(NativeSurvival).GetMethod(nameof(CurrentSlot))));
        hostileDamage=(Action<Projectile>)dmd.Generate().CreateDelegate(typeof(Action<Projectile>));
    }
    private readonly record struct ItemState(Item Reference,int Type,int Stack,bool Active);
    private static ItemState[] Items()=>Main.item.Select(i=>new ItemState(i,i?.type??0,i?.stack??0,i?.active??false)).ToArray();
    private static void WorldChanges(ItemState[] before,Player p)
    {
        bool changed=false;
        for(int i=0;i<before.Length;i++)
        {
            var item=Main.item[i];var old=before[i];
            if(ReferenceEquals(old.Reference,item)&&old.Type==item.type&&old.Stack==item.stack&&old.Active==item.active)continue;
            NetMessage.SendData(MessageID.SyncItem,-1,-1,null,i);changed=true;
        }
        if(changed) { if(!p.dead)NetworkStateSync.Full(p);Pickups++; }
    }
    internal static void Run(Player p,Action action)
    {
        ProbeSession.Reservation.AssertOwner(EngineProgram.IsMainThread);
        lock(ProbeSession.Reservation.Gate)
        {
            if(selected!=null) { if(!Owns(p))throw new InvalidOperationException("Foreign/nested AI scope.");action();return; }
            NetworkStateSync.Require(p,true);
            if(!Binding(p,ProbeSession.WorldSession,ProbeSession.ActorGeneration)||VirtualPlayerEngineProbe.Fault!=null||ProbeSession.Quarantined)
                throw new InvalidOperationException("AI survival ownership unavailable.");
            var before=Items();var mouse=Main.mouseItem;var file=Main.ActivePlayerFileData;int local=Main.myPlayer;
            var players=(Player[])Main.player.Clone();
            selected=managed=p;world=managedWorld=ProbeSession.WorldSession;generation=managedGeneration=ProbeSession.ActorGeneration;
            try
            {
                action();
                if(!Owns(p))throw new InvalidOperationException("AI ownership changed during native survival operation.");
                WorldChanges(before,p);
            }
            catch(Exception e)
            {
                if(Owns(p)){ActorMining.Retire(p);ActorItemUse.Retire(p);ActorControl.Retire(p);}
                ProbeSession.Quarantine("Native survival failed; no rollback/retry of partial item or damage transaction: "+(e.InnerException??e).Message);
                throw;
            }
            finally
            {
                selected=null;world=null;generation=0;
                if(local!=Main.myPlayer||!ReferenceEquals(mouse,Main.mouseItem)||!ReferenceEquals(file,Main.ActivePlayerFileData)
                    ||Main.player.Length!=players.Length||players.Where((value,i)=>!ReferenceEquals(Main.player[i],value)).Any())
                { ProbeSession.Quarantine("Native survival changed global human player/file/mouse context.");throw new InvalidOperationException("Survival isolation violated."); }
            }
        }
    }
    internal static void Update(On_Player.orig_Update orig,Player p,int index)
    {
        if(!Main.dedServ||!ReferenceEquals(p,ProbeSession.Actor)||ProbeSession.Quarantined||VirtualPlayerEngineProbe.Fault!=null)
        {ActorControl.Update(orig,p,index);return;}
        try { Run(p,()=> {
            if(p.difficulty==2)throw new InvalidOperationException("Hardcore AI archives are unsupported; no human archive deletion is allowed.");
            ActorControl.Update(orig,p,index);
            if(!Owns(p))throw new InvalidOperationException("Actor replaced during native update.");
            if(!p.dead&&!p.ghost)npcCollision.Invoke(p,null); // Original local-only update region was skipped for this AI.
            if(p.dead&&p.respawnTimer<=0&&p.difficulty!=2)
            {
                ActorMining.Retire(p);ActorItemUse.Retire(p);ActorControl.Retire(p);p.FindSpawn();
                p.Spawn(PlayerSpawnContext.ReviveFromDeath);
                if(!Owns(p)||p.dead)throw new InvalidOperationException("Native respawn failed ownership/state validation.");
                ProbeSession.ActorGeneration=checked(ProbeSession.ActorGeneration+1);
                generation=managedGeneration=ProbeSession.ActorGeneration;
                Respawns++;
                NetMessage.SendData(MessageID.PlayerSpawn,-1,-1,null,p.whoAmI,(float)PlayerSpawnContext.ReviveFromDeath);
                NetworkStateSync.Full(p);
                Emit("native-respawn",new {generation,p.respawnTimer,p.statLife,p.statMana});
            }
        }); } catch(Exception e) { Emit("native-survival-stopped",(e.InnerException??e).Message); }
    }
    internal static void ProjectileDamage(On_Projectile.orig_Damage orig,Projectile projectile)
    {
        orig(projectile); // Native human/NPC/friendly projectile paths stay intact.
        var p=ProbeSession.Actor;
        if(!Main.dedServ||p==null||p.dead||!projectile.active||!projectile.hostile||projectile.damage<=0
            ||ProbeSession.Quarantined||VirtualPlayerEngineProbe.Fault!=null)return;
        try { Run(p,()=>hostileDamage(projectile)); }
        catch(Exception e) { Emit("native-projectile-stopped",(e.InnerException??e).Message); }
    }
    internal static void Hurt(On_Player.orig_Hurt_HurtInfo_bool orig,Player p,Player.HurtInfo info,bool quiet)
    {
        if(!Owns(p)){orig(p,info,quiet);return;}
        int life=p.statLife;orig(p,info,quiet);
        if(!Owns(p))throw new InvalidOperationException("Hurt callback changed AI ownership.");
        if(p.statLife!=life)
        {
            Hurts++;NetMessage.SendPlayerHurt(p.whoAmI,info,-1);
            NetMessage.SendData(MessageID.PlayerLifeMana,-1,-1,null,p.whoAmI);
            Emit("native-hurt",new {before=life,after=p.statLife,damage=info.Damage,p.immuneTime,p.respawnTimer,p.dead});
        }
    }
    internal static void Kill(On_Player.orig_KillMe orig,Player p,PlayerDeathReason source,double damage,int direction,bool pvp)
    {
        if(!Owns(p)){orig(p,source,damage,direction,pvp);return;}
        bool wasDead=p.dead;ActorMining.Retire(p);ActorItemUse.Retire(p);ActorControl.Retire(p);orig(p,source,damage,direction,pvp);
        if(!Owns(p))throw new InvalidOperationException("Kill callback changed AI ownership.");
        if(!wasDead&&p.dead)
        {
            Deaths++;NetMessage.SendPlayerDeath(p.whoAmI,source,(int)damage,direction,pvp,-1,-1);
            Emit("native-death",new {p.respawnTimer,nativeExpected=ExpectedRespawn(p,pvp),p.difficulty,generation});
        }
    }
    internal static void DeadSnapshot(Player p,int remote,int ignore)
    {
        if(ManagedDead(p))NetMessage.SendData(MessageID.PlayerSpawn,remote,ignore,null,p.whoAmI,(float)PlayerSpawnContext.SpawningIntoWorld);
    }
    internal static void Debug(Player expected,string session,long incarnation,bool kill)
    {
        if(expected==null||!ReferenceEquals(expected,ProbeSession.Actor)||session!=ProbeSession.WorldSession||incarnation!=ProbeSession.ActorGeneration)
            throw new InvalidOperationException("Actor changed since debug command was queued.");
        if(expected.dead)throw new InvalidOperationException("AI is already waiting for native respawn.");
        Run(expected,()=> {
            var reason=PlayerDeathReason.ByCustomReason(Terraria.Localization.NetworkText.FromLiteral(expected.name+" died during an explicit AI diagnostic."));
            if(kill)expected.KillMe(reason,5000,1,false);
            else expected.Hurt(reason,30,1);
        });
    }
    internal static string Summary()=>ProbeSession.Actor is not {} p?"AI 尚未载入。":
        $"AI {(p.dead?"死亡，等待复活":"存活")}；生命 {p.statLife}/{p.statLifeMax2}；复活剩余 {p.respawnTimer/60.0:F1}秒；受伤{Hurts}次，死亡{Deaths}次，复活{Respawns}次。";
    internal static string Bag()
    {
        var p=ProbeSession.Actor??throw new InvalidOperationException("Load an AI first.");
        NetworkStateSync.Require(p);
        var entries=p.inventory.Where(i=>!i.IsAir).GroupBy(i=>i.type).ToArray();
        return entries.Length==0?"AI 背包为空。":"AI 背包："+string.Join("、",entries.Take(8).Select(g=>g.First().Name+"×"+g.Sum(i=>i.stack)))
            +(entries.Length>8?$"；另有{entries.Length-8}种。":"。");
    }
}
