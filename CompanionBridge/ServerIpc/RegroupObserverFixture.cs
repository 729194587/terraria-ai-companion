using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Chat;
using Terraria.GameContent.NetModules;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.Net;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

// Copied world only. The probes connect to the game port and identify their own
// loopback source ports. Stage logged-in slots without driving graphical menus.
public sealed class RegroupObserverFixture : ModSystem
{
    public override bool IsLoadingEnabled(Mod mod) => Main.dedServ &&
        Program.LaunchParameters.ContainsKey("-companionregroupobservers") && ServerAiActionFixture.Enabled;
    private int[] slots;
    public override void Load() => Main.OnTickForThirdPartySoftwareOnly += Tick;
    public override void Unload() => Main.OnTickForThirdPartySoftwareOnly -= Tick;

    private void Tick()
    {
        var request = Path.Combine(Main.SavePath, "observer-request.json");
        if (!Program.IsMainThread || !File.Exists(request)) return;
        object result;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(request));
            lock (ProbeSession.Reservation.Gate)
                result = Execute(doc.RootElement);
        }
        catch (Exception e) { result = new { error = e.ToString() }; }
        File.Delete(request);
        var response = Path.Combine(Main.SavePath, "observer-response.json");
        File.WriteAllText(response + ".tmp", JsonSerializer.Serialize(result));
        File.Move(response + ".tmp", response, true);
    }

    private object Execute(JsonElement command)
    {
        switch (command.GetProperty("op").GetString())
        {
            case "setup":
                var actor = ProbeSession.Actor;
                int[] teams = { 2, 5, 3, 0 };
                var ports = command.GetProperty("ports").Deserialize<int[]>();
                if (slots != null || ports.Length != teams.Length || ports.Distinct().Count() != teams.Length)
                    throw new InvalidOperationException("Invalid observer setup");
                var peers = ports.Select(port => Netplay.Clients.SingleOrDefault(c => c.Id != actor.whoAmI &&
                    c.Socket?.IsConnected() == true && c.State == 0 &&
                    c.Socket.GetRemoteAddress() is TcpAddress a && IPAddress.IsLoopback(a.Address) && a.Port == port)).ToArray();
                // RemoteClient.Update initializes a newly accepted connection to State 0.
                if (peers.Any(c => c == null || !c.IsActive)) return new { waitingForAccept = true };
                slots = peers.Select(c => c.Id).ToArray();
                for (int i = 0; i < peers.Length; i++)
                {
                    var p = Main.player[slots[i]];
                    p.name = "Observer" + i;
                    p.team = teams[i];
                    p.position = actor.position + new Vector2(96 + i * 16, 0);
                    p.active = true;
                    peers[i].State = 10;
                }
                actor.team = 2;
                actor.inventory[20].SetDefaults(ItemID.WormholePotion);
                var color = Main.teamColor[5];
                return new { slots, actor = actor.name, module = NetManager.Instance.GetId<NetTextModule>(),
                    teamText = Language.GetTextValue(Lang.mp[22].Key, actor.name),
                    teleportText = Language.GetTextValue("Game.HasTeleportedTo", actor.name, "Observer1"),
                    teamColor = new int[] {color.R,color.G,color.B} };
            case "fence":
                foreach (int slot in slots)
                    ChatHelper.SendChatMessageToClient(NetworkText.FromLiteral(command.GetProperty("marker").GetString()), Color.White, slot);
                return new { states = slots.Select(i => new { slot=i, state=Netplay.Clients[i].State, connected=Netplay.Clients[i].IsConnected(), active=Main.player[i].active }).ToArray() };
            case "receive":
                var lines = new List<object>();
                void Capture(On_Main.orig_NewTextMultiline orig, string text, bool force, Color color, int width)
                {
                    orig(text, force, color, width);
                    lines.Add(new {text, rgb = new int[] {color.R,color.G,color.B}});
                }
                var receive = typeof(NetTextModule).GetMethod("DeserializeAsClient", BindingFlags.NonPublic | BindingFlags.Instance);
                On_Main.NewTextMultiline += Capture;
                try
                {
                    foreach (var packet in command.GetProperty("packets").EnumerateArray())
                    {
                        using var stream = new MemoryStream(packet.GetBytesFromBase64());
                        using var reader = new BinaryReader(stream);
                        if (!(bool)receive.Invoke(new NetTextModule(), new object[]{reader, 0}) || stream.Position != stream.Length)
                            throw new InvalidDataException("Native receiver did not consume packet");
                    }
                }
                finally { On_Main.NewTextMultiline -= Capture; }
                return new { lines };
            default: throw new ArgumentException("Unknown observer command");
        }
    }
}
