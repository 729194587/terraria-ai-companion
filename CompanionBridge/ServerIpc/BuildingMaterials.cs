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

    private readonly Dictionary<int,int> outstanding = new();
    internal void RequireOne(int type,bool matched=false)
    {
        required[type]=required.GetValueOrDefault(type)+1;
        if(!matched)outstanding[type]=outstanding.GetValueOrDefault(type)+1;
    }

    internal bool CanHold(int type)=>usable.GetValueOrDefault(type)>0;

    internal string ReserveOne(int type)
    {
        int used = reserved.GetValueOrDefault(type);
        if (used >= usable.GetValueOrDefault(type))
            return used < available.GetValueOrDefault(type) ? "material_not_available_for_native_placement" : "missing_material";
        reserved[type] = used+1;
        return null;
    }

    internal object[] Summary(IReadOnlyDictionary<int,int> consumed=null) => required.Select(pair =>
    {
        int type=pair.Key, carried=usable.GetValueOrDefault(type), total=available.GetValueOrDefault(type);
        return (object)new{itemType=type,required=pair.Value,available=total,carriedUsable=carried,
            carriedUnavailable=total-carried,storedNearby=(int?)null,reserved=reserved.GetValueOrDefault(type),
            consumed=consumed?.GetValueOrDefault(type)??0,missing=Math.Max(0,outstanding.GetValueOrDefault(type)-carried)};
    }).ToArray();
}
