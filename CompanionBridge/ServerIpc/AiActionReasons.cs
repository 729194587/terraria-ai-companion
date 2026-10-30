namespace CompanionBridge.ServerIpc;

// Translate the native executor's failure-only enum at the protocol boundary.
// Success is an outcome, never the textual representation of Failure.None.
internal static class AiActionReasons
{
    internal static string Navigation(string reason) => reason is
        "navigation_stalled" or "navigation_action_limit" or "navigation_search_timeout"
        ? reason : "native_navigation_failed";

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
            "NativeRefused"=>"native_use_refused", "NoPickPower"=>"native_tool_damage_refused",
            "NativeException"=>"native_mining_exception", "ExplicitStop"=>"explicit_stop",
            "DeathOrRetirement"=>"actor_retired", "Deadline"=>"native_mining_deadline",
            _=>"native_mining_unconfirmed"
        }
    };
}
