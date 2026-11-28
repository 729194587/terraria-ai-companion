using System;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using CompanionBridge.Common.Systems;
using CompanionBridge.Motion;
using Identity = CompanionBridge.ServerIpc.AiReadOnlyProtocol.Identity;
using Result = CompanionBridge.ServerIpc.AiAtomicActions.NativeResult;

namespace CompanionBridge.ServerIpc;

internal sealed class FollowTeammateTask(string id, JsonElement args, AiAtomicActions.IExecutor movement)
    : AbstractCompanionTask(id)
{
    internal static ulong? DurationUpdates(JsonElement args) =>
        args.TryGetProperty("durationSeconds", out var value) ? (ulong)value.GetInt32() * 60 : null;
    private readonly string teammate = args.GetProperty("teammateRef").GetString();
    private readonly int radius = 16 * (args.TryGetProperty("distanceTiles", out var value) ? value.GetInt32() : 3);
    private readonly ulong? duration = DurationUpdates(args);
    private ulong started, elapsed, nextPlan;
    private int legs, defenses;
    private bool holding, acquired;
    private float distance;
    private Vector2 destination;
    private CompanionTask child;
    private Func<CombatController> pendingDefense;
    private Func<bool> defenseAllowed;
    private string phase = "locating";
    internal override string Name => "follow_teammate";
    internal override string Phase => phase;
    internal override object Progress => new { teammateRef = teammate, elapsedUpdates = elapsed,
        durationUpdates = duration, distancePixels = distance, withinRange = holding, legs, defenses };
    internal override void Start(Identity frame) => started = frame.GameUpdate;
    internal override void Resume(Identity frame,ulong pausedUpdates)
    {
        started+=pausedUpdates;nextPlan=0;pendingDefense=null;defenseAllowed=null;phase="locating";
    }

    internal void OfferDefense(Func<CombatController> create, Func<bool> allowed)
    {
        if (child is CombatController || pendingDefense != null) return;
        pendingDefense = create;
        defenseAllowed = allowed;
    }

    private Result ReleaseChild(Identity frame)
    {
        if (child == null) return null;
        var stopped = base.Stop(frame, "cancelled", "follow_leg_ended");
        acquired |= stopped.InputsAcquired;
        child = null;
        return stopped.Status == "unknown" || !stopped.InputsReleased ? stopped : null;
    }

    internal override Result Stop(Identity frame, string status, string reason)
    {
        var uncertain = ReleaseChild(frame);
        return uncertain ?? new Result(status, reason, Progress, true, status == "completed", acquired);
    }

    private static PhysicsState StandingPose(Vector2 feet) => new(
        feet.X - PhysicsSimulator.PlayerW / 2, feet.Y - PhysicsSimulator.PlayerH, 0, 0, true, 0, true);

    private static bool OnFooting(Player player) =>
        (Math.Abs(player.velocity.Y) < .1f || player.sloping)
        && MotionSupport.SafeLanding(StandingPose(player.Bottom), TerrainReader.ReadCell);

    // Resolve a standing point under a jumping leader using the same terrain rules as the navigator.
    private static Vector2? Anchor(Player leader)
    {
        var feet = leader.Bottom;
        var pose = StandingPose(feet);
        if (MotionSupport.SafeLanding(pose, TerrainReader.ReadCell)) return feet;
        float first = MathF.Ceiling(feet.Y / 8) * 8;
        for (float y = first; y <= feet.Y + 12 * 16; y += 8)
        {
            var candidate = pose with { Py = y - PhysicsSimulator.PlayerH };
            if (MotionSupport.SafeLanding(candidate, TerrainReader.ReadCell)) return new(feet.X, y);
            if (MotionSupport.ClearanceRejection(candidate, TerrainReader.ReadCell) != null) break;
        }
        return null;
    }

    internal override Result Tick(Identity frame)
    {
        var actor = (Player)frame.Actor;
        var leader = ServerRegroup.Recipient(frame, teammate);
        elapsed = frame.GameUpdate - started;
        if (leader == null) return Stop(frame, "failed", "teammate_unavailable");
        var delta = leader.Bottom - actor.Bottom;
        distance = delta.Length();
        // A wider wake-up band avoids repeated starts when both players stand near its edge.
        holding = distance <= radius + (holding ? 32 : 0) && Math.Abs(delta.Y) <= (holding ? 40 : 24)
            && Collision.CanHitLine(actor.Center, 1, 1, leader.Center, 1, 1) && OnFooting(actor);
        if (elapsed >= duration)
            return holding ? Stop(frame, "completed", "follow_duration_verified")
                           : Stop(frame, "timeout", "follow_target_out_of_range");

        if ((pendingDefense != null || child is CombatController) && !defenseAllowed())
            return Stop(frame, "cancelled", "autonomy_revoked");
        if (pendingDefense != null)
        {
            if (ReleaseChild(frame) is {} uncertain) return uncertain;
            if (!ServerAiNativeActions.BodyIdle(frame)) return Stop(frame, "failed", "follow_body_busy");
            child = pendingDefense();
            pendingDefense = null;
            defenses++;
        }
        if (child is CombatController)
        {
            phase = "defending";
            var defense = RunChild(child, frame, CombatController.Budget);
            if (defense == null) return Running();
            child = null;
            acquired |= defense.InputsAcquired;
            if (defense.Status != "completed" && defense.Reason != "combat_target_unavailable")
                return defense with { Evidence = Progress, InputsAcquired = acquired };
            nextPlan = 0;
        }
        if (holding)
        {
            phase = "nearby";
            return ReleaseChild(frame) ?? Running();
        }
        if (frame.GameUpdate >= nextPlan)
        {
            nextPlan = frame.GameUpdate + 15;
            var anchor = Anchor(leader);
            // Once below an airborne teammate, wait for their landing instead of reissuing the same walk.
            if (anchor is {} below && below.Y - leader.Bottom.Y > 24
                && Math.Abs(below.Y - actor.Bottom.Y) <= 8 && Math.Abs(below.X - actor.Bottom.X) <= radius && OnFooting(actor))
            {
                phase = "waiting_for_landing";
                return ReleaseChild(frame) ?? Running();
            }
            // Keep the current leg when it still leads toward a teammate moving farther ahead.
            if (anchor is {} point && (child == null ||
                Vector2.DistanceSquared(point, destination) >= 32 * 32 &&
                (Math.Abs(point.Y - destination.Y) > 16 || Vector2.Dot(point - destination, destination - actor.Bottom) <= 0)))
            {
                if (ReleaseChild(frame) is {} uncertain) return uncertain;
                destination = point;
                var region = LandingRegion.AtDestination(point.X, point.Y);
                child = new NativeActionTask(movement, "navigate_to_landing_region",
                    NativeNavigationWorld.RegionArguments(region), $"{taskId}/leg-{++legs}");
            }
        }
        phase = child == null ? "waiting_for_landing" : "following";
        if (child == null) return Running();
        var leg = RunChild(child, frame, 1800);
        if (leg == null) return Running();
        child = null;
        acquired |= leg.InputsAcquired;
        if (leg.Status != "completed") return leg with { Evidence = Progress, InputsAcquired = acquired };
        return Running();
    }
}
