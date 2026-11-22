using System;
using System.Collections.Concurrent;
using System.IO;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.GameInput;
using Terraria.ID;
using Terraria.ModLoader;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.Common.Systems;

// The request carries no target position or slot. Only the reserved server actor can be watched.
public sealed class SpectatorSystem : ModSystem
{
    private const ushort Prefix = ushort.MaxValue;
    private const int PollTicks = 20;
    private const int TimeoutTicks = 240;
    private static ModKeybind toggle;
    private static Guid session;
    private static int slot = -1;
    private static long generation;
    private static Guid world;
    private static Vector2 center;
    private static ulong lastReply, lastPoll;
    private static bool showing;
    private static readonly ulong[] nextAllowed = new ulong[255];
    private sealed record Frame(int Sender, Guid Session, int Width, int Height, int Slot,
        Guid World, long Generation, Vector2 Center);
    private static readonly ConcurrentQueue<Frame> pending = new();
    internal static bool Watching => session != Guid.Empty;
    internal static bool Pressed => toggle?.JustPressed == true;

    public override void Load()
    {
        if (!Main.dedServ) toggle = KeybindLoader.RegisterKeybind(Mod, "WatchAI", "End");
    }
    public override void Unload() { toggle = null; Reset(); }
    public override void OnWorldUnload() => Reset();
    private static void Reset()
    {
        session = Guid.Empty;
        slot = -1;
        showing = false;
        Array.Clear(nextAllowed);
        pending.Clear();
    }
    internal static void Toggle()
    {
        if (Watching) { Stop("已返回玩家视角"); return; }
        if (Main.netMode != NetmodeID.MultiplayerClient || Main.LocalPlayer.dead) return;
        session = Guid.NewGuid();
        lastReply = Main.GameUpdateCount;
        Main.NewText("[观战] 正在同步 AI 和远端地形；再按快捷键退出。");
        Poll();
    }
    private static void Stop(string message)
    {
        session = Guid.Empty;
        slot = -1;
        showing = false;
        Main.NewText("[观战] " + message);
    }
    private static ModPacket Packet(Guid token)
    {
        var packet = ModContent.GetInstance<CompanionBridge>().GetPacket();
        packet.Write(Prefix);
        packet.Write(token.ToByteArray());
        return packet;
    }
    // Unzoomed dimensions also cover ordinary zoom-in. Include lighting/framing margins.
    private static Point HalfView() => new(Math.Clamp(Main.screenWidth / 2 + 320, 640, 4096),
        Math.Clamp(Main.screenHeight / 2 + 320, 480, 2160));
    private static void Poll()
    {
        lastPoll = Main.GameUpdateCount;
        var packet = Packet(session);
        var size = HalfView();
        packet.Write((ushort)size.X);
        packet.Write((ushort)size.Y);
        packet.Send();
    }
    internal static void Receive(BinaryReader r, int sender)
    {
        if (pending.Count >= 256) return;
        var token = new Guid(r.ReadBytes(16));
        if (Main.netMode == NetmodeID.Server && sender >= 0 && sender < 255)
            pending.Enqueue(new(sender, token, r.ReadUInt16(), r.ReadUInt16(), -1, default, 0, default));
        else if (Main.netMode == NetmodeID.MultiplayerClient)
        {
            int id = r.ReadInt16();
            var w = id < 0 ? default : new Guid(r.ReadBytes(16));
            long gen = id < 0 ? 0 : r.ReadInt64();
            Vector2 pos = id < 0 ? default : new(r.ReadSingle(), r.ReadSingle());
            pending.Enqueue(new(-1, token, 0, 0, id, w, gen, pos));
        }
    }
    public override void PostUpdateEverything()
    {
        while (pending.TryDequeue(out var f))
        {
            if (Main.netMode == NetmodeID.Server && f.Sender >= 0) Serve(f);
            else if (Watching && f.Sender == -1 && f.Session == session) Accept(f);
        }
        if (!Watching) return;
        if (Main.gameMenu || Netplay.Disconnect || Main.LocalPlayer.dead || Main.GameUpdateCount-lastReply > TimeoutTicks)
            Stop("连接失效或角色不可用，已退出");
        else if (Main.GameUpdateCount-lastPoll >= PollTicks) Poll();
    }
    private static Rectangle Tiles(Vector2 pos, int w, int h)
    {
        int X(float x) => Math.Clamp((int)(x/16), 0, Main.maxTilesX-1);
        int Y(float y) => Math.Clamp((int)(y/16), 0, Main.maxTilesY-1);
        int left=X(pos.X-w), top=Y(pos.Y-h);
        return new Rectangle(left,top,X(pos.X+w)-left+1,Y(pos.Y+h)-top+1);
    }
    private static void Serve(Frame f)
    {
        int client = f.Sender;
        if (!Main.player[client].active || !Netplay.Clients[client].IsActive ||
            Main.GameUpdateCount < nextAllowed[client]) return;
        nextAllowed[client] = Main.GameUpdateCount + PollTicks/2;
        var actor = ServerIpc.ServerAiSystem.SpectatorActor();
        var packet = Packet(f.Session);
        if (actor == null || actor.dead || actor.ghost || !Guid.TryParse(ProbeSession.WorldSession,out var w))
        {
            packet.Write((short)-1); packet.Send(client); return;
        }
        var area = Tiles(actor.Center,Math.Clamp((int)f.Width,640,4096),Math.Clamp((int)f.Height,480,2160));
        for (int x=Netplay.GetSectionX(area.Left); x<=Netplay.GetSectionX(area.Right-1); x++)
            for (int y=Netplay.GetSectionY(area.Top); y<=Netplay.GetSectionY(area.Bottom-1); y++)
                NetMessage.SendSection(client,x,y); // native per-client TileSections deduplication
        NetMessage.SendData(MessageID.PlayerControls,client,number:actor.whoAmI);
        Rectangle visible = new(area.X*16,area.Y*16,area.Width*16,area.Height*16);
        foreach (var npc in Main.ActiveNPCs)
            if (visible.Intersects(npc.Hitbox)) NetMessage.SendData(MessageID.SyncNPC,client,number:npc.whoAmI);
        foreach (var item in Main.ActiveItems)
            if (visible.Intersects(item.Hitbox)) NetMessage.SendData(MessageID.SyncItem,client,number:item.whoAmI);
        foreach (var projectile in Main.ActiveProjectiles)
            if (visible.Intersects(projectile.Hitbox)) NetMessage.SendData(MessageID.SyncProjectile,client,number:projectile.whoAmI);
        packet.Write((short)actor.whoAmI);
        packet.Write(w.ToByteArray());
        packet.Write(ProbeSession.ActorGeneration);
        packet.Write(actor.Center.X); packet.Write(actor.Center.Y);
        packet.Send(client);
    }
    private static void Accept(Frame f)
    {
        if (f.Slot < 0 || f.Slot >= Main.maxPlayers || f.Slot == Main.myPlayer ||
            slot >= 0 && (slot != f.Slot || generation != f.Generation || world != f.World))
        { Stop("AI 已离开或身份变化"); return; }
        slot = f.Slot; generation = f.Generation; world = f.World; center = f.Center;
        lastReply = Main.GameUpdateCount;
    }
    public override void ModifyScreenPosition()
    {
        if (!Watching || slot < 0) return;
        var actor = Main.player[slot];
        var focus = actor.Center;
        if (!actor.active || Vector2.DistanceSquared(focus,center)>256*256) return;
        var size=HalfView();
        var area=Tiles(focus,size.X,size.Y);
        for (int x=Netplay.GetSectionX(area.Left); x<=Netplay.GetSectionX(area.Right-1); x++)
            for (int y=Netplay.GetSectionY(area.Top); y<=Netplay.GetSectionY(area.Bottom-1); y++)
                if (!Main.sectionManager.SectionLoaded(x,y)) return;
        Main.screenPosition = focus - new Vector2(Main.screenWidth,Main.screenHeight)*0.5f;
        if (!showing) { showing=true; Main.NewText("[观战] 正在跟随 AI：" + actor.name); }
    }
}

public sealed class SpectatorInput : ModPlayer
{
    public override void ProcessTriggers(TriggersSet triggersSet)
    {
        if (Player.whoAmI == Main.myPlayer && SpectatorSystem.Pressed) SpectatorSystem.Toggle();
    }
    public override void SetControls()
    {
        if (Player.whoAmI != Main.myPlayer || !SpectatorSystem.Watching) return;
        Player.controlUseItem = Player.controlUseTile = Player.controlHook = Player.controlMount = false;
        Player.controlLeft = Player.controlRight = Player.controlJump = Player.controlUp = Player.controlDown = false;
    }
    public override bool PreItemCheck() => Player.whoAmI != Main.myPlayer || !SpectatorSystem.Watching;
}
