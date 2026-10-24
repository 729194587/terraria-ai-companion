using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CompanionBridge;
using CompanionBridge.Common.Systems;
using CompanionBridge.Common.Players;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

// Production schema, query, action adapter and control lifecycle. Native stack hooks
// and inventory storage are substitutes; not a Terraria or arbitrary-Mod acceptance test.
public static class InventoryOfflineTests
{
    private static readonly JsonSerializerOptions Json=new() {PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
    private static void Check(bool value,string message) {if(!value) throw new Exception(message);}
    public static void Run()
    {
        var controls=CompanionBridge.CompanionBridge.Controls;
        if(controls.SessionId!=0) controls.EndSession(controls.SessionId);
        controls.Reset(); int session=controls.BeginSession(),id=0;
        Main.myPlayer=0;Main.netMode=0;Main.gameMenu=false;Main.GameUpdateCount=1;
        ObservationReader.WorldSessionId="inventory-tests";
        var p=Main.player[0]=new Player {position=new(88,88)};
        var hook=new CompanionPlayer {Player=p};
        Item Air() => new() {type=0,stack=0};
        Item Item(int type=2,int stack=10,int max=99,byte prefix=0,bool favorite=false)
            => new() {type=type,stack=stack,maxStack=max,prefix=prefix,favorited=favorite};
        void Reset()
        {
            controls.Reset(); p.inventory=Enumerable.Range(0,59).Select(_=>Air()).ToArray();
            p.selectedItem=0;p.itemAnimation=p.itemTime=0;Main.netMode=0;Main.mouseItem=Air();
            ItemLoader.StackPermission=null;ItemLoader.OnStack=ItemLoader.OnSplit=null;Recipe.Refresh=null;
            ObservationReader.WorldSessionId="inventory-tests";
        }
        void Update() => controls.Update(true,Stopwatch.GetTimestamp(),new(true,new(88,88),new(0,0),100,100),
            worldSessionId:ObservationReader.WorldSessionId,queryAtomic:AtomicOperations.Query);
        JsonElement Query(int[] slots)
        {
            var request=ControlState.Parse(JsonSerializer.Serialize(new {v=1,id=++id,cmd="query_atomic",operation="inspect_inventory",arguments=new {mode="slots",slots}},Json));
            var task=controls.Enqueue(session,request,Stopwatch.GetTimestamp());Update();
            Check(task.Result.Ok,"Slots query protocol accepts request");
            return JsonSerializer.SerializeToElement(task.Result.Data,Json).GetProperty("data");
        }
        ControlRequest Start(string op,object args)
        {
            var request=ControlState.Parse(JsonSerializer.Serialize(new {v=1,id=++id,cmd="start_atomic",operation=op,arguments=args,expectedGeneration=controls.Generation},Json));
            var task=controls.Enqueue(session,request,Stopwatch.GetTimestamp());Update();
            Check(task.Result.Ok,"Inventory start protocol accepts request");
            hook.PostUpdateRunSpeeds();return request;
        }
        object Args(string op,int from=10,int to=0,int count=3,string sourceToken=null,string destinationToken=null)
        {
            sourceToken ??= AtomicInventory.Token(p.inventory[from],from);
            destinationToken ??= AtomicInventory.Token(p.inventory[to],to);
            if(op=="hold_inventory_item") return new {sourceSlot=from,hotbarKey=HotbarSlots.KeyForInventorySlot(to),sourceToken,destinationToken};
            if(op=="swap_inventory_slots") return new {sourceSlot=from,destinationSlot=to,sourceToken,destinationToken};
            return new {sourceSlot=from,destinationSlot=to,count,sourceToken,destinationToken};
        }
        void Confirm()
        {
            Check(controls.AtomicAction.Status=="running","Mutation is not completion");
            Main.GameUpdateCount++;hook.PostUpdateRunSpeeds();
            Check(controls.AtomicAction.Completed && controls.AtomicAction.EndEvidenceStatus=="pending","Next update verifies effect but not release");
            Main.GameUpdateCount++;hook.PostUpdateRunSpeeds();
            Check(controls.AtomicAction.EndEvidenceStatus=="known" && !p.controlUseItem,"Actual player hook confirms release");
        }
        void Failed(string reason)
        {Check(controls.AtomicAction.Status=="failed" && controls.AtomicAction.Reason==reason,"Expected known precondition failure: "+reason);}
        void Invalid(string operation,string args,bool query=false)
        {
            bool rejected=false;
            try {ControlState.Parse("{\"v\":1,\"id\":1,\"cmd\":\""+(query ? "query_atomic" : "start_atomic")+"\",\"operation\":\""+operation+"\",\"arguments\":"+args+(query ? "" : ",\"expectedGeneration\":0")+"}");}
            catch(Exception e) when(e is FormatException or InvalidOperationException) {rejected=true;}
            Check(rejected,"Strict inventory schema rejection: "+operation+args);
        }
        foreach(string args in new[]{"{\"mode\":\"slots\"}","{\"slots\":[0]}","{\"mode\":\"slots\",\"slots\":[0,0]}",
            "{\"mode\":\"slots\",\"slots\":[59]}","{\"mode\":\"slots\",\"slots\":[true]}","{\"mode\":\"slots\",\"slots\":[]}"})
            Invalid("inspect_inventory",args,true);
        string good=new string('a',64);
        string pair="\"sourceToken\":\""+good+"\",\"destinationToken\":\""+good+"\"";
        Invalid("transfer_inventory_item","{\"sourceSlot\":0,\"destinationSlot\":0,\"count\":1,"+pair+"}");
        Invalid("transfer_inventory_item","{\"sourceSlot\":0,\"destinationSlot\":50,\"count\":1,"+pair+"}");
        Invalid("transfer_inventory_item","{\"sourceSlot\":0,\"destinationSlot\":1,\"count\":0,"+pair+"}");
        Invalid("swap_inventory_slots","{\"sourceSlot\":0,\"destinationSlot\":1,\"sourceToken\":\"bad\",\"destinationToken\":\""+good+"\"}");
        Invalid("hold_inventory_item","{\"sourceSlot\":10,\"hotbarKey\":10,"+pair+"}");

        Reset();p.inventory[10]=Item(prefix:4,favorite:true);
        var q=Query(new[]{10,0});
        Check(q.GetProperty("inventory")[0].GetProperty("slot").GetInt32()==10
            && q.GetProperty("inventory")[0].GetProperty("prefix").GetInt32()==4
            && q.GetProperty("inventory")[0].GetProperty("maxStack").GetInt32()==99,"Compact explicit slots include action metadata");
        string token=q.GetProperty("inventory")[0].GetProperty("inventoryToken").GetString();
        Check(token.Length==64 && AtomicInventory.Token(p.inventory[10],10)==token,"Stable bounded opaque snapshot token");
        p.inventory[10]=p.inventory[10].Clone();
        Check(AtomicInventory.Token(p.inventory[10],10)!=token,"Same-value slot replacement invalidates token");
        var airToken=AtomicInventory.Token(p.inventory[0],0);p.inventory[0]=Air();
        Check(AtomicInventory.Token(p.inventory[0],0)==airToken,"Air placeholder replacement does not spuriously expire");
        ObservationReader.WorldSessionId="other-world";
        Check(AtomicInventory.Token(p.inventory[0],0)!=airToken,"Token cannot cross worlds");

        Reset();p.inventory[10]=Item(prefix:4,favorite:true);int splitCalls=0;
        ItemLoader.OnSplit=(_,_,count)=>{Check(count==3,"Native split exact requested count");splitCalls++;};
        Start("transfer_inventory_item",Args("transfer_inventory_item"));
        Check(p.inventory[10].stack==7 && p.inventory[0].stack==3 && p.inventory[0].prefix==4
            && p.inventory[10].favorited && !p.inventory[0].favorited && splitCalls==1,"Split preserves native prefix/favorite semantics");
        Confirm();Check(splitCalls==1,"Polling never repeats mutation");

        Reset();p.inventory[10]=Item(stack:3,prefix:5,favorite:true);
        Start("transfer_inventory_item",Args("transfer_inventory_item"));
        Check(p.inventory[10].IsAir && p.inventory[0].prefix==5 && p.inventory[0].favorited,"Full transfer retains item metadata");Confirm();
        Reset();p.inventory[10]=Item(favorite:true);p.inventory[0]=Item(stack:90);int stackCalls=0;
        ItemLoader.OnStack=(_,_,_)=>stackCalls++;
        Start("transfer_inventory_item",Args("transfer_inventory_item"));
        Check(p.inventory[10].stack==7 && p.inventory[0].stack==93 && !p.inventory[10].favorited && p.inventory[0].favorited && stackCalls==1,"Native stacking/favorite hook semantics");Confirm();

        Reset();p.inventory[10]=Item();p.inventory[0]=Item(type:3,prefix:7,favorite:true);
        var source=p.inventory[10];var destination=p.inventory[0];
        Start("swap_inventory_slots",Args("swap_inventory_slots"));
        Check(ReferenceEquals(p.inventory[0],source) && ReferenceEquals(p.inventory[10],destination),"Swap retains whole item instances");Confirm();
        for(int key=0;key<=9;key++)
        {
            Reset();p.inventory[10]=Item();int to=HotbarSlots.ToInventorySlot(key);p.inventory[to]=Item(type:3);
            Start("hold_inventory_item",Args("hold_inventory_item",to:to));
            Check(p.selectedItem==to && p.inventory[to].type==2 && p.inventory[10].type==3,"Hold swaps into correct displayed hotbar key");Confirm();
        }
        Reset();p.inventory[0]=Item();source=p.inventory[0];
        Start("hold_inventory_item",Args("hold_inventory_item",from:0));
        Check(ReferenceEquals(p.inventory[0],source),"Already in requested slot only selects");Confirm();

        Reset();p.inventory[10]=Item();token=AtomicInventory.Token(p.inventory[10],10);p.inventory[10].stack--;
        Start("transfer_inventory_item",Args("transfer_inventory_item",sourceToken:token));Failed("stale_inventory_snapshot");
        Check(p.inventory[10].stack==9 && p.inventory[0].IsAir,"Stale source cannot spend anything");
        Reset();p.inventory[10]=Item();token=AtomicInventory.Token(p.inventory[0],0);p.inventory[0]=Item(type:3);
        Start("swap_inventory_slots",Args("swap_inventory_slots",destinationToken:token));Failed("stale_inventory_snapshot");
        Reset();p.inventory[10]=Item();p.inventory[0]=Item(stack:98);
        Start("transfer_inventory_item",Args("transfer_inventory_item"));Failed("inventory_destination_capacity_insufficient");
        Check(p.inventory[10].stack==10 && p.inventory[0].stack==98,"Insufficient capacity never silently partially transfers");
        Reset();p.inventory[10]=Item(stack:2);Start("transfer_inventory_item",Args("transfer_inventory_item"));Failed("inventory_source_count_insufficient_or_invalid");
        Reset();p.inventory[10]=Item();p.inventory[0]=Item(prefix:1);Start("transfer_inventory_item",Args("transfer_inventory_item"));Failed("inventory_destination_not_compatible");
        Reset();p.inventory[10]=Item();p.inventory[0]=p.inventory[10];Start("transfer_inventory_item",Args("transfer_inventory_item"));Failed("inventory_slots_alias_same_item");
        Reset();Start("hold_inventory_item",Args("hold_inventory_item"));Failed("inventory_source_empty");
        Reset();p.inventory[10]=Item();Main.mouseItem=Item();Start("hold_inventory_item",Args("hold_inventory_item"));Failed("mouse_item_occupied");
        Reset();p.inventory[10]=Item();p.itemAnimation=1;Start("hold_inventory_item",Args("hold_inventory_item"));Failed("inventory_item_busy");
        Reset();p.inventory[10]=Item();Main.netMode=NetmodeID.MultiplayerClient;Start("hold_inventory_item",Args("hold_inventory_item"));Failed("inventory_requires_single_player");
        Reset();p.inventory[10]=Item();p.inventory[0]=Item();ItemLoader.StackPermission=(_,_)=>false;
        Start("transfer_inventory_item",Args("transfer_inventory_item"));Failed("inventory_stacking_denied");

        Reset();p.inventory[10]=Item();p.inventory[0]=Item();
        ItemLoader.StackPermission=(_,s)=>{s.stack--;return true;};
        Start("transfer_inventory_item",Args("transfer_inventory_item"));
        Check(controls.AtomicAction.Status=="unknown" && p.inventory[10].stack==9 && p.inventory[0].stack==10,"Mutating permission hook is unknown, never rolled back or followed by transfer");
        Reset();p.inventory[10]=Item();p.inventory[0]=Item();
        ItemLoader.OnStack=(d,s,_)=>{s.stack--;d.stack++;throw new Exception("fixture");};
        Start("transfer_inventory_item",Args("transfer_inventory_item"));
        Check(controls.AtomicAction.Status=="unknown" && p.inventory[10].stack==9 && p.inventory[0].stack==11,"Exception after effect remains unknown with observed effect retained");
        var nativeError=JsonSerializer.SerializeToElement(controls.AtomicAction.Evidence,Json).GetProperty("nativeError");
        Check(nativeError.GetProperty("phase").GetString()=="transfer_or_swap"
            && nativeError.GetProperty("type").GetString()=="System.Exception"
            && nativeError.GetProperty("message").GetString()=="fixture"
            && nativeError.GetProperty("stack").GetString().Length>0,"Bounded native exception cause and phase retained without changing unknown semantics");
        Reset();p.inventory[10]=Item();Recipe.Refresh=()=>p.inventory[20]=Item(type:3);
        Start("hold_inventory_item",Args("hold_inventory_item"));
        Check(controls.AtomicAction.Status=="unknown","Unexpected recipe refresh side effect does not pass verification");

        Reset();p.inventory[10]=Item();Start("transfer_inventory_item",Args("transfer_inventory_item"));
        p.inventory[0].stack++;Main.GameUpdateCount++;hook.PostUpdateRunSpeeds();
        Check(controls.AtomicAction.Status=="unknown","Operand change before confirmation is not success");
        Reset();p.inventory[10]=Item();var request=Start("transfer_inventory_item",Args("transfer_inventory_item"));
        controls.RevokeControl();AtomicOperations.Before(p,request);
        Check(controls.AtomicAction.Status=="cancelled" && p.inventory[10].stack==7 && p.inventory[0].stack==3,"Cancel after mutation preserves effects and cannot replay mutation");
        var evidence=JsonSerializer.SerializeToElement(controls.AtomicAction.Evidence,Json);
        Check(evidence.GetProperty("mutationAttempted").GetBoolean(),"Cancellation retains already-attempted effect evidence");

        Reset();p.inventory[10]=Item();p.inventory[0]=Item();
        ItemLoader.StackPermission=(_,_)=>{controls.RevokeControl();return true;};
        Start("transfer_inventory_item",Args("transfer_inventory_item"));
        Check(controls.AtomicAction.Status=="cancelled" && p.inventory[10].stack==10 && p.inventory[0].stack==10,"Ownership lost in hook prevents subsequent mutation");

        // The same gate prevents a cancellation receipt overtaking an admitted batch.
        Reset();p.inventory[10]=Item();p.inventory[0]=Item();Task cancel=null;using var attempted=new ManualResetEventSlim();
        ItemLoader.OnStack=(_,_,_)=> {
            cancel=Task.Run(()=>{attempted.Set();controls.EndSession(session);});
            Check(attempted.Wait(1000) && !cancel.IsCompleted,"Disconnect waits for admitted mutation gate");
        };
        Start("transfer_inventory_item",Args("transfer_inventory_item"));
        Check(cancel.Wait(2000) && p.inventory[10].stack==7 && p.inventory[0].stack==13,"Disconnect follows the admitted mutation, never races through it");
        ItemLoader.StackPermission=null;ItemLoader.OnStack=ItemLoader.OnSplit=null;Recipe.Refresh=null;
        controls.Reset();ObservationReader.WorldSessionId="hook-test";Main.mouseItem=Air();
        Console.WriteLine("PASS: inventory production adapter/schema/query/lifecycle: guarded tokens, native-hook substitutes, exact transfer/split/stack/swap/hold, next-update proof, stale/busy/capacity/network rejection, partial effect/exception, cancel/disconnect ordering (not game acceptance)");
    }
}
