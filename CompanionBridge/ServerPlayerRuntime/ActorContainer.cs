using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Terraria;
using Terraria.ID;
using CompanionBridge.ServerIpc;
using Result = CompanionBridge.ServerIpc.AiAtomicActions.NativeResult;

namespace VirtualPlayerEngineProbe;

internal static partial class ActorContainer
{
    private sealed record Target(int Index, Chest Record, int X, int Y, int Type, int FrameX);
    private sealed class Session(AiReadOnlyProtocol.Identity identity, Target target)
    {
        internal readonly AiReadOnlyProtocol.Identity Identity = identity;
        internal readonly Target Target = target;
        internal string Token;
        internal long ObservedAt;
    }

    private static Session opened;

    // Runtime reset is after retirement or before a new reservation is armed.
    internal static void Reset() => opened = null;

    internal static void ExpireForDiagnostic()
    {
        if (ServerContainerFixture.TransferEnabled && opened != null) opened.ObservedAt -= 30_000;
    }

    private static bool Vanilla(Tile tile) => tile.HasTile && tile.TileType is TileID.Containers or TileID.Containers2;

    private static string Resolve(Player player, int x, int y, out Target target)
    {
        target = null;
        if (!WorldGen.InWorld(x, y, 1)) return "invalid_target";
        var tile = Main.tile[x, y];
        if (!Vanilla(tile) || tile.TileFrameX < 0 || tile.TileFrameX % 18 != 0
            || tile.TileFrameY is not (0 or 18)) return "unsupported_container";
        x -= tile.TileFrameX / 18 % 2;
        y -= tile.TileFrameY / 18;
        if (!WorldGen.InWorld(x, y, 1) || !WorldGen.InWorld(x + 1, y + 1, 1)) return "invalid_target";
        var anchor = Main.tile[x, y];
        if (!Vanilla(anchor) || anchor.TileFrameX < 0 || anchor.TileFrameX % 36 != 0)
            return "invalid_container_footprint";
        for (int dx = 0; dx < 2; dx++)
        for (int dy = 0; dy < 2; dy++)
        {
            var part = Main.tile[x + dx, y + dy];
            if (!part.HasTile || part.IsActuated || part.TileType != anchor.TileType
                || part.TileFrameX != anchor.TileFrameX + dx * 18 || part.TileFrameY != dy * 18)
                return "invalid_container_footprint";
        }
        if (Chest.IsLocked(x, y)) return "container_locked";
        if (!player.IsInInteractionRangeToMultiTileHitbox(x, y)) return "out_of_native_interaction_range";
        int index = Chest.FindChest(x, y);
        if (index < 0 || Main.chest[index] is not { item.Length: 40 } chest || chest.item.Any(item => item == null))
            return "container_record_missing";
        target = new(index, chest, x, y, anchor.TileType, anchor.TileFrameX);
        return null;
    }

    private static bool Matches(Session session, AiReadOnlyProtocol.Identity identity) =>
        ReferenceEquals(session.Identity.Actor, identity.Actor) && session.Identity.World == identity.World
        && session.Identity.Generation == identity.Generation && session.Identity.Slot == identity.Slot;

    private static string Check(Session session, AiReadOnlyProtocol.Identity identity)
    {
        if (session == null || !Matches(session, identity)) return "container_not_open";
        var player = (Player)identity.Actor;
        var target = session.Target;
        if (!player.active || player.dead || player.ghost) return "actor_not_alive";
        if (player.chest != target.Index || player.chestX != target.X || player.chestY != target.Y)
            return "container_context_changed";
        string error = Resolve(player, target.X, target.Y, out var current);
        if (error != null) return error;
        if (current != target || !ReferenceEquals(current.Record, target.Record)) return "container_replaced";
        int owner = Chest.UsingChest(target.Index);
        return owner == identity.Slot && !Main.player.Take(255).Any(other =>
            other.active && other.whoAmI != identity.Slot && other.chest == target.Index)
            ? null : "container_in_use";
    }

    private static void Sync(Player player) =>
        NetMessage.SendData(MessageID.SyncPlayerChestIndex, -1, -1, null, player.whoAmI, player.chest);

    private static void Close(Player player)
    {
        opened = null;
        player.chest = -1;
        player.chestX = player.chestY = 0;
        Sync(player);
    }

    internal static void Tick(AiReadOnlyProtocol.Identity identity)
    {
        if (opened == null) return;
        if (identity == null || !ServerAiNativeActions.Owned(identity))
        {
            opened.Token = null;
            return; // Keep the owned context available for native retirement.
        }
        if (!Matches(opened, identity))
        {
            if (ReferenceEquals(opened.Identity.Actor, identity.Actor)) Close((Player)identity.Actor);
            else opened = null;
            return;
        }
        if (Check(opened, identity) != null) Close((Player)identity.Actor);
    }

    internal static void Retire(Player player)
    {
        if (opened == null || !ReferenceEquals(opened.Identity.Actor, player)) return;
        NetworkStateSync.Require(player, inactiveAllowed: true);
        Close(player);
    }

    private static string SlotToken(AiReadOnlyProtocol.Identity identity, Item item, string token, int slot) =>
        ActorInventory.Token(identity, item, $"container:{token}:{slot}");

    private static bool Fresh(Session session, AiReadOnlyProtocol.Identity identity, string token) =>
        Check(session, identity) == null && session.Token == token
        && Environment.TickCount64 - session.ObservedAt < 30_000;

    internal static object Inspect(AiReadOnlyProtocol.Identity identity)
    {
        var session = opened;
        string reason = Check(session, identity);
        if (reason != null) return new { status = "unavailable", reason, container = (object)null };
        var target = session.Target;
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var slots = target.Record.item.Select((item, slot) => new
        {
            slot, typeId = item.IsAir ? 0 : item.type, stack = item.IsAir ? 0 : item.stack,
            prefix = item.IsAir ? 0 : item.prefix, maxStack = item.IsAir ? 0 : item.maxStack,
            favorited = !item.IsAir && item.favorited, name = item.IsAir ? "" : item.Name,
            instanceId = ActorInventory.Instance(item),
            slotToken = SlotToken(identity, item, token, slot)
        }).ToArray();
        session.ObservedAt = Environment.TickCount64;
        session.Token = token;
        return new { status = "open", reason = (string)null, container = new
        {
            x = target.X, y = target.Y, typeId = target.Type, chestIndex = target.Index,
            name = target.Record.name ?? "", containerToken = token, slots
        }};
    }

    internal sealed class Action(AiReadOnlyProtocol.Identity identity, string operation, JsonElement arguments)
    {
        private bool attempted;
        private ulong mutationTick;
        private Target target;
        private Result result;

        internal Result Poll(bool cancel = false)
        {
            if (result != null) return result;
            var player = (Player)identity.Actor;
            bool opening = operation == "open_container";
            Result Report(string status, string reason, bool verified = false) =>
                new(status, reason, new { mutationAttempted = attempted, containerVerified = verified,
                    chestIndex = player.chest, x = target?.X, y = target?.Y, typeId = target?.Type,
                    mutationGameUpdate = mutationTick, observedGameUpdate = Main.GameUpdateCount },
                    ServerAiNativeActions.Released(player), verified, InputsAcquired: false);
            if (attempted)
            {
                if (Main.GameUpdateCount <= mutationTick)
                    return cancel ? Report("unknown", "container_confirmation_interrupted")
                        : Report("running", "awaiting_container_confirmation");
                bool verified = opening ? opened?.Target == target && Check(opened, identity) == null
                    : player.chest == -1 && opened == null;
                return result = verified ? Report("completed", opening ? "container_open_verified" : "container_close_verified", true)
                    : Report("unknown", "container_changed_before_confirmation");
            }
            if (cancel) return result = Report("cancelled", "explicit_stop");
            if (opening)
            {
                int x = arguments.GetProperty("x").GetInt32(), y = arguments.GetProperty("y").GetInt32();
                string reason = Resolve(player, x, y, out target);
                if (reason != null) return result = Report("failed", reason);
                if (player.chest != -1) return result = Report("failed", "container_already_open");
                if (Chest.UsingChest(target.Index) != -1) return result = Report("failed", "container_in_use");
            }
            else
            {
                if (!Fresh(opened, identity, arguments.GetProperty("containerToken").GetString()))
                    return result = Report("failed", "stale_container_token");
                target = opened.Target;
            }

            attempted = true;
            mutationTick = Main.GameUpdateCount;
            try
            {
                if (opening)
                {
                    player.chest = target.Index;
                    player.chestX = target.X;
                    player.chestY = target.Y;
                    opened = new(identity, target);
                    Sync(player);
                    // Packet 31 also fires a rigged chest using the opener as wiring user.
                    if (WorldGen.IsChestRigged(target.X, target.Y))
                    {
                        Wiring.SetCurrentUser(player.whoAmI);
                        try { Wiring.HitSwitch(target.X, target.Y); }
                        finally { Wiring.SetCurrentUser(-1); }
                        NetMessage.SendData(MessageID.HitSwitch, -1, -1, null, target.X, target.Y);
                    }
                }
                else Close(player);
            }
            catch
            {
                return result = Report("unknown", "container_mutation_unconfirmed");
            }
            return Report("running", "awaiting_container_confirmation");
        }
    }
}
