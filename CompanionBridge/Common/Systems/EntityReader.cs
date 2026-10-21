using System;
using System.Collections.Generic;
using Terraria;
using Terraria.ID;

namespace CompanionBridge.Common.Systems;

internal static class EntityReader
{
    // Called beside player/terrain reads on the game thread, before the vanilla update.
    internal static EntityPerception Read(Player observer)
    {
        var candidates = new List<EntityCandidate>();
        foreach (var player in Main.player)
        {
            if (!player.active || player.dead || player.ghost || player.whoAmI == observer.whoAmI) continue;
            candidates.Add(new("player", player.whoAmI, null, "Player", player.name,
                new(player.Center.X, player.Center.Y)));
        }
        foreach (var npc in Main.npc)
        {
            if (!npc.active || npc.life <= 0) continue;
            var flags = new NpcEvidence(npc.friendly, npc.townNPC || NPCID.Sets.ActsLikeTownNPC[npc.type],
                NPCID.Sets.CountsAsCritter[npc.type], npc.damage, npc.CanBeChasedBy(),
                npc.type >= NPCID.Count, NPCID.Sets.ProjectileNPC[npc.type]);
            candidates.Add(new("npc", npc.whoAmI, npc.type, npc.TypeName, npc.GivenOrTypeName,
                new(npc.Center.X, npc.Center.Y), Evidence: flags));
        }
        return EntityCollector.Build(candidates, observer.whoAmI, new(observer.Center.X, observer.Center.Y),
            Main.GameUpdateCount, DateTimeOffset.UtcNow.ToString("O"));
    }
}
