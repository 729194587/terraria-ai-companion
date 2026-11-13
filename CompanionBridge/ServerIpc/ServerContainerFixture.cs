using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

// Native test setup and probes are limited to an explicitly marked copied world.
internal static class ServerContainerFixture
{
    private static bool ready;
    private static int x, y, index;
    internal static void Tick(AiReadOnlyProtocol.Identity identity)
    {
        if (identity == null || !Program.LaunchParameters.ContainsKey("-companionaiipctest")
            || !Program.LaunchParameters.ContainsKey("-companioncontainertest")
            || !File.Exists(Path.Combine(Main.SavePath, "isolated-container-fixture"))) return;
        var player = (Player)identity.Actor;
        if (!ready)
        {
            x = Main.spawnTileX + 3;
            y = Main.spawnTileY + 6;
            for (int i = x - 12; i <= x + 25; i++)
            for (int j = y - 6; j <= y + 2; j++)
            {
                var tile = Main.tile[i, j];
                tile.ClearEverything();
                if (j == y + 2) { tile.HasTile = true; tile.TileType = TileID.Dirt; }
            }
            player.position = new Vector2(x * 16, (y + 2) * 16 - player.height);
            player.velocity = Vector2.Zero;
            index = Place(x, y, TileID.Containers, 0);
            int other = Place(x + 4, y, TileID.Containers2, 0);
            Place(x + 8, y, TileID.Containers, 72);
            Place(x + 20, y, TileID.Containers, 0);
            ready = true;
            File.WriteAllText(Path.Combine(Main.SavePath, "container-ready.json"), JsonSerializer.Serialize(new { x, y, index, other }));
        }
        var request = Path.Combine(Main.SavePath, "container-request");
        if (!File.Exists(request)) return;
        string command = File.ReadAllText(request);
        File.Delete(request);
        if (command is "replace" or "destroy" or "missing")
        {
            Main.chest[index] = null;
            if (command == "replace" && Place(x, y, TileID.Containers, 0, index) != index)
                throw new InvalidOperationException("Expected native Chest index reuse.");
            if (command == "destroy")
                for (int dx = 0; dx < 2; dx++)
                for (int dy = 0; dy < 2; dy++) Main.tile[x + dx, y + dy].ClearEverything();
        }
        if (command == "move-away") player.position.X = (x - 12) * 16;
        if (command == "move-back") player.position.X = x * 16;
        if (command == "invalidate") player.ghost = true;
        var clients = Netplay.Clients.Where(client => client.State == 10).Select(client => new
        {
            slot = client.Id, chest = Main.player[client.Id].chest
        }).ToArray();
        File.WriteAllText(Path.Combine(Main.SavePath, "container-state.json"), JsonSerializer.Serialize(new
        {
            command, clients, actorChest = player.chest, gameUpdate = Main.GameUpdateCount
        }));
    }

    private static int Place(int x, int y, ushort type, int frameX, int slot = -1)
    {
        int index = Chest.CreateChest(x, y, slot);
        if (index < 0) throw new InvalidOperationException("Native chest registration failed.");
        for (int dx = 0; dx < 2; dx++)
        for (int dy = 0; dy < 2; dy++)
        {
            var tile = Main.tile[x + dx, y + dy];
            tile.HasTile = true;
            tile.TileType = type;
            tile.TileFrameX = (short)(frameX + dx * 18);
            tile.TileFrameY = (short)(dy * 18);
        }
        Main.chest[index].item[0].SetDefaults(ItemID.Wood);
        Main.chest[index].item[0].stack = 7;
        return index;
    }
}
