using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

// Explicit copied-world diagnostics, driven by real subsequent native updates.
internal static class ServerInventoryFixture
{
    private static readonly string[] cases={"move","split","merge","swap","swap_empty","hold0","hold1","hold9","hold_same","stale_source","stale_destination","stale_world","stale_generation","stale_slot","stale_actor","stale_mod_state","capacity","prefix","busy","mod_split","mod_merge","mod_denied","permission_throw","stack_throw","split_throw","cancel_before","cancel_after","disconnect_before","disconnect_after","forget_after","save_throw","save_mutate","interference"};
    private static readonly List<object> rows=new();
    private static int index;
    private static Item[] saved;
    private static ActorControl.IMovementDriver savedDriver;
    private static int selected;
    private static object handle;
    private static ServerAiNativeActions executor;
    private static AiReadOnlyProtocol.Identity identity;
    private static Item source,destination;
    private static string kind;
    private static string[] human;
    private static Item mouse;
    private static int myPlayer,focus;
    private static bool inventory;
    private static string[] Human()=>Main.player[0].inventory.Select(i=>$"{i.type}:{i.stack}:{i.prefix}").ToArray();
    internal static void Tick(Player p)
    {
        if(!ServerAiActionFixture.Enabled||!Program.LaunchParameters.ContainsKey("-companioninventorytest"))return;
        string request=Path.Combine(Main.SavePath,"inventory-request");
        if(saved==null)
        {
            if(!File.Exists(request))return;
            File.Delete(request);rows.Clear();index=0;
            savedDriver=ActorControl.Driver;saved=p.inventory;selected=p.selectedItem;human=Human();mouse=Main.mouseItem;myPlayer=Main.myPlayer;focus=Main.focusRecipe;inventory=Main.playerInventory;
        }
        try
        {
            if(handle==null)
            {
                kind=cases[index];p.inventory=Enumerable.Range(0,59).Select(_=>new Item()).ToArray();p.selectedItem=0;
                p.inventory[10]=new Item(ItemID.Wood,10);p.inventory[0]=new Item();
                if(kind is "merge" or "capacity" or "prefix" or "permission_throw" or "stack_throw" or "mod_merge" or "mod_denied")p.inventory[0]=new Item(ItemID.Wood,5);
                if(kind is "swap" or "hold0" or "hold1" or "hold9")p.inventory[0]=new Item(ItemID.StoneBlock,5);
                if(kind is "mod_split" or "mod_merge" or "mod_denied" or "permission_throw" or "stack_throw" or "split_throw" or "stale_mod_state" or "save_throw" or "save_mutate")
                {
                    p.inventory[10]=new Item(ModContent.ItemType<InventoryDiagnosticItem>(),10);
                    if(!p.inventory[0].IsAir)p.inventory[0]=new Item(ModContent.ItemType<InventoryDiagnosticItem>(),5);
                    ((InventoryDiagnosticItem)p.inventory[10].ModItem).Mode=kind.StartsWith("save_")?"":kind;
                    if(!p.inventory[0].IsAir)((InventoryDiagnosticItem)p.inventory[0].ModItem).Mode=kind;
                }
                if(kind=="capacity")p.inventory[0].stack=p.inventory[0].maxStack;
                if(kind=="prefix")p.inventory[0].prefix=1;
                source=p.inventory[10];destination=p.inventory[0];
                identity=new(p,ProbeSession.WorldSession,ProbeSession.ActorGeneration,p.whoAmI,null);
                var tokenId=kind switch{
                    "stale_world"=>identity with{World="old-world"},"stale_generation"=>identity with{Generation=identity.Generation+1},
                    "stale_slot"=>identity with{Slot=identity.Slot-1},"stale_actor"=>identity with{Actor=new object()},_=>identity};
                int to=kind=="hold0"?9:kind=="hold9"?8:0,from=kind=="hold_same"?0:10;
                if(kind=="hold_same")p.inventory[0]=new Item(ItemID.Wood,10);
                string op=kind.StartsWith("hold")?"hold_inventory_item":kind.StartsWith("swap")?"swap_inventory_slots":"transfer_inventory_item";
                var args=new Dictionary<string,object>{{"sourceSlot",from},{"sourceToken",ActorInventory.Token(tokenId,p.inventory[from],from)},{"destinationToken",ActorInventory.Token(tokenId,p.inventory[to],to)}};
                if(op=="hold_inventory_item")args["hotbarKey"]=global::CompanionBridge.HotbarSlots.KeyForInventorySlot(to);
                else args["destinationSlot"]=to;
                if(op=="transfer_inventory_item")args["count"]=kind=="move"?10:3;
                if(kind=="stale_source")p.inventory[10].stack++;
                if(kind=="stale_destination")p.inventory[0]=new Item(ItemID.StoneBlock);
                if(kind=="stale_mod_state")((InventoryDiagnosticItem)source.ModItem).State++;
                if(kind.StartsWith("save_"))((InventoryDiagnosticItem)source.ModItem).Mode=kind;
                if(kind.StartsWith("disconnect_")||kind=="forget_after")
                {
                    using var disconnected=new System.Threading.CancellationTokenSource();
                    var owner=Guid.NewGuid();var id=Guid.NewGuid().ToString("N");var lifecycle=new AiAtomicActions(new ServerAiNativeActions());
                    var root=JsonSerializer.SerializeToElement(new{actionId=id,operation=op,arguments=args,expiresAtUnixMs=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+1500});
                    var binding=new AiReadOnlyProtocol.Binding("inventory-fixture",identity.World,identity.Generation,identity.Slot);
                    var reply=lifecycle.Handle(owner,"start",root,identity,binding,disconnected.Token);
                    if(reply.Error!=null)throw new InvalidOperationException(reply.Error);
                    void Pump()=>lifecycle.Tick(()=>identity with{GameUpdate=Main.GameUpdateCount,SelectedSlot=p.selectedItem},(_,_)=>throw new InvalidOperationException("Unexpected selection adapter"));
                    if(kind!="disconnect_before"){Pump();Pump();Pump();}
                    if(kind=="forget_after")lifecycle.Forget(owner);else disconnected.Cancel();
                    Pump();
                    var receipt=JsonSerializer.SerializeToElement(lifecycle.Handle(owner,"action",root,identity,binding,default).Data).GetProperty("action");
                    bool released=receipt.GetProperty("endEvidence").GetProperty("inputsReleased").GetBoolean();
                    Record(new(receipt.GetProperty("status").GetString(),receipt.GetProperty("reason").GetString(),receipt.GetProperty("evidence").Clone(),released));
                    return;
                }
                if(kind=="busy")p.itemAnimation=1;
                executor=new();handle=executor.Start(identity,op,JsonSerializer.SerializeToElement(args),"diagnostic-inventory-"+kind);
                if(kind=="busy")p.itemAnimation=0;
                if(kind=="cancel_before")Record(executor.Cancel(identity,handle));
                return;
            }
            var result=executor.Poll(identity,handle);
            if(result.Status=="running")
            {
                if(kind=="cancel_after")Record(executor.Cancel(identity,handle));
                else if(kind=="interference")p.inventory[30]=new Item(ItemID.Diamond);
                return;
            }
            Record(result);
        }
        catch(Exception e)
        {
            File.WriteAllText(Path.Combine(Main.SavePath,"inventory-selftest.json"),JsonSerializer.Serialize(new{passed=false,kind,error=e.ToString(),rows}));
            Restore(p);throw;
        }
    }
    private static void Record(AiAtomicActions.NativeResult result)
    {
        string expected=kind.StartsWith("stale")||kind is "capacity" or "prefix" or "busy" or "mod_denied"?"failed":
            kind is "permission_throw" or "stack_throw" or "split_throw" or "cancel_after" or "disconnect_after" or "forget_after" or "save_throw" or "save_mutate" or "interference"?"unknown":kind is "cancel_before" or "disconnect_before"?"cancelled":"completed";
        if(result.Status!=expected||!result.InputsReleased||result.EffectVerified!=(expected=="completed"))throw new InvalidOperationException(kind+": "+JsonSerializer.Serialize(result));
        var p=(Player)identity.Actor;
        if(kind.StartsWith("stale")&&result.Reason!="stale_inventory_snapshot")throw new InvalidOperationException("Stale token executed");
        if(kind=="swap"&&(!ReferenceEquals(p.inventory[0],source)||!ReferenceEquals(p.inventory[10],destination)))throw new InvalidOperationException("Swap lost item instance");
        if(kind=="cancel_before"&&source.stack!=10||kind=="cancel_after"&&(source.stack!=7||p.inventory[0].stack!=3))throw new InvalidOperationException("Cancellation replayed/rolled back");
        if(!Human().SequenceEqual(human)||!ReferenceEquals(mouse,Main.mouseItem)||myPlayer!=Main.myPlayer||focus!=Main.focusRecipe||inventory!=Main.playerInventory)throw new InvalidOperationException("Human/UI state changed");
        rows.Add(new{kind,result});handle=null;index++;
        if(index==cases.Length)
        {
            File.WriteAllText(Path.Combine(Main.SavePath,"inventory-selftest.json"),JsonSerializer.Serialize(new{passed=true,humanAndUiUnchanged=true,rows}));
            Restore(p);
        }
    }
    private static void Restore(Player p){ActorControl.Driver=savedDriver;p.inventory=saved;p.selectedItem=selected;saved=null;handle=null;}
}

// Only registers for the explicit diagnostic launch. Hooks run inside real ItemLoader.
public sealed class InventoryDiagnosticItem : ModItem
{
    internal string Mode="";
    internal int State;
    public override bool IsLoadingEnabled(Mod mod)=>Program.LaunchParameters.ContainsKey("-companioninventorytest")&&Program.LaunchParameters.ContainsKey("-companionaiipctest");
    public override string Texture=>"Terraria/Images/Item_9";
    public override void SetDefaults(){Item.width=16;Item.height=16;Item.maxStack=9999;}
    public override void SaveData(TagCompound tag)
    {
        if(Mode=="save_throw")throw new InvalidOperationException("Diagnostic SaveData threw");
        if(Mode=="save_mutate")State++;
        tag["state"]=State;tag["mode"]=Mode;
    }
    public override bool CanStack(Item source)
    {
        if(Mode=="permission_throw"){State++;throw new InvalidOperationException("Diagnostic CanStack mutated then threw");}
        return Mode!="mod_denied";
    }
    public override void OnStack(Item source,int numToTransfer)
    {
        State++;
        if(Mode=="stack_throw")throw new InvalidOperationException("Diagnostic OnStack mutated then threw");
    }
    public override void SplitStack(Item source,int numToTransfer)
    {
        State++;
        if(Mode=="split_throw")throw new InvalidOperationException("Diagnostic SplitStack mutated then threw");
    }
}
