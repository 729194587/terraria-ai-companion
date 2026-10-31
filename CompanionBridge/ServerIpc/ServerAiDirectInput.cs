using System;
using System.Text.Json;
using Terraria;
using CompanionBridge.Common.Systems;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

// One natural Player.Update per input update. No planner, support test or physics prediction.
internal sealed class ServerAiDirectInput : ActorControl.IMovementDriver
{
    internal sealed class Run
    {
        internal AiReadOnlyProtocol.Identity Identity;
        internal string ActionId, Horizontal, Vertical, Status="running", Reason="input_accepted", Detail;
        internal int MaxUpdates, JumpHoldUpdates, Updates, HealthStart, HealthEnd, BlockedHorizontalUpdates;
        internal StateVector StartPosition, EndPosition, EndVelocity;
        internal ulong AcceptedTick, ObservedTick;
        internal ulong? FirstInputTick, LastInputTick;
        internal bool InputReleased, Grounded, Dead;
    }
    internal Run Current;
    private bool executing, injected;
    public bool Running=>Current?.Status=="running";
    public bool Matches(Player p)=>Current!=null&&ReferenceEquals(Current.Identity.Actor,p);
    internal Run Start(AiReadOnlyProtocol.Identity identity,JsonElement args,string actionId)
    {
        ServerAiNativeActions.Require(identity);
        AtomicParameters.Validate("move_input",args,false);
        if(ActorControl.Running||ActorItemUse.Running||ActorMining.Running)throw new InvalidOperationException("Actor busy.");
        var p=(Player)identity.Actor;
        Current=new Run{Identity=identity,ActionId=actionId,Horizontal=args.GetProperty("horizontal").GetString(),
            Vertical=args.GetProperty("vertical").GetString(),MaxUpdates=args.GetProperty("maxUpdates").GetInt32(),
            JumpHoldUpdates=args.GetProperty("jumpHoldUpdates").GetInt32(),StartPosition=new(p.position.X,p.position.Y),
            HealthStart=p.statLife,AcceptedTick=Main.GameUpdateCount};
        Observe(p);
        return Current;
    }
    private static bool SupportedState(Player p)=>!p.mount.Active&&p.gravDir==1&&!p.frozen&&!p.stoned&&!p.webbed&&!p.tongued&&!p.shimmering
        &&!p.isControlledByFilm&&!p.sleeping.isSleeping&&!p.sitting.isSitting;
    public void BeforeMovement(Player p)
    {
        if(!executing||!Running||!Matches(p)||!NativeSurvival.Owns(p))throw new InvalidOperationException("No owned direct-input scope.");
        ServerAiNativeActions.Require(Current.Identity);
        if(!SupportedState(p)){Stop("failed","unsupported_actor_state");return;}
        if(injected)throw new InvalidOperationException("Duplicate direct-input boundary.");
        p.controlLeft=Current.Horizontal=="left";p.controlRight=Current.Horizontal=="right";
        p.controlUp=Current.Vertical=="up";p.controlDown=Current.Vertical=="down";
        p.controlJump=Current.Updates<Current.JumpHoldUpdates;
        p.controlUseItem=p.controlUseTile=p.channel=false;
        ServerAiDirectInputFixture.Input(p);
        Current.FirstInputTick??=Main.GameUpdateCount;Current.LastInputTick=Main.GameUpdateCount;
        Current.Updates++;injected=true;
    }
    private bool Release()
    {
        if(!ServerAiNativeActions.Owned(Current?.Identity))return false;
        var p=(Player)Current.Identity.Actor;
        p.controlLeft=p.controlRight=p.controlUp=p.controlDown=p.controlJump=p.controlUseItem=p.controlUseTile=p.channel=false;
        return true; // Deliberately preserve velocity, jump counter and native releaseJump latch.
    }
    private void Observe(Player p)
    {
        Current.EndPosition=new(p.position.X,p.position.Y);Current.EndVelocity=new(p.velocity.X,p.velocity.Y);
        Current.HealthEnd=p.statLife;Current.Dead=p.dead;
        Current.Grounded=TerrainReader.Grounded(p);Current.ObservedTick=Main.GameUpdateCount;
    }
    internal void Stop(string status,string reason)
    {
        if(Current==null||!Running)return;
        Current.InputReleased=Release();
        Current.Status=Current.InputReleased?status:"unknown";
        Current.Reason=Current.InputReleased?reason:"direct_input_binding_changed";
        if(Current.InputReleased)
        {
            try
            {
                var p=(Player)Current.Identity.Actor;Observe(p);
                if(!p.dead)NetworkStateSync.Full(p);
            }
            catch(Exception e){Current.Status="unknown";Current.Reason="direct_input_observation_unconfirmed";Current.Detail=e.Message;}
        }
        ServerAiDirectInputFixture.Result(new{Current.ActionId,Current.Status,Current.Reason,evidence=Evidence()});
        try{ProbeSession.Log("atomic-direct-input-result",new{Current.ActionId,Current.Status,Current.Reason,evidence=Evidence()});}
        catch{ /* Diagnostics cannot change input ownership or terminal status. */ }
    }
    public void Retire(Player p){if(Matches(p)&&Running)Stop("cancelled",p.dead?"actor_died":"actor_retired");}
    internal void ObserveDeath(Player p)
    {
        if(Current.InputReleased&&p.dead&&!Current.Dead)
        {Observe(p);Current.Reason="actor_died";}
    }
    internal object Evidence()=>new{
        actionId=Current.ActionId,requestedInputs=new{horizontal=Current.Horizontal,vertical=Current.Vertical,
            jumpHoldUpdates=Current.JumpHoldUpdates,maxUpdates=Current.MaxUpdates},
        updatesRequested=Current.MaxUpdates,updatesExecuted=Current.Updates,startPosition=Current.StartPosition,
        endPosition=Current.EndPosition,endVelocity=Current.EndVelocity,grounded=Current.Grounded,airborne=!Current.Grounded,
        healthStart=Current.HealthStart,healthEnd=Current.HealthEnd,healthDelta=Current.HealthEnd-Current.HealthStart,dead=Current.Dead,
        inputReleased=Current.InputReleased,settled=Current.Grounded&&Math.Abs(Current.EndVelocity.X)<=0.1f,
        noProgress=Math.Abs(Current.EndPosition.X-Current.StartPosition.X)<0.5f&&Math.Abs(Current.EndPosition.Y-Current.StartPosition.Y)<0.5f,
        blockedHorizontalUpdates=Current.BlockedHorizontalUpdates,acceptedGameUpdate=Current.AcceptedTick,firstInputGameUpdate=Current.FirstInputTick,
        lastInputGameUpdate=Current.LastInputTick,observedGameUpdate=Current.ObservedTick,effectStatus="native_input_observed"};
    public void Update(On_Player.orig_Update orig,Player p,int slot)
    {
        if(!Running||!Matches(p)){orig(p,slot);return;}
        ProbeSession.Reservation.AssertOwner(Program.IsMainThread);
        lock(ProbeSession.Reservation.Gate)
        {
            if(!ServerAiNativeActions.Owned(Current.Identity)){Stop("unknown","direct_input_binding_changed");orig(p,slot);return;}
            try
            {
                if(executing||slot!=p.whoAmI||Current.LastInputTick.HasValue&&Main.GameUpdateCount<=Current.LastInputTick.Value)
                    throw new InvalidOperationException("Duplicate/reentrant direct-input update.");
                if(p.dead||p.ghost){Stop("cancelled","actor_died");orig(p,slot);return;}
                if(Main.gameMenu||Netplay.Disconnect){Stop("cancelled","world_unavailable");orig(p,slot);return;}
                if(!SupportedState(p)){Stop("failed","unsupported_actor_state");orig(p,slot);return;}
                // Do not inject past native incapacitation controls. Airborne motion and knockback remain allowed.
                executing=true;injected=false;
                float beforeX=p.position.X;
                orig(p,slot);
                if(!ServerAiNativeActions.Owned(Current.Identity)){Stop("unknown","direct_input_binding_changed");return;}
                if(!Running)return; // Death/retirement hook may have ended the run.
                Observe(p);
                if(p.dead||p.ghost){Stop("cancelled","actor_died");return;}
                if(!injected)throw new InvalidOperationException("Audited movement boundary not reached.");
                if(!float.IsFinite(p.position.X)||!float.IsFinite(p.position.Y)||!float.IsFinite(p.velocity.X)||!float.IsFinite(p.velocity.Y))
                    throw new InvalidOperationException("Non-finite native physics state.");
                if(Current.Horizontal!="none"&&Math.Abs(p.position.X-beforeX)<0.01f)Current.BlockedHorizontalUpdates++;
                if(Current.Updates>=Current.MaxUpdates)Stop("completed","input_duration_elapsed");
                if(Running)NetworkStateSync.Tick(p);
            }
            catch(Exception e){Current.Detail=e.Message;Stop("unknown","direct_input_update_unconfirmed");}
            finally{Release();executing=injected=false;}
        }
    }
}
