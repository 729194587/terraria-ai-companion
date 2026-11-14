namespace CompanionBridge.ServerIpc;

// Translate the native executor's failure-only enum at the protocol boundary.
// Success is an outcome, never the textual representation of Failure.None.
internal static class AiActionReasons
{
    internal static string Navigation(string reason) => reason is
        "navigation_trip_update_budget" or "navigation_trip_search_budget" or "navigation_trip_repeated_state" or
        "navigation_stalled" or "navigation_action_limit" or "navigation_search_timeout" or "navigation_search_expansion_budget" or
        "navigation_surface_node_budget" or "navigation_local_coverage_exhausted"
        ? reason : "native_navigation_failed";

    internal static bool NavigationSearchIncomplete(string reason)=>reason is
        "navigation_trip_search_budget" or "navigation_search_timeout" or "navigation_search_expansion_budget"
        or "navigation_surface_node_budget" or "navigation_local_coverage_exhausted"
        or "navigation_edit_window_exceeded" or "navigation_edit_budget_exhausted";

    // Only deterministic planner/profile refusal and confirmed no-progress can hand off.
    // Search limits do not establish an obstruction and must not trigger excavation.
    internal static string NavigationRecoveryKind(string reason) => reason == "navigation_stalled" ? "no_progress"
        : reason?.StartsWith("no safe local route: ",System.StringComparison.Ordinal)==true ? "no_route"
        : reason?.StartsWith("unsupported navigation state: ",System.StringComparison.Ordinal)==true ? "profile_refused"
        : null;

    internal static string Mining(string status,string failure)=>status switch
    {
        "completed"=>"native_tile_destroyed",
        "running"=>"native_mining_running",
        "unknown"=>"native_mining_state_unknown",
        _=>failure switch
        {
            "InvalidTarget"=>"invalid_target", "UnsupportedTarget"=>"unsupported_target",
            "OutOfRange"=>"out_of_range", "InvalidTool"=>"invalid_tool", "Busy"=>"actor_busy",
            "ActorUnavailable"=>"actor_unavailable", "BindingChanged"=>"actor_binding_changed",
            "ItemChanged"=>"selected_item_changed", "TargetChanged"=>"target_changed",
            "NativeRefused"=>"native_use_refused", "TargetProtected"=>"target_protected", "NoPickPower"=>"insufficient_pick_power",
            "NativeException"=>"native_mining_exception", "ExplicitStop"=>"explicit_stop",
            "DeathOrRetirement"=>"actor_retired", "Deadline"=>"native_mining_deadline",
            _=>"native_mining_unconfirmed"
        }
    };
}
