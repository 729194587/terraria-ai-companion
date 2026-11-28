using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Chat;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.UI.Chat;

namespace CompanionBridge.Common.Systems;

// Isolated multiplayer client. Only setup removes old torches; pickup is ordinary Player.Update.
public sealed class TorchRecipientFixture : ModSystem
{
    public override bool IsLoadingEnabled(Mod mod) => !Main.dedServ
        && Program.LaunchParameters.ContainsKey("-companiontorchrecipient")
        && File.Exists(Path.Combine(Main.SavePath, "isolated-torch-recipient"));

    public override void PostUpdateEverything()
    {
        if (Main.gameMenu || Main.netMode != NetmodeID.MultiplayerClient || !Main.LocalPlayer.active) return;
        string request = Path.Combine(Main.SavePath, "recipient-request");
        if (!File.Exists(request)) return;
        string command = File.ReadAllText(request);
        File.Delete(request);
        var p = Main.LocalPlayer;
        if (command is "prepare" or "partial" or "far" or "approach")
        {
            int offset = command == "far" ? 22 : 6;
            p.position = new Vector2((Main.spawnTileX + offset) * 16, (Main.spawnTileY + 8) * 16 - p.height);
            p.velocity = Vector2.Zero;
            p.team = 1;
            NetMessage.SendData(MessageID.PlayerControls, number: p.whoAmI);
            NetMessage.SendData(MessageID.PlayerTeam, number: p.whoAmI);
            for (int i = 0; i < p.inventory.Length; i++)
            {
                if (command == "partial" && i < 50)
                {
                    p.inventory[i] = new Item(i == 10 ? ItemID.Torch : ItemID.StoneBlock);
                    p.inventory[i].stack = p.inventory[i].maxStack - (i == 10 ? 5 : 0);
                }
                else if (command != "approach" && p.inventory[i].type == ItemID.Torch)
                    p.inventory[i].TurnToAir();
                else continue;
                NetMessage.SendData(MessageID.SyncEquipment, number: p.whoAmI, number2: i);
            }
        }
        if (command == "make-room")
        {
            p.inventory[11].TurnToAir();
            NetMessage.SendData(MessageID.SyncEquipment, number: p.whoAmI, number2: 11);
        }
        if (command is "make-room" or "approach")
        {
            var ground = Main.item.Take(Main.maxItems).FirstOrDefault(i => i.active && i.type == ItemID.Torch);
            if (ground != null)
            {
                p.Bottom = ground.Bottom;
                p.velocity = Vector2.Zero;
                NetMessage.SendData(MessageID.PlayerControls, number: p.whoAmI);
            }
        }
        if (command == "ask")
            ChatHelper.SendChatMessageFromClient(ChatManager.Commands.CreateOutgoingMessage("我要下矿，帮我准备 12 支火把，拿给我"));
        var state = new { command, slot = p.whoAmI, p.name, p.dead, p.team,
            ground = Main.item.Take(Main.maxItems).Where(i => i.active && i.type == ItemID.Torch)
                .Select(i => new { slot = i.whoAmI, i.stack, owner = i.playerIndexTheItemIsReservedFor, x = i.Center.X, y = i.Center.Y }).ToArray(),
            torches = p.inventory.Where(i => i.type == ItemID.Torch).Sum(i => i.stack) };
        string path = Path.Combine(Main.SavePath, "recipient-state.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(state));
        File.Move(path + ".tmp", path, true);
    }
}
