using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace CompanionBridge.Common.Systems;

// Local inventory primitives, not synthesized UI clicks or arbitrary item creation.
// Transfers use the same native stack/split hooks used by ItemSlot; swaps retain instances.
internal static class AtomicInventory
{
    private sealed class Identity { internal readonly string Value = Guid.NewGuid().ToString("N"); }
    private static readonly ConditionalWeakTable<Item, Identity> Identities = new();
    private sealed record Core(int Type, int Stack, int Prefix, int MaxStack, bool Favorite);
    private sealed record Pending(MovementSnapshot Action, int Source, int Destination, string SourceAfter,
        string DestinationAfter, int Selected, ulong Tick, object Evidence);
    private static Pending _pending;

    internal static bool IsOperation(string op) => op is "transfer_inventory_item" or "swap_inventory_slots" or "hold_inventory_item";
    private static Core Read(Item i) => i.IsAir ? new(0,0,0,0,false) : new(i.type,i.stack,i.prefix,i.maxStack,i.favorited);
    private static object Describe(Player p,int slot)
    {
        if(slot>=p.inventory.Length || p.inventory[slot]==null) return null;
        var c=Read(p.inventory[slot]);
        // Mutation receipts stay bounded even if a Mod has oversized display text.
        return new {slot,hotbarKey=HotbarSlots.KeyForInventorySlot(slot),typeId=c.Type,stack=c.Stack,
            prefix=c.Prefix,maxStack=c.MaxStack,favorited=c.Favorite,inventoryToken=Token(p.inventory[slot],slot)};
    }

    internal static string Token(Item item, int slot)
    {
        var c=Read(item);
        // Empty slots use value identity: vanilla is free to replace an air placeholder.
        string identity=item.IsAir ? "air" : Identities.GetValue(item,_=>new Identity()).Value;
        string source=$"{ObservationReader.WorldSessionId}|{CompanionBridge.Controls.SessionId}|{Main.myPlayer}|{slot}|{identity}|{c.Type}|{c.Stack}|{c.Prefix}|{c.MaxStack}|{c.Favorite}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
    }

    private static bool Owned(MovementSnapshot a) => ObservationReader.WorldSessionId==a.WorldSessionId
        && CompanionBridge.Controls.WithAtomicOwnership(a,()=>{});

    private static void Finish(MovementSnapshot a, string status, string reason, object evidence=null) =>
        CompanionBridge.Controls.FinishAtomic(a.ActionId,status,reason,evidence);

    internal static void Before(Player p, ControlRequest request, MovementSnapshot action)
    {
        p.controlLeft=p.controlRight=p.controlJump=p.controlDown=p.controlUp=p.controlUseItem=false;
        if (_pending?.Action.ActionId==action.ActionId && _pending.Action.Session==action.Session
            && _pending.Action.Generation==action.Generation)
        {
            if (Main.GameUpdateCount<=_pending.Tick) return;
            bool verified=Owned(action) && Main.netMode==NetmodeID.SinglePlayer && p.selectedItem==_pending.Selected
                && _pending.Source<p.inventory.Length && _pending.Destination<p.inventory.Length
                && p.inventory[_pending.Source]!=null && p.inventory[_pending.Destination]!=null && Main.mouseItem.IsAir
                && Token(p.inventory[_pending.Source],_pending.Source)==_pending.SourceAfter
                && Token(p.inventory[_pending.Destination],_pending.Destination)==_pending.DestinationAfter;
            Finish(action,verified ? "completed" : "unknown",verified ? "inventory_change_verified_next_update" : "inventory_changed_before_confirmation",
                new { mutationAttempted=true, beforeAndImmediateAfter=_pending.Evidence,
                    confirmedAtGameUpdate=Main.GameUpdateCount, selectedSlot=p.selectedItem,
                    source=Describe(p,_pending.Source), destination=Describe(p,_pending.Destination),
                    effectStatus=verified ? "local_inventory_verified" : "unknown", authority="single_player_local_inventory" });
            _pending=null;
            return;
        }
        _pending=null;
        AtomicParameters.Validate(request.Operation,request.Arguments,false);
        if (Main.netMode!=NetmodeID.SinglePlayer) { Finish(action,"failed","inventory_requires_single_player",new {mutationAttempted=false}); return; }
        if (p.itemAnimation!=0 || p.itemTime!=0 || p.selectedItem==58)
        { Finish(action,"failed","inventory_item_busy",new {mutationAttempted=false}); return; }
        if (!Main.mouseItem.IsAir) { Finish(action,"failed","mouse_item_occupied",new {mutationAttempted=false}); return; }
        var args=request.Arguments;
        int from=args.GetProperty("sourceSlot").GetInt32();
        int to=request.Operation=="hold_inventory_item" ? HotbarSlots.ToInventorySlot(args.GetProperty("hotbarKey").GetInt32())
            : args.GetProperty("destinationSlot").GetInt32();
        if (from>=p.inventory.Length || to>=p.inventory.Length) { Finish(action,"failed","inventory_slot_out_of_range",new {mutationAttempted=false}); return; }

        // Keep validation, native hooks and mutation within the cancel/disconnect gate.
        bool admitted=CompanionBridge.Controls.WithAtomicOwnership(action,()=>
        {
            if (ObservationReader.WorldSessionId!=action.WorldSessionId) { Finish(action,"cancelled","world_session_changed",new {mutationAttempted=false}); return; }
            if (p.inventory.Any(i=>i==null)) { Finish(action,"failed","invalid_inventory_slot_contents",new {mutationAttempted=false}); return; }
            if (Token(p.inventory[from],from)!=args.GetProperty("sourceToken").GetString()
                || Token(p.inventory[to],to)!=args.GetProperty("destinationToken").GetString())
            { Finish(action,"failed","stale_inventory_snapshot",new {mutationAttempted=false}); return; }
            var source=p.inventory[from]; var destination=p.inventory[to];
            var s=Read(source); var d=Read(destination);
            bool transfer=request.Operation=="transfer_inventory_item", hold=request.Operation=="hold_inventory_item";
            if(from!=to && ReferenceEquals(source,destination) && !source.IsAir)
            { Finish(action,"failed","inventory_slots_alias_same_item",new {mutationAttempted=false}); return; }
            if (source.IsAir && (transfer || hold)) { Finish(action,"failed","inventory_source_empty",new {mutationAttempted=false}); return; }
            int count=transfer ? args.GetProperty("count").GetInt32() : s.Stack;
            if (transfer && (count>s.Stack || count>s.MaxStack || s.Stack>s.MaxStack))
            { Finish(action,"failed","inventory_source_count_insufficient_or_invalid",new {mutationAttempted=false}); return; }
            if (transfer && !destination.IsAir && (s.Type!=d.Type || s.Prefix!=d.Prefix))
            { Finish(action,"failed","inventory_destination_not_compatible",new {mutationAttempted=false}); return; }
            if (transfer && !destination.IsAir && (count>d.MaxStack-d.Stack || d.Stack>d.MaxStack))
            { Finish(action,"failed","inventory_destination_capacity_insufficient",new {mutationAttempted=false}); return; }

            var before=new { source=Describe(p,from), destination=Describe(p,to), selectedSlot=p.selectedItem };
            string[] allBefore=p.inventory.Select((item,slot)=>Token(item,slot)).ToArray();
            bool attempted=false;
            string phase="permission";
            string Bounded(string value,int length) => value==null ? null : value[..Math.Min(value.Length,length)];
            void Unknown(string reason,Exception error=null) => Finish(action,"unknown",reason,new {mutationAttempted=attempted,before,
                source=Describe(p,from),destination=Describe(p,to),
                nativeError=error==null ? null : new {phase,type=Bounded(error.GetType().FullName,256),
                    message=Bounded(error.Message,512),stack=Bounded(error.StackTrace,2048)},
                effectStatus="unknown; no rollback or automatic retry"});
            try
            {
                // A mod hook may mutate and then throw. Publish intent before invoking any hook.
                attempted=true;
                CompanionBridge.Controls.UpdateAtomicEvidence(action.ActionId,new {mutationAttempted=true,before,effectStatus="pending"});
                if (transfer && !destination.IsAir)
                {
                    bool allowed=ItemLoader.CanStack(destination,source);
                    bool unchanged=p.inventory.Select((item,slot)=>Token(item,slot)).SequenceEqual(allBefore);
                    if (!unchanged || !Owned(action)) { Unknown("inventory_changed_during_stack_permission_hook"); return; }
                    if (!allowed) { Finish(action,"failed","inventory_stacking_denied",new {mutationAttempted=true,before,observedInventoryUnchanged=true}); return; }
                }
                if (!Owned(action)) return;
                int moved=count;
                phase="transfer_or_swap";
                if (transfer)
                {
                    if (destination.IsAir) p.inventory[to]=ItemLoader.TransferWithLimit(source,count);
                    else ItemLoader.StackItems(destination,source,out moved,numToTransfer:count);
                    if (source.IsAir && source.stack>=0) source.TurnToAir();
                }
                else if (from!=to) Utils.Swap(ref p.inventory[from],ref p.inventory[to]);
                if (hold && Owned(action)) p.selectedItem=to;
                phase="recipe_refresh";
                Recipe.FindRecipes(); // Native refresh can invoke mod conditions; verify after it too.

                var afterS=Read(p.inventory[from]); var afterD=Read(p.inventory[to]);
                Core WithCount(Core value,int stack,bool favorite) => stack==0 ? new(0,0,0,0,false)
                    : value with {Stack=stack,Favorite=favorite};
                Core expectedS=transfer ? WithCount(s,s.Stack-count,d.Type==0 ? s.Favorite : false) : from==to ? s : d;
                Core expectedD=transfer ? WithCount(d.Type==0 ? s : d,d.Stack+count,
                    d.Type==0 ? count==s.Stack && s.Favorite : d.Favorite || s.Favorite) : s;
                bool othersUnchanged=p.inventory.Select((item,slot)=>slot==from || slot==to || Token(item,slot)==allBefore[slot]).All(v=>v);
                if (moved!=count || afterS!=expectedS || afterD!=expectedD || !othersUnchanged
                    || p.inventory.Any(i=>i.stack<0) || !Main.mouseItem.IsAir || !Owned(action))
                { Unknown("inventory_native_result_not_verified"); return; }
                if (!Owned(action)) return;
                var evidence=new {mutationAttempted=true,before,
                    after=new {source=Describe(p,from),destination=Describe(p,to),selectedSlot=p.selectedItem},
                    count=transfer ? (int?)count : null, gameUpdate=Main.GameUpdateCount,
                    effectStatus="pending_next_update", authority="single_player_local_inventory"};
                _pending=new(action,from,to,Token(p.inventory[from],from),Token(p.inventory[to],to),p.selectedItem,Main.GameUpdateCount,evidence);
                CompanionBridge.Controls.UpdateAtomicEvidence(action.ActionId,evidence);
            }
            catch (Exception error) { Unknown("inventory_native_exception",error); }
        });
        if (!admitted) Finish(action,"cancelled","inventory_ownership_lost_before_mutation",new {mutationAttempted=false});
    }
}
