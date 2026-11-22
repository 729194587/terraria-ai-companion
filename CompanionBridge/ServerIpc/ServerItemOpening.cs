using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;
using VirtualPlayerEngineProbe;
using Result = CompanionBridge.ServerIpc.AiAtomicActions.NativeResult;

namespace CompanionBridge.ServerIpc;

// Server-safe branches of ItemSlot.TryOpenContainer; omit its mouse, audio and recipe UI tail.
internal static class ServerItemOpening
{
    private static readonly Action<Item,Player> RightClickHooks=typeof(ItemLoader)
        .GetMethod("RightClickCallHooks",BindingFlags.Static|BindingFlags.NonPublic,null,new[]{typeof(Item),typeof(Player)},null)
        ?.CreateDelegate<Action<Item,Player>>();
    internal static bool Available=>RightClickHooks!=null && VirtualPlayerEngineProbe.VirtualPlayerEngineProbe.Engine?.Selected=="2026.09.3.1";
    private sealed record Reward(int typeId,int stack);
    private sealed record Observation(Player Player,int Type,List<Reward> Drops);
    private static Observation observing;

    internal static void Spawn(Item item,IEntitySource source)
    {
        if(observing is {} action && source is EntitySource_ItemOpen open && ReferenceEquals(open.Player,action.Player)
            && open.ItemType==action.Type && item.type>0 && item.stack>0)
            action.Drops.Add(new(item.type,item.stack));
    }

    internal static Result Open(AiReadOnlyProtocol.Identity id,JsonElement args)
    {
        Result Fail(string reason)=>new("failed",reason,null,true,InputsAcquired:false);
        var p=(Player)id.Actor;
        int slot=args.GetProperty("sourceSlot").GetInt32();
        var item=p.inventory[slot];
        if(ActorInventory.Token(id,item,slot)!=args.GetProperty("sourceToken").GetString())return Fail("stale_inventory_token");
        if(!Available || item.IsAir || item.type>=ItemID.Count || item.ModItem!=null
            || !(ItemID.Sets.BossBag[item.type] || ItemID.Sets.IsFishingCrate[item.type]) || !ItemLoader.CanRightClick(item))
            return Fail("unsupported_right_click_item");
        if(p.noItems || p.CCed || observing!=null)return Fail("actor_busy");
        int type=item.type,before=item.stack;
        var drops=new List<Reward>();
        try
        {
            observing=new(p,type,drops);
            if(ItemID.Sets.BossBag[type])p.OpenBossBag(type);
            else p.OpenFishingCrate(type);
            RightClickHooks(item,p);
            if(!ServerAiNativeActions.Owned(id) || !ReferenceEquals(p.inventory[slot],item) || item.type!=type || item.stack!=before)
                return new("unknown","opened_source_changed",null,true,InputsAcquired:false);
            if(ItemLoader.ConsumeItem(item,p) && --item.stack==0)item.TurnToAir();
            NetMessage.SendData(MessageID.SyncEquipment,-1,-1,null,p.whoAmI,slot,item.prefix);
            int after=item.IsAir?0:item.stack;
            bool verified=before-after==1 && drops.Count>0;
            var evidence=new{sourceSlot=slot,typeId=type,beforeStack=before,afterStack=after,drops};
            return new(verified?"completed":"unknown",verified?"container_item_opened":"opening_effect_unconfirmed",evidence,true,verified,InputsAcquired:false);
        }
        catch(Exception error)
        {
            return new("unknown","opening_effect_unconfirmed",null,true,InputsAcquired:false,ReasonDetail:error.Message);
        }
        finally{observing=null;}
    }
}
