using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.ID;
using Terraria.Chat;
using Terraria.Localization;
using Microsoft.Xna.Framework;
using VirtualPlayerEngineProbe;
using Identity = CompanionBridge.ServerIpc.AiReadOnlyProtocol.Identity;
using Result = CompanionBridge.ServerIpc.AiAtomicActions.NativeResult;

namespace CompanionBridge.ServerIpc;

internal static class ServerRegroup
{
    private sealed record Session(Player Player, object Socket, string World, string Token);
    private static readonly Dictionary<int, Session> sessions = new();

    private static Player teamActor;
    private static string teamWorld;
    private static ulong nextTeamChange;

    internal static void Clear()
    {
        sessions.Clear();
        ServerItemDrop.Clear();
        teamActor=null;
        teamWorld=null;
        nextTeamChange=0;
    }

    private static IEnumerable<Session> Online(Identity frame)
    {
        for (int i=0; i<Main.maxPlayers; i++)
        {
            var p=Main.player[i];
            if (!p.active || p.whoAmI == frame.Slot) continue;
            var client = Netplay.Clients[p.whoAmI];
            if (client?.State != 10 || client.PendingTermination || client.Socket?.IsConnected() != true) continue;
            if (!sessions.TryGetValue(p.whoAmI, out var s) || s.World != frame.World ||
                !ReferenceEquals(s.Player, p) || !ReferenceEquals(s.Socket, client.Socket))
                sessions[p.whoAmI] = s = new(p, client.Socket, frame.World, Guid.NewGuid().ToString("N"));
            yield return s;
        }
    }

    private static string Eligibility(Player actor, Player other) =>
        actor.team == 0 ? "actor_unteamed" : actor.team != other.team ? "different_team" :
        other.dead || other.ghost ? "teammate_dead" : null;

    internal static Player Recipient(Identity frame,string token) =>
        Online(frame).FirstOrDefault(s=>s.Token==token && Eligibility((Player)frame.Actor,s.Player)==null)?.Player;

    internal static bool InHandoffRange(Player actor, Player other) =>
        Vector2.DistanceSquared(actor.Center, other.Center) <= 48 * 48 &&
        Collision.CanHitLine(actor.Center, 1, 1, other.Center, 1, 1);

    internal static object ActiveTeamBosses(Identity frame)
    {
        var actor = (Player)frame.Actor;
        var team = Online(frame).Where(s => Eligibility(actor,s.Player)==null).ToArray();
        if (team.Length==0) return Array.Empty<object>();
        return Main.npc.Where(n=>n.active && n.life>0 && (n.boss || BossMembers.Supported(n.type)))
            .Select(n=>new { npc=n,member=team.FirstOrDefault(s=>n.target==s.Player.whoAmI || n.playerInteraction[s.Player.whoAmI]) })
            .Where(pair=>pair.member!=null).Take(12).Select(pair=>new {
                entityRef=NativeCombatWorld.Reference(frame,pair.npc),typeId=pair.npc.type,teammateRef=pair.member.Token,
                x=pair.member.Player.Center.X,y=pair.member.Player.Bottom.Y,source="active_boss_target_or_player_hit"
            }).ToArray();
    }

    internal static object Discover(Identity frame)
    {
        var p = (Player)frame.Actor;
        return new { actorTeam = p.team, hasWormholePotion = p.HasUnityPotion(), players = Online(frame).Select(s => new {
            teammateRef = s.Token, name = s.Player.name, team = s.Player.team,
            reason = Eligibility(p, s.Player),
            handoff = Eligibility(p, s.Player) == null ? new { x = s.Player.Bottom.X, y = s.Player.Bottom.Y,
                inRange = InHandoffRange(p, s.Player) } : null,
            lastDrop = ServerItemDrop.Status(frame,s.Token) }).ToArray() };
    }

    internal static Result SetTeam(Identity frame, int team)
    {
        ServerAiNativeActions.Require(frame);
        var p = (Player)frame.Actor;
        int beforeTeam = p.team;
        if (beforeTeam != team && (!p.TeamChangeAllowed() ||
            ReferenceEquals(teamActor,p) && teamWorld==frame.World && Main.GameUpdateCount<nextTeamChange))
            return new("failed","team_change_unavailable",new { operation="set_team",mutationAttempted=false,effectStatus="not_started" },true,InputsAcquired:false);
        if (beforeTeam != team)
        {
            nextTeamChange=Main.GameUpdateCount+(ulong)Math.Max(0,Main.teamCooldownLen);
            teamActor=p;
            teamWorld=frame.World;
        }
        p.team = team;
        NetMessage.SendData(MessageID.PlayerTeam, number:p.whoAmI);
        if (beforeTeam != team)
        {
            // MessageBuffer.GetData (PlayerTeam) notifies the old and new
            // non-neutral teams. The AI has no client to notify at its own slot.
            var message = NetworkText.FromKey(Lang.mp[team == 5 ? 22 : 13 + team].Key, p.name);
            for (int slot = 0; slot < Main.maxPlayers; slot++)
            {
                var observer = Main.player[slot];
                if (!observer.active || slot == p.whoAmI) continue;
                if ((beforeTeam > 0 && observer.team == beforeTeam) || (team > 0 && observer.team == team))
                    ChatHelper.SendChatMessageToClient(message, Main.teamColor[team], slot);
            }
        }
        return new("completed","team_verified",new { beforeTeam, team = p.team },true,p.team == team,false);
    }

    internal static Result Teleport(Identity frame, string token)
    {
        ServerAiNativeActions.Require(frame);
        var s = Online(frame).FirstOrDefault(candidate => candidate.Token == token);
        return s == null ? Refuse("teammate_session_unavailable") : UseWormhole(frame, s.Player, token);
    }

    private static Result Refuse(string reason) => new("failed", reason, new { operation="teleport_to_teammate",
        mutationAttempted=false, effectStatus="not_started" }, true, InputsAcquired:false);

    // Same game-thread mutation used after resolving a live connection and in the isolated native fixture.
    internal static Result UseWormhole(Identity frame, Player other, string token)
    {
        ServerAiNativeActions.Require(frame);
        var p = (Player)frame.Actor;
        if (!frame.CanAct || p.dead || p.ghost) return Refuse("actor_not_alive");
        if (Eligibility(p,other) is {} reason) return Refuse(reason);
        if (!p.HasUnityPotion()) return Refuse("wormhole_potion_missing");
        long Count() => p.inventory.Concat(p.bank4.item).Where(i => i.type == ItemID.WormholePotion).Sum(i => (long)i.stack);
        var destination = other.position;
        long before = Count();
        p.Teleport(destination, 3);
        NetMessage.SendData(MessageID.TeleportEntity, number:0, number2:p.whoAmI,
            number3:p.position.X, number4:p.position.Y, number5:3);
        p.TakeUnityPotion();
        long after = Count();
        bool verified = p.position == destination && before == after + 1;
        if (verified)
            ChatHelper.BroadcastChatMessage(NetworkText.FromKey("Game.HasTeleportedTo", p.name, other.name),
                new Color(250, 250, 0));
        return new(verified ? "completed" : "unknown", verified ? "wormhole_verified" : "wormhole_effect_unconfirmed",
            new { teammateRef=token, team=p.team, targetX=destination.X, targetY=destination.Y,
                actorX=p.position.X, actorY=p.position.Y, before, after }, true, verified, false);
    }
}
