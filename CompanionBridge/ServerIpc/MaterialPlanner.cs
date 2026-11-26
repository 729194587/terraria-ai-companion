using System;
using System.Collections.Generic;
using System.Linq;
using CompanionBridge.Common.Systems;
using Terraria;
using Terraria.ID;

namespace CompanionBridge.ServerIpc;

// Evaluate each alternative against an independent inventory ledger. Only the
// chosen branch commits its consumption, missing materials and batch surplus.
internal sealed class MaterialPlanner
{
    private readonly ILookup<int, int> recipes = Enumerable.Range(0, Recipe.numRecipes)
        .Where(id => !Main.recipe[id].Disabled && !Main.recipe[id].createItem.IsAir)
        .ToLookup(id => Main.recipe[id].createItem.type);
    private readonly HashSet<int> active = new();
    private int visits;

    private sealed class Ledger
    {
        public Dictionary<int, long> Stock = new();
        public Dictionary<int, long> Used = new();
        public Dictionary<int, long> Surplus = new();
        public Dictionary<(int type, string reason), long> Missing = new();
        public List<object> Steps = new();

        public Ledger Copy() => new() { Stock = new(Stock), Used = new(Used), Surplus = new(Surplus),
            Missing = new(Missing), Steps = new(Steps) };

        public long Take(int type, long count)
        {
            long extra = Math.Min(count, Surplus.GetValueOrDefault(type));
            Surplus[type] = Surplus.GetValueOrDefault(type) - extra;
            long held = Math.Min(count - extra, Stock.GetValueOrDefault(type));
            if (held > 0)
            {
                Stock[type] -= held;
                Used[type] = checked(Used.GetValueOrDefault(type) + held);
            }
            return extra + held;
        }

        public (long missing, int steps) Score() => (Missing.Values.Sum(), Steps.Count);
    }

    internal static object Read(Player player, int typeId, int count)
    {
        if (!ContentSamples.ItemsByType.TryGetValue(typeId, out var item) || item.IsAir)
            throw new QueryUnavailableException("unknown_item_type");
        var ledger = new Ledger();
        foreach (var slot in player.inventory.Take(58).Where(i => !i.IsAir))
            ledger.Stock[slot.type] = ledger.Stock.GetValueOrDefault(slot.type) + slot.stack;
        try
        {
            new MaterialPlanner().Expand(typeId, count, ref ledger);
        }
        catch (OverflowException) { throw new QueryUnavailableException("plan_quantity_overflow"); }

        return new { typeId, count, contentKey = Key(typeId), steps = ledger.Steps,
            inventoryUsed = ledger.Used.OrderBy(k => k.Key).Select(k => Amount(k.Key, k.Value)),
            deficits = ledger.Missing.OrderBy(k => k.Key.type).Select(k => new {
                typeId = k.Key.type, contentKey = Key(k.Key.type), count = k.Value, k.Key.reason }) };
    }

    private static string Key(int type) => AtomicQueries.Key("item", type);
    private static object Amount(int type, long count) => new { typeId = type, contentKey = Key(type), count };

    // Return the ancestor blocking a candidate. A closed cycle at its entry
    // becomes an external acquisition only after trying all non-circular routes.
    private int? Expand(int type, long count, ref Ledger state)
    {
        if (++visits > 16000 || active.Count >= 80)
            throw new QueryUnavailableException("plan_search_limit");
        count -= state.Take(type, count);
        if (count == 0) return null;
        if (!active.Add(type)) return type;
        int[] choices = recipes[type].ToArray();
        Ledger best = null;
        int? outerCycle = null;
        foreach (int id in choices)
        {
            if (++visits > 16000) throw new QueryUnavailableException("plan_search_limit");
            var trial = state.Copy();
            int? cycle = TryRecipe(id, count, choices, ref trial);
            if (cycle.HasValue)
            {
                if (cycle != type) outerCycle = cycle;
                continue;
            }
            if (best == null || trial.Score().CompareTo(best.Score()) < 0) best = trial;
        }
        active.Remove(type);
        if (best != null) state = best;
        else if (outerCycle.HasValue) return outerCycle;
        else
        {
            var key = (type, choices.Length == 0 ? "no_recipe" : "cycle");
            state.Missing[key] = checked(state.Missing.GetValueOrDefault(key) + count);
        }
        return null;
    }

    private int? TryRecipe(int id, long count, int[] choices, ref Ledger trial)
    {
        var recipe = Main.recipe[id];
        long yield = recipe.createItem.stack;
        long batches = 1 + (count - 1) / yield;
        var inputs = new List<object>();
        foreach (var ingredient in recipe.requiredItem)
        {
            long need = checked(batches * ingredient.stack);
            // Use any available group members (including partial stacks); deficits
            // use the recipe's representative item for a deterministic plan.
            var accepted = recipe.acceptedGroups.Select(g => RecipeGroup.recipeGroups[g])
                .Where(g => g.ContainsItem(ingredient.type)).SelectMany(g => g.ValidItems)
                .Where(t => t != ingredient.type).Distinct().OrderBy(t => t).Prepend(ingredient.type);
            long representative = 0;
            foreach (int member in accepted)
            {
                long taken = trial.Take(member, need);
                if (member == ingredient.type) representative = taken;
                else if (taken > 0) inputs.Add(Amount(member, taken));
                need -= taken;
                if (need == 0) break;
            }
            if (representative + need > 0) inputs.Add(Amount(ingredient.type, representative + need));
            if (need > 0 && Expand(ingredient.type, need, ref trial) is int cycle) return cycle;
        }
        int type = recipe.createItem.type;
        trial.Surplus[type] = checked(trial.Surplus.GetValueOrDefault(type) + (batches * yield - count));
        trial.Steps.Add(new { typeId = type, contentKey = Key(type), recipeId = id, recipeIds = choices,
            batches, outputPerBatch = yield, inputs, stations = recipe.requiredTile.Select(t => AtomicQueries.Key("tile", t)).ToArray(),
            conditions = recipe.Conditions.Select(c => c.Description.Value).ToArray() });
        return null;
    }
}
