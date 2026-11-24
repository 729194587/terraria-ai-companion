using System;
using System.Linq;
using CompanionBridge.Motion;
using Microsoft.Xna.Framework;
using Terraria;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

// The grapple phase owns a single native projectile. Only the unhooked
// movement tail is predicted; attached movement always runs in Terraria.
internal sealed class ServerGrappleClimb(GrappleClimbPlan plan,PhysicsParameters physics,Item item,Func<PhysicsState,bool> allowed=null)
{
    private enum Phase { Approach,Fire,Latch,Pull,Detach,Tail,Settle }
    private Phase phase=plan.Approach==null?Phase.Fire:Phase.Approach;
    private bool approaching;
    private ActorGrapple shot;
    private readonly LocalMotionController tail=new();
    private readonly float shootSpeed=item.shootSpeed;
    private int frames,deadline=35+(plan.Approach?.ExecuteFrames??0),stationary,settled;
    private float lastY=float.PositiveInfinity;
    private int life;
    internal string Failure { get; private set; }
    internal bool Complete => settled>=2;
    internal bool Unconfirmed { get; private set; }
    internal bool HasNativeEntry(Player p)=>shot?.HasNativeEntry(p)==true;
    internal bool WantsNativePull(Player p) => Failure==null&&phase is Phase.Latch or Phase.Pull or Phase.Detach
        &&shot?.Attached(p)==true;

    internal PhysicsInput Before(Player p,NativeNavigation navigation,int session,int generation,bool nativePull)
    {
        if(Failure!=null)return default;
        var state=NativeMotionWorld.Observe(p);
        var world=new NativeMotionWorld(state);
        bool attached=WantsNativePull(p);
        if(!Valid(p,state,world)||(life!=0&&p.statLife<life))
            return Fail("grapple_conditions_changed");
        // Life regeneration changes the damage diagnostic, not the movement
        // profile. Damage and fall immunity still have their own checks.
        var currentPhysics=physics with{Fall=physics.Fall with{Life=p.statLife}};
        if(!nativePull&&!attached&&(!navigation.Ordinary(p)||NativeNavigation.Parameters(p)!=currentPhysics))
            return Fail("grapple_mobility_changed");
        if(phase is Phase.Latch or Phase.Pull && shot?.Active==null)return Fail("grapple_projectile_lost");
        if(phase==Phase.Pull&&shot.Active.ai[0]!=2)return Fail("grapple_attachment_lost");
        if(!nativePull&&attached)return default; // GrappleMovement owns this frame's release input.
        if(nativePull)
        {
            if(phase==Phase.Latch)
            {
                if(shot.Active is not {} projectile||Math.Abs(projectile.Center.X-plan.AimX)>8
                    ||Math.Abs(projectile.Center.Y-plan.AimY)>8)return Fail("grapple_wrong_anchor");
                phase=Phase.Pull;deadline=frames+42;shot.Sync();Log("latched",state);
            }
            float feet=state.Py+PhysicsSimulator.PlayerH;
            if(feet<=plan.Landing.SurfaceY+GrappleClimbPlan.ReleaseBelowShelf&&p.releaseJump&&state.Vy<-.1f)
            {
                // Preview only the native release tick using the measured pull
                // velocity and its audited +0.01 detach adjustment. Replan
                // from the actual result after Terraria has removed the hook.
                var release=world.Move(state,state.Vx,state.Vy+.01f,1) with {Air=state.Air};
                if(world.SafeSweep(state,release,out _)&&plan.Tail(release,physics,world) is {} preview
                    &&(allowed==null||!preview.Frames.Exists(f=>!allowed(f.State))))
                {
                    phase=Phase.Detach;
                    return new(0,true);
                }
                if(feet<plan.Landing.SurfaceY+40)return Fail("grapple_no_safe_release");
            }
        }
        if(phase==Phase.Approach)
        {
            if(!approaching)
            {
                tail.Begin(session,plan.Approach.Frames[0].Input.Direction,generation);
                tail.LoadPlan(plan.Approach);
                approaching=true;
            }
            var input=tail.Before(session,plan.Approach.Frames[0].Input.Direction,state,physics,world,generation);
            return tail.Terminal?Fail("grapple_approach_invalidated"):input;
        }
        if(phase==Phase.Tail)
        {
            var input=tail.Before(session,plan.Direction,state,physics,world,generation);
            if(tail.Terminal)return Fail("grapple_tail_invalidated: "+tail.Snapshot.Reason);
            return input;
        }
        return default;
    }

    internal void After(AiReadOnlyProtocol.Identity identity,int session,int generation)
    {
        var p=(Player)identity.Actor;
        var state=NativeMotionWorld.Observe(p);
        var world=new NativeMotionWorld(state);
        if(Failure!=null)return;
        if(++frames>deadline||frames>145+(plan.Approach?.ExecuteFrames??0)){Fail("grapple_phase_timeout");return;}
        if(!Valid(p,state,world)||(life!=0&&p.statLife<life)){Fail("grapple_conditions_changed");return;}
        life=p.statLife;
        switch(phase)
        {
            case Phase.Approach:
                if(!approaching)break;
                tail.After(state,world);
                if(tail.Terminal){Fail("grapple_approach_deviation");break;}
                if(tail.NeedsPlan)
                {
                    if(!PhysicsSimulator.Settled(state,world) || Math.Abs(state.Px-plan.Approach.Landing.Px)>6
                        || (plan with{Approach=null}).Preview(state,physics,world,allowed)==null)
                    {Fail("grapple_launch_not_settled");break;}
                    tail.Cancel();phase=Phase.Fire;
                }
                break;
            case Phase.Fire:
                if(!PhysicsSimulator.Settled(state,world)){Fail("grapple_launch_not_settled");break;}
                if(!GrappleClimbPlan.ClearShot(state,plan.AnchorX,plan.AnchorY,world,null))
                {Fail("grapple_firing_lane_changed");break;}
                shot=ActorGrapple.Fire(identity,item,new Vector2(plan.AimX,plan.AimY));
                if(shot==null){Fail("grapple_fire_refused");break;}
                phase=Phase.Latch;
                Log("fired",state);
                break;
            case Phase.Latch:
                if(shot.Active is not {} projectile||projectile.ai[0]==1)Fail("grapple_missed");
                break;
            case Phase.Pull:
                stationary=lastY-state.Py>.1f?0:stationary+1;
                lastY=state.Py;
                if(stationary>=5)Fail("grapple_pull_blocked");
                break;
            case Phase.Detach:
                // A release input is not proof that the engine removed it.
                if(shot.Active!=null){Fail("grapple_native_detach_unconfirmed");break;}
                if(!Release()){Fail("grapple_cleanup_unconfirmed");break;}
                Log("detached",state);
                var prediction=plan.Tail(state,physics,world);
                if(prediction==null||allowed!=null&&prediction.Frames.Exists(f=>!allowed(f.State))){Fail("grapple_no_safe_handoff");break;}
                tail.Begin(session,plan.Direction,generation);
                tail.LoadPlan(prediction);
                phase=Phase.Tail;deadline=frames+prediction.Frames.Count+8;
                break;
            case Phase.Tail:
                tail.After(state,world);
                if(tail.Terminal){Fail("grapple_tail_deviation: "+tail.Snapshot.Reason);break;}
                if(tail.NeedsPlan)phase=Phase.Settle;
                break;
            case Phase.Settle:
                if(!ActorGrapple.HasHook(p)&&plan.Landing.Arrived(state,world))settled++;
                else settled=0;
                if(Complete)Log("settled",state);
                break;
        }
    }

    private bool Valid(Player p,PhysicsState state,NativeMotionWorld world)
    {
        var movement=NativeMovementReader.Capture(p);
        return (allowed==null||allowed(state))&&!p.dead&&!p.ghost&&!p.noItems&&!p.CCed&&!p.tongued&&!p.mount.Active&&p.gravDir==1
        &&!p.wet&&!p.pulley&&!p.webbed&&!p.stoned&&!p.frozen&&!p.sliding&&!p.slowFall
        &&AirMotionModel.Select(movement)==physics.Air&&p.rocketBoots==0
        &&ExtraJumpModel.Select(movement)==physics.ExtraJump
        &&!movement.ExtraJumps.Any(j=>j.Active||j.Enabled&&physics.ExtraJump==null)
        &&p.width==PhysicsSimulator.PlayerW&&p.height==PhysicsSimulator.PlayerH
        &&ReferenceEquals(ActorGrapple.Selected(p),item)&&item.shootSpeed==shootSpeed
        &&plan.Unchanged(world)&&world.SafeSweep(state,state,out _)
        &&(shot!=null||!ActorGrapple.HasHook(p));
    }

    private PhysicsInput Fail(string reason){Failure=reason;return default;}
    internal bool Release()
    {
        if(shot==null)return !Unconfirmed;
        var owned=shot;shot=null;
        try{Unconfirmed=!owned.Release();}
        catch(Exception e){Unconfirmed=true;Failure??="grapple_cleanup_exception: "+e.Message;}
        return !Unconfirmed;
    }

    internal void Log(string stage,PhysicsState state)
    {
        var detail=new{stage,phase=phase.ToString(),frames,Failure,Unconfirmed,
            x=state.Px,y=state.Py,vx=state.Vx,vy=state.Vy,
            plan.AnchorX,plan.AnchorY,index=shot?.Index,identity=shot?.Identity,owner=shot?.Owner};
        ProbeSession.Log("navigation-grapple",detail);
        ServerGrappleFixture.Record(detail);
    }
}
