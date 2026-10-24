using System;

namespace CompanionBridge.Motion;

// A bounded native Down-input action, not a new planner or a position/velocity edit.
internal sealed class PlatformDropAction
{
    internal const int MaxDropPixels = 64;
    internal string Status { get; private set; } = "idle";
    internal string Reason { get; private set; }
    internal bool Down { get; private set; }
    internal float StartFeetY { get; private set; }
    internal float TargetSurfaceY { get; private set; }
    private float _x;
    private int _life, _settled;
    private bool _departed;
    internal void Begin(PhysicsState start, int life, bool supported, Func<int,int,char> cell)
    {
        Status = "running"; Reason = "started"; Down = false; _departed = false; _settled = 0;
        _x = start.Px; _life = life; StartFeetY = start.Py + PhysicsSimulator.PlayerH;
        if (!supported || !start.Grounded || Math.Abs(start.Vx) > 0.1f || Math.Abs(start.Vy) > 0.1f
            || !start.ReleaseJump || start.JumpFramesLeft != 0)
        { Fail("drop_requires_supported_settled_player"); return; }
        int row = (int)Math.Round(StartFeetY / 16);
        if (Math.Abs(StartFeetY - row*16) > LandingRegion.FeetTolerance || !MotionSupport.SafeLanding(start,cell))
        { Fail("drop_requires_horizontal_platform"); return; }
        float support = 0;
        for (int x=(int)Math.Floor(_x/16);x<(int)Math.Ceiling((_x+PhysicsSimulator.PlayerW)/16);x++)
        {
            char c = cell(x,row);
            if (c is not ('.' or '=')) { Fail("drop_requires_horizontal_platform"); return; }
            if(c=='=') support+=Math.Min(_x+PhysicsSimulator.PlayerW,(x+1)*16)-Math.Max(_x,x*16);
        }
        if(support < MotionSupport.MinimumSupportPixels) { Fail("drop_requires_horizontal_platform"); return; }
        for(int offset=16;offset<=MaxDropPixels;offset+=16)
        {
            float surface = row*16+offset;
            var target = start with { Py=surface-PhysicsSimulator.PlayerH };
            if (!CorridorClear(start.Py,surface,cell)) break;
            if (MotionSupport.SafeFlatLanding(target,cell))
            { TargetSurfaceY=surface; Down=true; return; }
        }
        Fail("no_safe_drop_landing_within_64_pixels");
    }
    private bool CorridorClear(float top, float surface, Func<int,int,char> cell)
    {
        for(int x=(int)Math.Floor(_x/16);x<(int)Math.Ceiling((_x+PhysicsSimulator.PlayerW)/16);x++)
            for(int y=(int)Math.Floor(top/16);y<(int)Math.Ceiling(surface/16);y++)
                if(cell(x,y) is not ('.' or '=')) return false;
        return true;
    }
    internal void Before(PhysicsState state, int life, Func<int,int,char> cell)
    {
        if(Status!="running") return;
        if(life<_life) { Fail("life_decreased"); return; }
        _life=life;
        if(Math.Abs(state.Px-_x)>1) { Fail("drop_horizontal_drift"); return; }
        var target=state with { Px=_x, Py=TargetSurfaceY-PhysicsSimulator.PlayerH, Vx=0,Vy=0,Grounded=true };
        if(!MotionSupport.SafeFlatLanding(target,cell) || !CorridorClear(Math.Min(state.Py,StartFeetY-PhysicsSimulator.PlayerH),TargetSurfaceY,cell))
        { Fail("drop_landing_or_corridor_changed"); return; }
        float feet=state.Py+PhysicsSimulator.PlayerH;
        _departed |= feet>StartFeetY+4;
        Down=!_departed; // Release after clearing the original platform; don't fall through the next one.
        if(feet>TargetSurfaceY+LandingRegion.FeetTolerance) Fail("drop_overshot_target_surface");
    }
    internal void After(PhysicsState state, int life, Func<int,int,char> cell)
    {
        Before(state,life,cell);
        if(Status!="running") return;
        bool landed=_departed && !Down && state.Grounded && Math.Abs(state.Vx)<=0.1f && Math.Abs(state.Vy)<=0.1f
            && Math.Abs(state.Py+PhysicsSimulator.PlayerH-TargetSurfaceY)<=LandingRegion.FeetTolerance
            && state.ReleaseJump && state.JumpFramesLeft==0 && MotionSupport.SafeFlatLanding(state,cell);
        _settled=landed?_settled+1:0;
        if(_settled>=2) { Status="completed"; Reason="lower_landing_verified"; Down=false; }
    }
    private void Fail(string reason) { Status="failed"; Reason=reason; Down=false; }
}
