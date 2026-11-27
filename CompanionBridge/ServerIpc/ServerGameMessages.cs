using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Chat;
using Terraria.Localization;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

internal static class ServerGameMessages
{
    private sealed record Notice(long sequence,string text,string source,ulong tick,bool truncated,long? generation);
    private static readonly Queue<Notice> recent=new();
    internal static long Sequence {get;private set;}
    internal static void Reset(){recent.Clear();Sequence=0;ServerDrops.ResetPickupNotice();}
    internal static void Install()
    {
        On_ChatHelper.BroadcastChatMessageAs+=Broadcast;
        On_ChatHelper.SendChatMessageToClientAs+=Directed;
    }
    internal static void Uninstall()
    {
        On_ChatHelper.BroadcastChatMessageAs-=Broadcast;
        On_ChatHelper.SendChatMessageToClientAs-=Directed;
    }
    [ThreadStatic] private static bool broadcasting;
    private static void Broadcast(On_ChatHelper.orig_BroadcastChatMessageAs orig,byte author,NetworkText text,Color color,int excluded)
    {
        bool outer=broadcasting;
        broadcasting=true;
        try{orig(author,text,color,excluded);}
        finally{broadcasting=outer;}
        if(!outer && author==byte.MaxValue && ProbeSession.Actor is {} actor && actor.whoAmI!=excluded)
            Remember(text,"server_broadcast");
    }
    private static void Directed(On_ChatHelper.orig_SendChatMessageToClientAs orig,byte author,NetworkText text,Color color,int target)
    {
        orig(author,text,color,target);
        if(!broadcasting && author==byte.MaxValue && ProbeSession.Actor?.whoAmI==target)
            Remember(text,"server_direct");
    }
    internal static void InventoryFull(string text) => Remember(text, "inventory_full", ProbeSession.ActorGeneration);
    private static void Remember(NetworkText message,string source)
    {
        if(!Program.IsMainThread)return;
        Remember(message.ToString(), source, null);
    }
    private static void Remember(string text,string source,long? generation)
    {
        if(string.IsNullOrWhiteSpace(text))return;
        if(recent.Count==32)recent.Dequeue();
        recent.Enqueue(new(++Sequence,text.Length<=1024?text:text[..1024],source,Main.GameUpdateCount,text.Length>1024,generation));
    }
    internal static object Since(long cursor)
    {
        if(cursor==Sequence || ProbeSession.Actor is not {} player)return null;
        return new {through=Sequence,dropped=Math.Max(0,Sequence-recent.Count-cursor),
            scene=new {life=player.statLife,maxLife=player.statLifeMax2,x=player.position.X,y=player.position.Y,
                heldItem=player.HeldItem.Name,activity=ServerAiSystem.CurrentActivity},
            items=recent.Where(e=>e.sequence>cursor && (e.generation==null || e.generation==ProbeSession.ActorGeneration))
                .Select(e=>new {e.sequence,e.text,e.source,e.tick,e.truncated}).ToArray()};
    }
}
