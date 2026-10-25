using System;
using Terraria;
using Terraria.ID;
using EngineProgram = Terraria.Program;

namespace VirtualPlayerEngineProbe;

internal static class ActorControl
{
    internal static ActorAction<Player> Current;
    private static ActorAction<Player> executing;
    private static bool injected;
    internal static void Reset() { Current = executing = null; injected = false; }

    // Called under the lifecycle Gate, only on the engine game thread. A known
    // dead/inactive AI may release inputs, but an unknown/replaced Player may not.
    private static bool Owned(ActorAction<Player> context)
    {
        var view = ProbeSession.Reservation.Snapshot;
        var p = context.Actor;
        return context.Matches(ProbeSession.Actor, ProbeSession.WorldSession, ProbeSession.ActorGeneration)
            && Main.netMode == NetmodeID.Server && Main.dedServ && view != null && p.whoAmI == view.Slot
            && Main.player != null && view.Slot < Main.player.Length && ReferenceEquals(Main.player[view.Slot], p)
            && Netplay.Clients != null && view.Slot < Netplay.Clients.Length
            && VirtualPlayerEngineProbe.ConnectionIdle(Netplay.Clients[view.Slot]);
    }
    private static void Healthy(Player p)
    {
        NetworkStateSync.Require(p);
        if (Main.gameMenu || Netplay.Disconnect || p.ghost || p.mount.Active || p.frozen || p.stoned || p.webbed
            || p.tongued || p.shimmering || p.isControlledByFilm || p.sleeping.isSleeping || p.sitting.isSitting)
            throw new InvalidOperationException("Actor is not in a supported walking state.");
        if (!float.IsFinite(p.position.X) || !float.IsFinite(p.position.Y)
            || !float.IsFinite(p.velocity.X) || !float.IsFinite(p.velocity.Y))
            throw new InvalidOperationException("Actor physics state is not finite.");
    }
    private static void Inputs(Player p, bool right)
    {
        p.controlLeft = p.controlUp = p.controlDown = p.controlJump = p.controlUseItem = p.controlUseTile = false;
        p.controlRight = right;
    }
    private static void Release(ActorAction<Player> context, bool brake)
    {
        if (!Owned(context)) return; // Never mutate an object whose ownership is unknown.
        Inputs(context.Actor, false);
        if (brake) context.Actor.velocity.X = 0; // Preserve gravity/vertical velocity; never set position.
    }
    private static void Captured(Player expected, long generation, string world)
    {
        if (expected == null || !ReferenceEquals(expected, ProbeSession.Actor)
            || generation != ProbeSession.ActorGeneration || world != ProbeSession.WorldSession)
            throw new InvalidOperationException("Actor changed since the command was queued; command discarded.");
    }
    internal static void Start(Player expected, long generation, string world)
    {
        ProbeSession.Reservation.AssertOwner(EngineProgram.IsMainThread);
        lock (ProbeSession.Reservation.Gate)
        {
            Captured(expected, generation, world);
            Healthy(expected);
            if (Current?.Phase == ActionPhase.Running) throw new InvalidOperationException("An action is already running.");
            if (Math.Abs(expected.velocity.X) > 0.01f) throw new InvalidOperationException("Wait until the actor is horizontally stationary.");
            Current = new ActorAction<Player>(expected, world, generation, expected.position.X);
            Emit("control-start", new { world, generation, slot = expected.whoAmI, x = expected.position.X,
                targetPixels = ActorAction<Player>.Distance, maxUpdates = ActorAction<Player>.MaxUpdates });
        }
    }
    internal static void Stop(Player expected, long generation, string world)
    {
        ProbeSession.Reservation.AssertOwner(EngineProgram.IsMainThread);
        lock (ProbeSession.Reservation.Gate)
        {
            Captured(expected, generation, world);
            if (Current == null || Current.Phase != ActionPhase.Running) return;
            if (!Owned(Current)) { Invalidate(Current); return; }
            Current.Stop(ActionPhase.Cancelled, "explicit stop");
            Release(Current, true);
            NetworkStateSync.Full(expected);
            Result(Current);
        }
    }
    internal static void Retire(Player expected)
    {
        ProbeSession.Reservation.AssertOwner(EngineProgram.IsMainThread);
        if (Current == null || !ReferenceEquals(Current.Actor, expected)) return;
        if (!Owned(Current)) { Invalidate(Current); return; }
        Current.Stop(ActionPhase.Cancelled, "actor retired"); Release(Current, true); Current = null;
    }
    internal static void CheckBinding()
    {
        if (Current?.Phase != ActionPhase.Running) return;
        ProbeSession.Reservation.AssertOwner(EngineProgram.IsMainThread);
        lock (ProbeSession.Reservation.Gate) if (!Owned(Current)) Invalidate(Current);
    }
    private static void Invalidate(ActorAction<Player> context)
    {
        context.Stop(ActionPhase.Stale, "world/incarnation/ownership changed; no Player writes");
        // Existing G1 code retains/quarantines an unknown slot. Do not adopt it.
        if (context.Matches(ProbeSession.Actor, ProbeSession.WorldSession, ProbeSession.ActorGeneration))
            ProbeSession.Quarantine("G4 actor slot/connection ownership changed; no control writes.");
        Result(context);
    }

    // Actual native call site: Player.Update -> PostUpdateRunSpeeds ->
    // HorizontalMovement. SetControls is local-player-only and is NOT used.
    internal static void BeforeMovement(Player p)
    {
        var context = executing;
        if (context == null || !ReferenceEquals(context.Actor, p)) return;
        ProbeSession.Reservation.AssertOwner(EngineProgram.IsMainThread);
        if (!Owned(context)) throw new InvalidOperationException("Control ownership changed during native update.");
        Healthy(p);
        if (context.Phase != ActionPhase.Running) throw new InvalidOperationException("Control action stopped during native update.");
        Inputs(p, true); injected = true;
    }

    internal static void Update(On_Player.orig_Update orig, Player p, int slot)
    {
        var context = Current;
        if (context == null || context.Phase != ActionPhase.Running || !ReferenceEquals(context.Actor, p))
        { orig(p, slot); return; } // All human calls, including their exceptions, remain native.
        ProbeSession.Reservation.AssertOwner(EngineProgram.IsMainThread);
        lock (ProbeSession.Reservation.Gate)
        {
            if (!Owned(context)) { Invalidate(context); orig(p, slot); return; }
            if (executing != null) throw new InvalidOperationException("Reentrant controlled Player.Update rejected.");
            try
            {
                Healthy(p);
                if (slot != p.whoAmI) throw new InvalidOperationException("Native update slot differs from bound actor.");
                context.Begin(Main.GameUpdateCount);
                executing = context; injected = false;
                orig(p, slot); // Exactly one natural engine update; never call Player.Update manually.
                if (!Owned(context)) { Invalidate(context); return; }
                Healthy(p);
                if (!injected) throw new InvalidOperationException("Native server movement hook was not reached.");
                context.Observe(p.position.X);
                if (context.Phase != ActionPhase.Running)
                {
                    Release(context, true);
                    NetworkStateSync.Full(p); // Native final position + released input + zero horizontal velocity.
                    Result(context);
                }
                else NetworkStateSync.Tick(p); // G2 rate limit; capture input before the finally release.
            }
            catch (Exception e)
            {
                context.Stop(ActionPhase.Failed, e.Message);
                Release(context, true);
                if (Owned(context) && !ProbeSession.Quarantined && VirtualPlayerEngineProbe.Fault == null)
                {
                    try { NetworkStateSync.Full(p); }
                    catch (Exception syncError) { NetworkStateSync.Fail(syncError); }
                }
                Result(context);
            }
            finally
            {
                Release(context, context.Phase != ActionPhase.Running);
                executing = null; injected = false;
            }
        }
    }
    private static void Emit(string name, object detail)
    {
        try { ProbeSession.Log(name, detail); } catch { /* Diagnostics cannot leave controls pressed or start another action. */ }
    }
    private static void Result(ActorAction<Player> context) => Emit("control-result", new {
        world = context.World, generation = context.Generation, slot = context.Actor.whoAmI,
        phase = context.Phase.ToString(), startX = context.StartX, x = context.Actor.position.X,
        vx = context.Actor.velocity.X, right = context.Actor.controlRight,
        dx = context.DeltaX, updates = context.Updates, reason = context.Reason });
    internal static string Summary()
    {
        if (Current == null) return "AI 尚无移动动作。";
        string phase = Current.Phase switch { ActionPhase.Running => "进行中", ActionPhase.Completed => "已完成", ActionPhase.TimedOut => "超时，可能遇阻",
            ActionPhase.Cancelled => "已停止", ActionPhase.Failed => "失败，详情见日志", _ => "已失效" };
        return $"AI {phase}：向右 {Current.DeltaX:F1}px（{Current.DeltaX / 16:F1}格），{Current.Updates} 次原生更新。";
    }
}
