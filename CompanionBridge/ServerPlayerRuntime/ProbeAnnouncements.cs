using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Chat;
using Terraria.Localization;

namespace VirtualPlayerEngineProbe;

// Lifecycle notices, separate from periodic/late-join state synchronization.
// Use the same localization keys and yellow as native player announcements,
// while retaining our own deduplication instead of writing RemoteClient fields.
internal static class ProbeAnnouncements
{
    private static Player joinedActor;
    private static string joinedName;
    private static bool joinNoticeAttempted, leaveNoticeAttempted;
    internal static long JoinNotices, LeaveNotices, NoticeFailures;

    internal static void ResetNotices()
    {
        joinedActor = null;
        joinedName = null;
        joinNoticeAttempted = leaveNoticeAttempted = false;
        JoinNotices = LeaveNotices = NoticeFailures = 0;
    }

    internal static void Join(Player expected)
    {
        VirtualPlayerEngineProbe.Reservation.AssertOwner(Program.IsMainThread);
        lock (VirtualPlayerEngineProbe.Reservation.Gate)
        {
            NetworkStateSync.Require(expected);
            if (joinNoticeAttempted) return;
            joinedActor = expected;
            joinedName = expected.name;
            joinNoticeAttempted = true; // A throwing broadcast may already have sent.
            try
            {
                ChatHelper.BroadcastChatMessage(NetworkText.FromKey(Lang.mp[19].Key, joinedName),
                    new Color(255, 240, 20), expected.whoAmI);
                JoinNotices++;
            }
            catch (Exception e) { NoticeFailures++; VirtualPlayerEngineProbe.WarnAnnouncement(e); }
        }
    }

    internal static void Left(Player expected)
    {
        VirtualPlayerEngineProbe.Reservation.AssertOwner(Program.IsMainThread);
        lock (VirtualPlayerEngineProbe.Reservation.Gate)
        {
            NetworkStateSync.Require(expected, inactiveAllowed: true);
            if (!joinNoticeAttempted || leaveNoticeAttempted || !ReferenceEquals(joinedActor, expected)) return;
            leaveNoticeAttempted = true;
            try
            {
                ChatHelper.BroadcastChatMessage(NetworkText.FromKey(Lang.mp[20].Key, joinedName),
                    new Color(255, 240, 20), expected.whoAmI);
                LeaveNotices++;
            }
            catch (Exception e) { NoticeFailures++; VirtualPlayerEngineProbe.WarnAnnouncement(e); }
        }
    }
}
