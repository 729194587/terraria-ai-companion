using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using CompanionBridge.Motion;
using CompanionBridge.Common.Systems;

namespace CompanionBridge.ServerIpc;

// Short-horizon local threats. NPC affiliations and attackability do not decide contact risk.
internal sealed class BossEvasion
{
    internal const int Horizon=48;
    private const int MaxThreats=12;
    internal int FallbackSamples {get;private set;}
    internal int CustomTrajectorySamples {get;private set;}
    internal int Truncated {get;private set;}
    internal double WorstPoseError {get;private set;}
    internal int PreferredDirection {get;set;}
    private bool predicted;
    internal void After(Player p)
    {
        if(predicted)WorstPoseError=Math.Max(WorstPoseError,Vector2.Distance(p.position,new(expected.Px,expected.Py)));
        predicted=false;
    }
    private readonly List<Threat> threats=new();
    private Prediction path;
    private int cursor;
    private PhysicsState expected;
    internal string Reason {get;private set;}
    internal int Decisions {get;private set;}
    internal int NoRoute {get;private set;}

    private readonly record struct Threat(Vector2 Center,Vector2 Velocity,Vector2 Half,Projectile Beam,float Priority,float Gravity,int LastFrame=Horizon,bool IsProjectile=false)
    {
        internal bool Hits(Vector2 from,Vector2 to,int frame)
        {
            if(frame>LastFrame)return false;
            if(Beam!=null)
                return Beam.Colliding(Beam.Hitbox,new Rectangle((int)(to.X-10),(int)(to.Y-21),20,42));
            // Sweep the relative motion, including projectile sub-updates, against the expanded body.
            var a=from-Center-Velocity*(frame-1);
            var b=to-Center-Velocity*frame;
            a.Y-=Gravity*(frame-1)*frame*.5f;
            b.Y-=Gravity*frame*(frame+1)*.5f;
            return Collision.CheckAABBvLineCollision(-Half,Half*2,a,b);
        }
    }
    // Clip ordinary hostile trajectories at their first tile impact. Beams
    // use their native hitbox and non-colliding shots can pass through walls.
    private static int FlightFrames(Projectile q,int horizon,float gravity)
    {
        int updates=q.MaxUpdates;
        int limit=Math.Min(horizon,(q.timeLeft+updates-1)/updates);
        if(!q.tileCollide)return limit;
        Vector2 pos=q.Center, velocity=q.velocity*updates;
        var profile=new CombatBallistics.CollisionProfile(q.width,q.height,updates,true,new(.5f));
        for(int frame=1;frame<=limit;frame++)
        {
            velocity.Y+=gravity;
            var step=velocity/updates;
            for(int i=0;i<updates;i++)
            {
                if(!CombatBallistics.ClearStep(pos,step,profile,dryOnly:false))return frame-1;
                pos+=step;
            }
        }
        return limit;
    }

    internal static Projectile Incoming(Player p,Func<Projectile,bool> allowed)
    {
        foreach(var q in Main.ActiveProjectiles)
        {
            if(!q.hostile || q.friendly || q.damage<=0)continue;
            var speed=q.velocity*q.MaxUpdates;
            float gravity=BossExtensions.Gravity(q.type);
            var beam=q.type==ProjectileID.PhantasmalDeathray?q:null;
            var threat=new Threat(q.Center,speed,new((q.width+p.width)*.5f,(q.height+p.height)*.5f),beam,0,gravity);
            if(beam==null && Vector2.Distance(q.Center,p.Center)>threat.Half.Length()+
                (speed-p.velocity).Length()*Horizon+Math.Abs(gravity)*Horizon*(Horizon+1)/2)continue;
            for(int f=1;f<=Horizon;f++)
                if(threat.Hits(p.Center+p.velocity*(f-1),p.Center+p.velocity*f,f))
                {
                    if(FlightFrames(q,f,gravity)>=f && allowed(q))return q;
                    break;
                }
        }
        return null;
    }
    private bool platforms,canDrop,kite;
    private Vector2 target;
    private static bool PlatformAt(Player p,int offset)
    {
        int row=(int)MathF.Round(p.Bottom.Y/16)+offset;
        int left=(int)MathF.Floor(p.position.X/16),right=(int)MathF.Ceiling(p.Right.X/16);
        for(int x=left;x<right;x++)
            if(TerrainReader.ReadCell(x,row)!='=')return false;
        return true;
    }
    internal bool Needed(Player p,NPC enemy,bool ranged,bool bossEncounter,bool reflex=false)
    {
        canDrop=p.velocity.Y==0 && Math.Abs(p.Bottom.Y-MathF.Round(p.Bottom.Y/16)*16)<.5f && PlatformAt(p,0);
        platforms=canDrop;
        for(int offset=1;offset<=6 && !platforms;offset++)platforms=PlatformAt(p,-offset);
        target=enemy==null?p.Center:enemy.Center+enemy.velocity*8;
        kite=bossEncounter && ranged && platforms && Vector2.Distance(enemy.Center,p.Center)<260;
        threats.Clear();
        void Add(Vector2 c,Vector2 v,int w,int h,Projectile beam=null,float gravity=0,Projectile shot=null,NPC body=null)
        {
            var half=new Vector2((w+p.width)*.5f,(h+p.height)*.5f);
            if(beam!=null || Vector2.DistanceSquared(c,p.Center)<=MathF.Pow(120+half.Length()+v.Length()*Horizon+Math.Abs(gravity)*Horizon*(Horizon+1)/2,2))
            {
                if(reflex && body!=null && !NativeCombatWorld.ContactPath(p,body))return;
                if(gravity!=0)CustomTrajectorySamples++;
                var delta=c-p.Center;
                var relative=v-p.velocity;
                float time=Math.Clamp(-Vector2.Dot(delta,relative)/Math.Max(.001f,relative.LengthSquared()),0,Horizon);
                float miss=(delta+time*relative).Length()-half.Length();
                float priority=beam!=null?-1:Math.Max(0,miss)+time;
                int frames=reflex && shot!=null?FlightFrames(shot,Horizon,gravity):Horizon;
                if(frames<=0)return;
                var threat=new Threat(c,v,half,beam,priority,gravity,frames,shot!=null);
                if(threats.Count<MaxThreats){threats.Add(threat);return;}
                Truncated++;
                int worst=0;
                for(int i=1;i<threats.Count;i++)if(threats[i].Priority>threats[worst].Priority)worst=i;
                if(priority<threats[worst].Priority)threats[worst]=threat;
            }
        }
        foreach(var n in Main.ActiveNPCs)
            if(!n.friendly&&!n.townNPC&&n.damage>0&&n.life>0)
                Add(n.Center,n.velocity,n.width,n.height,gravity:reflex?NativeCombatWorld.JumpGravity(n):0,body:n);
        foreach(var q in Main.ActiveProjectiles)
            if(q.hostile&&q.damage>0&&(!reflex || !q.friendly))
            {
                if(BossExtensions.Conflicted(q.type))FallbackSamples++;
                Add(q.Center,q.velocity*q.MaxUpdates,q.width,q.height,
                q.type==ProjectileID.PhantasmalDeathray?q:null,BossExtensions.Gravity(q.type),q);
            }
        if(path!=null && cursor<path.Frames.Count)return true;
        // Ordinary melee should still close to striking distance between imminent threats.
        foreach(var threat in threats)
            for(int f=1;f<=(bossEncounter || reflex && threat.IsProjectile ? Horizon : ReflexContact.Horizon);f++)
                if(threat.Hits(p.Center+p.velocity*(f-1),p.Center+p.velocity*f,f))return true;
        if(kite)return true;
        Reason=null;
        return false;
    }
    private int Exposure(Vector2 center,Prediction candidate,int offset=0)
    {
        int cost=0;
        for(int f=1;f<=Horizon;f++)
        {
            var state=candidate.Frames[Math.Min(offset+f-1,candidate.Frames.Count-1)].State;
            var next=new Vector2(state.Px+10,state.Py+21);
            foreach(var threat in threats)
                if(threat.Hits(center,next,f)){cost+=Horizon+1-f;break;}
            center=next;
        }
        return cost;
    }
    internal PhysicsInput Before(Player p)
    {
        var state=NativeMotionWorld.Observe(p);
        var terrain=new NativeMotionWorld(state);
        var physics=NativeNavigation.Parameters(p);
        Prediction incumbent=null;
        int incumbentRisk=int.MaxValue;
        int incumbentCursor=cursor;
        if(path!=null && cursor<path.Frames.Count && (!platforms || !state.Grounded || cursor%16!=0))
        {
            var step=path.Frames[cursor];
            try
            {
                var next=PhysicsSimulator.Step(state,step.Input,physics,terrain);
                if(Math.Abs(next.Px-step.State.Px)<.1f && Math.Abs(next.Py-step.State.Py)<.1f
                    && (path.ExtraJumpFrame<0 || physics.ExtraJump!=null && ExtraJumpModel.MatchesResource(step.State,next,true))
                    && (path.AirStartFrame<0 || physics.Air?.Kind==AirKind.Wings && AirMotionModel.Matches(step.State,next,true))
                    && next.Grounded==step.State.Grounded && terrain.SafeSweep(state,next,out _))
                {
                    int risk=Exposure(p.Center,path,cursor);
                    if(risk==0)return Advance();
                    // Keep the timed release/press sequence when replanning cannot lower exposure.
                    if(path.ExtraJumpFrame>=0 || path.AirStartFrame>=0){incumbent=path;incumbentRisk=risk;}
                }
            }
            catch(UnsafeMotionException){ /* Discard the invalidated trajectory. */ }
        }
        Decisions++;
        path=null;
        int best=int.MaxValue;
        float bestPosition=float.MaxValue;
        void Consider(int direction,int hold,bool down,int extraFrame=-1,int flightFrame=-1,int flightHold=0)
        {
            var candidate=PhysicsSimulator.Predict(state,direction,hold,physics,terrain,direction==0?0:platforms?32:18,
                extraJumpFrame:extraFrame,airStartFrame:flightFrame,airHoldFrames:flightHold,allowStationaryHop:true,drop:down);
            int cost=candidate.Safe?Exposure(p.Center,candidate):int.MaxValue;
            ServerCombatFixture.Candidate(Decisions,state,direction,hold,candidate,cost);
            if(!candidate.Safe || cost>best)return;
            // At equal exposure, spend mobility only for a better firing position.
            float position=(extraFrame>=0?1:0)+flightHold*.15f;
            if(platforms)
            {
                var end=candidate.Landing;
                float gap=Math.Abs(end.Px+10-target.X);
                // Equal-risk paths keep a firing lane and separate vertically from the attacker.
                if(kite)position+=Math.Max(0,220-gap)+Math.Max(0,gap-360);
                position+=Math.Max(0,90-Math.Abs(end.Py+21-target.Y))*.55f;
            }
            if(cost==best && position>=bestPosition)return;
            best=cost;bestPosition=position;path=candidate;
        }
        foreach(int hold in new[]{0,8,Math.Max(9,physics.JumpHeight)})
            foreach(int direction in new[]{0,PreferredDirection,-PreferredDirection})
            {
                if(hold>0 && !state.Grounded && (physics.ExtraJump!=null || physics.Air?.Kind==AirKind.Wings))continue;
                Consider(direction,hold,false);
                if(best==0 && bestPosition==0)goto selected;
            }
        if(canDrop && state.Grounded && !state.Wet)
            foreach(int direction in new[]{0,PreferredDirection,-PreferredDirection})Consider(direction,0,true);
        if(!state.Wet && (best!=0 || bestPosition>0))
        {
            int hold=state.Grounded?physics.JumpHeight:0;
            int start=state.Grounded?hold+2:state.ReleaseJump?0:1; // Release jump before a new airborne press.
            foreach(int direction in new[]{PreferredDirection,0,-PreferredDirection})
            {
                if(physics.ExtraJump!=null && (state.Grounded || state.ExtraAvailable && !state.ExtraUsed))
                    foreach(int delay in new[]{0,6})Consider(direction,hold,false,extraFrame:start+delay);
                if(physics.Air?.Kind==AirKind.Wings && state.Air?.WingTime>0)
                    foreach(int duration in new[]{12,28})Consider(direction,hold,false,flightFrame:start,flightHold:duration);
            }
        }
        selected:
        if(incumbent!=null && incumbentRisk<=best)
        {
            path=incumbent;cursor=incumbentCursor;
            return Advance();
        }
        ServerCombatFixture.ArenaPlan(path);
        cursor=0;
        Reason=best!=0?"no_safe_route":path.ExtraJumpFrame>=0?"cloud_jump":path.AirStartFrame>=0?"wing_flight":path.Frames[0].Input.Down?"platform_drop":
            platforms && path.Landing.Py<state.Py-8?"platform_jump":kite?"platform_spacing":"evade";
        if(best!=0)NoRoute++;
        return path==null?default:Advance();
    }
    private PhysicsInput Advance()
    {
        var next=path.Frames[cursor++];
        expected=next.State;
        predicted=true;
        return next.Input;
    }
}
