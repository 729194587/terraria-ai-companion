using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

// Native test setup and probes are limited to an explicitly marked copied world.
internal static class ServerContainerFixture
{
    private static bool ready;
    private static int x, y, index;
    internal static bool TransferEnabled => Program.LaunchParameters.ContainsKey("-companioncontainertransfertest")
        && Program.LaunchParameters.ContainsKey("-companionaiipctest");

    internal static bool BatchEnabled => Program.LaunchParameters.ContainsKey("-companioncontainerbatchtest")
        && Program.LaunchParameters.ContainsKey("-companionaiipctest");

    internal static void Reset() => ready = false;

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
            if (BatchEnabled)
            {
                Place(x + 16, y, TileID.Containers, 0);
                Place(x + 42, y, TileID.Containers, 0);
            }
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
        if (command == "restore-actor") player.ghost = false;
        if (TransferEnabled || BatchEnabled)
        {
            switch (command)
            {
                case "seed-batch":
                    for (int slot = 0; slot < 59; slot++) player.inventory[slot] = new Item();
                    for (int slot = 0; slot < 40; slot++) Main.chest[index].item[slot] = new Item();
                    break;
                case "seed-nearby":
                    player.inventory[0] = new Item(ItemID.Wood, 2);
                    player.inventory[10] = new Item(ItemID.Wood, 11);
                    player.inventory[11] = new Item(ItemID.Wood, 3) { favorited = true };
                    player.inventory[12] = new Item(ItemID.CopperCoin, 4);
                    player.inventory[13] = new Item(ItemID.StoneBlock, 13);
                    Main.chest[index].item[0] = new Item(ItemID.Wood, 9997);
                    Main.chest[index].item[1] = new Item(ItemID.Wood, 9998);
                    Main.chest[Chest.FindChest(x + 16, y)].item[1] = new Item(ItemID.StoneBlock, 5);
                    foreach (int offset in new[]{8, 20, 42})
                        Main.chest[Chest.FindChest(x + offset, y)].item[1] = new Item(ItemID.StoneBlock, 5);
                    break;
                case "batch-fill-bag":
                    for (int slot = 0; slot < 50; slot++) player.inventory[slot] = new Item(ItemID.StoneBlock, 9999);
                    player.inventory[10] = new Item(ItemID.Wood, 9997);
                    Main.chest[index].item[0] = new Item(ItemID.Wood, 9);
                    break;
                case "batch-fill-chest":
                    for (int slot = 0; slot < 40; slot++) Main.chest[index].item[slot] = new Item(ItemID.StoneBlock, 9999);
                    Main.chest[index].item[0] = new Item(ItemID.Wood, 9997);
                    player.inventory[10] = new Item(ItemID.Wood, 9);
                    break;
                case "batch-roundtrip":
                    Main.chest[index].item[0] = new Item(ItemID.Lens, 7);
                    break;
                case "batch-restock":
                    player.inventory[9] = new Item(ItemID.Wood, 9996) { favorited = true };
                    player.inventory[12] = new Item(ItemID.Wood, 9997);
                    Main.chest[index].item[0] = new Item(ItemID.Wood, 9);
                    Main.chest[index].item[1] = new Item(ItemID.StoneBlock, 3);
                    break;
                case "seed-transfer":
                    player.inventory[10] = new Item(ItemID.Wood, 30);
                    player.inventory[11] = new Item();
                    player.inventory[12] = new Item(ItemID.Wood, 9999);
                    Main.chest[index].item[1] = new Item();
                    Main.chest[index].item[2] = new Item(ItemID.Wood, 9997);
                    Main.chest[index].item[3] = new Item(ItemID.Wood, 9999);
                    break;
                case "partial-bag":
                    player.inventory[12] = new Item(ItemID.Wood, 9997);
                    break;
                case "fill":
                    for (int slot = 0; slot < 50; slot++) player.inventory[slot] = new Item(ItemID.Wood, 9999);
                    for (int slot = 0; slot < 40; slot++) Main.chest[index].item[slot] = new Item(ItemID.Wood, 9999);
                    break;
                case "expire":
                    ActorContainer.ExpireForDiagnostic();
                    break;
                case "seed-mod":
                    int type = ModContent.ItemType<InventoryDiagnosticItem>();
                    player.inventory[10] = new Item(type, 20);
                    Main.chest[index].item[1] = new Item(type, 2);
                    ((InventoryDiagnosticItem)Main.chest[index].item[1].ModItem).Mode = "mod_denied";
                    break;
                case "allow-mod":
                    ((InventoryDiagnosticItem)Main.chest[index].item[1].ModItem).Mode = "";
                    break;
                case "throw-mod":
                    ((InventoryDiagnosticItem)Main.chest[index].item[1].ModItem).Mode = "stack_throw";
                    break;
                case "retire":
                    ProbeSession.Finish("container diagnostic retirement");
                    break;
                case "generation":
                    ProbeSession.ActorGeneration++;
                    break;
            }
        }
        var clients = Netplay.Clients.Where(client => client.State == 10).Select(client => new
        {
            slot = client.Id, chest = Main.player[client.Id].chest,
            inventory = Main.player[client.Id].inventory.Select(item => new { item.type, item.stack, item.prefix }).ToArray()
        }).ToArray();
        File.WriteAllText(Path.Combine(Main.SavePath, "container-state.json"), JsonSerializer.Serialize(new
        {
            command, clients, actorChest = player.chest, gameUpdate = Main.GameUpdateCount,
            batchInventory = BatchEnabled ? player.inventory.Select(item => new { item.type, item.stack, item.prefix, item.favorited }).ToArray() : null,
            batchChests = BatchEnabled ? new[]{index, Chest.FindChest(x + 16, y), Chest.FindChest(x + 20, y), Chest.FindChest(x + 8, y), Chest.FindChest(x + 42, y)}.Where(slot => slot >= 0 && Main.chest[slot] != null)
                .Select(slot => new { index = slot, slots = Main.chest[slot].item.Select(item => new { item.type, item.stack, item.prefix }).ToArray() }).ToArray() : null,
            modStackCalls = (Main.chest[index]?.item[1].ModItem as InventoryDiagnosticItem)?.State
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
