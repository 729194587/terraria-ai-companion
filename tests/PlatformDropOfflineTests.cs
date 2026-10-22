using System;
using System.Diagnostics;
using CompanionBridge;
using CompanionBridge.Motion;
using CompanionBridge.Common.Players;
using CompanionBridge.Common.Systems;
using Terraria;

public static class PlatformDropOfflineTests
{
    private static void Check(bool ok,string message) { if(!ok) throw new Exception(message); }
    private static char Cell(int x,int y) => x>=99 && x<=104 && y==97 ? '=' : y>=100 ? '#' : '.';
    private static PhysicsState Step(PhysicsState state,bool down,float floor)
    {
        float vy=state.Vy+0.4f;
        float y=state.Py+vy;
        // This is only a test collision adapter. The actual game uses native DryCollision.
        if(!down && state.Py+42<=floor && y+42>=floor) return state with { Py=floor-42,Vy=0,Grounded=true };
        return state with { Py=y,Vy=vy,Grounded=false };
    }
    public static void Run()
    {
        foreach(int key in new[]{1,2,3,4,5,6,7,8,9,0})
            Check(HotbarSlots.KeyForInventorySlot(HotbarSlots.ToInventorySlot(key))==key,"Hotbar key round trip");
        Check(HotbarSlots.ToInventorySlot(1)==0 && HotbarSlots.ToInventorySlot(0)==9
            && HotbarSlots.KeyForInventorySlot(10)==null,"Keyboard 1/0 map to first/last native slots only");
        foreach(int rows in new[]{1,3,4})
        {
            float floor=(97+rows)*16;
            char Map(int x,int y) => y==97 || y==97+rows ? '=' : '.';
            var state=new PhysicsState(1600,1510,0,0,true);
            var drop=new PlatformDropAction(); drop.Begin(state,100,true,Map);
            Check(drop.Status=="running" && drop.Down && drop.TargetSurfaceY==floor,"Choose nearest lower flat platform");
            bool sawReleased=false;
            for(int f=0;f<100 && drop.Status=="running";f++)
            {
                drop.Before(state,100,Map);
                sawReleased|=!drop.Down;
                state=Step(state,drop.Down,floor);
                drop.After(state,100,Map);
            }
            Check(drop.Status=="completed" && sawReleased && state.Py+42==floor && state.Px==1600,
                "Native Down input clears source, releases before next platform and verifies settled landing without horizontal detour");
        }
        var start=new PhysicsState(1600,1510,0,0,true);
        foreach(char bad in new[]{'#','h','a','b','?','!'})
        {
            var drop=new PlatformDropAction();drop.Begin(start,100,true,(x,y)=>y==97?bad:y>=100?'#':'.');
            Check(drop.Status=="failed" && !drop.Down,"Only flat one-way platform permits drop");
        }
        foreach(char bad in new[]{'?','!'})
        {
            var drop=new PlatformDropAction();drop.Begin(start,100,true,(x,y)=>y==97?'=':y==98?bad:y>=100?'#':'.');
            Check(drop.Status=="failed" && !drop.Down,"Unknown/liquid/hazard corridor prevents Down injection");
        }
        var noFloor=new PlatformDropAction();noFloor.Begin(start,100,true,(x,y)=>y==97?'=':'.');
        Check(noFloor.Status=="failed" && !noFloor.Down,"No safe lower surface within bound is explicit failure");
        var changed=new PlatformDropAction();changed.Begin(start,100,true,Cell);
        changed.Before(start,100,(x,y)=>y==97?'=':'.');
        Check(changed.Status=="failed" && !changed.Down,"Recheck lower support before continued Down");
        var damaged=new PlatformDropAction();damaged.Begin(start,100,true,Cell);damaged.After(start,99,Cell);
        Check(damaged.Status=="failed" && !damaged.Down,"Life decrease stops native input");

        // Actual protocol/control -> production ModPlayer -> actual drop action -> release receipt.
        var controls=CompanionBridge.CompanionBridge.Controls;
        if(controls.SessionId!=0) controls.EndSession(controls.SessionId);
        controls.Reset();int session=controls.BeginSession(),id=0;
        Main.myPlayer=0;Main.netMode=0;Main.gameMenu=false;
        Main.player[0]=new Player { whoAmI=0,position=new(1600,1510) };
        TerrainReader.Cell=Cell;
        var hook=new CompanionPlayer { Player=Main.LocalPlayer };
        var request=ControlState.Parse("{\"v\":1,\"id\":"+(++id)+",\"cmd\":\"start_atomic\",\"operation\":\"drop_through_platform\",\"arguments\":{},\"expectedGeneration\":"+controls.Generation+"}");
        var pending=controls.Enqueue(session,request,Stopwatch.GetTimestamp());
        PhysicsState actual=start;
        void Tick(bool simulate=true)
        {
            Main.GameUpdateCount++;
            controls.Update(true,Stopwatch.GetTimestamp(),new(true,new(actual.Px,actual.Py),new(actual.Vx,actual.Vy),100,100),worldSessionId:ObservationReader.WorldSessionId);
            var p=Main.LocalPlayer;p.controlDown=p.controlLeft=p.controlRight=p.controlJump=false;
            hook.PostUpdateRunSpeeds();
            Check(!p.controlLeft && !p.controlRight,"Drop never walks sideways");
            if(simulate) actual=Step(actual,p.controlDown,1600);
            p.position=new(actual.Px,actual.Py);p.velocity=new(actual.Vx,actual.Vy);
            hook.PostUpdate();
        }
        Tick();Check(pending.Result.Ok && controls.AtomicAction.DurationMs==3000,"Bounded drop starts on existing action lease");
        int actionId=controls.AtomicAction.ActionId;
        for(int f=0;f<100 && controls.AtomicAction.Status=="running";f++)
        {
            if(f%10==0) controls.Enqueue(session,new(++id,"renew_move",ActionId:actionId),Stopwatch.GetTimestamp());
            Tick();
        }
        Check(controls.AtomicAction.Completed && controls.AtomicAction.Evidence!=null,"Actual adapter only completes on verified lower landing");
        Tick(false);Check(controls.AtomicAction.EndEvidence!=null && !Main.LocalPlayer.controlDown,"Actual hook confirms Down input released");
        // Re-start at source and cancel before the first physics step.
        actual=start;Main.LocalPlayer.position=new(1600,1510);Main.LocalPlayer.velocity=new(0,0);
        request=request with { Id=++id,ExpectedGeneration=controls.Generation };
        controls.Enqueue(session,request,Stopwatch.GetTimestamp());Tick(false);
        controls.Enqueue(session,new(++id,"cancel_move",ActionId:controls.AtomicAction.ActionId),Stopwatch.GetTimestamp());Tick(false);
        Check(controls.AtomicAction.Status=="cancelled" && controls.AtomicAction.EndEvidence!=null && !Main.LocalPlayer.controlDown,
            "Cancelled native drop cannot hold Down or resume");
        controls.EndSession(session);controls.Reset();TerrainReader.Cell=(x,y)=>'.';
        var deadlineControl=new ControlState();int deadlineSession=deadlineControl.BeginSession();
        long now=Stopwatch.GetTimestamp();
        var deadlineStart=ControlState.Parse("{\"v\":1,\"id\":1,\"cmd\":\"start_atomic\",\"operation\":\"drop_through_platform\",\"arguments\":{},\"expectedGeneration\":0}");
        deadlineControl.Enqueue(deadlineSession,deadlineStart,now);
        var snapshot=new PlayerSnapshot(true,new(1600,1510),new(0,0),100,100);
        deadlineControl.Update(true,now,snapshot,worldSessionId:"drop-deadline");
        int deadlineId=deadlineControl.AtomicAction.ActionId;
        for(int half=1;half<=6;half++)
        {
            long time=now+half*Stopwatch.Frequency/2;
            deadlineControl.Enqueue(deadlineSession,new(half+1,"renew_move",ActionId:deadlineId),time);
            deadlineControl.Update(true,time,snapshot,worldSessionId:"drop-deadline");
        }
        Check(deadlineControl.AtomicAction.Status=="timeout" && !deadlineControl.IsMoving,
            "Down action absolute three-second deadline remains fixed despite lease renewal");
        deadlineControl.EndSession(deadlineSession);
        Console.WriteLine("PASS: hotbar 1..9,0 mapping; bounded native Down drop, single-layer release, terrain/health guards, actual player hooks/lease/cancel/endEvidence (collision stub)");
    }
}
