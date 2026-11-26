using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.GameContent.ItemDropRules;
using Terraria.ID;

namespace CompanionBridge.Common.Systems;

internal static class ItemSourcesQuery
{
    private static ItemDropDatabase database;
    private static ILookup<int, (int netId, DropRateInfo drop)> sources;

    internal static object Read(int typeId)
    {
        if (!ContentSamples.ItemsByType.TryGetValue(typeId, out var item) || item.IsAir)
            return null;

        if (database != Main.ItemDropsDB)
        {
            sources = ReportDrops().ToLookup(row => row.drop.itemId);
            database = Main.ItemDropsDB;
        }

        var rows = sources[typeId].Select(row =>
        {
            var npc = ContentSamples.NpcsByNetId[row.netId];
            var drop = row.drop;
            return new {
                netId = row.netId, npcType = npc.type, name = Lang.GetNPCNameValue(row.netId),
                contentKey = npc.ModNPC?.FullName ?? "Terraria/" + NPCID.Search.GetName(row.netId),
                boss = npc.boss || NPCID.Sets.ShouldBeCountedAsBoss[npc.type],
                probability = drop.dropRate, minStack = drop.stackMin, maxStack = drop.stackMax,
                conditions = drop.conditions?.Select(c => new {
                    id = c.GetType().FullName, description = c.GetConditionDescription()
                }).ToArray() ?? []
            };
        }).ToArray();
        return new { typeId, contentKey = AtomicQueries.Key("item", typeId), sources = rows };
    }

    private static IEnumerable<(int netId, DropRateInfo drop)> ReportDrops()
    {
        foreach (int netId in ContentSamples.NpcsByNetId.Keys.OrderBy(id => id))
        {
            var reports = new List<DropRateInfo>();
            foreach (var rule in Main.ItemDropsDB.GetRulesForNPCID(netId, false))
                rule.ReportDroprates(reports, new DropRateInfoChainFeed(1f));
            foreach (var drop in reports)
                yield return (netId, drop);
        }
    }
}
