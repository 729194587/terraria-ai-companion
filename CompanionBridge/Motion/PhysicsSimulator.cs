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

    internal bool IsBasic => Math.Abs(AccRun - Basic.AccRun) < 0.001f
        && Math.Abs(MaxRun - Basic.MaxRun) < 0.001f && Math.Abs(AccRunSpeed - Basic.AccRunSpeed) < 0.001f
        && Math.Abs(RunSlowdown - Basic.RunSlowdown) < 0.001f && Math.Abs(Gravity - Basic.Gravity) < 0.001f
        && Math.Abs(JumpSpeed - Basic.JumpSpeed) < 0.001f && JumpHeight == Basic.JumpHeight
        && Math.Abs(MaxFall - Basic.MaxFall) < 0.001f;
}

internal readonly record struct PhysicsState(float Px, float Py, float Vx, float Vy,
    bool Grounded, int JumpFramesLeft = 0, bool ReleaseJump = true);
internal readonly record struct PhysicsInput(int Direction, bool Jump);
internal readonly record struct PredictedFrame(PhysicsInput Input, PhysicsState State);
internal sealed record Prediction(bool Safe, string Reason, List<PredictedFrame> Frames,
    PhysicsState Landing, int ExecuteFrames, bool Jump);

internal interface IMotionWorld
{
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
                return new(false, "walking blocked", frames, next, 0, false);
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
            stable = doneMoving && next.Grounded && Math.Abs(next.Vx) < 0.01f ? stable + 1 : 0;
            s = next;
            if (stable >= 2)
            {
                bool safe = (brakingOnly || direction * (s.Px - start.Px) >= (hold > 0 ? 24 : 4))
                    && world.SafeLanding(s);
                int execute = movingLandingFrames > 0 ? movingLandingFrames : frames.Count;
                if (hold == 0 && !brakingOnly)
                {
                    execute = walkFrames;
                    while (!frames[execute - 1].State.Grounded && execute < frames.Count) execute++;
                }
                return new(safe, safe ? "safe landing and braking" : "no safe landing/progress",
                    frames, s, execute, hold > 0);
            }
        }
        return new(false, "no stable landing within prediction limit", frames, s, 0, hold > 0);
    }
}
