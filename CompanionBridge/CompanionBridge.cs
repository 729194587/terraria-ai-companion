using System.Diagnostics;
using System.Net.Sockets;
using Terraria;
using Terraria.ModLoader;
using CompanionBridge.Motion;

namespace CompanionBridge;

public sealed class CompanionBridge : Mod
{
    internal static ModKeybind ToggleAutoMoveKeybind { get; private set; }
    internal static ControlState Controls { get; } = new();
    internal static TcpControlServer Server { get; private set; }
    internal static bool AutoMoveEnabled => Controls.IsMoving;

    // Death/ghost: revoke actions while retaining this world's observation/chat connection.
    internal static void RevokeControl()
    {
        GameNavigation.Cancel();
        Controls.RevokeControl();
    }

    // World/session teardown and manual takeover retain their existing disconnect semantics.
    internal static void StopControl()
    {
        GameNavigation.Cancel();
        Server?.Disconnect();
        Controls.Reset();
    }

    internal static void ToggleManual()
    {
        bool wasMoving = AutoMoveEnabled;
        StopControl();
        if (!wasMoving) Controls.ToggleManual(Stopwatch.GetTimestamp());
    }

    public override void Load()
    {
        Controls.Reset();
        if (Main.dedServ)
            return;

        ToggleAutoMoveKeybind = KeybindLoader.RegisterKeybind(this, "ToggleAutoMove", "P");
        Server = new TcpControlServer(Controls);
        try
        {
            Server.Start();
            Logger.Info($"CompanionBridge M1.5 loaded; TCP 127.0.0.1:{TcpControlServer.DefaultPort}; auto move is off.");
        }
        catch (SocketException e)
        {
            Server.Dispose();
            Server = null;
            Logger.Error($"Bridge TCP unavailable; manual P still works: {e.Message}");
        }
    }

    public override void Unload()
    {
        StopControl();
        Server?.Dispose();
        Server = null;
        ToggleAutoMoveKeybind = null;
    }
}
