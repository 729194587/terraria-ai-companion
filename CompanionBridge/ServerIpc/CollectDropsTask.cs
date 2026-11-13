using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using CompanionBridge.Motion;
using CompanionBridge.Common.Systems;
using VirtualPlayerEngineProbe;
using Result = CompanionBridge.ServerIpc.AiAtomicActions.NativeResult;

namespace CompanionBridge.ServerIpc;

internal sealed class CollectDropsTask(string id, JsonElement args, AiAtomicActions.IExecutor executor) : AbstractCompanionTask(id)
{
    internal const ulong Budget = 1500;
    private const int TargetLimit = 16;
    private const ulong ApproachBudget = 240;
    // Bounds stance probes per approach. The search rectangle is the native grab
    // range; only the sample count is capped, so cost stays predictable without
    // narrowing where the Actor may stand to pick an item up.
    private const int ApproachProbeBudget = 6144;
    private sealed class Target(ServerDrops.Drop drop)
    {
        internal readonly ServerDrops.Drop Drop = drop;
        internal string Reason;
        internal int Attempts;
        internal readonly HashSet<(float X, float Y)> RejectedStances = new();
    }
    private readonly List<Target> targets = new();
    private readonly Dictionary<int, int> collected = new();
    private Player player;
    private int initialLife;
    private Target target;
    private bool uncertain;
    private ulong navigationUntil;
    private NativeActionTask navigation;
    private Rectangle planned;
    private LandingRegion plannedRegion;
    private ulong waitUntil;
    private bool truncated, usedInputs;
    internal override string Name => "collect_drops";
    internal override string Phase => navigation != null ? "approaching_drop" : target != null ? "waiting_for_pickup" : "selecting_drop";
    internal override object Progress => Evidence();
    internal override object LastResult => lastResult == null ? null : TaskSlot.Receipt(lastResult with { Evidence = null });

    internal override Result Prepare(AiReadOnlyProtocol.Identity frame)
    {
        player = (Player)frame.Actor;
        initialLife = player.statLife;
        float radius = args.TryGetProperty("radiusPixels", out var r) ? r.GetSingle() : 320;
        var types = args.TryGetProperty("typeIds", out var t) ? t.EnumerateArray().Select(v => v.GetInt32()).ToHashSet() : null;
        string reference = args.TryGetProperty("entityRef", out var e) ? e.GetString() : null;
        var observed = AtomicQueries.ScanDrops(player.Center, radius, types)
            .Select(row => new ServerDrops.Drop(row.Slot, row.Item, ServerDrops.Reference(frame, row.Item), row.Item.type, row.Item.prefix))
            .Where(drop => reference == null || drop.Reference == reference).Take(TargetLimit + 1).ToArray();
        if (reference != null && observed.Length == 0) return Failed("drop_reference_unavailable");
        truncated = observed.Length > TargetLimit;
        targets.AddRange(observed.Take(TargetLimit).Select(drop => new Target(drop)));
        return null;
    }

    internal override void Start(AiReadOnlyProtocol.Identity frame) => ServerDrops.Current = this;
    internal bool Allows(Item item) => targets.Any(row => row.Reason == null && ReferenceEquals(row.Drop.Item, item) && ServerDrops.Live(row.Drop));

    internal void Credit(int slot, Item item, int type, int count)
    {
        var row = targets.FirstOrDefault(t => t.Drop.Slot == slot && ReferenceEquals(t.Drop.Item, item));
        if (row == null) return;
        collected[type] = collected.GetValueOrDefault(type) + count;
        if (!ServerDrops.Live(row.Drop)) row.Reason = "collected";
    }

    internal void Uncertain() => uncertain = true;

    private object Evidence() => new
    {
        collected = collected.OrderBy(row => row.Key).Select(row => new { typeId = row.Key, count = row.Value }).ToArray(),
        remaining = targets.Where(row => ServerDrops.Live(row.Drop)).Select(row => new {
            entityRef = row.Drop.Reference, typeId = row.Drop.Type, count = row.Drop.Item.stack,
            x = row.Drop.Item.Center.X, y = row.Drop.Item.Center.Y, reason = row.Reason ?? "pending"
        }).ToArray(),
        unavailable = targets.Where(row => row.Reason == "drop_unavailable").Select(row => row.Drop.Reference).ToArray(),
        truncated,
        pickupUnconfirmed = uncertain,
        outcome = collected.Count > 0 && (truncated || targets.Any(row => row.Reason != "collected")) ? "partial"
            : collected.Count > 0 ? "collected" : "none"
    };

    private Result End(string status, string reason)
    {
        if (ReferenceEquals(ServerDrops.Current, this)) ServerDrops.Current = null;
        return new(status, reason, Evidence(), ServerAiNativeActions.Released(player),
            status == "completed", InputsAcquired: usedInputs);
    }

    internal override Result Tick(AiReadOnlyProtocol.Identity frame)
    {
        if (uncertain) return Stop(frame, "unknown", "native_pickup_effect_unconfirmed");
        if (NativeNavigationWorld.CheckHazards(frame, initialLife) is {} hazard) return Stop(frame, "cancelled", hazard);
        foreach (var row in targets)
            if (row.Reason == null && !ServerDrops.Live(row.Drop)) row.Reason = "drop_unavailable";

        if (navigation != null)
        {
            bool moved = target.Reason != null || Distance(planned, target.Drop.Item.Hitbox) > 24;
            if (moved)
            {
                var stopped = base.Stop(frame, "cancelled", "drop_target_changed");
                navigation = null;
                if (stopped.Status == "unknown") return End("unknown", stopped.Reason);
                if (target.Attempts >= 4 && target.Reason == null) target.Reason = "drop_moved_repeatedly";
                target = null;
                return Running();
            }
            if (frame.GameUpdate >= navigationUntil)
            {
                var stopped = base.Stop(frame, "timeout", "navigation_timeout");
                navigation = null;
                if (stopped.Status == "unknown") return End("unknown", stopped.Reason);
                target.Reason = "navigation_timeout";
                target = null;
                return Running();
            }
            var child = RunChild(navigation, frame, ApproachBudget);
            if (child == null) return Running();
            navigation = null;
            if (child.Status == "unknown") return End("unknown", child.Reason);
            if (child.Status != "completed")
            {
                if (child.Status == "timeout" || target.Attempts >= 4)
                    target.Reason = child.Status == "timeout" ? "navigation_timeout" : child.Reason;
                else
                    target.RejectedStances.Add((plannedRegion.MinPlayerLeftX, plannedRegion.SurfaceY));
                target = null;
                return Running();
            }
            waitUntil = frame.GameUpdate + 75;
        }

        if (target != null)
        {
            if (target.Reason == null && frame.GameUpdate < waitUntil
                && Reach(player.Hitbox, target.Drop.Item)) return Running();
            if (target.Reason == null && frame.GameUpdate >= waitUntil)
                target.Reason = PickupBlock(target.Drop.Item) ?? "native_pickup_not_observed";
            target = null;
        }

        foreach (var candidate in targets.Where(row => row.Reason == null)
                     .OrderBy(row => Vector2.DistanceSquared(player.Center, row.Drop.Item.Center)))
        {
            var item = candidate.Drop.Item;
            if (PickupBlock(item) is {} blocked) { candidate.Reason = blocked; continue; }
            if (Reach(player.Hitbox, item))
            {
                target = candidate;
                waitUntil = frame.GameUpdate + 75;
                return Running();
            }
            if (candidate.Attempts >= 4) { candidate.Reason = "drop_moved_repeatedly"; continue; }
            if (!GameNavigation.Ordinary(player)) { candidate.Reason = "unsupported_navigation_profile"; continue; }
            var region = Approach(candidate);
            if (region == null) { candidate.Reason = "no_safe_pickup_stance"; continue; }
            candidate.Attempts++;
            target = candidate;
            planned = item.Hitbox;
            plannedRegion = region;
            usedInputs = true;
            navigationUntil = frame.GameUpdate + ApproachBudget;
            navigation = new(executor, "navigate_to_landing_region", JsonSerializer.SerializeToElement(new
            {
                minPlayerLeftX = region.MinPlayerLeftX, maxPlayerLeftX = region.MaxPlayerLeftX, surfaceY = region.SurfaceY
            }), taskId + "/approach-" + candidate.Drop.Slot + "-" + candidate.Attempts);
            return Running();
        }

        if (targets.Count == 0) return End("completed", "no_matching_drops");
        if (!truncated && targets.All(row => row.Reason == "collected")) return End("completed", "drops_collected");
        if (collected.Count == 0 && targets.Any(row => row.Reason == "navigation_timeout"))
            return End("timeout", "navigation_timeout");
        return End("failed", collected.Count > 0 ? "drops_partially_collected" : "drops_not_collected");
    }

    private string PickupBlock(Item item)
    {
        if (!player.CanAcceptItemIntoInventory(item)) return "pickup_prevented";
        if (!player.ItemSpace(item).CanTakeItem) return "inventory_full";
        return null;
    }

    // The native pull rectangle from Player.GrabItems: the Actor hitbox inflated
    // by GetItemGrabRange on every side, intersected with the item hitbox. This is
    // the same test the game runs, so no private reach enlargement is applied.
    private bool Reach(Rectangle hitbox, Item item)
    {
        int range = Math.Max(0, player.GetItemGrabRange(item));
        hitbox.Inflate(range, range);
        return hitbox.Intersects(item.Hitbox);
    }

    // World-pixel bounds of the stance search for one drop. This is the native grab
    // rectangle: the item hitbox grown by the full GetItemGrabRange, clipped to the
    // world. The range is never reduced to keep the search small; only the sample
    // count below is budgeted. See DropStanceSearch for the pure geometry.
    private LandingRegion Approach(Target candidate)
    {
        var item = candidate.Drop.Item;
        var observed = NativeMotionWorld.Observe(player);
        var box = item.Hitbox;
        int range = Math.Max(0, player.GetItemGrabRange(item));
        LandingRegion best = null;
        float distance = float.PositiveInfinity;
        var bounds = DropStanceSearch.Bounds(box, player.width, player.height, range, Main.maxTilesX, Main.maxTilesY);
        if (bounds.Width == 0 || bounds.Height == 0) return null;
        int min = bounds.Left, max = bounds.Right, top = bounds.Top, bottom = bounds.Bottom;
        int actorFeet = (int)(player.position.Y + player.height);
        var rows = new List<int>();
        for (int feet = (top + 7) / 8 * 8; feet <= bottom; feet += 8) rows.Add(feet);
        // Nearer foot levels first, so a truncated budget still keeps the stances
        // that need the shortest trip.
        rows.Sort((a, b) => Math.Abs(a - actorFeet).CompareTo(Math.Abs(b - actorFeet)));
        int probes = 0;
        foreach (int feet in rows)
        {
            if (probes >= ApproachProbeBudget) break;
            int? start = null;
            int previous = min - 1;
            NativeMotionWorld world = null;
            int worldX = 0;
            void Consider(int right)
            {
                if (!start.HasValue) return;
                var band = new LandingRegion(start.Value, right, feet);
                start = null;
                if (candidate.RejectedStances.Contains((band.MinPlayerLeftX, band.SurfaceY))) return;
                float score = band.Distance(observed) + Math.Max(0, 12 - (band.MaxPlayerLeftX - band.MinPlayerLeftX)) * 2;
                if (score < distance) { best = band; distance = score; }
            }
            for (int x = min; x <= max && probes < ApproachProbeBudget; x += 8)
            {
                probes++;
                if (x != previous + 1) Consider(previous);
                var pose = new PhysicsState(x, feet - player.height, 0, 0, true, 0, true);
                if (world == null || x - worldX > 240)
                { world = new NativeMotionWorld(pose); worldX = x; }
                if (world.SafeLanding(pose)) start ??= x;
                else Consider(previous);
                previous = x;
            }
            // A band still open at the end of the row ends at the last probe.
            Consider(previous);
        }
        return best;
    }

    private static float Distance(Rectangle left, Rectangle right) =>
        Vector2.Distance(new(left.Center.X, left.Center.Y), new(right.Center.X, right.Center.Y));

    internal override Result Stop(AiReadOnlyProtocol.Identity frame, string status, string reason)
    {
        var release = base.Stop(frame, status, reason);
        return release.Status == "unknown" || !release.InputsReleased ? End("unknown", release.Reason) with { InputsReleased = false }
            : End(status, reason);
    }

    internal override void Abandon()
    {
        if (ReferenceEquals(ServerDrops.Current, this)) ServerDrops.Current = null;
        base.Abandon();
    }
}
