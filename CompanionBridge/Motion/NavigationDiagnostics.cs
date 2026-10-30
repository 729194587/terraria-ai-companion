using System;
using System.Text.Json;

namespace CompanionBridge.Motion;

// Called at the game adapter boundary, after its timed prediction/verification.
// The same snapshot is used by get_navigation. Never serialize on every frame.
internal sealed class NavigationDiagnostics
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly Action<string> _write;
    private NavigationSnapshot _previous;
    internal int WriteErrors { get; private set; }

    internal NavigationDiagnostics(Action<string> write) => _write = write;

    internal void Observe(NavigationSnapshot current)
    {
        bool sameTask = _previous != null && current.Session == _previous.Session
            && current.Generation == _previous.Generation && current.TargetX == _previous.TargetX
            && current.TargetRegion == _previous.TargetRegion;
        if (current.PlanNumber > 0 && (!sameTask || current.PlanNumber != _previous.PlanNumber))
            Write("navigation_plan", current);
        if (sameTask && current.CompletedActions > _previous.CompletedActions)
            Write("navigation_checkpoint", current);
        if (current.Status is "arrived" or "failed" or "unknown" or "confirming_landing"
            && (!sameTask || current.Status != _previous.Status))
            Write("navigation_state", current);
        if (current.Status == "idle" && _previous?.Session > 0)
            // Keep the cancelled task's identity and last plan in its log receipt;
            // the actual idle snapshot clears all stale planning diagnostics.
            Write("navigation_cancel", _previous with { Status = "idle", Reason = current.Reason });
        _previous = current;
    }

    private void Write(string eventName, NavigationSnapshot snapshot)
    {
        try { _write("Navigation " + JsonSerializer.Serialize(new { @event = eventName,
            goalKind = snapshot.TargetRegion != null ? "landing_region" : "horizontal", navigation = snapshot }, JsonOptions)); }
        catch { WriteErrors++; } // Observability must not change navigation inputs or outcomes.
    }
}
