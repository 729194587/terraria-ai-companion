using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Terraria;
using Terraria.ModLoader;
using CompanionBridge.ServerIpc;

namespace VirtualPlayerEngineProbe;

// Actor-only inventory primitives. No local UI, mouse item, or Recipe.FindRecipes.
// Crafting rebuilds its own actor material/adjacency cache on every query/batch.
internal static class ActorInventory
{
    private sealed class Identity { internal readonly string Value=Guid.NewGuid().ToString("N"); }
    private static readonly ConditionalWeakTable<Item,Identity> identities=new();
    private sealed record Core(int Type,int Stack,int Prefix,int MaxStack,bool Favorite);
    private static Core Read(Item i)=>i.IsAir?new(0,0,0,0,false):new(i.type,i.stack,i.prefix,i.maxStack,i.favorited);
    internal sealed class Context
    {
        internal AiReadOnlyProtocol.Identity Identity;
        internal string Operation,Status="running",Reason="awaiting_inventory_mutation",Error;
        internal JsonElement Arguments;
        internal int Source,Destination,Selected;
        internal bool MutationAttempted;
        internal ulong MutationTick,ConfirmedTick;
        internal string[] AfterTokens;
        internal object Before,After;
        internal Item SourceBefore,DestinationBefore;
        internal bool SourceBaselineEstablished,DestinationBaselineEstablished;
    }
    internal static bool IsOperation(string op)=>op is "transfer_inventory_item" or "swap_inventory_slots" or "hold_inventory_item";
    internal static string Token(AiReadOnlyProtocol.Identity id,Item item,int slot)=>Token(id,item,slot.ToString());
    internal static string Token(AiReadOnlyProtocol.Identity id,Item item,string slot)
    {
        var c=Read(item);
        // No serialization or Mod hooks: custom ModItem/GlobalItem state is unknown.
        // Native CanStack/stack/split hooks remain authoritative at execution time.
        string instance=item.IsAir?"air":identities.GetValue(item,_=>new Identity()).Value;
        string binding=$"{id.World}|{id.Generation}|{id.Slot}|{identitiesForActor.GetValue(id.Actor,_=>new Identity()).Value}|{slot}|{instance}|{c}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("core_only_v2|"+binding))).ToLowerInvariant();
    }
    private static readonly ConditionalWeakTable<object,Identity> identitiesForActor=new();
    private static string[] Tokens(Context c,Player p)=>p.inventory.Select((i,s)=>Token(c.Identity,i,s)).ToArray();
    private static object Describe(Context c,Player p,int slot)
    {
        var v=Read(p.inventory[slot]);
        return new{slot,hotbarKey=CompanionBridge.HotbarSlots.KeyForInventorySlot(slot),typeId=v.Type,stack=v.Stack,prefix=v.Prefix,maxStack=v.MaxStack,favorited=v.Favorite,
            instanceId=Instance(p.inventory[slot]),inventoryToken=Token(c.Identity,p.inventory[slot],slot)};
    }
    private static object Snapshot(Context c,Player p)=>new{source=Describe(c,p,c.Source),destination=Describe(c,p,c.Destination),selectedSlot=p.selectedItem};
    private static void Finish(Context c,string status,string reason){c.Status=status;c.Reason=reason;}
    internal static Context Begin(AiReadOnlyProtocol.Identity id,string operation,JsonElement args)
    {
        ServerAiNativeActions.Require(id);
        return new(){Identity=id,Operation=operation,Arguments=args.Clone(),Source=args.GetProperty("sourceSlot").GetInt32(),
            Destination=operation=="hold_inventory_item"?CompanionBridge.HotbarSlots.ToInventorySlot(args.GetProperty("hotbarKey").GetInt32()):args.GetProperty("destinationSlot").GetInt32()};
    }
    internal static void Step(Context c,bool confirmOnlyMovedSlots=false)
    {
        if(c.Status!="running")return;
        ServerAiNativeActions.Require(c.Identity);
        var p=(Player)c.Identity.Actor;
        if(c.AfterTokens!=null)
        {
            if(Main.GameUpdateCount<=c.MutationTick)return;
            try
            {
                var confirmed=Tokens(c,p);
                // Maintenance restoration can coexist with natural pickups
                // in unrelated slots between mutation and confirmation.
                bool sameSlots=confirmOnlyMovedSlots
                    ?confirmed[c.Source]==c.AfterTokens[c.Source]&&confirmed[c.Destination]==c.AfterTokens[c.Destination]
                    :confirmed.SequenceEqual(c.AfterTokens);
                bool valid=sameSlots&&p.selectedItem==c.Selected&&ServerAiNativeActions.Owned(c.Identity);
                c.ConfirmedTick=Main.GameUpdateCount;
                Finish(c,valid?"completed":"unknown",valid?"inventory_change_verified_next_update":"inventory_changed_before_confirmation");
            }
            catch(Exception e){Unknown(c,e);}
            return;
        }
        void Fail(string reason)=>Finish(c,"failed",reason);
        if(c.Source<0||c.Source>=50||c.Destination<0||c.Destination>=50||p.inventory.Length<50){Fail("inventory_slot_out_of_range");return;}
        if(p.itemAnimation!=0||p.itemTime!=0||p.reuseDelay!=0||p.channel||p.selectedItem==58){Fail("inventory_item_busy");return;}
        if(p.inventory.Any(i=>i==null)){Fail("invalid_inventory_slot_contents");return;}
        try
        {
            string[] beforeTokens=Tokens(c,p);
            if(beforeTokens[c.Source]!=c.Arguments.GetProperty("sourceToken").GetString()||beforeTokens[c.Destination]!=c.Arguments.GetProperty("destinationToken").GetString())
            {Fail("stale_inventory_snapshot");return;}
            var source=p.inventory[c.Source];var destination=p.inventory[c.Destination];
            var s=Read(source);var d=Read(destination);
            bool transfer=c.Operation=="transfer_inventory_item",hold=c.Operation=="hold_inventory_item";
            if(c.Source!=c.Destination&&ReferenceEquals(source,destination)&&!source.IsAir){Fail("inventory_slots_alias_same_item");return;}
            if(source.IsAir&&(transfer||hold)){Fail("inventory_source_empty");return;}
            int count=transfer?c.Arguments.GetProperty("count").GetInt32():s.Stack;
            if(transfer&&(count<=0||count>s.Stack||count>s.MaxStack||s.Stack>s.MaxStack)){Fail("inventory_source_count_insufficient_or_invalid");return;}
            if(transfer&&!destination.IsAir&&(s.Type!=d.Type||s.Prefix!=d.Prefix)){Fail("inventory_destination_not_compatible");return;}
            if(transfer&&!destination.IsAir&&(count>d.MaxStack-d.Stack||d.Stack>d.MaxStack)){Fail("inventory_destination_capacity_insufficient");return;}
            c.SourceBefore=source;c.SourceBaselineEstablished=true;
            c.DestinationBefore=destination;c.DestinationBaselineEstablished=true;
            c.Before=Snapshot(c,p);int selected=p.selectedItem;
            if(transfer&&!destination.IsAir)
            {
                c.MutationAttempted=true; // Permission hooks can mutate before returning/throwing.
                bool allowed=ItemLoader.CanStack(destination,source);
                if(!Tokens(c,p).SequenceEqual(beforeTokens)||p.selectedItem!=selected||!ServerAiNativeActions.Owned(c.Identity))
                {Finish(c,"unknown","inventory_changed_during_stack_permission_hook");return;}
                if(!allowed){Fail("inventory_stacking_denied");return;}
            }
            // Recheck binding and basic slot state after permission hooks.
            if(!Tokens(c,p).SequenceEqual(beforeTokens)||p.selectedItem!=selected||!ServerAiNativeActions.Owned(c.Identity))
            {Finish(c,"unknown","inventory_changed_before_native_mutation");return;}
            c.MutationAttempted=true;
            int moved=count;
            if(transfer)
            {
                if(destination.IsAir)p.inventory[c.Destination]=ItemLoader.TransferWithLimit(source,count);
                else ItemLoader.StackItems(destination,source,out moved,numToTransfer:count);
                if(source.IsAir&&source.stack>=0)source.TurnToAir();
            }
            else if(c.Source!=c.Destination)Utils.Swap(ref p.inventory[c.Source],ref p.inventory[c.Destination]);
            if(hold){ServerAiNativeActions.Require(c.Identity);p.selectedItem=c.Destination;}
            Core WithCount(Core value,int stack,bool favorite)=>stack==0?new(0,0,0,0,false):value with{Stack=stack,Favorite=favorite};
            var expectedS=transfer?WithCount(s,s.Stack-count,d.Type==0?s.Favorite:false):c.Source==c.Destination?s:d;
            var expectedD=transfer?WithCount(d.Type==0?s:d,d.Stack+count,d.Type==0?count==s.Stack&&s.Favorite:d.Favorite||s.Favorite):s;
            string[] after=Tokens(c,p);
            bool others=after.Select((token,i)=>i==c.Source||i==c.Destination||token==beforeTokens[i]).All(v=>v);
            bool references=transfer||c.Source==c.Destination||ReferenceEquals(p.inventory[c.Source],destination)&&ReferenceEquals(p.inventory[c.Destination],source);
            if(moved!=count||Read(p.inventory[c.Source])!=expectedS||Read(p.inventory[c.Destination])!=expectedD||!others||!references
                ||p.selectedItem!=(hold?c.Destination:selected)||p.inventory.Any(i=>i.stack<0)||!ServerAiNativeActions.Owned(c.Identity))
            {Finish(c,"unknown","inventory_native_result_not_verified");return;}
            c.After=Snapshot(c,p);
            NetworkStateSync.Full(p);
            if(!Tokens(c,p).SequenceEqual(after)||p.selectedItem!=(hold?c.Destination:selected)||!ServerAiNativeActions.Owned(c.Identity))
            {Finish(c,"unknown","inventory_changed_during_sync");return;}
            c.AfterTokens=after;c.Selected=p.selectedItem;c.MutationTick=Main.GameUpdateCount;c.Reason="awaiting_inventory_confirmation";
        }
        catch(Exception e){Unknown(c,e);}
    }
    internal static string Instance(Item item)=>item==null?null:identities.GetValue(item,_=>new Identity()).Value;
    // Exception evidence must remain independent of item properties/serialization/Mod hooks.
    private static object BasicSlot(Player p,int slot,Item before,bool baselineEstablished)
    {
        if(!baselineEstablished)return new{slot,available=false,reason="baseline_not_established"};
        Item item=p.inventory!=null&&slot>=0&&slot<p.inventory.Length?p.inventory[slot]:null;
        return new{slot,available=true,present=item!=null,typeId=item?.type,stack=item?.stack,prefix=item==null?(int?)null:item.prefix,
            maxStack=item?.maxStack,favorited=item?.favorited,instanceId=Instance(item),instanceChanged=!ReferenceEquals(item,before)};
    }
    private static object ExceptionSnapshot(Context c)
    {
        if(!ServerAiNativeActions.Owned(c.Identity))return new{available=false,reason="actor_binding_changed"};
        var p=(Player)c.Identity.Actor;
        return new{available=true,kind="basic_fields_only",gameUpdate=Main.GameUpdateCount,selectedSlot=p.selectedItem,
            source=BasicSlot(p,c.Source,c.SourceBefore,c.SourceBaselineEstablished),destination=BasicSlot(p,c.Destination,c.DestinationBefore,c.DestinationBaselineEstablished),customState="unknown"};
    }
    private static void Unknown(Context c,Exception e)
    {
        // Best effort only: diagnostic failure must never replace the original unknown.
        try{c.After=ExceptionSnapshot(c);}catch{c.After=new{available=false,reason="basic_snapshot_unavailable"};}
        c.Error=(e.GetType().FullName+": "+e.Message);c.Error=c.Error[..Math.Min(c.Error.Length,768)];
        Finish(c,"unknown","inventory_native_exception");
    }
    internal static void Cancel(Context c)
    {
        if(c.Status=="running")Finish(c,c.MutationAttempted?"unknown":"cancelled",c.MutationAttempted?"inventory_cancelled_before_confirmation":"explicit_stop");
    }
    internal static object Evidence(Context c)=>new{mutationAttempted=c.MutationAttempted,before=c.Before,after=c.After,sourceSlot=c.Source,destinationSlot=c.Destination,
        selectedSlot=c.Selected,mutationGameUpdate=c.MutationTick,confirmedAtGameUpdate=c.ConfirmedTick,inventoryVerified=c.Status=="completed",
        effectStatus=c.Status=="completed"?"server_inventory_verified":c.Status=="unknown"?"unknown; no rollback or automatic retry":"not_confirmed",nativeError=c.Error};
}
