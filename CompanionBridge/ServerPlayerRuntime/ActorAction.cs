using System;

namespace VirtualPlayerEngineProbe;

public enum ActionPhase { Running, Completed, TimedOut, Cancelled, Failed, Stale }

// Runtime incarnation, not a save-file generation: even reloading the same
// profile creates a new binding. No slot number can substitute for Actor.
public sealed class ActorAction<T> where T : class
{
    public readonly T Actor;
    public readonly string World;
    public readonly long Generation;
    public readonly float StartX;
    public const float Distance = 64;
    public const int MaxUpdates = 90;
    public ActionPhase Phase { get; private set; } = ActionPhase.Running;
    public int Updates { get; private set; }
    public float DeltaX { get; private set; }
    public string Reason { get; private set; } = "";
    private ulong? lastTick;

    public ActorAction(T actor, string world, long generation, float x)
    {
        if (actor == null || string.IsNullOrEmpty(world) || world == "none" || generation <= 0 || !float.IsFinite(x))
            throw new ArgumentException("A live actor incarnation and finite position are required.");
        Actor = actor; World = world; Generation = generation; StartX = x;
    }
    public bool Matches(T actor, string world, long generation) => ReferenceEquals(Actor, actor)
        && World == world && Generation == generation;
    public void Begin(ulong tick)
    {
        if (Phase != ActionPhase.Running) throw new InvalidOperationException("Action is not running.");
        if (lastTick.HasValue && tick <= lastTick.Value) throw new InvalidOperationException("Duplicate or reversed native frame.");
        lastTick = tick; Updates++;
    }
    public void Observe(float x)
    {
        if (Phase != ActionPhase.Running) return;
        if (!float.IsFinite(x)) throw new InvalidOperationException("Non-finite native position.");
        DeltaX = x - StartX;
        if (DeltaX >= Distance) Stop(ActionPhase.Completed, "target reached");
        else if (Updates >= MaxUpdates) Stop(ActionPhase.TimedOut, "blocked or insufficient movement");
    }
    public void Stop(ActionPhase phase, string reason) { Phase = phase; Reason = reason; }
}
