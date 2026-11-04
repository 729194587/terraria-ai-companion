using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Terraria;
using Terraria.IO;
using Terraria.ModLoader.IO;

namespace VirtualPlayerEngineProbe;

internal static class CharacterIo
{
    [ThreadStatic] private static PlayerFileData saving;
    [ThreadStatic] private static bool active;
    internal static bool Active => active;
    internal static long ExitRequests;
    public static bool IsCharacterFile(PlayerFileData data) => active && saving != null && ReferenceEquals(saving, data);

    internal static T Run<T>(PlayerFileData file, Func<T> action)
    {
        ProbeSession.Reservation.AssertOwner(Program.IsMainThread);
        if (active) throw new InvalidOperationException("Nested character IO rejected.");
        var players = (Player[])Main.player.Clone();
        int myPlayer = Main.myPlayer;
        var activeFile = Main.ActivePlayerFileData;
        saving = file; active = true;
        try { return action(); }
        finally
        {
            saving = null; active = false;
            if (Main.myPlayer != myPlayer || !ReferenceEquals(Main.ActivePlayerFileData, activeFile)
                || Main.player.Length != players.Length || players.Where((p, i) => !ReferenceEquals(Main.player[i], p)).Any())
            {
                ProbeSession.Quarantine("Native character IO changed a published player/active file; reservation retained.");
                throw new InvalidOperationException("Native IO violated player isolation.");
            }
        }
    }

    internal static Player Read(string path)
    {
        var file = Run(null, () => Player.LoadPlayer(path, cloudSave: false));
        var p = file?.Player;
        if (file == null || file.IsCloudSave || p == null || p.loadStatus != 0 || file.customDataFail != null
            || Main.player.Any(existing => ReferenceEquals(existing, p)))
            throw new IOException("Native character load failed or returned a published Player.");
        return p;
    }

    internal static string State(Player p,bool persistentOnly=false)
    {
        // Use native item tags, including ModItem/GlobalItem data; do not invent
        // an alternative save format. This digest only validates native read-back.
        var items = p.inventory.Concat(p.armor).Concat(p.dye).Concat(p.miscEquips).Concat(p.miscDyes)
            .Concat(p.Loadouts.SelectMany(l => l.Armor.Concat(l.Dye))).Select(ItemIO.Save).ToList();
        if(persistentOnly)items.AddRange(p.bank.item.Concat(p.bank2.item).Concat(p.bank3.item).Concat(p.bank4.item).Select(ItemIO.Save));
        var data = new TagCompound {
            ["name"]=p.name, ["hair"]=p.hair, ["hairDye"]=p.hairDye, ["skinVariant"]=p.skinVariant,
            ["life"]=persistentOnly?0:p.statLife, ["mana"]=persistentOnly?0:p.statMana,
            ["crystals"]=p.ConsumedLifeCrystals, ["fruit"]=p.ConsumedLifeFruit, ["manaCrystals"]=p.ConsumedManaCrystals,
            ["colors"]=new[] {p.hairColor,p.eyeColor,p.skinColor,p.shirtColor,p.underShirtColor,p.pantsColor,p.shoeColor}
                .Select(c=>unchecked((int)c.PackedValue)).ToList(),
            ["items"]=items, ["marker"]=p.GetModPlayer<ProbePlayer>().PersistentMarker
        };
        if(persistentOnly){data["difficulty"]=p.difficulty;data["loadout"]=p.CurrentLoadoutIndex;}
        using var bytes = new MemoryStream(); TagIO.ToStream(data, bytes, compress: false);
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
    }
}
