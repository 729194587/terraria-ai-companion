using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using VirtualPlayerEngineProbe;

namespace CompanionBridge.ServerIpc;

internal static class ServerCollectFixture
{
    private static bool ready;
    private static int x, floor;
    private static readonly int[] batchTypes = {ItemID.Wood,ItemID.StoneBlock,ItemID.DirtBlock,ItemID.Gel,
        ItemID.Torch,ItemID.Rope,ItemID.SandBlock,ItemID.ClayBlock,ItemID.MudBlock,ItemID.AshBlock,
        ItemID.CopperOre,ItemID.IronOre,ItemID.SilverOre,ItemID.GoldOre,ItemID.Cobweb,ItemID.Glass,ItemID.IceBlock};
    private static readonly System.Reflection.MethodInfo combine = typeof(Item).GetMethod("CombineWithNearbyItems",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    internal static void Reset() => ready = false;

    internal static void Tick(AiReadOnlyProtocol.Identity identity)
    {
        if (identity == null || !Program.LaunchParameters.ContainsKey("-companionaiipctest")
            || !Program.LaunchParameters.ContainsKey("-companioncollecttest")
            || !File.Exists(Path.Combine(Main.SavePath, "isolated-collect-fixture"))) return;
        var player = (Player)identity.Actor;
        if (!ready)
        {
            x = Main.spawnTileX;
            floor = Main.spawnTileY + 8;
            for (int tx = x - 30; tx <= x + 67; tx++)
            for (int ty = floor - 14; ty <= floor + 5; ty++)
            {
                var tile = Main.tile[tx, ty];
                tile.ClearEverything();
                if (ty >= floor) { tile.HasTile = true; tile.TileType = TileID.Dirt; }
            }
            ready = true;
            File.WriteAllText(Path.Combine(Main.SavePath, "container-ready.json"), "{}");
        }
        foreach (var npc in Main.npc)
            if (npc.active && !npc.friendly && Vector2.DistanceSquared(npc.Center, player.Center) <= 400 * 400)
                npc.active = false;
        var path = Path.Combine(Main.SavePath, "container-request");
        if (!File.Exists(path)) return;
        string command = File.ReadAllText(path);
        if (command is "allow-contact-pickup" or "merge-drop" or "remove-drop" && ServerDrops.Current == null) return;
        if (command == "move-drop" && (ServerDrops.Current?.Phase != "approaching_drop" || player.velocity.Y >= -0.1f)) return;
        File.Delete(path);
        if (command.StartsWith("seed", StringComparison.Ordinal))
        {
            for (int slot = 0; slot < Main.item.Length; slot++)
                if (Main.item[slot].active)
                {
                    Main.item[slot] = new Item();
                    NetMessage.SendData(MessageID.SyncItem, -1, -1, null, slot);
                }
            for (int i = 0; i < 59; i++) player.inventory[i] = new Item();
            player.position = new Vector2(x * 16, floor * 16 - player.height);
            player.velocity = Vector2.Zero;
            if (command == "seed-full")
                for (int i = 0; i < 50; i++) player.inventory[i] = new Item(ItemID.DirtBlock, 9999);
            if (command == "seed-partial") player.inventory[49] = new Item(ItemID.Wood, 9996);
            if (command == "seed-partial")
                for (int i = 0; i < 49; i++) player.inventory[i] = new Item(ItemID.DirtBlock, 9999);
            for (int tx = x + 8; tx <= x + 16; tx++)
                {
                    var tile = Main.tile[tx, floor - 1];
                    tile.HasTile = command == "seed-step";
                    tile.TileType = TileID.Dirt;
                }
            if (command == "seed-batch")
            {
                foreach(int type in batchTypes)Spawn(player,type,1,x+3,floor,600);
            }
            else if (command != "seed-stone")
                Spawn(player, ItemID.Wood, 7, x + (command == "seed-timeout" ? 58 : command == "seed-identity" ? 3 : 10), floor - (command == "seed-step" ? 1 : 0), command == "seed-identity" ? 600 : 0);
            if (command is not ("seed-identity" or "seed-batch"))
                Spawn(player, ItemID.StoneBlock, 5, x + (command == "seed-stone" ? 3 : command == "seed-region" ? 10 : 15),
                    floor - (command == "seed-step" ? 1 : 0), command == "seed-stone" ? 600 : 0);
            NetworkStateSync.Full(player);
        }
        if (command == "merge-drop")
        {
            int slot = Array.FindIndex(Main.item, i => i.active && i.type == ItemID.Wood);
            if(slot<0)throw new InvalidOperationException("Merge fixture wood disappeared before intervention.");
            var item = Main.item[slot];
            int added = Item.NewItem(new EntitySource_Misc("collect_merge_fixture"), item.Hitbox, ItemID.Wood, 3);
            Main.item[added].position = item.position;
            Main.item[added].playerIndexTheItemIsReservedFor = player.whoAmI;
            Main.item[added].keepTime = 1800;
            combine.Invoke(item, new object[] { slot });
            NetMessage.SendData(MessageID.SyncItem, -1, -1, null, slot);
        }
        if (command == "move-drop")
        {
            var item = Main.item.First(i => i.active && i.type == ItemID.Wood);
            item.position.X += 80;
            item.keepTime = 1800;
            item.playerIndexTheItemIsReservedFor = player.whoAmI;
            NetMessage.SendData(MessageID.SyncItem, -1, -1, null, Array.IndexOf(Main.item, item));
        }
        if (command == "reinitialize-drop")
        {
            int slot = Array.FindIndex(Main.item, item => item.active && item.type == ItemID.Wood);
            var item = Main.item[slot];
            var position = item.position;
            int type = item.type, prefix = item.prefix, stack = item.stack;
            item.SetDefaults(type);
            item.position = position;
            item.prefix = prefix;
            item.stack = stack;
            item.active = true;
            item.noGrabDelay = 600;
            item.keepTime = 1800;
            item.playerIndexTheItemIsReservedFor = player.whoAmI;
            if (!ReferenceEquals(Main.item[slot], item) || item.type != type)
                throw new InvalidOperationException("Drop fixture must reinitialize the same object in the same slot.");
            NetMessage.SendData(MessageID.SyncItem, -1, -1, null, slot);
        }
        // The reference cases isolate admission and native contact pickup.
        // The mixed-step and movement cases exercise navigation separately.
        if (command == "allow-contact-pickup")
            foreach (var item in Main.item.Where(item => item.active && ServerDrops.Current.Allows(item)))
            {
                item.position = player.Center - new Vector2(item.width / 2f, item.height / 2f);
                item.velocity = Vector2.Zero;
                item.keepTime = 1800;
                item.playerIndexTheItemIsReservedFor = player.whoAmI;
                item.noGrabDelay = 0;
            }
        if (command == "remove-drop")
        {
            int slot = Array.FindIndex(Main.item, i => i.active && i.type == ItemID.StoneBlock);
            if (slot >= 0) { Main.item[slot] = new Item(); NetMessage.SendData(MessageID.SyncItem, -1, -1, null, slot); }
        }
        if(command=="seed-mixed-tool")
        {
            var tile=Main.tile[x+4,floor-1];
            tile.HasTile=true;tile.TileType=TileID.Stone;
            player.inventory[0]=new Item(ItemID.Picksaw);
            player.selectedItem=0;
            NetworkStateSync.Full(player);
        }
        object hazardProof=null;
        if(command=="hazards")
        {
            int npcSlot=Array.FindIndex(Main.npc,n=>!n.active);
            var previous=Main.npc[npcSlot];
            var npc=new NPC();
            npc.SetDefaults(NPCID.Zombie);
            npc.active=true;
            npc.Center=player.Center+new Vector2(60,0);
            npc.target=player.whoAmI;
            Main.npc[npcSlot]=npc;
            int wallX=(int)(player.Center.X/16)+2, foot=(int)(player.Bottom.Y/16);
            void SetWall(bool solid)
            {
                for(int ty=foot-5;ty<foot;ty++)
                {
                    var wall=Main.tile[wallX,ty];
                    wall.HasTile=solid;wall.TileType=TileID.Stone;
                }
            }
            SetWall(true);
            try
            {
                string blocked=NativeNavigationWorld.CheckHazards(identity,player.statLife);
                SetWall(false);
                string visible=NativeNavigationWorld.CheckHazards(identity,player.statLife);
                npc.target=255;
                string untargeted=NativeNavigationWorld.CheckHazards(identity,player.statLife);
                npc.Center=player.Center;
                string contact=NativeNavigationWorld.CheckHazards(identity,player.statLife);
                hazardProof=new{blocked,visible,untargeted,contact,health=NativeNavigationWorld.CheckHazards(identity,player.statLife+1)};
            }
            finally
            {
                SetWall(false);
                Main.npc[npcSlot]=previous;
            }
        }
        File.WriteAllText(Path.Combine(Main.SavePath, "container-state.json"), JsonSerializer.Serialize(new
        {
            command, x = player.Center.X, y = player.Center.Y, vx = player.velocity.X, vy = player.velocity.Y,
            inputsReleased = ServerAiNativeActions.Released(player),
            hazardProof, mineX=x+4,mineY=floor-1,
            items = Main.item.Select((item, slot) => new { item, slot }).Where(row => row.item.active)
                .Select(row => new { row.slot, type = row.item.type, stack = row.item.stack, owner = row.item.playerIndexTheItemIsReservedFor, grabbed = row.item.beingGrabbed }).ToArray(),
            inventory = player.inventory.Select((item, slot) => new { slot, item.type, item.stack }).ToArray(),
            clients = Netplay.Clients.Where(client => client.State == 10).Select(client => new {
                slot = client.Id, items = Main.player[client.Id].inventory.Select(item => new { item.type, item.stack }).ToArray()
            }).ToArray()
        }));
    }

    private static void Spawn(Player player, int type, int stack, int tx, int ty, int grabDelay = 0)
    {
        int slot = Item.NewItem(new EntitySource_Misc("collect_fixture"), new Rectangle(tx * 16, ty * 16 - 20, 1, 1), type, stack);
        var item = Main.item[slot];
        item.velocity = Vector2.Zero;
        item.noGrabDelay = grabDelay;
        item.keepTime = 1800;
        item.playerIndexTheItemIsReservedFor = player.whoAmI;
        NetMessage.SendData(MessageID.SyncItem, -1, -1, null, slot);
    }
}
