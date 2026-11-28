using System;
using System.IO;
using System.Linq;
using CompanionBridge.ServerIpc;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace CompanionBridge.Common.Systems;

public sealed class HandoffObservation : ModSystem
{
    internal const ushort PacketPrefix = ushort.MaxValue - 1;
    private sealed record WatchItem(Guid Token, int Slot, int Type, int Prefix);
    private static WatchItem watched;

    public override void Load()
    {
        if (Main.dedServ) On_MessageBuffer.GetData += ServerItemDrop.SyncItem;
        else On_Player.PickupItem += ObservePickup;
    }

    public override void Unload()
    {
        if (Main.dedServ) On_MessageBuffer.GetData -= ServerItemDrop.SyncItem;
        else On_Player.PickupItem -= ObservePickup;
        watched = null;
    }

    public override void OnWorldUnload() => watched = null;

    private static ModPacket Packet(Guid token)
    {
        var packet = ModContent.GetInstance<CompanionBridge>().GetPacket();
        packet.Write(PacketPrefix);
        packet.Write(token.ToByteArray());
        return packet;
    }

    internal static void Watch(int recipient, Guid token, int slot, int type, int prefix)
    {
        var packet = Packet(token);
        packet.Write((short)slot);
        packet.Write(type);
        packet.Write((byte)prefix);
        packet.Send(recipient);
    }

    internal static void Receive(BinaryReader reader, int sender)
    {
        var token = new Guid(reader.ReadBytes(16));
        if (!Program.IsMainThread) return;
        if (Main.netMode == NetmodeID.MultiplayerClient)
            watched = new(token, reader.ReadInt16(), reader.ReadInt32(), reader.ReadByte());
        else if (Main.netMode == NetmodeID.Server && sender >= 0 && sender < Main.maxPlayers)
            ServerItemDrop.ObserveReceipt(sender, token, reader.ReadInt32(), reader.ReadInt32());
    }

    private static Item ObservePickup(On_Player.orig_PickupItem orig, Player player, int who, int index, Item item)
    {
        var watch = watched;
        if (watch == null || Main.netMode != NetmodeID.MultiplayerClient || who != Main.myPlayer
            || !ReferenceEquals(player, Main.LocalPlayer) || index != watch.Slot || item.type != watch.Type
            || item.prefix != watch.Prefix || !ReferenceEquals(Main.item[index], item))
            return orig(player, who, index, item);

        long Count() => player.inventory.Concat(player.bank4.item)
            .Where(i => !i.IsAir && i.type == watch.Type && i.prefix == watch.Prefix).Sum(i => (long)i.stack);
        long inventory = Count();
        int before = item.stack;
        var remainder = orig(player, who, index, item);
        int left = remainder.IsAir || !remainder.active ? 0 : remainder.stack;
        if (left < before && (left == 0 || remainder.type == watch.Type && remainder.prefix == watch.Prefix)
            && Count() - inventory == before - left)
        {
            var packet = Packet(watch.Token);
            packet.Write(before);
            packet.Write(left);
            packet.Send();
            if (left == 0) watched = null;
        }
        return remainder;
    }
}
