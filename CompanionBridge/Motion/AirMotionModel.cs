using System;
using System.Linq;

namespace CompanionBridge.Motion;

internal enum AirKind { None, Wings, RocketBoots }
internal enum AirPhase { None, Flying, Gliding, Descending, RocketPulse }
internal sealed record AirResources(float WingTime,int WingMax,int RocketTime,int RocketMax,
    int Delay,bool CanRocket,bool RocketRelease,bool RocketFrame,int Boots=0);
internal sealed record AirFeedback(AirKind Kind,AirPhase Phase,bool Confirmed,int Frame,
    AirResources Expected,AirResources Observed)
{
    public string Motion=>Phase.ToString();
}

// Two pinned native models, not a general special-movement framework.
internal sealed record AirMotionModel(AirKind Kind)
{
    internal static readonly AirMotionModel Angel=new(AirKind.Wings),Rocket=new(AirKind.RocketBoots);
    internal static readonly WingBaseSnapshot AngelStats=new(100,6.25f,1,false,-1,1);
    internal static AirKind Strategy(MovementSnapshot m)=>m.Wings.FunctionalSlot!=0?AirKind.Wings:
        m.Rockets.Boots!=0?AirKind.RocketBoots:AirKind.None;
    internal static AirMotionModel Select(MovementSnapshot m)
    {
        if(!m.BlockExtraJumps&&m.ExtraJumps.Any(j=>j.Enabled)||m.ActiveExtraJump)return null;
        return Strategy(m) switch
        {
            // Slot 1 is the original pinned profile. Native Angel Wings (item 493) report slot 2.
            AirKind.Wings when m.Wings.FunctionalSlot is 1 or 2&&m.Wings.BaseStats==AngelStats&&m.Rockets.Delay==0=>Angel,
            AirKind.RocketBoots when m.Rockets.Boots==1=>Rocket,
            _=>null
        };
    }
    internal static AirResources Resources(MovementSnapshot m)=>new(m.Wings.Time,m.Wings.TimeMax,
        m.Rockets.Time,m.Rockets.TimeMax,m.Rockets.Delay,m.Rockets.CanRocket,m.Rockets.Release,m.Rockets.Frame,m.Rockets.Boots);
    internal static bool Matches(PhysicsState expected,PhysicsState actual,bool boundary=false)
    {
        var e=expected.Air;var a=actual.Air;
        if(e==null||a==null)return e==a;
        bool refresh=boundary&&expected.Grounded&&actual.Grounded&&!actual.ExtraActive;
        return e.Boots==a.Boots&&e.WingMax==a.WingMax&&e.RocketMax==a.RocketMax
            &&(Math.Abs(e.WingTime-a.WingTime)<.001f||refresh&&a.WingTime==a.WingMax)
            &&(e.RocketTime==a.RocketTime||refresh&&a.RocketTime==a.RocketMax)
            &&e.Delay==a.Delay&&e.CanRocket==a.CanRocket
            &&e.RocketRelease==a.RocketRelease&&e.RocketFrame==a.RocketFrame
            &&expected.JumpFramesLeft==actual.JumpFramesLeft&&expected.ReleaseJump==actual.ReleaseJump;
    }
    internal bool Valid(AirResources r)=>r!=null&&float.IsFinite(r.WingTime)&&r.WingTime>=-1
        &&r.WingMax>=0&&r.RocketTime>=0&&r.RocketMax>=0&&r.Delay>=0&&r.Delay<=10;

    // Called after ordinary JumpMovement and before collision. Active flight /
    // rocket pulses bypass ordinary gravity in the native Update branch.
    internal (float Vy,AirResources Resources,AirPhase Phase) Step(PhysicsState s,PhysicsInput input,
        PhysicsParameters ph,float vy,int jump,bool launched)
    {
        var r=s.Air??throw new UnsafeMotionException("missing native air resources");
        float wing=r.WingTime;int fuel=r.RocketTime,delay=r.Delay;
        bool can=r.CanRocket,release=r.RocketRelease,frame=r.RocketFrame;
        if(launched){can=false;release=false;}
        if(!input.Jump)release=true;
        if(r.Boots==0)fuel=0;
        if(vy>-ph.JumpSpeed&&vy!=0)can=true;
        if(vy==0&&!input.Jump)wing=r.WingMax;
        bool flying=Kind==AirKind.Wings&&input.Jump&&wing>0&&jump==0&&vy!=0;
        if(flying)
        {
            vy-=.1f;
            if(vy>0)vy-=.5f;
            else if(vy>-ph.JumpSpeed*.5f)vy-=.1f;
            vy=Math.Max(vy,-ph.JumpSpeed*1.5f);wing-=1;
        }
        // Vanilla converts fuel even when no powered wing movement occurred.
        // This is still wing strategy: never request a rocket pulse with wings.
        if(Kind==AirKind.Wings&&r.Boots!=0&&vy!=0&&fuel>0)
        {int bonus=fuel*6;wing=Math.Min(wing+bonus,r.WingMax+bonus);fuel=0;}
        if(vy==0)fuel=r.RocketMax;
        if(Kind==AirKind.RocketBoots&&input.Jump&&delay==0&&can&&release)
        {
            if(fuel>0){fuel--;delay=10;}
            else can=false;
        }
        if(delay==0)frame=false;
        bool pulse=delay>0;
        if(pulse)
        {
            frame=true;delay--;
            vy-=.1f;
            if(vy>0)vy-=.5f;
            else if(vy>-ph.JumpSpeed*.5f)vy-=.1f;
            vy=Math.Max(vy,-ph.JumpSpeed*1.5f);
        }
        var phase=flying?AirPhase.Flying:pulse?AirPhase.RocketPulse:AirPhase.Descending;
        if(!flying&&!pulse)
        {
            if(Kind==AirKind.Wings&&input.Jump&&vy>0)
            {vy=Math.Min(vy+ph.Gravity/3,ph.MaxFall/3);phase=AirPhase.Gliding;}
            else vy=Math.Min(vy+ph.Gravity,ph.MaxFall);
        }
        return(vy,new(wing,r.WingMax,fuel,r.RocketMax,delay,can,release,frame,r.Boots),phase);
    }

    // Bounded feedback correction: simulate the complete remaining suffix, never
    // accept an air steering change without its safe settled landing tail.
    internal static Prediction Correct(Prediction plan,int at,PhysicsState actual,PhysicsParameters ph,IMotionWorld world,double budgetMs=3)
    {
        if(ph.Air?.Kind!=AirKind.Wings||plan.AirStartFrame<0||at<=0||actual.Grounded
            ||plan.ExecuteFrames!=plan.Frames.Count)return plan;
        var old=plan.Frames[at-1].State;
        float drift=Math.Max(Math.Abs(old.Px-actual.Px),Math.Abs(old.Py-actual.Py));
        if(drift<.05f||drift>6||!Matches(old,actual,true))return plan;
        var timer=System.Diagnostics.Stopwatch.StartNew();
        Prediction best=null;float score=float.PositiveInfinity;
        var intended=plan.Frames[at].Input;
        int direction=Math.Sign(plan.Landing.Px-actual.Px);
        foreach(var candidate in new[]{intended,new PhysicsInput(direction,intended.Jump),new PhysicsInput(direction,false),new PhysicsInput(direction,true)})
        {
            var frames=new System.Collections.Generic.List<PredictedFrame>(plan.Frames.GetRange(0,at));
            var s=actual;bool safe=true;
            try
            {
                for(int i=at;i<plan.Frames.Count;i++)
                {
                    if(timer.Elapsed.TotalMilliseconds>=budgetMs)return best??plan;
                    var input=i<at+4?candidate:plan.Frames[i].Input;
                    var next=PhysicsSimulator.Step(s,input,ph,world);
                    if(!world.SafeSweep(s,next,out _)||next.Py-actual.Py>FallSafety.LookaheadPixels){safe=false;break;}
                    frames.Add(new(input,next));s=next;
                }
                if(!safe||frames.Count<2||!PhysicsSimulator.Settled(s,world)||!PhysicsSimulator.Settled(frames[^2].State,world)
                    ||Math.Abs(s.Px-plan.Landing.Px)>8||Math.Abs(s.Py-plan.Landing.Py)>4)continue;
                float cost=Math.Abs(s.Px-plan.Landing.Px)+Math.Abs(s.Py-plan.Landing.Py)*2;
                if(cost<score){score=cost;best=plan with{Frames=frames,Landing=s};}
            }
            catch(UnsafeMotionException){ }
        }
        return best??plan;
    }
}
