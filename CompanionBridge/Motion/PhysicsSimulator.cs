// Adapted from Reisenbug/TerraBlind, Perception/PhysicsSimulator.cs (MIT).
// Pinned source and adaptation notes: ThirdParty/TerraBlind/NOTICE.md.
using System;
using System.Collections.Generic;

namespace CompanionBridge.Motion;

public sealed record NavigationAirImpulse(string Key, float HorizontalSpeed, float VerticalSpeed, int CooldownTicks)
{
    internal NavigationAirImpulse Next { get; init; }
}

internal sealed record ActionCooldown(string Key,int Remaining,ActionCooldown Next)
{
    internal static int Get(ActionCooldown list,string key)
    {
        for (;list!=null;list=list.Next) if (list.Key==key) return list.Remaining;
        return 0;
    }
    internal static ActionCooldown Advance(ActionCooldown entry)
    {
        if (entry==null) return null;
        var next=Advance(entry.Next);
        if (entry.Remaining <= 1) return next;
        if (entry.Remaining == int.MaxValue && next==entry.Next) return entry;
        return entry with {Next=next, Remaining=entry.Remaining==int.MaxValue?int.MaxValue:entry.Remaining-1};
    }
}

internal readonly record struct LaunchInput(int Hold,int ExtraFrame=-1,int AirStart=-1,int AirHold=0,
    int ImpulseFrame=-1,string ImpulseKey=null,int ExtraCount=1,int ExtraHold=-1);

internal sealed record PhysicsParameters(float AccRun, float MaxRun, float AccRunSpeed,
    float RunSlowdown, float Gravity, float MaxFall, float JumpSpeed, int JumpHeight,ExtraJumpModel ExtraJump=null,AirMotionModel Air=null,
    FallSafety Fall=default)
{
    // Player.Update initializes 10, then unconditionally adds 0.01 at IL_020d–021a
    // before our input hook. Match the effective value, not the reset constant.
    internal static readonly PhysicsParameters Basic = new(0.08f, 3, 3, 0.2f, 0.4f, 10.01f, 5.01f, 15);

    // Native pinned-engine boundary measurement: BuffID.Swiftness (3).
    internal static readonly PhysicsParameters Swiftness = new(0.1f, 3.75f, 3, 0.2f, 0.4f, 10.01f, 5.01f, 15);
    // The stock water profile is applied per frame without changing the route's
    // movement capabilities when the actor crosses a waterline.
    internal bool NoAirRepress { get; init; }
    internal bool GroundOnly { get; init; }
    internal NavigationAirImpulse Impulse { get; init; }
    internal bool CanWade { get; init; }
    internal const float WaterGravity=.2f, WaterMaxFall=5.01f, WaterJumpSpeed=6.01f;
    internal const int WaterJumpHeight=30;
    internal bool UsesWaterProfile=>Math.Abs(Gravity-WaterGravity)<.001f
        &&Math.Abs(MaxFall-WaterMaxFall)<.001f&&Math.Abs(JumpSpeed-WaterJumpSpeed)<.001f&&JumpHeight==WaterJumpHeight;
    internal IEnumerable<LaunchInput> Launches(PhysicsState pose,int direction,int holdStep)
    {
        yield return new(0);
        if (GroundOnly) yield break;
        // Long holds beyond the local prediction horizon cannot produce a settled edge.
        int maxHold=Math.Min(JumpHeight,180);
        for(int hold=0;hold<maxHold;hold+=holdStep) yield return new(Math.Min(hold+holdStep,maxHold));
        if (ExtraJump!=null && (pose.ExtraAvailable || pose.Grounded))
            foreach(var launch in ExtraJump.Launches(maxHold)) yield return launch;
        if (Air!=null)
            foreach(var launch in Air.Launches(maxHold,pose.Air)) yield return launch;
        if (direction==0) yield break;
        for(var impulse=Impulse;impulse!=null;impulse=impulse.Next)
            foreach(int delay in new[]{3,6})
            {
                int frame=maxHold+delay;
                if(ActionCooldown.Get(pose.Cooldowns,impulse.Key)<=frame)
                    yield return new(maxHold,ImpulseFrame:frame,ImpulseKey:impulse.Key);
            }
    }

    // Geometric bounds only. Each candidate must still simulate through landing and braking.
    internal float RiseBound(float wingTicks)
    {
        float coast = JumpSpeed * JumpSpeed / (2 * Gravity);
        float rise = JumpSpeed * JumpHeight + coast;
        for(var jump=ExtraJump;jump!=null;jump=jump.Next) rise += JumpSpeed * jump.HoldFrames(this) + coast;
        if (Air != null) rise += JumpSpeed * (Air.Kind == AirKind.Wings ? Math.Clamp(wingTicks, 0, 120) * Air.RiseLimit : 16);
        float impulseRise=0;
        for(var impulse=Impulse;impulse!=null;impulse=impulse.Next)
            if(impulse.VerticalSpeed<0) impulseRise=Math.Max(impulseRise,impulse.VerticalSpeed*impulse.VerticalSpeed/(2*Gravity));
        return Math.Clamp(rise + impulseRise + 16, 16, 1024);
    }
    internal static int PlanningCooldown(int remaining) => remaining > 180 ? int.MaxValue : remaining;
    internal int ReachX(float wingTicks)
    {
        float ticks = 2 * JumpSpeed / Gravity + JumpHeight;
        for(var jump=ExtraJump;jump!=null;jump=jump.Next) ticks += jump.HoldFrames(this) + JumpSpeed / Gravity;
        if (Air != null) ticks += Math.Clamp(wingTicks, 0, 120);
        float speed = MaxRun;
        for(var jump=ExtraJump;jump!=null;jump=jump.Next) speed=Math.Max(speed,MaxRun*jump.Speed);
        for (var action=Impulse;action!=null;action=action.Next) speed=Math.Max(speed,action.HorizontalSpeed);
        speed = Math.Max(speed, Math.Max(AccRunSpeed, Air?.AirRunSpeed ?? 0));
        return (int)Math.Clamp(ticks * speed, ExtraJump == null && Air == null ? 144 : 256, 1024);
    }
    internal bool IsBasic=>Matches(Basic);
    internal bool IsSupportedGroundPhysics=>Positive(AccRun)&&Positive(MaxRun)&&Positive(AccRunSpeed)
        &&Positive(RunSlowdown)&&Positive(Gravity)&&Positive(MaxFall)&&Positive(JumpSpeed)&&JumpHeight>=0;
    private static bool Positive(float value)=>float.IsFinite(value)&&value>0;
    internal bool Matches(PhysicsParameters other)=>Math.Abs(AccRun-other.AccRun)<.001f
        &&Math.Abs(MaxRun-other.MaxRun)<.001f&&Math.Abs(AccRunSpeed-other.AccRunSpeed)<.001f
        &&Math.Abs(RunSlowdown-other.RunSlowdown)<.001f&&Math.Abs(Gravity-other.Gravity)<.001f
        &&Math.Abs(JumpSpeed-other.JumpSpeed)<.001f&&JumpHeight==other.JumpHeight
        &&Math.Abs(MaxFall-other.MaxFall)<.001f;

}

// Terraria 1.4.4.9 Player.Update uses the tile row of the body's top,
// not feet or velocity, and damages only above 25 + extraFall rows.
internal readonly record struct FallSafety(int ExtraFall,bool Immune,int Life=0)
{
    // Terrain observation / simulation horizon. Immunity does not certify a landing.
    internal const int LookaheadPixels=48*16;
    internal static int Row(float top)=>(int)(top/16f);
    internal long RawDamage(int fallStart,float landingTop)=>Immune?0:
        Math.Max(0L,(long)Row(landingTop)-fallStart-25L-ExtraFall)*10;
    internal bool Safe(int fallStart,float landingTop)=>RawDamage(fallStart,landingTop)==0;
    internal bool CouldBeLethal(long rawDamage)=>rawDamage>0&&Life>0&&rawDamage>=Life;
    internal bool Safe(float startTop,float landingTop)=>Safe(Row(startTop),landingTop);
}

internal readonly record struct PhysicsState(float Px, float Py, float Vx, float Vy,
    bool Grounded, int JumpFramesLeft = 0, bool ReleaseJump = true,
    bool ExtraAvailable=false,bool ExtraActive=false,bool ExtraUsed=false,AirResources Air=null,AirPhase AirPhase=AirPhase.None,bool Wet=false,ActionCooldown Cooldowns=null,int RemainingJumps=-1,int ActiveJump=0);
internal readonly record struct PhysicsInput(int Direction, bool Jump, bool Down=false,string ImpulseKey=null);
internal readonly record struct PredictedFrame(PhysicsInput Input, PhysicsState State);
internal sealed record Prediction(bool Safe, string Reason, List<PredictedFrame> Frames,
    PhysicsState Landing, int ExecuteFrames, bool Jump,
    NavigationBlockingTile[] BlockingTiles = null,int ExtraJumpFrame=-1,int InitialHold=0,
    int AirStartFrame=-1,int AirHoldFrames=0,AirKind AirMode=AirKind.None,string ImpulseKey=null,int ImpulseFrame=-1,int ExtraCount=1,int ExtraHold=-1,LandingRegion FlightGoal=null);

internal sealed record NavigationBlockingTile(int X,int Y,string ContentKey,
    string Basis="rejected_root_forward_walk_contact");

// A confirmed horizontal TileCollision clip against a full solid face only.
// Native StepUp/Down has already run. Platforms, slopes, liquids, unknown cells
// and mere nearby solids cannot produce this evidence. At most four cells.
internal static class NavigationContact
{
    internal static NavigationBlockingTile[] Horizontal(float x,float y,float requestedX,float clippedX,
        System.Func<int,int,(char Shape,string ContentKey)> read)
    {
        if(!float.IsFinite(x)||!float.IsFinite(y)||!float.IsFinite(requestedX)||!float.IsFinite(clippedX)
            ||requestedX==0||System.Math.Abs(clippedX)>=System.Math.Abs(requestedX)
            ||requestedX*clippedX<0)return null;
        float face=x+clippedX+(requestedX>0?PhysicsSimulator.PlayerW:0);
        int grid=(int)System.Math.Round(face/16);
        if(System.Math.Abs(face-grid*16)>0.001f)return null;
        int tx=requestedX>0?grid:grid-1;
        var found=new List<NavigationBlockingTile>();
        for(int ty=(int)System.Math.Floor(y/16);ty<(int)System.Math.Ceiling((y+PhysicsSimulator.PlayerH)/16)&&found.Count<4;ty++)
        {
            var cell=read(tx,ty);
            if(cell.Shape=='#'&&!string.IsNullOrEmpty(cell.ContentKey))found.Add(new(tx,ty,cell.ContentKey));
        }
        return found.Count==0?null:found.ToArray();
    }
}

internal interface IMotionWorld
{
    long TerrainSignature(PhysicsState start,Prediction edge=null)=>0;
    NavigationBlockingTile[] LastHorizontalContact => null;
    PhysicsState Move(PhysicsState start, float vx, float vy, float gravity);
    PhysicsState MoveThroughPlatform(PhysicsState start,float vx,float vy,float gravity)
        =>throw new UnsafeMotionException("platform descent unsupported by this world");
    bool WaterContact(PhysicsState state)=>false;
    PhysicsState MoveInWater(PhysicsState start,float vx,float vy,float gravity)=>Move(start,vx,vy,gravity);
    bool SafeSweep(PhysicsState start, PhysicsState end, out string reason);
    bool SafeLanding(PhysicsState state);
    bool NegatesFallDamage(PhysicsState state)=>false;
    // Opt in only when the world can verify a flat solid or one-way platform top.
    bool SafeFlatLanding(PhysicsState state) => false;
    string LandingRejection(PhysicsState state) => SafeLanding(state) ? null : "unsafe foot support or body clearance";
}

internal sealed class UnsafeMotionException : Exception
{
    internal UnsafeMotionException(string message) : base(message) { }
}

internal static class PhysicsSimulator
{
    internal static bool SupportedCheckpoint(PhysicsState s,IMotionWorld world)=>s.Grounded
        &&Math.Abs(s.Vy)<=0.1f&&s.ReleaseJump&&s.JumpFramesLeft==0&&!s.ExtraActive
        &&(s.Air==null||s.Air.Delay==0&&!s.Air.RocketFrame)&&world.SafeLanding(s);
    internal static bool Settled(PhysicsState s,IMotionWorld world)
        => Math.Abs(s.Vx)<=0.1f&&SupportedCheckpoint(s,world);
    internal const int PlayerW = 20, PlayerH = 42, MaxFrames = 90, WalkFrames = 12;

    // TerraBlind's dry step, adapted to the installed 1.4.4.9 IL. Collision is
    // supplied by native Terraria APIs in-game; offline worlds are explicitly test doubles.
    internal static PhysicsState Step(PhysicsState s, PhysicsInput input, PhysicsParameters ph, IMotionWorld world)
    {
        if(ph.NoAirRepress && !s.Grounded && s.ReleaseJump && input.Jump)
            throw new UnsafeMotionException("unmodeled extra jump input");
        if (ph.GroundOnly && (!s.Grounded || input.Jump || input.Down || input.ImpulseKey != null))
            throw new UnsafeMotionException("unmodeled airborne action");
        bool wet=world.WaterContact(s);
        if((wet||s.Wet)&&!ph.CanWade)throw new UnsafeMotionException("unsupported water movement profile");
        if(input.Down && (s.Wet || wet))throw new UnsafeMotionException("wet platform descent unsupported");
        float gravity=s.Wet?PhysicsParameters.WaterGravity:ph.Gravity;
        float maxFall=s.Wet?PhysicsParameters.WaterMaxFall:ph.MaxFall;
        float jumpSpeed=s.Wet?PhysicsParameters.WaterJumpSpeed:ph.JumpSpeed;
        int jumpHeight=s.Wet?PhysicsParameters.WaterJumpHeight:ph.JumpHeight;
        float vx = s.Vx, vy = s.Vy;
        var cooldowns = ActionCooldown.Advance(s.Cooldowns);
        if (input.ImpulseKey != null)
        {
            var impulse = ph.Impulse;
            while (impulse!=null && impulse.Key!=input.ImpulseKey) impulse=impulse.Next;
            if (impulse == null || s.Grounded || wet || ActionCooldown.Get(s.Cooldowns,input.ImpulseKey) > 0 || input.Direction == 0)
                throw new UnsafeMotionException("air impulse unavailable");
            vx = input.Direction * impulse.HorizontalSpeed;
            vy = impulse.VerticalSpeed;
            cooldowns = new(impulse.Key,PhysicsParameters.PlanningCooldown(impulse.CooldownTicks),cooldowns);
        }
        int jfl = s.JumpFramesLeft;
        bool launched=false;
        bool airborneWing=ph.Air?.Kind==AirKind.Wings && s.Vy!=0;
        float horizontalRun=airborneWing?Math.Max(ph.AccRunSpeed,ph.Air.AirRunSpeed):ph.AccRunSpeed;
        float acceleration=airborneWing?ph.AccRun*ph.Air.AirAcceleration:ph.AccRun;
        bool available=s.ExtraAvailable,active=s.ExtraActive,used=s.ExtraUsed;
        int remaining=s.RemainingJumps;
        int activeBit=s.ActiveJump;
        var activeModel=ph.ExtraJump?.First(activeBit);
        acceleration*=activeModel?.Acceleration??1;
        float maxRun=ph.MaxRun*(activeModel?.Speed??1);
        if(ph.ExtraJump!=null)
        {
            if(remaining<0) remaining=available&&!used?ph.ExtraJump.Mask:0;
            if(s.Grounded&&s.Vy==0){available=true;used=false;remaining=ph.ExtraJump.Mask;}
        }
        // Upstream's else-if chain is confirmed by 1.4.4.9 IL: primary
        // acceleration jumps to IL_0f96, bypassing weak acceleration/friction.
        // The clamp at IL_021b is trackBoost-only, not ordinary running.
        if (input.Direction > 0 && vx < maxRun)
        {
            if (vx < -ph.RunSlowdown) vx += ph.RunSlowdown;
            vx += acceleration;
        }
        else if (input.Direction > 0 && vx < horizontalRun)
        {
            // HorizontalMovement adds a second weak acceleration term for wings, including in air.
            if (vy == 0f || airborneWing)
            {
                if (vx < -ph.RunSlowdown) vx += ph.RunSlowdown;
                vx += acceleration * (ph.Air?.Kind==AirKind.Wings ? 0.4f : 0.2f);
            }
        }
        else if (input.Direction < 0 && vx > -maxRun)
        {
            if (vx > ph.RunSlowdown) vx -= ph.RunSlowdown;
            vx -= acceleration;
        }
        else if (input.Direction < 0 && vx > -horizontalRun)
        {
            if (vy == 0f || airborneWing)
            {
                if (vx > ph.RunSlowdown) vx -= ph.RunSlowdown;
                vx -= acceleration * (ph.Air?.Kind==AirKind.Wings ? 0.4f : 0.2f);
            }
        }
        else if (vy == 0f) // vanilla L19591 ground friction: velocity.Y==0, not a Grounded flag
        {
            if (vx > ph.RunSlowdown)       vx -= ph.RunSlowdown;
            else if (vx < -ph.RunSlowdown)  vx += ph.RunSlowdown;
            else                            vx  = 0f;
        }
        else
        {
            float airSlow = ph.RunSlowdown * 0.5f;
            if (vx > airSlow)        vx -= airSlow;
            else if (vx < -airSlow)  vx += airSlow;
            else                     vx  = 0f;
        }
        if (input.Jump)
        {
            if (jfl > 0)
            {
                if (vy == 0) jfl = 0;
                else { vy = -jumpSpeed; jfl--; }
            }
            else if (s.Grounded && s.ReleaseJump)
            {
                vy = -jumpSpeed;
                // Launch sets the full counter; only subsequent hold frames decrement.
                jfl = jumpHeight;
                launched=true;
            }
            else if(ph.ExtraJump?.First(remaining) is {} nextJump && s.ReleaseJump&&vy!=0&&available&&!used)
            {
                vy=-ph.JumpSpeed;jfl=nextJump.HoldFrames(ph);
                remaining &= ~nextJump.Bit; activeBit=nextJump.Bit;
                available=remaining!=0;active=true;used=!available;launched=true;
            }
        }
        else jfl = 0;
        if(jfl==0){active=false;activeBit=0;}
        AirResources resources=s.Air;AirPhase phase=AirPhase.None;
        if(ph.Air!=null)
        {
            if(!ph.Air.Valid(resources))throw new UnsafeMotionException("invalid air resource state");
            (vy,resources,phase)=ph.Air.Step(s,input,ph,vy,jfl,launched);
        }
        else vy = Math.Min(vy + gravity, maxFall);
        // Player.Update IL_7490..74c6 shortens the remaining held jump on exit
        // from ordinary water, after JumpMovement and before dry collision.
        if(s.Wet&&!wet)jfl=Math.Min(jfl,jumpHeight/5);
        var moved = input.Down?world.MoveThroughPlatform(s,vx,vy,gravity):wet?world.MoveInWater(s,vx,vy,gravity):world.Move(s,vx,vy,gravity);
        if (ph.GroundOnly && !moved.Grounded) throw new UnsafeMotionException("ground-only route loses support");
        if (vy < 0 && Math.Abs(moved.Vy - vy) > 0.01f) jfl = 0;
        return moved with { JumpFramesLeft = jfl, ReleaseJump = !input.Jump,
            RemainingJumps=remaining,ActiveJump=activeBit,ExtraAvailable=available,ExtraActive=active,ExtraUsed=used,Air=resources,AirPhase=phase,Wet=wet,Cooldowns=cooldowns };
    }

    // The upstream input-enumeration/simulation loop, bounded to one local action.
    // Include braking and stable support in validation. Walking executes only the
    // moving prefix and replans, so an open flat surface does not become stop/start.
    internal static Prediction Predict(PhysicsState start, int direction, int hold,
        PhysicsParameters ph, IMotionWorld world, int walkFrames = WalkFrames, bool rollingJump = false,
        int extraJumpFrame=-1,int airStartFrame=-1,int airHoldFrames=0,bool allowStationaryHop=false,bool drop=false,LandingRegion walkingGoal=null,int impulseFrame=-1,string impulseKey=null,int extraCount=1,int extraHoldLimit=-1,LandingRegion flightGoal=null,bool cruise=false,int extraCoast=0,bool reserveFinalJump=false)
    {
        var frames = new List<PredictedFrame>();
        bool brakingOnly = direction == 0 && hold == 0 && walkFrames == 0;
        var s = start;
        bool airborne = false,airborneBelow=false;
        int stable = 0;
        int movingLandingFrames = 0;
        bool landingChecked = false, extendLanding = false;
        int fallStart=FallSafety.Row(start.Py);
        int trigger=extraJumpFrame, endHold=-1, launches=0;
        int horizon=airStartFrame>=0 && ph.Air?.Kind==AirKind.Wings?Math.Max(180,airStartFrame+airHoldFrames+180):
            extraCoast>0?300+(extraCount+1)*extraCoast:extraCount>1?300:airStartFrame>=0?180:extraJumpFrame>=0?140:MaxFrames;
        var terrain=world as ISurfaceTerrain;
        for (int f=0;f<(cruise?Math.Min(1200,horizon+600):horizon);f++)
        {
            bool carryLanding=cruise || extraCoast>0;
            if(carryLanding && terrain!=null && f%32==0)world=terrain.MotionAt(s);
            bool moving = hold > 0 ? !airborne || !s.Grounded
                || (rollingJump && (Math.Abs(s.Vy) > 0.01f || extendLanding || carryLanding && movingLandingFrames==0)) : f < walkFrames;
            // A final charge can break the descent after the earlier full-height jumps.
            if(reserveFinalJump && launches==extraCount-1 && f==trigger && !s.Grounded &&
                (s.Vy<=0 || s.Py<start.Py-ph.JumpSpeed*ph.JumpHeight))trigger++;
            if(f==trigger)
            {
                var jump=ph.ExtraJump?.First(s.RemainingJumps);
                if(jump==null||s.Grounded||!s.ReleaseJump||!s.ExtraAvailable||s.ExtraUsed||s.Vy==0)
                    return new(false,"extra jump launch unavailable",frames,s,0,true,ExtraJumpFrame:extraJumpFrame,InitialHold:hold);
                int held=jump.HoldFrames(ph);
                if(launches+1==extraCount && extraHoldLimit>=0) held=Math.Min(held,extraHoldLimit);
                endHold=f+held;
                trigger=++launches<extraCount?endHold+2+extraCoast:-1;
            }
            bool extraHold=extraJumpFrame>=0&&f>=extraJumpFrame&&f<=endHold;
            bool airHold=airStartFrame>=0&&f>=airStartFrame&&f<airStartFrame+airHoldFrames;
            bool glide=airStartFrame>=0&&ph.Air?.Kind==AirKind.Wings&&s.Air?.WingTime<=0&&s.Vy>0&&!s.Grounded;
            int horizontal=moving?direction:0;
            if(flightGoal!=null && airborne && !s.Grounded && hold>0)
            {
                // Neutral input coasts in the air. Countersteer before the target.
                float target=(flightGoal.MinPlayerLeftX+flightGoal.MaxPlayerLeftX)*.5f;
                float error=target-s.Px;
                float brake=Math.Max(.01f,ph.RunSlowdown+ph.AccRun);
                float stopping=s.Vx*Math.Abs(s.Vx)/(2*brake);
                horizontal=Math.Abs(error)<4 && Math.Abs(s.Vx)<.35f?0:Math.Sign(error-stopping);
            }
            var input = new PhysicsInput(horizontal, f < hold||extraHold||(cruise?!s.Grounded && f>=airStartFrame && (airHold || s.Air?.WingTime<=0):airHold||glide),drop && s.Py<=start.Py+4 && !airborneBelow, f==impulseFrame ? impulseKey : null);
            PhysicsState next;
            try
            {
                next = Step(s, input, ph, world);
                if (!world.SafeSweep(s, next, out string reason))
                    return new(false, reason, frames, next, 0, hold > 0);
            }
            catch (UnsafeMotionException error) { return new(false, error.Message, frames, s, 0, hold > 0); }
            // Reversing input can spend a frame at zero speed before accelerating
            // away. Judge a blocked walk once velocity no longer opposes input.
            if (hold == 0 && direction * s.Vx >= 0 && Math.Abs(next.Px - s.Px) < 0.01f && Math.Abs(next.Vx) < 0.01f && f < walkFrames)
                return new(false, "walking blocked", frames, next, 0, false, world.LastHorizontalContact);
            airborne |= !next.Grounded;
            airborneBelow |= next.Py>start.Py+4;
            if(s.Grounded||s.JumpFramesLeft>0||s.Wet)fallStart=FallSafety.Row(s.Py);
            if(next.Py-start.Py>FallSafety.LookaheadPixels && !(cruise && ph.Fall.Safe(fallStart,next.Py)))
                return new(false,"descent leaves the local collision horizon",frames,next,0,hold>0);
            if(next.Grounded&&ph.Fall.RawDamage(fallStart,next.Py) is >0 and var damage&&!world.NegatesFallDamage(next))
                return new(false,$"landing would exceed the native fall damage threshold: raw damage={damage}, life={ph.Fall.Life}, potentially lethal={ph.Fall.CouldBeLethal(damage)}",
                    frames,next,0,hold>0);
            frames.Add(new(input, next));
            if (rollingJump && hold > 0 && movingLandingFrames == 0)
            {
                // Retain upstream's extra grounded step before handing off a jump.
                // Both landing states must have safe support and released jump;
                // the rest of this prediction still proves a full braking tail.
                bool groundedLanding = airborne && next.Grounded && Math.Abs(next.Vy) < 0.01f && f >= hold;
                bool supported = groundedLanding
                    && next.ReleaseJump && next.JumpFramesLeft == 0 && world.SafeLanding(next);
                if(carryLanding)
                {
                    if(supported && landingChecked)movingLandingFrames=frames.Count;
                    landingChecked=supported;
                }
                else if (!landingChecked && groundedLanding)
                {
                    landingChecked = true;
                    extendLanding = supported;
                }
                else if (extendLanding)
                {
                    extendLanding = false;
                    if (supported) movingLandingFrames = frames.Count;
                }
            }
            bool doneMoving = hold > 0 ? airborne && next.Grounded && f >= hold : f >= walkFrames;
            stable = doneMoving && SupportedCheckpoint(next,world) && Math.Abs(next.Vx) < 0.01f ? stable + 1 : 0;
            s = next;
            if (stable >= 2)
            {
                // A neutral jump can ascend through a one-way platform without
                // horizontal travel. Require a different supported height; a
                // hop returning to the same floor must not create search cycles.
                bool progress = direction == 0 && hold > 0
                    ? allowStationaryHop || start.Py - s.Py >= 4
                    : direction * (s.Px - start.Px) >= (hold > 0 ? 24 : 4)
                        || hold > 0 && s.Py <= start.Py - 16 && direction * (s.Px - start.Px) > 0;
                bool safe = (!drop || airborneBelow && s.Py-start.Py is >=16 and <=96) && (brakingOnly || progress || hold==0 && walkingGoal?.Contains(s)==true)
                    && world.SafeLanding(s)&&(extraJumpFrame<0||launches==extraCount)&&(airStartFrame<0||f>=airStartFrame);
                if(impulseFrame>=0)safe &= f>=impulseFrame && frames.Exists(frame=>frame.Input.ImpulseKey!=null);
                if(airStartFrame>=0)safe&=frames.Exists(p=>p.State.AirPhase is AirPhase.Flying or AirPhase.Gliding or AirPhase.RocketPulse);
                int execute = movingLandingFrames > 0 ? movingLandingFrames : frames.Count;
                if (hold == 0 && !brakingOnly)
                {
                    execute = walkFrames;
                    while (!SupportedCheckpoint(frames[execute - 1].State,world) && execute < frames.Count) execute++;
                }
                return new(safe, safe ? "safe landing and braking" : "no safe landing/progress",
                    frames, s, execute, hold > 0,ExtraJumpFrame:extraJumpFrame,InitialHold:hold,
                    AirStartFrame:airStartFrame,AirHoldFrames:airHoldFrames,AirMode:ph.Air?.Kind??AirKind.None,ImpulseKey:impulseKey,ImpulseFrame:impulseFrame,ExtraCount:extraCount,ExtraHold:extraHoldLimit,FlightGoal:flightGoal);
            }
        }
        return new(false, "no stable landing within prediction limit", frames, s, 0, hold > 0);
    }
}
