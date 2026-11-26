using System;
using System.Collections.Generic;
using System.Reflection;
using Terraria;
using Terraria.ModLoader;

namespace VirtualPlayerEngineProbe;

// The server reads carried items and applies ordinary potion effects only to its own
// Player. ImproveGame's client update also writes local UI and global scene flags.
internal sealed class ImproveGamePotionBuffs
{
    internal static ImproveGamePotionBuffs Current { get; private set; }
    private const int Duration = 30; // ImproveGame's native passive refresh duration.
    private readonly Mod mod;
    private readonly Func<object> config;
    private readonly FieldInfo enabled, requirement, share, range, potionMappings, conflicts;
    private readonly PropertyInfo blacklist;
    private readonly MethodInfo blacklistedTypes;
    private readonly Func<List<Item>, IEnumerable<Item>, List<Item>> collect;
    private readonly Func<Item, List<int>> buffTypes;

    internal static void Install(Action<Action, Action> install)
    {
        if (!ModLoader.TryGetMod("ImproveGame", out var mod)) return;
        var adapter = new ImproveGamePotionBuffs(mod);
        install(() => Current = adapter, () => Current = null);
    }

    private ImproveGamePotionBuffs(Mod mod)
    {
        this.mod = mod;
        Type ModType(string name) => mod.Code.GetType("ImproveGame." + name, throwOnError: true);
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        static FieldInfo Field<T>(Type owner, string name)
        {
            var field = owner.GetField(name, flags);
            return field?.FieldType == typeof(T) ? field : throw new MissingFieldException(owner.FullName, name);
        }
        var settings = ModType("Common.Configs.ImproveConfigs");
        config = settings.GetProperty("Instance", flags).GetMethod.CreateDelegate<Func<object>>();
        enabled = Field<bool>(settings, "NoConsume_Potion");
        requirement = Field<int>(settings, "NoConsume_PotionRequirement");
        share = Field<bool>(settings, "ShareInfBuffs");
        range = Field<int>(settings, "ShareRange");
        var integration = ModType("Common.ModSystems.ModIntegrationsSystem");
        potionMappings = Field<Dictionary<int, List<int>>>(integration, "ModdedPotionBuffs");
        conflicts = Field<Dictionary<int, List<int>>>(integration, "ModdedBuffConflicts");
        var infinitePlayer = ModType("Modules.InfiniteBuff.InfiniteBuffModPlayer");
        blacklist = infinitePlayer.GetProperty("Blacklist", flags);
        blacklistedTypes = blacklist.PropertyType.GetMethod("GetBuffTypes", flags, Type.EmptyTypes);
        if (blacklistedTypes?.ReturnType != typeof(HashSet<int>))
            throw new MissingMethodException(blacklist.PropertyType.FullName, "GetBuffTypes");
        collect = infinitePlayer.GetMethod("CollectAvailableItems", flags)
            .CreateDelegate<Func<List<Item>, IEnumerable<Item>, List<Item>>>();
        buffTypes = ModType("Common.GlobalItems.ApplyBuffItem").GetMethod("GetItemBuffTypes", flags)
            .CreateDelegate<Func<Item, List<int>>>();
    }

    internal void Refresh(Player actor)
    {
        if (Main.GameUpdateCount % (Duration / 2) != 0 || !NativeSurvival.Owns(actor) || actor.dead || actor.ghost) return;
        var settings = config();
        if (!(bool)enabled.GetValue(settings)) return;
        var items = collect(new List<Item>(), actor.inventory);
        if (actor.team != 0 && (bool)share.GetValue(settings))
        {
            int tiles = (int)range.GetValue(settings);
            foreach (var peer in Main.ActivePlayers)
            {
                if (peer == actor || peer.team != actor.team || (tiles != -1 && peer.Distance(actor.Center) > tiles * 16f))
                    continue;
                var client = Netplay.Clients[peer.whoAmI];
                if (client?.State == 10 && !client.PendingTermination && client.IsConnected())
                    collect(items, peer.inventory);
            }
        }
        if (items.Count == 0) return;
        var minimum = (int)requirement.GetValue(settings);
        var registered = (Dictionary<int, List<int>>)potionMappings.GetValue(null);
        var effects = new HashSet<int>();
        foreach (var item in items)
        {
            // The upstream collector also recognizes furniture. Only the potion branch
            // of GetItemBuffTypes belongs here (including registered mod potions).
            if (item.stack >= minimum && ((item.buffType > 0 && item.buffTime >= 3600) || registered.ContainsKey(item.type)))
                effects.UnionWith(buffTypes(item));
        }
        if (effects.Count == 0) return;
        var recipient = actor.GetModPlayer(mod.Find<ModPlayer>("InfiniteBuffModPlayer"));
        effects.ExceptWith((HashSet<int>)blacklistedTypes.Invoke(blacklist.GetValue(recipient), null));
        var mapping = (Dictionary<int, List<int>>)conflicts.GetValue(null);
        var excluded = new HashSet<int>();
        foreach (int type in effects)
            if (mapping.TryGetValue(type, out var incompatible)) excluded.UnionWith(incompatible);
        effects.ExceptWith(excluded);
        foreach (int type in effects)
        {
            if (type <= 0 || type >= BuffLoader.BuffCount) continue;
            int slot = actor.FindBuffIndex(type);
            if (slot < 0 || actor.buffTime[slot] < Duration)
                actor.AddBuff(type, Duration, quiet: true);
        }
        foreach (int type in excluded)
            if (type > 0 && type < BuffLoader.BuffCount) actor.ClearBuff(type);
    }
}
