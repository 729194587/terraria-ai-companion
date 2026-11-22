// Adapted from Reisenbug/TerraBlind, Perception/PhysicsSimulator.cs (MIT).
// Pinned source and adaptation notes: ThirdParty/TerraBlind/NOTICE.md.
using System;
using System.Collections.Generic;

namespace CompanionBridge.Motion;

internal sealed record PhysicsParameters(float AccRun, float MaxRun, float AccRunSpeed,
    float RunSlowdown, float Gravity, float MaxFall, float JumpSpeed, int JumpHeight)
{
    // Player.Update initializes 10, then unconditionally adds 0.01 at IL_020d–021a
    // before our input hook. Match the effective value, not the reset constant.
    internal static readonly PhysicsParameters Basic = new(0.08f, 3, 3, 0.2f, 0.4f, 10.01f, 5.01f, 15);

    // Native pinned-engine boundary measurement: BuffID.Swiftness (3).
    internal static readonly PhysicsParameters Swiftness = new(0.1f, 3.75f, 3, 0.2f, 0.4f, 10.01f, 5.01f, 15);
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

internal readonly record struct PhysicsState(float Px, float Py, float Vx, float Vy,
    bool Grounded, int JumpFramesLeft = 0, bool ReleaseJump = true);
internal readonly record struct PhysicsInput(int Direction, bool Jump);
internal readonly record struct PredictedFrame(PhysicsInput Input, PhysicsState State);
internal sealed record Prediction(bool Safe, string Reason, List<PredictedFrame> Frames,
    PhysicsState Landing, int ExecuteFrames, bool Jump,
    NavigationBlockingTile[] BlockingTiles = null);

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
    NavigationBlockingTile[] LastHorizontalContact => null;
    PhysicsState Move(PhysicsState start, float vx, float vy, float gravity);
    bool SafeSweep(PhysicsState start, PhysicsState end, out string reason);
    bool SafeLanding(PhysicsState state);
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
        &&Math.Abs(s.Vy)<=0.1f&&s.ReleaseJump&&s.JumpFramesLeft==0&&world.SafeLanding(s);
    internal const int PlayerW = 20, PlayerH = 42, MaxFrames = 90, WalkFrames = 12;

    // TerraBlind's dry step, adapted to the installed 1.4.4.9 IL. Collision is
    // supplied by native Terraria APIs in-game; offline worlds are explicitly test doubles.
    internal static PhysicsState Step(PhysicsState s, PhysicsInput input, PhysicsParameters ph, IMotionWorld world)
    {
        float vx = s.Vx, vy = s.Vy;
        int jfl = s.JumpFramesLeft;
        // Upstream's else-if chain is confirmed by 1.4.4.9 IL: primary
        // acceleration jumps to IL_0f96, bypassing weak acceleration/friction.
        // The clamp at IL_021b is trackBoost-only, not ordinary running.
        if (input.Direction > 0 && vx < ph.MaxRun)
        {
            if (vx < -ph.RunSlowdown) vx += ph.RunSlowdown;
            vx += ph.AccRun;
        }
        else if (input.Direction > 0 && vx < ph.AccRunSpeed)
        {
            // vanilla L19527: weak-accel inner gate is velocity.Y==0 (on ground). use frame-start vy (pre-gravity),
            // matching HorizontalMovement's read. NOT a self-invented Grounded flag.
            if (vy == 0f)
            {
                if (vx < -ph.RunSlowdown) vx += ph.RunSlowdown;
                vx += ph.AccRun * 0.2f;
            }
        }
        else if (input.Direction < 0 && vx > -ph.MaxRun)
        {
            if (vx > ph.RunSlowdown) vx -= ph.RunSlowdown;
            vx -= ph.AccRun;
        }
        else if (input.Direction < 0 && vx > -ph.AccRunSpeed)
        {
            if (vy == 0f)
            {
                if (vx > ph.RunSlowdown) vx -= ph.RunSlowdown;
                vx -= ph.AccRun * 0.2f;
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
                else { vy = -ph.JumpSpeed; jfl--; }
            }
            else if (s.Grounded && s.ReleaseJump)
            {
                vy = -ph.JumpSpeed;
                // Launch sets the full counter; only subsequent hold frames decrement.
                jfl = ph.JumpHeight;
            }
        }
        else jfl = 0;
        vy = Math.Min(vy + ph.Gravity, ph.MaxFall);
        var moved = world.Move(s, vx, vy, ph.Gravity);
        if (vy < 0 && Math.Abs(moved.Vy - vy) > 0.01f) jfl = 0;
        return moved with { JumpFramesLeft = jfl, ReleaseJump = !input.Jump };
    }

    // The upstream input-enumeration/simulation loop, bounded to one local action.
    // Include braking and stable support in validation. Walking executes only the
    // moving prefix and replans, so an open flat surface does not become stop/start.
    internal static Prediction Predict(PhysicsState start, int direction, int hold,
        PhysicsParameters ph, IMotionWorld world, int walkFrames = WalkFrames, bool rollingJump = false,
        float maxDropPixels = 16)
    {
        var frames = new List<PredictedFrame>();
        bool brakingOnly = direction == 0 && hold == 0 && walkFrames == 0;
        var s = start;
        bool airborne = false;
        int stable = 0;
        int movingLandingFrames = 0;
        bool landingChecked = false, extendLanding = false;
        float lastGroundY = start.Py;
        for (int f = 0; f < MaxFrames; f++)
        {
            bool moving = hold > 0 ? !airborne || !s.Grounded
                || (rollingJump && (Math.Abs(s.Vy) > 0.01f || extendLanding)) : f < walkFrames;
            var input = new PhysicsInput(moving ? direction : 0, f < hold);
            PhysicsState next;
            try
            {
                next = Step(s, input, ph, world);
                if (!world.SafeSweep(s, next, out string reason))
                    return new(false, reason, frames, next, 0, hold > 0);
            }
            catch (UnsafeMotionException error) { return new(false, error.Message, frames, s, 0, hold > 0); }
            if (hold == 0 && Math.Abs(next.Px - s.Px) < 0.01f && Math.Abs(next.Vx) < 0.01f && f < walkFrames)
                return new(false, "walking blocked", frames, next, 0, false, world.LastHorizontalContact);
            airborne |= !next.Grounded;
            if (hold > 0 && next.Py - start.Py > maxDropPixels)
                return new(false, $"jump descent exceeds {maxDropPixels:0}px limit", frames, next, 0, true);
            if (hold == 0 && next.Py - lastGroundY > maxDropPixels)
                return new(false, $"walking drop exceeds {maxDropPixels:0}px limit", frames, next, 0, false);
            if (next.Grounded) lastGroundY = next.Py;
            frames.Add(new(input, next));
            if (rollingJump && hold > 0 && movingLandingFrames == 0)
            {
                // Retain upstream's extra grounded step before handing off a jump.
                // Both landing states must have safe support and released jump;
                // the rest of this prediction still proves a full braking tail.
                bool groundedLanding = airborne && next.Grounded && Math.Abs(next.Vy) < 0.01f && f >= hold;
                bool supported = groundedLanding
                    && next.ReleaseJump && next.JumpFramesLeft == 0 && world.SafeLanding(next);
                if (!landingChecked && groundedLanding)
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
                    ? start.Py - s.Py >= 4
                    : direction * (s.Px - start.Px) >= (hold > 0 ? 24 : 4);
                bool safe = (brakingOnly || progress)
                    && world.SafeLanding(s);
                int execute = movingLandingFrames > 0 ? movingLandingFrames : frames.Count;
                if (hold == 0 && !brakingOnly)
                {
                    execute = walkFrames;
                    while (!SupportedCheckpoint(frames[execute - 1].State,world) && execute < frames.Count) execute++;
                }
                return new(safe, safe ? "safe landing and braking" : "no safe landing/progress",
                    frames, s, execute, hold > 0);
            }
        }
        return new(false, "no stable landing within prediction limit", frames, s, 0, hold > 0);
    }
}
