using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;

namespace CompanionBridge.ServerIpc;

internal sealed class BuildingMaterials
{
    private readonly Dictionary<int,int> available = new();
    private readonly Dictionary<int,int> usable = new();
    private readonly Dictionary<int,int> reserved = new();
    private readonly Dictionary<int,int> required = new();

    internal BuildingMaterials(Player player)
    {
        for (int slot=0; slot<58; slot++)
        {
            var item = player.inventory[slot];
            if (item == null || item.IsAir) continue;
            available[item.type] = available.GetValueOrDefault(item.type) + item.stack;
            if (slot<50) usable[item.type] = usable.GetValueOrDefault(item.type) + item.stack;
        }
    }

    internal void RequireOne(int type) => required[type] = required.GetValueOrDefault(type)+1;

    internal string ReserveOne(int type)
    {
        int used = reserved.GetValueOrDefault(type);
        if (used >= usable.GetValueOrDefault(type))
            return used < available.GetValueOrDefault(type) ? "material_not_available_for_native_placement" : "missing_material";
        reserved[type] = used+1;
        return null;
    }

    internal sealed record Budget(int itemType,int required,int available,int missing);
    internal Budget[] Summary() => required.Select(pair => new Budget(pair.Key,pair.Value,
        available.GetValueOrDefault(pair.Key),Math.Max(0,pair.Value-available.GetValueOrDefault(pair.Key)))).ToArray();
}
