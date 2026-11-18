using System.Diagnostics;
using System.Net.Sockets;
using System.IO;
using Terraria;
using Terraria.ModLoader;
using CompanionBridge.Motion;
using CompanionBridge.Common.Systems;

namespace CompanionBridge;

public sealed class CompanionBridge : Mod
{
    public override object Call(params object[] args)=>ServerIpc.BossExtensions.Register(args);
    public override void HandlePacket(BinaryReader reader, int whoAmI) => AtomicMultiplayer.Receive(reader, whoAmI);
    internal static ModKeybind ToggleAutoMoveKeybind { get; private set; }
    internal static ControlState Controls { get; } = new();
    internal static TcpControlServer Server { get; private set; }
    internal static bool AutoMoveEnabled => Controls.IsMoving;

    // Death/ghost: revoke actions while retaining this world's observation/chat connection.
    internal static void RevokeControl(PlayerSnapshot snapshot = null)
    {
        GameNavigation.Cancel();
        Controls.RevokeControl(snapshot);
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
        {
            ServerIpc.ServerAiSystem.LoadRuntime(this);
            return;
        }

        ToggleAutoMoveKeybind = KeybindLoader.RegisterKeybind(this, "ToggleAutoMove", "P");
        Program.LaunchParameters.TryGetValue("-companionbridgeport", out string portArgument);
        int port = TcpControlServer.ParseLaunchPort(portArgument);
        Server = new TcpControlServer(Controls, port);
        try
        {
            Server.Start();
            Logger.Info($"CompanionBridge loaded; TCP 127.0.0.1:{port}; auto move is off.");
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
        ServerIpc.ServerAiSystem.UnloadRuntime();
        ServerIpc.BossExtensions.Reset();
        StopControl();
        Server?.Dispose();
        Server = null;
        ToggleAutoMoveKeybind = null;
    }
}
