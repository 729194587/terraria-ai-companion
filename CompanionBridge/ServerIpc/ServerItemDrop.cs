using System;
using System.Linq;
using System.Text.Json;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using VirtualPlayerEngineProbe;
using Identity = CompanionBridge.ServerIpc.AiReadOnlyProtocol.Identity;
using Result = CompanionBridge.ServerIpc.AiAtomicActions.NativeResult;

namespace CompanionBridge.ServerIpc;

internal static class ServerItemDrop
{
    // The native pickup callback is conclusive only for the same session and item incarnation.
    private sealed record Handoff(Identity Actor, string Teammate, ServerDrops.Drop Drop, int Count)
    {
        internal int Received;
    }
    private static Handoff latest;
    internal static void Clear() => latest=null;
    internal static bool Available => MerchantShop.VanillaServicesSupported;

    internal static Result Drop(Identity id, JsonElement args)
    {
        Result Refuse(string reason) => new("failed",reason,null,true,InputsAcquired:false);
        ServerAiNativeActions.Require(id);
        var p=(Player)id.Actor;
        int slot=args.GetProperty("sourceSlot").GetInt32(), count=args.GetProperty("count").GetInt32();
        var source=p.inventory[slot];
        if(ActorInventory.Token(id,source,slot)!=args.GetProperty("sourceToken").GetString())return Refuse("stale_inventory_token");
        if(!Available || p.noItems || p.CCed)return Refuse("drop_unavailable");
        if(source.IsAir || source.favorited)return Refuse("empty_or_favorited_slot");
        if(count<1 || count>source.stack)return Refuse("insufficient_stack");
        // This slice covers ordinary stackable items. Coins use denomination conversion on pickup.
        if(source.maxStack<=1 || source.type>=ItemID.Count || source.ModItem!=null || source.IsACoin)
            return Refuse("unsupported_drop_item");
        var teammate=args.TryGetProperty("teammateRef",out var value)?value.GetString():null;
        var recipient=teammate==null?null:ServerRegroup.Recipient(id,teammate);
        if(teammate!=null && recipient==null)return Refuse("teammate_unavailable");

        // NewItem can recycle an active world slot when the pool is full. Only admit with free space.
        var free=Enumerable.Range(0,Main.maxItems)
            .Where(i=>!Main.item[i].active && Main.timeItemSlotCannotBeReusedFor[i]==0).ToArray();
        if(free.Length==0)return Refuse("world_item_slots_full");
        int type=source.type,prefix=source.prefix,before=source.stack;
        var remainder=source.Clone();
        var part=ItemLoader.TransferWithLimit(remainder,count);
        if(part.type!=type || part.prefix!=prefix || part.stack!=count || (remainder.IsAir?0:remainder.stack)!=before-count)
            return Refuse("split_unconfirmed");

        // Stage the debit before creating the world entity. Do not publish either half until verified.
        if(recipient!=null)
        {
            int facing=Math.Sign(recipient.Center.X-p.Center.X);
            if(facing!=0 && facing!=p.direction)
            {
                p.ChangeDir(facing);
                if(p.direction!=facing)return Refuse("cannot_face_teammate");
            }
        }
        p.inventory[slot]=remainder;
        ActorInventory.InvalidateSlot(id,slot);
        try
        {
            int index=Item.NewItem(p.GetSource_Misc("ThrowItem"),p.position,p.width,p.height,part,noBroadcast:true);
            if(Array.IndexOf(free,index)<0 || !Main.item[index].active || Main.item[index].type!=type ||
                Main.item[index].prefix!=prefix || Main.item[index].stack!=count)
                throw new InvalidOperationException("Native spawn identity or quantity differs");
            var world=Main.item[index];
            world.velocity.Y=-2;
            world.velocity.X=p.velocity.X+4*p.direction;
            world.ownIgnore=p.whoAmI;
            world.ownTime=100;
            world.FindOwner(index);
            NetMessage.SendData(MessageID.SyncItem,-1,-1,null,index);
            NetMessage.SendData(MessageID.ItemOwner,-1,-1,null,index);
            NetMessage.SendData(MessageID.SyncEquipment,-1,-1,null,p.whoAmI,slot,remainder.prefix);
            string entityRef=ServerDrops.Reference(id,index,world);
            var drop=new ServerDrops.Drop(index,world,entityRef,type,prefix);
            int after=p.inventory[slot].IsAir?0:p.inventory[slot].stack;
            if(!ServerDrops.Live(drop) || world.stack!=count || !ReferenceEquals(p.inventory[slot],remainder) || after!=before-count)
                throw new InvalidOperationException("Drop changed before confirmation");
            latest=teammate==null?null:new(id,teammate,drop,count);
            return new("completed","world_drop_verified",new{sourceSlot=slot,typeId=type,prefix,beforeStack=before,
                afterStack=after,spawnedCount=count,entityRef,teammateRef=teammate,receiptStatus="unconfirmed"},
                true,true,InputsAcquired:false);
        }
        catch(Exception error)
        {
            // In this pinned vanilla scope a synchronous throw with every reserved slot still empty
            // has no live world delivery. Restore only our untouched staged source.
            if(free.All(i=>!Main.item[i].active) && ReferenceEquals(p.inventory[slot],remainder))
                p.inventory[slot]=source;
            NetMessage.SendData(MessageID.SyncEquipment,-1,-1,null,p.whoAmI,slot,p.inventory[slot].prefix);
            return new("unknown","drop_effect_unconfirmed",null,true,InputsAcquired:false,ReasonDetail:error.GetType().Name);
        }
    }

    internal static object Status(Identity id,string teammate)
    {
        var h=latest;
        if(h==null || h.Teammate!=teammate || !ReferenceEquals(h.Actor.Actor,id.Actor) ||
            h.Actor.Generation!=id.Generation || h.Actor.World!=id.World || h.Actor.Slot!=id.Slot)return null;
        return new{entityRef=h.Drop.Reference,typeId=h.Drop.Type,spawnedCount=h.Count,confirmedReceived=h.Received,
            stillOnGround=ServerDrops.Live(h.Drop),receiptStatus=h.Received==h.Count?"received":h.Received>0?"partial":"unconfirmed"};
    }

    internal static Item Pickup(On_Player.orig_PickupItem orig,Player player,int playerIndex,int index,Item item)
    {
        var confirm=BeforePickup(player,index,item);
        int before = item.stack;
        var remaining=ServerDrops.Pickup(orig,player,playerIndex,index,item);
        confirm?.Invoke(remaining);
        if (NativeSurvival.Owns(player) && ReferenceEquals(Main.item[index], remaining) &&
            remaining.active && !remaining.IsAir && remaining.stack <= before)
            ServerDrops.ReportRemainder(player, remaining, before - remaining.stack);
        return remaining;
    }

    private static Action<Item> BeforePickup(Player player,int index,Item item)
    {
        var h=latest;
        if(h==null || h.Received==h.Count || index!=h.Drop.Slot || !ReferenceEquals(item,h.Drop.Item) ||
            !ServerDrops.Live(h.Drop) || item.stack!=h.Count-h.Received || !ServerAiNativeActions.Owned(h.Actor) ||
            !ReferenceEquals(ServerRegroup.Recipient(h.Actor,h.Teammate),player))return null;
        int stack=item.stack;
        long InInventory()=>player.inventory.Concat(player.bank4.item)
            .Where(i=>!i.IsAir && i.type==h.Drop.Type && i.prefix==h.Drop.Prefix).Sum(i=>(long)i.stack);
        long before=InInventory();
        return remainder=>{
            int left=remainder.IsAir?0:remainder.stack;
            int removed=stack-left;
            if(ReferenceEquals(latest,h) && removed>0 && removed<=stack && InInventory()-before==removed &&
                (left==0 || remainder.type==h.Drop.Type && remainder.prefix==h.Drop.Prefix))
                h.Received+=removed;
        };
    }
}
