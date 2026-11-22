using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;

namespace CompanionBridge.Common.Systems;

internal static class ItemInfoQuery
{
    internal static object Read(int typeId)
    {
        if (!ContentSamples.ItemsByType.TryGetValue(typeId, out var item) || item.IsAir)
            return null;

        var tooltip = Lang.GetTooltip(typeId);
        var lines = Enumerable.Range(0, tooltip.Lines).Select(tooltip.GetLine).ToList();
        if (lines.All(string.IsNullOrWhiteSpace)) lines.Clear();
        var data = new Dictionary<string, object>
        {
            ["typeId"] = typeId,
            ["contentKey"] = AtomicQueries.Key("item", typeId),
            ["name"] = item.Name,
            ["mod"] = item.ModItem?.Mod.Name ?? "Terraria",
            ["stats"] = new {
                item.damage, item.defense, pickPower = item.pick, axePower = item.axe * 5,
                hammerPower = item.hammer, rarity = item.rare, valueCopper = item.value,
                item.maxStack, item.mana, item.useTime, item.useAnimation, item.crit,
                item.healLife, item.healMana, item.knockBack, item.accessory, item.consumable
            },
            ["tooltip"] = lines
        };
        var extension = ReadShiftTooltip(item.ModItem, lines);
        if (!string.IsNullOrWhiteSpace(extension))
            data["extraTooltip"] = extension;
        return data;
    }

    private static string ReadShiftTooltip(ModItem item, List<string> lines)
    {
        // Invoke interface getters so explicit implementations and default values both work.
        var contract = item?.GetType().GetInterfaces().FirstOrDefault(i => i.Name == "IHoldShiftTooltipItem");
        if (contract == null) return null;
        // Indicators use a mod-relative key; flavor and extension use item-relative keys.
        object Property(string name) => contract.GetProperty(name)?.GetValue(item);
        void Append(string key)
        {
            if (Language.Exists(key))
                lines.AddRange(Language.GetTextValue(key).ReplaceLineEndings("\n").Split('\n'));
        }
        if (Property("ShowExtensionIndicator") is true && Property("ExtensionIndicatorKey") is string indicator)
            Append(item.Mod.GetLocalizationKey(indicator));
        if (Property("HasFlavorTooltip") is true && Property("FlavorTooltipKey") is string flavor)
            Append(item.GetLocalizationKey(flavor));

        if (Property("TooltipExtensionText") is LocalizedText text && text != LocalizedText.Empty)
            return text.ToString();
        if (Property("TooltipExtensionKey") is not string suffix)
            return null;
        var key = item.GetLocalizationKey(suffix);
        return Language.Exists(key) ? Language.GetTextValue(key) : null;
    }
}
