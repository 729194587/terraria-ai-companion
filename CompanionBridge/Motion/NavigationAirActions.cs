using System;
using System.Collections.Generic;
using System.Linq;
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
    private static KeyValuePair<string,INavigationAirAction>[] registered=Array.Empty<KeyValuePair<string,INavigationAirAction>>();
    internal static bool Register(INavigationAirAction adapter)
    {
        if (string.IsNullOrWhiteSpace(adapter.Key)) throw new ArgumentException("Missing navigation action key");
        if(!adapters.TryAdd(adapter.Key,adapter))return false;
        registered=adapters.ToArray();
        return true;
    }
    private static readonly HashSet<string> disabled=new();
    internal static void Reset()
    {
        adapters.Clear();
        registered=Array.Empty<KeyValuePair<string,INavigationAirAction>>();
        disabled.Clear();
    }

    private static void Disable(string key,Exception error)
    {
        disabled.Add(key);
        Terraria.ModLoader.ModContent.GetInstance<CompanionBridge>().Logger.Warn($"Disabled navigation air action {key}: {error}");
    }

    internal static (NavigationAirImpulse Action,ActionCooldown Cooldowns) Capture(Player player)
    {
        NavigationAirImpulse actions=null;
        ActionCooldown cooldowns=null;
        foreach (var pair in registered)
        {
            if(disabled.Contains(pair.Key))continue;
            NavigationAirImpulse action;
            int cooldown;
            try { action=pair.Value.Capture(player,out cooldown); }
            catch(Exception error) { Disable(pair.Key,error);continue; }
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
    internal static bool Activate(Player player, PhysicsInput input)
    {
        try
        {
            if (!disabled.Contains(input.ImpulseKey) && adapters.TryGetValue(input.ImpulseKey,out var adapter)
                && adapter.Capture(player,out int cooldown)?.Key == input.ImpulseKey && cooldown == 0)
            {
                adapter.Activate(player,input.Direction);
                return true;
            }
        }
        catch(Exception error) { Disable(input.ImpulseKey,error); }
        player.controlLeft=player.controlRight=player.controlJump=player.controlDown=player.controlUp=false;
        player.controlUseItem=player.controlUseTile=false;
        return false;
    }
}
