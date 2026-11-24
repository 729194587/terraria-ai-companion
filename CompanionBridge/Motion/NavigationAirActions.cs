using System;
using System.Collections.Generic;
using Terraria;

namespace CompanionBridge.Motion;

// Capture is read-only and called on the game thread. HorizontalSpeed is in
// px/update along the input direction, VerticalSpeed is signed in world space.
// Activation sets velocity before native horizontal movement and gravity.
public interface INavigationAirAction
{
    string Key { get; }
    NavigationAirImpulse Capture(Player player, out int remainingCooldown);
    void Activate(Player player, int direction);
}

internal static class NavigationAirActions
{
    private static readonly Dictionary<string,INavigationAirAction> adapters = new();
    internal static bool Register(INavigationAirAction adapter)
    {
        if (string.IsNullOrWhiteSpace(adapter.Key)) throw new ArgumentException("Missing navigation action key");
        return adapters.TryAdd(adapter.Key, adapter);
    }
    internal static void Reset() => adapters.Clear();

    internal static (NavigationAirImpulse Action,ActionCooldown Cooldowns) Capture(Player player)
    {
        NavigationAirImpulse actions=null;
        ActionCooldown cooldowns=null;
        foreach (var pair in adapters)
        {
            var action = pair.Value.Capture(player, out int cooldown);
            if (action != null && action.Key == pair.Key && cooldown >= 0 && cooldown <= action.CooldownTicks
                && action.CooldownTicks > 0 && float.IsFinite(action.HorizontalSpeed)
                && action.HorizontalSpeed is > 0 and <= 32 && float.IsFinite(action.VerticalSpeed)
                && Math.Abs(action.VerticalSpeed) <= 32)
            {
                actions=action with{Next=actions};
                if(cooldown>0) cooldowns=new(pair.Key,PhysicsParameters.PlanningCooldown(cooldown),cooldowns);
            }
        }
        return (actions,cooldowns);
    }
    internal static void Activate(Player player, PhysicsInput input)
    {
        if (adapters.TryGetValue(input.ImpulseKey,out var adapter)
            && adapter.Capture(player,out int cooldown)?.Key == input.ImpulseKey && cooldown == 0)
            adapter.Activate(player,input.Direction);
    }
}
