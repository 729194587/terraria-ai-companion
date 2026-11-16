using System;
using System.Collections.Generic;
using System.Text.Json;
using CompanionBridge.ServerIpc;
using Identity = CompanionBridge.ServerIpc.AiReadOnlyProtocol.Identity;
using Result = CompanionBridge.ServerIpc.AiAtomicActions.NativeResult;

public static class CombatOfflineTests
{
    private sealed class World : ICombatWorld
    {
        public CombatTarget Target {get;set;} = new(2, 1, "spawn-a");
        public CombatWeapon Weapon => new(0, 1, "Sword");
        public CombatHit LastHit { get; set; }
        public int ItemChecks => 0;
        public int CombinedUpdates => 0;
        internal CombatObservation Observation = new(true,true,true,10);
        internal int Starts, Releases, Steps, Abandons;
        internal bool Throw, Released = true;
        internal string StepFailure;
        public string Prepare() => null;
        public void Acquire() => Starts++;
        public CombatObservation Observe() => Observation;
        public string Step(out string phase)
        {
            phase = "approach";
            Steps++;
            if (Throw) throw new InvalidOperationException("native fault");
            return StepFailure;
        }
        internal bool HasNext;
        public string Retarget(Identity frame,IReadOnlySet<string> skipped)
        {
            if(!HasNext)return "combat_threats_cleared";
            HasNext=false;Target=new(2,1,"spawn-b");LastHit=null;Observation=new(true,true,true,15);
            return null;
        }
        public bool Release() { Releases++; return Released; }
        public void Abandon() => Abandons++;
    }

    private sealed class Executor : AiAtomicActions.IExecutor
    {
        internal bool Released = true;
        internal bool AllowTransfer = true;
        internal string StopReason;
        public bool SupportsCombat => true;
        public bool CanTransferBody(Identity id) => AllowTransfer;
        public string Validate(string op, JsonElement args) => null;
        public object Start(Identity id, string op, JsonElement args, string actionId) => new object();
        public Result Poll(Identity id, object handle) => CompanionTask.Running();
        public Result Cancel(Identity id, object handle) => CancelWithReason(id,handle,"cancelled","explicit_cancel");
        public Result CancelWithReason(Identity id, object handle, string status, string reason)
        { StopReason = reason; return new(status,reason,null,Released); }
        public void Shutdown() {}
    }

    private static void Check(bool ok, string name)
    { if(!ok)throw new Exception("Combat: "+name); }

    public static void Run()
    {
        var id = new Identity(new object(),"world",1,254,null);
        var frame = id with{GameUpdate=1};
        var next = id with{GameUpdate=2};
        Check(CombatStrikeEvidence.Observe(true,6,-3,9,false,2) is {Defeated:true,LifeBefore:6,LifeAfter:-3},"lethal strike survives deactivation");
        Check(CombatStrikeEvidence.Observe(true,6,0,6,true,2) is {Defeated:false},"death veto cannot prove defeat");
        Check(CombatStrikeEvidence.Observe(true,6,0,0,false,2)==null,"zero native damage cannot prove external kill");
        Check(CombatStrikeEvidence.Observe(false,6,0,6,false,2)==null,"replacement cannot produce strike evidence");
        Check(CombatStrikeEvidence.Observe(true,0,-1,1,false,2)==null,"already dead target has no new evidence");
        var cycle = new CombatInputCycle();
        cycle.Movement(1);
        Check(cycle.BeginItemCheck(1)&&!cycle.BeginItemCheck(1),"one item check per update");
        cycle.Swing(1);cycle.Movement(1);
        Check(cycle.CombinedUpdates==1&&cycle.ItemChecks==1,"movement and swing combine exactly once");
        Check(cycle.BeginItemCheck(2),"next update allowed");
        cycle.Swing(2);cycle.Movement(3);
        Check(cycle.CombinedUpdates==1&&cycle.ItemChecks==2,"inputs from different frames never combine");
        TaskSlot Task(World w) => new(new CombatController(w,"agent",null),"task",null,id,20);
        var world = new World();
        var task = Task(world);
        Check(task.Tick(frame).Status=="running"&&world.Starts==1,"accepted to running");
        Check(task.Tick(frame).Status=="running"&&world.Steps==0,"same tick deduplicated");
        Check(task.Tick(next).Status=="running"&&world.Steps==1,"native approach");
        world.LastHit = new(10,4,6,false,2);
        world.Observation = new(true,true,true,4);
        Check(task.Tick(id with{GameUpdate=3}).Status=="running","damage is not completion");
        world.LastHit = new(4,0,4,true,3);
        world.Observation = new(true,false,false,0);
        Check(task.Tick(id with{GameUpdate=4}) is {Status:"completed",InputsReleased:true,EffectVerified:true}
            &&world.Releases==1,"attributed native defeat with release");
        foreach(var (observation, hit, expected) in new[] {
            (new CombatObservation(false,true,true,30),new CombatHit(4,0,4,true,1),"combat_target_replaced"),
            (new CombatObservation(true,false,false,0),(CombatHit)null,"combat_target_invalidated"),
            (new CombatObservation(true,false,false,-3),new CombatHit(10,4,6,false,1),"combat_target_invalidated"),
            (new CombatObservation(true,false,false,4),(CombatHit)null,"combat_target_invalidated") })
        {
            world = new(){Observation=observation,LastHit=hit};task=Task(world);task.Tick(frame);
            Check(task.Tick(next) is {Status:"failed",EffectVerified:false} r && r.Reason==expected&&world.Releases==1,expected);
        }
        world=new(){Throw=true};task=Task(world);task.Tick(frame);
        Check(task.Tick(next) is {Status:"unknown",InputsReleased:true,EffectVerified:false}&&world.Releases==1,"exception releases and remains unknown");
        world=new();task=Task(world);task.Tick(frame);
        Check(task.Tick(id with{GameUpdate=20}) is {Status:"timeout",Reason:"task_game_update_deadline",InputsReleased:true,EffectVerified:false}
            &&world.Releases==1,"deadline releases combat ownership");
        foreach(var (reason,status) in new[]{("combat_firing_navigation_timeout","timeout"),("combat_native_unconfirmed","unknown")})
        {
            world=new(){StepFailure=reason};task=Task(world);task.Tick(frame);
            Check(task.Tick(next) is {InputsReleased:true,EffectVerified:false} nav&&nav.Status==status
                &&nav.Reason==reason&&world.Releases==1,"incomplete firing route is not a verified obstruction");
        }
        world=new(){Released=false};task=Task(world);task.Tick(frame);
        Check(task.Stop(next,"cancelled","explicit_cancel") is {Status:"unknown",InputsReleased:false},"unconfirmed cleanup");
        world=new();task=Task(world);task.Tick(frame);
        Check(task.Stop(next,"cancelled","explicit_cancel") is {Status:"cancelled",InputsReleased:true}&&world.Releases==1,"explicit cancel");
        world=new();task=Task(world);task.Tick(frame);
        Check(task.Tick(next with{Generation=2}).Status=="unknown"&&world.Abandons==1&&world.Releases==0,"stale actor gets no writes");

        world=new(){HasNext=true};task=new(new CombatController(world,"reflex","native_threat"),"defense",null,id,20);
        task.Tick(frame);
        Check(task.Tick(next).Status=="running"&&world.Target.EntityRef=="spawn-a","valid reflex target retained");
        world.LastHit=new(10,0,10,true,2);world.Observation=new(true,false,false,0);
        Check(task.Tick(id with{GameUpdate=3}).Status=="running"&&world.Target.EntityRef=="spawn-b"
            &&world.LastHit==null&&world.Releases==0&&world.Starts==1,"reflex switches spawn without releasing body or transferring kill proof");
        world.Observation=new(true,false,false,0);
        var defense=task.Tick(id with{GameUpdate=4});
        var encounters=JsonSerializer.SerializeToElement(defense.Evidence).GetProperty("encounters");
        Check(defense is {Status:"completed",Reason:"combat_threats_cleared",InputsReleased:true}&&world.Releases==1
            &&encounters.GetArrayLength()==2&&encounters[0].GetProperty("LastHit").GetProperty("Defeated").GetBoolean()
            &&encounters[1].GetProperty("LastHit").ValueKind==JsonValueKind.Null,"external removal clears last threat without inventing a kill");

        foreach(bool released in new[]{true,false})
        {
            var executor = new Executor{Released=released};
            var actions = new AiAtomicActions(executor);
            var owner=Guid.NewGuid();
            var binding=new AiReadOnlyProtocol.Binding("actor","world",1,254);
            var ordinary=Guid.NewGuid().ToString("N");
            var start=JsonSerializer.SerializeToElement(new{actionId=ordinary,operation="mine_target",arguments=new{x=1,y=2},expiresAtUnixMs=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+1000});
            Check(actions.Handle(owner,"start",start,id,binding,default).Error==null,"ordinary admission");
            actions.Tick(()=>frame,null);actions.Tick(()=>next,null);
            actions.Reflex(next,"actor",null,null);
            Check(executor.StopReason==null,"no authoritative threat");
            actions.Reflex(next,"actor","spawn-a","npc_targeting_actor_in_range");
            Check(executor.StopReason==CombatProtocol.Interrupt,"reflex requests combat interruption");
            var action=JsonSerializer.SerializeToElement(actions.CombatState(next)).GetProperty("action");
            if(released)
            {
                var combatId=action.GetProperty("actionId").GetString();
                actions.Reflex(next,"actor","spawn-a","npc_targeting_actor_in_range");
                Check(JsonSerializer.SerializeToElement(actions.CombatState(next)).GetProperty("action").GetProperty("actionId").GetString()==combatId,"one active combat");
                var protocol=new AiReadOnlyProtocol(actions);
                using var bound=JsonDocument.Parse(protocol.Handle(owner,"{\"v\":1,\"id\":\"bind\",\"op\":\"bind\"}",()=>next));
                var lease=bound.RootElement.GetProperty("binding");
                string Request(string op)=>JsonSerializer.Serialize(new{v=1,id=op,op,binding=lease});
                using var state=JsonDocument.Parse(protocol.Handle(owner,Request("state"),()=>next));
                Check(state.RootElement.GetProperty("ok").GetBoolean(),"observation while combat owns body");
                var turn=JsonSerializer.Serialize(new{v=1,id="turn",op="reply_turn",binding=lease,replyTurn=1});
                var say=JsonSerializer.Serialize(new{v=1,id="say",op="say",binding=lease,replyTurn=1,
                    messageId=Guid.NewGuid().ToString("N"),text="combat progress",expiresAtUnixMs=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+1000});
                int sent=0;
                object Publish(Identity actor,string text){sent++;return new{status="published"};}
                protocol.ObserveReplyTurn(owner,turn);
                protocol.Handle(owner,turn,()=>next,say:Publish);
                using var speech=JsonDocument.Parse(protocol.Handle(owner,say,()=>next,say:Publish));
                Check(speech.RootElement.GetProperty("ok").GetBoolean()&&sent==1&&actions.HasActive,"chat during combat");
                var cancel=JsonSerializer.SerializeToElement(new{actionId=combatId});
                Check(actions.Handle(owner,"cancel",cancel,next,binding,default).Error==null&&!actions.HasActive,"bound Agent cancels reflex");
                actions.Reflex(next with{GameUpdate=1000},"actor","spawn-a","npc_targeting_actor_in_range");
                Check(!actions.HasActive,"explicit cancel suppresses reflex for that spawn");
            }
            else
            {
                Check(action.ValueKind==JsonValueKind.Null&&!actions.HasActive,"reflex refuses unsafe transfer");
                Check(actions.Handle(owner,"start",start,next,binding,default).Error=="duplicate_action","reject replay");
                actions.Reflex(next with{GameUpdate=3},"actor","spawn-a","npc_targeting_actor_in_range");
                Check(!actions.HasActive,"unknown cleanup fences future reflex");
            }
        }
        Console.WriteLine("Combat offline lifecycle and priority passed.");
    }
}
