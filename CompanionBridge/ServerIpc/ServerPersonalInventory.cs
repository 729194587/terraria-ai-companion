using System.Linq;
using System.Text.Json;
using Terraria;
using Terraria.ID;
using VirtualPlayerEngineProbe;
using Identity=CompanionBridge.ServerIpc.AiReadOnlyProtocol.Identity;
using Result=CompanionBridge.ServerIpc.AiAtomicActions.NativeResult;

namespace CompanionBridge.ServerIpc;

internal static class ServerPersonalInventory
{
    internal static object ObserveTrash(Identity id)
    {
        var item=((Player)id.Actor).trashItem;
        return new {typeId=item.type,stack=item.IsAir?0:item.stack,prefix=(int)item.prefix,
            trashToken=ActorInventory.Token(id,item,"trash")};
    }

    internal static Result Trash(Identity id,JsonElement args)
    {
        ServerAiNativeActions.Require(id);
        var p=(Player)id.Actor;
        int slot=args.GetProperty("slot").GetInt32();
        var bag=p.inventory[slot];
        var old=p.trashItem;
        bool take=args.GetProperty("mode").GetString()=="retrieve";
        Result Fail(string reason)=>new("failed",reason,null,true,InputsAcquired:false);
        if(p.HasLockedInventory()||p.CCed||p.noItems)return Fail("inventory_locked");
        if(ActorInventory.Token(id,bag,slot)!=args.GetProperty("slotToken").GetString() ||
            ActorInventory.Token(id,old,"trash")!=args.GetProperty("trashToken").GetString())return Fail("stale_inventory_token");
        if(take ? old.IsAir || !bag.IsAir : bag.IsAir || bag.favorited)return Fail("invalid_trash_transfer");
        var source=take?old:bag;
        var before=ActorInventory.Read(source);
        // SellOrTrash's trash branch clones the whole source and clears it, replacing prior trash.
        if(take){p.inventory[slot]=old;p.trashItem=new Item();}
        else {p.trashItem=bag.Clone();bag.TurnToAir();}
        ActorInventory.InvalidateSlot(id,slot);
        bool verified=ActorInventory.Read(take?p.inventory[slot]:p.trashItem)==before &&
            (take?p.trashItem:p.inventory[slot]).IsAir;
        NetMessage.SendData(MessageID.SyncEquipment,-1,-1,null,p.whoAmI,slot);
        return new(verified?"completed":"unknown",verified?"trash_transfer_verified":"trash_transfer_unconfirmed",
            new{slot,mode=take?"retrieve":"discard",typeId=before.Type,stack=before.Stack,verified},
            true,verified,InputsAcquired:false);
    }

    private static (Item,ActorInventory.Core,int,int,int,int)[] Gear(Item[] armor,Item[] dyes) =>
        armor.Concat(dyes).Select(i=>(i,ActorInventory.Read(i),i.defense,i.headSlot,i.bodySlot,i.legSlot)).ToArray();

    internal static Result Switch(Identity id,JsonElement args)
    {
        ServerAiNativeActions.Require(id);
        var p=(Player)id.Actor;
        int before=p.CurrentLoadoutIndex, target=args.GetProperty("loadout").GetInt32()-1;
        if(p.CCed || p.noItems || p.HasLockedInventory() || before+1!=args.GetProperty("expectedLoadout").GetInt32())
            return new("failed","loadout_unavailable_or_changed",null,true,InputsAcquired:false);
        var old=p.Loadouts[before];
        var next=p.Loadouts[target];
        var activeGear=Gear(p.armor,p.dye);
        var incoming=Gear(next.Armor,next.Dye);
        var parked=Gear(old.Armor,old.Dye);
        var activeHide=p.hideVisibleAccessory.ToArray();
        var nextHide=next.Hide.ToArray();
        var oldHide=old.Hide.ToArray();
        p.TrySwitchingLoadout(target);
        bool verified=p.CurrentLoadoutIndex==target && (before==target ||
            Gear(p.armor,p.dye).SequenceEqual(incoming) && Gear(old.Armor,old.Dye).SequenceEqual(activeGear) &&
            Gear(next.Armor,next.Dye).SequenceEqual(parked) && p.hideVisibleAccessory.SequenceEqual(nextHide) &&
            old.Hide.SequenceEqual(activeHide) && next.Hide.SequenceEqual(oldHide));
        if(before!=target && verified)NetworkStateSync.Full(p);
        return new(verified?"completed":"unknown",verified?"loadout_verified":"loadout_unconfirmed",
            new{beforeLoadout=before+1,afterLoadout=p.CurrentLoadoutIndex+1,verified},true,verified,InputsAcquired:false);
    }
}
