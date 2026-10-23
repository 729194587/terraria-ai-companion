using System;
using System.Diagnostics;
using System.Text.Json;
using CompanionBridge;

public static class LocalInputOfflineTests
{
    static void Check(bool ok,string message) { if(!ok) throw new Exception(message); }
    public static void Run()
    {
        foreach(string fields in new[]{",\"chatId\":\"forged\"",",\"text\":\"move\"",",\"expectedGeneration\":0",",\"cmd\":\"begin_local_turn\""})
        {
            bool rejected=false;
            try { ControlState.Parse("{\"v\":1,\"id\":1,\"cmd\":\"begin_local_turn\""+fields+"}"); }
            catch(FormatException) { rejected=true; }
            Check(rejected,"Local identity registration rejects parameters/duplicates");
        }
        var state=new ControlState(); int session=state.BeginSession(),id=0;
        long now=Stopwatch.GetTimestamp();
        var player=new PlayerSnapshot(true,new(80,80),new(0,0),100,100);
        ControlRequest Begin() => ControlState.Parse("{\"v\":1,\"id\":"+(++id)+",\"cmd\":\"begin_local_turn\"}");
        ControlReply Apply(ControlRequest request,bool ready=true,long? at=null)
        {
            var pending=state.Enqueue(session,request,now);
            state.Update(ready,at??now,ready?player:PlayerSnapshot.NotInWorld,worldSessionId:"local-test");
            return pending.Result;
        }
        string ChatId(ControlReply reply) => JsonSerializer.SerializeToElement(reply.Data).GetProperty("chatId").GetString();
        var request=Begin();
        var queued=state.Enqueue(session,request,now);
        Check(!queued.IsCompleted,"Local turn is registered on game thread, never socket thread");
        state.Update(true,now,player,worldSessionId:"local-test");
        Check(queued.Result.Ok && queued.Result.Status=="local_turn_started" && !state.IsMoving,"Registration does not acquire action lease");
        string first=ChatId(queued.Result);
        ControlRequest Action(string chat) => ControlState.Parse(JsonSerializer.Serialize(new {v=1,id=++id,cmd="start_atomic",
            operation="mine_wall",arguments=new {x=5,y=5},expectedGeneration=state.Generation,chatId=chat}));
        Check(Apply(Action(first)).Ok,"Local token uses ordinary guarded action start");
        var running=state.AtomicAction;
        var secondReply=Apply(Begin()); string second=ChatId(secondReply);
        Check(second!=first && !state.IsMoving && state.AtomicAction.Status=="cancelled"
            && state.AtomicAction.Reason=="new_local_input","New local instruction cancels existing action and rotates identity");
        Check(state.AtomicAction.EndEvidenceStatus=="pending","Cancellation ACK is not release evidence");
        Check(Apply(Action(first)).Status=="superseded","Stale local token cannot act even with current generation");
        Check(Apply(new(++id,"renew_move",ActionId:running.ActionId)).Status=="action_changed","Old action cannot renew after replacement");
        Check(Apply(Action(second)).Status=="control_busy","New turn cannot bypass pending input release");
        state.ConfirmMovementInputReleased("local-test",10,"PostUpdateRunSpeeds_before_bridge_input",true);
        Check(Apply(Action(second)).Ok,"New local action allowed after real hook release boundary");
        state.RevokeControl(player,now);
        state.ConfirmMovementInputReleased("local-test",11,"UpdateDead_after_revoke",true);
        state.Update(true,now,player,worldSessionId:"local-test");
        state.ToggleManual(now);
        Check(Apply(Begin()).Status=="manual_active" && state.Mode==ControlMode.Manual,"Local entry cannot take over manual P control");
        state.Reset();
        Check(Apply(Begin(),at:now+Stopwatch.Frequency).Status=="expired","Queued registration is bounded by existing lease age");
        Check(Apply(Begin(),ready:false).Status=="not_ready","No local token issued outside ready world");
        state.Update(true,now,player,worldSessionId:"local-test");
        var oldPending=state.Enqueue(session,Begin(),now);
        state.EndSession(session);
        Check(!oldPending.Result.Ok,"Disconnect clears pending registration");
        session=state.BeginSession();
        string fresh=ChatId(Apply(Begin()));
        Check(fresh!=first && fresh!=second,"Local turn identity belongs to current TCP session");
        Check(Apply(Action(second)).Status=="superseded","Old session token cannot act in new session");
        // Remote mode continues using its existing latest-chat identity.
        state.ObserveChat("remote-event");
        Check(Apply(Action(fresh)).Status=="superseded","Remote chat can invalidate a local token via existing guard");
        Check(Apply(Action("remote-event")).Ok,"Existing remote chat action behavior remains supported");
        state.EndSession(session);
        Console.WriteLine("PASS: local terminal protocol: game-thread identity, superseded guards, replacement cancellation, pending release, manual/readiness/expiry and session isolation");
    }
}
