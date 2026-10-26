using System;
using MonoMod.Cil;
using Terraria;
using Terraria.ID;

namespace VirtualPlayerEngineProbe;

// A living native player normally delegates ownership handoff to its client.
// AI has no such client: run the SAME native selection and send branches instead.
internal static class NativeItemOwnership
{
    [ThreadStatic] private static Player previousActor;
    [ThreadStatic] private static Item previousItem;
    [ThreadStatic] private static int itemIndex;
    [ThreadStatic] private static string world;
    [ThreadStatic] private static long generation;
    private static bool Current(Player p,string session,long incarnation)=>p!=null&&Program.IsMainThread
        &&ProbeSession.Reservation.OwnerThread==Environment.CurrentManagedThreadId
        &&ReferenceEquals(ProbeSession.Actor,p)&&session!="none"&&session==ProbeSession.WorldSession
        &&incarnation>0&&incarnation==ProbeSession.ActorGeneration&&VirtualPlayerEngineProbe.IsNetworkActor(p.whoAmI);
    public static bool ShouldReconsider(int slot)
    {
        var p=ProbeSession.Actor;
        if(!Main.dedServ||Main.netMode!=NetmodeID.Server||p==null||slot!=p.whoAmI)return false;
        lock(ProbeSession.Reservation.Gate)return Current(p,ProbeSession.WorldSession,ProbeSession.ActorGeneration);
    }
    public static bool OwnsPrevious(int slot)=>previousActor!=null&&slot==previousActor.whoAmI
        &&Main.item!=null&&itemIndex>=0&&itemIndex<Main.item.Length&&ReferenceEquals(Main.item[itemIndex],previousItem)
        &&Current(previousActor,world,generation);
    internal static void Install(Action<Action,Action> install)
    {
        ILContext.Manipulator server=il=>ItemOwnershipPatches.Patch(il.Method,true,il.Module.ImportReference(typeof(NativeItemOwnership).GetMethod(nameof(ShouldReconsider))));
        ILContext.Manipulator owner=il=>ItemOwnershipPatches.Patch(il.Method,false,il.Module.ImportReference(typeof(NativeItemOwnership).GetMethod(nameof(OwnsPrevious))));
        install(()=>IL_Main.UpdateServer+=server,()=>IL_Main.UpdateServer-=server);
        install(()=>IL_Item.FindOwner+=owner,()=>IL_Item.FindOwner-=owner);
        install(()=>On_Item.FindOwner+=FindOwner,()=>On_Item.FindOwner-=FindOwner);
    }
    private static void FindOwner(On_Item.orig_FindOwner orig,Item item,int index)
    {
        if(!ShouldReconsider(item.playerIndexTheItemIsReservedFor)){orig(item,index);return;}
        lock(ProbeSession.Reservation.Gate)
        {
            var p=ProbeSession.Actor;string session=ProbeSession.WorldSession;long incarnation=ProbeSession.ActorGeneration;
            if(!Current(p,session,incarnation)||item.playerIndexTheItemIsReservedFor!=p.whoAmI){orig(item,index);return;}
            if(previousActor!=null){ProbeSession.Quarantine("Nested AI item-owner selection; no extra handoff.");return;}
            if(index<0||index>=Main.item.Length||!ReferenceEquals(Main.item[index],item))
            {ProbeSession.Quarantine("AI item-owner reference changed; no extra handoff.");return;}
            previousActor=p;previousItem=item;itemIndex=index;world=session;generation=incarnation;
            try
            {
                orig(item,index);
                if(!Current(p,session,incarnation)||!ReferenceEquals(Main.item[index],item))
                    throw new InvalidOperationException("AI/item identity changed during native ownership selection.");
            }
            catch(Exception e)
            {
                ProbeSession.Quarantine("Native item-owner handoff failed; no rollback/retry: "+e.Message);
                try{VirtualPlayerEngineProbe.Instance?.Logger.Error("AI item-owner handoff stopped",e);}catch{}
            }
            finally{previousActor=null;previousItem=null;itemIndex=-1;world=null;generation=0;}
        }
    }
}
