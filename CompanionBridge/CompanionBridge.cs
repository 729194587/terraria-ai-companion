using System.Diagnostics;
using System.Net.Sockets;
using Terraria;
using Terraria.ModLoader;

namespace CompanionBridge;

public sealed class CompanionBridge : Mod
{
    internal static ModKeybind ToggleAutoMoveKeybind { get; private set; }
    internal static ControlState Controls { get; } = new();
    internal static TcpControlServer Server { get; private set; }
    internal static bool AutoMoveEnabled => Controls.IsMoving;

    internal static void StopControl()
    {
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
            Logger.Info($"CompanionBridge M1.3 loaded; TCP 127.0.0.1:{TcpControlServer.DefaultPort}; auto move is off.");
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
