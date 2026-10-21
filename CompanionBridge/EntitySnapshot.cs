using System;
using System.Collections.Generic;
using System.Linq;

namespace CompanionBridge;

internal sealed record NpcEvidence(bool Friendly, bool TownNpc, bool Critter, int Damage,
    bool Chaseable, bool Modded, bool ProjectileNpc);
internal sealed record EntityCandidate(string Kind, int Id, int? TypeId, string TypeName,
    string Name, StateVector Center, bool Active = true, NpcEvidence Evidence = null);
internal sealed record NearbyEntity(string Kind, int Id, int? TypeId, string TypeName, string Name,
    bool NameTruncated, string Classification, string ClassificationBasis, StateVector CenterWorldPixels,
    StateVector DeltaPixels, float DistancePixels, string Horizontal, string Vertical, NpcEvidence Evidence);
internal sealed record EntityPerception(bool Available, string SampledAt, ulong GameUpdate,
    int ObserverPlayerId, StateVector ReferenceCenterWorldPixels, float RadiusPixels, int Limit,
    int MatchedCount, bool Truncated, NearbyEntity[] Items)
{
    public string Scope => "Client-known active living entities only; outside radius, unsynced entities and omitted entries are unknown. No visibility or line-of-sight test.";
}

internal static class EntityCollector
{
    internal const float Radius = 480;
    internal const int Limit = 12;

    internal static (string Kind, string Basis) Classify(NpcEvidence flags)
    {
        if (flags == null) return ("unknown", "NPC flags unavailable");
        if (flags.ProjectileNpc) return ("unknown", "Projectile-like NPC; creature affiliation not inferred");
        if (flags.TownNpc) return ("friendly_npc", "Engine town-NPC flag");
        if (flags.Critter) return flags.Damage == 0
            ? ("critter", "Critter registry and zero contact damage; not classified as an enemy")
            : ("unknown", "Critter registry conflicts with positive contact damage");
        if (flags.Friendly) return ("friendly_npc", "Engine friendly flag");
        if (!flags.Critter && !flags.Modded && flags.Damage > 0 && flags.Chaseable)
            return ("hostile_creature", "Inferred: vanilla, nonfriendly, noncritter, damaging and chaseable; current aggression not verified");
        return ("unknown", "Insufficient evidence; friendly=false alone does not establish hostility");
    }

    internal static EntityPerception Build(IEnumerable<EntityCandidate> candidates, int observerId,
        StateVector center, ulong update, string sampledAt)
    {
        var items = new List<NearbyEntity>();
        foreach (var candidate in candidates)
        {
            if (!candidate.Active || candidate.Kind == "player" && candidate.Id == observerId) continue;
            float dx = candidate.Center.X - center.X, dy = candidate.Center.Y - center.Y;
            float distance = MathF.Sqrt(dx * dx + dy * dy);
            if (!float.IsFinite(distance) || distance > Radius) continue;
            var classification = candidate.Kind == "player"
                ? (Kind: "other_player", Basis: "Player identity; PvP hostility not evaluated") : Classify(candidate.Evidence);
            string name = candidate.Name ?? "", typeName = candidate.TypeName ?? "";
            items.Add(new(candidate.Kind, candidate.Id, candidate.TypeId,
                typeName.Length > 64 ? typeName[..64] : typeName, name.Length > 64 ? name[..64] : name,
                name.Length > 64 || typeName.Length > 64, classification.Kind, classification.Basis,
                candidate.Center, new(dx, dy), distance,
                dx < 0 ? "left" : dx > 0 ? "right" : "aligned",
                dy < 0 ? "above" : dy > 0 ? "below" : "aligned", candidate.Evidence));
        }
        var sorted = items.OrderBy(item => item.DistancePixels).ThenBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Id).ToArray();
        return new(true, sampledAt, update, observerId, center, Radius, Limit, sorted.Length,
            sorted.Length > Limit, sorted.Take(Limit).ToArray());
    }
}
